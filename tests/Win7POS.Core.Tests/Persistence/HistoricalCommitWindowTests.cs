using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed class HistoricalCommitWindowTests
{
    private static HistoricalPublicationProof Proof()
        =>new(Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),
            "synthetic-local-dataset",Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),0,1,new string('a',64),new string('b',64),"content_authorized_v2",
            new HistoricalPublicationLocalDecision(1,0,0,0,0,1000,1),new HistoricalPublicationCanonicalDecision(Guid.NewGuid().ToString("D"),1,1,0,0,1000));

    [TestMethod]
    public void CommitWindow_BindsOriginalProofAndIncludesRequestTimeWithoutRenewingTheDeadline()
    {
        var proof=Proof();long clock=1000;
        var grant=new HistoricalLocalWriteGrant(proof.OperationId,"1",60000,123,()=>{},()=>clock,1000,proof);
        clock=40877;
        var window=grant.CaptureCommitWindow(TimeSpan.FromSeconds(10));
        Assert.AreSame(proof,window.Proof);Assert.AreEqual(grant.AdmissionId,window.AdmissionId);
        Assert.AreEqual(60000,window.RemainingMilliseconds);Assert.AreEqual(123L,window.RequestElapsedMilliseconds);
        Assert.AreEqual(39877L,window.ElapsedMilliseconds);Assert.AreEqual(10000L,window.MinimumRemainingMilliseconds);
        clock++;
        Assert.AreEqual("history_local_write_grant_expired",Assert.Throws<InvalidOperationException>(()=>grant.CaptureCommitWindow(TimeSpan.FromSeconds(10))).Message);
    }

    [TestMethod]
    public void CommitWindow_RoundsElapsedUpAndRejectsRollbackAndCurrentActorRevocation()
    {
        var proof=Proof();long clock=1000000;var revoked=false;
        var grant=new HistoricalLocalWriteGrant(proof.OperationId,"1",60000,0,()=>{if(revoked)throw new InvalidOperationException("synthetic_revoked");},()=>clock,1000000,proof);
        clock++;Assert.AreEqual(1L,grant.CaptureCommitWindow(TimeSpan.FromSeconds(10)).ElapsedMilliseconds);
        clock=999999;Assert.Throws<InvalidOperationException>(()=>grant.CaptureCommitWindow(TimeSpan.Zero));
        clock=1000000;revoked=true;
        Assert.AreEqual("synthetic_revoked",Assert.Throws<InvalidOperationException>(()=>grant.CaptureCommitWindow(TimeSpan.Zero)).Message);
    }

    [TestMethod]
    public void CommitWindow_IdentityOnlyGrantCannotProduceContentCommitEvidence()
    {
        var grant=new HistoricalLocalWriteGrant(Guid.NewGuid().ToString("D"),"1",60000,0,()=>{});
        Assert.AreEqual("history_publication_content_required",Assert.Throws<InvalidOperationException>(()=>grant.CaptureCommitWindow(TimeSpan.Zero)).Message);
        Assert.Throws<ArgumentOutOfRangeException>(()=>grant.CaptureCommitWindow(TimeSpan.FromMilliseconds(-1)));
    }

    [TestMethod]
    public void CommitWindow_DelayedContinuationCannotRestartTheOriginalAuthorizationClock()
    {
        var proof=Proof();long clock=40500;
        var grant=new HistoricalLocalWriteGrant(proof.OperationId,"1",60000,100,()=>{},()=>clock,1000,proof,1000);
        var witness=grant.CaptureCommitWindow(TimeSpan.FromSeconds(10));
        Assert.AreEqual(39500L,witness.RequestElapsedMilliseconds);Assert.AreEqual(0L,witness.ElapsedMilliseconds);
        clock=41001;Assert.Throws<InvalidOperationException>(()=>grant.CaptureCommitWindow(TimeSpan.FromSeconds(10)));
        clock=51000;
        Assert.Throws<InvalidOperationException>(()=>new HistoricalLocalWriteGrant(proof.OperationId,"1",60000,100,()=>{},()=>clock,1000,proof,1000));
    }
}
