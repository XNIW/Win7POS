using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogSyncPolicyTests
{
    [TestMethod]
    public void Evaluate_AtLeastThirtyPolicyCasesFollowIncrementalFirstMatrix()
    {
        var normalTriggers = new[]
        {
            CatalogSyncTrigger.StartOfDay,
            CatalogSyncTrigger.Periodic,
            CatalogSyncTrigger.Manual,
            CatalogSyncTrigger.Foreground,
            CatalogSyncTrigger.NetworkRecovered,
            CatalogSyncTrigger.CatalogImportAcked,
            CatalogSyncTrigger.PartialResume,
            CatalogSyncTrigger.FirstBootstrap,
            CatalogSyncTrigger.ShopTransition,
            CatalogSyncTrigger.RestoreCompleted,
            CatalogSyncTrigger.ExactnessMismatch
        };

        var cases = new List<(CatalogSyncTrigger Trigger, CatalogSyncState State, CatalogSyncMode Mode, CatalogFullSyncReason Reason)>
        {
            (CatalogSyncTrigger.FirstBootstrap, State(bootstrapCompleted: false), CatalogSyncMode.Full, CatalogFullSyncReason.FirstBootstrap),
            (CatalogSyncTrigger.StartOfDay, State(hasShopBinding: false), CatalogSyncMode.Full, CatalogFullSyncReason.MissingShopBinding),
            (CatalogSyncTrigger.Periodic, State(legacyCursorMissing: true), CatalogSyncMode.Full, CatalogFullSyncReason.MissingLegacyCursor),
            (CatalogSyncTrigger.CursorRejected, State(), CatalogSyncMode.Full, CatalogFullSyncReason.CursorRejectedOrExpired),
            (CatalogSyncTrigger.Periodic, State(cursorRejectedOrExpired: true), CatalogSyncMode.Full, CatalogFullSyncReason.CursorRejectedOrExpired),
            (CatalogSyncTrigger.ServerFullRequired, State(), CatalogSyncMode.Full, CatalogFullSyncReason.ServerRequestedReset),
            (CatalogSyncTrigger.Periodic, State(serverRequestedReset: true), CatalogSyncMode.Full, CatalogFullSyncReason.ServerRequestedReset),
            (CatalogSyncTrigger.ShopTransition, State(shopChanged: true), CatalogSyncMode.Full, CatalogFullSyncReason.ShopChanged),
            (CatalogSyncTrigger.RestoreCompleted, State(restoreRecoveryRequired: true), CatalogSyncMode.Full, CatalogFullSyncReason.RestoreRecovery),
            (CatalogSyncTrigger.ExactnessMismatch, State(exactnessRepairRequired: true), CatalogSyncMode.Full, CatalogFullSyncReason.ExactnessRepair),
            (CatalogSyncTrigger.AdministratorRepair, State(administratorRepairAuthorized: true), CatalogSyncMode.Full, CatalogFullSyncReason.AdministratorRepair),
            (CatalogSyncTrigger.Foreground, State(migrationInvalidatedCursor: true), CatalogSyncMode.Full, CatalogFullSyncReason.MigrationInvalidatedCursor),
            (CatalogSyncTrigger.AdministratorRepair, State(), CatalogSyncMode.Blocked, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.PartialResume, State(cursor: "cursor-7", hasPartialCheckpoint: true), CatalogSyncMode.ResumeIncremental, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.PartialResume, State(cursor: null, hasPartialCheckpoint: true), CatalogSyncMode.Blocked, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Manual, State(failure: CatalogSyncFailure.OperatorCancelled), CatalogSyncMode.NoOp, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Periodic, State(failure: CatalogSyncFailure.AuthenticationDenied), CatalogSyncMode.Blocked, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Periodic, State(failure: CatalogSyncFailure.UnsupportedContract), CatalogSyncMode.Blocked, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Periodic, State(failure: CatalogSyncFailure.DatabaseIntegrityFailed), CatalogSyncMode.Blocked, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Periodic, State(failure: CatalogSyncFailure.Network), CatalogSyncMode.Incremental, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Periodic, State(failure: CatalogSyncFailure.Timeout), CatalogSyncMode.Incremental, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Periodic, State(failure: CatalogSyncFailure.HttpServerError), CatalogSyncMode.Incremental, CatalogFullSyncReason.None),
            (CatalogSyncTrigger.Periodic, State(catalogIsStale: true), CatalogSyncMode.Incremental, CatalogFullSyncReason.None)
        };

        foreach (var trigger in normalTriggers)
        {
            cases.Add((trigger, State(cursor: "cursor-normal"), CatalogSyncMode.Incremental, CatalogFullSyncReason.None));
        }

        Assert.IsTrue(cases.Count >= 30, "The policy table must retain at least thirty cases.");
        foreach (var item in cases)
        {
            var decision = CatalogSyncPolicy.Evaluate(item.Trigger, item.State);
            Assert.AreEqual(item.Mode, decision.Mode, $"Unexpected mode for {item.Trigger}.");
            Assert.AreEqual(item.Reason, decision.FullReason, $"Unexpected full reason for {item.Trigger}.");
        }
    }

    [TestMethod]
    public void Evaluate_OneHundredNormalRunsAreAllIncrementalOrResume()
    {
        var triggers = new[]
        {
            CatalogSyncTrigger.StartOfDay,
            CatalogSyncTrigger.Periodic,
            CatalogSyncTrigger.Manual,
            CatalogSyncTrigger.Foreground,
            CatalogSyncTrigger.NetworkRecovered,
            CatalogSyncTrigger.CatalogImportAcked,
            CatalogSyncTrigger.PartialResume
        };

        var incrementalCount = 0;
        for (var index = 0; index < 100; index++)
        {
            var resume = index % 9 == 0;
            var decision = CatalogSyncPolicy.Evaluate(
                triggers[index % triggers.Length],
                State(cursor: "cursor-" + index, hasPartialCheckpoint: resume));

            if (decision.Mode == CatalogSyncMode.Incremental ||
                decision.Mode == CatalogSyncMode.ResumeIncremental)
            {
                incrementalCount++;
            }
        }

        Assert.AreEqual(100, incrementalCount);
    }

    [TestMethod]
    public void Evaluate_EachFullReasonIsExclusiveToItsEvidence()
    {
        var fullCases = new[]
        {
            (CatalogSyncTrigger.FirstBootstrap, State(bootstrapCompleted: false), CatalogFullSyncReason.FirstBootstrap),
            (CatalogSyncTrigger.Periodic, State(hasShopBinding: false), CatalogFullSyncReason.MissingShopBinding),
            (CatalogSyncTrigger.Periodic, State(legacyCursorMissing: true), CatalogFullSyncReason.MissingLegacyCursor),
            (CatalogSyncTrigger.CursorRejected, State(), CatalogFullSyncReason.CursorRejectedOrExpired),
            (CatalogSyncTrigger.ServerFullRequired, State(), CatalogFullSyncReason.ServerRequestedReset),
            (CatalogSyncTrigger.ShopTransition, State(shopChanged: true), CatalogFullSyncReason.ShopChanged),
            (CatalogSyncTrigger.RestoreCompleted, State(restoreRecoveryRequired: true), CatalogFullSyncReason.RestoreRecovery),
            (CatalogSyncTrigger.ExactnessMismatch, State(exactnessRepairRequired: true), CatalogFullSyncReason.ExactnessRepair),
            (CatalogSyncTrigger.AdministratorRepair, State(administratorRepairAuthorized: true), CatalogFullSyncReason.AdministratorRepair),
            (CatalogSyncTrigger.Periodic, State(migrationInvalidatedCursor: true), CatalogFullSyncReason.MigrationInvalidatedCursor)
        };

        foreach (var item in fullCases)
        {
            var decision = CatalogSyncPolicy.Evaluate(item.Item1, item.Item2);
            Assert.AreEqual(CatalogSyncMode.Full, decision.Mode);
            Assert.AreEqual(item.Item3, decision.FullReason);
            Assert.IsTrue(decision.IsBlocking);
            Assert.IsFalse(decision.PreserveExistingSaleSafe);
        }
    }

    [TestMethod]
    public void Evaluate_ForbiddenFailuresNeverBecomeFullOrChangeResumeCursor()
    {
        var failures = new[]
        {
            CatalogSyncFailure.Network,
            CatalogSyncFailure.Timeout,
            CatalogSyncFailure.HttpServerError,
            CatalogSyncFailure.AuthenticationDenied,
            CatalogSyncFailure.UnsupportedContract,
            CatalogSyncFailure.DatabaseIntegrityFailed,
            CatalogSyncFailure.OperatorCancelled
        };

        foreach (var failure in failures)
        {
            var decision = CatalogSyncPolicy.Evaluate(
                CatalogSyncTrigger.Manual,
                State(cursor: "stable-cursor", hasPartialCheckpoint: true, failure: failure));

            Assert.AreNotEqual(CatalogSyncMode.Full, decision.Mode, failure.ToString());
            Assert.AreEqual(CatalogFullSyncReason.None, decision.FullReason, failure.ToString());
            if (decision.Mode == CatalogSyncMode.ResumeIncremental || decision.Mode == CatalogSyncMode.NoOp)
            {
                Assert.AreEqual("stable-cursor", decision.ResumeCursor);
            }
        }
    }

    [TestMethod]
    public void Evaluate_ManualAndStaleCatalogStayIncremental()
    {
        var manual = CatalogSyncPolicy.Evaluate(CatalogSyncTrigger.Manual, State(cursor: "cursor"));
        var stale = CatalogSyncPolicy.Evaluate(
            CatalogSyncTrigger.Periodic,
            State(cursor: "cursor", catalogIsStale: true));

        Assert.AreEqual(CatalogSyncMode.Incremental, manual.Mode);
        Assert.AreEqual(CatalogSyncMode.Incremental, stale.Mode);
    }

    private static CatalogSyncState State(
        string? cursor = "cursor-stable",
        bool bootstrapCompleted = true,
        bool hasShopBinding = true,
        bool legacyCursorMissing = false,
        bool hasPartialCheckpoint = false,
        bool cursorRejectedOrExpired = false,
        bool serverRequestedReset = false,
        bool shopChanged = false,
        bool restoreRecoveryRequired = false,
        bool exactnessRepairRequired = false,
        bool administratorRepairAuthorized = false,
        bool migrationInvalidatedCursor = false,
        CatalogSyncFailure failure = CatalogSyncFailure.None,
        bool catalogIsStale = false)
    {
        return new CatalogSyncState(
            cursor,
            bootstrapCompleted,
            hasShopBinding,
            legacyCursorMissing,
            hasPartialCheckpoint,
            cursorRejectedOrExpired,
            serverRequestedReset,
            shopChanged,
            restoreRecoveryRequired,
            exactnessRepairRequired,
            administratorRepairAuthorized,
            migrationInvalidatedCursor,
            failure,
            catalogIsStale);
    }
}
