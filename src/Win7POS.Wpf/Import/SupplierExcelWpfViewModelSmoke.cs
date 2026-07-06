using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Win7POS.Core;
using Win7POS.Data;

namespace Win7POS.Wpf.Import
{
    internal static class SupplierExcelWpfViewModelSmoke
    {
        private const string Flag = "--supplier-excel-wpf-viewmodel-smoke";

        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if ((args ?? Array.Empty<string>()).All(arg => !string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            try
            {
                exitCode = RunWithDispatcherPump(RunAsync(args ?? Array.Empty<string>()));
            }
            catch (Exception ex)
            {
                var reportPath = ValueAfter(args, "--report");
                WriteReport(reportPath, "FAIL", ex.ToString());
                exitCode = 1;
            }

            return true;
        }

        public static void PrepareDataDirFromCommandLine(string[] commandLineArgs)
        {
            var dataDir = ValueAfter((commandLineArgs ?? Array.Empty<string>()).Skip(1).ToArray(), "--data-dir");
            if (!string.IsNullOrWhiteSpace(dataDir))
            {
                Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", dataDir);
            }
        }

        private static int RunWithDispatcherPump(Task<int> task)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var source = FirstNonEmpty(ValueAfter(args, "--source"), "Products");
            var format = FirstNonEmpty(ValueAfter(args, "--format"), "xlsx").ToLowerInvariant();
            var dataDir = FirstNonEmpty(
                ValueAfter(args, "--data-dir"),
                Environment.GetEnvironmentVariable("WIN7POS_DATA_DIR"));
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                dataDir = Path.Combine(Path.GetTempPath(), "Win7POS", "wpf-smoke-" + Guid.NewGuid().ToString("N"));
            }

            Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", dataDir);
            Directory.CreateDirectory(dataDir);
            AppPaths.EnsureCreated();

            var filePath = ValueAfter(args, "--file");
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = Path.Combine(dataDir, "supplier-smoke-" + source + "." + format);
                CreateSupplierFixture(filePath, format, source);
            }

            DbInitializer.EnsureCreated(PosDbOptions.Default());

            var viewModel = new SupplierExcelImportViewModel(
                new SupplierExcelImportWorkflowService(),
                new SmokeFileDialogService(filePath),
                new SmokeCompletionDialogService());
            var result = await viewModel.RunSmokeAsync().ConfigureAwait(true);
            var barcode = BuildBarcode(source, format);
            var proof = ReadProof(barcode);
            var ok = result.CatalogImportOutboxId > 0 &&
                File.Exists(result.BackupPath) &&
                proof.ProductRows == 1 &&
                proof.ImportPriceHistoryRows > 0 &&
                proof.OutboxRows > 0;

            var report = new StringBuilder();
            report.AppendLine("status=" + (ok ? "PASS" : "FAIL"));
            report.AppendLine("source=" + source);
            report.AppendLine("format=" + format);
            report.AppendLine("file=" + Path.GetFileName(filePath));
            report.AppendLine("dataDir=" + dataDir);
            report.AppendLine("backupCreated=" + File.Exists(result.BackupPath).ToString(CultureInfo.InvariantCulture));
            report.AppendLine("outboxId=" + result.CatalogImportOutboxId.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("outboxStatus=" + result.CatalogImportOutboxStatus);
            report.AppendLine("productRows=" + proof.ProductRows.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("importPriceHistoryRows=" + proof.ImportPriceHistoryRows.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("outboxRows=" + proof.OutboxRows.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("inserted=" + result.Inserted.ToString(CultureInfo.InvariantCulture));
            report.AppendLine("updated=" + result.Updated.ToString(CultureInfo.InvariantCulture));
            WriteReport(ValueAfter(args, "--report"), ok ? "PASS" : "FAIL", report.ToString());

            return ok ? 0 : 1;
        }

        private static SupplierSmokeDbProof ReadProof(string barcode)
        {
            using (var conn = new SqliteConnection("Data Source=" + AppPaths.DbPath))
            {
                conn.Open();
                return new SupplierSmokeDbProof
                {
                    ProductRows = ExecuteLong(conn, "SELECT COUNT(1) FROM products WHERE barcode = @barcode", barcode),
                    ImportPriceHistoryRows = ExecuteLong(conn, "SELECT COUNT(1) FROM product_price_history WHERE barcode = @barcode AND source = 'IMPORT'", barcode),
                    OutboxRows = ExecuteLong(conn, "SELECT COUNT(1) FROM catalog_import_outbox WHERE status IN ('pending', 'acked')", barcode)
                };
            }
        }

        private static long ExecuteLong(SqliteConnection conn, string sql, string barcode)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@barcode", barcode);
                var value = cmd.ExecuteScalar();
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        private static void CreateSupplierFixture(string path, string format, string source)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (string.Equals(format, "xls", StringComparison.OrdinalIgnoreCase))
            {
                var html = "<html><body><table>" +
                    "<tr><th>barcode</th><th>productName</th><th>purchasePrice</th><th>retailPrice</th><th>quantity</th><th>supplier</th><th>category</th></tr>" +
                    "<tr><td>" + BuildBarcode(source, format) + "</td><td>Smoke " + source + " " + format + "</td><td>100</td><td>180</td><td>3</td><td>Smoke Supplier</td><td>Smoke Category</td></tr>" +
                    "</table></body></html>";
                File.WriteAllText(path, html, Encoding.UTF8);
                return;
            }

            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.Worksheets.Add("Supplier");
                sheet.Cell(1, 1).Value = "barcode";
                sheet.Cell(1, 2).Value = "productName";
                sheet.Cell(1, 3).Value = "purchasePrice";
                sheet.Cell(1, 4).Value = "retailPrice";
                sheet.Cell(1, 5).Value = "quantity";
                sheet.Cell(1, 6).Value = "supplier";
                sheet.Cell(1, 7).Value = "category";
                sheet.Cell(2, 1).Value = BuildBarcode(source, format);
                sheet.Cell(2, 2).Value = "Smoke " + source + " " + format;
                sheet.Cell(2, 3).Value = 100;
                sheet.Cell(2, 4).Value = 180;
                sheet.Cell(2, 5).Value = 3;
                sheet.Cell(2, 6).Value = "Smoke Supplier";
                sheet.Cell(2, 7).Value = "Smoke Category";
                workbook.SaveAs(path);
            }
        }

        private static string BuildBarcode(string source, string format)
        {
            var safeSource = new string((source ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            var safeFormat = new string((format ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            return "SMOKE-" + safeSource + "-" + safeFormat;
        }

        private static string ValueAfter(string[] args, string key)
        {
            for (var i = 0; i < (args ?? Array.Empty<string>()).Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return string.Empty;
        }

        private static string FirstNonEmpty(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left) ? (right ?? string.Empty) : left.Trim();
        }

        private static void WriteReport(string reportPath, string status, string body)
        {
            var path = string.IsNullOrWhiteSpace(reportPath)
                ? Path.Combine(Path.GetTempPath(), "Win7POS", "supplier-excel-wpf-smoke.txt")
                : reportPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, "supplier_excel_wpf_viewmodel_smoke=" + status + Environment.NewLine + body, Encoding.UTF8);
        }

        private sealed class SmokeFileDialogService : ISupplierExcelFileDialogService
        {
            private readonly string _path;

            public SmokeFileDialogService(string path)
            {
                _path = path;
            }

            public string SelectSupplierExcelFile()
            {
                return _path;
            }
        }

        private sealed class SmokeCompletionDialogService : ISupplierExcelCompletionDialogService
        {
            public void ShowCompletion(string title, string message)
            {
            }
        }

        private sealed class SupplierSmokeDbProof
        {
            public long ImportPriceHistoryRows { get; set; }
            public long OutboxRows { get; set; }
            public long ProductRows { get; set; }
        }
    }
}
