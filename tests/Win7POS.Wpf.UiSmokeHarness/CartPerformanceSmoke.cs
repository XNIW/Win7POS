using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using Win7POS.Core.Models;
using Win7POS.Core.Util;
using Dapper;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class CartPerformanceSmoke
    {
        internal static async Task<string> RunAsync(string dataDir, int count)
        {
            DbInitializer.EnsureCreated(PosDbOptions.Default());
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            using (var conn = factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                conn.Execute(@"WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<@count)
INSERT INTO products(id,barcode,name,unitPrice,is_active) SELECT x,printf('P%08d',x),printf('Product %08d',x),1000,1 FROM n;
INSERT INTO product_meta(barcode,stock_qty) SELECT barcode,100000 FROM products;", new { count }, tx);
                conn.Execute(@"WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10)
INSERT INTO product_price_history(barcode,timestamp,type,old_price,new_price,source)
SELECT barcode,printf('2026-09-%02d',x),'retail',900,1000,'synthetic-functional-performance' FROM products CROSS JOIN n;", transaction: tx);
                tx.Commit();
            }
            var text = new StringBuilder("products,cart,operation,sample,ms,private_bytes,gc0,gc1,gc2,dispatcher_probe_ms,connections,product_commands,commands_on_dispatcher\n");
            foreach (var size in new[] { 1, 10, 50, 100, 500 })
            {
                var service = new PosWorkflowService();
                var session = (PosSession)typeof(PosWorkflowService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                session.ReplaceWithLines(Enumerable.Range(1, size).Select(i => new RestoredLine
                { ProductId = i, Barcode = "P" + i.ToString("D8"), Name = "Product " + i, UnitPrice = 1000, Quantity = 1 }).ToList());
                for (var sample = 0; sample < 31; sample++)
                {
                    var watch = Stopwatch.StartNew();
                    double probeMs = 0;
                    var probe = Dispatcher.CurrentDispatcher.InvokeAsync(() => probeMs = watch.Elapsed.TotalMilliseconds, DispatcherPriority.Send);
                    using var metrics = SqliteWorkMetrics.Begin();
                    await service.AddByBarcodeAsync("P00000001");
                    var ms = watch.Elapsed.TotalMilliseconds;
                    await probe.Task;
                    text.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},scan,{2},{3:F3},{4},{5},{6},{7},{8:F3},{9},{10},{11}\n",
                        count, size, sample, ms, Process.GetCurrentProcess().PrivateMemorySize64,
                        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), probeMs,
                        metrics.Connections, metrics.ProductCommands, metrics.ProductCommandsOnCallingThread);
                }
            }
            File.WriteAllText(Path.Combine(dataDir, "cart-performance.csv"), text.ToString());
            await MeasureOtherFlowsAsync(dataDir, count, factory);
            var soakText = Environment.GetEnvironmentVariable("WIN7POS_QA_SOAK_MINUTES");
            if (!string.IsNullOrEmpty(soakText))
            {
                if (!int.TryParse(soakText, out var minutes) || minutes < 1 || minutes > 120)
                    throw new ArgumentException("WIN7POS_QA_SOAK_MINUTES must be 1..120.");
                await MeasureSoakAsync(dataDir, count, minutes);
            }
            return "PASS measurements recorded; sample 0 is first-call, samples 1-30 warm; no physical hardware used.";
        }

        private static async Task MeasureOtherFlowsAsync(string dataDir, int count, SqliteConnectionFactory factory)
        {
            var output = new StringBuilder("products,operation,sample,ms,private_bytes,gc0,gc1,gc2\n");
            void Record(string operation, int sample, Stopwatch watch)
            {
                output.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F3},{4},{5},{6},{7}\n",
                    count, operation, sample, watch.Elapsed.TotalMilliseconds,
                    Process.GetCurrentProcess().PrivateMemorySize64, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
            var service = new PosWorkflowService();
            var stages = new StringBuilder("products,sample,service_ms,apply_ms,layout_ms,bitmap_allocate_ms,bitmap_render_ms\n");
            using (var vm = new PosViewModel(service))
            {
                var watch = Stopwatch.StartNew();
                var view = new PosView();
                (view.DataContext as IDisposable)?.Dispose();
                view.DataContext = vm;
                var host = new Window { Width = 1024, Height = 768, Content = view, ShowInTaskbar = false };
                try
                {
                    host.Show();
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    while (vm.IsBusy) await Task.Delay(1);
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Record("pos_view_entry_first_in_process", 0, watch);
                    var session = (PosSession)typeof(PosWorkflowService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                    session.ReplaceWithLines(Enumerable.Range(1, 500).Select(i => new RestoredLine
                    { ProductId = i, Barcode = "P" + i.ToString("D8"), Name = "Product " + i, UnitPrice = 1000, Quantity = 1 }).ToList());
                    vm.ApplyDiscountSnapshot(await service.GetSnapshotAsync());
                    for (var sample = 0; sample < 31; sample++)
                    {
                        watch.Restart();
                        var snapshot = await service.AddByBarcodeAsync("P00000001");
                        var serviceMs = watch.Elapsed.TotalMilliseconds;
                        vm.ApplyDiscountSnapshot(snapshot);
                        var applyMs = watch.Elapsed.TotalMilliseconds;
                        view.UpdateLayout();
                        var layoutMs = watch.Elapsed.TotalMilliseconds;
                        // Deterministic offscreen rendering also works on locked/occluded
                        // desktops. This is a rendered-view measurement, not monitor latency.
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1024, 768, 96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32);
                        var allocateMs = watch.Elapsed.TotalMilliseconds;
                        bitmap.Render(view);
                        var renderMs = watch.Elapsed.TotalMilliseconds;
                        stages.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2:F3},{3:F3},{4:F3},{5:F3},{6:F3}\n",
                            count, sample, serviceMs, applyMs - serviceMs, layoutMs - applyMs,
                            allocateMs - layoutMs, renderMs - allocateMs);
                        Record("scan_500_view_bitmap", sample, watch);
                    }
                }
                finally { host.Close(); }
            }
            var products = new ProductRepository(factory);
            var sales = new SaleRepository(factory);
            await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
            { ShopCode = "PERF-SHOP", ShopId = "synthetic-performance-shop", ShopName = "Synthetic performance fixture" });
            for (var sample = 0; sample < 31; sample++)
            {
                var watch = Stopwatch.StartNew();
                await products.SearchDetailsAsync("Product 000", 100);
                Record("search_details_100", sample, watch);
                var sale = new Sale { Code = "PERF-" + Guid.NewGuid().ToString("N"), CreatedAt = UnixTime.NowMs(),
                    Kind = (int)SaleKind.Sale, Total = 1000, PaidCash = 1000 };
                watch.Restart();
                await sales.InsertSaleAsync(sale, new[] { new SaleLine { Barcode = "P00000001", ProductId = 1,
                    Name = "Product 1", UnitPrice = 1000, Quantity = 1, LineTotal = 1000 } });
                Record("local_sale_transaction_no_network", sample, watch);
                watch.Restart();
                await service.GetDailyCsvContentAsync(DateTime.Today);
                Record("daily_csv_1_to_31_sales", sample, watch);
            }
            File.WriteAllText(Path.Combine(dataDir, "flow-performance.csv"), output.ToString());
            File.WriteAllText(Path.Combine(dataDir, "render-stages.csv"), stages.ToString());
        }

        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, int flags);

        // Test-only finite soak. No forced collection, hardware output or remote requests.
        private static async Task MeasureSoakAsync(string dataDir, int count, int minutes)
        {
            using var process = Process.GetCurrentProcess();
            using var csv = new StreamWriter(Path.Combine(dataDir, "cart-soak.csv"), false) { AutoFlush = true };
            csv.WriteLine("products,cycle,phase,elapsed_s,private_bytes,managed_bytes,handles,gdi,user,threads,gc0,gc1,gc2,dispatcher_ms,pending_dispatcher,cache_entries,image_lookups,scan_ms,render_ms");
            var elapsed = Stopwatch.StartNew();
            var dispatcher = Dispatcher.CurrentDispatcher;
            // Hooks can race for synchronous Send operations. Never retain the
            // operations (and their closures) just to observe queue occupancy.
            var pending = new List<WeakReference>();
            DispatcherHookEventHandler posted = (s, e) => { lock (pending) pending.Add(new WeakReference(e.Operation)); };
            DispatcherHookEventHandler finished = (s, e) =>
            {
                lock (pending) pending.RemoveAll(reference => !reference.IsAlive || ReferenceEquals(reference.Target, e.Operation));
            };
            dispatcher.Hooks.OperationPosted += posted;
            dispatcher.Hooks.OperationCompleted += finished;
            dispatcher.Hooks.OperationAborted += finished;
            var service = new PosWorkflowService();
            using var vm = new PosViewModel(service);
            var view = new PosView();
            (view.DataContext as IDisposable)?.Dispose();
            view.DataContext = vm;
            var host = new Window { Width = 1024, Height = 768, Content = view, ShowInTaskbar = false };
            int CollectionCount(string field)
            {
                var value = typeof(PosViewModel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(vm);
                return (int)value.GetType().GetProperty("Count").GetValue(value);
            }
            async Task Record(int cycle, string phase, double scanMs = 0, double renderMs = 0)
            {
                var probe = Stopwatch.StartNew();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send);
                var probeMs = probe.Elapsed.TotalMilliseconds;
                int pendingCount;
                lock (pending)
                {
                    pending.RemoveAll(reference => !(reference.Target is DispatcherOperation operation) ||
                        operation.Status == DispatcherOperationStatus.Completed || operation.Status == DispatcherOperationStatus.Aborted);
                    pendingCount = pending.Select(reference => reference.Target).Where(operation => operation != null).Distinct().Count();
                }
                process.Refresh();
                csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3:F3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13:F3},{14},{15},{16},{17:F3},{18:F3}",
                    count, cycle, phase, elapsed.Elapsed.TotalSeconds, process.PrivateMemorySize64,
                    GC.GetTotalMemory(false), process.HandleCount, GetGuiResources(process.Handle, 0),
                    GetGuiResources(process.Handle, 1), process.Threads.Count, GC.CollectionCount(0),
                    GC.CollectionCount(1), GC.CollectionCount(2), probeMs, pendingCount,
                    CollectionCount("_cartProductImageCache"), CollectionCount("_cartProductImageLookups"), scanMs, renderMs));
            }
            try
            {
                host.Show();
                while (vm.IsBusy) await Task.Delay(10);
                var session = (PosSession)typeof(PosWorkflowService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                for (var cycle = 0; elapsed.Elapsed < TimeSpan.FromMinutes(minutes); cycle++)
                {
                    session.ReplaceWithLines(Enumerable.Range(1, 500).Select(i => new RestoredLine
                    { ProductId = i, Barcode = "P" + i.ToString("D8"), Name = "Product " + i, UnitPrice = 1000, Quantity = 1 }).ToList());
                    vm.ApplyDiscountSnapshot(await service.GetSnapshotAsync());
                    foreach (var mode in new[] { CartViewMode.Rows, CartViewMode.Grid })
                    {
                        await vm.SetCartViewModeAsync(mode);
                        for (var scan = 0; scan < 10; scan++)
                        {
                            var watch = Stopwatch.StartNew();
                            vm.ApplyDiscountSnapshot(await service.AddByBarcodeAsync("P00000001"));
                            var scanMs = watch.Elapsed.TotalMilliseconds;
                            view.UpdateLayout();
                            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1024, 768, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                            bitmap.Render(view);
                            await Record(cycle, mode.ToString(), scanMs, watch.Elapsed.TotalMilliseconds - scanMs);
                        }
                    }
                    var dialog = new Win7POS.Wpf.Pos.Dialogs.DiscountDialog(null, true, service, vm, 100, () => Task.FromResult(false))
                    { Owner = Win7POS.Wpf.Infrastructure.DialogOwnerHelper.GetSafeOwner() };
                    dialog.Show();
                    dialog.UpdateLayout();
                    dialog.Close();
                    // Exercise real image presenters and editor lifetime using synthetic images.
                    var imageResult = await ProductImageUiWpfSmoke.RunAsync(Path.Combine(dataDir, "soak-images"));
                    if (!imageResult.StartsWith("PASS", StringComparison.Ordinal)) throw new InvalidOperationException(imageResult);
                    await Record(cycle, "before_idle");
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    await Record(cycle, "after_idle");
                }
            }
            finally
            {
                host.Close();
                dispatcher.Hooks.OperationPosted -= posted;
                dispatcher.Hooks.OperationCompleted -= finished;
                dispatcher.Hooks.OperationAborted -= finished;
            }
        }
    }
}
