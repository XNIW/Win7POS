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
    public void Evaluate_ClampsToAuthoritativeStaffExpiryBeforePolicyTtl()
    {
        var session = Session(ServerContact.AddHours(12));
        session.EffectiveOfflineAuthorizationExpiresAt =
            ServerContact.AddMinutes(90).ToString("O");

        var allowed = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddMinutes(89));
        var expired = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddMinutes(90));

        Assert.IsTrue(allowed.Allowed);
        Assert.AreEqual(ServerContact.AddMinutes(90), allowed.EffectiveExpiresAt);
        Assert.IsFalse(expired.Allowed);
        Assert.AreEqual("offline_lease_expired", expired.Code);
    }

    [TestMethod]
    public void Evaluate_SessionExpiryWinsBeforeAuthoritativeStaffExpiry()
    {
        var session = Session(ServerContact.AddHours(2));
        session.EffectiveOfflineAuthorizationExpiresAt =
            ServerContact.AddHours(6).ToString("O");

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddHours(2));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(ServerContact.AddHours(2), decision.EffectiveExpiresAt);
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
    public void Evaluate_UsesMonotonicLowerBoundWhenWallClockIsFrozen()
    {
        var authoritativeExpiry = ServerContact.AddMilliseconds(200);
        var frozenHighWater = ServerContact.AddMilliseconds(50);
        var session = Session(ServerContact.AddHours(12));
        session.EffectiveOfflineAuthorizationExpiresAt =
            authoritativeExpiry.ToString("O");

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddMilliseconds(50),
            frozenHighWater,
            authoritativeExpiry);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("offline_lease_expired", decision.Code);
        Assert.AreEqual(authoritativeExpiry, decision.EstimatedServerNow);
    }

    [TestMethod]
    public void Evaluate_AllowsEqualWallHighWaterBeforeMonotonicExpiry()
    {
        var frozenHighWater = ServerContact.AddMilliseconds(50);
        var monotonicLowerBound = ServerContact.AddMilliseconds(75);
        var session = Session(ServerContact.AddHours(12));
        session.EffectiveOfflineAuthorizationExpiresAt =
            ServerContact.AddMilliseconds(200).ToString("O");

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddMilliseconds(50),
            frozenHighWater,
            monotonicLowerBound);

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual(monotonicLowerBound, decision.EstimatedServerNow);
        Assert.AreEqual(frozenHighWater, decision.WallEstimatedServerNow);
    }

    [TestMethod]
    public void Evaluate_WallRollbackStillDeniesWithMonotonicLowerBound()
    {
        var wallHighWater = ServerContact.AddMilliseconds(50);
        var session = Session(ServerContact.AddHours(12));

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddMilliseconds(49),
            wallHighWater,
            ServerContact.AddMilliseconds(100));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("clock_rollback", decision.Code);
        Assert.AreEqual(
            ServerContact.AddMilliseconds(49),
            decision.WallEstimatedServerNow);
    }

    [TestMethod]
    public void Evaluate_DeniesPersistedSessionWithoutAuthoritativeOfflineExpiry()
    {
        var session = Session(ServerContact.AddHours(12));
        session.EffectiveOfflineAuthorizationExpiresAt = null;
        session.OfflineAuthorizationAttested = false;

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddMinutes(1));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("offline_attestation_required", decision.Code);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Evaluate_DeniesLegacyTrustedStateVersions(int formatVersion)
    {
        var session = Session(ServerContact.AddHours(12));
        session.EffectiveOfflineAuthorizationExpiresAt = null;
        session.OfflineAuthorizationAttested = false;
        session.TrustedStateFormatVersion = formatVersion;

        var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,
            LocalReceipt.AddMinutes(1));

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("offline_attestation_required", decision.Code);
    }

    [TestMethod]
    public void Evaluate_DeniesMalformedOrAlreadyExpiredAuthoritativeExpiry()
    {
        var malformed = Session(ServerContact.AddHours(12));
        malformed.EffectiveOfflineAuthorizationExpiresAt = "not-a-time";
        Assert.AreEqual(
            "offline_attestation_invalid",
            PosOfflineAuthorizationLeasePolicy.Evaluate(malformed, LocalReceipt).Code);

        var expired = Session(ServerContact.AddHours(12));
        expired.EffectiveOfflineAuthorizationExpiresAt = ServerContact.ToString("O");
        Assert.AreEqual(
            "offline_attestation_invalid",
            PosOfflineAuthorizationLeasePolicy.Evaluate(expired, LocalReceipt).Code);

        var alreadyExpired = Session(ServerContact.AddHours(12));
        alreadyExpired.EffectiveOfflineAuthorizationExpiresAt =
            ServerContact.AddTicks(-1).ToString("O");
        Assert.AreEqual(
            "offline_attestation_invalid",
            PosOfflineAuthorizationLeasePolicy.Evaluate(
                alreadyExpired,
                LocalReceipt).Code);
    }

    [TestMethod]
    public void ValidateOnlineReceipt_AllowsLegacyAdminWithoutOfflineAttestation()
    {
        var session = Session(ServerContact.AddHours(12));
        session.EffectiveOfflineAuthorizationExpiresAt = null;
        session.OfflineAuthorizationAttested = false;

        var decision = PosOfflineAuthorizationLeasePolicy.ValidateOnlineReceipt(
            session,
            LocalReceipt);

        Assert.IsTrue(decision.Allowed);
    }

    [TestMethod]
    public void OnlineResponses_DeserializeAuthenticatedServerTime()
    {
        var firstLogin = Deserialize<PosFirstLoginResponse>(
            "{\"ok\":true,\"serverTime\":\"2026-07-15T12:00:00Z\"," +
            "\"effectiveOfflineAuthorizationExpiresAt\":\"2026-07-15T13:00:00Z\"}");
        var legacyFirstLogin = Deserialize<PosFirstLoginResponse>(
            "{\"ok\":true,\"serverTime\":\"2026-07-15T12:00:00Z\"}");
        var heartbeat = Deserialize<PosHeartbeatResponse>(
            "{\"ok\":true,\"serverTime\":\"2026-07-15T13:00:00Z\"}");

        Assert.AreEqual("2026-07-15T12:00:00Z", firstLogin.ServerTime);
        Assert.AreEqual(
            "2026-07-15T13:00:00Z",
            firstLogin.EffectiveOfflineAuthorizationExpiresAt);
        Assert.IsNull(legacyFirstLogin.EffectiveOfflineAuthorizationExpiresAt);
        Assert.AreEqual("2026-07-15T13:00:00Z", heartbeat.ServerTime);
    }

    [TestMethod]
    public void OnlineSyncGeneration_BindsShopDeviceStaffCredentialAndSession()
    {
        var baseline = Generation();

        Assert.AreNotEqual(
            baseline.Fingerprint,
            Generation(shopId: "shop-b").Fingerprint);
        Assert.AreNotEqual(
            baseline.Fingerprint,
            Generation(deviceId: "device-b").Fingerprint);
        Assert.AreNotEqual(
            baseline.Fingerprint,
            Generation(staffId: "staff-b").Fingerprint);
        Assert.AreNotEqual(
            baseline.Fingerprint,
            Generation(credentialVersion: 8).Fingerprint);
        Assert.AreNotEqual(
            baseline.Fingerprint,
            Generation(sessionId: "session-b").Fingerprint);
    }

    private static PosTrustedDeviceSession Session(DateTimeOffset expiresAt)
    {
        return new PosTrustedDeviceSession
        {
            EffectiveOfflineAuthorizationExpiresAt = expiresAt.ToString("O"),
            LastOkLocalAt = LocalReceipt.ToString("O"),
            LastOkServerAt = ServerContact.ToString("O"),
            OfflineAuthorizationAttested = true,
            PosSessionId = "session-test",
            SessionExpiresAt = expiresAt.ToString("O"),
            ShopDeviceId = "device-test",
            TrustedStateFormatVersion = 4
        };
    }

    private static OnlineSyncGeneration Generation(
        string shopId = "shop-a",
        string deviceId = "device-a",
        string staffId = "staff-a",
        int credentialVersion = 7,
        string sessionId = "session-a")
    {
        return new OnlineSyncGeneration(
            "generation-a",
            sessionId,
            deviceId,
            shopId,
            "SHOP-A",
            staffId,
            credentialVersion);
    }

    private static T Deserialize<T>(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(stream)!;
    }
}
