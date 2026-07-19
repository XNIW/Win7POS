using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogHeartbeatContractTests
{
    [TestMethod]
    public void LegacyAndUnknownFieldsRemainCompatible()
    {
        var legacy = Deserialize("{\"ok\":true,\"serverTime\":\"now\",\"session\":null}");
        Assert.IsTrue(legacy.Ok);
        Assert.AreEqual(string.Empty, legacy.CatalogRevision);
        Assert.IsNull(legacy.CatalogChangesAvailable);
        Assert.IsNull(legacy.NextPollAfterSeconds);
        Assert.IsNull(legacy.Code);

        var current = Deserialize("{\"ok\":true,\"serverTime\":\"now\",\"session\":null," +
            "\"catalogRevision\":\" revision-8 \",\"catalogChangesAvailable\":false," +
            "\"nextPollAfterSeconds\":30,\"futureField\":{\"x\":1}}");
        Assert.AreEqual("revision-8", current.CatalogRevision);
        Assert.AreEqual(false, current.CatalogChangesAvailable);
        Assert.AreEqual(30, current.NextPollAfterSeconds);

        var denied = Deserialize("{\"ok\":false,\"code\":\"auth_denied\",\"session\":null}");
        Assert.AreEqual("auth_denied", denied.Code);
    }

    [TestMethod]
    public void MalformedOptionalHintsDoNotInvalidateAuthenticatedEnvelope()
    {
        foreach (var hints in new[]
        {
            "\"catalogRevision\":42,\"catalogChangesAvailable\":\"false\",\"nextPollAfterSeconds\":\"30\"",
            "\"catalogRevision\":{},\"catalogChangesAvailable\":{},\"nextPollAfterSeconds\":{}",
            "\"catalogRevision\":\"bad\\u0001revision\",\"catalogChangesAvailable\":1,\"nextPollAfterSeconds\":2147483648",
            "\"nextPollAfterSeconds\":1.5"
        })
        {
            var response = Deserialize("{\"ok\":true,\"serverTime\":\"still-valid\",\"session\":null," + hints + "}");
            Assert.IsTrue(response.Ok);
            Assert.AreEqual("still-valid", response.ServerTime);
            Assert.AreEqual(string.Empty, response.CatalogRevision);
            Assert.IsNull(response.CatalogChangesAvailable);
            Assert.IsNull(response.NextPollAfterSeconds);
        }
    }

    private static PosHeartbeatResponse Deserialize(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(PosHeartbeatResponse));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (PosHeartbeatResponse)serializer.ReadObject(stream)!;
    }
}
