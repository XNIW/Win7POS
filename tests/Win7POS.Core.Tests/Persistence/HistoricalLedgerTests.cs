using System.Security.Cryptography;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Reports;
using Win7POS.Data;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed class HistoricalLedgerTests
{
    [TestMethod]
    public async Task ThreeCyclesKeepAuditAndUseNewInternalIds()
    {
        using var f = new Fixture(); var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var plan = await f.Plan(); var receipt = await f.Service.ApplyAsync(plan, f.Guard(plan), f.Backup(), default);
            Assert.AreEqual(1, receipt.InsertedCount); Assert.AreEqual(1234L, receipt.SignedAmountClp);
            using (var c = f.Factory.Open()) ids.Add(c.ExecuteScalar<long>("SELECT id FROM historical_transactions WHERE is_active=1"));
            var undo = f.UndoGuard(receipt.BatchId, 1);
            await f.Service.UndoAsync(receipt.BatchId, 1, undo, f.Backup(), default);
        }
        Assert.AreEqual(3, ids.Distinct().Count());
        using var db = f.Factory.Open();
        Assert.AreEqual(3L, db.ExecuteScalar<long>("SELECT count(*) FROM historical_transactions WHERE is_active=0"));
        Assert.AreEqual(6L, db.ExecuteScalar<long>("SELECT count(*) FROM historical_import_operation"));
        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT count(*) FROM sales"));
        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT count(*) FROM sales_sync_outbox"));
        Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT count(*) FROM pragma_foreign_key_check"));
    }

    [TestMethod]
    public async Task DuplicateObserverOwnsNothingAndReplayNeedsNoSourceOrBackup()
    {
        using var f = new Fixture(); var a = await f.Plan();
        var first = await f.Service.ApplyAsync(a, f.Guard(a), f.Backup(), default);
        var b = await f.Plan(); var second = await f.Service.ApplyAsync(b, f.Guard(b), f.Backup(), default);
        Assert.AreEqual(0, second.InsertedCount); Assert.AreEqual(1, second.DuplicateCount);
        await f.Service.UndoAsync(first.BatchId, 1, f.UndoGuard(first.BatchId, 1), f.Backup(), default);
        using (var c = f.Factory.Open()) Assert.AreEqual(0L, c.ExecuteScalar<long>("SELECT count(*) FROM historical_transactions WHERE is_active=1"));
        File.Delete(f.Source);
        var replay = await f.Service.ApplyAsync(a, f.Guard(a), Path.Combine(f.Root, "missing", "not-used.db"), default);
        Assert.IsTrue(replay.IsReplay); Assert.AreEqual(first.BatchId, replay.BatchId); Assert.AreEqual("Undone", replay.CurrentBatchState);
        Assert.AreEqual(1, replay.InsertedCount); Assert.AreEqual(1234L, replay.SignedAmountClp);
    }

    [TestMethod]
    public async Task EconomicConflictRollsBackEverything()
    {
        using var f = new Fixture(); var a = await f.Plan(); await f.Service.ApplyAsync(a, f.Guard(a), f.Backup(), default);
        var conflict = await f.Plan(amount: 999);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(conflict, f.Guard(conflict), f.Backup(), default));
        using var c = f.Factory.Open();
        Assert.AreEqual(1L, c.ExecuteScalar<long>("SELECT count(*) FROM historical_import_batch"));
        Assert.AreEqual(1L, c.ExecuteScalar<long>("SELECT count(*) FROM historical_import_operation"));
        Assert.AreEqual(1234L, c.ExecuteScalar<long>("SELECT SUM(signed_amount_clp) FROM historical_transactions WHERE is_active=1"));
    }

    [TestMethod]
    public async Task InactiveEconomicCorrectionRequiresReviewedApprovalAndPersistsItAcrossRestart()
    {
        using var f = new Fixture(); var firstPlan = await f.Plan(amount: 1000);
        var first = await f.Service.ApplyAsync(firstPlan, f.Guard(firstPlan), f.Backup(), default);
        long previousId; using (var c = f.Factory.Open()) previousId = c.ExecuteScalar<long>("SELECT id FROM historical_transactions");
        await f.Service.UndoAsync(first.BatchId, 1, f.UndoGuard(first.BatchId, 1), f.Backup(), default);
        var proposed = await f.Plan(amount: 9000);
        var preview = await f.Service.PreviewAsync(proposed, default);
        Assert.AreEqual(1, preview.CorrectionCount); Assert.AreEqual(1, preview.UnapprovedCorrectionCount); Assert.IsFalse(preview.CanApply);
        var candidate = preview.Corrections.Single();
        Assert.AreEqual(previousId, candidate.PreviousTransactionId); Assert.AreEqual(1000L, candidate.Previous.GrossClp); Assert.AreEqual(9000L, candidate.Proposed.GrossClp);
        Assert.AreEqual("synthetic", candidate.SourceSheet); Assert.AreEqual(2, candidate.SourceRow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(proposed, f.Guard(proposed), f.Backup(), default));
        var approved = WithApprovals(proposed, new[] { candidate.Approve("synthetic-correction-evidence") });
        Assert.AreNotEqual(proposed.RequestFingerprint, approved.RequestFingerprint);
        Assert.IsTrue((await f.Service.PreviewAsync(approved, default)).CanApply);
        var result = await f.Service.ApplyAsync(approved, f.Guard(approved), f.Backup(), default);
        Assert.AreEqual(1, result.CorrectionCount); Assert.AreEqual(9000L, result.SignedAmountClp);
        using (var c = f.Factory.Open())
        {
            Assert.AreEqual(previousId, c.ExecuteScalar<long>("SELECT supersedes_id FROM historical_transactions WHERE is_active=1"));
            Assert.AreEqual(1L, c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_observation WHERE observation_kind='Correction' AND existing_transaction_id=@previousId", new { previousId }));
            Assert.AreEqual(1000L, c.ExecuteScalar<long>("SELECT signed_amount_clp FROM historical_transactions WHERE id=@previousId AND is_active=0", new { previousId }));
        }
        f.Restart();
        var persisted = await f.Service.GetReceiptAsync(approved.OperationId, "shop-test", default);
        Assert.IsNotNull(persisted); Assert.AreEqual(1, persisted.CorrectionCount);
        Assert.AreEqual("synthetic-correction-evidence", persisted.CorrectionApprovals.Single().EvidenceReference);
        Assert.AreEqual(1000L, persisted.CorrectionApprovals.Single().Previous.GrossClp);
        Assert.AreEqual(9000L, persisted.CorrectionApprovals.Single().Proposed.GrossClp);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InactiveCorrectionApprovalCannotBeReusedAfterRevisionOrLatestVersionChanges(bool replaceLatest)
    {
        using var f = new Fixture(); var firstPlan = await f.Plan(amount: 1000);
        var first = await f.Service.ApplyAsync(firstPlan, f.Guard(firstPlan), f.Backup(), default);
        await f.Service.UndoAsync(first.BatchId, 1, f.UndoGuard(first.BatchId, 1), f.Backup(), default);
        var proposed = await f.Plan(amount: 9000); var preview = await f.Service.PreviewAsync(proposed, default);
        var oldApproval = preview.Corrections.Single().Approve("synthetic-reviewed-before-change");
        var intervening = await f.Plan(amount: 1000, id: replaceLatest ? "000123" : "unrelated");
        var inserted = await f.Service.ApplyAsync(intervening, f.Guard(intervening), f.Backup(), default);
        if (replaceLatest) await f.Service.UndoAsync(inserted.BatchId, 1, f.UndoGuard(inserted.BatchId, 1), f.Backup(), default);
        var rebuilt = WithApprovals(await f.Plan(amount: 9000), new[] { oldApproval });
        Assert.IsFalse((await f.Service.PreviewAsync(rebuilt, default)).CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(rebuilt, f.Guard(rebuilt), f.Backup(), default));
        using var c = f.Factory.Open();
        Assert.AreEqual(0L, c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1 AND external_id='000123'"));
    }

    [TestMethod]
    public async Task InactiveCorrectionApprovalCannotBeReusedForDifferentProposedEconomics()
    {
        using var f = new Fixture(); var firstPlan = await f.Plan(amount: 1000);
        var first = await f.Service.ApplyAsync(firstPlan, f.Guard(firstPlan), f.Backup(), default);
        await f.Service.UndoAsync(first.BatchId, 1, f.UndoGuard(first.BatchId, 1), f.Backup(), default);
        var preview = await f.Service.PreviewAsync(await f.Plan(amount: 9000), default);
        var changed = WithApprovals(await f.Plan(amount: 8000), new[] { preview.Corrections.Single().Approve("synthetic-9000-only") });
        Assert.IsFalse((await f.Service.PreviewAsync(changed, default)).CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(changed, f.Guard(changed), f.Backup(), default));
    }

    [TestMethod]
    public async Task ActiveFeeVatSwapConflictsEvenWhenGrossAndNetAreEqual()
    {
        using var f = new Fixture(); var first = await f.Plan(amount: 1000, fee: -10, vat: -5, net: 985);
        await f.Service.ApplyAsync(first, f.Guard(first), f.Backup(), default);
        var swap = await f.Plan(amount: 1000, fee: -12, vat: -3, net: 985);
        Assert.AreEqual(1, (await f.Service.PreviewAsync(swap, default)).ConflictCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(swap, f.Guard(swap), f.Backup(), default));
        using var c = f.Factory.Open(); Assert.AreEqual(-10L, c.ExecuteScalar<long>("SELECT signed_fee_clp FROM historical_transactions"));
        Assert.AreEqual(1L, c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
    }

    [TestMethod]
    public async Task ActiveEconomicStateDivergenceConflictsButCaseVariationDoesNot()
    {
        using var f = new Fixture(); var first = await f.Plan(amount: 1000);
        await f.Service.ApplyAsync(first, f.Guard(first), f.Backup(), default);
        var equivalent = await f.Plan(amount: 1000, transactionState: "aprobada");
        Assert.AreEqual(1, (await f.Service.PreviewAsync(equivalent, default)).DuplicateCount);
        var conflict = await f.Plan(amount: 1000, transactionState: "ANULADA");
        Assert.AreEqual(1, (await f.Service.PreviewAsync(conflict, default)).ConflictCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(conflict, f.Guard(conflict), f.Backup(), default));
    }

    [TestMethod]
    public async Task SettlementMetadataOnlyIsObservedWithoutOverwritingEconomicsOrChangingRevision()
    {
        using var f = new Fixture(); var first = await f.Plan(amount: 1000, settlement: "Pending");
        await f.Service.ApplyAsync(first, f.Guard(first), f.Backup(), default);
        var next = await f.Plan(amount: 1000, settlement: "Settled");
        Assert.IsTrue((await f.Service.PreviewAsync(next, default)).CanApply);
        var result = await f.Service.ApplyAsync(next, f.Guard(next), f.Backup(), default);
        Assert.AreEqual(1, result.DuplicateCount); Assert.AreEqual(0, result.CorrectionCount);
        using var c = f.Factory.Open();
        Assert.AreEqual("APROBADA|Pending", c.ExecuteScalar<string>("SELECT state||'|'||settlement_state FROM historical_transactions"));
        StringAssert.Contains(c.ExecuteScalar<string>("SELECT code FROM historical_import_observation WHERE observation_kind='Metadata'"), "Settled");
        Assert.AreEqual(next.DataRevision, (await f.Service.ReadDatasetAsync(default)).DataRevision);
    }

    private static HistoricalSalesImportPlan WithApprovals(HistoricalSalesImportPlan p, IEnumerable<HistoricalSalesCorrectionApproval> approvals)
        => new(p.OperationId, p.ShopId, p.DatasetId, p.DataRevision, p.RestoreEpoch, p.Analysis, p.Assignments, p.NativeStartBusinessDateTime, p.OverlapEvidenceReference, approvals);

    [TestMethod]
    public async Task MerchantAssignmentDisplayLimitAlsoBlocksOversizedPlansWithoutDroppingSourceAnalysis()
    {
        using var f = new Fixture(); var p = await f.Plan();
        var assignments = Enumerable.Range(0, 201).Select(i => new HistoricalMerchantAssignment(
            new HistoricalSalesMerchant("SYNTHETIC-" + i, "fixture", "42", "1", "test"), "scope-" + i, "binding-" + i, "synthetic-evidence")).ToArray();
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new HistoricalSalesImportPlan(p.OperationId, p.ShopId, p.DatasetId, p.DataRevision, p.RestoreEpoch,
            p.Analysis, assignments, p.NativeStartBusinessDateTime, p.OverlapEvidenceReference));
        Assert.AreEqual("historical_merchant_assignment_limit", error.Message);
        Assert.AreEqual(1, p.Analysis.AcceptedTransactions.Count);
    }

    [TestMethod]
    public async Task FinalAuthorityRevocationRollsBackRevisionAndReceipt()
    {
        using var f = new Fixture(); var plan = await f.Plan();
        var guard = f.Guard(plan, commit: (_, _) => throw new InvalidOperationException("revoked"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(plan, guard, f.Backup(), default));
        using var c = f.Factory.Open();
        Assert.AreEqual(0L, c.ExecuteScalar<long>("SELECT count(*) FROM historical_transactions"));
        Assert.AreEqual(0L, c.ExecuteScalar<long>("SELECT count(*) FROM historical_import_operation"));
        Assert.AreEqual(plan.DataRevision, (await f.Service.ReadDatasetAsync(default)).DataRevision);
    }

    [TestMethod]
    public async Task StalePlanAndRestoreEpochFailClosed()
    {
        using var f = new Fixture(); var stale = await f.Plan(); var other = await f.Plan(id: "other");
        await f.Service.ApplyAsync(other, f.Guard(other), f.Backup(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(stale, f.Guard(stale), f.Backup(), default));
        var fresh = await f.Plan(); f.Epoch++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(fresh, f.Guard(fresh), f.Backup(), default));
    }

    [TestMethod]
    public async Task NamespaceAndIdAreExactStringsNotTimestampAmountHeuristics()
    {
        using var f = new Fixture(); var a = await f.Plan(id: "000000000000000001"); await f.Service.ApplyAsync(a, f.Guard(a), f.Backup(), default);
        var b = await f.Plan(id: "000000000000000002"); await f.Service.ApplyAsync(b, f.Guard(b), f.Backup(), default);
        var cplan = await f.Plan(id: "000000000000000001", ns: "financial-id-v1"); await f.Service.ApplyAsync(cplan, f.Guard(cplan), f.Backup(), default);
        using var c = f.Factory.Open(); Assert.AreEqual(3L, c.ExecuteScalar<long>("SELECT count(*) FROM historical_transactions WHERE is_active=1"));
    }

    [TestMethod]
    public async Task TerminalOperationCannotBeReboundOrUpdated()
    {
        using var f = new Fixture(); var a = await f.Plan(); await f.Service.ApplyAsync(a, f.Guard(a), f.Backup(), default);
        var b = await f.Plan(operation: a.OperationId, id: "different");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(b, f.Guard(b), f.Backup(), default));
        using var c = f.Factory.Open();
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => c.Execute("UPDATE historical_import_operation SET inserted_count=0"));
    }

    [TestMethod]
    public async Task SourceMutationFailsBeforeBackupAndRows()
    {
        using var f = new Fixture(); var p = await f.Plan(); File.AppendAllText(f.Source, "changed"); var backup = f.Backup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(p, f.Guard(p), backup, default));
        Assert.IsFalse(File.Exists(backup));
        using var c = f.Factory.Open(); Assert.AreEqual(0L, c.ExecuteScalar<long>("SELECT count(*) FROM historical_import_batch"));
    }

    [TestMethod]
    public async Task ReconstructedRequestReplaysAcrossRevisionAndRestartWithoutSource()
    {
        using var f = new Fixture(); var original = await f.Plan();
        var receipt = await f.Service.ApplyAsync(original, f.Guard(original), f.Backup(), default);
        var other = await f.Plan(id: "unrelated"); await f.Service.ApplyAsync(other, f.Guard(other), f.Backup(), default);
        f.Restart();
        var reconstructed = await f.Plan(operation: original.OperationId);
        Assert.AreEqual(original.RequestFingerprint, reconstructed.RequestFingerprint);
        var persisted = await f.Service.GetReceiptAsync(original.OperationId, "shop-test", default);
        Assert.IsNotNull(persisted); Assert.AreEqual(receipt.BatchId, persisted.BatchId); Assert.AreEqual(1234L, persisted.SignedAmountClp);
        Assert.IsNull(await f.Service.GetReceiptAsync(original.OperationId, "other-shop", default));
        File.Delete(f.Source);
        var replay = await f.Service.TryReplayAsync(f.Guard(reconstructed), default);
        Assert.IsNotNull(replay); Assert.IsTrue(replay.IsReplay); Assert.AreEqual(receipt.BatchId, replay.BatchId);
    }

    [TestMethod]
    public async Task SelectedSourcesRemainWriteProtectedThroughTheCommitBoundary()
    {
        using var f = new Fixture(); var plan = await f.Plan(); var checkedAtCommit = false;
        f.Hooks.AtPhase = (phase, _, _) =>
        {
            if (phase != "before_commit") return;
            checkedAtCommit = true;
            Assert.ThrowsExactly<IOException>(() => { using var writer = new FileStream(f.Source, FileMode.Open, FileAccess.Write, FileShare.None); });
        };
        await f.Service.ApplyAsync(plan, f.Guard(plan), f.Backup(), default);
        Assert.IsTrue(checkedAtCommit);
        using var writableAfterReturn = new FileStream(f.Source, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.IsTrue(writableAfterReturn.CanWrite);
    }

    [TestMethod]
    public async Task VerifiedSourcesAreRequestBoundAndConcurrentDisposalCannotReleaseAnActiveApply()
    {
        using var f = new Fixture(); var plan = await f.Plan();
        using var verified = await f.Service.VerifySourcesAsync(plan, default);
        var different = await f.Plan(amount: 999);
        var wrongBackup = f.Backup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ApplyAsync(different, f.Guard(different), wrongBackup, default, verified));
        Assert.IsFalse(File.Exists(wrongBackup));
        f.Hooks.AtPhase = (phase, _, _) =>
        {
            if (phase != "before_commit") return;
            verified.Dispose();
            Assert.ThrowsExactly<IOException>(() => { using var writer = new FileStream(f.Source, FileMode.Open, FileAccess.Write, FileShare.None); });
        };
        await f.Service.ApplyAsync(plan, f.Guard(plan), f.Backup(), default, verified);
        using var writerAfterUse = new FileStream(f.Source, FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.IsTrue(writerAfterUse.CanWrite);
    }

    [TestMethod]
    public async Task SharedAdmissionCancellationIsHonoredBeforeBackupOrMaintenanceBody()
    {
        using var f = new Fixture(); var plan = await f.Plan();
        using var verified = await f.Service.VerifySourcesAsync(plan, default);
        using var admission = new CancellationTokenSource(); admission.Cancel();
        var entered = false; var backup = f.Backup();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Service.ApplyAsync(plan, f.Guard(plan), backup, default, verified, admission.Token, () => entered = true));
        Assert.IsFalse(entered); Assert.IsFalse(File.Exists(backup));
        using var c = f.Factory.Open(); Assert.AreEqual(0L, c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
    }

    [TestMethod]
    public async Task ReadOnlyIdentityReplayNeedsNoMutationGuardAndRejectsWrongShopOrFingerprint()
    {
        using var f = new Fixture(); var plan = await f.Plan();
        var first = await f.Service.ApplyAsync(plan, f.Guard(plan), f.Backup(), default);
        f.Restart(); File.Delete(f.Source);
        var identity = new HistoricalSalesOperationIdentity(HistoricalImportOperationKind.Apply, plan.OperationId, plan.RequestFingerprint, plan.ShopId, plan.DatasetId);
        var replay = await f.Service.TryReplayAsync(identity, default);
        Assert.IsNotNull(replay); Assert.AreEqual(first.BatchId, replay.BatchId); Assert.IsTrue(replay.IsReplay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.TryReplayAsync(new HistoricalSalesOperationIdentity(identity.Kind, identity.OperationId, identity.RequestFingerprint, "other-shop", identity.DatasetId), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.TryReplayAsync(new HistoricalSalesOperationIdentity(identity.Kind, identity.OperationId, "different-request", identity.ShopId, identity.DatasetId), default));
    }

    [TestMethod]
    public async Task OffsetZeroInterpretationDecisionIsDistinctAndPersistedInFileAudit()
    {
        using var f = new Fixture();
        var iso = await f.Plan(operation: "synthetic-time-decision", modernChoice: HistoricalSalesModernTimeInterpretation.IsoUtc);
        var local = await f.Plan(operation: "synthetic-time-decision", modernChoice: HistoricalSalesModernTimeInterpretation.ExportedLocal);
        var isoRow = iso.Analysis.AcceptedTransactions.Single(); var localRow = local.Analysis.AcceptedTransactions.Single();
        Assert.AreEqual(isoRow.BusinessDateTime, localRow.BusinessDateTime); Assert.AreEqual(isoRow.Timestamp.OccurredAtUtc, localRow.Timestamp.OccurredAtUtc);
        Assert.AreEqual(isoRow.Timestamp.RawTimestamp, localRow.Timestamp.RawTimestamp); Assert.AreEqual(isoRow.Timestamp.RawOffset, localRow.Timestamp.RawOffset);
        Assert.AreNotEqual(iso.RequestFingerprint, local.RequestFingerprint);
        var applied = await f.Service.ApplyAsync(iso, f.Guard(iso), f.Backup(), default);
        f.Restart();
        using var connection = f.Factory.Open();
        Assert.AreEqual("synthetic-v1;interpretation=IsoUtc;CLP=exported", connection.ExecuteScalar<string>("SELECT temporal_policy FROM historical_import_file WHERE batch_id=@BatchId", applied));
        Assert.AreEqual("synthetic-evidence", connection.ExecuteScalar<string>("SELECT evidence_reference FROM historical_import_file WHERE batch_id=@BatchId", applied));
    }

    [TestMethod]
    public async Task LegacyLocalClockAndExplicitClpApprovalRemainReadableInFileAudit()
    {
        using var f = new Fixture(); var plan = await f.Plan();
        var applied = await f.Service.ApplyAsync(plan, f.Guard(plan), f.Backup(), default);
        using var connection = f.Factory.Open();
        Assert.AreEqual("synthetic-v1;interpretation=LegacyLocal;CLP=explicitly-approved", connection.ExecuteScalar<string>("SELECT temporal_policy FROM historical_import_file WHERE batch_id=@BatchId", applied));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "Win7POS-History-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "synthetic-source.bin");
        private PosDatabaseAuthorityLease _owner;
        internal HistoricalSalesImportTestHooks Hooks { get; } = new();
        internal SqliteConnectionFactory Factory { get; private set; }
        internal HistoricalSalesImportService Service { get; private set; }
        internal long Epoch;
        internal Fixture()
        {
            Directory.CreateDirectory(Root); File.WriteAllText(Source, "independent synthetic source bytes");
            var opt = PosDbOptions.ForPath(Path.Combine(Root, "fixture.db")); _owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(opt); opt = opt.WithAccessContext(_owner.AccessContext); Factory = new SqliteConnectionFactory(opt); DbInitializer.EnsureCreated(opt);
            Service = new HistoricalSalesImportService(Factory, () => new SalesReportAuthorityStamp(Epoch, Factory.AuthorityStamp.AuthorityInstanceEpoch), Hooks);
        }
        internal void Restart()
        {
            var token = _owner.FixtureToken; var options = PosDbOptions.ForPath(Factory.DbPath);
            SqliteConnectionFactory.ClearAllPools(); _owner.Dispose();
            _owner = PosDatabaseAuthorityLease.AcquireSyntheticExisting(options, token);
            options = options.WithAccessContext(_owner.AccessContext); Factory = new SqliteConnectionFactory(options);
            Service = new HistoricalSalesImportService(Factory, () => new SalesReportAuthorityStamp(Epoch, Factory.AuthorityStamp.AuthorityInstanceEpoch), Hooks);
        }
        internal string Backup() => Path.Combine(Root, "backup-" + Guid.NewGuid().ToString("N") + ".db");
        internal async Task<HistoricalSalesImportPlan> Plan(long amount = 1234, string id = "000123", string ns = "legacy-id-v1", string? operation = null,
            long? fee = 0, long? vat = 0, long? net = null, string settlement = "LIQUIDADA", string transactionState = "APROBADA", HistoricalSalesModernTimeInterpretation? modernChoice = null)
        {
            var m = new HistoricalSalesMerchant("SYNTHETIC", "fixture", "42", "1", "test");
            var t = modernChoice.HasValue ? HistoricalSalesTimeRules.ParseModern("2020-01-02T11:12:13Z", "0", new HistoricalSalesImportOptions { ModernTimeInterpretation = modernChoice.Value, TemporalPolicyVersion = "synthetic-v1", TemporalEvidenceReference = "synthetic-evidence" }) :
                new HistoricalSalesTimestamp("2020-01-02 11:12:13", "", null, new DateTime(2020, 1, 2, 11, 12, 13), null, null, null, HistoricalSalesModernTimeInterpretation.Unresolved, "synthetic-v1", "synthetic-evidence", false);
            var profile = modernChoice.HasValue ? HistoricalSalesProfile.GetnetModern : HistoricalSalesProfile.GetnetLegacy;
            var row = new HistoricalSalesTransaction(new HistoricalSalesSource("f1", "synthetic", 2), profile, ns, id, "", "", m, t, HistoricalSalesMovementKind.Sale, transactionState, settlement, amount, fee, vat, net ?? amount, HistoricalSalesCardSubtype.Debit, "", "", "", "");
            var bytes = File.ReadAllBytes(Source); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var f = new HistoricalSalesImportFile("f1", Source, hash, bytes.Length, "synthetic", profile, 1);
            var summary = new HistoricalSalesImportSummary(1, 1, 1, 0, 0, amount, amount, 0, amount, 1, 0, 0, t.BusinessDateTime, t.BusinessDateTime);
            var analysis = new HistoricalSalesImportAnalysis(new[] { f }, new[] { row }, Array.Empty<HistoricalSalesRejectedRow>(), Array.Empty<HistoricalSalesImportDiagnostic>(), summary, false);
            var state = await Service.ReadDatasetAsync(default);
            return new HistoricalSalesImportPlan(operation ?? Guid.NewGuid().ToString("N"), "shop-test", state.DatasetId, state.DataRevision, Epoch, analysis,
                new[] { new HistoricalMerchantAssignment(m, "scope-test", "binding-test", "synthetic-evidence") }, new DateTime(2021, 1, 1), "synthetic-start-proof");
        }
        internal HistoricalImportAuthorizationCommitGuard Guard(HistoricalSalesImportPlan p, Action<TimeSpan, Action>? commit = null)
            => Make(HistoricalImportOperationKind.Apply, p.OperationId, p.RequestFingerprint, p.DatasetId, p.RestoreEpoch, commit);
        internal HistoricalImportAuthorizationCommitGuard UndoGuard(string batch, long revision)
        {
            var s = Service.ReadDatasetAsync(default).GetAwaiter().GetResult(); var op = Guid.NewGuid().ToString("N");
            return Make(HistoricalImportOperationKind.Undo, op, HistoricalSalesImportPlan.UndoFingerprint(op, "shop-test", s.DatasetId, batch, revision, Epoch), s.DatasetId, Epoch, null);
        }
        private static HistoricalImportAuthorizationCommitGuard Make(HistoricalImportOperationKind kind, string op, string hash, string dataset, long epoch, Action<TimeSpan, Action>? commit)
            => new(kind, op, hash, "shop-test", "SHOP", "device", 1, dataset, epoch, 1, "generation", "staff", 1, () => { }, commit ?? ((_, a) => a()));
        public void Dispose() { SqliteConnectionFactory.ClearAllPools(); _owner.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
