using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Win7POS.Core;
using Win7POS.Core.Audit;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Core.Util;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Printing;

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
        private readonly DbMaintenanceRepository _dbMaintenance;
        private readonly AuditLogRepository _audit = new AuditLogRepository();
        private readonly PosSession _session;
        private readonly PosDbOptions _options;
        private readonly SqliteConnectionFactory _factory;
        private readonly IReceiptPrinter _receiptPrinter = new WindowsSpoolerReceiptPrinter();

        private SaleCompleted _lastCompletedSale;

        public PosWorkflowService()
        {
            _options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(_options);

            _factory = new SqliteConnectionFactory(_options);
            _products = new ProductRepository(_factory);
            _sales = new SaleRepository(_factory);
            _settings = new SettingsRepository(_factory);
            _dbMaintenance = new DbMaintenanceRepository(_factory);
            _session = new PosSession(new DataProductLookup(_products), new DataSalesStore(_sales));
        }

        public string DbPath => _options.DbPath;

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
                return await ReadPrinterSettingsNoLockAsync().ConfigureAwait(false);
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

        public async Task RestoreDbAsync(string backupDbPath)
        {
            if (string.IsNullOrWhiteSpace(backupDbPath))
                throw new ArgumentException("backup path is empty");
            if (!File.Exists(backupDbPath))
                throw new FileNotFoundException("Backup file not found.", backupDbPath);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                SqliteConnection.ClearAllPools();
                File.Copy(backupDbPath, _options.DbPath, true);
                var details = AuditDetails.Kv(
                    ("backupPath", backupDbPath),
                    ("dbPath", _options.DbPath));
                await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.DbRestore, details).ConfigureAwait(false);
                _logger.LogInfo("POS DB restored from: " + backupDbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS DB restore failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> IntegrityCheckAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _dbMaintenance.IntegrityCheckAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task VacuumAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _dbMaintenance.VacuumAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task WalCheckpointAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _dbMaintenance.WalCheckpointAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DailySalesSummary> GetDailySummaryAsync(DateTime date)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _sales.GetDailySummaryAsync(date).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> ExportDailyCsvAsync(DateTime date)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                AppPaths.EnsureCreated();
                var rows = await _sales.GetSalesForDateAsync(date).ConfigureAwait(false);
                var fileName = "daily_" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";
                var path = Path.Combine(AppPaths.ExportsDirectory, fileName);
                using (var sw = new StreamWriter(path, false, Encoding.UTF8))
                {
                    await sw.WriteLineAsync("saleId;code;kind;related_sale_id;createdAt;total;paidCash;paidCard;change").ConfigureAwait(false);
                    foreach (var s in rows)
                    {
                        await sw.WriteLineAsync(
                            s.Id + ";" +
                            EscapeCsv(s.Code) + ";" +
                            s.Kind + ";" +
                            (s.RelatedSaleId.HasValue ? s.RelatedSaleId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty) + ";" +
                            s.CreatedAt + ";" +
                            s.Total + ";" +
                            s.PaidCash + ";" +
                            s.PaidCard + ";" +
                            s.Change).ConfigureAwait(false);
                    }
                }
                return path;
            }
            finally
            {
                _gate.Release();
            }
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

        public async Task<PosSaleResult> CompleteSaleAsync(
            PosPaymentInfo payment,
            string saleCode = null,
            long? createdAtMs = null)
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

                var effectiveCreated = (createdAtMs.HasValue && createdAtMs.Value != 0) ? createdAtMs.Value : (long?)null;
                var sale = new Sale
                {
                    Code = !string.IsNullOrWhiteSpace(saleCode) ? saleCode : SaleCodeGenerator.NewCode("V"),
                    CreatedAt = effectiveCreated ?? UnixTime.NowMs(),
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

        public async Task<RefundPreviewModel> BuildRefundPreviewAsync(long originalSaleId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DbInitializer.EnsureCreated(_options);
                var sale = await _sales.GetByIdAsync(originalSaleId).ConfigureAwait(false);
                if (sale == null)
                    throw new InvalidOperationException("Vendita non trovata.");
                if (sale.Kind != (int)SaleKind.Sale)
                    throw new InvalidOperationException("Solo le vendite normali possono essere rimborsate.");

                var lines = await _sales.GetReturnableLinesAsync(originalSaleId).ConfigureAwait(false);
                var rows = lines.Select(x => new RefundPreviewLine
                {
                    OriginalLineId = x.OriginalLineId,
                    Barcode = x.Barcode ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    UnitPriceMinor = x.UnitPrice,
                    SoldQty = x.SoldQty,
                    RefundedQty = x.RefundedQty,
                    RemainingQty = x.RemainingQty,
                    QtyToRefund = 0
                }).ToList();

                return new RefundPreviewModel
                {
                    OriginalSaleId = sale.Id,
                    OriginalSaleCode = sale.Code ?? string.Empty,
                    OriginalCreatedAtMs = sale.CreatedAt,
                    OriginalTotalMinor = sale.Total,
                    IsAlreadyVoided = sale.VoidedBySaleId.HasValue,
                    Lines = rows,
                    MaxRefundableMinor = rows.Sum(x => x.RemainingQty * x.UnitPriceMinor)
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RefundCreateResult> CreateRefundAsync(RefundCreateRequest req, bool useReceipt42, bool autoPrint)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (req.OriginalSaleId <= 0) throw new ArgumentException("invalid original sale id");

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DbInitializer.EnsureCreated(_options);
                var original = await _sales.GetByIdAsync(req.OriginalSaleId).ConfigureAwait(false);
                if (original == null)
                    throw new InvalidOperationException("Vendita originale non trovata.");
                if (original.Kind != (int)SaleKind.Sale)
                    throw new InvalidOperationException("La vendita selezionata non e rimborsabile.");

                var returnable = await _sales.GetReturnableLinesAsync(req.OriginalSaleId).ConfigureAwait(false);
                var returnableMap = returnable.ToDictionary(x => x.OriginalLineId, x => x);
                if (returnableMap.Count == 0)
                    throw new InvalidOperationException("Nessuna riga rimborsabile.");

                var selected = new List<RefundLineRequest>();
                if (req.IsFullVoid)
                {
                    if (original.VoidedBySaleId.HasValue)
                        throw new InvalidOperationException("Vendita gia stornata.");
                    foreach (var x in returnable)
                    {
                        if (x.RemainingQty <= 0) continue;
                        selected.Add(new RefundLineRequest
                        {
                            OriginalLineId = x.OriginalLineId,
                            Barcode = x.Barcode ?? string.Empty,
                            Name = x.Name ?? string.Empty,
                            UnitPriceMinor = x.UnitPrice,
                            QtyToRefund = x.RemainingQty
                        });
                    }
                }
                else
                {
                    foreach (var line in req.Lines ?? new List<RefundLineRequest>())
                    {
                        if (line == null || line.QtyToRefund <= 0) continue;
                        if (!returnableMap.TryGetValue(line.OriginalLineId, out var source))
                            throw new InvalidOperationException("Riga reso non valida.");
                        if (line.QtyToRefund > source.RemainingQty)
                            throw new InvalidOperationException("Quantita reso oltre il disponibile.");

                        selected.Add(new RefundLineRequest
                        {
                            OriginalLineId = source.OriginalLineId,
                            Barcode = source.Barcode ?? string.Empty,
                            Name = source.Name ?? string.Empty,
                            UnitPriceMinor = source.UnitPrice,
                            QtyToRefund = line.QtyToRefund
                        });
                    }
                }

                if (selected.Count == 0)
                    throw new InvalidOperationException("Nessuna riga selezionata per il reso.");

                var refundPositiveTotal = selected.Sum(x => x.QtyToRefund * x.UnitPriceMinor);
                if (refundPositiveTotal <= 0)
                    throw new InvalidOperationException("Totale rimborso non valido.");

                var cash = req.Payment == null ? 0 : req.Payment.CashMinor;
                var card = req.Payment == null ? 0 : req.Payment.CardMinor;
                if (cash < 0 || card < 0)
                    throw new InvalidOperationException("Pagamento rimborso non valido.");
                if (cash + card != refundPositiveTotal)
                    throw new InvalidOperationException("Cash + Card deve essere uguale al totale rimborso.");

                var refundSale = new Sale
                {
                    Code = SaleCodeGenerator.NewCode("R"),
                    CreatedAt = UnixTime.NowMs(),
                    Kind = (int)SaleKind.Refund,
                    RelatedSaleId = original.Id,
                    Reason = (req.Reason ?? string.Empty).Trim(),
                    Total = -Math.Abs(refundPositiveTotal),
                    PaidCash = -Math.Abs(cash),
                    PaidCard = -Math.Abs(card),
                    Change = 0
                };

                var refundLines = selected.Select(x => new SaleLine
                {
                    ProductId = null,
                    Barcode = x.Barcode ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    Quantity = x.QtyToRefund,
                    UnitPrice = x.UnitPriceMinor,
                    LineTotal = -Math.Abs(x.QtyToRefund * x.UnitPriceMinor),
                    RelatedOriginalLineId = x.OriginalLineId
                }).ToList();

                using (var conn = _factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        var refundSaleId = await ExecuteInsertRefundSaleIdAsync(conn, tx, refundSale).ConfigureAwait(false);
                        refundSale.Id = refundSaleId;

                        foreach (var line in refundLines)
                            line.SaleId = refundSaleId;

                        await _sales.InsertSaleLinesAsync(conn, tx, refundLines).ConfigureAwait(false);

                        var voided = req.IsFullVoid ? "true" : "false";
                        var details = AuditDetails.Kv(
                            ("originalSaleId", original.Id.ToString()),
                            ("refundSaleId", refundSaleId.ToString()),
                            ("isFullVoid", req.IsFullVoid.ToString()),
                            ("voided", voided),
                            ("totalMinor", refundSale.Total.ToString()),
                            ("lines", refundLines.Count.ToString()));
                        await _audit.AppendAsync(conn, tx, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.RefundCreate, details).ConfigureAwait(false);

                        if (req.IsFullVoid)
                            await _sales.MarkSaleVoidedAsync(conn, tx, original.Id, refundSaleId, UnixTime.NowMs()).ConfigureAwait(false);

                        tx.Commit();
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }

                var completed = new SaleCompleted(refundSale, refundLines);
                var receipt42 = BuildRefundReceiptPreview(completed, true);
                var receipt32 = BuildRefundReceiptPreview(completed, false);

                if (autoPrint)
                {
                    try
                    {
                        var receiptText = useReceipt42 ? receipt42 : receipt32;
                        await PrintReceiptTextNoLockAsync(receiptText, useReceipt42, "REFUND_" + refundSale.Code).ConfigureAwait(false);
                    }
                    catch (Exception printEx)
                    {
                        _logger.LogError(printEx, "POS refund print failed");
                    }
                }

                return new RefundCreateResult
                {
                    RefundSaleId = refundSale.Id,
                    RefundSaleCode = refundSale.Code,
                    Receipt42 = receipt42,
                    Receipt32 = receipt32,
                    TotalMinor = refundSale.Total
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS create refund failed");
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
                    TotalMinor = x.Total,
                    Kind = x.Kind,
                    RelatedSaleId = x.RelatedSaleId,
                    VoidedBySaleId = x.VoidedBySaleId
                }).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<RecentSaleItem>> GetSalesBetweenAsync(long fromMs, long toMs)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.GetSalesBetweenAsync(fromMs, toMs).ConfigureAwait(false);
                return rows.Select(x => new RecentSaleItem
                {
                    SaleId = x.Id,
                    SaleCode = x.Code ?? string.Empty,
                    CreatedAtMs = x.CreatedAt,
                    TotalMinor = x.Total,
                    Kind = x.Kind,
                    RelatedSaleId = x.RelatedSaleId,
                    VoidedBySaleId = x.VoidedBySaleId
                }).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<RecentSaleItem>> GetSalesByCodeFilterAsync(string codeFilter)
        {
            if (string.IsNullOrWhiteSpace(codeFilter))
                return Array.Empty<RecentSaleItem>();
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.GetByCodeLikeAsync(codeFilter).ConfigureAwait(false);
                return rows.Select(x => new RecentSaleItem
                {
                    SaleId = x.Id,
                    SaleCode = x.Code ?? string.Empty,
                    CreatedAtMs = x.CreatedAt,
                    TotalMinor = x.Total,
                    Kind = x.Kind,
                    RelatedSaleId = x.RelatedSaleId,
                    VoidedBySaleId = x.VoidedBySaleId
                }).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<SaleDetailResult> GetSaleDetailsAsync(long saleId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sale = await _sales.GetByIdAsync(saleId).ConfigureAwait(false);
                if (sale == null)
                    return null;
                var lines = await _sales.GetLinesBySaleIdAsync(saleId).ConfigureAwait(false);
                return new SaleDetailResult { Sale = sale, Lines = lines };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosPrintResult> PrintReceiptBySaleIdAsync(long saleId, bool use42)
        {
            var preview = await GetReceiptPreviewBySaleIdAsync(saleId, use42).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(preview))
                return new PosPrintResult { SavedCopy = false };
            var settings = await GetPrinterSettingsAsync().ConfigureAwait(true);
            var fileTag = "SALE_" + saleId;
            return await PrintReceiptTextAsync(preview, use42, fileTag).ConfigureAwait(true);
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
                if (sale.Kind == (int)SaleKind.Refund)
                    return BuildRefundReceiptPreview(completed, use42);
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

        public async Task<PosPrintResult> PrintReceiptTextAsync(string receiptText, bool use42, string fileTag)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await PrintReceiptTextNoLockAsync(receiptText, use42, fileTag).ConfigureAwait(false);
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

        public async Task<PosWorkflowSnapshot> ClearCartAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.Clear();
                return BuildSnapshot("Carrello svuotato.");
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
                use42 ? ReceiptOptions.Default42Clp() : ReceiptOptions.Default32Clp(),
                new ReceiptShopInfo
                {
                    Name = "Win7POS",
                    Address = "",
                    Footer = "Grazie"
                });
            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildRefundReceiptPreview(SaleCompleted completed, bool use42)
        {
            var baseText = BuildReceiptPreview(completed, use42);
            return "RESO/STORNO" + Environment.NewLine + baseText;
        }

        private static async Task<long> ExecuteInsertRefundSaleIdAsync(SqliteConnection conn, SqliteTransaction tx, Sale refundSale)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO sales(code, createdAt, kind, related_sale_id, reason, total, paidCash, paidCard, change)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @Reason, @Total, @PaidCash, @PaidCard, @Change);
SELECT last_insert_rowid();";

                cmd.Parameters.AddWithValue("@Code", (object)refundSale.Code ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", refundSale.CreatedAt);
                cmd.Parameters.AddWithValue("@Kind", refundSale.Kind);
                cmd.Parameters.AddWithValue("@RelatedSaleId", (object)refundSale.RelatedSaleId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Reason", (object)refundSale.Reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Total", refundSale.Total);
                cmd.Parameters.AddWithValue("@PaidCash", refundSale.PaidCash);
                cmd.Parameters.AddWithValue("@PaidCard", refundSale.PaidCard);
                cmd.Parameters.AddWithValue("@Change", refundSale.Change);

                var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (obj == null || obj == DBNull.Value) return 0;
                return Convert.ToInt64(obj, CultureInfo.InvariantCulture);
            }
        }

        private async Task<PosPrinterSettings> ReadPrinterSettingsNoLockAsync()
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

        private async Task<PosPrintResult> PrintReceiptTextNoLockAsync(string receiptText, bool use42, string fileTag)
        {
            var text = receiptText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Receipt text is empty.");

            var printer = await ReadPrinterSettingsNoLockAsync().ConfigureAwait(false);
            var outputDirectory = string.IsNullOrWhiteSpace(printer.OutputDirectory)
                ? Path.Combine(AppPaths.DataDirectory, "receipts")
                : printer.OutputDirectory;
            var outputPath = Path.Combine(outputDirectory, NormalizeFileTag(fileTag) + ".txt");

            await _receiptPrinter.PrintAsync(text, new ReceiptPrintOptions
            {
                PrinterName = printer.PrinterName,
                Copies = printer.Copies < 1 ? 1 : printer.Copies,
                CharactersPerLine = use42 ? 42 : 32,
                SaveCopyToFile = printer.SaveCopyToFile,
                OutputPath = outputPath
            }).ConfigureAwait(false);

            return new PosPrintResult
            {
                SavedCopy = printer.SaveCopyToFile,
                OutputPath = outputPath
            };
        }

        private static string NormalizeFileTag(string value)
        {
            var tag = string.IsNullOrWhiteSpace(value) ? "RECEIPT" : value.Trim();
            foreach (var ch in Path.GetInvalidFileNameChars())
                tag = tag.Replace(ch, '_');
            return tag;
        }

        private static string EscapeCsv(string value)
        {
            return (value ?? string.Empty).Replace(";", ",");
        }
    }

    public sealed class RefundPreviewModel
    {
        public long OriginalSaleId { get; set; }
        public string OriginalSaleCode { get; set; } = string.Empty;
        public long OriginalCreatedAtMs { get; set; }
        public int OriginalTotalMinor { get; set; }
        public bool IsAlreadyVoided { get; set; }
        public int MaxRefundableMinor { get; set; }
        public List<RefundPreviewLine> Lines { get; set; } = new List<RefundPreviewLine>();
    }

    public sealed class RefundPreviewLine
    {
        public long OriginalLineId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int UnitPriceMinor { get; set; }
        public int SoldQty { get; set; }
        public int RefundedQty { get; set; }
        public int RemainingQty { get; set; }
        public int QtyToRefund { get; set; }
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

    public sealed class PosPrintResult
    {
        public bool SavedCopy { get; set; }
        public string OutputPath { get; set; } = string.Empty;
    }

    public sealed class SaleDetailResult
    {
        public Sale Sale { get; set; }
        public IReadOnlyList<SaleLine> Lines { get; set; }
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
        public int Kind { get; set; }
        public long? RelatedSaleId { get; set; }
        public long? VoidedBySaleId { get; set; }
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
