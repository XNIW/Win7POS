using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Dapper;
using Win7POS.Core.Receipt;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // One local fixture, not a network sync/scheduler or long-running stability qualification.
    internal static class SyncBackupLoadSmoke
    {
        private const int ProductCount = 100000;
        private const int PageCount = 40;
        private const int PageRows = 500;
        private const int UiSamples = 24;
        private static readonly TimeSpan WorkBudget = TimeSpan.FromSeconds(45);
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static async Task RunAsync()
        {
            Require(IntPtr.Size == 4, "synthetic load requires the x86 WPF host");
            Require(Environment.GetEnvironmentVariable("WIN7POS_ADMIN_WEB_BASE_URL") == "http://127.0.0.1:9", "synthetic load requires the disabled local endpoint");
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var directory = Path.GetDirectoryName(factory.DbPath);
            var previousOperator = OperatorSessionHolder.Current;
            var state = new Observation();
            var samples = new List<Sample>();
            Window host = null;
            PosViewModel vm = null;
            Exception failure = null;
            Task catalogWorker = null, backupWorker = null, memoryWorker = null;
            using var start = new ManualResetEventSlim();
            using var firstPageCommitted = new ManualResetEventSlim();
            using var firstScanIssued = new ManualResetEventSlim();
            using var budgetCancellation = new CancellationTokenSource();
            using var memoryStop = new CancellationTokenSource();
            var catalogReady = Signal();
            var backupReady = Signal();
            var nativeBoundary = Signal();
            var budget = new Stopwatch();
            try
            {
                OperatorSessionHolder.Current = await CartPerformanceRegressionSmoke.CreateQaOperatorAsync().ConfigureAwait(true);
                var op = OperatorSessionHolder.Current;
                // These grants exist only in this disposable synthetic database.
                using (var connection = factory.Open())
                    connection.Execute("INSERT OR IGNORE INTO role_permissions(role_id,permission_code) VALUES(@role,@code)",
                        new[] { new { role = op.CurrentUser.RoleId, code = PermissionCodes.DbBackup }, new { role = op.CurrentUser.RoleId, code = PermissionCodes.CatalogImport } });
                Require(await op.LoginAsync(op.CurrentUser.Username, "2468").ConfigureAwait(true) == LoginResult.Success, "synthetic operator permission reload failed");
                var permissions = new PermissionService(op);
                var actor = op.CurrentUser.Id;
                Action<string> demand = permission =>
                {
                    Require(ReferenceEquals(OperatorSessionHolder.Current, op) && op.CurrentUser?.Id == actor, "synthetic operator changed during load");
                    permissions.Demand(permission, "Synthetic local load fixture");
                };
                foreach (var permission in new[] { PermissionCodes.PosSell, PermissionCodes.PosPay, PermissionCodes.DbBackup, PermissionCodes.CatalogImport }) demand(permission);
                await Task.Run(() =>
                {
                    using var connection = factory.Open();
                    using var transaction = connection.BeginTransaction();
                    connection.Execute(@"WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<@count)
INSERT INTO products(barcode,name,unitPrice,is_active,remote_product_id,remote_base_revision)
SELECT printf('LOAD%08d',x),printf('Load Product %08d',x),1000,1,printf('load-remote-%08d',x),'2026-10-03T10:00:00Z' FROM n;
INSERT INTO product_meta(barcode,stock_qty) SELECT barcode,100000 FROM products WHERE barcode LIKE 'LOAD%';", new { count = ProductCount }, transaction);
                    transaction.Commit();
                }).ConfigureAwait(true);
                using (var connection = factory.Open())
                {
                    Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE barcode LIKE 'LOAD%'") == ProductCount, "synthetic catalog seed count differs");
                    state.SalesBefore = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales");
                    state.OutboxBefore = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox");
                }
                var view = new PosView();
                vm = (PosViewModel)view.DataContext;
                var loaded = false;
                view.Loaded += (_, __) => loaded = true;
                host = new Window { Content = view, Width = 1024, Height = 768, ShowInTaskbar = false };
                host.Show();
                await CartPerformanceRegressionSmoke.WaitAsync(() => loaded && !vm.IsBusy, "synthetic load view initialization", 10000).ConfigureAwait(true);
                await CartPerformanceRegressionSmoke.DrainAsync().ConfigureAwait(true);
                var barcodeBox = (TextBox)view.FindName("BarcodeBox");
                Require(barcodeBox != null && vm.CartItems.Count == 0, "synthetic load started with a nonempty cart");
                state.PrivateBefore = PrivateBytes(); state.PrivatePeak = state.PrivateBefore;
                budget.Start(); budgetCancellation.CancelAfter(WorkBudget);
                var token = budgetCancellation.Token;

                catalogWorker = Task.Run(async () =>
                {
                    var active = Interlocked.Increment(ref state.CatalogActive);
                    Maximum(ref state.CatalogPeak, active);
                    catalogReady.TrySetResult(true);
                    try
                    {
                        start.Wait(token);
                        using var run = new RemoteCatalogBatchRepository(factory).CreateRunContext();
                        state.RunFlight = (SemaphoreSlim)typeof(RemoteCatalogApplyRunContext).GetField("_singleFlight", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(run);
                        for (var page = 1; page <= PageCount; page++)
                        {
                            token.ThrowIfCancellationRequested(); demand(PermissionCodes.CatalogImport);
                            var sample = BeginSample("catalog_page", page, state);
                            var result = await run.ApplyAsync(BuildPage(page), token).ConfigureAwait(false);
                            Require(result.ProductsApplied == PageRows && result.ProductsSkipped == 0 && result.ProductIdentityConflicts == 0, "catalog page did not apply completely");
                            Interlocked.Increment(ref state.PagesCommitted); // ApplyAsync returned after transaction commit.
                            FinishSample(sample, state); lock (samples) samples.Add(sample);
                            if (page == 1)
                            {
                                firstPageCommitted.Set();
                                firstScanIssued.Wait(token);
                            }
                            await Task.Yield();
                        }
                        state.RunTransactions = run.Diagnostics.CommittedTransactionCount;
                        Require(run.Diagnostics.PagesApplied == PageCount && state.RunTransactions == PageCount && state.RunFlight.CurrentCount == 1, "catalog run did not release its single-flight guard or commit every page");
                    }
                    finally { Interlocked.Decrement(ref state.CatalogActive); }
                }, token);
                var hooks = new BackupRestoreTestHooks
                {
                    NativeSnapshotRunner = copy =>
                    {
                        nativeBoundary.TrySetResult(true);
                        Require(firstPageCommitted.Wait(TimeSpan.FromSeconds(10), token), "catalog first commit did not reach backup boundary");
                        Require(firstScanIssued.Wait(TimeSpan.FromSeconds(10), token), "public scan did not reach backup boundary");
                        var entered = Stopwatch.GetTimestamp();
                        Interlocked.Increment(ref state.NativeCalls);
                        try { copy(); }
                        finally { lock (state.NativeIntervals) state.NativeIntervals.Add(Tuple.Create(entered, Stopwatch.GetTimestamp())); }
                    }
                };
                var backupPath = Path.Combine(directory, "synthetic-load-validated.db");
                backupWorker = Task.Run(async () =>
                {
                    var active = Interlocked.Increment(ref state.BackupActive);
                    Maximum(ref state.BackupPeak, active);
                    backupReady.TrySetResult(true);
                    try
                    {
                        start.Wait(token); demand(PermissionCodes.DbBackup);
                        var sample = BeginSample("validated_backup", 1, state);
                        var validation = await new SqliteOnlineBackup(factory, null, hooks).CreateVerifiedAsync(backupPath, token).ConfigureAwait(false);
                        Require(validation.IsValid, "synthetic load snapshot failed integrity or foreign-key validation");
                        Interlocked.Increment(ref state.BackupsPublished);
                        FinishSample(sample, state); lock (samples) samples.Add(sample);
                    }
                    finally { Interlocked.Decrement(ref state.BackupActive); }
                }, token);
                memoryWorker = Task.Run(async () =>
                {
                    try
                    {
                        for (var sample = 0; sample < 900 && !memoryStop.IsCancellationRequested; sample++)
                        {
                            Maximum(ref state.PrivatePeak, PrivateBytes());
                            Interlocked.Increment(ref state.MemorySamples);
                            var flight = state.RunFlight;
                            if (flight != null && flight.CurrentCount == 0) Interlocked.Increment(ref state.GuardHeldSamples);
                            await Task.Delay(50, memoryStop.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (memoryStop.IsCancellationRequested) { }
                });
                await WithinBudget(Task.WhenAll(catalogReady.Task, backupReady.Task), budget).ConfigureAwait(true);
                start.Set();
                await WithinBudget(nativeBoundary.Task, budget).ConfigureAwait(true);
                for (var ordinal = 1; ordinal <= UiSamples; ordinal++)
                {
                    demand(PermissionCodes.PosSell);
                    var completed = Signal(); var sawBusy = false;
                    PropertyChangedEventHandler handler = (_, args) =>
                    {
                        if (args.PropertyName != nameof(vm.IsBusy)) return;
                        if (vm.IsBusy)
                        {
                            if (!sawBusy) { sawBusy = true; Maximum(ref state.UiPeak, Interlocked.Increment(ref state.UiActive)); }
                        }
                        else if (sawBusy) { sawBusy = false; Interlocked.Decrement(ref state.UiActive); completed.TrySetResult(true); }
                    };
                    vm.PropertyChanged += handler;
                    var sample = BeginSample("public_scan", ordinal, state);
                    try
                    {
                        using var sql = SqliteWorkMetrics.Begin();
                        barcodeBox.Text = Barcode(ordinal);
                        barcodeBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                        if (ordinal == 1) firstScanIssued.Set();
                        barcodeBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(barcodeBox), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                        if (ordinal == 1)
                        {
                            Require(!vm.AddBarcodeCommand.CanExecute(null), "concurrent barcode submission remained enabled");
                            vm.AddBarcodeCommand.Execute(null); // The public submission guard must reject this duplicate.
                        }
                        await WithinBudget(completed.Task, budget).ConfigureAwait(true);
                        view.UpdateLayout();
                        sample.ProductCommands = sql.ProductCommands;
                        sample.CommandsOnUi = sql.ProductCommandsOnCallingThread;
                        Require(sample.CommandsOnUi == 0 && vm.CartItems.Count == ordinal && vm.CartItems.All(row => row.Quantity == 1) && vm.Total == ordinal * 1000L, "public scan lost/duplicated input or ran product SQL on the UI thread");
                        Interlocked.Increment(ref state.ScansCompleted);
                    }
                    finally { vm.PropertyChanged -= handler; }
                    FinishSample(sample, state); lock (samples) samples.Add(sample);
                    var probe = BeginSample("dispatcher_input_probe", ordinal, state);
                    await WithinBudget(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Input).Task, budget).ConfigureAwait(true);
                    FinishSample(probe, state); lock (samples) samples.Add(probe);
                    demand(PermissionCodes.PosPay);
                    sample = BeginSample("payment_preview", ordinal, state);
                    var draft = new PaymentReceiptDraft { SaleCode = "SYNTHETIC-LOAD", CreatedAtMs = 1791028800000L, DefaultPrint = false, UseReceipt42 = true,
                        ShopInfo = new ReceiptShopInfo { Name = "Synthetic Local Load" }, CartLines = vm.CartItems.Select(row => new PaymentReceiptDraftLine { Barcode = row.Barcode, Name = row.Name, Quantity = row.Quantity, UnitPrice = row.UnitPrice, LineTotal = row.LineTotal }).ToArray() };
                    using (var payment = new PaymentViewModel(vm.Total, draft, (_, __) => { Interlocked.Increment(ref state.OutputCalls); throw new InvalidOperationException("fixture attempted physical output"); }, openDrawerDefault: false)
                    { ShouldPrint = false, AutoPrintFiscalBoleta = false, OpenDrawerForCurrentPayment = false })
                    {
                        var confirmed = 0; var cancelled = 0;
                        payment.RequestClose += accepted => { if (accepted) confirmed++; else cancelled++; };
                        payment.SetExactTotalCommand.Execute(null);
                        Require(payment.IsValid && payment.CashAmountMinor == vm.Total, "exact cash preview differs from cart");
                        payment.PayAllCardCommand.Execute(null);
                        Require(payment.IsValid && payment.CardAmountMinor == vm.Total && payment.CashAmountMinor == 0 && !string.IsNullOrWhiteSpace(payment.ReceiptPreviewText), "card payment preview differs from cart");
                        payment.ConfirmCommand.Execute(null); payment.CancelCommand.Execute(null);
                        Require(confirmed == 1 && cancelled == 1, "payment preview public commands failed");
                        var paymentView = new PaymentView { DataContext = payment };
                        paymentView.Measure(new Size(800, 600)); paymentView.Arrange(new Rect(0, 0, 800, 600)); paymentView.UpdateLayout();
                        var image = new RenderTargetBitmap(800, 600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); image.Render(paymentView);
                        paymentView.DataContext = null;
                    }
                    Interlocked.Increment(ref state.PaymentsCompleted);
                    FinishSample(sample, state); lock (samples) samples.Add(sample);
                    Maximum(ref state.PrivatePeak, PrivateBytes());
                }
                await WithinBudget(Task.WhenAll(catalogWorker, backupWorker), budget).ConfigureAwait(true);
                Require(state.CatalogPeak == 1 && state.BackupPeak == 1 && state.UiPeak == 1 && state.CatalogActive == 0 && state.BackupActive == 0 && state.UiActive == 0, "fixture workers overlapped within a lane or remained active");
                Require(state.PagesCommitted == PageCount && state.BackupsPublished == 1 && state.ScansCompleted == UiSamples && state.PaymentsCompleted == UiSamples && state.OutputCalls == 0, "finite load workload did not finish exactly once");
                Require(samples.Any(row => row.Kind == "public_scan" && row.BackupAtStart == 1 && row.CatalogAtStart == 1), "public scan did not overlap the controlled backup/catalog operations");
                await Task.Run(() => { CheckBusiness(factory, state, true); CheckBusiness(new SqliteConnectionFactory(PosDbOptions.ForPath(backupPath)), state, false); }).ConfigureAwait(true);
                state.BehaviorPassed = true;
            }
            catch (Exception error) { failure = error; }
            finally
            {
                start.Set(); firstScanIssued.Set(); budgetCancellation.Cancel(); memoryStop.Cancel();
                try { await Task.WhenAll(new[] { catalogWorker, backupWorker, memoryWorker }.Where(task => task != null)).ConfigureAwait(true); }
                catch (Exception error) { if (failure == null) failure = error; }
                host?.Close(); vm?.Dispose(); OperatorSessionHolder.Current = previousOperator;
                state.PrivateEnd = PrivateBytes(); Maximum(ref state.PrivatePeak, state.PrivateEnd);
                budget.Stop();
                WriteEvidence(directory, samples, state, budget.Elapsed.TotalMilliseconds, failure == null && state.BehaviorPassed);
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static RemoteCatalogBatch BuildPage(int page) => new RemoteCatalogBatch { Products = Enumerable.Range(ProductCount - PageRows + 1, PageRows).Select(index => new RemoteCatalogProductWrite
        { Barcode = Barcode(index), RemoteProductId = "load-remote-" + index.ToString("D8", Invariant), Name = "Load Product " + index.ToString("D8", Invariant) + " generation " + page,
            UnitPrice = 1000 + page, StockQuantity = 1000.125m, RemoteUpdatedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero).AddSeconds(page).ToString("O", Invariant) }).ToArray() };
        private static string Barcode(int index) => "LOAD" + index.ToString("D8", Invariant);
        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static void Require(bool value, string message) => FunctionalCompletionSmoke.Require(value, message);
        private static async Task WithinBudget(Task task, Stopwatch budget)
        {
            var remaining = WorkBudget - budget.Elapsed;
            Require(remaining > TimeSpan.Zero, "finite synthetic load budget exhausted");
            using var timeout = new CancellationTokenSource();
            try { Require(await Task.WhenAny(task, Task.Delay(remaining, timeout.Token)).ConfigureAwait(true) == task, "finite synthetic load budget exhausted"); }
            finally { timeout.Cancel(); }
            await task.ConfigureAwait(true);
        }
        private static long PrivateBytes() { using var process = Process.GetCurrentProcess(); process.Refresh(); return process.PrivateMemorySize64; }
        private static void Maximum(ref long target, long value) { long prior; do { prior = Interlocked.Read(ref target); if (value <= prior) return; } while (Interlocked.CompareExchange(ref target, value, prior) != prior); }
        private static Sample BeginSample(string kind, int ordinal, Observation state) => new Sample { Kind = kind, Ordinal = ordinal, Start = Stopwatch.GetTimestamp(), CatalogAtStart = Volatile.Read(ref state.CatalogActive), BackupAtStart = Volatile.Read(ref state.BackupActive) };
        private static void FinishSample(Sample sample, Observation state) { sample.End = Stopwatch.GetTimestamp(); sample.CatalogAtEnd = Volatile.Read(ref state.CatalogActive); sample.BackupAtEnd = Volatile.Read(ref state.BackupActive); }
        private static void CheckBusiness(SqliteConnectionFactory factory, Observation state, bool live)
        {
            using var connection = factory.Open();
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE barcode LIKE 'LOAD%'") == ProductCount, "load snapshot lost catalog rows");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == state.SalesBefore && connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox") == state.OutboxBefore, "preview changed committed sales or outbox");
            var price = connection.QuerySingle<PriceEvidence>("SELECT COUNT(*) AS Rows,MIN(unitPrice) AS Minimum,MAX(unitPrice) AS Maximum FROM products WHERE barcode >= 'LOAD00099501' AND barcode <= 'LOAD00100000'");
            Require(price.Rows == PageRows && price.Minimum == price.Maximum && price.Minimum >= 1001 && price.Maximum <= 1000 + PageCount && (!live || price.Minimum == 1000 + PageCount), "snapshot contains a partial catalog page generation");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE barcode >= 'LOAD00099501' AND barcode <= 'LOAD00100000' AND stock_qty=1000.125") == PageRows, "fractional stock differs in load snapshot");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM article_product_remote_shadow WHERE barcode >= 'LOAD00099501' AND barcode <= 'LOAD00100000' AND retail_price=@price AND stock_quantity=1000.125", new { price = price.Minimum }) == PageRows, "remote shadow differs from committed page");
            if (!live) state.SnapshotGeneration = price.Minimum - 1000;
        }
        private static void WriteEvidence(string directory, List<Sample> samples, Observation state, double elapsed, bool passed)
        {
            Sample[] rows; lock (samples) rows = samples.OrderBy(sample => sample.Start).ToArray();
            Tuple<long, long>[] copies; lock (state.NativeIntervals) copies = state.NativeIntervals.ToArray();
            var csv = new StringBuilder("kind,ordinal,ms,start_tick,end_tick,catalog_active_start,catalog_active_end,backup_active_start,backup_active_end,native_copy_interval_overlap,product_commands,product_commands_on_ui\n");
            foreach (var row in rows) csv.AppendFormat(Invariant, "{0},{1},{2:F3},{3},{4},{5},{6},{7},{8},{9},{10},{11}\n", row.Kind, row.Ordinal, row.Milliseconds, row.Start, row.End, row.CatalogAtStart, row.CatalogAtEnd, row.BackupAtStart, row.BackupAtEnd, copies.Any(copy => copy.Item1 <= row.End && row.Start <= copy.Item2) ? 1 : 0, row.ProductCommands, row.CommandsOnUi);
            File.WriteAllText(Path.Combine(directory, "sync-backup-load-samples.csv"), csv.ToString(), new UTF8Encoding(false));
            var receipt = new StringBuilder("{\"schemaVersion\":1,\"scope\":\"synthetic_local_x86_ui_catalog_apply_and_backup\",\"behaviorIntegrityPassed\":" + (passed ? "true" : "false") + ",\"performanceThresholdsApplied\":false,\"liveBackendExecuted\":false,\"integratedStabilityQualified\":false,\"physicalOutputExecuted\":false,\"singleFlightScope\":\"observed_fixture_workers_and_run_context_guard_only\",\"peakMemoryScope\":\"observed_50ms_samples_plus_ui_endpoints_after_fixture_setup\",\"quantileMethod\":\"nearest_rank_all_samples_first_call_retained\"");
            foreach (var metric in new Dictionary<string, long> { ["products"] = ProductCount, ["declaredPageLimit"] = PageCount, ["rowsPerPage"] = PageRows, ["declaredUiSamples"] = UiSamples, ["budgetSeconds"] = 45, ["pagesCommitted"] = state.PagesCommitted, ["runCommittedTransactions"] = state.RunTransactions, ["backupsPublished"] = state.BackupsPublished, ["scansCompleted"] = state.ScansCompleted, ["paymentPreviewsCompleted"] = state.PaymentsCompleted, ["catalogWorkerPeak"] = state.CatalogPeak, ["backupWorkerPeak"] = state.BackupPeak, ["uiBusyPeak"] = state.UiPeak, ["catalogGuardHeldSamples"] = state.GuardHeldSamples, ["nativeCopyCalls"] = state.NativeCalls, ["snapshotPageGeneration"] = state.SnapshotGeneration, ["privateBeforeBytes"] = state.PrivateBefore, ["privateObservedPeakBytes"] = state.PrivatePeak, ["privateAfterCleanupBytes"] = state.PrivateEnd, ["memorySamples"] = state.MemorySamples, ["outputCallbackCalls"] = state.OutputCalls }) receipt.Append(',').Append('"').Append(metric.Key).Append("\":").Append(metric.Value.ToString(Invariant));
            receipt.Append(",\"elapsedMs\":").Append(elapsed.ToString("F3", Invariant));
            foreach (var kind in new[] { "public_scan", "payment_preview", "dispatcher_input_probe", "catalog_page", "validated_backup" })
            {
                var values = rows.Where(sample => sample.Kind == kind).Select(sample => sample.Milliseconds).OrderBy(value => value).ToArray();
                receipt.AppendFormat(Invariant, ",\"{0}\":{{\"samples\":{1},\"p50Ms\":{2:F3},\"p95Ms\":{3:F3},\"maxMs\":{4:F3}", kind, values.Length, Quantile(values, .50), Quantile(values, .95), values.Length == 0 ? 0 : values[values.Length - 1]);
                receipt.Append('}');
            }
            receipt.Append(",\"nativeCopyBoundaryNote\":\"initial barriers are explicit; operation overlap and measured native-copy interval overlap are separate; synchronous native copy cannot be interrupted mid-step\"}");
            var json = receipt.ToString();
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max))
                while (reader.Read()) { }
            File.WriteAllText(Path.Combine(directory, "sync-backup-load.json"), json, new UTF8Encoding(false));
        }
        private static double Quantile(double[] sorted, double fraction) => sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)];
        private sealed class PriceEvidence { public long Rows { get; set; } public long Minimum { get; set; } public long Maximum { get; set; } }
        private sealed class Sample
        {
            internal string Kind; internal int Ordinal, CatalogAtStart, CatalogAtEnd, BackupAtStart, BackupAtEnd, ProductCommands, CommandsOnUi;
            internal long Start, End; internal double Milliseconds => (End - Start) * 1000d / Stopwatch.Frequency;
        }
        private sealed class Observation
        {
            internal int CatalogActive, BackupActive, UiActive, PagesCommitted, BackupsPublished, ScansCompleted, PaymentsCompleted, MemorySamples, GuardHeldSamples, NativeCalls, OutputCalls;
            internal long CatalogPeak, BackupPeak, UiPeak, RunTransactions, SalesBefore, OutboxBefore, SnapshotGeneration, PrivateBefore, PrivatePeak, PrivateEnd;
            internal bool BehaviorPassed; internal SemaphoreSlim RunFlight;
            internal readonly List<Tuple<long, long>> NativeIntervals = new List<Tuple<long, long>>();
        }
    }
}
