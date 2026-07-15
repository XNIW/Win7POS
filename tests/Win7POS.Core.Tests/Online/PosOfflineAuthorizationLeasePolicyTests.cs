using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.Serialization.Json;
using System.Text;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosOfflineAuthorizationLeasePolicyTests
{
    private static readonly DateTimeOffset ServerContact = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
    private static readonly DateTimeOffset LocalReceipt = DateTimeOffset.Parse("2026-07-15T11:59:58Z");

    [TestMethod]
    public void Evaluate_AllowsFreshOfflineSessionUsingServerAndLocalReceiptClocks()
    {
        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            Session(ServerContact.AddHours(12)),
            LocalReceipt.AddHours(2));

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual("ok", decision.Code);
        Assert.AreEqual(ServerContact.AddHours(12), decision.EffectiveExpiresAt);
    }

    [TestMethod]
    public void Evaluate_DeniesAtExactSessionExpiry()
    {
        var session = Session(ServerContact.AddHours(3));
        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(session, LocalReceipt.AddHours(3));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("offline_lease_expired", decision.Code);
    }

    [TestMethod]
    public void Evaluate_DeniesWhenMaximumOfflineAgeWinsOverFutureSessionExpiry()
    {
        var session = Session(ServerContact.AddDays(7));
        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(session, LocalReceipt.AddHours(12));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("offline_lease_expired", decision.Code);
        Assert.AreEqual(ServerContact.AddHours(12), decision.EffectiveExpiresAt);
    }

    [TestMethod]
    public void Evaluate_DeniesLegacyStateWithoutLocalReceiptTimestamp()
    {
        var session = Session(ServerContact.AddHours(12));
        session.LastOkLocalAt = null;

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(session, LocalReceipt.AddMinutes(1));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("local_receipt_time_invalid", decision.Code);
    }

    [TestMethod]
    public void Evaluate_DeniesMalformedOrIncoherentTimestamps()
    {
        var malformed = Session(ServerContact.AddHours(12));
        malformed.LastOkServerAt = "not-a-time";
        Assert.AreEqual(
            "last_server_time_invalid",
            PosOfflineAuthorizationLeasePolicy.Evaluate(malformed, LocalReceipt).Code);

        var incoherent = Session(ServerContact.AddMinutes(-1));
        Assert.AreEqual(
            "session_window_invalid",
            PosOfflineAuthorizationLeasePolicy.Evaluate(incoherent, LocalReceipt).Code);
    }

    [TestMethod]
    public void Evaluate_DeniesLocalClockRollback()
    {
        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            Session(ServerContact.AddHours(12)),
            LocalReceipt.AddTicks(-1));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("clock_rollback", decision.Code);
    }

    [TestMethod]
    public void Evaluate_DeniesMissingTrustedSession()
    {
        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(null, LocalReceipt);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("trusted_session_missing", decision.Code);
    }

    [TestMethod]
    public void Evaluate_DeniesRollbackBelowProcessHighWater()
    {
        var session = Session(ServerContact.AddHours(12));
        var highWater = ServerContact.AddHours(4);

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddHours(3),
            highWater);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("clock_rollback", decision.Code);
    }

    [TestMethod]
    public void OnlineResponses_DeserializeAuthenticatedServerTime()
    {
        var firstLogin = Deserialize<PosFirstLoginResponse>(
            "{\"ok\":true,\"serverTime\":\"2026-07-15T12:00:00Z\"}");
        var heartbeat = Deserialize<PosHeartbeatResponse>(
            "{\"ok\":true,\"serverTime\":\"2026-07-15T13:00:00Z\"}");

        Assert.AreEqual("2026-07-15T12:00:00Z", firstLogin.ServerTime);
        Assert.AreEqual("2026-07-15T13:00:00Z", heartbeat.ServerTime);
    }

    private static PosTrustedDeviceSession Session(DateTimeOffset expiresAt)
    {
        return new PosTrustedDeviceSession
        {
            LastOkLocalAt = LocalReceipt.ToString("O"),
            LastOkServerAt = ServerContact.ToString("O"),
            PosSessionId = "session-test",
            SessionExpiresAt = expiresAt.ToString("O"),
            ShopDeviceId = "device-test"
        };
    }

    private static T Deserialize<T>(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(stream)!;
    }
}
