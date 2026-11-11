using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalBaselineStoreTests
{
    sealed class SourcePostHandler:HttpMessageHandler
    {
        internal int Calls;internal byte[] Body=Array.Empty<byte>();internal HttpStatusCode Status=HttpStatusCode.OK;
        internal string? ErrorCode;internal bool WrongAck;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Assert.AreEqual(HttpMethod.Post,request.Method);Assert.IsNull(request.Headers.Authorization);
            Assert.IsFalse(request.Headers.Contains("apikey"));Assert.IsFalse(request.Headers.Contains("x-device-token"));Assert.IsFalse(request.Headers.Contains("x-session-token"));
            Assert.AreEqual("synthetic-device-token-019",request.Headers.GetValues("x-history-device-token").Single());
            Assert.AreEqual("synthetic-session-token-019",request.Headers.GetValues("x-history-session-token").Single());
            Assert.IsTrue(request.RequestUri!.AbsoluteUri.EndsWith("/api/pos/payment-history/source",StringComparison.Ordinal));Assert.AreEqual("application/zip",request.Content!.Headers.ContentType!.MediaType);
            Body=await request.Content.ReadAsByteArrayAsync(ct);Assert.AreEqual((long)Body.Length,request.Content.Headers.ContentLength);
            var upload=WrongAck?Guid.NewGuid().ToString("D"):request.Headers.GetValues("x-history-upload-id").Single();
            var json=ErrorCode!=null?"{\"ok\":false,\"code\":\""+ErrorCode+"\"}":"{\"ok\":true,\"upload_id\":\""+upload+"\",\"already_present\":"+(Status==HttpStatusCode.Conflict?"true":"false")+"}";
            return new HttpResponseMessage(Status){Content=new StringContent(json,System.Text.Encoding.UTF8,"application/json")};
        }
    }
    static OnlineSyncRequestCredentials Credentials(OnlineSyncGeneration g)=>new(g,"synthetic-device-token-019","synthetic-session-token-019","synthetic-credential-stamp");
    static string UploadUrl(HistoricalBaselineFlow f,string origin="https://admin.invalid")=>origin+"/api/pos/payment-history/source";
    [TestMethod]
    public async Task BaselineSource_UsesVerifiedSelectiveProductArchiveAndKeepsTheFullBackupLocal()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var facts=f.ReadFacts();var state=await f.Service.ReadDatasetAsync(default);
        var root=Path.Combine(Path.GetTempPath(),"s-"+Guid.NewGuid().ToString("N"));
        var receipt=await new HistoricalBaselineSource(f.Db.Factory,root).PrepareAsync(await f.Guard(),Guid.Parse(state.RestoreLineageId).ToString("D"),default);
        Assert.IsTrue(File.Exists(receipt.ArchivePath));Assert.IsTrue(File.Exists(receipt.BackupPath));Assert.AreEqual("history-local-export-v2",receipt.Manifest.SchemaVersion);
        using var zip=ZipFile.OpenRead(receipt.ArchivePath);Assert.AreEqual(8,zip.Entries.Count);
        Assert.IsTrue(zip.Entries.All(e=>e.FullName=="manifest.json"||e.FullName.EndsWith(".ndjson",StringComparison.Ordinal)));
        Assert.IsFalse(zip.Entries.Any(e=>e.FullName.EndsWith(".db",StringComparison.OrdinalIgnoreCase)));Assert.AreEqual(facts,f.ReadFacts());
    }
    [TestMethod]
    public async Task BaselineSource_BoundsUnattendedPreparationWithoutDeletingOrReplacingPriorSources()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var state=await f.Service.ReadDatasetAsync(default);
        var root=Path.Combine(Path.GetTempPath(),"s-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        for(var i=0;i<20;i++)Directory.CreateDirectory(Path.Combine(root,Guid.NewGuid().ToString("N")));
        var error=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>new HistoricalBaselineSource(f.Db.Factory,root).PrepareAsync(f.Guard().GetAwaiter().GetResult(),Guid.Parse(state.RestoreLineageId).ToString("D"),default));
        Assert.AreEqual("history_baseline_export_backpressure",error.Code);Assert.AreEqual(20,Directory.EnumerateDirectories(root).Count());Assert.AreEqual(0,Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Count());
    }
    [TestMethod]
    public async Task BaselineUploader_StreamsOnlyOriginalSelectiveBytesToConfiguredAdminWithScopedCredentials()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);
        await Store(f,g).SaveUploadAsync(flow.FlowId,Guid.NewGuid().ToString("D"),default);flow=(await Store(f,g).ReadAsync(Server,1,default))!;
        var handler=new SourcePostHandler();using var client=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler);
        await client.UploadAsync(UploadUrl(flow),flow,Credentials(g),()=>{},default);Assert.AreEqual(1,handler.Calls);
        CollectionAssert.AreEqual(File.ReadAllBytes(flow.ArchivePath),handler.Body);Assert.IsFalse(File.ReadAllBytes(flow.BackupPath).SequenceEqual(handler.Body));
        Assert.AreEqual("upload_created",(await Store(f,g).ReadAsync(Server,1,default))!.State,"Source transfer cannot declare server hash verification or apply a financial baseline.");
    }
    [TestMethod]
    [DataRow("host")][DataRow("path")][DataRow("scheme")][DataRow("userinfo")][DataRow("query")][DataRow("fragment")][DataRow("port")]
    public async Task BaselineUploader_RejectsUnboundTargetBeforeAnyHttpOrArchiveUpload(string mutation)
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);
        flow.UploadId=Guid.NewGuid().ToString("D");var url=UploadUrl(flow);
        url=mutation switch {"host"=>url.Replace("admin.invalid","other.invalid"),"path"=>url.Replace("/source","/other"),
            "scheme"=>url.Replace("https:","http:"),"userinfo"=>url.Replace("https://","https://user@"),"query"=>url+"?other=1","fragment"=>url+"#fragment",
            "port"=>url.Replace("admin.invalid/","admin.invalid:8443/"),_=>throw new AssertFailedException()};
        var handler=new SourcePostHandler();using var client=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler);
        var error=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>client.UploadAsync(url,flow,Credentials(g),()=>{},default));Assert.AreEqual("history_baseline_upload_target_invalid",error.Code);Assert.AreEqual(0,handler.Calls);
    }
    [TestMethod]
    public async Task BaselineUploader_ChangedSourceCannotBeSentAndRedirectIsNotAnAcknowledgement()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);flow.UploadId=Guid.NewGuid().ToString("D");
        var handler=new SourcePostHandler{Status=HttpStatusCode.TemporaryRedirect};using var client=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler);
        var redirect=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>client.UploadAsync(UploadUrl(flow),flow,Credentials(g),()=>{},default));Assert.AreEqual("history_baseline_upload_failed",redirect.Code);Assert.AreEqual(1,handler.Calls);
        File.AppendAllText(flow.ArchivePath,"changed");var error=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>client.UploadAsync(UploadUrl(flow),flow,Credentials(g),()=>{},default));
        Assert.AreEqual("history_baseline_source_unavailable",error.Code);Assert.AreEqual(1,handler.Calls);
    }
    [TestMethod]
    public async Task BaselineUploader_BindsTransportAckAndReturnsActualAuthenticationDenialForTheSharedRequestGate()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);flow.UploadId=Guid.NewGuid().ToString("D");
        var handler=new SourcePostHandler{WrongAck=true};using var client=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler);
        var wrong=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>client.UploadAsync(UploadUrl(flow),flow,Credentials(g),()=>{},default));
        Assert.AreEqual("history_baseline_upload_failed",wrong.Code);
        handler.WrongAck=false;handler.Status=HttpStatusCode.Conflict;
        Assert.AreEqual(string.Empty,await client.UploadAsync(UploadUrl(flow),flow,Credentials(g),()=>{},default));
        handler.Status=HttpStatusCode.Forbidden;handler.ErrorCode="auth_denied";
        Assert.AreEqual("auth_denied",await client.UploadAsync(UploadUrl(flow),flow,Credentials(g),()=>{},default));
        handler.ErrorCode="history_cloud_access_denied";
        var denied=await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>client.UploadAsync(UploadUrl(flow),flow,Credentials(g),()=>{},default));
        Assert.IsTrue(denied.Denied);Assert.AreEqual("history_baseline_upload_denied",denied.Code);
    }
    [TestMethod]
    public async Task BaselineUploader_OnlyAnExplicitLoopbackAdminAllowsLoopbackServiceFixtures()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();var flow=await Prepare(f,g);flow.UploadId=Guid.NewGuid().ToString("D");
        var url=UploadUrl(flow,"http://127.0.0.1:54321");var handler=new SourcePostHandler();
        using(var client=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler))
            await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>client.UploadAsync(url,flow,Credentials(g),()=>{},default));
        var local=new SourcePostHandler();using(var client=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("http://127.0.0.1:54321")),local))
            await client.UploadAsync(url,flow,Credentials(g),()=>{},default);
        Assert.AreEqual(0,handler.Calls);Assert.AreEqual(1,local.Calls);
        using var differentPort=new HistoricalBaselineUploader(new PosAdminWebOptions(new Uri("http://127.0.0.1:54322")),local);
        await Assert.ThrowsAsync<HistoricalCloudRequestException>(()=>differentPort.UploadAsync(url,flow,Credentials(g),()=>{},default));
        Assert.AreEqual(1,local.Calls,"Credentials cannot be sent to another service on the same loopback host.");
    }
}
