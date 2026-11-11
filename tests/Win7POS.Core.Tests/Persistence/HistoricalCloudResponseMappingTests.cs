using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed class HistoricalCloudResponseMappingTests
{
    const string Shop="10000000-0000-4000-8000-000000003019",Device="20000000-0000-4000-8000-000000003019",Session="30000000-0000-4000-8000-000000003019";
    static HistoricalCloudResult Map(PosOnlineResult<HistoricalCloudResponse>? response)=>HistoricalCloudResponseMapper.RequireResult(response!,Shop,Device,Session);
    sealed class WireReply(HttpStatusCode status,string body):HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")});}
    }
    [TestMethod]
    [DataRow("history_cloud_writer_closed",409,false,false,true)]
    [DataRow("history_cloud_access_denied",403,true,false,false)]
    [DataRow("auth_denied",403,true,false,false)]
    [DataRow("history_cloud_unavailable",503,false,true,false)]
    [DataRow("http_5xx",503,false,true,false)]
    [DataRow("history_input_invalid",400,false,false,false)]
    public async Task RealHttpClientAndSharedMapperPreserveImmutableFailureAndOnlyExactConflictPasses(string code,int status,bool denied,bool retryable,bool conflict)
    {
        var handler=new WireReply((HttpStatusCode)status,"{\"ok\":false,\"code\":\""+code+"\",\"message\":\"private body must not be copied\"}");
        using var client=new PosAdminWebClient(new PosAdminWebOptions(new Uri("https://admin.invalid")),handler);
        var response=await client.HistoricalCloudAsync(new HistoricalCloudRequest{DeviceId=Device,SessionId=Session,DeviceToken="synthetic-device-token-019",SessionToken="synthetic-session-token-019",Operation="local_review",Payload=new()},default);
        var error=Assert.ThrowsExactly<HistoricalCloudRequestException>(()=>Map(response));
        Assert.AreEqual(1,handler.Calls);Assert.AreEqual(status,response.HttpStatus);Assert.AreEqual(code,error.Code);
        Assert.AreEqual(denied,error.Denied);Assert.AreEqual(retryable,error.Retryable);
        Assert.AreEqual(conflict,AuthenticatedHistoryServiceHarness.IsExpectedConflict(error));
        // A later successful request cannot alter the captured exception.
        Map(PosOnlineResult<HistoricalCloudResponse>.Ok(new(){Ok=true,ShopId=Shop,DeviceId=Device,SessionId=Session,Result=new()}));
        Assert.AreEqual(code,error.Code);Assert.AreEqual(code,AuthenticatedHistoryServiceHarness.ClosedCode(error.Code));
    }
    [TestMethod]
    [DataRow("{not-json")]
    [DataRow("{\"ok\":true}")]
    public async Task MalformedHttpSuccessCannotBecomeAConflictPass(string body)
    {
        using var client=new PosAdminWebClient(new PosAdminWebOptions(new Uri("https://admin.invalid")),new WireReply(HttpStatusCode.OK,body));
        var response=await client.HistoricalCloudAsync(new HistoricalCloudRequest{DeviceId=Device,SessionId=Session,Operation="authority",Payload=new()},default);
        var error=Assert.ThrowsExactly<HistoricalCloudRequestException>(()=>Map(response));
        Assert.IsFalse(AuthenticatedHistoryServiceHarness.IsExpectedConflict(error));
        Assert.IsFalse(error.Denied);Assert.IsFalse(error.Retryable);
    }
    [TestMethod]
    [DataRow("shop")][DataRow("device")][DataRow("session")][DataRow("result")][DataRow("ok")][DataRow("body")]
    public void EveryEnvelopeBindingAndResultRemainMandatory(string mutation)
    {
        var body=new HistoricalCloudResponse{Ok=true,ShopId=Shop,DeviceId=Device,SessionId=Session,Result=new()};
        switch(mutation){case "shop":body.ShopId=Device;break;case "device":body.DeviceId=Shop;break;case "session":body.SessionId=Shop;break;case "result":body.Result=null!;break;case "ok":body.Ok=false;break;case "body":body=null!;break;}
        var error=Assert.ThrowsExactly<HistoricalCloudRequestException>(()=>Map(PosOnlineResult<HistoricalCloudResponse>.Ok(body)));
        Assert.AreEqual("history_response_invalid",error.Code);Assert.IsFalse(AuthenticatedHistoryServiceHarness.IsExpectedConflict(error));
    }
    [TestMethod]
    public void ResnapshotAndAbsentTransportStayDistinctFromConflict()
    {
        Assert.ThrowsExactly<HistoricalCloudResnapshotRequiredException>(()=>Map(PosOnlineResult<HistoricalCloudResponse>.Failure("history_resnapshot_required","",false)));
        var absent=Assert.ThrowsExactly<HistoricalCloudRequestException>(()=>Map(null));Assert.AreEqual("history_request_failed",absent.Code);
        Assert.IsFalse(AuthenticatedHistoryServiceHarness.IsExpectedConflict(absent));
        Assert.IsFalse(AuthenticatedHistoryServiceHarness.IsExpectedConflict(new InvalidOperationException("history_cloud_writer_closed")));
    }
    [TestMethod]
    [DataRow("history_cloud_writer_closed-extra")][DataRow("history_cloud_writer_closed\nprivate")][DataRow("history_cloud_writer_closed\n")][DataRow("history_cloud_writer_closed\r")][DataRow("https://private.invalid/token")][DataRow("auth_secret=value")][DataRow("")]
    public void RedactionRejectsUnknownTextWithoutTurningItIntoExpectedConflict(string value)
    {Assert.AreEqual("history_test_failed",AuthenticatedHistoryServiceHarness.ClosedCode(value));Assert.IsFalse(AuthenticatedHistoryServiceHarness.IsExpectedConflict(new HistoricalCloudRequestException(value,false,false)));}
    [TestMethod]
    public void AuthenticationAndTemporaryFailuresCannotPassEvenWithAMislabelledConflictCode()
    {
        Assert.IsFalse(AuthenticatedHistoryServiceHarness.IsExpectedConflict(new HistoricalCloudRequestException("history_cloud_writer_closed",true,false)));
        Assert.IsFalse(AuthenticatedHistoryServiceHarness.IsExpectedConflict(new HistoricalCloudRequestException("history_cloud_writer_closed",false,true)));
    }
}
