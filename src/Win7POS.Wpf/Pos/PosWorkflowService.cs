using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Core.Util;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos
{
    public sealed class PosWorkflowService
    {
        private const string KeyPrinterName = "printer.name";
        private const string KeyPrinterCopies = "printer.copies";
        private const string KeyAutoPrint = "pos.autoPrint";
        private const string KeySaveReceiptCopy = "printer.saveReceiptCopy";
        private const string KeyReceiptOutputDirectory = "printer.outputDirectory";

        private readonly FileLogger _logger = new FileLogger();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private readonly ProductRepository _products;
        private readonly SaleRepository _sales;
        private readonly SettingsRepository _settings;
        private readonly PosSession _session;
        private readonly PosDbOptions _options;

        private SaleCompleted _lastCompletedSale;

        public PosWorkflowService()
        {
            _options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(_options);

            var factory = new SqliteConnectionFactory(_options);
            _products = new ProductRepository(factory);
            _sales = new SaleRepository(factory);
            _settings = new SettingsRepository(factory);
            _session = new PosSession(new DataProductLookup(_products), new DataSalesStore(_sales));
        }

        public async Task<bool?> GetUseReceipt42Async()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _settings.GetBoolAsync("pos.useReceipt42").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetUseReceipt42Async(bool value)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settings.SetBoolAsync("pos.useReceipt42", value).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosPrinterSettings> GetPrinterSettingsAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var printerName = await _settings.GetStringAsync(KeyPrinterName).ConfigureAwait(false) ?? string.Empty;
                var copies = await _settings.GetIntAsync(KeyPrinterCopies).ConfigureAwait(false) ?? 1;
                if (copies < 1) copies = 1;
                var autoPrint = await _settings.GetBoolAsync(KeyAutoPrint).ConfigureAwait(false);
                var saveCopy = await _settings.GetBoolAsync(KeySaveReceiptCopy).ConfigureAwait(false);
                var outputDir = await _settings.GetStringAsync(KeyReceiptOutputDirectory).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(outputDir))
                    outputDir = Path.Combine(AppPaths.DataDirectory, "receipts");

                return new PosPrinterSettings
                {
                    PrinterName = printerName,
                    Copies = copies,
                    AutoPrint = autoPrint ?? true,
                    SaveCopyToFile = saveCopy ?? false,
                    OutputDirectory = outputDir
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetPrinterSettingsAsync(PosPrinterSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var copies = settings.Copies < 1 ? 1 : settings.Copies;
                var outputDir = string.IsNullOrWhiteSpace(settings.OutputDirectory)
                    ? Path.Combine(AppPaths.DataDirectory, "receipts")
                    : settings.OutputDirectory;

                await _settings.SetStringAsync(KeyPrinterName, settings.PrinterName ?? string.Empty).ConfigureAwait(false);
                await _settings.SetIntAsync(KeyPrinterCopies, copies).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeyAutoPrint, settings.AutoPrint).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeySaveReceiptCopy, settings.SaveCopyToFile).ConfigureAwait(false);
                await _settings.SetStringAsync(KeyReceiptOutputDirectory, outputDir).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> GetAutoPrintAsync()
        {
            var settings = await GetPrinterSettingsAsync().ConfigureAwait(false);
            return settings.AutoPrint;
        }

        public async Task SetAutoPrintAsync(bool value)
        {
            var settings = await GetPrinterSettingsAsync().ConfigureAwait(false);
            settings.AutoPrint = value;
            await SetPrinterSettingsAsync(settings).ConfigureAwait(false);
        }

        public async Task<string> BackupDbAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DbInitializer.EnsureCreated(_options);
                AppPaths.EnsureCreated();

                var fileName = "pos_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db";
                var outputPath = Path.Combine(AppPaths.BackupsDirectory, fileName);
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDir))
                    Directory.CreateDirectory(outputDir);

                File.Copy(_options.DbPath, outputPath, true);
                _logger.LogInfo("POS DB backup created: " + outputPath);
                return outputPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS DB backup failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task InitializeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInfo("POS initialize start");
                await EnsureDemoProductsAsync().ConfigureAwait(false);
                _logger.LogInfo("POS initialize done");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS initialize failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> AddByBarcodeAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var code = (barcode ?? string.Empty).Trim();
                _logger.LogInfo("POS add barcode: " + code);
                await _session.AddByBarcodeAsync(code).ConfigureAwait(false);
                return BuildSnapshot("Item aggiunto.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS add barcode failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CreateProductAsync(string barcode, string name, int unitPriceMinor)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var code = (barcode ?? string.Empty).Trim();
                var productName = (name ?? string.Empty).Trim();
                if (code.Length == 0) throw new ArgumentException("barcode is empty");
                if (productName.Length == 0) throw new ArgumentException("name is empty");
                if (unitPriceMinor < 0) throw new ArgumentException("price is invalid");

                await _products.UpsertAsync(new Product
                {
                    Barcode = code,
                    Name = productName,
                    UnitPrice = unitPriceMinor
                }).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosPayResult> PayAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInfo("POS pay start");
                var completed = await _session.PayCashAsync().ConfigureAwait(false);
                _lastCompletedSale = completed;

                var preview = BuildReceiptPreview(completed, true);
                var snapshot = BuildSnapshot("Pagamento completato.");
                _logger.LogInfo("POS pay done: " + completed.Sale.Code);
                return new PosPayResult
                {
                    SaleCode = completed.Sale.Code,
                    ReceiptPreview = preview,
                    Snapshot = snapshot
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS pay failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosSaleResult> CompleteSaleAsync(PosPaymentInfo payment)
        {
            if (payment == null) throw new ArgumentNullException(nameof(payment));

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.Lines.Count == 0)
                    throw new PosException(PosErrorCode.EmptyCart);

                var total = _session.Total;
                if (!payment.IsValid(total))
                    throw new InvalidOperationException("Pagamento non valido.");

                var sale = new Sale
                {
                    Code = SaleCodeGenerator.NewCode("V"),
                    CreatedAt = UnixTime.NowMs(),
                    Total = total,
                    PaidCash = payment.CashAmountMinor,
                    PaidCard = payment.CardAmountMinor,
                    Change = payment.GetChangeMinor(total)
                };

                var saleLines = _session.Lines.Select(x => new SaleLine
                {
                    ProductId = x.ProductId,
                    Barcode = x.Barcode,
                    Name = x.Name,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    LineTotal = x.LineTotal
                }).ToList();

                var saleId = await _sales.InsertSaleAsync(sale, saleLines).ConfigureAwait(false);
                sale.Id = saleId;

                var completed = new SaleCompleted(sale, saleLines);
                _lastCompletedSale = completed;
                _session.Clear();
                var snapshot = BuildSnapshot("Vendita completata.");

                return new PosSaleResult
                {
                    SaleId = sale.Id,
                    SaleCode = sale.Code,
                    TotalMinor = sale.Total,
                    CreatedAtMs = sale.CreatedAt,
                    Receipt42 = BuildReceiptPreview(completed, true),
                    Receipt32 = BuildReceiptPreview(completed, false),
                    Snapshot = snapshot
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS complete sale failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> IncreaseQtyAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, barcode, StringComparison.Ordinal));
                if (line == null) return BuildSnapshot(string.Empty);
                _session.SetQuantity(line.Barcode, line.Quantity + 1);
                return BuildSnapshot("Quantita aumentata.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> DecreaseQtyAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, barcode, StringComparison.Ordinal));
                if (line == null) return BuildSnapshot(string.Empty);
                var next = line.Quantity - 1;
                if (next < 1) next = 1;
                _session.SetQuantity(line.Barcode, next);
                return BuildSnapshot("Quantita diminuita.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> RemoveLineAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.RemoveLine(barcode);
                return BuildSnapshot("Riga rimossa.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<RecentSaleItem>> GetRecentSalesAsync(int limit = 20)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.LastSalesAsync(limit).ConfigureAwait(false);
                return rows.Select(x => new RecentSaleItem
                {
                    SaleId = x.Id,
                    SaleCode = x.Code ?? string.Empty,
                    CreatedAtMs = x.CreatedAt,
                    TotalMinor = x.Total
                }).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> GetReceiptPreviewBySaleIdAsync(long saleId, bool use42)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sale = await _sales.GetByIdAsync(saleId).ConfigureAwait(false);
                if (sale == null) return string.Empty;
                var lines = await _sales.GetLinesBySaleIdAsync(saleId).ConfigureAwait(false);
                var completed = new SaleCompleted(sale, lines);
                return BuildReceiptPreview(completed, use42);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> GetLastReceiptPreviewAsync(bool use42)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_lastCompletedSale == null)
                    return string.Empty;
                return BuildReceiptPreview(_lastCompletedSale, use42);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> GetSnapshotAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return BuildSnapshot(string.Empty);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureDemoProductsAsync()
        {
            if (!_options.IsDemo)
            {
                _logger.LogInfo("POS demo seed skipped: IsDemo=false");
                return;
            }

            var existing = await _products.ListAllAsync().ConfigureAwait(false);
            if (existing.Count > 0)
            {
                _logger.LogInfo("POS demo seed skipped: products already exist");
                return;
            }

            var demo = new[]
            {
                new Product { Barcode = "1234567890123", Name = "Coca Cola 500ml", UnitPrice = 1000 },
                new Product { Barcode = "9876543210000", Name = "Water 500ml", UnitPrice = 700 },
                new Product { Barcode = "1111111111111", Name = "Snack Bar", UnitPrice = 250 }
            };

            foreach (var item in demo)
                await _products.UpsertAsync(item).ConfigureAwait(false);
            _logger.LogInfo("POS demo seed inserted: " + demo.Length);
        }

        private PosWorkflowSnapshot BuildSnapshot(string status)
        {
            var lines = _session.Lines
                .Select(x => new PosCartLine
                {
                    Barcode = x.Barcode,
                    Name = x.Name,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    LineTotal = x.LineTotal
                })
                .ToList();

            return new PosWorkflowSnapshot
            {
                Lines = lines,
                Subtotal = _session.Total,
                Total = _session.Total,
                Status = status ?? string.Empty
            };
        }

        private static string BuildReceiptPreview(SaleCompleted completed, bool use42)
        {
            var lines = ReceiptFormatter.Format(
                completed.Sale,
                completed.Lines,
                use42 ? ReceiptOptions.Default42() : ReceiptOptions.Default32(),
                new ReceiptShopInfo
                {
                    Name = "Win7 POS Demo",
                    Address = "Via Roma 1, Torino",
                    Footer = "Powered by Win7POS"
                });
            return string.Join(Environment.NewLine, lines);
        }
    }

    public sealed class PosWorkflowSnapshot
    {
        public List<PosCartLine> Lines { get; set; } = new List<PosCartLine>();
        public int Subtotal { get; set; }
        public int Total { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public sealed class PosPayResult
    {
        public string SaleCode { get; set; } = string.Empty;
        public string ReceiptPreview { get; set; } = string.Empty;
        public PosWorkflowSnapshot Snapshot { get; set; } = new PosWorkflowSnapshot();
    }

    public sealed class PosSaleResult
    {
        public long SaleId { get; set; }
        public string SaleCode { get; set; } = string.Empty;
        public int TotalMinor { get; set; }
        public long CreatedAtMs { get; set; }
        public string Receipt42 { get; set; } = string.Empty;
        public string Receipt32 { get; set; } = string.Empty;
        public PosWorkflowSnapshot Snapshot { get; set; } = new PosWorkflowSnapshot();
    }

    public sealed class PosPaymentInfo
    {
        public int CashAmountMinor { get; set; }
        public int CardAmountMinor { get; set; }

        public bool IsValid(int totalMinor)
        {
            if (CashAmountMinor < 0 || CardAmountMinor < 0) return false;
            return CashAmountMinor + CardAmountMinor >= totalMinor;
        }

        public int GetChangeMinor(int totalMinor)
        {
            if (!IsValid(totalMinor)) return 0;
            return CashAmountMinor + CardAmountMinor - totalMinor;
        }
    }

    public sealed class RecentSaleItem
    {
        public long SaleId { get; set; }
        public string SaleCode { get; set; } = string.Empty;
        public long CreatedAtMs { get; set; }
        public int TotalMinor { get; set; }
    }

    public sealed class PosCartLine
    {
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int UnitPrice { get; set; }
        public int LineTotal { get; set; }
    }

    public sealed class PosPrinterSettings
    {
        public string PrinterName { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
        public bool AutoPrint { get; set; } = true;
        public bool SaveCopyToFile { get; set; }
        public string OutputDirectory { get; set; } = string.Empty;
    }
}
