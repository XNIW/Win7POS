using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Products;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class FunctionalCompletionSmoke
    {
        private static string _scenario;
        internal static async Task<string> RunAsync(string dataDir, bool performance, string productCount, string scenario)
        {
            _scenario = scenario;
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            if (performance) return await CartPerformanceSmoke.RunAsync(dataDir, int.TryParse(productCount, out var count) ? count : 20000);
            var results = new List<string>();
            await CheckAsync(results, "INTEGRATED_login_sale_retry_receipt_restart", FunctionalSaleSmoke.RunAsync);
            await CheckAsync(results, "F06_reversed_preview_delete_close", HeldCartsViewModelSmoke.RunAsync);
            await CheckAsync(results, "F01_export_gate", async () =>
            {
                // A timed-out service is discarded; the harness process exits after this run.
                var service = new PosWorkflowService();
                var export = service.ExportDailyCsvAsync(new DateTime(2026, 9, 25));
                Require(await Task.WhenAny(export, Task.Delay(2000)) == export, "export deadlocked");
                Require(File.Exists(await export), "missing CSV");
                var exportPath = await export;
                File.Delete(exportPath);
                Directory.CreateDirectory(exportPath);
                var writeFailed = false;
                try { await service.ExportDailyCsvAsync(new DateTime(2026, 9, 25)); }
                catch (UnauthorizedAccessException) { writeFailed = true; }
                catch (IOException) { writeFailed = true; }
                finally { Directory.Delete(exportPath); }
                Require(writeFailed, "export file failure was not exercised");
                var next = service.GetSnapshotAsync();
                Require(await Task.WhenAny(next, Task.Delay(2000)) == next, "gate not released");
                await next;
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    var denied = false;
                    try { await service.ExportDailyCsvAsync(DateTime.Today, cancelled.Token); }
                    catch (OperationCanceledException) { denied = true; }
                    Require(denied, "export ignored cancellation");
                    await service.GetSnapshotAsync();
                }
                await Task.WhenAll(service.ExportDailyCsvAsync(DateTime.Today), service.ExportDailyCsvAsync(DateTime.Today), service.GetPrinterSettingsAsync());
            });
            await CheckAsync(results, "F07_settings_rollback", async () =>
            {
                var service = new PosWorkflowService();
                await service.SetPrinterSettingsAsync(new PosPrinterSettings { PrinterName = "before", Copies = 1, CashDrawerMode = "disabled" });
                var factory = new SqliteConnectionFactory(options);
                using (var conn = factory.Open())
                    conn.Execute(@"CREATE TRIGGER fail_printer_setting BEFORE UPDATE ON app_settings
WHEN NEW.key = 'printer.copies' BEGIN SELECT RAISE(ABORT, 'injected settings failure'); END;");
                var failed = false;
                try { await service.SetPrinterSettingsAsync(new PosPrinterSettings { PrinterName = "after", Copies = 2, CashDrawerMode = "disabled" }); }
                catch { failed = true; }
                using (var conn = factory.Open()) conn.Execute("DROP TRIGGER fail_printer_setting");
                Require(failed, "fault not injected");
                var actual = await service.GetPrinterSettingsAsync();
                Require(actual.PrinterName == "before" && actual.Copies == 1, "partially saved printer settings");
            });
            await CheckAsync(results, "F02_F03_workflow_stable_identity_batch_dispatcher", async () =>
            {
                var factory = new SqliteConnectionFactory(options);
                using (var conn = factory.Open())
                    conn.Execute("INSERT INTO products(barcode,name,unitPrice,is_active) VALUES('A','A',1000,1),('B','B',1000,1)");
                var service = new PosWorkflowService();
                await service.AddByBarcodeAsync("A");
                await service.ApplyCartDiscountPercentAsync(10);
                PosWorkflowSnapshot snapshot;
                using (var metrics = SqliteWorkMetrics.Begin())
                {
                    snapshot = await service.AddByBarcodeAsync("B");
                    Require(metrics.ProductCommands == 2 && metrics.Connections == 2, "scan query count is not constant");
                    Require(metrics.ProductCommandsOnCallingThread == 0, "SQLite ran on dispatcher");
                }
                Require(snapshot.Total == 1800, "workflow rewrote discount total");
                var older = snapshot;
                var bKey = snapshot.Lines.Single(x => x.Barcode == "B").LineKey;
                await service.RemoveLineAsync("A");
                snapshot = await service.SetQtyByLineAsync(bKey, 2);
                Require(snapshot.Lines.Single(x => x.Barcode == "B").Quantity == 2, "stable key missed B");
                await service.RemoveLineAsync("B");
                await service.AddByBarcodeAsync("B");
                snapshot = await service.SetQtyByLineAsync(bKey, 9);
                Require(snapshot.Lines.Single(x => x.Barcode == "B").Quantity == 1, "stale key changed a new line");
                using (var vm = new PosViewModel(service))
                {
                    vm.ApplyDiscountSnapshot(snapshot);
                    var rowNotifications = 0;
                    vm.CartItems[0].PropertyChanged += (_, __) => rowNotifications++;
                    vm.ApplyDiscountSnapshot(snapshot);
                    Require(rowNotifications == 0, "unchanged snapshot invalidated rendered cart rows");
                    vm.ApplyDiscountSnapshot(older);
                    Require(vm.CartItems.Count == 1 && vm.CartItems[0].Barcode == "B", "stale snapshot replaced current cart");
                }
            });
            await CheckAsync(results, "F05_same_clock_recovery_restart_active_cart", async () =>
            {
                using (var conn = new SqliteConnectionFactory(options).Open())
                    conn.Execute("INSERT OR IGNORE INTO products(barcode,name,unitPrice,is_active) VALUES('A','A',1000,1),('B','B',1000,1)");
                var fixedClock = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
                var service = new PosWorkflowService(() => fixedClock);
                await service.AddByBarcodeAsync("A");
                await service.ApplyLineDiscountByFinalPriceAsync("A", 650);
                var first = await service.SuspendCartAsync();
                await service.AddByBarcodeAsync("B");
                var second = await service.SuspendCartAsync();
                Require(first.HoldId != second.HoldId, "same-clock hold collision");
                await service.AddByBarcodeAsync("B");
                var refused = false;
                try { await service.RecoverHeldCartAsync(first.HoldId); } catch (InvalidOperationException) { refused = true; }
                Require(refused && (await service.GetSnapshotAsync()).Total == 1000, "active cart overwritten");
                await service.ClearCartAsync();
                var recovered = await service.RecoverHeldCartAsync(first.HoldId);
                Require(recovered.Total == 650, "held economics lost");
                var holds = new HeldCartRepository(new SqliteConnectionFactory(options));
                Require((await holds.ListHoldsAsync()).Any(x => x.HoldId == first.HoldId), "recovery deleted durable hold");
                var restarted = new PosWorkflowService();
                Require((await restarted.RecoverHeldCartAsync(first.HoldId)).Total == 650, "restart cannot recover hold");
                var session = (PosSession)typeof(PosWorkflowService).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(restarted);
                Require(session.Lines.Single(x => x.Barcode == "A").ProductId.HasValue, "product identity missing");
                await restarted.SetQtyAsync("A", 2);
                var resuspended = await restarted.SuspendCartAsync();
                Require(resuspended.HoldId == first.HoldId && (await holds.LoadHoldLinesAsync(first.HoldId)).Single(x => x.Barcode == "A").Qty == 2, "resuspend duplicated or lost edited hold");
                var factory = new SqliteConnectionFactory(options);
                using (var conn = factory.Open()) conn.Execute("UPDATE products SET is_active=0 WHERE barcode='A'");
                var missingRefused = false;
                try { await restarted.RecoverHeldCartAsync(first.HoldId); } catch (InvalidOperationException) { missingRefused = true; }
                Require(missingRefused && (await holds.ListHoldsAsync()).Any(x => x.HoldId == first.HoldId), "disabled product lost hold");
                await new SettingsRepository(factory).SetStringAsync("pos.catalog.bound_shop_id", "different-shop");
                Require((await holds.ListHoldsAsync()).Count == 0, "foreign shop exposed holds");
                var foreignRefused = false;
                try { await holds.ClaimAsync(first.HoldId); } catch (InvalidOperationException) { foreignRefused = true; }
                Require(foreignRefused, "foreign shop recovered hold");
                foreignRefused = false;
                try { await holds.DeleteHoldAsync(first.HoldId); } catch (InvalidOperationException) { foreignRefused = true; }
                Require(foreignRefused, "stale foreign-shop selection deleted hold");
            });
            await CheckAsync(results, "F08_discount_preview_lifetime", async () =>
            {
                var vm = new DiscountViewModel("A", true, (_, __, ___, ____) => Task.FromResult(true),
                    new DiscountPreviewContext { Barcode = "A", Quantity = 3, OriginalUnitPrice = 101 });
                vm.ValueText = "50";
                Require(vm.PreviewFinalUnitPrice == 50 && vm.PreviewFinalLineTotal == 151, "half-peso preview differs from session");
                var weak = CreateDisposedDiscountViewModel();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Require(!weak.IsAlive, "discount view model retained by localization event");
                vm.Dispose();
                await Task.CompletedTask;
            });
            await CheckAsync(results, "P109_product_editor_late_render_close", async () =>
            {
                foreach (var closeDuringEvent in new[] { false, true })
                {
                    var model = new ProductEditViewModel(ProductEditMode.New, null, ProductsWorkflowService.CreateDefault());
                    var dialog = new ProductEditDialog(model) { Owner = DialogOwnerHelper.GetSafeOwner() };
                    try
                    {
                        dialog.Show();
                        if (closeDuringEvent) dialog.ContentRendered += (_, __) => dialog.Close();
                        else dialog.Close();
                        // Deliver the real virtual callback in both close/render orderings.
                        try
                        {
                            typeof(ProductEditDialog).GetMethod("OnContentRendered", BindingFlags.Instance | BindingFlags.NonPublic)
                                .Invoke(dialog, new object[] { EventArgs.Empty });
                        }
                        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
                        Require(!dialog.IsVisible, "closed product editor became visible again");
                    }
                    finally { dialog.Close(); }
                }
                await Task.CompletedTask;
            });
            return (results.Count == 0 || results.Any(x => x.StartsWith("FAIL")) ? "FAIL" : "PASS") + Environment.NewLine + string.Join(Environment.NewLine, results);
        }

        internal static async Task CheckAsync(List<string> results, string name, Func<Task> test)
        {
            if (!string.IsNullOrEmpty(_scenario) && !string.Equals(_scenario, name, StringComparison.Ordinal)) return;
            try { await test(); results.Add("PASS " + name); }
            catch (Exception ex) { results.Add("FAIL " + name + " " + ex.Message); }
        }

        internal static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference CreateDisposedDiscountViewModel()
        {
            var vm = new DiscountViewModel("A", true, (_, __, ___, ____) => Task.FromResult(true));
            var weak = new WeakReference(vm);
            vm.Dispose();
            return weak;
        }

    }
}
