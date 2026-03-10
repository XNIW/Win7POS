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
        private const string KeyShopName = "shop.name";
        private const string KeyShopAddress = "shop.address";
        private const string KeyShopCity = "shop.city";
        private const string KeyShopRut = "shop.rut";
        private const string KeyShopPhone = "shop.phone";
        private const string KeyShopFooter = "shop.footer";
        private const string KeyFiscalBoletaNumber = "fiscal.boletaNumber";

        private readonly FileLogger _logger = new FileLogger();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private readonly ProductRepository _products;
        private readonly SaleRepository _sales;
        private readonly SettingsRepository _settings;
        private readonly DbMaintenanceRepository _dbMaintenance;
        private readonly SupplierRepository _suppliers;
        private readonly CategoryRepository _categories;
        private readonly HeldCartRepository _heldCarts;
        private readonly AuditLogRepository _audit = new AuditLogRepository();
        private readonly PosSession _session;
        private readonly PosDbOptions _options;
        private readonly SqliteConnectionFactory _factory;
        private readonly IReceiptPrinter _receiptPrinter = new WindowsSpoolerReceiptPrinter();

        private SaleCompleted _lastCompletedSale;

        public PosWorkflowService()
        {
            _options = PosDbOptions.Default();
            // EnsureCreated spostato in InitializeAsync() per non bloccare il thread UI al primo render

            _factory = new SqliteConnectionFactory(_options);
            _products = new ProductRepository(_factory);
            _sales = new SaleRepository(_factory);
            _settings = new SettingsRepository(_factory);
            _dbMaintenance = new DbMaintenanceRepository(_factory);
            _suppliers = new SupplierRepository(_factory);
            _categories = new CategoryRepository(_factory);
            _heldCarts = new HeldCartRepository(_factory);
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

        public async Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesAsync(DateTime fromDate, DateTime toDate)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _sales.GetDailySummariesAsync(fromDate, toDate).ConfigureAwait(false);
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
                var content = await GetDailyCsvContentAsync(date).ConfigureAwait(false);
                var fileName = "daily_" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";
                var path = Path.Combine(AppPaths.ExportsDirectory, fileName);
                await Task.Run(() => File.WriteAllText(path, content, Encoding.UTF8)).ConfigureAwait(false);
                return path;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Restituisce il contenuto CSV per un giorno (per Salva con nome).</summary>
        public async Task<string> GetDailyCsvContentAsync(DateTime date)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.GetSalesForDateAsync(date).ConfigureAwait(false);
                return BuildSalesCsvContent(rows);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Restituisce il contenuto CSV per un periodo (per Salva con nome).</summary>
        public async Task<string> GetPeriodCsvContentAsync(DateTime fromDate, DateTime toDate)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var from = new DateTimeOffset(fromDate.Date).ToUnixTimeMilliseconds();
                var to = new DateTimeOffset(toDate.Date.AddDays(1)).ToUnixTimeMilliseconds();
                var rows = await _sales.GetSalesBetweenAsync(from, to).ConfigureAwait(false);
                return BuildSalesCsvContent(rows);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Restituisce CSV per un elenco di date (es. giorni selezionati nello storico).</summary>
        public async Task<string> GetDaysCsvContentAsync(IReadOnlyList<DateTime> dates)
        {
            if (dates == null || dates.Count == 0)
                return "saleId;code;kind;related_sale_id;createdAt;total;paidCash;paidCard;change" + Environment.NewLine;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sb = new StringBuilder();
                var headerDone = false;
                foreach (var date in dates)
                {
                    var rows = await _sales.GetSalesForDateAsync(date.Date).ConfigureAwait(false);
                    if (rows.Count == 0) continue;
                    if (!headerDone)
                    {
                        sb.AppendLine("saleId;code;kind;related_sale_id;createdAt;total;paidCash;paidCard;change");
                        headerDone = true;
                    }
                    foreach (var s in rows)
                    {
                        sb.Append(s.Id).Append(';')
                            .Append(EscapeCsv(s.Code)).Append(';')
                            .Append(s.Kind).Append(';')
                            .Append(s.RelatedSaleId.HasValue ? s.RelatedSaleId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
                            .Append(s.CreatedAt).Append(';')
                            .Append(s.Total).Append(';')
                            .Append(s.PaidCash).Append(';')
                            .Append(s.PaidCard).Append(';')
                            .Append(s.Change).AppendLine();
                    }
                }
                if (!headerDone)
                    sb.AppendLine("saleId;code;kind;related_sale_id;createdAt;total;paidCash;paidCard;change");
                return sb.ToString();
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string BuildSalesCsvContent(IReadOnlyList<Sale> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("saleId;code;kind;related_sale_id;createdAt;total;paidCash;paidCard;change");
            foreach (var s in rows)
            {
                sb.Append(s.Id).Append(';')
                    .Append(EscapeCsv(s.Code)).Append(';')
                    .Append(s.Kind).Append(';')
                    .Append(s.RelatedSaleId.HasValue ? s.RelatedSaleId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
                    .Append(s.CreatedAt).Append(';')
                    .Append(s.Total).Append(';')
                    .Append(s.PaidCash).Append(';')
                    .Append(s.PaidCard).Append(';')
                    .Append(s.Change).AppendLine();
            }
            return sb.ToString();
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
            await Task.Run(() => DbInitializer.EnsureCreated(_options)).ConfigureAwait(false);

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
                return await BuildSnapshotAsync("Item aggiunto.");
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

        public async Task<PosWorkflowSnapshot> AddManualPriceAsync(int unitPriceMinor)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInfo("POS add manual price: " + unitPriceMinor);
                await _session.AddManualPriceAsync(unitPriceMinor).ConfigureAwait(false);
                return await BuildSnapshotAsync("Aggiunto (senza codice).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS add manual price failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CreateProductAsync(string barcode, string name, int unitPriceMinor)
        {
            await CreateProductFullAsync(barcode, name, unitPriceMinor, 0, null, null, 0).ConfigureAwait(false);
        }

        /// <summary>Overload con supplier/category by name (no IDs). Nome può essere vuoto: usa "Prodotto senza codice".</summary>
        public async Task CreateProductFullAsync(
            string barcode,
            string name,
            int unitPriceMinor,
            int purchasePriceMinor,
            string supplierName,
            string categoryName,
            int stockQty)
        {
            var productName = (name ?? string.Empty).Trim();
            if (productName.Length == 0) productName = "Prodotto senza codice";
            await CreateProductFullAsync(barcode, productName, unitPriceMinor, purchasePriceMinor, null, supplierName ?? string.Empty, null, categoryName ?? string.Empty, stockQty).ConfigureAwait(false);
        }

        public async Task CreateProductFullAsync(
            string barcode,
            string name,
            int unitPriceMinor,
            int purchasePriceMinor,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var code = (barcode ?? string.Empty).Trim();
                var productName = (name ?? string.Empty).Trim();
                if (code.Length == 0) throw new ArgumentException("barcode is empty");
                if (productName.Length == 0) throw new ArgumentException("name is empty");
                if (unitPriceMinor < 0) throw new ArgumentException("price is invalid");
                if (purchasePriceMinor < 0) purchasePriceMinor = 0;
                if (stockQty < 0) stockQty = 0;

                await _products.UpsertAsync(new Product
                {
                    Barcode = code,
                    Name = productName,
                    UnitPrice = unitPriceMinor
                }).ConfigureAwait(false);

                await _products.UpsertMetaAsync(code, purchasePriceMinor, supplierId, supplierName ?? string.Empty, categoryId, categoryName ?? string.Empty, stockQty).ConfigureAwait(false);

                await _products.InsertPriceHistoryAsync(code, "retail", unitPriceMinor, "MANUAL").ConfigureAwait(false);
                if (purchasePriceMinor > 0)
                    await _products.InsertPriceHistoryAsync(code, "purchase", purchasePriceMinor, "MANUAL").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Aggiorna nome e prezzo prodotto in DB e aggiorna la riga in carrello. Ritorna snapshot aggiornato.</summary>
        public async Task<PosWorkflowSnapshot> UpdateProductAsync(string barcode, string name, long unitPriceMinor)
        {
            var code = (barcode ?? string.Empty).Trim();
            if (code.Length == 0) return await GetSnapshotAsync().ConfigureAwait(true);

            var product = await _products.GetByBarcodeAsync(code).ConfigureAwait(false);
            if (product == null) return await GetSnapshotAsync().ConfigureAwait(true);

            await _products.UpdateAsync(product.Id, name ?? string.Empty, unitPriceMinor).ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.SetLineUnitPrice(code, unitPriceMinor);
                return await BuildSnapshotAsync("Prezzo aggiornato.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Sincronizza la riga carrello con il catalogo (prezzo/nome). Se il prodotto non è più in DB, rimuove la riga. Usato dopo Modifica prodotto -> Salva.</summary>
        public async Task<PosWorkflowSnapshot> SyncCartLineFromCatalogAsync(string barcode)
        {
            var code = (barcode ?? string.Empty).Trim();
            if (code.Length == 0)
                return await GetSnapshotAsync().ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, code, StringComparison.Ordinal));
                if (line == null)
                    return await BuildSnapshotAsync(string.Empty).ConfigureAwait(false);

                var product = await _products.GetByBarcodeAsync(code).ConfigureAwait(false);

                if (product == null)
                {
                    _session.SetQuantity(code, 0);
                    return await BuildSnapshotAsync("Prodotto rimosso dal carrello: non più presente nel database.").ConfigureAwait(false);
                }

                _session.SetLineUnitPrice(code, product.UnitPrice);
                _session.SetLineName(code, product.Name ?? string.Empty);

                return await BuildSnapshotAsync("Carrello sincronizzato con il catalogo.").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<Data.Repositories.SupplierListItem>> GetSuppliersAsync()
        {
            return await _suppliers.ListAllAsync().ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<Data.Repositories.CategoryListItem>> GetCategoriesAsync()
        {
            return await _categories.ListAllAsync().ConfigureAwait(false);
        }

        public async Task<PosPayResult> PayAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInfo("POS pay start");
                var completed = await _session.PayCashAsync().ConfigureAwait(false);
                _lastCompletedSale = completed;
                var shop = await GetShopInfoNoLockAsync().ConfigureAwait(false);
                var preview = BuildReceiptPreview(completed, true, shop);
                var snapshot = await BuildSnapshotAsync("Pagamento completato.");
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
                var snapshot = await BuildSnapshotAsync("Vendita completata.");
                var shop = await GetShopInfoNoLockAsync().ConfigureAwait(false);

                return new PosSaleResult
                {
                    SaleId = sale.Id,
                    SaleCode = sale.Code,
                    TotalMinor = sale.Total,
                    CreatedAtMs = sale.CreatedAt,
                    Receipt42 = BuildReceiptPreview(completed, true, shop),
                    Receipt32 = BuildReceiptPreview(completed, false, shop),
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
                var shop = await GetShopInfoNoLockAsync().ConfigureAwait(false);
                var receipt42 = BuildRefundReceiptPreview(completed, true, shop);
                var receipt32 = BuildRefundReceiptPreview(completed, false, shop);

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
                if (line == null) return await BuildSnapshotAsync(string.Empty);
                _session.SetQuantity(line.Barcode, line.Quantity + 1);
                return await BuildSnapshotAsync("Quantita aumentata.");
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
                if (line == null) return await BuildSnapshotAsync(string.Empty);
                var next = line.Quantity - 1;
                if (next < 1) next = 1;
                _session.SetQuantity(line.Barcode, next);
                return await BuildSnapshotAsync("Quantita diminuita.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Imposta la quantità della riga con il barcode dato (0 = rimuovi riga).</summary>
        public async Task<PosWorkflowSnapshot> SetQtyAsync(string barcode, int qty)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var code = (barcode ?? string.Empty).Trim();
                if (code.Length == 0) return await BuildSnapshotAsync(string.Empty);
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, code, StringComparison.Ordinal));
                if (line == null) return await BuildSnapshotAsync(string.Empty);
                _session.SetQuantity(code, qty <= 0 ? 0 : qty);
                return await BuildSnapshotAsync("Quantita aggiornata.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS SetQty failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Imposta la quantità della riga identificata da LineKey (indice nella lista). Funziona anche per righe manuali.
        /// Nota: LineKey = index è una patch veloce; la riga è ancora aggiornata tramite barcode interno (_session.SetQuantity(line.Barcode, qty)).
        /// In futuro preferire una chiave riga stabile (es. id univoco) per evitare fragilità con barcode duplicati o riordini.</summary>
        public async Task<PosWorkflowSnapshot> SetQtyByLineAsync(string lineKey, int qty)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(lineKey) || !int.TryParse(lineKey, out var index))
                    return await BuildSnapshotAsync(string.Empty);
                if (index < 0 || index >= _session.Lines.Count)
                    return await BuildSnapshotAsync(string.Empty);
                var line = _session.Lines[index];
                if (DiscountKeys.IsDiscount(line.Barcode ?? ""))
                    return await BuildSnapshotAsync(string.Empty);
                _session.SetQuantity(line.Barcode ?? "", qty <= 0 ? 0 : qty);
                return await BuildSnapshotAsync("Quantita aggiornata.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS SetQtyByLine failed");
                throw;
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
                return await BuildSnapshotAsync("Riga rimossa.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ApplyCartDiscountPercentAsync(int percent)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyCartDiscountPercent(percent);
                return await BuildSnapshotAsync(percent <= 0 ? "Sconto carrello rimosso." : "Sconto carrello applicato.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ApplyLineDiscountPercentAsync(string barcode, int percent)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyLineDiscountPercent(barcode, percent);
                return await BuildSnapshotAsync(percent <= 0 ? "Sconto rimosso." : "Sconto aggiornato.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ApplyLineDiscountAmountAsync(string barcode, int amountMinor)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyLineDiscountAmount(barcode, amountMinor);
                return await BuildSnapshotAsync("Sconto importo applicato.");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Applica sconto riga impostando il prezzo unitario finale desiderato (sempre unitario, non totale riga). 0 = rimuovi sconto.</summary>
        public async Task<PosWorkflowSnapshot> ApplyLineDiscountByFinalPriceAsync(string barcode, long finalUnitPriceMinor)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyLineDiscountByFinalUnitPrice(barcode, finalUnitPriceMinor);
                return await BuildSnapshotAsync("Sconto aggiornato.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ClearCartDiscountAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ClearCartDiscount();
                return await BuildSnapshotAsync("Sconto carrello rimosso.");
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
                var shop = await GetShopInfoNoLockAsync().ConfigureAwait(false);
                if (sale.Kind == (int)SaleKind.Refund)
                    return BuildRefundReceiptPreview(completed, use42, shop);
                return BuildReceiptPreview(completed, use42, shop);
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
                var shop = await GetShopInfoNoLockAsync().ConfigureAwait(false);
                return BuildReceiptPreview(_lastCompletedSale, use42, shop);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<ReceiptShopInfo> GetShopInfoAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await GetShopInfoNoLockAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<ReceiptShopInfo> GetShopInfoNoLockAsync()
        {
            var name = await _settings.GetStringAsync(KeyShopName).ConfigureAwait(false);
            var address = await _settings.GetStringAsync(KeyShopAddress).ConfigureAwait(false);
            var city = await _settings.GetStringAsync(KeyShopCity).ConfigureAwait(false);
            var rut = await _settings.GetStringAsync(KeyShopRut).ConfigureAwait(false);
            var phone = await _settings.GetStringAsync(KeyShopPhone).ConfigureAwait(false);
            var footer = await _settings.GetStringAsync(KeyShopFooter).ConfigureAwait(false);
            return new ReceiptShopInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Win7 POS Store" : name.Trim(),
                Address = address?.Trim() ?? "",
                City = city?.Trim() ?? "",
                Rut = rut?.Trim() ?? "",
                Phone = phone?.Trim() ?? "",
                Footer = string.IsNullOrWhiteSpace(footer) ? "Grazie e arrivederci" : footer.Trim()
            };
        }

        public async Task SaveShopInfoAsync(ReceiptShopInfo shop)
        {
            if (shop == null) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settings.SetStringAsync(KeyShopName, shop.Name ?? "").ConfigureAwait(false);
                await _settings.SetStringAsync(KeyShopAddress, shop.Address ?? "").ConfigureAwait(false);
                await _settings.SetStringAsync(KeyShopCity, shop.City ?? "").ConfigureAwait(false);
                await _settings.SetStringAsync(KeyShopRut, shop.Rut ?? "").ConfigureAwait(false);
                await _settings.SetStringAsync(KeyShopPhone, shop.Phone ?? "").ConfigureAwait(false);
                await _settings.SetStringAsync(KeyShopFooter, shop.Footer ?? "").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<int> GetFiscalBoletaNumberAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var v = await _settings.GetIntAsync(KeyFiscalBoletaNumber).ConfigureAwait(false);
                return v ?? 0;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetFiscalBoletaNumberAsync(int number)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settings.SetIntAsync(KeyFiscalBoletaNumber, number).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosPrintResult> PrintReceiptTextAsync(string receiptText, bool use42, string fileTag, bool isFiscalPrint = false)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await PrintReceiptTextNoLockAsync(receiptText, use42, fileTag, isFiscalPrint).ConfigureAwait(false);
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
                var toRemove = new List<string>();
                foreach (var x in _session.Lines)
                {
                    if (DiscountKeys.IsDiscount(x.Barcode ?? "")) continue;
                    if ((x.Barcode ?? "").StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase)) continue;
                    var product = await _products.GetByBarcodeAsync(x.Barcode ?? "").ConfigureAwait(false);
                    if (product == null) toRemove.Add(x.Barcode ?? "");
                }
                foreach (var b in toRemove)
                    _session.RemoveLine(b);
                var status = toRemove.Count > 0 ? "Prodotto rimosso dal carrello: non più presente nel database." : string.Empty;
                return await BuildSnapshotAsync(status).ConfigureAwait(false);
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
                return await BuildSnapshotAsync("Carrello svuotato.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<SuspendCartResult> SuspendCartAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.Lines.Count == 0)
                    return new SuspendCartResult { Success = false, Message = "Carrello vuoto." };

                var lines = _session.Lines.Select(x => new Data.Repositories.HeldCartLineRow
                {
                    Barcode = x.Barcode ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    UnitPrice = x.UnitPrice,
                    Qty = x.Quantity
                }).ToList();

                var createdAtMs = UnixTime.NowMs();
                var holdId = "H-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                var total = _session.Total;

                await _heldCarts.CreateHoldAsync(holdId, createdAtMs, total, lines).ConfigureAwait(false);
                _session.Clear();

                return new SuspendCartResult { Success = true, HoldId = holdId, Message = "Carrello sospeso." };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<HeldCartItem>> GetHeldCartsAsync()
        {
            var rows = await _heldCarts.ListHoldsAsync().ConfigureAwait(false);
            return rows.Select(x => new HeldCartItem
            {
                HoldId = x.HoldId,
                CreatedAtMs = x.CreatedAtMs,
                TotalMinor = x.TotalMinor,
                TimeText = FormatHoldTime(x.CreatedAtMs)
            }).ToList();
        }

        /// <summary>Read-only preview of held cart lines (does not recover or delete).</summary>
        public async Task<IReadOnlyList<HoldLineDisplay>> PeekHeldCartLinesAsync(string holdId)
        {
            if (string.IsNullOrEmpty(holdId)) return Array.Empty<HoldLineDisplay>();
            var lines = await _heldCarts.LoadHoldLinesAsync(holdId).ConfigureAwait(false);
            return lines.Select(x => new HoldLineDisplay
            {
                Barcode = x.Barcode ?? string.Empty,
                Name = x.Name ?? string.Empty,
                UnitPrice = x.UnitPrice,
                Qty = x.Qty
            }).ToList();
        }

        public async Task DeleteHeldCartAsync(string holdId)
        {
            await _heldCarts.DeleteHoldAsync(holdId).ConfigureAwait(false);
        }

        public async Task<PosWorkflowSnapshot> RecoverHeldCartAsync(string holdId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var lines = await _heldCarts.LoadHoldLinesAsync(holdId).ConfigureAwait(false);
                if (lines.Count == 0)
                    return await BuildSnapshotAsync("Nessuna riga nel carrello sospeso.");

                var restored = lines.Select(x => new RestoredLine
                {
                    ProductId = null,
                    Barcode = x.Barcode,
                    Name = x.Name,
                    UnitPrice = x.UnitPrice,
                    Quantity = x.Qty
                }).ToList();

                _session.ReplaceWithLines(restored);
                await _heldCarts.DeleteHoldAsync(holdId).ConfigureAwait(false);

                return await BuildSnapshotAsync("Carrello recuperato.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string FormatHoldTime(long ms)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return dt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
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

        private async Task<PosWorkflowSnapshot> BuildSnapshotAsync(string status)
        {
            var lines = new List<PosCartLine>();
            var index = 0;
            // LineKey = index: patch veloce; in futuro usare chiave riga stabile (id univoco)
            foreach (var x in _session.Lines)
            {
                var stockQty = 0;
                long discountAmountMinor = 0;
                int discountPercent = 0;
                if (!DiscountKeys.IsDiscount(x.Barcode ?? "") && !(x.Barcode ?? "").StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase))
                {
                    var details = await _products.GetDetailsByBarcodeAsync(x.Barcode ?? "").ConfigureAwait(false);
                    if (details != null) stockQty = details.StockQty;
                }
                if (!DiscountKeys.IsDiscount(x.Barcode ?? ""))
                {
                    var discLine = _session.Lines.FirstOrDefault(d => DiscountKeys.IsLineDiscountFor(d.Barcode ?? "", x.Barcode ?? ""));
                    if (discLine != null)
                    {
                        discountAmountMinor = discLine.LineTotal < 0 ? -discLine.LineTotal : 0;
                        var (_, pct) = DiscountKeys.ParseLinePct(discLine.Barcode ?? "");
                        discountPercent = pct ?? (x.LineTotal > 0 ? (int)Math.Round(discountAmountMinor * 100.0 / x.LineTotal, MidpointRounding.AwayFromZero) : 0);
                    }
                }
                lines.Add(new PosCartLine
                {
                    LineKey = index.ToString(),
                    Barcode = x.Barcode ?? "",
                    Name = x.Name ?? "",
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    LineTotal = x.LineTotal,
                    StockQty = stockQty,
                    DiscountAmountMinor = discountAmountMinor,
                    DiscountPercent = discountPercent
                });
                index++;
            }

            long subtotalBeforeDiscounts = 0;
            foreach (var x in _session.Lines)
            {
                if (!DiscountKeys.IsDiscount(x.Barcode ?? ""))
                    subtotalBeforeDiscounts += x.LineTotal;
            }

            return new PosWorkflowSnapshot
            {
                Lines = lines,
                Subtotal = subtotalBeforeDiscounts,
                Total = _session.Total,
                Status = status ?? string.Empty
            };
        }

        private static string BuildReceiptPreview(SaleCompleted completed, bool use42, ReceiptShopInfo shop = null)
        {
            shop = shop ?? new ReceiptShopInfo();
            var lines = new List<string>(ReceiptFormatter.Format(
                completed.Sale,
                completed.Lines,
                use42 ? ReceiptOptions.Default42Clp() : ReceiptOptions.Default32Clp(),
                shop));
            // Stessa struttura della stampante: "Scontrino: XXX" in fondo (sotto la stampante disegna il barcode Code128)
            if (!string.IsNullOrEmpty(completed?.Sale?.Code))
            {
                lines.Add("");
                lines.Add("Scontrino: " + completed.Sale.Code);
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildRefundReceiptPreview(SaleCompleted completed, bool use42, ReceiptShopInfo shop = null)
        {
            var baseText = BuildReceiptPreview(completed, use42, shop);
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

        private async Task<PosPrintResult> PrintReceiptTextNoLockAsync(string receiptText, bool use42, string fileTag, bool isFiscalPrint = false)
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
                OutputPath = outputPath,
                UseReceiptHeaderStyle = !isFiscalPrint
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
        public long OriginalTotalMinor { get; set; }
        public bool IsAlreadyVoided { get; set; }
        public long MaxRefundableMinor { get; set; }
        public List<RefundPreviewLine> Lines { get; set; } = new List<RefundPreviewLine>();
    }

    public sealed class RefundPreviewLine
    {
        public long OriginalLineId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPriceMinor { get; set; }
        public int SoldQty { get; set; }
        public int RefundedQty { get; set; }
        public int RemainingQty { get; set; }
        public int QtyToRefund { get; set; }
    }

    public sealed class PosWorkflowSnapshot
    {
        public List<PosCartLine> Lines { get; set; } = new List<PosCartLine>();
        public long Subtotal { get; set; }
        public long Total { get; set; }
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
        public long TotalMinor { get; set; }
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
        public long CashAmountMinor { get; set; }
        public long CardAmountMinor { get; set; }

        public bool IsValid(long totalMinor)
        {
            if (CashAmountMinor < 0 || CardAmountMinor < 0) return false;
            return CashAmountMinor + CardAmountMinor >= totalMinor;
        }

        public long GetChangeMinor(long totalMinor)
        {
            if (!IsValid(totalMinor)) return 0;
            return CashAmountMinor + CardAmountMinor - totalMinor;
        }
    }

    public sealed class SuspendCartResult
    {
        public bool Success { get; set; }
        public string HoldId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public sealed class HeldCartItem
    {
        public string HoldId { get; set; } = string.Empty;
        public long CreatedAtMs { get; set; }
        public long TotalMinor { get; set; }
        public string TimeText { get; set; } = string.Empty;
    }

    public sealed class HoldLineDisplay
    {
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public int Qty { get; set; }
    }

    public sealed class RecentSaleItem
    {
        public long SaleId { get; set; }
        public string SaleCode { get; set; } = string.Empty;
        public long CreatedAtMs { get; set; }
        public long TotalMinor { get; set; }
        public int Kind { get; set; }
        public long? RelatedSaleId { get; set; }
        public long? VoidedBySaleId { get; set; }
    }

    public sealed class PosCartLine
    {
        public string LineKey { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal { get; set; }
        public int StockQty { get; set; }
        /// <summary>Importo sconto applicato a questa riga (solo righe prodotto con sconto riga).</summary>
        public long DiscountAmountMinor { get; set; }
        /// <summary>Percentuale sconto (es. 58 per -58%). Solo se sconto riga percentuale.</summary>
        public int DiscountPercent { get; set; }
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
