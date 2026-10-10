using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Migrations;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class CatalogImportRecoveryCompletionSmoke
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static PosTrustedDeviceSession _session;
        private static OnlineSyncGeneration _generation;
        private static PosAdminWebOptions _options;
        private static readonly List<string> Evidence = new List<string>();
        private static int _flushedEvidenceCount;

        internal static async Task RunAsync()
        {
            var previousUrl = Environment.GetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable);
            var admin = new TcpListener(IPAddress.Loopback, 0);
            admin.Start();
            var failures = new List<string>();
            try
            {
                var url = "http://127.0.0.1:" + ((IPEndPoint)admin.LocalEndpoint).Port;
                Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, url);
                _options = new PosAdminWebOptions(new Uri(url));
                var store = new PosTrustedDeviceStore();
                store.SaveFirstLogin(AuthorizationLeaseWpfSmoke.BuildResponse(true), "qa-recovery-ui-" + Guid.NewGuid().ToString("N"));
                Require(store.TryRead(out _session) && PosOnlineSyncSupervisorHost.TryCreateGeneration(_session, out _generation), "trusted generation fixture unavailable");
                Evidence.Add("scope=local_net48_x86_real_sync_center_recovery_dialog; base_products_per_batch=3; invalid_rows=1; nochange_rows=2; supported_local_plan_rows=5000; authoritative_large_admin_test=separate_interop_corpus; accepted_child=loopback_receipt_retirement_race; live_admin=False; physical_hardware=False");
                Evidence.Add("machine=" + Environment.MachineName + "; clr_reported_os_compatibility=" + Environment.OSVersion + "; clr=" + Environment.Version + "; pointer_bytes=" + IntPtr.Size);
                await CheckAsync(failures, "sync_center_correct_verify_double_commit_backup", CheckFlowAsync);
                await CheckAsync(failures, "failure_draft_rollback_retry", CheckFailureAsync);
                await CheckAsync(failures, "draft_close_reopen_verify_apply_once", CheckDraftReopenAsync);
                await CheckAsync(failures, "draft_failed_save_close_retry_explicit_discard", CheckDraftDiscardAsync);
                await CheckAsync(failures, "cancel_pending_no_orphans", CheckCancelAsync);
                await CheckAsync(failures, "authorization_denied_and_revoked", CheckAuthorizationAsync);
                await CheckAsync(failures, "four_languages_1024x768", CheckLanguagesAsync);
                await CheckAsync(failures, "accepted_replacement_retire_race_preserves_unsent_draft", CheckAcceptedReplacementAsync);
                await CheckAsync(failures, "bounded_repeat_dispatcher_sql_memory_comparison", CheckMeasurementsAsync);
                await CheckAsync(failures, "supported_5000_local_plan_virtualized_backup_worker", () => CheckBatchAsync(5000, true));
                await CheckAsync(failures, "supported_1000_commit_backup_worker", () => CheckBatchAsync(1000, true));
                Require(!admin.Pending(), "never-sent recovery connected to Admin");
                Require(failures.Count == 0, string.Join(" | ", failures));
            }
            finally
            {
                admin.Stop();
                Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, previousUrl);
                FlushEvidence();
            }
        }

        private static async Task CheckFlowAsync()
        {
            var fixture = await Fixture.CreateAsync("flow");
            using (var host = new CenterHost(fixture))
            {
                await host.ReadyAsync();
                CheckPresentation(host.ViewModel.ImportRecoveries.Single(), fixture);
                var dialog = await host.OpenRecoveryAsync();
                try
                {
                    await ReadyAsync(dialog);
                    var grid = Named<DataGrid>(dialog, "RecoveryRows");
                    var rows = Rows(dialog);
                    Require(rows.Length == 3 && rows[0].RetailPrice == "2147483648", "recovery omitted original rows");
                    var counts = fixture.Counts();
                    Click(dialog, "PrepareButton");
                    await IdleAsync(dialog);
                    var error = Named<TextBlock>(dialog, "RecoveryStatus").Text;
                    Require(error.Contains(rows[0].Barcode) && error.Contains(PosLocalization.T("importRecovery.retail")) && Digits(error).Contains("999999999"), "verification error omitted barcode, field or limit");
                    Require(!Named<Button>(dialog, "CommitButton").IsEnabled && counts == fixture.Counts(), "invalid recovery committed data");
                    Require(Keyboard.FocusedElement is TextBox focused && focused.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "RetailPrice", "verification did not focus the invalid price editor");
                    await EditRetailAsync(dialog, "200");
                    await VerifyAsync(dialog);
                    var servicePreview = await new CatalogImportRecoveryService(fixture.Factory).BuildPreviewAsync(Draft(dialog), rows, CancellationToken.None);
                    Require(servicePreview.NoChangeRows.Count == 2, "fixture did not cover two unchanged rows");
                    await CheckQueryShapeAsync(fixture, servicePreview, 1);
                    var backupsBefore = Directory.GetFiles(AppPaths.BackupsDirectory, "before-catalog-recovery-*.db");
                    using (await new CatalogShopTransitionBarrier(fixture.Factory).EnterAsync())
                    {
                        Click(dialog, "CommitButton");
                        Require(Busy(dialog) && !grid.IsEnabled && !Named<Button>(dialog, "PrepareButton").IsEnabled && !Named<Button>(dialog, "CommitButton").IsEnabled && Named<Button>(dialog, "CancelRecoveryButton").IsEnabled,
                            "recovery did not own loading state before its await");
                        Click(dialog, "CommitButton");
                        Click(dialog, "PrepareButton");
                        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                        Require(counts == fixture.Counts(), "duplicate click mutated the pending recovery");
                    }
                    await IdleAsync(dialog);
                    Require(Named<TextBlock>(dialog, "RecoveryStatus").Text == PosLocalization.T("importRecovery.queued") && grid.IsReadOnly, "recovery did not report queued pending ACK");
                    fixture.CheckReplacement();
                    var backups = Directory.GetFiles(AppPaths.BackupsDirectory, "before-catalog-recovery-*.db").Except(backupsBefore).ToArray();
                    Require(backups.Length == 1, "double confirm made multiple backups or no verified backup");
                    var backupFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(backups[0]));
                    var validation = await new DbMaintenanceRepository(backupFactory).ValidateAsync();
                    Require(validation.IsValid, "recovery backup was not SQLite-verified");
                    using (var connection = backupFactory.Open())
                        Require(connection.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode=@barcode", new { barcode = fixture.Rows[0].Barcode }) == 2147483648L && connection.ExecuteScalar<long>("SELECT count(*) FROM catalog_import_outbox") == 1, "backup was captured after recovery effects");
                }
                finally { dialog.Close(); }
                await WaitAsync(() => host.ViewModel.ImportRecoveries.Any(item => item.Batch.ReplacementOutboxId.HasValue), "Sync Center awaiting replacement ACK");
                var waiting = host.ViewModel.ImportRecoveries.Single();
                Require(!waiting.CanPrepare && waiting.State.StartsWith(PosLocalization.T("importRecovery.awaitingAck"), StringComparison.Ordinal) &&
                    waiting.Batch.PlanCompletedParts == 0 && waiting.Batch.PlanCompletedRows == 0,
                    "original was presented as resolved before ACK");
            }
            Require(!Application.Current.Windows.OfType<CatalogImportRecoveryDialog>().Any(), "orphan recovery window after flow");
        }

        private static async Task CheckDraftReopenAsync()
        {
            var fixture = await Fixture.CreateAsync("draft-reopen");
            using (var host = new CenterHost(fixture))
            {
                await host.ReadyAsync();
                var dialog = await host.OpenRecoveryAsync();
                await ReadyAsync(dialog);
                var before = fixture.Counts();
                await EditRetailAsync(dialog, "321");
                dialog.Close();
                await WaitAsync(() => !dialog.IsVisible, "draft flush before close");
                Require(fixture.Counts() == before, "saving a draft applied economic changes");
                var reopened = await host.OpenRecoveryAsync();
                await ReadyAsync(reopened);
                try
                {
                    Require(Rows(reopened)[0].RetailPrice == "321" && Draft(reopened).HasSavedDraft,
                        "saved draft did not survive close/reopen");
                    Require(!Named<Button>(reopened, "CommitButton").IsEnabled && fixture.Counts() == before,
                        "reopened draft was automatically validated or applied");
                    Require(Named<Button>(reopened, "DiscardDraftButton").IsVisible,
                        "saved draft has no explicit discard action");
                    await EditRetailAsync(reopened, "200");
                    await VerifyAsync(reopened);
                    Click(reopened, "CommitButton");
                    Click(reopened, "CommitButton");
                    await IdleAsync(reopened);
                    fixture.CheckReplacement();
                    using (var connection = fixture.Factory.Open())
                        Require(connection.ExecuteScalar<int>("SELECT count(*) FROM catalog_import_recovery_draft") == 0,
                            "applied draft remained as unapplied work");
                    Evidence.Add("draft_close_reopen; entered=321; restored=321; automatic_apply=False; final_value=200; local_apply_count=1; authoritative_ack=False");
                }
                finally { reopened.Close(); }
            }
        }

        private static async Task CheckDraftDiscardAsync()
        {
            var fixture = await Fixture.CreateAsync("draft-discard");
            using (var host = new CenterHost(fixture))
            {
                await host.ReadyAsync();
                var dialog = await host.OpenRecoveryAsync();
                await ReadyAsync(dialog);
                var before = fixture.Counts();
                try
                {
                    using (var connection = fixture.Factory.Open())
                        connection.Execute("CREATE TRIGGER fail_draft_save BEFORE INSERT ON catalog_import_recovery_draft BEGIN SELECT RAISE(ABORT,'draft_save_fault'); END;");
                    await EditRetailAsync(dialog, "321");
                    dialog.Close();
                    await WaitAsync(() => Named<TextBlock>(dialog, "DraftStateText").Text ==
                        PosLocalization.T("importRecovery.draftSaveFailed"), "failed draft save remains reviewable");
                    Require(dialog.IsVisible && Rows(dialog)[0].RetailPrice == "321" && fixture.Counts() == before,
                        "failed draft flush closed the window, lost edits or applied data");
                    using (var connection = fixture.Factory.Open()) connection.Execute("DROP TRIGGER fail_draft_save");
                    var confirmationOperation = AcceptConfirmationAsync(dialog, "discard_draft");
                    Click(dialog, "DiscardDraftButton");
                    await confirmationOperation;
                    await IdleAsync(dialog);
                    Require(Rows(dialog)[0].RetailPrice == "2147483648" && fixture.Counts() == before,
                        "discarding the draft removed the original or applied data");
                    using (var connection = fixture.Factory.Open())
                        Require(connection.ExecuteScalar<int>("SELECT count(*) FROM catalog_import_recovery_draft") == 0 &&
                            connection.ExecuteScalar<int>("SELECT count(*) FROM catalog_import_outbox") == 1,
                            "explicit draft discard removed due operations or retained draft data");
                    fixture.CheckOriginal();
                    Evidence.Add("draft_save_failure; close_blocked=True; edited_value_retained=321; explicit_discard=True; economic_writes=0; original_outbox_preserved=True");
                }
                finally
                {
                    using (var connection = fixture.Factory.Open()) connection.Execute("DROP TRIGGER IF EXISTS fail_draft_save");
                    dialog.Close();
                }
            }
        }

        private static async Task CheckFailureAsync()
        {
            var fixture = await Fixture.CreateAsync("fault");
            using (var host = new CenterHost(fixture))
            {
                await host.ReadyAsync();
                var dialog = await host.OpenRecoveryAsync();
                try
                {
                    await ReadyAsync(dialog);
                    await EditRetailAsync(dialog, "200");
                    await VerifyAsync(dialog);
                    var before = fixture.Counts();
                    using (var connection = fixture.Factory.Open())
                        connection.Execute("CREATE TRIGGER fail_recovery_history BEFORE INSERT ON product_price_history BEGIN SELECT RAISE(ABORT,'recovery_ui_fault'); END;");
                    Click(dialog, "CommitButton");
                    await IdleAsync(dialog);
                    Require(Rows(dialog)[0].RetailPrice == "200" && Named<TextBlock>(dialog, "RecoveryStatus").Text != PosLocalization.T("importRecovery.queued") && fixture.Counts() == before, "failed recovery discarded draft, reported success or wrote partial effects");
                    fixture.CheckOriginal();
                    using (var connection = fixture.Factory.Open()) connection.Execute("DROP TRIGGER fail_recovery_history");
                    await VerifyAsync(dialog);
                    Click(dialog, "CommitButton");
                    await IdleAsync(dialog);
                    fixture.CheckReplacement();
                }
                finally { dialog.Close(); }
            }
        }

        private static async Task CheckCancelAsync()
        {
            var fixture = await Fixture.CreateAsync("cancel");
            using (var host = new CenterHost(fixture))
            {
                await host.ReadyAsync();
                var dialog = await host.OpenRecoveryAsync();
                await ReadyAsync(dialog);
                await EditRetailAsync(dialog, "200");
                await VerifyAsync(dialog);
                var before = fixture.Counts();
                using (await new CatalogShopTransitionBarrier(fixture.Factory).EnterAsync())
                {
                    Click(dialog, "CommitButton");
                    Require(Busy(dialog), "cancellation did not enter a pending recovery");
                    Click(dialog, "CancelRecoveryButton");
                    await WaitAsync(() => !dialog.IsVisible && !Busy(dialog), "cancelled recovery drain/close");
                }
                Require(fixture.Counts() == before, "cancelled pending recovery committed effects");
                fixture.CheckOriginal();
                Require(!Application.Current.Windows.OfType<CatalogImportRecoveryDialog>().Any(), "cancel left an orphan recovery window");
                var retry = await host.OpenRecoveryAsync();
                await ReadyAsync(retry);
                Require(Rows(retry).Length == 3, "cancel removed original recovery rows");
                retry.Close();
            }
        }

        private static async Task CheckAuthorizationAsync()
        {
            var fixture = await Fixture.CreateAsync("auth");
            var allowed = false;
            using (var host = new CenterHost(fixture, () => allowed))
            {
                await host.ReadyAsync();
                var before = fixture.Counts();
                host.OpenButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(!Application.Current.Windows.OfType<CatalogImportRecoveryDialog>().Any() && fixture.Counts() == before, "unauthorized recovery opened or mutated");
                allowed = true;
                var dialog = await host.OpenRecoveryAsync();
                try
                {
                    await ReadyAsync(dialog);
                    await EditRetailAsync(dialog, "200");
                    await VerifyAsync(dialog);
                    allowed = false;
                    Click(dialog, "CommitButton");
                    await IdleAsync(dialog);
                    Require(fixture.Counts() == before && Rows(dialog)[0].RetailPrice == "200" && !Named<Button>(dialog, "CommitButton").IsEnabled,
                        "permission revocation allowed recovery commit or lost draft");
                    Require(Named<TextBlock>(dialog, "RecoveryStatus").Text == PosLocalization.T("importRecovery.authentication"), "revoked permission omitted the localized authorization cause");
                }
                finally { dialog.Close(); }
            }
        }

        private static async Task CheckLanguagesAsync()
        {
            var original = PosLocalization.Current.CurrentLanguage;
            var causes = new HashSet<string>();
            try
            {
                foreach (var language in new[] { "en", "es", "it", "zh-CN" })
                {
                    RecordPhase("language_switch:" + language + ":begin");
                    PosLocalization.Current.SetLanguage(language);
                    RecordPhase("language_switch:" + language + ":complete");
                    var fixture = await Fixture.CreateAsync("lang-" + language);
                    using (var host = new CenterHost(fixture))
                    {
                        await host.ReadyAsync();
                        var presentation = host.ViewModel.ImportRecoveries.Single();
                        CheckPresentation(presentation, fixture);
                        causes.Add(presentation.Cause);
                        Capture(host.Center, "sync-center-blocked-" + language, host.OpenButton());
                        var dialog = await host.OpenRecoveryAsync();
                        try
                        {
                            await ReadyAsync(dialog);
                            Click(dialog, "PrepareButton"); await IdleAsync(dialog);
                            var error = Named<TextBlock>(dialog, "RecoveryStatus").Text;
                            Require(error.Contains(PosLocalization.T("importRecovery.retail")) && error.Contains(fixture.Rows[0].Barcode), "recovery field error not localized");
                            foreach (var name in new[] { "PrepareButton", "CommitButton", "CancelRecoveryButton" }) CheckBounds(Named<Button>(dialog, name), dialog, language);
                            Require(Named<DataGrid>(dialog, "RecoveryRows").Items.Count == 3, "translated recovery lost rows");
                            Capture(dialog, "recovery-invalid-" + language,
                                Named<Button>(dialog, "PrepareButton"), Named<Button>(dialog, "CommitButton"), Named<Button>(dialog, "CancelRecoveryButton"));
                        }
                        finally { dialog.Close(); }
                    }
                }
                Require(causes.Count == 4, "recovery cause fell back to one language");
                await CheckOverlayFallbackGeometryAsync();
            }
            finally { PosLocalization.Current.SetLanguage(original); }
        }

        private static async Task CheckOverlayFallbackGeometryAsync()
        {
            var fixture = await Fixture.CreateAsync("geometry-fallback");
            using (var host = new CenterHost(fixture, forceOwnerOutsideWorkArea: true))
            {
                await host.ReadyAsync();
                var geometry = RecordGeometry(host.Center, "causal_owner_outside_workarea");
                var workArea = MonitorHelper.GetWorkAreaForExactWindowOrPrimary(host.Owner);
                Evidence.Add("causal_workarea_fallback; oldExactSizeAssumptionWouldPass=" +
                    (Math.Abs(host.Center.ActualWidth - 1024) < 1 && Math.Abs(host.Center.ActualHeight - 768) < 1) + "; " + geometry);
                Require(!Contains(workArea, WindowBounds(host.Owner)), "QA owner did not leave the work area: " + geometry);
                Require(SameBounds(WindowBounds(host.Center), workArea) && !SameBounds(WindowBounds(host.Center), WindowBounds(host.Owner)), "overlay did not use the standard work-area fallback: " + geometry);
                Capture(host.Center, "sync-center-causal-workarea-fallback", host.OpenButton());
                var dialog = await host.OpenRecoveryAsync();
                try
                {
                    await ReadyAsync(dialog);
                    Capture(dialog, "recovery-causal-workarea-fallback",
                        Named<Button>(dialog, "PrepareButton"), Named<Button>(dialog, "CommitButton"), Named<Button>(dialog, "CancelRecoveryButton"));
                }
                finally { dialog.Close(); }
            }
        }

        private static async Task CheckAcceptedReplacementAsync()
        {
            var fixture = await Fixture.CreateAsync("accepted-race");
            using (var connection = fixture.Factory.Open())
                connection.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id", new { id = fixture.OriginalId });
            var childLookups = 0;
            var childAccepted = false;
            using (var admin = new RecoveryReceiptServer(body =>
            {
                var child = Deserialize<PosCatalogImportCorrectionReceiptRequest>(body);
                if (child.OriginalRequest?.Correction == null)
                {
                    var receipt = RootReceipt(Deserialize<PosCatalogImportReceiptRequest>(body));
                    if (childAccepted)
                        foreach (var snapshot in receipt.CurrentProductSnapshots)
                        {
                            snapshot.RetailPrice = 200;
                            snapshot.BaseRevision = "2026-10-08T00:00:00.000001Z";
                        }
                    return Serialize(receipt);
                }
                var lookup = Interlocked.Increment(ref childLookups);
                Require(lookup == 1 ? child.SchemaVersion == PosCatalogImportReceiptContract.SchemaVersion :
                    child.SchemaVersion == PosCatalogImportReceiptContract.RetirementSchemaVersion, "child lookup/retirement used the wrong wire operation");
                if (lookup == 2) childAccepted = true;
                return Serialize(ChildReceipt(child, lookup == 1 ? "not_found" : "accepted"));
            }))
            {
                var service = new CatalogImportRecoveryService(fixture.Factory, AppPaths.BackupsDirectory);
                var initial = await service.PrepareAsync(fixture.OriginalId, admin.Options, _session, _generation, CancellationToken.None);
                initial.Rows[0].RetailPrice = "200";
                var applied = await service.CommitAsync(initial, initial.Rows, () => true, _generation, CancellationToken.None);
                Require(applied.Errors == 0 && applied.CatalogImportOutboxId > 0, "accepted-root fixture did not create its initial correction");
                using (var connection = fixture.Factory.Open())
                    connection.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='revision_conflict' WHERE id=@id; UPDATE catalog_import_recovery SET dispatch_count=1 WHERE original_id=@id;", new { id = applied.CatalogImportOutboxId });
                var previousUrl = Environment.GetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable);
                Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, admin.Url);
                try
                {
                    using (var host = new CenterHost(fixture))
                    {
                        await host.ReadyAsync();
                        Require(host.ViewModel.ImportRecoveries.Single().CanPrepare, "failed correction chain had no recovery CTA");
                        var dialog = await host.OpenRecoveryAsync();
                        try
                        {
                            await ReadyAsync(dialog);
                            Require(Draft(dialog).CanRetire && !Draft(dialog).CanCommit && Named<Button>(dialog, "RetireButton").Visibility == Visibility.Visible, "not-found child did not require explicit retirement");
                            await EditRetailAsync(dialog, "300");
                            await CheckAllRecoveryButtonsAsync(dialog);
                            var before = fixture.Counts();
                            var confirmationOperation = AcceptConfirmationAsync(dialog, "retire_child");
                            Click(dialog, "RetireButton");
                            await confirmationOperation;
                            await IdleAsync(dialog);
                            Require(Draft(dialog).RequiresAcceptedReconciliation && Rows(dialog)[0].RetailPrice == "300" && Named<DataGrid>(dialog, "RecoveryRows").IsReadOnly,
                                "accepted retirement race lost the unsent draft or allowed another correction");
                            Require(Named<Button>(dialog, "CommitButton").Content.ToString() == PosLocalization.T("importRecovery.reconcileAccepted") &&
                                Named<TextBlock>(dialog, "RecoveryStatus").Text == PosLocalization.T("importRecovery.acceptedDraftRetained") && fixture.Counts() == before,
                                "accepted race displayed ordinary commit success or changed local economic state");
                            Click(dialog, "CommitButton");
                            Click(dialog, "CommitButton");
                            await IdleAsync(dialog);
                            Require(Rows(dialog)[0].RetailPrice == "300" && Named<TextBlock>(dialog, "RecoveryStatus").Text == PosLocalization.T("importRecovery.acceptedReconciled") &&
                                Named<TextBlock>(dialog, "RecoveryStatus").Text != PosLocalization.T("importRecovery.queued"), "receipt reconciliation falsely claimed to send draft300");
                            using (var connection = fixture.Factory.Open())
                            {
                                Require(connection.ExecuteScalar<long>("SELECT count(*) FROM catalog_import_outbox") == 2 &&
                                    connection.ExecuteScalar<long>("SELECT count(*) FROM product_price_history") == 7 &&
                                    connection.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode=@barcode", new { barcode = fixture.Rows[0].Barcode }) == 200 &&
                                    connection.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = fixture.OriginalId }) == "recovered" &&
                                    connection.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }) == "acked",
                                    "accepted receipt reconciliation created a batch, changed draft price or failed to close its ancestors");
                                Require(connection.ExecuteScalar<string>("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id", new { id = fixture.OriginalId }) == fixture.Original.PayloadHash,
                                    "accepted receipt reconciliation changed immutable original hash");
                            }
                            Require(childLookups == 2 && admin.Requests == 3, "accepted reconciliation emitted an additional network mutation");
                            Evidence.Add("accepted_retirement_race original_intent=200; retained_draft=300; committed_local_price=200; outbox_rows=2; new_outbox_rows=0; history_rows=7; child_receipt_calls=2; http_calls=3; duplicate_confirmation_effects=0; live_admin=False");
                        }
                        finally { dialog.Close(); }
                        await WaitAsync(() => host.ViewModel.ImportRecoveries.Any(item => item.Batch.HasSavedDraft && item.CanPrepare),
                            "reconciled ancestor exposes retained draft");
                        var resumed = await host.OpenRecoveryAsync();
                        await ReadyAsync(resumed);
                        try
                        {
                            Require(Rows(resumed)[0].RetailPrice == "300" && Draft(resumed).HasSavedDraft &&
                                !Named<Button>(resumed, "CommitButton").IsEnabled,
                                "retained draft was unreachable, lost or automatically approved after reconciliation");
                            var beforeResume = fixture.Counts();
                            await VerifyAsync(resumed);
                            Require(fixture.Counts() == beforeResume, "reviewing retained draft applied data");
                            Click(resumed, "CommitButton");
                            Click(resumed, "CommitButton");
                            await IdleAsync(resumed);
                            Require(Named<TextBlock>(resumed, "RecoveryStatus").Text == PosLocalization.T("importRecovery.queued"),
                                "retained draft could not be applied after accepted reconciliation");
                            using (var connection = fixture.Factory.Open())
                            {
                                Require(connection.ExecuteScalar<long>("SELECT count(*) FROM catalog_import_outbox") == 3 &&
                                    connection.ExecuteScalar<long>("SELECT count(*) FROM product_price_history") == 8 &&
                                    connection.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode=@barcode", new { barcode = fixture.Rows[0].Barcode }) == 300 &&
                                    connection.ExecuteScalar<decimal>("SELECT sum(stock_qty) FROM product_meta") == 3 &&
                                    connection.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }) == "acked" &&
                                    connection.ExecuteScalar<string>("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id", new { id = fixture.OriginalId }) == fixture.Original.PayloadHash &&
                                    connection.ExecuteScalar<int>("SELECT count(*) FROM catalog_import_recovery_draft") == 0,
                                    "resumed draft duplicated stock/history, rewrote an accepted operation or remained unapplied");
                            }
                            Evidence.Add("accepted_retained_draft_reopened; baseline=200; draft=300; explicit_verify=True; local_apply_count=1; prior_child_ack_preserved=True; stock=3; new_authoritative_ack=False");
                        }
                        finally { resumed.Close(); }
                    }
                }
                finally { Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, previousUrl); }
            }
        }

        private static async Task CheckAllRecoveryButtonsAsync(CatalogImportRecoveryDialog dialog)
        {
            var originalLanguage = PosLocalization.Current.CurrentLanguage;
            try
            {
                foreach (var language in new[] { "en", "es", "it", "zh-CN" })
                {
                    PosLocalization.Current.SetLanguage(language);
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => dialog.UpdateLayout(), DispatcherPriority.Render);
                    var buttons = new[] { "DiscardDraftButton", "RetireButton", "PrepareButton", "CommitButton", "CancelRecoveryButton" }
                        .Select(name => Named<Button>(dialog, name)).ToArray();
                    Require(buttons.All(button => button.IsVisible), "actual retirement and draft state did not expose every action");
                    var draft = Draft(dialog);
                    var labelKeys = new[] { "importRecovery.discardDraft",
                        draft.HasPreparedPlan || draft.RequiresPlanRetirement ? "importRecovery.retirePreparedPlan" : "importRecovery.retire",
                        "importRecovery.verify", draft.RequiresAcceptedReconciliation ? "importRecovery.reconcileAccepted" : "importRecovery.commit",
                        Busy(dialog) ? "common.cancel" : "common.close" };
                    for (var index = 0; index < buttons.Length; index++)
                        Require(buttons[index].Content?.ToString() == PosLocalization.T(labelKeys[index]),
                            "recovery action did not use the actual language before measuring: " + language + "/" + buttons[index].Name);
                    foreach (var button in buttons) CheckBounds(button, dialog, language);
                    Capture(dialog, "recovery-all-actions-" + language, buttons);
                }
            }
            finally { PosLocalization.Current.SetLanguage(originalLanguage); }
        }

        private static async Task CheckMeasurementsAsync()
        {
            var fixture = await Fixture.CreateAsync("repeat");
            using (var control = new FocusReturnHost())
            {
                var controlRetained = await CheckControlMeasurementsAsync(control);
                for (var index = 0; index < 2; index++) await OpenCloseAsync(fixture, control);
                await DrainAndCollectAsync();
                var managedBefore = GC.GetTotalMemory(true);
                var closed = new List<WeakReference>();
                using (var observation = new BoundedDispatcherObservation(Dispatcher.CurrentDispatcher))
                using (var samples = new Measurement(Dispatcher.CurrentDispatcher, "repeat-3row"))
                {
                    for (var index = 0; index < 5; index++)
                        using (var sql = SqliteWorkMetrics.Begin())
                        {
                            var watch = Stopwatch.StartNew();
                            closed.Add(await OpenCloseAsync(fixture, control));
                            Evidence.Add("recovery_open_cycle=" + (index + 1) + "; elapsed_ms=" + watch.Elapsed.TotalMilliseconds.ToString("F3", Invariant) + "; observed_connections=" + sql.Connections + "; observed_product_commands=" + sql.ProductCommands + "; product_commands_on_dispatcher=" + sql.ProductCommandsOnCallingThread);
                            Require(sql.ProductCommandsOnCallingThread == 0, "recovery product query ran on Dispatcher");
                        }
                    await DrainAndCollectAsync(closed, "recovery");
                    await samples.StopAsync();
                    var state = observation.Snapshot();
                    var retained = closed.Count(reference => reference.IsAlive);
                    var managedAfter = GC.GetTotalMemory(true);
                    Evidence.Add("recovery_release absolute_release=NOT_QUALIFIED; control_closed_window_weakrefs_alive=" + controlRetained + "; recovery_closed_window_weakrefs_alive=" + retained + "; pending_closed_window_roots=" + state.ClosedRootedWindows + "; observer_dropped=" + observation.Dropped + "; managed_before_bytes=" + managedBefore + "; managed_after_gc_bytes=" + managedAfter);
                    samples.Write();
                    // This host retains closed plain WPF and Recovery DataGrids through
                    // native UIA handles. Compare identical caller/focus lifecycles;
                    // surviving weak targets never qualify absolute release or Win7.
                    Require(retained <= controlRetained && state.ClosedRootedWindows == 0 && observation.Dropped == 0, "repeat recovery exceeded plain-control retention or lost observations");
                    Require(managedAfter - managedBefore <= 8388608, "five-cycle managed growth exceeded the existing 8 MiB window budget");
                    Evidence.Add("recovery_comparative_regression=PASS; absolute_release=NOT_QUALIFIED; win7_qualified=False");
                }
            }
        }

        private static async Task<int> CheckControlMeasurementsAsync(FocusReturnHost control)
        {
            var closed = new List<WeakReference>();
            using (var samples = new Measurement(Dispatcher.CurrentDispatcher, "control-plain-grid-3row"))
            {
                for (var index = 0; index < 5; index++) closed.Add(await OpenCloseControlAsync(control));
                await DrainAndCollectAsync(closed, "plain_control");
                await samples.StopAsync(); samples.Write();
                var retained = closed.Count(reference => reference.IsAlive);
                Evidence.Add("control_release closed_window_weakrefs_alive=" + retained + "; focus_return=visible_caller_button_before_close; model_rows=3");
                return retained;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<WeakReference> OpenCloseControlAsync(FocusReturnHost control)
        {
            Window dialog = null;
            DataGrid grid = null;
            try
            {
                grid = new DataGrid { AutoGenerateColumns = true, ItemsSource = Enumerable.Range(0, 3).Select(index => new SupplierImportEditableRow { Barcode = "CONTROL-" + index, ProductName = "Control", RetailPrice = "200" }).ToArray() };
                dialog = new Window { Width = 860, Height = 640, Owner = control.Owner, Content = grid, ShowInTaskbar = false };
                dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                grid.SelectedIndex = 0; grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns.First(column => (string)column.Header == "RetailPrice")); grid.BeginEdit();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
                await control.ReturnFocusAsync();
                var weak = new WeakReference(dialog);
                dialog.Close();
                return weak;
            }
            finally { dialog?.Close(); dialog = null; grid = null; }
        }

        private static async Task CheckBatchAsync(int rowCount, bool canCommit)
        {
            // Setup is outside the observed interval. All legacy rows are retained;
            // each supported local plan is committed once after a verified backup.
            // Authoritative remote reconciliation is qualified by the separate corpus.
            var fixture = await Fixture.CreateAsync("batch-" + rowCount, rowCount);
            var before = fixture.Counts();
            var backupsBefore = Directory.GetFiles(AppPaths.BackupsDirectory, "before-catalog-recovery-*.db");
            using (var samples = new Measurement(Dispatcher.CurrentDispatcher, "single-" + rowCount + "row"))
            using (var sql = SqliteWorkMetrics.Begin())
            using (var host = new CenterHost(fixture))
            {
                var watch = Stopwatch.StartNew();
                await host.ReadyAsync();
                Require(host.ViewModel.ImportRecoveries.Single().Batch.ItemCount == rowCount, "original payload did not list every row");
                var dialog = await host.OpenRecoveryAsync();
                try
                {
                    await ReadyAsync(dialog);
                    Require(Rows(dialog).Length == rowCount, "large prepare lost original rows");
                    var realized = Descendants(Named<DataGrid>(dialog, "RecoveryRows")).OfType<DataGridRow>().Count();
                    Require(realized <= 32, "5000-row recovery disabled DataGrid virtualization");
                    await EditRetailAsync(dialog, "200");
                    if (!canCommit)
                    {
                        Click(dialog, "PrepareButton");
                        await IdleAsync(dialog);
                        Require(!Named<Button>(dialog, "CommitButton").IsEnabled &&
                            Named<TextBlock>(dialog, "RecoveryStatus").Text == ImportRecoveryPresentation.FriendlyCause("recovery_payload_too_large"), "oversize recovery did not show its localized payload limit");
                        Require(Rows(dialog).Length == rowCount && Rows(dialog)[0].RetailPrice == "200" && before == fixture.Counts(), "oversize rejection lost draft rows or wrote data");
                        Require(!Directory.GetFiles(AppPaths.BackupsDirectory, "before-catalog-recovery-*.db").Except(backupsBefore).Any(), "oversize rejection created a backup before validation");
                        Click(dialog, "CommitButton");
                        await IdleAsync(dialog);
                        Require(before == fixture.Counts(), "disabled oversize commit mutated data");
                        Evidence.Add("oversize_batch rows=" + rowCount + "; payload_utf8_bytes=" + Encoding.UTF8.GetByteCount(fixture.Original.PayloadJson) + "; realized_grid_rows=" + realized + "; list_prepare_verify_ms=" + watch.Elapsed.TotalMilliseconds.ToString("F3", Invariant) + "; draft_retained=True; writes=0; backup_count=0; cause=recovery_payload_too_large");
                        await samples.StopAsync(); samples.Write();
                        fixture.CheckOriginal();
                        return;
                    }
                    await VerifyAsync(dialog);
                    var preview = await new CatalogImportRecoveryService(fixture.Factory).BuildPreviewAsync(Draft(dialog), Rows(dialog), CancellationToken.None);
                    Require(preview.NoChangeRows.Count == rowCount - 1, "large recovery fixture did not retain unchanged rows");
                    var expectedBatches = (rowCount + 499) / 500;
                    await CheckQueryShapeAsync(fixture, preview, expectedBatches);
                    Evidence.Add("large_batch observed_rows=" + rowCount + "; payload_utf8_bytes=" + Encoding.UTF8.GetByteCount(fixture.Original.PayloadJson) + "; realized_grid_rows=" + realized + "; list_prepare_verify_ms=" + watch.Elapsed.TotalMilliseconds.ToString("F3", Invariant) + "; observed_connections=" + sql.Connections + "; product_commands_on_dispatcher=" + sql.ProductCommandsOnCallingThread);
                    var callingThread = Thread.CurrentThread.ManagedThreadId;
                    var observedBatches = 0;
                    var observedProductWrites = 0;
                    var observedHistoryWrites = 0;
                    var applyOperationsOnDispatcher = 0;
                    var applyHooks = new SupplierExcelImportTestHooks
                    {
                        AfterExistingProductBatch = count => { Interlocked.Increment(ref observedBatches); if (Thread.CurrentThread.ManagedThreadId == callingThread) Interlocked.Increment(ref applyOperationsOnDispatcher); },
                        AfterProductWrite = count => { Interlocked.Increment(ref observedProductWrites); if (Thread.CurrentThread.ManagedThreadId == callingThread) Interlocked.Increment(ref applyOperationsOnDispatcher); },
                        AfterHistoryWrite = count => { Interlocked.Increment(ref observedHistoryWrites); if (Thread.CurrentThread.ManagedThreadId == callingThread) Interlocked.Increment(ref applyOperationsOnDispatcher); }
                    };
                    var service = typeof(CatalogImportRecoveryDialog).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
                    typeof(CatalogImportRecoveryService).GetField("_applyHooks", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, applyHooks);
                    var commitWatch = Stopwatch.StartNew();
                    Click(dialog, "CommitButton");
                    Require(Busy(dialog) && !Named<Button>(dialog, "CommitButton").IsEnabled, "large commit did not own loading state before await");
                    Click(dialog, "CommitButton");
                    Click(dialog, "PrepareButton");
                    await IdleAsync(dialog);
                    Require(Named<TextBlock>(dialog, "RecoveryStatus").Text == PosLocalization.T("importRecovery.queued"), "large recovery did not queue a replacement");
                    var backups = Directory.GetFiles(AppPaths.BackupsDirectory, "before-catalog-recovery-*.db").Except(backupsBefore).ToArray();
                    Require(backups.Length == 1, "single large recovery made no verified backup or multiple backups");
                    var backupFactory = new SqliteConnectionFactory(PosDbOptions.ForPath(backups[0]));
                    var backupValidation = await Task.Run(() => new DbMaintenanceRepository(backupFactory).ValidateAsync());
                    Require(backupValidation.IsValid, "large recovery backup was not SQLite-verified");
                    await Task.Run(() =>
                    {
                        using (var connection = backupFactory.Open())
                            Require(connection.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode=@barcode", new { barcode = fixture.Rows[0].Barcode }) == 2147483648L &&
                                connection.ExecuteScalar<long>("SELECT count(*) FROM catalog_import_outbox") == 1, "large backup was captured after recovery effects");
                    });
                    var progress = await new CatalogImportRecoveryService(fixture.Factory).GetPlanProgressAsync(fixture.OriginalId, CancellationToken.None);
                    Require(progress.TotalRows == rowCount && progress.CompletedRows == 0 && progress.TotalParts >= (rowCount + 999) / 1000,
                        "persisted plan progress lost rows or claimed a partial ACK");
                    Evidence.Add("large_commit rows=" + rowCount + "; elapsed_ms=" + commitWatch.Elapsed.TotalMilliseconds.ToString("F3", Invariant) + "; verified_backup_bytes=" + new FileInfo(backups[0]).Length + "; observed_connections=" + sql.Connections + "; product_commands_on_dispatcher=" + sql.ProductCommandsOnCallingThread + "; apply_batch_queries=" + observedBatches + "; apply_product_writes=" + observedProductWrites + "; apply_history_writes=" + observedHistoryWrites + "; apply_operations_on_dispatcher=" + applyOperationsOnDispatcher + "; replacement_parts=" + progress.TotalParts + "; replacement_rows=" + rowCount + "; completed_parts=0; original_resolved=False");
                    Require(observedBatches == expectedBatches && observedProductWrites == 1 && observedHistoryWrites == 1 && applyOperationsOnDispatcher == 0, "large commit SQL batching or worker-thread execution regressed");
                    Require(sql.ProductCommandsOnCallingThread == 0, "large recovery product query ran on Dispatcher");
                    fixture.CheckReplacement();
                }
                finally { dialog.Close(); }
                await samples.StopAsync(); samples.Write();
                fixture.CheckOriginal();
            }
        }

        private static async Task CheckQueryShapeAsync(Fixture fixture, SupplierImportSyncPreview preview, int batches)
        {
            var before = fixture.Counts();
            var dryRun = await Task.Run(() => new SupplierExcelImportApplier(fixture.Factory).ApplyAsync(preview,
                new SupplierExcelImportApplyOptions { InsertNew = true, DryRun = true }));
            Evidence.Add("recovery_apply_dryrun_sql rows=" + fixture.Rows.Length + "; existing_product_batches=" + dryRun.SqlMetrics.ExistingProductSelectCommands + "; total_commands=" + dryRun.SqlMetrics.TotalCommands + "; writes=" + dryRun.SqlMetrics.ProductWriteCommands);
            Require(dryRun.Errors == 0 && dryRun.SqlMetrics.ExistingProductSelectCommands == batches && dryRun.SqlMetrics.ProductWriteCommands == 0 && before == fixture.Counts(),
                "recovery dry-run query shape was not batched or changed data");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static async Task<WeakReference> OpenCloseAsync(Fixture fixture, FocusReturnHost control)
        {
            CatalogImportRecoveryDialog dialog = null;
            try
            {
                dialog = new CatalogImportRecoveryDialog(fixture.Factory, fixture.OriginalId, () => true, () => _generation) { Owner = control.Owner };
                dialog.Show(); await ReadyAsync(dialog);
                await EditRetailAsync(dialog, "200");
                await VerifyAsync(dialog);
                // Match the existing lifecycle runner and the real caller's focus
                // restoration. WPF input restoration may retain a retired editor
                // when a test closes both its focused dialog and disposable owner.
                await control.ReturnFocusAsync();
                var weak = new WeakReference(dialog);
                dialog.Close(); dialog = null;
                return weak;
            }
            finally { dialog?.Close(); dialog = null; }
        }

        private sealed class FocusReturnHost : IDisposable
        {
            internal readonly Window Owner;
            private readonly Button _input = new Button { Content = "Caller focus target" };
            internal FocusReturnHost() { Owner = new Window { Width = 1024, Height = 768, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false, Content = _input }; Owner.Show(); }
            internal async Task ReturnFocusAsync()
            {
                Keyboard.ClearFocus(); Owner.Activate(); _input.Focus();
                await Owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                Require(ReferenceEquals(Keyboard.FocusedElement, _input), "visible caller did not receive keyboard focus before close");
            }
            public void Dispose() { Owner.Close(); }
        }

        private sealed class CenterHost : IDisposable
        {
            internal readonly Window Owner;
            internal readonly SyncCenterDialog Center;
            private readonly TaskCompletionSource<bool> _rendered = new TaskCompletionSource<bool>();
            private EventHandler _renderedHandler;
            internal SyncCenterViewModel ViewModel => (SyncCenterViewModel)Center.DataContext;
            internal CenterHost(Fixture fixture, Func<bool> allow = null, bool forceOwnerOutsideWorkArea = false)
            {
                allow = allow ?? (() => true);
                Owner = new Window { Width = 1024, Height = 768, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false };
                Owner.Show();
                if (forceOwnerOutsideWorkArea)
                {
                    // Only the plain QA caller is positioned outside its monitor.
                    // Dialog positioning remains entirely in DialogShellWindow.
                    var workArea = MonitorHelper.GetWorkAreaForExactWindowOrPrimary(Owner);
                    Owner.Left = workArea.Left - 32;
                    Owner.Top = workArea.Top;
                }
                Center = new SyncCenterDialog(fixture.Factory, (trigger, repair, token) => Task.FromResult(new CatalogSyncRunResult(true)),
                    _ => Task.FromResult(false), _ => Task.FromResult(allow()), allow, () => _generation) { Owner = Owner };
                _renderedHandler = (_, __) =>
                {
                    Center.ContentRendered -= _renderedHandler;
                    _rendered.TrySetResult(true);
                };
                Center.ContentRendered += _renderedHandler;
                Center.Show();
            }
            internal async Task ReadyAsync()
            {
                await WaitAsync(() => _rendered.Task.IsCompleted, "Sync Center ContentRendered");
                await WaitAsync(() => ViewModel.ImportRecoveries.Count == 1, "Sync Center recovery list");
            }
            internal Button OpenButton() => Descendants(Center).OfType<Button>().Single(button => button.Tag is CatalogImportRecoveryBatch);
            internal async Task<CatalogImportRecoveryDialog> OpenRecoveryAsync()
            {
                var open = OpenButton();
                CatalogImportRecoveryDialog dialog = null;
                var rendered = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = (_, __) => rendered.TrySetResult(true);
                // Observe the real nested dialog before its first rendering operation;
                // polling Application.Windows alone can discover it after that event.
                DispatcherHookEventHandler posted = (_, __) =>
                {
                    if (!Center.Dispatcher.CheckAccess() || dialog != null) return;
                    dialog = Application.Current.Windows.OfType<CatalogImportRecoveryDialog>().SingleOrDefault();
                    if (dialog != null) dialog.ContentRendered += renderedHandler;
                };
                Center.Dispatcher.Hooks.OperationPosted += posted;
                try
                {
                    _ = Center.Dispatcher.BeginInvoke(new Action(() => open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))), DispatcherPriority.Input);
                    await WaitAsync(() => dialog != null && rendered.Task.IsCompleted, "nested recovery ContentRendered");
                    Require(ReferenceEquals(dialog.Owner, Center), "recovery did not use the Sync Center owner");
                    return dialog;
                }
                finally
                {
                    Center.Dispatcher.Hooks.OperationPosted -= posted;
                    if (dialog != null) dialog.ContentRendered -= renderedHandler;
                }
            }
            public void Dispose()
            {
                foreach (var recovery in Application.Current.Windows.OfType<CatalogImportRecoveryDialog>().ToArray()) recovery.Close();
                Center.ContentRendered -= _renderedHandler;
                Center.Close(); Owner.Close();
            }
        }

        private sealed class Fixture
        {
            private static string _pristinePath;
            private static string _pristineHash;
            internal SqliteConnectionFactory Factory;
            internal SupplierImportEditableRow[] Rows;
            internal CatalogImportOutboxEntry Original;
            internal long OriginalId;
            internal static async Task<Fixture> CreateAsync(string name, int rowCount = 3)
            {
                var setupWatch = Stopwatch.StartNew();
                RecordPhase("fixture_setup:" + name + ":begin");
                var fixture = new Fixture { Factory = new SqliteConnectionFactory(PosDbOptions.ForPath(Path.Combine(AppPaths.DataDirectory, "recovery-ui-" + name + ".db"))) };
                CreatePristineSchemaCopy(fixture.Factory, name);
                using (var connection = fixture.Factory.Open())
                    connection.Execute("INSERT OR REPLACE INTO app_settings(key,value) VALUES(@id,@shop),(@code,@shopCode)", new { id = OutboxShopBinding.OfficialShopIdKey, shop = _session.ShopId, code = OutboxShopBinding.OfficialShopCodeKey, shopCode = _session.ShopCode });
                await new OnlineSyncGenerationRepository(fixture.Factory).ActivateAndRecoverAsync(_generation, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                fixture.Rows = Enumerable.Range(0, rowCount).Select(index => new SupplierImportEditableRow { RowNumber = index + 2,
                    Barcode = "UIREC-" + name + "-" + index, ProductName = "Recovery " + index, PurchasePrice = "100", RetailPrice = index == 0 ? "2147483648" : "200", Quantity = "1" }).ToArray();
                var request = new PosCatalogImportRequest { SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = "supplier_excel",
                    Batch = new PosCatalogImportBatchRequest { ClientImportId = "legacy-ui-" + name, IdempotencyKey = "legacy-ui-" + name + ":pos-catalog-import-v1", CreatedAt = "2026-10-08T00:00:00Z", SourceFileName = "legacy.xlsx" },
                    Summary = new PosCatalogImportSummaryRequest { NewProducts = rowCount }, Items = fixture.Rows.Select(row => new PosCatalogImportItemRequest { RowNumber = row.RowNumber, Barcode = row.Barcode, ClientItemId = "legacy-item-" + row.RowNumber, ProductName = row.ProductName, ChangeKind = "new", Operation = "upsert_product", PurchasePrice = row.PurchasePrice, RetailPrice = row.RetailPrice, Quantity = row.Quantity }).ToArray() };
                var json = Serialize(request);
                fixture.Original = new CatalogImportOutboxEntry { ClientImportId = request.Batch.ClientImportId, IdempotencyKey = request.Batch.IdempotencyKey, PayloadJson = json, PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json), SchemaVersion = request.SchemaVersion, Source = request.Source, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var applier = new SupplierExcelImportApplier(fixture.Factory);
                var preview = await applier.BuildPreviewAsync(fixture.Rows);
                var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { InsertNew = true, CatalogImportOutboxEntry = fixture.Original });
                Require(result.Errors == 0 && result.CatalogImportOutboxId > 0, "legacy original fixture did not commit");
                fixture.OriginalId = result.CatalogImportOutboxId;
                var blocked = await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(_options, _session, _generation, CancellationToken.None);
                Require(blocked.Blocked == 1 && blocked.FailureKind == SyncFailureKind.LocalValidation, "legacy import was not locally blocked");
                fixture.CheckOriginal();
                RecordPhase("fixture_setup:" + name + ":complete; elapsed_ms=" + setupWatch.Elapsed.TotalMilliseconds.ToString("F3", Invariant));
                return fixture;
            }
            private static void CreatePristineSchemaCopy(SqliteConnectionFactory target, string name)
            {
                // Initialize and validate once inside the same bounded scenario.
                // Each case still owns a distinct database and runs the real
                // generation, import, local blocking, backup and recovery paths.
                if (_pristinePath == null)
                {
                    var initializeWatch = Stopwatch.StartNew();
                    _pristinePath = Path.Combine(AppPaths.DataDirectory, "recovery-ui-pristine-" + Guid.NewGuid().ToString("N") + ".db");
                    DbInitializer.EnsureCreated(PosDbOptions.ForPath(_pristinePath));
                    VerifyPristine(new SqliteConnectionFactory(PosDbOptions.ForPath(_pristinePath)));
                    RequireNoSidecars(_pristinePath);
                    _pristineHash = HashFile(_pristinePath);
                    RecordPhase("fixture_pristine_initialized; elapsed_ms=" + initializeWatch.Elapsed.TotalMilliseconds.ToString("F3", Invariant) + "; sha256=" + _pristineHash);
                }
                var copyWatch = Stopwatch.StartNew();
                var expectedParent = Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Require(string.Equals(Path.GetDirectoryName(target.DbPath), expectedParent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(target.DbPath) == "recovery-ui-" + name + ".db" && !File.Exists(target.DbPath),
                    "fixture copy target is not a new owned QA database");
                RequireNoSidecars(_pristinePath);
                Require(HashFile(_pristinePath) == _pristineHash, "pristine schema changed between fixtures");
                File.Copy(_pristinePath, target.DbPath, false);
                Require(HashFile(target.DbPath) == _pristineHash, "fixture is not an exact pristine schema copy");
                VerifyPristine(target);
                RecordPhase("fixture_pristine_copy:" + name + "; elapsed_ms=" + copyWatch.Elapsed.TotalMilliseconds.ToString("F3", Invariant) + "; sha256=" + _pristineHash + "; independent_database=True");
            }
            private static void VerifyPristine(SqliteConnectionFactory factory)
            {
                using (var connection = factory.Open())
                {
                    Require(connection.ExecuteScalar<string>("PRAGMA journal_mode") == "delete", "pristine fixture requires the existing DELETE journal policy");
                    Require(connection.ExecuteScalar<string>("PRAGMA integrity_check") == "ok" && !connection.Query("PRAGMA foreign_key_check").Any(),
                        "pristine fixture integrity failed");
                    var ledger = connection.Query<MigrationPin>("SELECT migration_id AS Id,checksum AS Checksum FROM schema_migrations ORDER BY migration_id")
                        .Select(pin => pin.Id + ":" + pin.Checksum);
                    Require(ledger.SequenceEqual(SchemaMigrationRegistry.All.Select(migration => migration.MigrationId + ":" + migration.Checksum)),
                        "pristine fixture migration ledger differs from the full current registry");
                    Require(connection.ExecuteScalar<long>(@"SELECT
                        (SELECT count(*) FROM products)+(SELECT count(*) FROM sales)+(SELECT count(*) FROM product_price_history)+
                        (SELECT count(*) FROM catalog_import_outbox)+(SELECT count(*) FROM catalog_import_plan)+
                        (SELECT count(*) FROM catalog_import_recovery_draft)+(SELECT count(*) FROM catalog_import_prepared_plan)") == 0,
                        "pristine fixture contains economic, outbox, plan or draft state");
                }
            }
            private static void RequireNoSidecars(string path)
            {
                Require(!File.Exists(path + "-wal") && !File.Exists(path + "-shm") && !File.Exists(path + "-journal"),
                    "pristine fixture has an active SQLite sidecar");
            }
            private static string HashFile(string path)
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            private sealed class MigrationPin { public string Id { get; set; } public string Checksum { get; set; } }
            internal string Counts()
            {
                using (var connection = Factory.Open()) return connection.ExecuteScalar<long>("SELECT count(*) FROM catalog_import_outbox") + "/" + connection.ExecuteScalar<long>("SELECT count(*) FROM product_price_history") + "/" + connection.ExecuteScalar<long>("SELECT sum(unitPrice) FROM products");
            }
            internal void CheckOriginal()
            {
                using (var connection = Factory.Open())
                {
                    var row = connection.QuerySingle<Stored>("SELECT status AS Status,payload_json AS Json,payload_hash AS Hash FROM catalog_import_outbox WHERE id=@id", new { id = OriginalId });
                    Require(row.Status == "failed_blocked" && row.Json == Original.PayloadJson && row.Hash == Original.PayloadHash, "original immutable payload/hash/status changed");
                }
            }
            internal void CheckReplacement()
            {
                CheckOriginal();
                using (var connection = Factory.Open())
                {
                    var replacementId = connection.ExecuteScalar<long>("SELECT replacement_id FROM catalog_import_recovery WHERE original_id=@id", new { id = OriginalId });
                    var parts = connection.Query<CatalogImportOutboxEntry>(@"SELECT o.payload_json AS PayloadJson,o.payload_hash AS PayloadHash,o.operation_type AS OperationType
FROM catalog_import_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id JOIN catalog_import_outbox o ON o.id=m.outbox_id
WHERE p.original_id=@id ORDER BY m.ordinal", new { id = OriginalId }).ToArray();
                    Require(parts.Length > 0 && parts.All(part => CatalogImportPlanBuilder.Measure(part) <= 512 * 1024), "replacement envelope exceeds the verified 512 KiB Admin route limit");
                    var requests = parts.Select(part => Deserialize<PosCatalogImportRequest>(part.PayloadJson)).ToArray();
                    var items = requests.SelectMany(part => part.Items).ToArray();
                    Require(requests.All(part => part.Items.Length <= 1000 && part.Batch.ClientImportId != Original.ClientImportId && part.Batch.IdempotencyKey != Original.IdempotencyKey) && items.Length == Rows.Length && items.Select(item => item.Barcode).OrderBy(value => value).SequenceEqual(Rows.Select(row => row.Barcode).OrderBy(value => value)), "replacement reused identity or lost unchanged original rows");
                    Require(items.All(item => item.RetailPrice == "200" && item.PurchasePrice == "100" && item.Quantity == "1"), "replacement prices or quantity differ");
                    Require(connection.ExecuteScalar<long>("SELECT count(*) FROM catalog_import_outbox") == 1 + parts.Length && connection.ExecuteScalar<long>("SELECT count(*) FROM products WHERE unitPrice=200") == Rows.Length &&
                        connection.ExecuteScalar<long>("SELECT count(*) FROM catalog_import_recovery WHERE original_id=@id AND replacement_id=@replacementId AND resolved_at IS NULL", new { id = OriginalId, replacementId }) == 1, "replacement duplicated queue, resolved before ACK or did not correct local price");
                }
            }
        }

        private static string ReceiptUuid(string seed)
        {
            var hash = CatalogImportOutboxPayloadBuilder.Sha256Hex(seed);
            return hash.Substring(0, 8) + "-" + hash.Substring(8, 4) + "-4" + hash.Substring(13, 3) + "-8" + hash.Substring(17, 3) + "-" + hash.Substring(20, 12);
        }

        private static PosCatalogImportReceiptResponse RootReceipt(PosCatalogImportReceiptRequest request)
        {
            var items = request.OriginalRequest.Items;
            return new PosCatalogImportReceiptResponse { Ok = true, Code = "success", SchemaVersion = request.SchemaVersion, OriginalSchemaVersion = request.OriginalRequest.SchemaVersion,
                Status = "accepted", ShopId = _session.ShopId, ShopDeviceId = request.ShopDeviceId, ClientImportId = request.ClientImportId,
                IdempotencyKey = request.IdempotencyKey, PayloadHash = request.PayloadHash, CanonicalPayloadHash = "sha256:" + CatalogImportOutboxPayloadBuilder.Sha256Hex("root|" + request.PayloadHash),
                Receipt = new PosCatalogImportPersistedAck { Ok = true, Status = "accepted", BatchId = ReceiptUuid("root-batch"),
                    Items = items.Select(item => new PosCatalogImportPersistedItemAck { ClientItemId = item.ClientItemId, Barcode = item.Barcode,
                        RemoteProductId = ReceiptUuid("product|" + item.Barcode), Status = "accepted", AuthoritativeRevision = "2026-10-08T00:00:00.000000Z" }).ToArray(),
                    RemoteProductIds = items.Select(item => new PosCatalogImportPersistedProductAck { Barcode = item.Barcode, ClientItemId = item.ClientItemId,
                        RemoteProductId = ReceiptUuid("product|" + item.Barcode), AuthoritativeRevision = "2026-10-08T00:00:00.000000Z" }).ToArray(),
                    RemotePriceIds = items.SelectMany(item => new[] { "purchase", "retail" }.Where(type => CatalogImportOutboxPayloadBuilder.IsAdminPrice(type == "retail" ? item.RetailPrice : item.PurchasePrice))
                        .Select(type => new PosCatalogImportPersistedPriceAck { Barcode = item.Barcode, ClientItemId = item.ClientItemId,
                            RemoteProductId = ReceiptUuid("product|" + item.Barcode), PriceType = type, RemotePriceId = ReceiptUuid("original-price|" + item.Barcode + "|" + type) })).ToArray(),
                    Summary = new PosCatalogImportPersistedSummary { AcceptedItemCount = items.Length, ProductCount = items.Length } },
                CurrentProductSnapshots = items.Select(item => new PosCatalogImportProductSnapshot { ClientItemId = item.ClientItemId,
                    RemoteProductId = ReceiptUuid("product|" + item.Barcode), SnapshotStatus = "available", BaseRevision = "2026-10-08T00:00:00.000000Z",
                    RetailPrice = 150, PurchasePrice = 100, StockQuantity = 1 }).ToArray() };
        }

        private static PosCatalogImportReceiptResponse ChildReceipt(PosCatalogImportCorrectionReceiptRequest request, string status)
        {
            var items = request.OriginalRequest.Correction.Items;
            return new PosCatalogImportReceiptResponse { Ok = true, Code = "success", SchemaVersion = request.SchemaVersion, OriginalSchemaVersion = PosCatalogImportCorrectionContract.SchemaVersion,
                Status = status, ShopId = _session.ShopId, ShopDeviceId = request.ShopDeviceId, ClientImportId = request.ClientImportId,
                IdempotencyKey = request.IdempotencyKey, PayloadHash = request.PayloadHash, CanonicalPayloadHash = "sha256:" + CatalogImportOutboxPayloadBuilder.Sha256Hex("child|" + request.PayloadHash),
                SnapshotOnly = status == "not_found", Receipt = status == "accepted" ? new PosCatalogImportPersistedAck { Ok = true, Status = "accepted", BatchId = ReceiptUuid("child-batch"),
                    Items = items.Select(item => new PosCatalogImportPersistedItemAck { ClientItemId = item.ClientItemId, RemoteProductId = item.RemoteProductId,
                        Status = "accepted", AuthoritativeRevision = "2026-10-08T00:00:00.000001Z" }).ToArray(),
                    RemoteProductIds = items.Select(item => new PosCatalogImportPersistedProductAck { ClientItemId = item.ClientItemId, RemoteProductId = item.RemoteProductId,
                        AuthoritativeRevision = "2026-10-08T00:00:00.000001Z" }).ToArray(),
                    RemotePriceIds = items.SelectMany(item => item.FieldMask.Where(field => field != "quantityDelta").Select(field => new PosCatalogImportPersistedPriceAck
                    { ClientItemId = item.ClientItemId, RemoteProductId = item.RemoteProductId, PriceType = field == "retailPrice" ? "retail" : "purchase",
                        RemotePriceId = ReceiptUuid("child-price|" + item.ClientItemId + "|" + field) })).ToArray(),
                    Summary = new PosCatalogImportPersistedSummary { AcceptedItemCount = items.Length, ProductCount = items.Length } } : null };
        }

        // Synthetic, bounded HTTP framing for the actual receipt/retirement clients.
        // It accepts no external connection and records counts rather than credentials.
        private sealed class RecoveryReceiptServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Task _serve;
            private int _requests;
            private volatile bool _stopped;
            internal int Requests => Volatile.Read(ref _requests);
            internal PosAdminWebOptions Options { get; }
            internal string Url { get; }
            internal RecoveryReceiptServer(Func<string, string> response)
            {
                _listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
                Options = new PosAdminWebOptions(new Uri(Url));
                _serve = Task.Run(async () =>
                {
                    try
                    {
                        while (!_stopped)
                        {
                            using (var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false))
                            using (var stream = client.GetStream())
                            {
                                var bytes = new List<byte>();
                                var headerEnd = -1;
                                var one = new byte[1];
                                while (headerEnd < 0 && bytes.Count < 65536)
                                {
                                    if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) break;
                                    bytes.Add(one[0]);
                                    if (bytes.Count >= 4 && bytes[bytes.Count - 4] == 13 && bytes[bytes.Count - 3] == 10 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10) headerEnd = bytes.Count - 4;
                                }
                                Require(headerEnd >= 0, "receipt fixture did not receive bounded HTTP headers");
                                var header = Encoding.ASCII.GetString(bytes.Take(headerEnd).ToArray());
                                Require(header.Contains("/api/pos/catalog/import-receipt") || header.Contains("/api/pos/catalog/import-retire"), "receipt fixture received a remote mutation endpoint");
                                var length = int.Parse(header.Split(new[] { "\r\n" }, StringSplitOptions.None).Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Substring(15).Trim(), Invariant);
                                Require(length >= 0 && length <= 65536, "receipt fixture exceeded its synthetic three-row request bound");
                                var body = new byte[length];
                                var offset = 0;
                                while (offset < length)
                                {
                                    var read = await stream.ReadAsync(body, offset, length - offset).ConfigureAwait(false);
                                    Require(read > 0, "receipt fixture received a truncated request"); offset += read;
                                }
                                Interlocked.Increment(ref _requests);
                                var payload = Encoding.UTF8.GetBytes(response(Encoding.UTF8.GetString(body)));
                                var status = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(status, 0, status.Length).ConfigureAwait(false);
                                await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (ObjectDisposedException) when (_stopped) { }
                    catch (SocketException) when (_stopped) { }
                });
            }
            public void Dispose()
            {
                _stopped = true; _listener.Stop();
                if (_serve.IsCompleted) _serve.GetAwaiter().GetResult();
                else _serve.ContinueWith(task => { var observed = task.Exception; }, TaskScheduler.Default);
            }
        }

        private sealed class Stored { public string Status { get; set; } public string Json { get; set; } public string Hash { get; set; } }
        private static SupplierImportEditableRow[] Rows(CatalogImportRecoveryDialog dialog) => Named<DataGrid>(dialog, "RecoveryRows").ItemsSource.Cast<SupplierImportEditableRow>().ToArray();
        private static CatalogImportRecoveryDraft Draft(CatalogImportRecoveryDialog dialog) => (CatalogImportRecoveryDraft)typeof(CatalogImportRecoveryDialog).GetField("_draft", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
        private static bool Busy(CatalogImportRecoveryDialog dialog) => (bool)typeof(CatalogImportRecoveryDialog).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
        private static T Named<T>(FrameworkElement dialog, string name) where T : class => (T)dialog.FindName(name);
        private static void Click(CatalogImportRecoveryDialog dialog, string name) => Named<Button>(dialog, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static Task ReadyAsync(CatalogImportRecoveryDialog dialog) => WaitAsync(() => !Busy(dialog) && Named<DataGrid>(dialog, "RecoveryRows").ItemsSource != null, "recovery original rows");
        private static Task IdleAsync(CatalogImportRecoveryDialog dialog) => WaitAsync(() => !Busy(dialog), "recovery operation completion");
        private static async Task AcceptConfirmationAsync(CatalogImportRecoveryDialog owner, string stage)
        {
            RecordPhase(stage + ":confirmation_requested");
            ApplyConfirmDialog confirmation = null;
            var rendered = new TaskCompletionSource<bool>();
            EventHandler onRendered = (_, __) => rendered.TrySetResult(true);
            DispatcherHookEventHandler posted = (_, __) =>
            {
                if (!owner.Dispatcher.CheckAccess() || confirmation != null) return;
                confirmation = Application.Current.Windows.OfType<ApplyConfirmDialog>()
                    .SingleOrDefault(window => ReferenceEquals(window.Owner, owner));
                if (confirmation != null) confirmation.ContentRendered += onRendered;
            };
            owner.Dispatcher.Hooks.OperationPosted += posted;
            try
            {
                // Retirement first flushes the draft asynchronously. One posted
                // click can run before ShowConfirm; observe its real owner and
                // render event instead, within the existing ten-second bound.
                var watch = Stopwatch.StartNew();
                while (confirmation == null || !rendered.Task.IsCompleted)
                {
                    Require(watch.Elapsed < TimeSpan.FromSeconds(10), stage + " confirmation timed out");
                    await Task.Delay(10);
                }
                Require(confirmation.IsVisible && ReferenceEquals(confirmation.Owner, owner),
                    stage + " confirmation did not render under its recovery owner");
                RecordPhase(stage + ":confirmation_rendered");
                Descendants(confirmation).OfType<Button>().Single(button => button.IsDefault)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RecordPhase(stage + ":confirmation_clicked");
            }
            catch (Exception ex)
            {
                RecordPhase(stage + ":confirmation_failed:" + ex.Message);
                // A synchronous ShowDialog may still be pumping inside Click.
                // Cancel only this observed QA confirmation so its caller can
                // return and report the bounded failure without the outer kill.
                if (confirmation != null && confirmation.IsVisible &&
                    ReferenceEquals(confirmation.Owner, owner)) confirmation.Close();
                throw;
            }
            finally
            {
                owner.Dispatcher.Hooks.OperationPosted -= posted;
                if (confirmation != null) confirmation.ContentRendered -= onRendered;
            }
        }
        private static async Task VerifyAsync(CatalogImportRecoveryDialog dialog) { Click(dialog, "PrepareButton"); await IdleAsync(dialog); Require(Named<Button>(dialog, "CommitButton").IsEnabled, "corrected recovery did not verify: " + Named<TextBlock>(dialog, "RecoveryStatus").Text); }
        private static async Task EditRetailAsync(CatalogImportRecoveryDialog dialog, string value)
        {
            var grid = Named<DataGrid>(dialog, "RecoveryRows");
            var row = Rows(dialog)[0];
            grid.SelectedItem = row;
            grid.ScrollIntoView(row, grid.Columns[3]);
            grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[3]);
            grid.BeginEdit();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var editor = Descendants(grid.Columns[3].GetCellContent(row)).OfType<TextBox>().Single();
            editor.Text = value;
            editor.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(row.RetailPrice == value, "actual price editor did not retain correction");
        }
        private static string Digits(string value) => new string(value.Where(char.IsDigit).ToArray());
        private static void Capture(Window visual, string name, params FrameworkElement[] actions)
        {
            RecordPhase("capture:" + name + ":begin");
            visual.UpdateLayout();
            var geometry = RecordGeometry(visual, "live_" + name);
            var workArea = MonitorHelper.GetWorkAreaForExactWindowOrPrimary(visual.Owner ?? visual);
            Require(Contains(workArea, WindowBounds(visual)), "live overlay escaped its monitor work area: " + geometry);
            var root = visual.Content as FrameworkElement;
            Require(root != null, "dialog has no rendered root: " + geometry);
            var card = (root as Panel)?.Children.OfType<Border>().SingleOrDefault();
            Require(card != null, "dialog has no rendered overlay card: " + geometry);
            var requiredWidth = RequiredCardLength(card.Width, card.MinWidth);
            var requiredHeight = RequiredCardLength(card.Height, card.MinHeight);
            var ownerBounds = visual.Owner == null ? Rect.Empty : WindowBounds(visual.Owner);
            var ownerCanHost = visual.Owner != null && workArea.Contains(ownerBounds) && ownerBounds.Width >= requiredWidth && ownerBounds.Height >= requiredHeight;
            var expectedBounds = ownerCanHost ? ownerBounds : workArea;
            var policyGeometry = geometry + "; required_card=" + requiredWidth.ToString(Invariant) + "," + requiredHeight.ToString(Invariant) + "; expected_native=" + RectText(expectedBounds);
            Evidence.Add(policyGeometry);
            Require(SameBounds(WindowBounds(visual), expectedBounds), "live overlay did not use its standard owner/work-area bounds: " + policyGeometry);
            foreach (var action in actions) CheckBounds(action, visual, "live_" + name);

            var originalSize = root.RenderSize;
            var originalSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(root);
            var originalParent = VisualTreeHelper.GetParent(root);
            var originalDataContext = root.ReadLocalValue(FrameworkElement.DataContextProperty);
            var originalWidth = root.ReadLocalValue(FrameworkElement.WidthProperty);
            var originalHeight = root.ReadLocalValue(FrameworkElement.HeightProperty);
            var dpi = VisualTreeHelper.GetDpi(visual);
            // Match Program's exact Measure/Arrange viewport. Keep the real root
            // attached and preserve every binding; restore its native layout before
            // the Dispatcher can resume the live window's pending layout work.
            using (visual.Dispatcher.DisableProcessing())
            {
                try
                {
                    root.Measure(new Size(1024, 768));
                    root.Arrange(new Rect(0, 0, 1024, 768));
                    var viewportGeometry = "viewport=" + name + "; target=root_content; logical_width=" + root.ActualWidth.ToString(Invariant) + "; logical_height=" + root.ActualHeight.ToString(Invariant) + "; " + geometry;
                    Evidence.Add(viewportGeometry);
                    Require(Math.Abs(root.ActualWidth - 1024) < 1 && Math.Abs(root.ActualHeight - 768) < 1, "translated root screenshot escaped its exact 1024x768 viewport: " + viewportGeometry);
                    foreach (var action in actions) CheckViewportBounds(action, root, viewportGeometry);
                    var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(AppPaths.DataDirectory, name + ".png"))) encoder.Save(stream);
                    Evidence.Add("screenshot=" + name + "; target=root_content_exact_viewport; logical_width=" + root.ActualWidth.ToString(Invariant) + "; logical_height=" + root.ActualHeight.ToString(Invariant) + "; dpi_scale_x=" + dpi.DpiScaleX.ToString(Invariant) + "; dpi_scale_y=" + dpi.DpiScaleY.ToString(Invariant));
                }
                finally
                {
                    root.Measure(originalSize);
                    root.Arrange(originalSlot);
                    visual.InvalidateMeasure(); visual.InvalidateArrange(); visual.UpdateLayout();
                    var restored = RecordGeometry(visual, "restored_" + name);
                    Require(Math.Abs(root.ActualWidth - originalSize.Width) < 1 && Math.Abs(root.ActualHeight - originalSize.Height) < 1 &&
                        ReferenceEquals(VisualTreeHelper.GetParent(root), originalParent) &&
                        Equals(root.ReadLocalValue(FrameworkElement.DataContextProperty), originalDataContext) &&
                        Equals(root.ReadLocalValue(FrameworkElement.WidthProperty), originalWidth) && Equals(root.ReadLocalValue(FrameworkElement.HeightProperty), originalHeight),
                        "exact viewport capture changed the live layout, parent or bindings: " + restored);
                }
            }
            RecordPhase("capture:" + name + ":complete");
        }
        private static void CheckPresentation(ImportRecoveryPresentation item, Fixture fixture)
        {
            Require(item.Cause == PosLocalization.T("importRecovery.invalidData") && item.AffectedFields.Contains(fixture.Rows[0].Barcode) && item.AffectedFields.Contains(PosLocalization.T("importRecovery.retail")) && Digits(item.AffectedFields).Contains("999999999") && item.Batch.ItemCount == 3 && item.Batch.NeverSent, "Sync Center omitted friendly cause, affected field/barcode/limit or delivery evidence");
        }
        private static void CheckBounds(FrameworkElement element, Window window, string language)
        {
            var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            var geometry = RecordGeometry(window, "live_cta_" + language) + "; cta=" + element.Name + "; cta_bounds=" + RectText(bounds);
            Evidence.Add(geometry);
            Require(element.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight, "recovery CTA clipped in its live window: " + geometry);
        }
        private static void CheckViewportBounds(FrameworkElement element, FrameworkElement root, string geometry)
        {
            var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            var received = geometry + "; cta=" + element.Name + "; cta_bounds=" + RectText(bounds);
            Evidence.Add(received);
            Require(element.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= root.ActualWidth && bounds.Bottom <= root.ActualHeight,
                "recovery CTA clipped in its exact 1024x768 root viewport: " + received);
        }
        private static string RecordGeometry(Window window, string stage)
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            var summary = "geometry=" + stage + "; dialog=" + RectText(WindowBounds(window)) + "; owner=" + (window.Owner == null ? "none" : RectText(WindowBounds(window.Owner))) +
                "; work_area=" + RectText(MonitorHelper.GetWorkAreaForExactWindowOrPrimary(window.Owner ?? window)) +
                "; dpi_scale_x=" + dpi.DpiScaleX.ToString(Invariant) + "; dpi_scale_y=" + dpi.DpiScaleY.ToString(Invariant);
            Evidence.Add(summary);
            return summary;
        }
        private static Rect WindowBounds(Window window) => new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
        private static string RectText(Rect value) => value.X.ToString(Invariant) + "," + value.Y.ToString(Invariant) + "," + value.Width.ToString(Invariant) + "," + value.Height.ToString(Invariant);
        private static bool Contains(Rect container, Rect value) => value.Left >= container.Left - 1 && value.Top >= container.Top - 1 && value.Right <= container.Right + 1 && value.Bottom <= container.Bottom + 1;
        private static bool SameBounds(Rect left, Rect right) => Math.Abs(left.Left - right.Left) < 1 && Math.Abs(left.Top - right.Top) < 1 && Math.Abs(left.Width - right.Width) < 1 && Math.Abs(left.Height - right.Height) < 1;
        private static double RequiredCardLength(double explicitLength, double minimum) => !double.IsNaN(explicitLength) && !double.IsInfinity(explicitLength) && explicitLength > 0 ? explicitLength : (!double.IsNaN(minimum) && !double.IsInfinity(minimum) && minimum > 0 ? minimum : 1);
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
        private static async Task WaitAsync(Func<bool> complete, string stage)
        {
            RecordPhase("wait_begin:" + stage);
            var watch = Stopwatch.StartNew();
            while (!complete()) { Require(watch.Elapsed < TimeSpan.FromSeconds(10), stage + " timed out"); await Task.Delay(10); }
            RecordPhase("wait_condition_complete:" + stage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            RecordPhase("wait_idle_complete:" + stage);
        }
        private static async Task DrainAndCollectAsync(IReadOnlyList<WeakReference> closed = null, string scope = "warmup")
        {
            // Match the existing Program lifecycle observer: WPF weak-event and
            // binding cleanup can retire roots after a Dispatcher timer turn.
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var retained = closed?.Count(reference => reference.IsAlive) ?? 0;
                if (closed == null || retained == 0 || attempt == 10)
                {
                    Evidence.Add("collection scope=" + scope + "; bounded_attempts=" + attempt + "; max_attempts=10; closed_weakrefs_alive=" + retained);
                    return;
                }
                await Task.Delay(100);
            }
        }
        private static async Task CheckAsync(List<string> failures, string name, Func<Task> action)
        {
            RecordPhase(name + "=BEGIN");
            try { await action(); Evidence.Add(name + "=PASS"); }
            catch (Exception ex) { Evidence.Add(name + "=FAIL " + ex); failures.Add(name + ": " + ex.Message); }
            FlushEvidence();
            Console.WriteLine("IMPORT_RECOVERY_PHASE=" + Evidence[Evidence.Count - 1]);
        }
        private static void RecordPhase(string phase)
        {
            Evidence.Add("phase=" + phase + "; utc=" + DateTimeOffset.UtcNow.ToString("O", Invariant));
            FlushEvidence();
            Console.WriteLine("IMPORT_RECOVERY_PHASE=" + phase);
        }
        private static void FlushEvidence()
        {
            var pending = Evidence.Skip(_flushedEvidenceCount).ToArray();
            if (pending.Length == 0) return;
            var path = Path.Combine(AppPaths.DataDirectory, "import-recovery-completion.txt");
            if (_flushedEvidenceCount == 0) File.WriteAllLines(path, pending);
            else File.AppendAllLines(path, pending);
            _flushedEvidenceCount = Evidence.Count;
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("import_recovery_ui: " + message); }
        private static string Serialize<T>(T value) { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
        private static T Deserialize<T>(string value) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(value))) return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(stream); }

        private sealed class Measurement : IDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly List<string> _memory = new List<string>();
            private readonly List<double> _probes = new List<double>();
            private readonly Task _memoryTask, _probeTask;
            private readonly string _prefix;
            private long _privatePeak, _managedPeak;
            internal Measurement(Dispatcher dispatcher, string prefix)
            {
                _prefix = prefix;
                var watch = Stopwatch.StartNew();
                _memoryTask = Task.Run(async () =>
                {
                    try { while (!_stop.IsCancellationRequested)
                    {
                        using (var process = Process.GetCurrentProcess())
                        {
                            process.Refresh(); var privateBytes = process.PrivateMemorySize64; var managed = GC.GetTotalMemory(false);
                            _privatePeak = Math.Max(_privatePeak, privateBytes); _managedPeak = Math.Max(_managedPeak, managed);
                            Require(_memory.Count < 1200, "memory sampling exceeded its finite 60-second observation bound");
                            _memory.Add(watch.Elapsed.TotalMilliseconds.ToString("F3", Invariant) + "," + privateBytes + "," + managed);
                        }
                        await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                    } } catch (OperationCanceledException) { }
                });
                _probeTask = Task.Run(async () =>
                {
                    try { while (!_stop.IsCancellationRequested)
                    {
                        var posted = Stopwatch.GetTimestamp();
                        await dispatcher.InvokeAsync(() => _probes.Add((Stopwatch.GetTimestamp() - posted) * 1000d / Stopwatch.Frequency), DispatcherPriority.Input, _stop.Token).Task.ConfigureAwait(false);
                        Require(_probes.Count < 1200, "dispatcher sampling exceeded its finite observation bound");
                        await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                    } } catch (OperationCanceledException) { }
                });
            }
            internal async Task StopAsync() { _stop.Cancel(); await Task.WhenAll(_memoryTask, _probeTask); }
            internal void Write()
            {
                File.WriteAllLines(Path.Combine(AppPaths.DataDirectory, "recovery-ui-" + _prefix + "-memory-samples.csv"), new[] { "elapsed_ms,private_bytes,managed_bytes" }.Concat(_memory));
                File.WriteAllLines(Path.Combine(AppPaths.DataDirectory, "recovery-ui-" + _prefix + "-dispatcher-probes.csv"), new[] { "enqueue_to_input_ms" }.Concat(_probes.Select(value => value.ToString("F3", Invariant))));
                var sorted = _probes.OrderBy(value => value).ToArray();
                Evidence.Add("measurement scope=" + _prefix + "; memory_interval_ms=50; observed_memory_samples=" + _memory.Count + "; observed_private_peak_bytes=" + _privatePeak + "; observed_managed_peak_bytes=" + _managedPeak + "; dispatcher_samples=" + sorted.Length + "; dispatcher_p95_ms=" + (sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]).ToString("F3", Invariant) + "; dispatcher_max_ms=" + (sorted.Length == 0 ? 0 : sorted.Last()).ToString("F3", Invariant));
                Evidence.Add("budget_source=docs/reports/evidence/2026-09-26-post-pr111/preregistered-budget-v1.json; applied_managed_5_cycle_growth_bytes=8388608; compared_probe_max_ms=200; compared_private_peak_bytes=201326592; compared_managed_peak_bytes=33554432; workload_differs_from_cart_control=True; qualified_stability=False; peak_is_observed_50ms_samples_not_lifetime_peak=True");
                Require(_memory.Count > 1 && sorted.Length > 1, "bounded measurement did not acquire actual memory/probe samples");
            }
            public void Dispose()
            {
                _stop.Cancel();
                Task.WhenAll(_memoryTask, _probeTask).ContinueWith(task =>
                {
                    var observedFailure = task.Exception;
                    _stop.Dispose();
                }, TaskScheduler.Default);
            }
        }
    }
}
