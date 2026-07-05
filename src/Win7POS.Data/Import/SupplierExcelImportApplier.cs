using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Data.Online;

namespace Win7POS.Data.Import
{
    public sealed class SupplierExcelImportApplyOptions
    {
        public CatalogImportOutboxEntry CatalogImportOutboxEntry { get; set; }
        public bool DryRun { get; set; }
        public bool InsertNew { get; set; } = true;
    }

    public sealed class SupplierExcelImportApplyResult
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int NoChange { get; set; }
        public int Errors { get; set; }
        public int SuppliersCreated { get; set; }
        public int CategoriesCreated { get; set; }
        public int PriceHistoryInserted { get; set; }
        public List<string> ErrorMessages { get; } = new List<string>();
        public List<string> ChangedBarcodes { get; } = new List<string>();
        public long CatalogImportOutboxId { get; set; }
        public string CatalogImportOutboxStatus { get; set; } = string.Empty;
    }

    public sealed class SupplierExcelImportApplier
    {
        private readonly SqliteConnectionFactory _factory;

        public SupplierExcelImportApplier(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<SupplierExcelImportApplyResult> ApplyAsync(
            SupplierImportSyncPreview preview,
            SupplierExcelImportApplyOptions options)
        {
            var result = new SupplierExcelImportApplyResult();
            if (preview == null)
            {
                AddError(result, "Sync DB preview richiesto prima di applicare.");
                return result;
            }
            if (!preview.CanApply)
            {
                AddError(result, "Sync DB preview contiene errori e blocca l'applicazione.");
                return result;
            }
            return await ApplyAsync(preview.ValidatedRows, options).ConfigureAwait(false);
        }

        public async Task<SupplierExcelImportApplyResult> ApplyAsync(
            IReadOnlyList<SupplierImportEditableRow> rows,
            SupplierExcelImportApplyOptions options)
        {
            var result = new SupplierExcelImportApplyResult();
            options = options ?? new SupplierExcelImportApplyOptions();
            if (rows == null || rows.Count == 0) return result;

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var resolver = new CategorySupplierResolver(conn, tx);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var row in rows)
                    {
                        if (row == null)
                        {
                            result.NoChange += 1;
                            continue;
                        }
                        if (row.IsSkipped)
                        {
                            result.NoChange += 1;
                            continue;
                        }
                        var barcode = Normalize(row?.Barcode);
                        if (barcode.Length == 0)
                        {
                            AddError(result, "Barcode mancante in riga " + (row == null ? 0 : row.RowNumber).ToString(CultureInfo.InvariantCulture));
                            continue;
                        }
                        if (!seen.Add(barcode))
                        {
                            AddError(result, "Barcode duplicato dopo revisione: " + barcode);
                            continue;
                        }

                        var existing = await LoadExistingAsync(conn, tx, barcode).ConfigureAwait(false);
                        if (existing == null && !options.InsertNew)
                        {
                            result.NoChange += 1;
                            continue;
                        }

                        var merged = MergeRow(row, existing, result);
                        if (merged == null) continue;

                        if (options.DryRun)
                        {
                            if (existing == null)
                            {
                                result.Inserted += 1;
                                result.ChangedBarcodes.Add(barcode);
                            }
                            else if (HasChanges(existing, merged))
                            {
                                result.Updated += 1;
                                result.ChangedBarcodes.Add(barcode);
                            }
                            else
                            {
                                result.NoChange += 1;
                            }
                            continue;
                        }

                        var supplierId = string.IsNullOrWhiteSpace(merged.SupplierName)
                            ? merged.SupplierId
                            : await resolver.GetOrCreateSupplierIdAsync(merged.SupplierName).ConfigureAwait(false);
                        var categoryId = string.IsNullOrWhiteSpace(merged.CategoryName)
                            ? merged.CategoryId
                            : await resolver.GetOrCreateCategoryIdAsync(merged.CategoryName).ConfigureAwait(false);

                        merged.SupplierId = supplierId;
                        merged.CategoryId = categoryId;

                        if (existing == null)
                        {
                            await InsertProductAsync(conn, tx, merged).ConfigureAwait(false);
                            result.Inserted += 1;
                            result.ChangedBarcodes.Add(barcode);
                            result.PriceHistoryInserted += await InsertInitialPriceHistoryAsync(conn, tx, row, merged).ConfigureAwait(false);
                        }
                        else if (HasChanges(existing, merged))
                        {
                            await UpdateProductAsync(conn, tx, existing, merged).ConfigureAwait(false);
                            result.Updated += 1;
                            result.ChangedBarcodes.Add(barcode);
                            result.PriceHistoryInserted += await InsertChangedPriceHistoryAsync(conn, tx, existing, row, merged).ConfigureAwait(false);
                        }
                        else
                        {
                            result.NoChange += 1;
                        }
                    }

                    result.SuppliersCreated = resolver.SuppliersCreated;
                    result.CategoriesCreated = resolver.CategoriesCreated;

                    if (result.Errors > 0)
                    {
                        tx.Rollback();
                        return result;
                    }

                    if (!options.DryRun &&
                        options.CatalogImportOutboxEntry != null &&
                        result.ChangedBarcodes.Count > 0)
                    {
                        result.CatalogImportOutboxId = await CatalogImportOutboxRepository
                            .EnqueueAsync(conn, tx, options.CatalogImportOutboxEntry)
                            .ConfigureAwait(false);
                        result.CatalogImportOutboxStatus = "pending";
                    }

                    if (options.DryRun) tx.Rollback();
                    else tx.Commit();
                    return result;
                }
                catch (Exception ex)
                {
                    try { tx.Rollback(); } catch { }
                    AddError(result, ex.Message);
                    return result;
                }
            }
        }

        private static void AddError(SupplierExcelImportApplyResult result, string message)
        {
            result.Errors += 1;
            result.ErrorMessages.Add(message ?? "Errore import.");
        }

        private static async Task<ProductDetailsRow> LoadExistingAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string barcode)
        {
            return await conn.QueryFirstOrDefaultAsync<ProductDetailsRow>(@"
SELECT
  p.id AS Id,
  p.barcode AS Barcode,
  p.name AS Name,
  p.unitPrice AS UnitPrice,
  COALESCE(m.article_code, '') AS ArticleCode,
  COALESCE(m.name2, '') AS Name2,
  COALESCE(m.purchase_price, 0) AS PurchasePrice,
  COALESCE(m.stock_qty, 0) AS StockQty,
  m.supplier_id AS SupplierId,
  COALESCE(m.supplier_name, '') AS SupplierName,
  m.category_id AS CategoryId,
  COALESCE(m.category_name, '') AS CategoryName
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.barcode = @barcode
  AND COALESCE(p.is_active, 1) = 1
LIMIT 1",
                new { barcode },
                tx).ConfigureAwait(false);
        }

        private static ProductDetailsRow MergeRow(
            SupplierImportEditableRow row,
            ProductDetailsRow existing,
            SupplierExcelImportApplyResult result)
        {
            var barcode = Normalize(row?.Barcode);
            var name = TextOrExisting(row?.ProductName, existing == null ? null : existing.Name);
            var itemNumber = TextOrExisting(row?.ItemNumber, existing == null ? null : existing.ArticleCode);
            var secondName = TextOrExisting(row?.SecondProductName, existing == null ? null : existing.Name2);

            if (existing == null && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(secondName) && string.IsNullOrWhiteSpace(itemNumber))
            {
                AddError(result, "Nuovo prodotto senza productName, secondProductName o itemNumber: " + barcode);
                return null;
            }
            if (existing == null && string.IsNullOrWhiteSpace(name))
                name = string.IsNullOrWhiteSpace(secondName) ? itemNumber : secondName;
            if (existing == null && string.IsNullOrWhiteSpace(row?.RetailPrice))
            {
                AddError(result, "Nuovo prodotto senza retailPrice: " + barcode);
                return null;
            }

            if (string.IsNullOrWhiteSpace(name))
                name = itemNumber;

            int purchase;
            long retail;
            int stock;
            if (!TryParseIntOrExisting(row?.PurchasePrice, existing == null ? 0 : existing.PurchasePrice, out purchase) ||
                !TryParseLongOrExisting(row?.RetailPrice, existing == null ? 0 : existing.UnitPrice, out retail) ||
                !TryParseIntOrExisting(row?.Quantity, existing == null ? 0 : existing.StockQty, out stock))
            {
                AddError(result, "Valore numerico non valido per barcode " + barcode);
                return null;
            }

            if (purchase < 0 || retail < 0 || stock < 0)
            {
                AddError(result, "Valori negativi non ammessi per barcode " + barcode);
                return null;
            }

            return new ProductDetailsRow
            {
                Id = existing == null ? 0 : existing.Id,
                Barcode = barcode,
                Name = name ?? string.Empty,
                UnitPrice = retail,
                ArticleCode = itemNumber ?? string.Empty,
                Name2 = secondName ?? string.Empty,
                PurchasePrice = purchase,
                StockQty = stock,
                SupplierId = existing == null ? null : existing.SupplierId,
                SupplierName = TextOrExisting(row?.Supplier, existing == null ? null : existing.SupplierName) ?? string.Empty,
                CategoryId = existing == null ? null : existing.CategoryId,
                CategoryName = TextOrExisting(row?.Category, existing == null ? null : existing.CategoryName) ?? string.Empty
            };
        }

        private static bool HasChanges(ProductDetailsRow existing, ProductDetailsRow merged)
        {
            if (existing == null) return true;
            return !TextEquals(existing.Name, merged.Name) ||
                existing.UnitPrice != merged.UnitPrice ||
                !TextEquals(existing.ArticleCode, merged.ArticleCode) ||
                !TextEquals(existing.Name2, merged.Name2) ||
                existing.PurchasePrice != merged.PurchasePrice ||
                existing.StockQty != merged.StockQty ||
                !TextEquals(existing.SupplierName, merged.SupplierName) ||
                !TextEquals(existing.CategoryName, merged.CategoryName);
        }

        private static Task InsertProductAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            ProductDetailsRow row)
        {
            return conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, is_active, remote_deleted_at)
VALUES(@Barcode, @Name, @UnitPrice, 1, NULL);

INSERT OR REPLACE INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @Barcode, @ArticleCode, @Name2, @PurchasePrice, 0, 0,
  @SupplierId, @SupplierName, @CategoryId, @CategoryName, @StockQty);",
                row,
                tx);
        }

        private static Task UpdateProductAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            ProductDetailsRow existing,
            ProductDetailsRow row)
        {
            return conn.ExecuteAsync(@"
UPDATE products
SET name = @Name,
    unitPrice = @UnitPrice,
    is_active = 1,
    remote_deleted_at = NULL
WHERE barcode = @Barcode;

INSERT OR REPLACE INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @Barcode, @ArticleCode, @Name2, @PurchasePrice, 0, 0,
  @SupplierId, @SupplierName, @CategoryId, @CategoryName, @StockQty);",
                row,
                tx);
        }

        private static async Task<int> InsertInitialPriceHistoryAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            SupplierImportEditableRow source,
            ProductDetailsRow row)
        {
            var count = 0;
            var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            if (!string.IsNullOrWhiteSpace(source.PurchasePrice))
            {
                count += await InsertPriceHistoryAsync(conn, tx, row.Barcode, ts, "purchase", null, row.PurchasePrice).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(source.RetailPrice))
            {
                count += await InsertPriceHistoryAsync(conn, tx, row.Barcode, ts, "retail", null, (int)row.UnitPrice).ConfigureAwait(false);
            }
            return count;
        }

        private static async Task<int> InsertChangedPriceHistoryAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            ProductDetailsRow existing,
            SupplierImportEditableRow source,
            ProductDetailsRow row)
        {
            var count = 0;
            var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            if (!string.IsNullOrWhiteSpace(source.PurchasePrice) && existing.PurchasePrice != row.PurchasePrice)
            {
                count += await InsertPriceHistoryAsync(conn, tx, row.Barcode, ts, "purchase", existing.PurchasePrice, row.PurchasePrice).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(source.RetailPrice) && existing.UnitPrice != row.UnitPrice)
            {
                count += await InsertPriceHistoryAsync(conn, tx, row.Barcode, ts, "retail", (int)existing.UnitPrice, (int)row.UnitPrice).ConfigureAwait(false);
            }
            return count;
        }

        private static Task<int> InsertPriceHistoryAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string barcode,
            string timestamp,
            string type,
            int? oldPrice,
            int newPrice)
        {
            return conn.ExecuteAsync(@"
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @timestamp, @type, @oldPrice, @newPrice, 'IMPORT')",
                new
                {
                    barcode,
                    timestamp,
                    type,
                    oldPrice,
                    newPrice
                },
                tx);
        }

        private static bool TryParseIntOrExisting(string value, int existing, out int parsed)
        {
            parsed = existing;
            if (string.IsNullOrWhiteSpace(value)) return true;
            var number = SupplierImportAnalyzer.ParseNumber(value);
            if (!number.HasValue || number.Value < int.MinValue || number.Value > int.MaxValue) return false;
            parsed = Convert.ToInt32(Math.Round(number.Value));
            return true;
        }

        private static bool TryParseLongOrExisting(string value, long existing, out long parsed)
        {
            parsed = existing;
            if (string.IsNullOrWhiteSpace(value)) return true;
            var number = SupplierImportAnalyzer.ParseNumber(value);
            if (!number.HasValue || number.Value < long.MinValue || number.Value > long.MaxValue) return false;
            parsed = Convert.ToInt64(Math.Round(number.Value));
            return true;
        }

        private static string TextOrExisting(string value, string existing)
        {
            var normalized = Normalize(value);
            return normalized.Length > 0 ? normalized : (existing ?? string.Empty);
        }

        private static string Normalize(string value)
        {
            if (value == null) return string.Empty;
            var trimmed = value.Trim();
            return trimmed.Length == 0
                ? string.Empty
                : string.Join(" ", trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool TextEquals(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
    }
}
