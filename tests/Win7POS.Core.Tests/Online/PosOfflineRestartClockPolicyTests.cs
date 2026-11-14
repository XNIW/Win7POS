using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosOfflineRestartClockPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");
    private static PosOfflineRestartReceipt Receipt() => new()
    {
        ProfileScope="profile-a",StateSha256="state-a",ReceiptId="receipt-a",
        GenerationFingerprint="shop-device-staff-version-session-a",ClockDomain="boot-a",
        Frequency=1000,Timestamp=1000,ServerAnchor=Now,LocalHighWater=Now
    };
    private static bool Advance(PosOfflineRestartReceipt? receipt, out PosOfflineRestartReceipt advanced,
        out string code, string profile="profile-a",string state="state-a",string id="receipt-a",
        string generation="shop-device-staff-version-session-a",string domain="boot-a",
        long frequency=1000,long timestamp=2000,DateTimeOffset? now=null) =>
        PosOfflineRestartClockPolicy.TryAdvance(receipt!,profile,state,id,generation,domain,
            frequency,timestamp,now??Now.AddSeconds(1),out advanced,out code);

    [TestMethod]
    public void AdvancesAcrossProcessesWithoutExtendingOriginalLease()
    {
        Assert.IsTrue(Advance(Receipt(),out var next,out var code));
        Assert.AreEqual("ok",code);
        Assert.AreEqual(Now.AddSeconds(1),next.ServerAnchor);
        Assert.IsTrue(Advance(next,out var reopened,out _,timestamp:3000,now:Now.AddSeconds(2)));
        Assert.AreEqual(Now.AddSeconds(2),reopened.ServerAnchor);
    }

    [TestMethod]
    public void FrozenWallClockStillConsumesTheWholeOfflineLease()
    {
        Assert.IsTrue(Advance(Receipt(),out var next,out _,timestamp:43201000,now:Now));
        Assert.AreEqual(Now.AddHours(12),next.ServerAnchor);
        var session=new PosTrustedDeviceSession {LastOkServerAt=Now.ToString("O"),
            LastOkLocalAt=Now.ToString("O"),SessionExpiresAt=Now.AddDays(1).ToString("O"),
            EffectiveOfflineAuthorizationExpiresAt=Now.AddHours(12).ToString("O"),OfflineAuthorizationAttested=true};
        Assert.AreEqual("offline_lease_expired",PosOfflineAuthorizationLeasePolicy.Evaluate(
            session,Now,null,next.ServerAnchor).Code);
    }

    [TestMethod]
    public void MissingWitnessAfterBootOrHiveUnloadCannotAuthorizeOldProfile()
    {
        Assert.IsFalse(Advance(null,out _,out var code));
        Assert.AreEqual("offline_attestation_required",code);
    }

    [TestMethod]
    [DataRow("profile")][DataRow("state")][DataRow("receipt")][DataRow("generation")]
    public void RejectsChangedProfileTamperingOldBackupOrDeviceStaffGeneration(string changed)
    {
        var receipt=Receipt();
        if(changed=="profile")receipt.ProfileScope="profile-b";
        if(changed=="state")receipt.StateSha256="old-state";
        if(changed=="receipt")receipt.ReceiptId="old-receipt";
        if(changed=="generation")receipt.GenerationFingerprint="other-device-or-credential";
        Assert.IsFalse(Advance(receipt,out _,out var code));
        Assert.AreEqual("offline_attestation_invalid",code);
    }

    [TestMethod]
    [DataRow("domain")][DataRow("frequency")][DataRow("counter")][DataRow("invalid-frequency")]
    public void RejectsLostMonotonicContinuity(string changed)
    {
        Assert.IsFalse(Advance(Receipt(),out _,out var code,
            domain:changed=="domain"?"new-boot":"boot-a",
            frequency:changed=="frequency"?2000:changed=="invalid-frequency"?0:1000,
            timestamp:changed=="counter"?999:2000));
        Assert.AreEqual("trusted_time_continuity_lost",code);
    }

    [TestMethod]
    public void RejectsWallRollbackAcrossReopenAndDoesNotMutateWitness()
    {
        var receipt=Receipt();receipt.LocalHighWater=Now.AddHours(2);
        Assert.IsFalse(Advance(receipt,out _,out var code,now:Now.AddHours(1)));
        Assert.AreEqual("clock_rollback",code);
        Assert.AreEqual(Now,receipt.ServerAnchor);
        Assert.AreEqual(1000L,receipt.Timestamp);
    }

    [TestMethod]
    public void QuantizationRoundsUpAndOverflowFailsClosed()
    {
        var receipt=Receipt();receipt.Frequency=3;
        Assert.IsTrue(Advance(receipt,out var next,out _,frequency:3,timestamp:1001));
        Assert.AreEqual(Now.AddTicks(3333334),next.ServerAnchor);
        receipt.ServerAnchor=DateTimeOffset.MaxValue;
        Assert.IsFalse(Advance(receipt,out _,out var code,frequency:3,timestamp:1001));
        Assert.AreEqual("trusted_time_continuity_lost",code);
    }

    [TestMethod]
    public void PersistentWitness_SurvivesBootAndClockAdjustmentWithSameIdentity()
    {
        var receipt=Receipt();receipt.PersistentAuthorization=true;
        Assert.IsTrue(Advance(receipt,out var next,out var code,
            domain:"boot-b",frequency:2000,timestamp:1,now:Now.AddDays(-1)));
        Assert.AreEqual("ok",code);
        Assert.IsTrue(next.PersistentAuthorization);
        Assert.AreEqual("boot-b",next.ClockDomain);
        Assert.AreEqual(Now,next.LocalHighWater);
    }

    [TestMethod]
    [DataRow("profile")][DataRow("state")][DataRow("receipt")][DataRow("generation")]
    public void PersistentWitness_StillRejectsOldBackupOrDifferentIdentity(string changed)
    {
        var receipt=Receipt();receipt.PersistentAuthorization=true;
        Assert.IsFalse(Advance(receipt,out _,out var code,
            profile:changed=="profile"?"other":"profile-a",
            state:changed=="state"?"old":"state-a",
            id:changed=="receipt"?"old":"receipt-a",
            generation:changed=="generation"?"revoked":"shop-device-staff-version-session-a"));
        Assert.AreEqual("offline_attestation_invalid",code);
    }
}
