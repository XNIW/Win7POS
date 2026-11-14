using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Core.Tests.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed class HistoricalWebImportClientTests
{
    const string Shop="10000000-0000-4000-8000-000000003019",Dataset="90000000-0000-4000-8000-000000003019";
    static readonly string Key=new('a',64),Actor=new('b',64),FileHash=new('c',64),Config=new('d',64),Fingerprint=new('e',64);
    static string Id()=>Guid.NewGuid().ToString("D");
    static HistoricalWebSummaryDto ReviewSummary()=>new(){Total=2,NewCount=1,DuplicateCount=1,Conflicts=0,Blocked=0,NewGrossClp="1000",KnownNewNetClp="975",KnownNewNetCount=1};
    static HistoricalWebResultDto Review()=>new(){ShopId=Shop,DatasetId=Dataset,Revision="2",WriterEpoch="3",WriterReady=true,CanCommit=true,Summary=ReviewSummary(),Reasons=Array.Empty<string>(),ReviewDigest=Key};
    static HistoricalWebResultDto Status()=>new(){JobId=Id(),State="review_ready",ProcessedRows=3,ErrorCode=null,Diagnostics=Array.Empty<HistoricalWebDiagnosticDto>(),Merchants=Array.Empty<HistoricalWebMerchantDto>(),
        Summary=new(){SourceRows=3,Accepted=2,Sales=2,Cancellations=0,Rejected=1,SignedGrossClp="2000",PositiveGrossClp="2000",CancellationGrossClp="0",KnownNetClp="1950",KnownNetCount=2,
            RejectedRequestedClp="500",KnownRejectedRequestedCount=1,SourceAccepted=2,NeedsTimeDecision=true,Candidates=new()}};

    [TestMethod]
    public void WebPayloadUsesCanonicalClosedOperationFieldsAndExplicitFalseAndEmptyCorrections()
    {
        static JsonDocument Wire(HistoricalCloudPayload payload){using var bytes=new MemoryStream();new DataContractJsonSerializer(typeof(HistoricalCloudPayload)).WriteObject(bytes,payload);return JsonDocument.Parse(bytes.ToArray());}
        using var start=Wire(new(){RequestKey=Id(),WebUploadIds=new[]{Id()},WebConfiguration=new(){LegacyAmountsAreClp=false,ModernTimeInterpretation="Unresolved",TemporalPolicyVersion="unresolved-web-v1",TemporalEvidenceReference=""}});
        CollectionAssert.AreEquivalent(new[]{"request_key","upload_ids","configuration"},start.RootElement.EnumerateObject().Select(x=>x.Name).ToArray());
        Assert.IsFalse(start.RootElement.GetProperty("configuration").GetProperty("legacyAmountsAreClp").GetBoolean());
        using var configure=Wire(new(){WebJobId=Id(),WebEvidenceIds=Array.Empty<string>(),WebCorrections=Array.Empty<string>()});
        CollectionAssert.AreEquivalent(new[]{"job_id","evidence_ids","corrections"},configure.RootElement.EnumerateObject().Select(x=>x.Name).ToArray());
        Assert.AreEqual(0,configure.RootElement.GetProperty("corrections").GetArrayLength());
    }
    [TestMethod]
    public void CanonicalReviewAndReceiptRemainExactAndImmutable()
    {
        var dto=Review();var summary=HistoricalWebImportResponseMapper.Review(dto,Shop);dto.Summary.NewCount=999;
        Assert.AreEqual(1,summary.NewCount);Assert.AreEqual(975L,summary.KnownNewNetClp);
        var receipt=HistoricalWebImportResponseMapper.Commit(new(){OperationId=Id(),BatchId=Id(),Kind="apply",DatasetId=Dataset,WriterEpoch="3",Revision="9007199254740993",Summary=ReviewSummary()},Dataset,3);
        Assert.AreEqual(9007199254740993L,receipt.Revision);Assert.AreEqual(1000L,receipt.Summary.NewGrossClp);
    }
    [TestMethod]
    [DataRow("shop")][DataRow("epoch")][DataRow("digest")][DataRow("missing_bool")][DataRow("negative_count")][DataRow("net_count")][DataRow("mixed_summary")][DataRow("reason")]
    public void MalformedCanonicalReviewIsNeverConfirmable(string mutation)
    {
        var r=Review();switch(mutation){case "shop":r.ShopId=Id();break;case "epoch":r.WriterEpoch="03";break;case "digest":r.ReviewDigest="bad";break;
            case "missing_bool":r.CanCommit=null;break;case "negative_count":r.Summary.NewCount=-1;break;case "net_count":r.Summary.KnownNewNetCount=2;break;
            case "mixed_summary":r.Summary.SourceRows=2;break;case "reason":r.Reasons=new[]{"raw financial detail"};break;}
        var error=Assert.ThrowsExactly<InvalidOperationException>(()=>HistoricalWebImportResponseMapper.Review(r,Shop));Assert.AreEqual(HistoricalWebImportResponseMapper.InvalidCode,error.Message);
    }
    [TestMethod]
    public void ParserPreviewKeepsDeclaredRejectionsAndUnknownTimeSeparateFromIncome()
    {
        var status=Status();var parsed=HistoricalWebImportResponseMapper.Status(status,status.JobId);
        Assert.AreEqual(3,parsed.SourceRows);Assert.AreEqual(2,parsed.Accepted);Assert.AreEqual(1,parsed.Rejected);Assert.AreEqual(500L,parsed.RejectedRequestedClp);
        Assert.IsTrue(parsed.NeedsTimeDecision);Assert.IsNull(parsed.FromBusinessDateTime);
        status.Summary.Total=3;Assert.ThrowsExactly<InvalidOperationException>(()=>HistoricalWebImportResponseMapper.Status(status,status.JobId));
    }
    [TestMethod]
    public void IndependentParserEconomicsMustMatchWhileUnresolvedLocalClockDoesNotVetoWeb()
    {
        var facts=new HistoricalSalesImportSummary(3,2,2,0,1,2000,2000,0,1950,2,500,1,null,null);
        var local=new HistoricalSalesImportAnalysis(Array.Empty<HistoricalSalesImportFile>(),Array.Empty<HistoricalSalesTransaction>(),Array.Empty<HistoricalSalesRejectedRow>(),Array.Empty<HistoricalSalesImportDiagnostic>(),facts,true);
        var status=Status();var parsed=HistoricalWebImportResponseMapper.Status(status,status.JobId);
        HistoricalWebImportPolicy.DemandParserParity(local,parsed);
        status.Summary.SignedGrossClp="2001";parsed=HistoricalWebImportResponseMapper.Status(status,status.JobId);
        var error=Assert.ThrowsExactly<InvalidOperationException>(()=>HistoricalWebImportPolicy.DemandParserParity(local,parsed));Assert.AreEqual("history_web_parser_mismatch",error.Message);
    }
    [TestMethod]
    public void CanonicalEnvelopeNeverAcceptsAnotherDeviceSessionOrShop()
    {
        var device=Id();var session=Id();var response=PosOnlineResult<HistoricalWebCloudResponse>.Ok(new(){Ok=true,ShopId=Shop,DeviceId=device,SessionId=session,Result=Review()});
        Assert.IsNotNull(HistoricalWebImportResponseMapper.RequireResult(response,Shop,device,session));
        Assert.ThrowsExactly<InvalidOperationException>(()=>HistoricalWebImportResponseMapper.RequireResult(response,Shop,Id(),session));
        Assert.ThrowsExactly<InvalidOperationException>(()=>HistoricalWebImportResponseMapper.RequireResult(response,Shop,device,Id()));
    }
    [TestMethod]
    public void WebConfigurationNeverPromotesCsharpEvidenceOrInventsLegacyCurrencyForModernFiles()
    {
        var cs=Proof(HistoricalEvidenceKind.ModernTime,"getnet-ooxml-v1");var web=Proof(HistoricalEvidenceKind.ModernTime,HistoricalWebImportPolicy.WebParserVersion);
        var context=Context(cs,web);var selected=HistoricalWebImportPolicy.ApplicableEvidence(context,new[]{FileHash});
        Assert.AreEqual(1,selected.Count);Assert.AreEqual(web.Id,selected[0].Id);
        var modern=new HistoricalSalesImportFile("f","synthetic.xlsx",FileHash,1,"Synthetic",HistoricalSalesProfile.GetnetModern,1);
        var config=HistoricalWebImportPolicy.ParserConfiguration(selected,new[]{modern});Assert.IsFalse(config.LegacyAmountsAreClp);Assert.AreEqual("IsoUtc",config.ModernTimeInterpretation);
        var uncovered=new HistoricalSalesImportFile("other","synthetic-other.xlsx",new string('f',64),1,"Synthetic",HistoricalSalesProfile.GetnetModern,1);
        Assert.AreEqual("Unresolved",HistoricalWebImportPolicy.ParserConfiguration(selected,new[]{modern,uncovered}).ModernTimeInterpretation);
        var empty=HistoricalWebImportPolicy.ApplicableEvidence(Context(cs),new[]{FileHash});
        var unresolved=HistoricalWebImportPolicy.ParserConfiguration(empty,new[]{modern});Assert.AreEqual("Unresolved",unresolved.ModernTimeInterpretation);Assert.AreEqual("",unresolved.TemporalEvidenceReference);
        web.State="Revoked";Assert.AreEqual(0,HistoricalWebImportPolicy.ApplicableEvidence(Context(web),new[]{FileHash}).Count);
    }
    [TestMethod]
    public async Task IntentRestartRetainsAnalysisAndCommitKeysAndProtectsUnknownFinancialOutcome()
    {
        using var f=new IntentFixture();var store=new HistoricalWebImportIntentStore(f.Db.Factory);
        var first=await store.GetOrCreateAsync(Key,Actor,Dataset,3,new[]{FileHash},Config,Fingerprint,default);
        var job=Id();await store.SetUploadAsync(Key,0,Id(),default);await store.SetJobAsync(Key,job,default);
        var review=await store.SetReviewAsync(Key,Key,new[]{Id()},default);await store.BeginCommitAsync(Key,job,Key,review.CommitRequestKey,default);
        var restarted=new HistoricalWebImportIntentStore(f.Db.Factory);
        var replay=await restarted.GetOrCreateAsync(Key,Actor,Dataset,3,new[]{FileHash},new string('f',64),new string('0',64),default);
        Assert.AreEqual(first.AnalysisRequestKey,replay.AnalysisRequestKey);Assert.AreEqual(review.CommitRequestKey,replay.CommitRequestKey);Assert.AreEqual(Config,replay.ConfigDigest);
        var error=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>restarted.SetReviewAsync(Key,new string('1',64),review.EvidenceIds,default));
        Assert.AreEqual("history_web_commit_recovery_required",error.Message);
        using var c=f.Db.Factory.Open();Assert.AreEqual(1L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM app_settings"));
        var metadata=c.ExecuteScalar<string>("SELECT value FROM app_settings")!;Assert.IsFalse(metadata.Contains("token",StringComparison.OrdinalIgnoreCase));Assert.IsFalse(metadata.Contains("https://",StringComparison.Ordinal));
    }
    [TestMethod]
    public async Task FreshV6SessionForSamePrincipalKeepsPendingRequestIdentity()
    {
        var trusted=new PosTrustedDeviceSession{ShopId=Shop,ShopDeviceId=Id(),StaffId=Id(),StaffCredentialVersion=7,PosSessionId=Id(),DeviceToken="synthetic-device-a",SessionToken="synthetic-session-a"};
        var principal=HistoricalWebImportPolicy.PrincipalFingerprint(trusted);
        using var f=new IntentFixture();var store=new HistoricalWebImportIntentStore(f.Db.Factory);
        var first=await store.GetOrCreateAsync(Key,principal,Dataset,3,new[]{FileHash},Config,Fingerprint,default);var job=Id();await store.SetJobAsync(Key,job,default);
        var reviewed=await store.SetReviewAsync(Key,Key,new[]{Id()},default);await store.BeginCommitAsync(Key,job,Key,reviewed.CommitRequestKey,default);
        trusted.PosSessionId=Id();trusted.SessionToken="synthetic-session-b";
        Assert.AreEqual(principal,HistoricalWebImportPolicy.PrincipalFingerprint(trusted));
        var reopened=await new HistoricalWebImportIntentStore(f.Db.Factory).GetOrCreateAsync(Key,HistoricalWebImportPolicy.PrincipalFingerprint(trusted),Dataset,3,new[]{FileHash},Config,Fingerprint,default);
        Assert.AreEqual(first.AnalysisRequestKey,reopened.AnalysisRequestKey);Assert.AreEqual(reviewed.CommitRequestKey,reopened.CommitRequestKey);Assert.IsTrue(reopened.CommitAttempted);
        trusted.StaffCredentialVersion++;Assert.AreNotEqual(principal,HistoricalWebImportPolicy.PrincipalFingerprint(trusted));
    }
    [TestMethod]
    public async Task OnlyObservedReceiptAllowsAnotherExplicitPreparationForSameFiles()
    {
        using var f=new IntentFixture();var store=new HistoricalWebImportIntentStore(f.Db.Factory);
        await store.GetOrCreateAsync(Key,Actor,Dataset,3,new[]{FileHash},Config,Fingerprint,default);var job=Id();await store.SetJobAsync(Key,job,default);
        var old=await store.SetReviewAsync(Key,Key,new[]{Id()},default);await store.BeginCommitAsync(Key,job,Key,old.CommitRequestKey,default);
        var operation=Id();await store.CompleteAsync(Key,old.CommitRequestKey,operation,default);
        var committed=await store.ReadAsync(Key,default);Assert.AreEqual(operation,committed.ReceiptOperationId);Assert.AreEqual(old.CommitRequestKey,committed.CommitRequestKey);
        var fresh=await store.GetOrCreateAsync(Key,Actor,Dataset,3,new[]{FileHash},Config,Fingerprint,default);
        Assert.IsNull(fresh.JobId);Assert.IsNull(fresh.CommitRequestKey);Assert.IsFalse(fresh.CommitAttempted);Assert.AreNotEqual(old.AnalysisRequestKey,fresh.AnalysisRequestKey);
    }
    [TestMethod]
    public async Task IntentRejectsPeerAndStalePlanWithoutChangingDurableKeys()
    {
        using var f=new IntentFixture();var store=new HistoricalWebImportIntentStore(f.Db.Factory);
        var before=await store.GetOrCreateAsync(Key,Actor,Dataset,3,new[]{FileHash},Config,Fingerprint,default);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>store.GetOrCreateAsync(Key,new string('f',64),Dataset,3,new[]{FileHash},Config,Fingerprint,default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>store.BeginCommitAsync(Key,Id(),Key,Id(),default));
        Assert.AreEqual(before.AnalysisRequestKey,(await store.ReadAsync(Key,default)).AnalysisRequestKey);
    }
    [TestMethod]
    public async Task UploaderSendsOnlyExactXlsxBytesAndScopedHeadersToConfiguredGetnetRoute()
    {
        var path=Path.Combine(Path.GetTempPath(),"synthetic-web-"+Guid.NewGuid().ToString("N")+".xlsx");var bytes=Encoding.UTF8.GetBytes("synthetic-xlsx-bytes-only");await File.WriteAllBytesAsync(path,bytes);
        var source=new HistoricalSalesImportFile("f",path,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.Length,"Synthetic",HistoricalSalesProfile.GetnetLegacy,1);
        var g=new OnlineSyncGeneration(Id(),Id(),Id(),Shop,"SYNTHETIC",Id(),7);var credentials=new OnlineSyncRequestCredentials(g,"synthetic-device-token-019","synthetic-session-token-019","synthetic-stamp");
        var handler=new UploadHandler();using var uploader=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler);
        Assert.AreEqual("",await uploader.UploadGetnetAsync("https://admin.invalid/api/pos/payment-history/source?source=getnet",Id(),source,credentials,()=>{},default));
        CollectionAssert.AreEqual(bytes,handler.Body);Assert.AreEqual(1,handler.Calls);
        await File.AppendAllTextAsync(path,"changed");var changed=await Assert.ThrowsExactlyAsync<Win7POS.Data.Import.HistoricalCloudRequestException>(()=>uploader.UploadGetnetAsync("https://admin.invalid/api/pos/payment-history/source?source=getnet",Id(),source,credentials,()=>{},default));
        Assert.AreEqual("history_web_source_changed",changed.Code);Assert.AreEqual(1,handler.Calls);
    }
    [TestMethod]
    [DataRow("https://other.invalid/api/pos/payment-history/source?source=getnet")]
    [DataRow("https://admin.invalid/api/pos/payment-history/source")]
    [DataRow("https://admin.invalid/api/pos/payment-history/source?source=getnet&other=1")]
    [DataRow("https://user@admin.invalid/api/pos/payment-history/source?source=getnet")]
    [DataRow("https://admin.invalid/api/pos/payment-history/source?source=getnet#fragment")]
    public void UploaderRejectsEveryNonCanonicalDestination(string target)
        =>Assert.ThrowsExactly<Win7POS.Data.Import.HistoricalCloudRequestException>(()=>HistoricalBaselineUploader.DemandGetnetUri(target,new Uri("https://admin.invalid")));
    [TestMethod]
    public async Task UploadCancellationInterruptsOwnedRequestWithoutRetry()
    {
        var path=Path.Combine(Path.GetTempPath(),"synthetic-web-cancel-"+Guid.NewGuid().ToString("N")+".xlsx");var bytes=Encoding.UTF8.GetBytes("synthetic-only");await File.WriteAllBytesAsync(path,bytes);
        var source=new HistoricalSalesImportFile("f",path,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.Length,"Synthetic",HistoricalSalesProfile.GetnetLegacy,1);
        var g=new OnlineSyncGeneration(Id(),Id(),Id(),Shop,"SYNTHETIC",Id(),7);var credentials=new OnlineSyncRequestCredentials(g,"synthetic-device-token-019","synthetic-session-token-019","synthetic-stamp");
        var handler=new UploadHandler{Block=true};using var uploader=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler);using var stop=new CancellationTokenSource();
        var pending=uploader.UploadGetnetAsync("https://admin.invalid/api/pos/payment-history/source?source=getnet",Id(),source,credentials,()=>{},stop.Token);
        await handler.Entered.Task;stop.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>pending);Assert.AreEqual(1,handler.Calls);
    }

    static HistoricalQualifiedEvidenceDto Proof(HistoricalEvidenceKind kind,string parser)=>new(){Id=Id(),ShopId=Shop,Kind=kind.ToString(),State="Verified",Issuer="PlatformMaster",
        QualificationVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,MerchantId=Id(),AliasKey=kind==HistoricalEvidenceKind.MerchantMapping?Actor:"",Profile="GetnetModern",ParserVersion=parser,
        Files=new[]{FileHash},FromTicks="0",ToTicks=DateTime.MaxValue.Ticks.ToString(CultureInfo.InvariantCulture),PolicyVersion="synthetic-provider-v1",Reference="synthetic-reviewed-document",
        Interpretation=kind==HistoricalEvidenceKind.ModernTime?"IsoUtc":"Unresolved",ProviderVerified=kind==HistoricalEvidenceKind.ModernTime,DocumentSha256=Key,DocumentUploadId=Id()};
    static HistoricalQualificationContext Context(params HistoricalQualifiedEvidenceDto[] evidence)=>HistoricalQualificationResponseMapper.RequireContext(new(){QualificationSchemaVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,
        QualificationShopId=Shop,QualificationConfigDigest=Config,QualificationRuntime=new(){DatasetId=Dataset,WriterEpoch="3",WriterAuthority="web"},QualifiedEvidence=evidence,QualificationLocalScopeBindings=Array.Empty<HistoricalLocalScopeBindingDto>()},Shop);
    sealed class IntentFixture:IDisposable
    {
        internal readonly OwnedSqliteTestFile Db;
        internal IntentFixture(){var path=Path.Combine(Path.GetTempPath(),"W7WI."+Guid.NewGuid().ToString("N"),"p.db");Directory.CreateDirectory(Path.GetDirectoryName(path)!);Db=OwnedSqliteTestFile.Create(path);using var c=Db.Factory.Open();c.Execute("CREATE TABLE app_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL)");}
        public void Dispose()=>Db.Dispose();
    }
    sealed class UploadHandler:HttpMessageHandler
    {
        internal int Calls;internal byte[] Body=Array.Empty<byte>();internal bool Block;internal readonly TaskCompletionSource<bool> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Assert.AreEqual(HttpMethod.Post,request.Method);Assert.IsNull(request.Headers.Authorization);Assert.IsFalse(request.Headers.Contains("apikey"));
            Assert.AreEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",request.Content!.Headers.ContentType!.MediaType);
            Assert.AreEqual("synthetic-device-token-019",request.Headers.GetValues("x-history-device-token").Single());Assert.AreEqual("synthetic-session-token-019",request.Headers.GetValues("x-history-session-token").Single());
            Body=await request.Content.ReadAsByteArrayAsync(ct);Assert.AreEqual((long)Body.Length,request.Content.Headers.ContentLength);
            Entered.TrySetResult(true);if(Block)await Task.Delay(Timeout.Infinite,ct);
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{ok=true,upload_id=request.Headers.GetValues("x-history-upload-id").Single(),already_present=false}),Encoding.UTF8,"application/json")};
        }
    }
}
