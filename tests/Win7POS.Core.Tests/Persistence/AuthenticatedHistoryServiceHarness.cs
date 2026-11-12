using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

// Test-only console entry: build the existing test assembly with OutputType=Exe
// and StartupObject set to this class. Ordinary MSTest discovery adds no skip.
// Uses the real Core/Data client, supervisor, HTTP, export, ledger and replica.
// Local actor composition is synthetic; this does not attest WPF/Win7 hardware.
internal static class AuthenticatedHistoryServiceHarness
{
    sealed class Actor
    {
        public string deviceId{get;set;}="";public string sessionId{get;set;}="";
        public string deviceToken{get;set;}="";public string sessionToken{get;set;}="";
    }
    sealed class Scope
    {
        public string schema{get;set;}="";public string nonce{get;set;}="";public string endpoint{get;set;}="";
        public string clientSha{get;set;}="";public string shopId{get;set;}="";public string staffId{get;set;}="";
        public int credentialVersion{get;set;}public Actor actorA{get;set;}=new();public Actor actorB{get;set;}=new();
        public Actor recoveryApplyActor{get;set;}=new();public Actor recoverySessionsActor{get;set;}=new();
        public bool extendedCases{get;set;}
        public bool faultCases{get;set;}public bool adminOrigin{get;set;}public bool conflictCases{get;set;}public bool recoveryRaces{get;set;}public RestartCheckpoint? restart{get;set;}
    }
    sealed class Fact
    {
        public string Namespace{get;set;}="";public string Id{get;set;}="";public string Ticks{get;set;}="";public string Gross{get;set;}="";
        public string? Fee{get;set;}public string? Vat{get;set;}public string? Net{get;set;}
        internal string BasicKey=>Namespace+"|"+Id+"|"+Ticks+"|"+Gross;
        internal string Key=>BasicKey+"|"+(Fee??"null")+"|"+(Vat??"null")+"|"+(Net??"null");
    }
    sealed class Binding{public string Scope{get;set;}="";public string Id{get;set;}="";public string Evidence{get;set;}="";}
    sealed class ExportBinding{public string scope{get;set;}="";public string alias{get;set;}="";}
    sealed class FrozenExport{public string path{get;set;}="";public string sha256{get;set;}="";public ExportBinding[] bindings{get;set;}=Array.Empty<ExportBinding>();}
    sealed class SavedFixture
    {
        public string root{get;set;}="";public string token{get;set;}="";public string dataset{get;set;}="";public string lineage{get;set;}="";
        public long revision{get;set;}public string originalReceipt{get;set;}="";public string generation{get;set;}="";
    }
    sealed class RestartCheckpoint
    {
        public string schema{get;set;}="asus019-restart-v1";public string nonce{get;set;}="";public string operation{get;set;}="";
        public SavedFixture a{get;set;}=new();public SavedFixture b{get;set;}=new();public Fact[] wanted{get;set;}=Array.Empty<Fact>();public string[] newIds{get;set;}=Array.Empty<string>();
    }
    static string _phase="preflight",_directory="",_lastCode="";static Scope _scope=new();static object? _localDiagnostics,_requestFailure;
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=false};
    static void Demand(bool condition,string code){if(!condition)throw new InvalidOperationException(code);}
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Demand(args.Length==1&&Path.IsPathFullyQualified(args[0]),"history_test_context_required");
            _directory=Path.GetDirectoryName(Path.GetFullPath(args[0]))!;
            Win7POS.Data.PosDatabasePathIdentity.RejectReparseComponents(args[0]);
            _scope=JsonSerializer.Deserialize<Scope>(await File.ReadAllTextAsync(args[0]),Json)??throw new InvalidDataException();
            Demand(_scope.schema=="asus019-authenticated-history-v2"&&Guid.TryParseExact(_scope.nonce,"N",out _)&&_scope.shopId==HistoricalCloudExportTests.Fixture.Shop,"history_test_scope_invalid");
            var endpoint=new Uri(_scope.endpoint);Demand(endpoint.IsLoopback&&endpoint.Scheme=="http"&&endpoint.UserInfo==""&&endpoint.Query==""&&endpoint.Fragment==""&&endpoint.AbsolutePath=="/","history_test_loopback_required");
            HistoricalReplicaProtocol.DemandId(_scope.staffId);HistoricalReplicaProtocol.DemandId(_scope.actorA.deviceId);HistoricalReplicaProtocol.DemandId(_scope.actorB.deviceId);
            Demand(_scope.actorA.deviceId!=_scope.actorB.deviceId&&_scope.credentialVersion>0&&System.Text.RegularExpressions.Regex.IsMatch(_scope.clientSha,"^[0-9a-f]{40}$"),"history_test_scope_invalid");
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(_scope.extendedCases?6:4));var ct=deadline.Token;
            if(_scope.restart!=null)return await ResumeAfterLostAck(_scope.restart,ct);
            await using var a=await Session.Create(_scope.actorA,true);await using var b=await Session.Create(_scope.actorB,false);
            var original=a.LocalFacts();Demand(original.Length>0&&b.LocalFacts().Length==0,"history_test_initial_ledger");
            _phase="initial_baseline";await a.Until(a.HasBaseline,ct);await b.Until(b.HasBaseline,ct);await a.Turn(ct);
            var before=a.Facts();Same(original,before);Same(before,b.Facts());a.NoNative();b.NoNative();
            if(_scope.recoveryRaces)await RecoveryRaceSources(a,b,before,ct);
            var source=NewSource(a.Fixture);var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
            var state=await a.Fixture.Service.ReadDatasetAsync(ct);
            var assignments=ReadAssignments(a,analysis);
            var plan=new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"),_scope.shopId,state.DatasetId,state.DataRevision,0,analysis,assignments,new DateTime(2025,1,1),"Synthetic service harness approved start");
            var expected=analysis.AcceptedTransactions.Where(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19).Select(ToFact).ToArray();
            Demand(expected.Length>0,"history_test_new_source_missing");
            _phase="source_ready";await SaveState(new{phase=_phase,nonce=_scope.nonce,baselineCount=before.Length,sourceSha=Hash(await File.ReadAllBytesAsync(source)),expected},ct);
            await WaitSignal("allow_local_apply",ct);
            _phase="local_apply";var authority=await a.Request("authority",new(),ct);
            using var publicationUse=HistoricalPublicationOperationUse.Begin(a.Fixture.Db.Factory,plan.OperationId);
            using var draft=await a.Fixture.Service.PreparePublicationDraftAsync(plan,a.Generation.ShopDeviceId,ct);
            var admission=await new HistoricalPublicationPreparer(a.Fixture.Db.Factory,a.Demand,a.Request).PrepareAsync(draft,authority,ct);
            var live=new HistoricalLocalWriteGrant(Guid.Parse(plan.OperationId).ToString("D"),authority.WriterEpoch,admission.RemainingMilliseconds,
                admission.GrantRequestElapsedMilliseconds,a.Demand,clock:admission.ReadMonotonicMilliseconds,frequency:1000,publicationProof:admission.Proof,requestStartedTimestamp:admission.MonotonicRequestStartedMilliseconds);
            var guard=new HistoricalImportAuthorizationCommitGuard(HistoricalImportOperationKind.Apply,plan.OperationId,plan.RequestFingerprint,_scope.shopId,"SYNTHETIC",
                a.Generation.ShopDeviceId,1,plan.DatasetId,0,1,a.Generation.Fingerprint,a.Generation.StaffId,a.Generation.StaffCredentialVersion,a.Demand,(_,action)=>{a.Demand();action();a.Demand();});
            guard.AttachPublicationGrant(live);
            var receipt=await a.Fixture.Service.ApplyAsync(plan,guard,Path.Combine(a.Fixture.Output,"new-apply.db"),ct,publicationDraft:draft,publicationProof:admission.Proof);
            Demand(receipt.InsertedCount==expected.Length,"history_test_local_count_mismatch");
            _phase="published";
            await a.Until(async()=>{var status=await new HistoricalPublicationOutbox(a.Fixture.Db.Factory).ReadStatusAsync(plan.OperationId,_scope.shopId,ct);return status?.Status=="synchronized";},ct);
            var wanted=before.Concat(expected).OrderBy(x=>x.Key,StringComparer.Ordinal).ToArray();
            await b.Until(()=>Task.FromResult(Equal(wanted,b.Facts())),ct);await a.Until(()=>Task.FromResult(Equal(wanted,a.Facts())),ct);
            a.NoNative();b.NoNative();Same(a.Facts(),b.Facts());
            await SaveState(new{phase=_phase,nonce=_scope.nonce,status="PASS_CORE_DATA_ROUNDTRIP",count=wanted.Length,facts=wanted,factsSha=Hash(Encoding.UTF8.GetBytes(string.Join("\n",wanted.Select(x=>x.Key)))),newIds=expected.Select(x=>x.Id)},ct);
            await WaitSignal("admin_observed",ct);
            if(_scope.extendedCases)await DuplicateAndUndo(a,b,source,receipt,before,wanted,ct);
            if(_scope.faultCases){await CheckpointLostAck(a,b,ct);return 0;}
            _phase="complete";Console.WriteLine("authenticated_history_core_data_pass");return 0;
        }
        catch(Exception e)
        {
            if(_directory!="")await SaveState(new{phase=_phase,nonce=_scope.nonce,status="FAIL",exception=e.GetType().Name,code=ClosedCode(e is HistoricalCloudRequestException requestError?requestError.Code:e.Message),lastCode=ClosedCode(_lastCode),localDiagnostics=_localDiagnostics,requestFailure=_requestFailure},default);
            Console.Error.WriteLine("authenticated_history_client_failed");return 1;
        }
    }
    internal static string ClosedCode(string? value)=>value!=null&&value.Length<=102&&value.IndexOf('\r')<0&&value.IndexOf('\n')<0&&System.Text.RegularExpressions.Regex.IsMatch(value,"^(?:(?:history|historical|auth)_[a-z0-9_]{1,90}|http_(?:[0-9]{3}|5xx)|success|invalid_response|io_error|network_error|dns|tls|timeout|cancelled|response_too_large|rate_limited|db_failure)$")?value:"history_test_failed";
    internal static bool IsExpectedConflict(Exception error)=>error is HistoricalCloudRequestException request&&request.Code=="history_cloud_writer_closed"&&!request.Denied&&!request.Retryable;
    static HistoricalMerchantAssignment[] ReadAssignments(Session session,HistoricalSalesImportAnalysis analysis)
    {
        // Release this reader before publication and exclusive Apply maintenance.
        using var c=session.Fixture.Db.Factory.Open();
        return analysis.AcceptedTransactions.Select(t=>t.Merchant).GroupBy(HistoricalMerchantAssignment.MerchantKey).Select(group=>{
            var binding=c.QuerySingle<Binding>("SELECT scope_id Scope,id Id,evidence_reference Evidence FROM historical_merchant_binding WHERE shop_id=@shop AND alias_key=@alias",new{shop=_scope.shopId,alias=group.Key});
            return new HistoricalMerchantAssignment(group.First(),binding.Scope,binding.Id,binding.Evidence);
        }).ToArray();
    }
    static async Task DuplicateAndUndo(Session a,Session b,string source,HistoricalSalesOperationReceipt first,Fact[] baseline,Fact[] published,CancellationToken ct)
    {
        _phase="duplicate";
        var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var state=await a.Fixture.Service.ReadDatasetAsync(ct);
        var plan=new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"),_scope.shopId,state.DatasetId,state.DataRevision,0,analysis,ReadAssignments(a,analysis),new DateTime(2025,1,1),"Synthetic service harness approved start");
        using(var publicationUse=HistoricalPublicationOperationUse.Begin(a.Fixture.Db.Factory,plan.OperationId))
        using(var draft=await a.Fixture.Service.PreparePublicationDraftAsync(plan,a.Generation.ShopDeviceId,ct))
        {
            var admission=await Admit(a,draft,ct);
            var duplicate=await a.Fixture.Service.ApplyAsync(plan,admission.guard,Path.Combine(a.Fixture.Output,"duplicate-apply.db"),ct,publicationDraft:draft,publicationProof:admission.admission.Proof);
            Demand(duplicate.InsertedCount==0&&duplicate.DuplicateCount==analysis.AcceptedTransactions.Count,"history_test_duplicate_effect");
        }
        await Converge(a,b,plan.OperationId,published,ct);await ObserveCase("duplicate",published,ct);
        _phase="undo";state=await a.Fixture.Service.ReadDatasetAsync(ct);var operation=Guid.NewGuid().ToString("N");
        using(var publicationUse=HistoricalPublicationOperationUse.Begin(a.Fixture.Db.Factory,operation))
        using(var draft=await a.Fixture.Service.PrepareUndoPublicationDraftAsync(operation,_scope.shopId,a.Generation.ShopDeviceId,state.DatasetId,0,first.BatchId,1,ct))
        {
            var admission=await Admit(a,draft,ct);
            await a.Fixture.Service.UndoAsync(first.BatchId,1,admission.guard,Path.Combine(a.Fixture.Output,"undo-first.db"),ct,publicationDraft:draft,publicationProof:admission.admission.Proof);
        }
        await Converge(a,b,operation,baseline,ct);await ObserveCase("undo",baseline,ct);
        _phase="correction_source_ready";var corrected=CorrectionSource(a.Fixture,source);
        var correctionAnalysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{corrected},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var expected=correctionAnalysis.AcceptedTransactions.Where(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19).Select(ToFact).ToArray();
        Demand(expected.Length==1&&expected[0].Key!=published.Single(x=>x.Id==expected[0].Id).Key,"history_test_correction_source");
        await SaveState(new{phase=_phase,nonce=_scope.nonce,sourceSha=Hash(await File.ReadAllBytesAsync(corrected)),expected},ct);await WaitSignal("allow_correction_apply",ct);
        _phase="correction";state=await a.Fixture.Service.ReadDatasetAsync(ct);operation=Guid.NewGuid().ToString("N");
        var assignments=ReadAssignments(a,correctionAnalysis);
        var tentative=new HistoricalSalesImportPlan(operation,_scope.shopId,state.DatasetId,state.DataRevision,0,correctionAnalysis,assignments,new DateTime(2025,1,1),"Synthetic service harness approved start");
        var preview=await a.Fixture.Service.PreviewAsync(tentative,ct);
        Demand(preview.ConflictCount==0&&preview.CorrectionCount==1&&preview.Corrections.Count==1&&!preview.CorrectionsTruncated,"history_test_correction_preview");
        var correction=new HistoricalSalesImportPlan(operation,_scope.shopId,state.DatasetId,state.DataRevision,0,correctionAnalysis,assignments,new DateTime(2025,1,1),"Synthetic service harness approved start",new[]{preview.Corrections[0].Approve("Synthetic correction after verified Undo")});
        using(var publicationUse=HistoricalPublicationOperationUse.Begin(a.Fixture.Db.Factory,correction.OperationId))
        using(var draft=await a.Fixture.Service.PreparePublicationDraftAsync(correction,a.Generation.ShopDeviceId,ct))
        {
            var admission=await Admit(a,draft,ct);
            var applied=await a.Fixture.Service.ApplyAsync(correction,admission.guard,Path.Combine(a.Fixture.Output,"correction-apply.db"),ct,publicationDraft:draft,publicationProof:admission.admission.Proof);
            Demand(applied.InsertedCount==1&&applied.CorrectionCount==1,"history_test_correction_count");
        }
        var wanted=baseline.Concat(expected).ToArray();await Converge(a,b,operation,wanted,ct);await ObserveCase("correction",wanted,ct);
    }
    static Fact ToFact(HistoricalSalesTransaction t)=>new(){Namespace=t.ExternalNamespace,Id=t.ExternalId,Ticks=t.BusinessDateTime!.Value.Ticks.ToString(CultureInfo.InvariantCulture),Gross=t.GrossClp.ToString(CultureInfo.InvariantCulture),Fee=t.CommissionClp?.ToString(CultureInfo.InvariantCulture),Vat=t.CommissionVatClp?.ToString(CultureInfo.InvariantCulture),Net=t.NetClp?.ToString(CultureInfo.InvariantCulture)};
    static string CorrectionSource(HistoricalCloudExportTests.Fixture fixture,string source,string? replacementId=null)
    {
        var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var row=analysis.AcceptedTransactions.Single(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19);
        Demand(row.GrossClp>0,"history_test_correction_sale_required");
        var target=Path.Combine(fixture.Output,"corrected-legacy.xlsx");using var input=ZipFile.OpenRead(source);using var output=ZipFile.Open(target,ZipArchiveMode.Create);var changed=false;
        foreach(var entry in input.Entries)
        {
            var next=output.CreateEntry(entry.FullName,CompressionLevel.Optimal);using var read=entry.Open();using var write=next.Open();
            if(entry.FullName.StartsWith("xl/",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal))
            {
                var xml=XDocument.Load(read);XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                if(entry.FullName=="xl/worksheets/sheet1.xml")
                {
                var rows=xml.Descendants(ns+"row");var header=rows.Single(r=>(string?)r.Attribute("r")=="1");
                Demand(header.Elements(ns+"c").Single(c=>(string?)c.Attribute("r")=="C1").Value.StartsWith("VALOR",StringComparison.Ordinal)&&header.Elements(ns+"c").Single(c=>(string?)c.Attribute("r")=="G1").Value.StartsWith("MONTO",StringComparison.Ordinal),"history_test_correction_fixture_layout");
                var data=rows.Single(r=>(string?)r.Attribute("r")==row.Source.RowNumber.ToString(CultureInfo.InvariantCulture));
                void Set(string column,long value){var cell=data.Elements(ns+"c").Single(c=>(string?)c.Attribute("r")==column+row.Source.RowNumber.ToString(CultureInfo.InvariantCulture));cell.SetAttributeValue("t","n");cell.RemoveNodes();cell.Add(new XElement(ns+"v",value.ToString(CultureInfo.InvariantCulture)));}
                Set("C",checked(row.GrossClp+1));if(row.NetClp.HasValue)Set("G",checked(row.NetClp.Value+1));changed=true;
                }
                if(replacementId!=null)foreach(var value in xml.Descendants().Where(x=>x.Name.LocalName=="t"||x.Name.LocalName=="v"))if(value.Value==row.ExternalId)value.Value=replacementId;
                xml.Save(write);
            }else read.CopyTo(write);
        }
        Demand(changed,"history_test_correction_sheet_missing");return target;
    }
    static async Task<(HistoricalImportAuthorizationCommitGuard guard,HistoricalPublicationAdmission admission)> Admit(Session session,HistoricalPublicationDraft draft,CancellationToken ct)
    {
        var authority=await session.Request("authority",new(),ct);
        var admission=await new HistoricalPublicationPreparer(session.Fixture.Db.Factory,session.Demand,session.Request).PrepareAsync(draft,authority,ct);
        var live=new HistoricalLocalWriteGrant(Guid.Parse(draft.OperationId).ToString("D"),authority.WriterEpoch,admission.RemainingMilliseconds,
            admission.GrantRequestElapsedMilliseconds,session.Demand,clock:admission.ReadMonotonicMilliseconds,frequency:1000,publicationProof:admission.Proof,requestStartedTimestamp:admission.MonotonicRequestStartedMilliseconds);
        var guard=new HistoricalImportAuthorizationCommitGuard(Enum.Parse<HistoricalImportOperationKind>(draft.Kind),draft.OperationId,draft.RequestFingerprint,draft.ShopId,"SYNTHETIC",
            session.Generation.ShopDeviceId,1,draft.DatasetId,draft.RestoreEpoch,1,session.Generation.Fingerprint,session.Generation.StaffId,session.Generation.StaffCredentialVersion,session.Demand,(_,action)=>{session.Demand();action();session.Demand();});
        guard.AttachPublicationGrant(live);return(guard,admission);
    }
    static async Task Converge(Session a,Session b,string operation,Fact[] wanted,CancellationToken ct)
    {
        await a.Until(async()=>{var status=await new HistoricalPublicationOutbox(a.Fixture.Db.Factory).ReadStatusAsync(operation,_scope.shopId,ct);return status?.Status=="synchronized";},ct);
        await b.Until(()=>Task.FromResult(Equal(wanted,b.Facts())),ct);await a.Until(()=>Task.FromResult(Equal(wanted,a.Facts())),ct);
        Same(a.Facts(),b.Facts());a.NoNative();b.NoNative();
    }
    static async Task ObserveCase(string phase,Fact[] facts,CancellationToken ct)
    {
        await SaveState(new{phase,nonce=_scope.nonce,status="PASS_CORE_DATA_CASE",count=facts.Length,factsSha=Hash(Encoding.UTF8.GetBytes(string.Join("\n",facts.Select(x=>x.Key).OrderBy(x=>x,StringComparer.Ordinal))))},ct);
        await WaitSignal(phase+"_observed",ct);
    }
    static async Task<SavedFixture> CaptureFixture(Session session,CancellationToken ct)
    {
        var state=await session.Fixture.Service.ReadDatasetAsync(ct);
        return new(){root=session.Fixture.Root,token=session.Fixture.Db.FixtureToken,dataset=state.DatasetId,lineage=state.RestoreLineageId,revision=state.DataRevision,originalReceipt=session.Fixture.OriginalReceipt,generation=session.Generation.Fingerprint};
    }
    static async Task<HistoricalCloudExportTests.Fixture> ReopenFixture(SavedFixture saved,Actor actor,CancellationToken ct)
    {
        Demand(Path.IsPathFullyQualified(saved.root)&&Guid.TryParseExact(saved.token,"N",out _),"history_test_restart_capability");
        var path=Path.GetFullPath(saved.root);var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        Demand(path.StartsWith(temporary,StringComparison.Ordinal)&&System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),"^W7HC\\.[0-9a-f]{32}$"),"history_test_restart_owned_root");
        Win7POS.Data.PosDatabasePathIdentity.RejectReparseComponents(path);
        var fixture=new HistoricalCloudExportTests.Fixture{Root=path,BoundDevice=actor.deviceId,OriginalReceipt=saved.originalReceipt,KeepFiles=true};
        fixture.Db=Win7POS.Core.Tests.Data.OwnedSqliteTestFile.Reopen(Path.Combine(path,"profile","fixture.db"),saved.token);
        fixture.Service=new HistoricalSalesImportService(fixture.Db.Factory,()=>fixture.Db.Factory.AuthorityStamp);
        var current=await fixture.Service.ReadDatasetAsync(ct);
        Demand(current.DatasetId==saved.dataset&&current.DataRevision==saved.revision&&current.RestoreLineageId==saved.lineage,"history_test_restart_ledger_changed");return fixture;
    }
    static async Task CheckpointLostAck(Session a,Session b,CancellationToken ct)
    {
        _phase="fault_source_ready";var source=NewSource(a.Fixture);var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var expected=analysis.AcceptedTransactions.Where(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19).Select(ToFact).ToArray();Demand(expected.Length==1,"history_test_fault_source");
        await SaveState(new{phase=_phase,nonce=_scope.nonce,sourceSha=Hash(await File.ReadAllBytesAsync(source)),expected},ct);await WaitSignal("allow_fault_apply",ct);
        _phase="lost_ack";var before=a.Facts();var dataset=await a.Fixture.Service.ReadDatasetAsync(ct);
        // Hold the real lost-ACK state until the next process. The original
        // automatic lane is drained; recovery resumes via a new supervisor.
        await a.DrainAutomatic(ct);
        var plan=new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"),_scope.shopId,dataset.DatasetId,dataset.DataRevision,0,analysis,ReadAssignments(a,analysis),new DateTime(2025,1,1),"Synthetic service harness approved start");
        using(var publicationUse=HistoricalPublicationOperationUse.Begin(a.Fixture.Db.Factory,plan.OperationId))
        using(var draft=await a.Fixture.Service.PreparePublicationDraftAsync(plan,a.Generation.ShopDeviceId,ct))
        {
            var admission=await Admit(a,draft,ct);await a.Fixture.Service.ApplyAsync(plan,admission.guard,Path.Combine(a.Fixture.Output,"lost-ack-apply.db"),ct,publicationDraft:draft,publicationProof:admission.admission.Proof);
        }
        var dropped=false;
        async Task<HistoricalCloudResult> DropActualAck(string operation,HistoricalCloudPayload payload,CancellationToken token)
        {
            var reply=await a.Request(operation,payload,token);
            if(operation=="local_reconcile"&&!dropped){Demand(reply.PublicationReceipt!=null,"history_test_lost_ack_receipt_absent");dropped=true;throw new HistoricalCloudRequestException("history_timeout",false,true);}
            return reply;
        }
        try{await new HistoricalPublicationSender(new HistoricalPublicationOutbox(a.Fixture.Db.Factory),a.Generation,a.Demand,DropActualAck).RunAsync(ct);}
        catch(HistoricalCloudRequestException e)when(dropped&&e.Code=="history_timeout"){}
        Demand(dropped,"history_test_fault_not_exercised");var status=await new HistoricalPublicationOutbox(a.Fixture.Db.Factory).ReadStatusAsync(plan.OperationId,_scope.shopId,ct);
        Demand(status?.Status=="uncertain","history_test_lost_ack_not_durable");
        var wanted=before.Concat(expected).ToArray();await b.Until(()=>Task.FromResult(Equal(wanted,b.Facts())),ct);
        // A pulls through the real replica service while its original ACK is
        // still absent; it must not create an echo or clear the pending fact.
        await a.PullUntil(()=>Equal(wanted,a.Facts()),ct);
        status=await new HistoricalPublicationOutbox(a.Fixture.Db.Factory).ReadStatusAsync(plan.OperationId,_scope.shopId,ct);Demand(status?.Status=="uncertain","history_test_pull_forgave_ack");
        a.NoNative();b.NoNative();
        var checkpoint=new RestartCheckpoint{nonce=_scope.nonce,operation=plan.OperationId,a=await CaptureFixture(a,ct),b=await CaptureFixture(b,ct),wanted=wanted,newIds=expected.Select(f=>f.Id).ToArray()};
        await File.WriteAllTextAsync(Path.Combine(_directory,"restart.private.json"),JsonSerializer.Serialize(checkpoint),ct);
        _phase="restart_pending";await SaveState(new{phase=_phase,nonce=_scope.nonce,operation=Guid.Parse(plan.OperationId).ToString("D"),count=wanted.Length,newIds=checkpoint.newIds,ackDroppedAfterRealReceipt=true,pullBeforeAck=true},ct);
        Console.WriteLine("authenticated_history_restart_checkpoint");
    }
    static async Task<int> ResumeAfterLostAck(RestartCheckpoint checkpoint,CancellationToken ct)
    {
        _phase="restart_recovery";Demand(_scope.faultCases&&checkpoint.schema=="asus019-restart-v1"&&checkpoint.nonce==_scope.nonce&&Guid.TryParse(checkpoint.operation,out _),"history_test_restart_scope");
        await using var a=await Session.Create(_scope.actorA,false,await ReopenFixture(checkpoint.a,_scope.actorA,ct));
        await using var b=await Session.Create(_scope.actorB,false,await ReopenFixture(checkpoint.b,_scope.actorB,ct));
        Demand(a.Generation.Fingerprint!=checkpoint.a.generation&&b.Generation.Fingerprint!=checkpoint.b.generation,"history_test_restart_generation_reused");
        await Converge(a,b,checkpoint.operation,checkpoint.wanted,ct);
        _phase="restart_recovered";await SaveState(new{phase=_phase,nonce=_scope.nonce,status="PASS_LOST_ACK_RESTART_PULL_BEFORE_ACK",count=checkpoint.wanted.Length,operation=Guid.Parse(checkpoint.operation).ToString("D"),newIds=checkpoint.newIds,factsSha=Hash(Encoding.UTF8.GetBytes(string.Join("\n",checkpoint.wanted.Select(f=>f.Key).OrderBy(x=>x,StringComparer.Ordinal))))},ct);
        await WaitSignal("restart_observed",ct);
        if(_scope.conflictCases)await ConflictingPeerImport(a,b,checkpoint.wanted,checkpoint.newIds.Single(),ct);
        if(_scope.adminOrigin)await AdminOrigin(a,b,checkpoint.wanted,ct);
        Console.WriteLine("authenticated_history_restarted_pass");return 0;
    }
    static async Task RecoveryRaceSources(Session a,Session b,Fact[] wanted,CancellationToken ct)
    {
        var actors=new[]{_scope.recoveryApplyActor,_scope.recoverySessionsActor};
        Demand(actors.All(actor=>Guid.TryParse(actor.deviceId,out _)&&actor.deviceId!=_scope.actorA.deviceId&&actor.deviceId!=_scope.actorB.deviceId)&&actors[0].deviceId!=actors[1].deviceId,"history_test_recovery_actor_scope");
        foreach(var item in new[]{(phase:"recovery_apply_ready",actor:actors[0]),(phase:"recovery_sessions_ready",actor:actors[1])})
        {
            // A separately owned empty dataset and synthetic device, respecting
            // the server's existing dataset/restore-lineage fence for A and B.
            var phase=item.phase;await using var recovery=await Session.Create(item.actor,false);
            var flow=await recovery.PrepareUncommittedBaseline(ct);
            _phase=phase;await SaveState(new{phase,nonce=_scope.nonce,descriptor=new{schema="asus019-baseline-lock-v1",nonce=_scope.nonce,shopId=_scope.shopId,deviceId=item.actor.deviceId,originalSessionId=item.actor.sessionId,runId=flow.RunId,applyRequestKey=flow.ApplyRequestId,previousReviewDigest=flow.ReviewDigest}},ct);
            await WaitSignal(phase+"_observed",ct);recovery.NoNative();Demand(recovery.LocalFacts().Length==0,"history_test_recovery_changed_empty_ledger");
        }
        await a.Until(()=>Task.FromResult(Equal(wanted,a.Facts())),ct);await b.Until(()=>Task.FromResult(Equal(wanted,b.Facts())),ct);a.NoNative();b.NoNative();
    }
    static async Task ConflictingPeerImport(Session a,Session b,Fact[] wanted,string existingId,CancellationToken ct)
    {
        _phase="conflict_peer_invariant";
        Demand(b.LocalFacts().Length==0,"history_test_conflict_peer_not_empty");var before=await b.Fixture.Service.ReadDatasetAsync(ct);
        _phase="conflict_source_create";
        var source=CorrectionSource(b.Fixture,NewSource(b.Fixture),existingId);var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        _phase="conflict_source_analysis";
        var divergent=analysis.AcceptedTransactions.Single(t=>t.ExternalId==existingId);Demand(divergent.GrossClp.ToString(CultureInfo.InvariantCulture)!=wanted.Single(f=>f.Id==existingId).Gross,"history_test_conflict_not_divergent");
        _phase="conflict_mapping_plan";
        var assignments=analysis.AcceptedTransactions.Select(t=>t.Merchant).GroupBy(HistoricalMerchantAssignment.MerchantKey).Select((g,i)=>new HistoricalMerchantAssignment(g.First(),"conflict-scope-"+i,"conflict-binding-"+i,"Synthetic independent mapping")).ToArray();
        var plan=new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"),_scope.shopId,before.DatasetId,before.DataRevision,0,analysis,assignments,new DateTime(2025,1,1),"Synthetic service harness approved start");
        _phase="conflict_source_ready";await SaveState(new{phase=_phase,nonce=_scope.nonce,sourceSha=Hash(await File.ReadAllBytesAsync(source)),operation=Guid.Parse(plan.OperationId).ToString("D")},ct);await WaitSignal("allow_conflict_prepare",ct);
        _phase="conflict_preparation";var denied=false;object? denial=null;
        using(var publicationUse=HistoricalPublicationOperationUse.Begin(b.Fixture.Db.Factory,plan.OperationId))
        using(var draft=await b.Fixture.Service.PreparePublicationDraftAsync(plan,b.Generation.ShopDeviceId,ct))
        {
            try{await Admit(b,draft,ct);}
            catch(HistoricalCloudRequestException e)when(IsExpectedConflict(e)){denied=true;denial=new{code=e.Code,httpStatus=b.LastRequestHttpStatus,transportDenied=e.Denied,retryable=e.Retryable};}
        }
        Demand(denied&&denial!=null&&b.LastRequestHttpStatus==409,"history_test_conflicting_grant_accepted");var after=await b.Fixture.Service.ReadDatasetAsync(ct);Demand(after.DatasetId==before.DatasetId&&after.DataRevision==before.DataRevision&&b.LocalFacts().Length==0,"history_test_conflict_changed_ledger");
        // Once the same live operation ownership as WPF is released, the real
        // supervisor seals and reconciles the negative outcome itself.
        await b.Until(()=>{using var read=b.Fixture.Db.Factory.Open();return Task.FromResult(read.ExecuteScalar<string>("SELECT server_resolution FROM historical_commit_witness WHERE operation_id=@op",new{op=plan.OperationId})=="aborted");},ct);
        Same(wanted,a.Facts());Same(wanted,b.Facts());a.NoNative();b.NoNative();
        _phase="conflict";await SaveState(new{phase=_phase,nonce=_scope.nonce,status="PASS_CONFLICT_DENIED_ABORT_RECONCILED",denial,operation=Guid.Parse(plan.OperationId).ToString("D"),count=wanted.Length,factsSha=Hash(Encoding.UTF8.GetBytes(string.Join("\n",wanted.Select(f=>f.Key).OrderBy(x=>x,StringComparer.Ordinal))))},ct);await WaitSignal("conflict_observed",ct);
    }
    static async Task AdminOrigin(Session a,Session b,Fact[] before,CancellationToken ct)
    {
        var source=NewSource(a.Fixture);var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var expected=analysis.AcceptedTransactions.Where(t=>t.ExternalId.StartsWith("8",StringComparison.Ordinal)&&t.ExternalId.Length==19).Select(ToFact).ToArray();Demand(expected.Length>0,"history_test_web_source_missing");
        _phase="web_source_ready";await SaveState(new{phase=_phase,nonce=_scope.nonce,sourcePath=source,sourceSha=Hash(await File.ReadAllBytesAsync(source)),expected},ct);
        await WaitSignal("admin_frozen",ct);_phase="freeze_ack";
        var exportA=await a.AcknowledgeFreeze(ct);var exportB=await b.AcknowledgeFreeze(ct);
        await SaveState(new{phase="freeze_acknowledged",nonce=_scope.nonce,exports=new[]{exportA,exportB}},ct);await WaitSignal("frozen_sources_reconciled",ct);
        _phase="frozen_replica_pull";await a.Turn(ct);await b.Turn(ct);
        await SaveState(new{phase="frozen_replicas_current",nonce=_scope.nonce},ct);await WaitSignal("admin_import_committed",ct);
        _phase="admin_origin_pull";var wanted=before.Concat(expected).ToArray();
        await a.Until(()=>Task.FromResult(Equal(wanted,a.Facts())),ct);await b.Until(()=>Task.FromResult(Equal(wanted,b.Facts())),ct);
        Same(a.Facts(),b.Facts());a.NoNative();b.NoNative();
        await ObserveCase("admin_origin",wanted,ct);
        _phase="revocation_ready";await SaveState(new{phase=_phase,nonce=_scope.nonce},ct);await WaitSignal("session_revoked",ct);
        await b.DemandRevocation(ct);await a.Turn(ct);Same(wanted,a.Facts());a.NoNative();b.NoNative();
        _phase="revocation";await SaveState(new{phase=_phase,nonce=_scope.nonce,status="PASS_SESSION_REVOCATION",otherActorActive=true},ct);await WaitSignal("revocation_observed",ct);
    }
    static async Task SaveState(object value,CancellationToken ct)
    {
        var target=Path.Combine(_directory,"client-state.private.json");var temporary=target+"."+Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(value),ct);File.Move(temporary,target,true);
    }
    static async Task WaitSignal(string phase,CancellationToken ct)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();var file=Path.Combine(_directory,"client-control.private.json");
        while(clock.Elapsed<TimeSpan.FromSeconds(60))
        {
            ct.ThrowIfCancellationRequested();if(File.Exists(file)){using var json=JsonDocument.Parse(await File.ReadAllTextAsync(file,ct));if(json.RootElement.GetProperty("nonce").GetString()==_scope.nonce&&json.RootElement.GetProperty("phase").GetString()==phase)return;}
            await Task.Delay(100,ct);
        }
        throw new InvalidOperationException("history_test_browser_signal_timeout");
    }
    static string NewSource(HistoricalCloudExportTests.Fixture f)
    {
        var original=Path.Combine(AppContext.BaseDirectory,"fixtures","historical-sales-getnet-v1","synthetic-legacy.xlsx");
        var analyzed=new HistoricalSalesImportAnalyzer().Analyze(new[]{original},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
        var old=analyzed.AcceptedTransactions[0].ExternalId;var id="8"+DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        var target=Path.Combine(f.Output,"new-"+Guid.NewGuid().ToString("N")+".xlsx");using var input=ZipFile.OpenRead(original);using var output=ZipFile.Open(target,ZipArchiveMode.Create);var changed=0;
        foreach(var entry in input.Entries)
        {
            var next=output.CreateEntry(entry.FullName,CompressionLevel.Optimal);using var read=entry.Open();using var write=next.Open();
            if(entry.FullName.StartsWith("xl/",StringComparison.Ordinal)&&entry.FullName.EndsWith(".xml",StringComparison.Ordinal))
            {
                var xml=XDocument.Load(read);foreach(var value in xml.Descendants().Where(x=>x.Name.LocalName=="t"||x.Name.LocalName=="v"))if(value.Value==old){value.Value=id;changed++;}xml.Save(write);
            }else read.CopyTo(write);
        }
        Demand(changed>0,"history_test_source_mutation_missing");return target;
    }
    static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    static bool Equal(Fact[] a,Fact[] b)=>a.Select(x=>x.Key).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(b.Select(x=>x.Key).OrderBy(x=>x,StringComparer.Ordinal));
    static void Same(Fact[] a,Fact[] b)=>Demand(Equal(a,b),"history_test_replica_mismatch");
    sealed class Session:IDisposable,IAsyncDisposable
    {
        internal HistoricalCloudExportTests.Fixture Fixture=null!;internal OnlineSyncGeneration Generation=null!;
        PosAdminWebClient _client=null!;OnlineSyncSupervisor _supervisor=null!;Actor _actor=new();bool _active=true;string _server="";long _epoch;
        internal string LastRequestCode{get;private set;}="";
        internal int? LastRequestHttpStatus{get;private set;}
        PosAdminWebOptions _options=null!;Func<HistoricalBaselineContext,HistoricalSalesDatasetState,CancellationToken,Task<HistoricalBaselinePreparation>> _prepareBaseline=null!;
        internal static async Task<Session> Create(Actor actor,bool legacy,HistoricalCloudExportTests.Fixture? existing=null)
        {
            var s=new Session{_actor=actor};s.Fixture=existing??await HistoricalCloudExportTests.Fixture.CreateAsync(deviceId:actor.deviceId,seedLegacy:legacy);s.Fixture.KeepFiles=true;
            s.Generation=new OnlineSyncGeneration(Guid.NewGuid().ToString("D"),actor.sessionId,actor.deviceId,_scope.shopId,"synthetic-service-harness",_scope.staffId,_scope.credentialVersion);
            await new OnlineSyncGenerationRepository(s.Fixture.Db.Factory).ActivateAndRecoverAsync(s.Generation,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var options=new PosAdminWebOptions(new Uri(_scope.endpoint));s._options=options;s._client=new PosAdminWebClient(options);
            var root=Path.Combine(Path.GetTempPath(),"s-"+Guid.NewGuid().ToString("N"));
            s._prepareBaseline=async(context,state,ct)=>{
                s.Demand();AuthenticatedHistoryServiceHarness.Demand(context.State=="missing"&&context.CanPrepare==true,"history_test_prepare_denied");
                var guard=new HistoricalCloudExportGuard(_scope.shopId,actor.deviceId,state.DatasetId,state.DataRevision,0,s.Demand);
                var receipt=await new HistoricalBaselineSource(s.Fixture.Db.Factory,root).PrepareAsync(guard,Guid.Parse(state.RestoreLineageId).ToString("D"),ct);
                return new HistoricalBaselinePreparation(receipt,guard);
            };
            var sync=new PosHistoricalCloudSyncService(s.Fixture.Db.Factory,s._prepareBaseline);
            s._supervisor=new OnlineSyncSupervisor(s.Generation,(context,trigger,ct)=>sync.SynchronizeAsync(options,context,_scope.clientSha,()=>s._active,ct),
                generation=>new OnlineSyncGenerationRepository(s.Fixture.Db.Factory).IsCurrentAndActiveAsync(generation),
                (_,_)=>{s._active=false;return Task.CompletedTask;},credentialProvider:_=>Task.FromResult(new OnlineSyncRequestCredentials(s.Generation,actor.deviceToken,actor.sessionToken,"synthetic-service-harness")));
            var authority=await s.Request("authority",new(),default);s._server=authority.DatasetId;s._epoch=HistoricalReplicaProtocol.Integer(authority.WriterEpoch);return s;
        }
        internal void Demand()=>AuthenticatedHistoryServiceHarness.Demand(_active,"history_test_generation_stopped");
        internal async Task<HistoricalCloudResult> Request(string operation,HistoricalCloudPayload payload,CancellationToken ct)
        {
            if(_phase=="conflict_preparation"&&operation=="local_review")
            {
                AuthenticatedHistoryServiceHarness.Demand(payload.Ordinal==1&&payload.ReviewToken==null,"history_test_conflict_probe_page");
                await SaveState(new{phase="conflict_review_ready",nonce=_scope.nonce,payload=new{operation_id=payload.OperationId,content_digest=payload.ContentDigest,ordinal=payload.Ordinal}},ct);
                await WaitSignal("allow_conflict_review",ct);
            }
            Demand();var result=await _client.HistoricalCloudAsync(new HistoricalCloudRequest{DeviceId=_actor.deviceId,SessionId=_actor.sessionId,DeviceToken=_actor.deviceToken,SessionToken=_actor.sessionToken,Operation=operation,Payload=payload},ct);
            _lastCode=result.Code;LastRequestCode=result.Code;LastRequestHttpStatus=result.HttpStatus;
            if(!result.Success)
            {
                RecordFailure(operation,result.Code,result.HttpStatus,result.Denied,result.Retryable);
            }
            try{return HistoricalCloudResponseMapper.RequireResult(result,_scope.shopId,_actor.deviceId,_actor.sessionId);}
            catch(HistoricalCloudRequestException error)when(result.Success)
            {
                RecordFailure(operation,error.Code,result.HttpStatus,error.Denied,error.Retryable);throw;
            }
        }
        void RecordFailure(string operation,string code,int? httpStatus,bool denied,bool retryable)
            =>_requestFailure=new{phase=_phase,actor=_actor.deviceId==_scope.actorA.deviceId?"A":_actor.deviceId==_scope.actorB.deviceId?"B":"other_test_actor",operation,code=ClosedCode(code),httpStatus,transportDenied=denied,retryable};
        internal async Task<bool> HasBaseline()
        {
            var f=await new HistoricalBaselineStore(Fixture.Db.Factory,Generation,Demand).ReadAsync(_server,_epoch,default);if(f?.State!="applied")return false;
            using var c=Fixture.Db.Factory.Open();return c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_read_mode WHERE flow_id=@FlowId",f)==1;
        }
        internal async Task Turn(CancellationToken ct){var result=await _supervisor.TriggerAsync(OnlineSyncLane.HistoricalPayments,OnlineSyncLaneTrigger.Manual,ct);_lastCode=result.Code;AuthenticatedHistoryServiceHarness.Demand(result.Success,"history_test_supervisor_turn_failed");}
        internal async Task Until(Func<Task<bool>> ready,CancellationToken ct)
        {
            for(var i=0;i<80;i++){await Turn(ct);if(await ready())return;await Task.Delay(500,ct);}
            var flow=await new HistoricalBaselineStore(Fixture.Db.Factory,Generation,Demand).ReadAsync(_server,_epoch,ct);
            _localDiagnostics=new{actor=_actor.deviceId==_scope.actorA.deviceId?"A":"B",baselineState=flow?.State,
                baselineReason=flow?.ReasonCode!=null&&System.Text.RegularExpressions.Regex.IsMatch(flow.ReasonCode,"^(?:history|baseline)_[a-z_]{1,90}$")?flow.ReasonCode:null,
                baselineUploadPresent=flow?.UploadId!=null,baselineRunPresent=flow?.RunId!=null,baselineReviewPresent=flow?.ReviewDigest!=null};
            throw new InvalidOperationException("history_test_convergence_timeout");
        }
        internal async Task PullUntil(Func<bool> ready,CancellationToken ct){for(var i=0;i<80;i++){await new HistoricalReplicaSynchronizer(new HistoricalReplicaStore(Fixture.Db.Factory,Demand,Generation),Demand,Request).RunAsync(_scope.shopId,_scope.clientSha,ct);if(ready())return;await Task.Delay(250,ct);}throw new InvalidOperationException("history_test_pull_before_ack_timeout");}
        internal Task DrainAutomatic(CancellationToken ct)=>_supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(10),ct);
        internal async Task<HistoricalBaselineFlow> PrepareUncommittedBaseline(CancellationToken ct)
        {
            var store=new HistoricalBaselineStore(Fixture.Db.Factory,Generation,Demand);
            async Task Upload(string url,HistoricalBaselineFlow flow,CancellationToken token)
            {
                using var uploader=new HistoricalBaselineUploader(_options);var code=await uploader.UploadAsync(url,flow,new OnlineSyncRequestCredentials(Generation,_actor.deviceToken,_actor.sessionToken,"synthetic-service-harness"),Demand,token);
                AuthenticatedHistoryServiceHarness.Demand(string.IsNullOrEmpty(code),"history_test_recovery_upload_failed");
            }
            var client=new HistoricalBaselineSynchronizer(Fixture.Db.Factory,Generation,Demand,Request,_prepareBaseline,Upload);HistoricalBaselineFlow? current=null;
            for(var i=0;i<80;i++)
            {
                current=await store.ReadAsync(_server,_epoch,ct);if(current?.State=="run_started")break;
                AuthenticatedHistoryServiceHarness.Demand(current==null||current.State!="needs_attention","history_test_recovery_baseline_attention");await client.RunAsync(ct);await Task.Delay(250,ct);
            }
            AuthenticatedHistoryServiceHarness.Demand(current?.State=="run_started","history_test_recovery_baseline_not_started");
            var ready=false;for(var i=0;i<80;i++)
            {
                var status=await Request("baseline_status",new(){RunId=current!.RunId},ct);if(status.State=="review_ready"){ready=true;break;}
                AuthenticatedHistoryServiceHarness.Demand(status.State!="failed"&&status.State!="cancelled","history_test_recovery_analysis_failed");await Task.Delay(250,ct);
            }
            AuthenticatedHistoryServiceHarness.Demand(ready,"history_test_recovery_analysis_timeout");var review=await Request("baseline_review",new(){RunId=current!.RunId},ct);
            AuthenticatedHistoryServiceHarness.Demand(review.CanApply==true,"history_test_recovery_review_blocked");await store.SealReviewAsync(current.FlowId,review.ReviewDigest,ct);
            return(await store.ReadAsync(_server,_epoch,ct))!;
        }
        internal async Task<FrozenExport> AcknowledgeFreeze(CancellationToken ct)
        {
            var authority=await Request("authority",new(),ct);AuthenticatedHistoryServiceHarness.Demand(authority.WriterAuthority=="frozen"&&authority.ProtocolVersion==1,"history_test_freeze_authority");
            await new HistoricalReplicaStore(Fixture.Db.Factory,Demand,Generation).ObserveAuthorityAsync(_scope.shopId,authority,ct);
            var state=await Fixture.Service.ReadDatasetAsync(ct);var stamp=Fixture.Db.Factory.AuthorityStamp;
            var guard=new HistoricalCloudExportGuard(_scope.shopId,Generation.ShopDeviceId,state.DatasetId,state.DataRevision,stamp.RestoreEpoch,()=>{
                Demand();var current=Fixture.Db.Factory.AuthorityStamp;AuthenticatedHistoryServiceHarness.Demand(current.RestoreEpoch==stamp.RestoreEpoch&&current.AuthorityInstanceEpoch==stamp.AuthorityInstanceEpoch,"history_test_freeze_database_changed");});
            var store=new HistoricalPreparedExportStore(Fixture.Db.Factory);await store.DemandHistoricalPublicationsDrainedAsync(guard,ct);await store.DemandNativeSalesDrainedAsync(guard,ct);
            var epoch=HistoricalReplicaProtocol.Integer(authority.WriterEpoch);var export=await store.ReadVerifiedAsync(epoch,guard,ct);
            if(export==null){var id=Guid.NewGuid().ToString("N");export=await new HistoricalCloudExporter(Fixture.Db.Factory).ExportAsync(Path.Combine(Fixture.Output,"freeze-"+id+".w7history.zip"),Path.Combine(Fixture.Output,"freeze-"+id+".db"),guard,ct);await store.SaveAsync(epoch,export,guard,ct);}
            await store.DemandHistoricalPublicationsDrainedAsync(guard,ct);await store.DemandNativeSalesDrainedAsync(guard,ct);
            var currentState=await Fixture.Service.ReadDatasetAsync(ct);AuthenticatedHistoryServiceHarness.Demand(currentState.DatasetId==guard.DatasetId&&currentState.DataRevision==guard.DataRevision,"history_test_freeze_snapshot_stale");
            var ack=await Request("freeze_ack",new(){WriterEpoch=authority.WriterEpoch,LocalDatasetId=guard.DatasetId,DataRevision=guard.DataRevision.ToString(CultureInfo.InvariantCulture),RestoreEpoch=guard.RestoreEpoch.ToString(CultureInfo.InvariantCulture),ArchiveSha256=export.ArchiveSha256,ManifestSha256=export.ManifestSha256,BackupSha256=export.Manifest.BackupSha256,LocalWritesDrained=true,NativeOutboxDrained=true,ProtocolVersion=1,ClientBuildSha=_scope.clientSha},ct);
            AuthenticatedHistoryServiceHarness.Demand(ack.Acknowledged&&ack.WriterEpoch==authority.WriterEpoch,"history_test_freeze_ack_invalid");guard.DemandStillValid();
            using var read=Fixture.Db.Factory.Open();return new(){path=export.ArchivePath,sha256=export.ArchiveSha256,bindings=read.Query<ExportBinding>("SELECT scope_id scope,alias_key alias FROM historical_merchant_binding WHERE shop_id=@shop AND is_active=1",new{shop=_scope.shopId}).ToArray()};
        }
        internal async Task DemandRevocation(CancellationToken ct)
        {
            var result=await _supervisor.TriggerAsync(OnlineSyncLane.HistoricalPayments,OnlineSyncLaneTrigger.Manual,ct);
            AuthenticatedHistoryServiceHarness.Demand(!result.Success&&_supervisor.GetSnapshot().AuthenticationStopped&&!_active,"history_test_revocation_not_stopped");
        }
        internal Fact[] Facts()
        {
            using var c=Fixture.Db.Factory.Open();
            var facts=c.Query<Fact>("SELECT e.external_namespace Namespace,e.external_id Id,CAST(e.business_ticks AS TEXT) Ticks,CAST(e.signed_amount_clp AS TEXT) Gross,CAST(e.signed_fee_clp AS TEXT) Fee,CAST(e.signed_fee_vat_clp AS TEXT) Vat,CAST(e.signed_net_clp AS TEXT) Net FROM historical_cloud_entity e JOIN historical_cloud_state s ON s.shop_id=e.shop_id AND s.generation_id=e.generation_id WHERE e.shop_id=@shop AND e.entity_kind='movement' AND e.is_active=1",new{shop=_scope.shopId}).ToArray();
            var visible=c.Query<Fact>("SELECT external_namespace Namespace,external_id Id,CAST(business_ticks AS TEXT) Ticks,CAST(signed_amount_clp AS TEXT) Gross FROM historical_baseline_report_movements WHERE shop_id=@shop AND is_active=1",new{shop=_scope.shopId}).ToArray();
            AuthenticatedHistoryServiceHarness.Demand(facts.Select(f=>f.BasicKey).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(visible.Select(f=>f.BasicKey).OrderBy(x=>x,StringComparer.Ordinal)),"history_test_report_replica_mismatch");
            return facts;
        }
        internal Fact[] LocalFacts(){using var c=Fixture.Db.Factory.Open();return c.Query<Fact>("SELECT external_namespace Namespace,external_id Id,CAST(business_ticks AS TEXT) Ticks,CAST(signed_amount_clp AS TEXT) Gross,CAST(signed_fee_clp AS TEXT) Fee,CAST(signed_fee_vat_clp AS TEXT) Vat,CAST(signed_net_clp AS TEXT) Net FROM historical_transactions WHERE shop_id=@shop AND is_active=1",new{shop=_scope.shopId}).ToArray();}
        internal void NoNative()
        {
            using var c=Fixture.Db.Factory.Open();AuthenticatedHistoryServiceHarness.Demand(c.ExecuteScalar<long>("SELECT (SELECT COUNT(*) FROM sales)+(SELECT COUNT(*) FROM sales_sync_outbox)+(SELECT COUNT(*) FROM local_stock_movements)")==0,"history_test_native_effect");
            if(Fixture.OriginalReceipt!="")AuthenticatedHistoryServiceHarness.Demand(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation WHERE receipt_json=@receipt",new{receipt=Fixture.OriginalReceipt})==1,"history_test_original_audit_changed");
        }
        public void Dispose(){_active=false;_supervisor?.Dispose();_client?.Dispose();Fixture?.Dispose();}
        public async ValueTask DisposeAsync()
        {
            _active=false;
            try{if(_supervisor!=null)await _supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));}
            finally{Dispose();}
        }
    }
}
