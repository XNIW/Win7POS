using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Win7POS.Data;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class ProductPagingWpfSmoke
    {
        private const int RowCount = 100000;

        internal static async Task<string> RunAsync()
        {
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            Seed(factory);

            var dispatcherThread = Thread.CurrentThread.ManagedThreadId;
            var pulseCount = 0;
            var wrongThreadPulse = false;
            var timer = new DispatcherTimer(DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            timer.Tick += (_, __) =>
            {
                pulseCount++;
                wrongThreadPulse |= Thread.CurrentThread.ManagedThreadId != dispatcherThread;
            };

            var service = ProductsWorkflowService.CreateDefault();
            timer.Start();
            var stopwatch = Stopwatch.StartNew();
            var page = await service.LoadDetailsPageAsync(
                string.Empty,
                targetPage: 1,
                pageSize: 200).ConfigureAwait(true);
            stopwatch.Stop();
            timer.Stop();

            if (page.Items.Count != 200 || page.TotalCount != RowCount)
                return "FAIL product paging result mismatch.";
            if (pulseCount <= 0)
                return "FAIL WPF dispatcher did not pulse while product paging was in flight.";
            if (wrongThreadPulse)
                return "FAIL dispatcher pulse ran on an unexpected thread.";

            return "PASS product paging dispatcher remained responsive; " +
                   "rows=" + RowCount.ToString() +
                   " pulses=" + pulseCount.ToString() +
                   " elapsed_ms=" + stopwatch.ElapsedMilliseconds.ToString() + ".";
        }

        private static void Seed(SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO products(id, barcode, name, unitPrice, is_active)
VALUES($id, $barcode, $name, $unit_price, 1);
INSERT INTO product_meta(barcode, stock_qty)
VALUES($barcode, 1);";
                var id = command.Parameters.Add("$id", SqliteType.Integer);
                var barcode = command.Parameters.Add("$barcode", SqliteType.Text);
                var name = command.Parameters.Add("$name", SqliteType.Text);
                var unitPrice = command.Parameters.Add("$unit_price", SqliteType.Integer);
                command.Prepare();

                for (var index = 1; index <= RowCount; index++)
                {
                    id.Value = index;
                    barcode.Value = "WPF-" + index.ToString("D8");
                    name.Value = "WPF Product " + index.ToString("D8");
                    unitPrice.Value = 100 + index;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }
    }
}
