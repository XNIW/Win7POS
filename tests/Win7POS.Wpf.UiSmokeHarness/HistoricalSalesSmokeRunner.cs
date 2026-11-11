using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Reports;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Data.Reports;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HistoricalSalesSmokeRunner
    {
        internal static async Task<int> RunAsync(string[] args)
        {
            var data = Arg(args, "--data-dir");
            var receipt = new HistoricalSalesQaReceipt(Arg(args, "--context-file"), "Smoke", Arg(args, "--receipt"));
            var visuals = new SyntheticQaVisualWriter(receipt.Context, data, Path.GetDirectoryName(Arg(args, "--receipt")));
            foreach (var id in HistoricalSalesQaReceipt.Contract("Smoke").Keys)
                await receipt.RunAsync(id, async test =>
                {
                    switch (id)
                    {
                        case "HIST01_PROFILES_PREVIEW": Profiles(data, test); break;
                        case "HIST02_APPLY_REOPEN": await ApplyReopenAsync(data, test); break;
                        case "HIST03_IDENTITY_DEDUP": await DedupAsync(data, test); break;
                        case "HIST04_STALE_AUTH_SCOPE": await StaleAsync(data, test); break;
                        case "HIST05_ATOMIC_CANCEL_RECOVERY": await AtomicAsync(data, test); break;
                        case "HIST06_BATCH_CANCEL_REIMPORT": await UndoAsync(data, test); break;
                        case "HIST07_REPORTS_EXPORT": await ReportsAsync(data, test, visuals); break;
                        case "HIST08_UI_LIFECYCLE": await UiAsync(data, test, visuals); break;
                    }
                });
            return receipt.Complete();
        }

        private static HistoricalSalesImportOptions Options() => new HistoricalSalesImportOptions { LegacyClpConfirmed = true, ModernTimeInterpretation = HistoricalSalesModernTimeInterpretation.IsoUtc, TemporalPolicyVersion = "synthetic-fixed-offset-v1", TemporalEvidenceReference = "synthetic-explicit-UTC-evidence" };
        private static string[] SourceFiles() => new[] { "synthetic-legacy.xlsx", "synthetic-modern.xlsx" }.Select(name => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "historical-sales-getnet-v1", name)).ToArray();
        private static HistoricalSalesImportAnalysis Analyze(params string[] files) => new HistoricalSalesImportAnalyzer().Analyze(files, Options());
        private static void Profiles(string data, HistoricalSalesQaCase test)
        {
            var files = SourceFiles(); var analysis = Analyze(files);
            test.Check("profiles_detected", analysis.Files.Select(x => x.Profile).Distinct().Count() == 2 && analysis.CanApply);
            var root = Path.Combine(data, "profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var reordered = Path.Combine(root, "reordered.xlsx");
            HistoricalSalesFixtureWriter.Write(reordered, HistoricalSalesFixtureWriter.LegacyHeaders, new[] { HistoricalSalesFixtureWriter.LegacyRow("0009876543210987654321", 1000) }, true);
            var row = Analyze(reordered);
            test.Check("header_mapping", row.CanApply && row.Summary.GrossClp == 1000);
            test.Check("exact_values", analysis.Summary.SourceRows == 6 && analysis.Summary.AcceptedCount == 5 && analysis.Summary.SaleCount == 4 && analysis.Summary.CancellationCount == 1 && analysis.Summary.RejectedCount == 1 && analysis.Summary.GrossClp == 3380 && analysis.Summary.KnownNetClp == 3320, 3380L, analysis.Summary.GrossClp);
            test.Check("text_ids", row.AcceptedTransactions.Single().ExternalId == "0009876543210987654321" && analysis.AcceptedTransactions.Any(x => x.AuthorizationCode == "000AB7"));
            var unresolved = Options(); unresolved.ModernTimeInterpretation = HistoricalSalesModernTimeInterpretation.Unresolved;
            test.Check("policy_visible", new HistoricalSalesImportAnalyzer().Analyze(files, unresolved).NeedsEvidence && analysis.AcceptedTransactions.All(x => x.Timestamp.PolicyVersion == "synthetic-fixed-offset-v1"));
            var invalid = Path.Combine(root, "ambiguous.xlsx");
            HistoricalSalesFixtureWriter.Write(invalid, HistoricalSalesFixtureWriter.LegacyHeaders.Concat(new[] { "FECHA VENTA" }).ToArray(), new[] { HistoricalSalesFixtureWriter.LegacyRow("bad", 1).Concat(new[] { "02/01/2000 08:00:00" }).ToArray() });
            test.Check("invalid_input_blocked", !Analyze(invalid).CanApply);
        }

        private static async Task ApplyReopenAsync(string data, HistoricalSalesQaCase test)
        {
            bool durable;
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var before = NativeState(f); var plan = await f.PreparePlanAsync(Analyze(SourceFiles())); var applied = await f.ApplyAsync(plan);
                durable = applied.InsertedCount == 5 && f.Count("historical_import_operation") == 1;
                test.Check("manifest_totals", applied.SignedAmountClp == 3380 && applied.RejectedCount == 1, 3380L, applied.SignedAmountClp);
                using (var c = new SqliteConnectionFactory(f.Options).OpenExistingForRecovery())
                    test.Check("reopened_records", c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1") == 5 && c.ExecuteScalar<long>("SELECT SUM(signed_amount_clp) FROM historical_transactions WHERE is_active=1") == 3380);
                test.Check("operational_invariants", before == NativeState(f));
            }
            // The existing UI receipt case now also proves the server write gate.
            // Keep the canonical 8 cases/52 assertions and its original time budget.
            test.Check("durable_receipt", durable && await CloudWriterDenialsAsync(data, test) && await ViewModelReceiptAsync(data, test));
        }

        private static async Task<bool> CloudWriterDenialsAsync(string data, HistoricalSalesQaCase test)
        {
            foreach (var reason in new[] { "frozen", "web", "offline", "wrong_operation", "expired", "v1_only" })
            {
                using var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data);
                var plan = await f.PlanAsync(); var before = f.State(); var workflow = f.CreateWorkflow(reason); var denied = false;
                try { await workflow.ApplyHistoricalSalesAsync(plan, f.Operator, CancellationToken.None); }
                catch (InvalidOperationException) { denied = true; }
                var expectedRequests = reason == "wrong_operation" || reason == "expired" ? 1 : 0;
                var passed = denied && f.LocalWriteRequests == expectedRequests && f.State() == before && !workflow.IsHistoricalMaintenanceActive;
                test.PrivateDiagnostic("synthetic_cloud_write_denial;case=" + reason + ";passed=" + passed);
                if (!passed) return false;
            }
            return true;
        }

        private static async Task<bool> ViewModelReceiptAsync(string data, HistoricalSalesQaCase test)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var workflow = f.CreateWorkflow();
                using (var model = new HistoricalSalesImportViewModel(workflow, f.Operator))
                {
                    var owner = new Window { Width = 1000, Height = 760, ShowInTaskbar = false };
                    owner.Show();
                    var dialog = new HistoricalSalesImportDialog(model) { Owner = owner };
                    DispatcherTimer confirm = null;
                    var confirmationDeniedReentry = false;
                    EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> failure = (_, e) =>
                    {
                        if (e.Exception is NullReferenceException)
                            test.PrivateDiagnostic("synthetic_vm_firstchance;exception=" + e.Exception);
                    };
                    AppDomain.CurrentDomain.FirstChanceException += failure;
                    try
                    {
                        dialog.Show();
                        model.LegacyClpConfirmed = true; model.TimeChoice = 1; model.TemporalEvidence = "synthetic-explicit-time";
                        await model.AnalyzeFilesForTestingAsync(SourceFiles());
                        foreach (var merchant in model.Merchants)
                        {
                            model.AddGroupCommand.Execute(null);
                            merchant.Group = model.Groups.Last();
                        }
                        model.NativeStart = new DateTime(2040, 1, 1); model.MerchantEvidence = "synthetic-merchant"; model.OverlapEvidence = "synthetic-overlap";
                        // Reuse one real dialog/analysis: reject a stale review,
                        // then re-review and commit once with reentry denied.
                        for (var stage = 0; stage < 2; stage++)
                        {
                            var changeReview = stage == 0;
                            model.PreviewCommand.Execute(null);
                            while (model.IsBusy) { deadline.Token.ThrowIfCancellationRequested(); await Dispatcher.Yield(DispatcherPriority.Background); }
                            if (!model.ApplyCommand.CanExecute(null)) return false;
                            confirm = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Send, (_, __) =>
                            {
                                if (deadline.IsCancellationRequested)
                                {
                                    confirm.Stop();
                                    foreach (var pending in Application.Current.Windows.OfType<Win7POS.Wpf.Import.ApplyConfirmDialog>().Where(w => w.Owner == dialog).ToArray()) pending.Close();
                                    return;
                                }
                                var popups = Application.Current.Windows.OfType<Win7POS.Wpf.Import.ApplyConfirmDialog>().Where(w => w.IsVisible && w.Owner == dialog).ToArray();
                                if (popups.Length == 0) return;
                                confirm.Stop(); var popup = popups.Single();
                                if (changeReview) model.OverlapEvidence = "synthetic-review-changed-during-confirmation";
                                else
                                {
                                    confirmationDeniedReentry = !model.ApplyCommand.CanExecute(null);
                                    if (!confirmationDeniedReentry) { popup.Close(); return; }
                                    model.ApplyCommand.Execute(null);
                                }
                                Descendants<Button>(popup).Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }, Dispatcher.CurrentDispatcher);
                            confirm.Start(); model.ApplyCommand.Execute(null);
                            while (model.IsBusy) { deadline.Token.ThrowIfCancellationRequested(); await Dispatcher.Yield(DispatcherPriority.Background); }
                            test.PrivateDiagnostic("synthetic_vm_apply;changedReview=" + changeReview + ";denied=" + confirmationDeniedReentry + ";status=" + model.Status);
                            if (changeReview && (f.Count("historical_import_operation") != 0 || f.Count("historical_transactions") != 0 ||
                                model.ApplyCommand.CanExecute(null) || model.Status != Win7POS.Wpf.Localization.PosLocalization.T("history.reviewStale"))) return false;
                        }
                        var contentProofSaved=await Task.Run(()=>
                        {
                            using var c=f.Factory.Open();
                            return c.ExecuteScalar<long>("SELECT count(*) FROM historical_publication_outbox WHERE proof_json IS NOT NULL AND grant_id IS NOT NULL AND writer_epoch=1 AND length(content_digest)=64")==1;
                        });
                        return contentProofSaved && confirmationDeniedReentry && f.LocalWriteRequests == 1 && model.Batches.Count == 1 && model.Batches[0].Rows == 5 &&
                            model.Status.Contains(model.Batches[0].ApplyOperationId) && f.Count("historical_import_operation") == 1;
                    }
                    finally
                    {
                        confirm?.Stop(); AppDomain.CurrentDomain.FirstChanceException -= failure;
                        foreach (var popup in Application.Current.Windows.OfType<Win7POS.Wpf.Import.ApplyConfirmDialog>().Where(w => w.Owner == dialog).ToArray()) popup.Close();
                        dialog.Close();
                        // Own the automatic read triggered by publication through
                        // cancellation/close before retiring this fixture's DB.
                        var cleanup=System.Diagnostics.Stopwatch.StartNew();
                        while(model.IsBusy&&cleanup.Elapsed<TimeSpan.FromSeconds(2))await Dispatcher.Yield(DispatcherPriority.Background);
                        if(dialog.IsVisible&&!model.IsBusy)dialog.Close();
                        if(model.IsBusy||dialog.IsVisible)throw new InvalidOperationException("qa_history_receipt_cleanup_not_drained");
                        owner.Close(); workflow.BackupAutomation.Dispose();
                    }
                }
            }
        }

        private static async Task DedupAsync(string data, HistoricalSalesQaCase test)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var analysis = Analyze(SourceFiles()); await f.ApplyAsync(await f.PreparePlanAsync(analysis));
                var duplicate = await f.ApplyAsync(await f.PreparePlanAsync(analysis));
                test.Check("unchanged_zero_new", duplicate.InsertedCount == 0 && duplicate.DuplicateCount == 5);
                var renamed = SourceFiles().Select((source, i) => { var path = Path.Combine(f.Root, "renamed-" + i + ".xlsx"); File.Copy(source, path); return path; }).Reverse().ToArray();
                var renamedResult = await f.ApplyAsync(await f.PreparePlanAsync(Analyze(renamed)));
                var two = Path.Combine(f.Root, "two.xlsx"); var reverse = Path.Combine(f.Root, "reverse.xlsx");
                var rows = new[] { HistoricalSalesFixtureWriter.LegacyRow("distinct-1", 2500), HistoricalSalesFixtureWriter.LegacyRow("distinct-2", 2500) };
                HistoricalSalesFixtureWriter.Write(two, HistoricalSalesFixtureWriter.LegacyHeaders, rows);
                var distinct = await f.ApplyAsync(await f.PreparePlanAsync(Analyze(two)));
                HistoricalSalesFixtureWriter.Write(reverse, HistoricalSalesFixtureWriter.LegacyHeaders, rows.Reverse(), true);
                var reordered = await f.ApplyAsync(await f.PreparePlanAsync(Analyze(reverse)));
                test.Check("renamed_reordered_overlap_zero", renamedResult.InsertedCount == 0 && renamedResult.DuplicateCount == 5 && reordered.InsertedCount == 0 && reordered.DuplicateCount == 2);
                test.Check("distinct_ids_kept", distinct.InsertedCount == 2);
                var conflict = Path.Combine(f.Root, "conflict.xlsx"); HistoricalSalesFixtureWriter.Write(conflict, HistoricalSalesFixtureWriter.LegacyHeaders, new[] { HistoricalSalesFixtureWriter.LegacyRow("distinct-1", 2499) });
                var before = f.State(); var denied = false; try { await f.ApplyAsync(await f.PreparePlanAsync(Analyze(conflict))); } catch (InvalidOperationException) { denied = true; }
                test.Check("conflict_blocked", denied && before == f.State());
            }
        }
        private static async Task StaleAsync(string data, HistoricalSalesQaCase test)
        {
            foreach (var id in new[] { "permission_revoked", "operator_changed", "shop_changed", "epoch_changed", "precommit_revocation" })
                using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
                {
                    var plan = await f.PlanAsync(); var before = f.State();
                    using (var use = await f.Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply, plan.OperationId, plan.RequestFingerprint, plan.DatasetId, plan.RestoreEpoch, () => f.Factory.AccessContext.DemandPath(f.Factory.DbPath), CancellationToken.None))
                    {
                        if (id == "permission_revoked") f.Operator.CurrentUser.PermissionCodes = Array.Empty<string>();
                        if (id == "operator_changed") f.Operator.SetUserForTesting(null);
                        if (id == "shop_changed") f.ChangeShopForTesting();
                        if (id == "epoch_changed") f.Factory.AccessContext.SetRestoreEpoch(f.Factory.AuthorityStamp.RestoreEpoch + 1);
                        if (id == "precommit_revocation") f.Hooks.AtPhase = (phase, _, __) => { if (phase == "before_commit") PosOnlineSyncRevocationLatch.Revoke(f.Generation); };
                        var denied = false; try { await f.Service.ApplyAsync(plan, use.Guard, f.Backup(), CancellationToken.None); } catch (InvalidOperationException) { denied = true; }
                        test.Check(id, denied && before == f.State() && !f.Operator.CurrentUserIsAdmin);
                    }
                }
        }
        private static async Task AtomicAsync(string data, HistoricalSalesQaCase test)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            using (var cancel = new CancellationTokenSource())
            {
                var plan = await f.PlanAsync(); var before = f.State(); f.Hooks.AtPhase = (phase, _, __) => { if (phase == "before_insert") cancel.Cancel(); };
                var denied = false;
                using (var use = await f.Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply, plan.OperationId, plan.RequestFingerprint, plan.DatasetId, plan.RestoreEpoch, () => f.Factory.AccessContext.DemandPath(f.Factory.DbPath), cancel.Token))
                    try { await f.Service.ApplyAsync(plan, use.Guard, f.Backup(), cancel.Token); } catch (OperationCanceledException) { denied = true; }
                test.Check("cancel_rollback", denied && before == f.State());
            }
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var plan = await f.PlanAsync(); var before = f.State(); f.Hooks.AtPhase = (phase, _, __) => { if (phase == "before_commit") throw new IOException("qa_expected_precommit_fault"); };
                var denied = false; try { await f.ApplyAsync(plan); } catch (IOException) { denied = true; }
                test.Check("precommit_fault_rollback", denied && before == f.State());
                f.Hooks.AtPhase = null; var directory = Path.Combine(f.Root, "unusable-destination"); Directory.CreateDirectory(directory); denied = false;
                using (var use = await f.Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply, plan.OperationId, plan.RequestFingerprint, plan.DatasetId, plan.RestoreEpoch, () => f.Factory.AccessContext.DemandPath(f.Factory.DbPath), CancellationToken.None))
                    try { await f.Service.ApplyAsync(plan, use.Guard, directory, CancellationToken.None); } catch (IOException) { denied = true; } catch (InvalidOperationException) { denied = true; }
                test.Check("disk_fault_safe", denied && before == f.State());
            }
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var plan = await f.PlanAsync(); var lost = false; f.Hooks.AtPhase = (phase, _, __) => { if (phase == "after_commit") throw new IOException("qa_expected_lost_response"); };
                try { await f.ApplyAsync(plan); } catch (IOException) { lost = true; }
                f.Hooks.AtPhase = null; File.Delete(plan.Analysis.Files[0].Path);
                var replay = await f.ApplyAsync(plan);
                test.Check("lost_response_reconciled", lost && replay.IsReplay && replay.InsertedCount == 1 && f.Count("historical_transactions") == 1);
            }
        }
        private static async Task UndoAsync(string data, HistoricalSalesQaCase test)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var native = NativeState(f); var ids = new List<long>(); var duplicatesSafe = true; var ownerSafe = true;
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    var owner = await f.ApplyAsync(await f.PlanAsync());
                    using (var c = f.Factory.Open()) ids.Add(c.ExecuteScalar<long>("SELECT id FROM historical_transactions WHERE is_active=1"));
                    var observer = await f.ApplyAsync(await f.PlanAsync()); await f.UndoAsync(observer);
                    using (var c = f.Factory.Open()) duplicatesSafe &= c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1") == 1;
                    await f.UndoAsync(owner);
                    using (var c = f.Factory.Open()) ownerSafe &= c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1") == 0;
                }
                test.Check("owner_only_undo", ownerSafe && native == NativeState(f));
                test.Check("duplicate_batch_not_owner", duplicatesSafe);
                test.Check("three_cycles", ids.Count == 3 && ids.Distinct().Count() == 3);
                test.Check("audit_retained", f.Count("historical_transactions") == 3 && f.Count("historical_import_operation") == 12 && f.Count("historical_import_batch") == 6);
            }
        }

        private static async Task ReportsAsync(string data, HistoricalSalesQaCase test, SyntheticQaVisualWriter visuals)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                await SeedReportAsync(f, true);
                var source = WriteModern(f.Root, "report-positive", new[] { HistoricalSalesFixtureWriter.ModernRow("report-9000", 9000) });
                await f.ApplyAsync(await f.PreparePlanAsync(Analyze(source)));
                var workflow = f.CreateWorkflow();
                var context = await workflow.OpenAnalyticalReportAsync(Day, Day.AddDays(1), SalesReportOrigins.All, true, null, null, CancellationToken.None);
                test.Check("signed_totals", context.Snapshot.Summary.BalanceClp == 10000 && context.Snapshot.Summary.CardClp == 10000, 10000L, context.Snapshot.Summary.BalanceClp);
                test.Check("hourly_parity", context.Snapshot.HourlyAmounts.Sum() == 10000);
                var own = await workflow.OpenAnalyticalReportAsync(Day, Day.AddDays(1), SalesReportOrigins.All, false, f.Operator.CurrentUser.Id, null, CancellationToken.None);
                var permissions = f.Operator.CurrentUser.PermissionCodes;
                var role = f.Operator.CurrentUser.RoleCode;
                var personalVmSafe = false;
                try
                {
                    f.Operator.CurrentUser.RoleCode = "qa-custom-daily-report";
                    // Remove only ViewAll. DailyCloseView and the same authority epoch remain available.
                    f.Operator.CurrentUser.PermissionCodes = permissions.Where(code => code != PermissionCodes.RegisterViewAll).ToArray();
                    using (var report = new DailyReportViewModel(workflow, permissions: new PermissionService(f.Operator), session: f.Operator))
                    {
                        report.DateText = "2000-01-02"; await report.ReloadAnalyticalAsync();
                        var daily = report.ReportNumbersVisible && report.NetAmount == 1000 && report.SalesCount == 1;
                        report.SelectedTabIndex = 1; report.HistoryFromText = "2000-01-02"; report.HistoryToText = "2000-01-02";
                        await report.ReloadAnalyticalAsync();
                        var personalCsv = await report.GetExportCsvContentAsync(ExportRequest.ForMarked(new[] { Day }));
                        personalVmSafe = daily && report.ReportNumbersVisible && report.PeriodNetAmount == 1000 && report.MarkedCount == 1 &&
                            !personalCsv.Contains("report-9000") && !personalCsv.Contains(";9000;") && !personalCsv.Contains(HistoricalSalesMixedFixture.UnknownU);
                    }
                }
                finally { f.Operator.CurrentUser.PermissionCodes = permissions; f.Operator.CurrentUser.RoleCode = role; }
                test.Check("operator_scope", own.Snapshot.Summary.BalanceClp == 1000 && own.Snapshot.Summary.HistoricalMovementCount == 0 && personalVmSafe);
                var otherFilter = new SalesReportFilter("synthetic-other-shop", Day, Day.AddDays(1), SalesReportOrigins.Historical, true, null, NativeSalesOwnershipRepository.PolicyHash, "");
                var other = await context.Query.OpenAsync(otherFilter, null, 20, CancellationToken.None);
                test.Check("tenant_scope", other.Summary.BalanceClp == 0 && other.FirstPage.Entries.Count == 0);
                var historical = context.Snapshot.FirstPage.Entries.Single(x => x.Key.Origin == SalesReportOrigin.Historical);
                var native = context.Snapshot.FirstPage.Entries.Single(x => x.Key.Origin == SalesReportOrigin.Native);
                test.Check("detail_routing", !historical.HasItemLines && !historical.CanRefund && !historical.CanReprintNativeReceipt && !historical.Key.Equals(native.Key));
                var csv = SalesReportCsvSerializer.Header + "\n" + SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Metadata(context.Snapshot)) + "\n" + string.Join("\n", context.Snapshot.FirstPage.Entries.Select(e => SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Movement(context.Snapshot.Token, context.Snapshot.Coverage, e))));
                test.Check("csv_contract", csv.Contains("signed_amount_clp") && csv.Contains("9000") && !csv.Contains(HistoricalSalesMixedFixture.UnknownU) && SalesReportCsvSerializer.Text("000123") == "\"000123\"" && SalesReportCsvSerializer.Text(" \t=1") == "\"' \t=1\"");
                test.Check("native_ticket_average", context.Snapshot.Summary.NativeAverageReceiptClp == 1000 && context.Snapshot.Summary.NativePositiveReceiptCount == 1);
                test.Check("coverage_state_no_disclosure", context.Snapshot.Coverage.Status == SalesReportCoverageStatus.VerifiedSubset && !csv.Contains("7000") && context.Snapshot.FirstPage.Entries.Count == 2);
                var destination = Path.Combine(f.Root, "public-synthetic-report.csv");
                var published = await ExportAsync(f, workflow, context, destination, false, false);
                var noOverwrite = false; try { await ExportAsync(f, workflow, context, destination, false, false); } catch (IOException) { noOverwrite = true; }
                var original = File.ReadAllBytes(destination);
                var tampered = await ExportAsync(f, workflow, context, destination, false, false, true, true);
                test.Check("export_publication_outcome", published.Outcome == SalesReportPublicationOutcome.Published && published.Token.Equals(context.Snapshot.Token) && noOverwrite && tampered.Outcome == SalesReportPublicationOutcome.FailedBeforePublication && tampered.ErrorCode == "report_publication_content_changed" && original.SequenceEqual(File.ReadAllBytes(destination)));
                await f.ApplyAsync(await f.PlanAsync(id: "invalidates-report"));
                var stale = false; try { await context.Query.ValidateAsync(context.Snapshot.Token, context.Filter, context.NativeTimePolicy, CancellationToken.None); } catch (ReportSnapshotStaleException) { stale = true; }
                test.Check("snapshot_invalidation", stale);
            }
            var beforeDenied = false; var afterPublished = false; var viewAllDenied = false;
            foreach (var revokeAfter in new[] { false, true })
                using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
                {
                    await f.ApplyAsync(await f.PlanAsync()); var service = f.CreateWorkflow();
                    var context = await service.OpenAnalyticalReportAsync(Day, Day.AddDays(1), SalesReportOrigins.Historical, true, null, null, CancellationToken.None);
                    var destination = Path.Combine(f.Root, "authority.csv");
                    try { var result = await ExportAsync(f, service, context, destination, true, revokeAfter); afterPublished |= revokeAfter && result.Outcome == SalesReportPublicationOutcome.Published && File.Exists(destination); }
                    catch (InvalidOperationException) { beforeDenied |= !revokeAfter && !File.Exists(destination); }
                }
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                await f.ApplyAsync(await f.PlanAsync()); var service = f.CreateWorkflow();
                var context = await service.OpenAnalyticalReportAsync(Day, Day.AddDays(1), SalesReportOrigins.Historical, true, null, null, CancellationToken.None);
                var destination = Path.Combine(f.Root, "view-all-revoked.csv");
                PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var beforeEpoch);
                try { await ExportAsync(f, service, context, destination, false, false, revokeViewAll: true); }
                catch (InvalidOperationException)
                {
                    PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var afterEpoch);
                    viewAllDenied = !File.Exists(destination) && beforeEpoch == afterEpoch &&
                        f.Operator.CurrentUser.PermissionCodes.Contains(PermissionCodes.DailyCloseView) && !f.Operator.CurrentUser.PermissionCodes.Contains(PermissionCodes.RegisterViewAll);
                }
            }
            test.Check("export_publication_authorization", beforeDenied && afterPublished && viewAllDenied && await PassiveReadExpiryAsync(data) && await ExportMaintenanceDeniedAsync(data));
            test.Check("hourly_signed_geometry", await GeometryAsync(data, visuals));
        }
        private static Task<SalesReportExportResult> ExportAsync(HistoricalSalesFoundationRunner.Fixture f, PosWorkflowService service, SalesReportContext context, string destination, bool revoke, bool revokeAfter, bool overwrite = false, bool tamper = false, bool revokeViewAll = false, bool expireBeforePermit = false)
        {
            return new SalesReportExportService().ExportAsync(context, destination, overwrite, request =>
            {
                if (tamper) using (var stream = new FileStream(request.TemporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { var value = stream.ReadByte(); stream.Position = 0; stream.WriteByte((byte)(value ^ 1)); stream.Flush(true); }
                var issuer = f.Operator.PrepareSalesReportPublication(request.Token, context.Filter, request.TemporaryPath, request.DestinationPath, request.ContentSha256, request.ContentBytes, request.OverwriteAllowed, PermissionCodes.DailyCloseView, CancellationToken.None);
                return () =>
                {
                    if (revokeViewAll) f.Operator.CurrentUser.PermissionCodes = f.Operator.CurrentUser.PermissionCodes.Where(code => code != PermissionCodes.RegisterViewAll).ToArray();
                    if (expireBeforePermit) f.Expire();
                    if (revoke && !revokeAfter) f.Operator.SetUserForTesting(null);
                    var permit = issuer(); if (revoke && revokeAfter) f.Operator.SetUserForTesting(null); return permit;
                };
            }, ct => service.DemandAnalyticalReportCurrentAsync(context, ct), CancellationToken.None);
        }
        private static async Task<bool> PassiveReadExpiryAsync(string data)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                await f.ApplyAsync(await f.PlanAsync());
                var service = f.CreateWorkflow();
                var context = await service.OpenAnalyticalReportAsync(Day, Day.AddDays(1), SalesReportOrigins.Historical, true, null, null, CancellationToken.None);
                var authority = f.Operator.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.DailyCloseView);
                using (var report = new DailyReportViewModel(service, permissions: new PermissionService(f.Operator), session: f.Operator))
                using (var import = new HistoricalSalesImportViewModel(service, f.Operator))
                {
                    report.DateText = "2000-01-02"; report.ReportOriginIndex = 2; await report.ReloadAnalyticalAsync();
                    var source = WriteModern(f.Root, "expiry-analysis", new[] { HistoricalSalesFixtureWriter.ModernRow("expiry-input", 1000) });
                    import.LegacyClpConfirmed = true; import.TimeChoice = 1; import.TemporalEvidence = "synthetic-expiry-evidence";
                    await import.AnalyzeFilesForTestingAsync(new[] { source });
                    var loaded = report.ReportNumbersVisible && report.NetAmount == 1000 && import.ImportReadAuthorityCurrent && !string.IsNullOrEmpty(import.Summary);
                    var changes = 0; Action changed = () => changes++; f.Operator.SessionChanged += changed;
                    try
                    {
                        PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var beforeEpoch);
                        var destination = Path.Combine(f.Root, "expired-publication.csv"); var publicationDenied = false;
                        try { await ExportAsync(f, service, context, destination, false, false, expireBeforePermit: true); }
                        catch (InvalidOperationException) { publicationDenied = !File.Exists(destination); }
                        var captured = false;
                        var readAllowed = f.Operator.TryUseSalesReportReadAuthority(authority, context.Filter, () => captured = true);
                        PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var afterEpoch);
                        return loaded && publicationDenied && beforeEpoch == afterEpoch && changes == 0 && !readAllowed && !captured &&
                            !report.ReportNumbersVisible && !report.PrintSummaryCommand.CanExecute(null) && !report.PrintSelectedHistoryCommand.CanExecute(null) &&
                            !import.ImportReadAuthorityCurrent && string.IsNullOrEmpty(import.Summary) && string.IsNullOrEmpty(import.CoverageDetails);
                    }
                    finally { f.Operator.SessionChanged -= changed; }
                }
            }
        }
        private static async Task<bool> ExportMaintenanceDeniedAsync(string data)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var release = new ManualResetEventSlim())
            {
                await f.ApplyAsync(await f.PlanAsync());
                var service = f.CreateWorkflow();
                var context = await service.OpenAnalyticalReportAsync(Day, Day.AddDays(1), SalesReportOrigins.Historical, true, null, null, deadline.Token);
                await service.DemandAnalyticalReportCurrentAsync(context, deadline.Token);
                var read = f.Operator.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.DailyCloseView);
                var destination = Path.Combine(f.Root, "maintenance-publication.csv");
                var prepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<SalesReportExportResult> export = null;
                try
                {
                    export = new SalesReportExportService().ExportAsync(context, destination, false, request =>
                    {
                        var issuer = f.Operator.PrepareSalesReportPublication(request.Token, context.Filter, request.TemporaryPath, request.DestinationPath,
                            request.ContentSha256, request.ContentBytes, request.OverwriteAllowed, PermissionCodes.DailyCloseView, deadline.Token);
                        prepared.TrySetResult(true); release.Wait(deadline.Token); return issuer;
                    }, ct => service.DemandAnalyticalReportCurrentAsync(context, ct), deadline.Token);
                    if (await Task.WhenAny(prepared.Task, export) != prepared.Task) { await export; return false; }
                    using (await PosOnlineSyncRevocationLatch.EnterAuthorizationMaintenanceAsync(deadline.Token))
                    {
                        // Real CSV bytes and query validation precede maintenance;
                        // the already prepared issuer must still deny in the gap.
                        var readDenied = !f.Operator.IsSalesReportReadAuthorityCurrent(read, context.Filter);
                        release.Set();
                        try { var result = await export; return readDenied && result.Outcome == SalesReportPublicationOutcome.FailedBeforePublication && !File.Exists(destination); }
                        catch (InvalidOperationException) { return readDenied && !File.Exists(destination); }
                    }
                }
                catch (OperationCanceledException) { return false; }
                finally
                {
                    release.Set(); deadline.Cancel();
                    if (export != null) try { await export; } catch (InvalidOperationException) { } catch (OperationCanceledException) { }
                }
            }
        }
        private static async Task SeedReportAsync(HistoricalSalesFoundationRunner.Fixture f, bool unknown)
        {
            var policy = HistoricalSalesMixedFixture.CreatePolicy(); var state = await f.Service.ReadDatasetAsync(CancellationToken.None);
            using (var use = await f.Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply, Guid.NewGuid().ToString("N"), policy.Hash, state.DatasetId, f.Authority.RestoreEpoch, () => f.Factory.AccessContext.DemandPath(f.Factory.DbPath), CancellationToken.None))
                await new SalesReportTimePolicyRepository(f.Factory).SaveApprovedAsync(f.ShopId, policy, "synthetic-policy-evidence", use.Guard, CancellationToken.None);
            await HistoricalSalesMixedFixture.SeedAsync(f, 1, 0, CancellationToken.None);
            if (unknown) await HistoricalSalesMixedFixture.SeedUnattributedAsync(f, 7000, CancellationToken.None);
        }
        private static async Task<bool> GeometryAsync(string data, SyntheticQaVisualWriter visuals)
        {
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var source = WriteModern(f.Root, "signed", new[] { HistoricalSalesFixtureWriter.ModernRow("positive", 1000), HistoricalSalesFixtureWriter.ModernRow("negative", -700, 3600), HistoricalSalesFixtureWriter.ModernRow("negative-next", -700, 86400) });
                await f.ApplyAsync(await f.PreparePlanAsync(Analyze(source)));
                var service = f.CreateWorkflow();
                using (var vm = new DailyReportViewModel(service, permissions: new PermissionService(f.Operator), session: f.Operator))
                {
                    vm.DateText = "2000-01-02"; vm.ReportOriginIndex = 2;
                    var view = new DailyReportView { DataContext = vm }; var window = new Window { Content = view, Width = 1024, Height = 768, ShowInTaskbar = false };
                    try
                    {
                        window.Show(); await vm.ReloadAnalyticalAsync(); await PumpAsync(); window.UpdateLayout();
                        var plot = (FrameworkElement)view.FindName("HourlyPlot"); var line = (FrameworkElement)view.FindName("HourlyZeroLine");
                        var negatives = Descendants<Border>(view).Where(x => x.Name == "NegativeBar" && x.ActualHeight > 0).ToArray();
                        var positives = Descendants<Border>(view).Where(x => x.Name == "PositiveBar" && x.ActualHeight > 0).ToArray();
                        var mixed = plot.ActualHeight > 0 && line.ActualHeight > 0 && negatives.Length == 1 && positives.Length == 1 && negatives[0].TransformToAncestor(plot).Transform(new Point()).Y >= line.TransformToAncestor(plot).Transform(new Point()).Y;
                        if (mixed) visuals.Capture("hourly_mixed", plot, f);
                        vm.DateText = "2000-01-03"; await vm.ReloadAnalyticalAsync(); await PumpAsync(); window.UpdateLayout();
                        if (vm.NetAmount == -700) visuals.Capture("hourly_negative", plot, f);
                        return mixed && vm.NetAmount == -700 && Descendants<Border>(view).Any(x => x.Name == "NegativeBar" && x.ActualHeight > 0) && !Descendants<Border>(view).Any(x => x.Name == "PositiveBar" && x.ActualHeight > 0);
                    }
                    finally { window.Close(); view.DataContext = null; }
                }
            }
        }
        private static async Task UiAsync(string data, HistoricalSalesQaCase test, SyntheticQaVisualWriter visuals)
        {
            var onboardingVerified = await VerifyEmptyOnboardingAsync(data, test);
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var plan = await f.PlanAsync(); var applied = await f.ApplyAsync(plan); var service = f.CreateWorkflow();
                var model = new HistoricalSalesImportViewModel(service, f.Operator); var owner = new Window { Width = 1024, Height = 768, ShowInTaskbar = false };
                var oldLanguage = Win7POS.Wpf.Localization.PosLocalization.Current.CurrentLanguage;
                var oldCulture = Thread.CurrentThread.CurrentCulture;
                Win7POS.Wpf.Localization.PosLocalization.Current.SetLanguage("en"); Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                owner.Show(); var dialog = new HistoricalSalesImportDialog(model) { Owner = owner ?? DialogOwnerHelper.GetSafeOwner() };
                try
                {
                    dialog.Show(); await PumpAsync(); dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); await PumpAsync();
                    test.Check("keyboard_focus", await VerifyHistoricalKeyboardAsync(service, f.Operator) && onboardingVerified);
                    dialog.Activate(); await PumpAsync();
                    test.Check("dialog_standard", dialog is DialogShellWindow && dialog.WindowStartupLocation == WindowStartupLocation.Manual && Math.Abs(dialog.MinWidth - Math.Min(720, dialog.MaxWidth)) < 0.1 && dialog.Owner == owner && Descendants<Button>(dialog).Any(button => ReferenceEquals(button.Style, dialog.FindResource("DialogActionButtonStyle"))));
                    test.Check("nonblocking_dispatcher", await VerifyAnalysisHeartbeatAsync(f, service));
                    var summarySource = WriteModern(f.Root, "visual-summary", new[] { HistoricalSalesFixtureWriter.ModernRow("synthetic-visual-only", 1000) });
                    model.LegacyClpConfirmed = true; model.TimeChoice = 1; model.TemporalEvidence = "synthetic-explicit-UTC-evidence";
                    await model.AnalyzeFilesForTestingAsync(new[] { summarySource }); await PumpAsync(); dialog.UpdateLayout();
                    var summaryRegion = Descendants<TextBlock>(dialog).FirstOrDefault(item => item.Text == model.Summary && !string.IsNullOrEmpty(model.Summary));
                    summaryRegion?.BringIntoView(); await PumpAsync(); dialog.UpdateLayout();
                    visuals.Capture("import_summary", summaryRegion, f);
                    using (var report = new DailyReportViewModel(service, permissions: new PermissionService(f.Operator), session: f.Operator))
                    {
                        report.DateText = "2000-01-02"; report.ReportOriginIndex = 2; await report.ReloadAnalyticalAsync();
                        var loaded = report.ReportNumbersVisible && report.NetAmount == 1000;
                        test.PrivateDiagnostic("report_loaded;loaded=" + loaded + ";visible=" + report.ReportNumbersVisible + ";net=" + report.NetAmount + ";busy=" + report.IsBusy + ";previewEmpty=" + string.IsNullOrEmpty(report.SummaryReceiptPreview) + ";status=" + report.Status);
                        await model.RefreshBatchesForTestingAsync();
                        var analysisLoaded = !string.IsNullOrEmpty(model.Summary) && !string.IsNullOrEmpty(model.CoverageDetails) && model.Batches.Count > 0;
                        test.PrivateDiagnostic("analysis_loaded;analysisLoaded=" + analysisLoaded + ";summaryEmpty=" + string.IsNullOrEmpty(model.Summary) + ";coverageEmpty=" + string.IsNullOrEmpty(model.CoverageDetails) + ";batches=" + model.Batches.Count + ";authority=" + model.ImportReadAuthorityCurrent + ";busy=" + model.IsBusy + ";status=" + model.Status);
                        var sessionChanges = 0; Action countChanges = () => sessionChanges++; f.Operator.SessionChanged += countChanges;
                        var epochBefore = PosOnlineSyncRevocationLatch.ReadAuthorizationStamp().Epoch;
                        PosOnlineSyncRevocationLatch.InvalidateAuthorizationState();
                        var deniedImmediately = PosOnlineSyncRevocationLatch.ReadAuthorizationStamp().Epoch > epochBefore && sessionChanges == 0 &&
                            !report.ReportNumbersVisible && !report.PrintSelectedHistoryCommand.CanExecute(null) && !model.ImportReadAuthorityCurrent;
                        test.PrivateDiagnostic("epoch_immediate;before=" + epochBefore + ";after=" + PosOnlineSyncRevocationLatch.ReadAuthorizationStamp().Epoch + ";denied=" + deniedImmediately + ";visible=" + report.ReportNumbersVisible + ";canPrint=" + report.PrintSelectedHistoryCommand.CanExecute(null) + ";importAuthority=" + model.ImportReadAuthorityCurrent + ";sessionChanges=" + sessionChanges);
                        await PumpAsync();
                        var epochCleared = deniedImmediately && report.NetAmount == 0 && report.HistoryRows.Count == 0 &&
                            report.HourlySalesPoints.Count == 0 && string.IsNullOrEmpty(report.SummaryReceiptPreview) &&
                            string.IsNullOrEmpty(model.Summary) && string.IsNullOrEmpty(model.CoverageDetails) && model.Batches.Count == 0;
                        test.PrivateDiagnostic("epoch_cleared;epochCleared=" + epochCleared + ";net=" + report.NetAmount + ";rows=" + report.HistoryRows.Count + ";points=" + report.HourlySalesPoints.Count + ";previewEmpty=" + string.IsNullOrEmpty(report.SummaryReceiptPreview) + ";summaryEmpty=" + string.IsNullOrEmpty(model.Summary) + ";coverageEmpty=" + string.IsNullOrEmpty(model.CoverageDetails) + ";batches=" + model.Batches.Count + ";sessionChanges=" + sessionChanges);
                        // Epoch-only rejection is proved above before any SessionChanged.
                        // Command binding revalidation may then legitimately force logout;
                        // do not reuse that invalid login for the pending-reader proof.
                        f.Operator.SessionChanged -= countChanges;
                        var lateIgnored = await HistoricalSalesLateReportProbe.RunAsync(data, test);
                        f.Operator.Logout(); await PumpAsync();
                        test.PrivateDiagnostic("logout_final;loaded=" + loaded + ";analysisLoaded=" + analysisLoaded + ";epochCleared=" + epochCleared + ";lateIgnored=" + lateIgnored + ";visible=" + report.ReportNumbersVisible + ";net=" + report.NetAmount);
                        test.Check("stale_result_ignored", loaded && analysisLoaded && epochCleared && lateIgnored && !report.ReportNumbersVisible && report.NetAmount == 0);
                    }
                    using (var c = new SqliteConnectionFactory(f.Options).OpenExistingForRecovery())
                        test.Check("reopened_receipt", c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation WHERE operation_id=@op", new { op = applied.OperationId }) == 1 && c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1") == 1);
                }
                finally { dialog.Close(); owner.Close(); model.Dispose(); model.Dispose(); Thread.CurrentThread.CurrentCulture = oldCulture; Win7POS.Wpf.Localization.PosLocalization.Current.SetLanguage(oldLanguage); }
            }
        }
        private static async Task<bool> VerifyEmptyOnboardingAsync(string data, HistoricalSalesQaCase test)
        {
            test.PrivateDiagnostic("history_onboarding;subcase=real_bootstrap_loopback;state=starting");
            if (!await ArticleMutationLoopbackHarness.VerifyEmptyShopBootstrapAsync(data, test)) return false;
            test.PrivateDiagnostic("history_onboarding;subcase=no_proof_and_empty;state=starting");
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(f.Generation, 1);
                var noProofDenied = false;
                try { await HistoricalOnboardingAccess.CreateAsync(f.Factory, f.Operator, f.Generation, CancellationToken.None); }
                catch (InvalidOperationException) { noProofDenied = true; }
                if (!noProofDenied || !await ConfirmEmptyForOnboardingAsync(f)) return false;
                var access = await HistoricalOnboardingAccess.CreateAsync(f.Factory, f.Operator, f.Generation, CancellationToken.None);
                var outcome = PosCatalogPullOutcome.AuthoritativelyEmpty(1);
                var bootstrap = PosOnlineBootstrapResult.CatalogIncomplete("catalog_authoritatively_empty", "synthetic", false, false, outcome);
                if (!bootstrap.CanPrepareEmptyShopHistory || bootstrap.CanOpenPos || bootstrap.CatalogSaleSafe ||
                    !bootstrap.CatalogCompleted || !bootstrap.FirstLoginSucceeded || !bootstrap.TrustedSessionPersisted ||
                    PosOnlineBootstrapResult.CatalogIncomplete("partial", "synthetic", false, true,
                        PosCatalogPullOutcome.Failure("partial", false, true, 1)).CanPrepareEmptyShopHistory ||
                    PosOnlineBootstrapResult.CatalogIncomplete("catalog_authoritatively_empty", "synthetic", true, false, outcome).CanPrepareEmptyShopHistory ||
                    PosOnlineBootstrapResult.Failure("catalog_authoritatively_empty", "synthetic", false,
                        firstLoginSucceeded: false, trustedSessionPersisted: false, catalogStarted: true).CanPrepareEmptyShopHistory ||
                    await new CatalogShopStateRepository(f.Factory).IsSaleSafeForOfficialShopAsync()) return false;
                test.PrivateDiagnostic("history_onboarding;subcase=dialog_keyboard_and_no_print;state=starting");
                var service = f.CreateWorkflow();
                // Keep this viewport fully on screen. The standard modal host
                // intentionally uses the monitor work area if a cascaded owner
                // extends outside it; that fallback is not a content-size failure.
                var owner = new Window { Width = 1024, Height = 768, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen };
                var dialog = new HistoricalOnboardingDialog(access, service);
                try
                {
                    owner.Show(); dialog.Owner = owner ?? DialogOwnerHelper.GetSafeOwner();
                    dialog.Show(); await PumpAsync(); dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); await PumpAsync();
                    var buttons = Descendants<Button>(dialog).Where(button => button.IsVisible && button.IsEnabled).ToArray();
                    test.PrivateDiagnostic("history_onboarding;subcase=dialog_state;buttons=" + buttons.Length +
                        ";focus=" + buttons.Any(button => button.IsKeyboardFocusWithin) +
                        ";width=" + dialog.ActualWidth.ToString(CultureInfo.InvariantCulture) +
                        ";height=" + dialog.ActualHeight.ToString(CultureInfo.InvariantCulture));
                    if (!buttons.Any(button => button.IsKeyboardFocusWithin) ||
                        !((Button)dialog.FindName("ImportButton")).IsEnabled || !((Button)dialog.FindName("ReportButton")).IsEnabled ||
                        dialog.WindowStartupLocation != WindowStartupLocation.Manual || dialog.Owner != owner ||
                        !OperatorDialogGeometrySmoke.SurfaceFitsWorkArea(dialog)) return false;
                    // The standalone service/view model can import and export, but its
                    // report command cannot send even a synthetic job to a printer.
                    using (var report = new DailyReportViewModel(service, permissions: new PermissionService(f.Operator),
                        session: f.Operator, allowPrinting: false))
                    {
                        if (report.PrintSummaryCommand.CanExecute(null) || report.PrintSelectedHistoryCommand.CanExecute(null)) return false;
                    }
                    f.Expire();
                    test.PrivateDiagnostic("history_onboarding;subcase=lease_expiry;state=starting");
                    var expiredDenied = false;
                    try { await access.DemandAsync(CancellationToken.None); } catch (InvalidOperationException) { expiredDenied = true; }
                    await Task.Delay(550); await PumpAsync();
                    if (!expiredDenied || access.IsCurrentInMemory || buttons.Take(2).Any(button => button.IsEnabled)) return false;
                }
                finally { dialog.Close(); owner.Close(); }
            }
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                test.PrivateDiagnostic("history_onboarding;subcase=permission_denied;state=starting");
                await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(f.Generation, 1);
                if (!await ConfirmEmptyForOnboardingAsync(f)) return false;
                f.Operator.CurrentUser.PermissionCodes = Array.Empty<string>();
                var denied = false;
                try { await HistoricalOnboardingAccess.CreateAsync(f.Factory, f.Operator, f.Generation, CancellationToken.None); }
                catch (InvalidOperationException) { denied = true; }
                if (!denied) return false;
            }
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                test.PrivateDiagnostic("history_onboarding;subcase=session_A_proof_B_generation;state=starting");
                var a = f.Generation;
                f.Generation = new OnlineSyncGeneration(OnlineSyncGeneration.CreateGenerationId(), a.PosSessionId,
                    a.ShopDeviceId, a.ShopId, a.ShopCode, a.StaffId, a.StaffCredentialVersion);
                await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(f.Generation, 1);
                if (!await ConfirmEmptyForOnboardingAsync(f)) return false;
                // Both the lease A and DB proof B are valid independently. The
                // capability must bind their full fingerprint, including generation.
                var denied = false;
                try { await HistoricalOnboardingAccess.CreateAsync(f.Factory, f.Operator, f.Generation, CancellationToken.None); }
                catch (InvalidOperationException) { denied = true; }
                if (!denied) return false;
            }
            using (var f = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                test.PrivateDiagnostic("history_onboarding;subcase=scope_and_revocation;state=starting");
                await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(f.Generation, 1);
                if (!await ConfirmEmptyForOnboardingAsync(f)) return false;
                var wrong = new OnlineSyncGeneration(f.Generation.GenerationId, f.Generation.PosSessionId, f.Generation.ShopDeviceId,
                    "synthetic-other-shop", "OTHER", f.Generation.StaffId, f.Generation.StaffCredentialVersion);
                var scopeDenied = false;
                try { await HistoricalOnboardingAccess.CreateAsync(f.Factory, f.Operator, wrong, CancellationToken.None); }
                catch (InvalidOperationException) { scopeDenied = true; }
                var access = await HistoricalOnboardingAccess.CreateAsync(f.Factory, f.Operator, f.Generation, CancellationToken.None);
                PosOnlineSyncRevocationLatch.Revoke(f.Generation);
                var revokedDenied = false;
                try { await access.DemandAsync(CancellationToken.None); } catch (InvalidOperationException) { revokedDenied = true; }
                var passed = scopeDenied && revokedDenied && !access.IsCurrentInMemory;
                test.PrivateDiagnostic("history_onboarding;subcase=completed;passed=" + passed);
                return passed;
            }
        }

        private static async Task<bool> ConfirmEmptyForOnboardingAsync(HistoricalSalesFoundationRunner.Fixture f)
        {
            var generation = f.Generation;
            var state = new CatalogShopStateRepository(f.Factory);
            var binding = await state.EnsureAndLoadCursorAsync(generation.ShopId, generation.ShopCode, generation);
            var fence = new RemoteCatalogCommitFence
            {
                ShopId = generation.ShopId, ShopCode = generation.ShopCode, GenerationId = generation.GenerationId,
                GenerationFingerprint = generation.Fingerprint, PosSessionId = generation.PosSessionId,
                ShopDeviceId = generation.ShopDeviceId, ExpectedEpoch = binding.Epoch,
                ExpectedPreviousCursor = binding.Cursor, ExpectedPreviousMode = binding.Mode
            };
            var run = Guid.NewGuid().ToString("N");
            await new RemoteCatalogBatchRepository(f.Factory).StageAuthoritativePageAsync(new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = true,
                AuthoritativeStagePage = new CatalogAuthoritativeStagePage { FullRunId = run, PageNumber = 1, HasMore = false }
            }, CancellationToken.None, fence);
            var code = await new CatalogFullRefreshReconciler(f.Factory).ConfirmAuthoritativeEmptyAsync(run,
                new PosCatalogSummaryResponse { Products = 0, ActiveProducts = 0, Categories = 0, Suppliers = 0, Prices = 0 },
                new CatalogExactnessRunContext { CatalogVersion = "synthetic-empty-v1", SyncCursor = "synthetic-empty-cursor",
                    SyncMode = "full_refresh", Pages = 1, HasMore = false, ProductRowsReceived = 0,
                    CategoryRowsReceived = 0, SupplierRowsReceived = 0, PriceRowsReceived = 0, PriceRowsAccepted = 0 }, fence);
            return code.Length == 0 && await state.IsAuthoritativelyEmptyAsync(generation);
        }

        private static async Task<bool> VerifyHistoricalKeyboardAsync(PosWorkflowService service, OperatorSession session)
        {
            var ownerInput = new TextBox { Text = "Synthetic keyboard owner", Margin = new Thickness(20) };
            var owner = new Window { Content = ownerInput, Width = 800, Height = 600, ShowInTaskbar = false };
            var model = new HistoricalSalesImportViewModel(service, session);
            var dialog = new HistoricalSalesImportDialog(model);
            try
            {
                owner.Show(); owner.Activate(); ownerInput.Focus(); await PumpAsync();
                var ownerHadFocus = ownerInput.IsKeyboardFocused && ownerInput.IsVisible;
                dialog.Owner = owner ?? DialogOwnerHelper.GetSafeOwner();
                var scripted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (deadline.Token.Register(() => dialog.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    scripted.TrySetResult(false); if (dialog.IsVisible) dialog.Close();
                }))))
                {
                    _ = dialog.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
                    {
                        try
                        {
                            await PumpAsync(); deadline.Token.ThrowIfCancellationRequested();
                            var policy = (ComboBox)dialog.FindName("TimePolicy"); var evidence = (TextBox)dialog.FindName("TimeEvidence");
                            policy.BringIntoView(); policy.Focus(); await PumpAsync();
                            var entered = dialog.IsActive && policy.IsKeyboardFocusWithin && policy.IsVisible;
                            ProcessHistoricalKey(dialog, Key.Tab); await PumpAsync();
                            var tabbed = evidence.IsKeyboardFocused && evidence.IsVisible && !policy.IsKeyboardFocusWithin;
                            var group = Descendants<Button>(dialog).Single(button => ReferenceEquals(button.Command, model.AddGroupCommand));
                            group.BringIntoView(); group.Focus(); await PumpAsync();
                            var groupHadFocus = group.IsKeyboardFocused && group.IsVisible && group.IsEnabled;
                            var groups = model.Groups.Count; var clicks = 0;
                            RoutedEventHandler clicked = (_, __) => clicks++; group.Click += clicked;
                            try { ProcessHistoricalKey(dialog, Key.Enter); await PumpAsync(); }
                            finally { group.Click -= clicked; }
                            var enteredCommand = clicks == 1 && model.Groups.Count == groups + 1;
                            deadline.Token.ThrowIfCancellationRequested(); ProcessHistoricalKey(dialog, Key.Escape); await PumpAsync();
                            scripted.TrySetResult(entered && tabbed && groupHadFocus && enteredCommand && !dialog.IsVisible);
                        }
                        catch { scripted.TrySetResult(false); }
                        finally { if (dialog.IsVisible) dialog.Close(); }
                    }));
                    dialog.ShowDialog(); // The product's modal owner/input/focus path.
                    var passed = await scripted.Task; await PumpAsync();
                    return ownerHadFocus && passed && owner.IsActive && ownerInput.IsVisible && ownerInput.IsKeyboardFocused;
                }
            }
            finally { dialog.Close(); owner.Close(); model.Dispose(); }
        }
        private static void ProcessHistoricalKey(Window window, Key key)
        {
            var focused = Keyboard.FocusedElement as UIElement;
            if (focused == null || Window.GetWindow(focused) != window || !focused.IsVisible)
                throw new InvalidOperationException("qa_history_keyboard_focus_missing");
            var source = PresentationSource.FromVisual(focused);
            if (source == null) throw new InvalidOperationException("qa_history_keyboard_source_missing");
            // InputManager runs WPF keyboard navigation and the focused control's
            // real key/class handlers. No command, click or result is injected.
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                { RoutedEvent = Keyboard.KeyDownEvent });
            if (window.IsVisible)
                InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                    { RoutedEvent = Keyboard.KeyUpEvent });
        }
        private static async Task<bool> VerifyAnalysisHeartbeatAsync(HistoricalSalesFoundationRunner.Fixture fixture, PosWorkflowService service)
        {
            var path = WriteModern(fixture.Root, "dispatcher-analysis", Enumerable.Range(0, 64)
                .Select(index => HistoricalSalesFixtureWriter.ModernRow("heartbeat-" + index.ToString(CultureInfo.InvariantCulture), 1000, index)));
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var release = new ManualResetEventSlim())
            {
                var parsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<HistoricalSalesImportAnalysis> analysis = null;
                service.SetHistoricalAnalyzerFactoryForTesting(() => new HistoricalSalesImportAnalyzer(ct =>
                {
                    parsed.TrySetResult(true); release.Wait(ct);
                }));
                try
                {
                    analysis = service.AnalyzeHistoricalSalesAsync(new[] { path }, Options(), deadline.Token);
                    // The observer is inside the REAL analyzer, after parsed
                    // input and before summary/result. It cannot race fast completion.
                    var first = await Task.WhenAny(parsed.Task, analysis);
                    if (first != parsed.Task) { await analysis; return false; }
                    var heartbeatWhileAnalyzing = false;
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => heartbeatWhileAnalyzing = parsed.Task.IsCompleted && !analysis.IsCompleted && analysis.Status == TaskStatus.Running, DispatcherPriority.Send);
                    release.Set(); var result = await analysis;
                    return heartbeatWhileAnalyzing && result.CanApply && result.Summary.AcceptedCount == 64;
                }
                catch (OperationCanceledException) { return false; }
                finally
                {
                    release.Set(); deadline.Cancel(); service.SetHistoricalAnalyzerFactoryForTesting(null);
                    if (analysis != null) try { await analysis; } catch (OperationCanceledException) { }
                }
            }
        }
        private static readonly DateTime Day = new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Unspecified);
        private static string WriteModern(string root, string name, IEnumerable<string[]> rows) { var path = Path.Combine(root, name + ".xlsx"); HistoricalSalesFixtureWriter.Write(path, HistoricalSalesFixtureWriter.ModernHeaders, rows); return path; }
        private static string NativeState(HistoricalSalesFoundationRunner.Fixture f) { using (var c = f.Factory.Open()) return string.Join("|", c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM local_stock_movements")); }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; } }
        private static async Task PumpAsync()
        {
            await Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            {
                if (Application.Current != null)
                    foreach (var window in Application.Current.Windows.Cast<Window>().ToArray()) if (window.IsVisible) window.UpdateLayout();
            }, DispatcherPriority.Render);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        private static string Arg(string[] args, string name) { var index = Array.IndexOf(args, name); if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("qa_history_argument_missing"); return args[index + 1]; }
    }
}
