using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
                        vm.ApplyDiscountSnapshot(snapshot);
                        view.UpdateLayout();
                        // Deterministic offscreen rendering also works on locked/occluded
                        // desktops. This is a rendered-view measurement, not monitor latency.
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1024, 768, 96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(view);
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
        }
    }
}
