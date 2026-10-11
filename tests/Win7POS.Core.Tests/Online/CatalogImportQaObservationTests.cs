using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

// Unit-only synthetic responses exercise hook boundaries. These never qualify
// live Admin acceptance, retirement, economic SQL or Windows 7.
[TestClass]
[DoNotParallelize]
public sealed class CatalogImportQaObservationTests
{
    private static readonly Uri Staging = new("https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev/");
    private const string Device = "30000000-0000-4000-8000-000000000094";
    private const string Plan = "10000000-0000-4000-8000-000000000094";

    [TestMethod]
    public async Task Observer_UsesExactBaseDeviceAndRoute_AndRestoresOnDispose()
    {
        var records = new List<PosAdminWebClient.CatalogImportQaWireRecord>();
        using var client = Client(Staging);
        using (var scope = PosAdminWebClient.ObserveCatalogImportForQa(Staging, Device, records.Add))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => PosAdminWebClient.ObserveCatalogImportForQa(Staging, Device, records.Add));
            await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None);
            await client.CatalogImportRecoveryApplyAsync(Apply("foreign-device", Plan, 1), CancellationToken.None);
            await client.HeartbeatAsync(new PosHeartbeatRequest(), CancellationToken.None);
            using var foreign = Client(new Uri("https://example.invalid/"));
            await foreign.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None);
            Assert.AreEqual(1, records.Count);
            Assert.IsTrue(records[0].ServerResponseObserved);
            Assert.AreEqual(200, records[0].HttpStatus);
            Assert.AreEqual(64, records[0].BodySha256.Length);
            Assert.AreEqual(64, records[0].CredentialBindingSha256.Length);
            Assert.AreEqual(64, records[0].ApplyIdentitySha256.Length);
            Assert.IsTrue(records[0].BodyUtf8Bytes > 0);
        }
        await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None);
        Assert.AreEqual(1, records.Count);
        using (var replacement = PosAdminWebClient.ObserveCatalogImportForQa(Staging, Device, records.Add))
            await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None);
        Assert.AreEqual(2, records.Count);
    }

    [TestMethod]
    public async Task Drop_IsSingleUseExactPart_RequiresParsedAcceptedReceipt_AndCannotSurviveDispose()
    {
        var records = new List<PosAdminWebClient.CatalogImportQaWireRecord>();
        using var client = Client(Staging);
        using (var scope = PosAdminWebClient.ObserveCatalogImportForQa(Staging, Device, records.Add))
        {
            scope.DropOneAcceptedPartResponse(Plan, 1);
            Assert.IsTrue((await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 0), CancellationToken.None)).Success);
            var lost = await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None);
            Assert.IsFalse(lost.Success);
            Assert.IsTrue(scope.AcceptedResponseDropped);
            Assert.IsTrue((await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None)).Success);
            Assert.AreEqual(1, records.Count(record => record.AcceptedResponseDropped));
            Assert.AreEqual(records[1].BodySha256, records[2].BodySha256);
            var rotated = Apply(Device, Plan, 1);
            rotated.SessionToken = "synthetic-rotated-session";
            Assert.IsTrue((await client.CatalogImportRecoveryApplyAsync(rotated, CancellationToken.None)).Success);
            Assert.AreNotEqual(records[2].BodySha256, records[3].BodySha256);
            Assert.AreNotEqual(records[2].CredentialBindingSha256, records[3].CredentialBindingSha256);
            Assert.AreEqual(records[2].ApplyIdentitySha256, records[3].ApplyIdentitySha256);
        }
        var afterDispose = PosAdminWebClient.ObserveCatalogImportForQa(Staging, Device, records.Add);
        afterDispose.DropOneAcceptedPartResponse(Plan, 1);
        afterDispose.Dispose();
        Assert.IsTrue((await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None)).Success);
        Assert.IsFalse(afterDispose.AcceptedResponseDropped);
    }

    [TestMethod]
    public async Task Drop_DoesNotTurnRejectedOrInvalidReceiptIntoAcceptedLoss()
    {
        using var client = new PosAdminWebClient(new PosAdminWebOptions(Staging), new Handler("not_found"));
        using var scope = PosAdminWebClient.ObserveCatalogImportForQa(Staging, Device, _ => { });
        scope.DropOneAcceptedPartResponse(Plan, 1);
        Assert.IsTrue((await client.CatalogImportRecoveryApplyAsync(Apply(Device, Plan, 1), CancellationToken.None)).Success);
        Assert.IsFalse(scope.AcceptedResponseDropped);
    }

    private static PosAdminWebClient Client(Uri uri) => new(new PosAdminWebOptions(uri), new Handler("accepted"));
    private static PosCatalogImportRecoveryApplyRequest Apply(string device, string plan, int index) => new()
    { ShopDeviceId = device, PlanId = plan, PartIndex = index, DeviceToken = "synthetic-device", SessionToken = "synthetic-session", PosSessionId = "synthetic-pos", ShopCode = "SYNTHETIC" };
    private sealed class Handler(string status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"status\":\"" + status + "\",\"receipt\":{\"ok\":true,\"status\":\"" + status + "\"}}", Encoding.UTF8, "application/json")
            });
    }
}
