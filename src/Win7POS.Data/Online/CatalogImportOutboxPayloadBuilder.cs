using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public static class CatalogImportOutboxPayloadBuilder
    {
        private const string Source = "supplier_excel";

        public static CatalogImportOutboxEntry BuildSupplierExcelEntry(
            SupplierImportSyncPreview preview,
            string sourceFileName,
            string appVersion)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            var items = BuildItems(preview).ToArray();
            if (items.Length == 0)
            {
                return null;
            }

            var fingerprint = string.IsNullOrWhiteSpace(preview.Fingerprint)
                ? BuildFallbackFingerprint(items)
                : preview.Fingerprint.Trim();
            var importHash = Sha256Hex(PosOnlineContract.CatalogImportSchemaVersion + "|" + fingerprint + "|" + BuildFallbackFingerprint(items));
            var clientImportId = "win7pos-catalog-import-" + importHash.Substring(0, 24);
            var idempotencyKey = clientImportId + ":" + PosOnlineContract.CatalogImportSchemaVersion;
            var batchCreatedAt = BuildStableBatchCreatedAt(importHash);
            var outboxCreatedAt = DateTimeOffset.UtcNow;

            for (var i = 0; i < items.Length; i++)
            {
                items[i].ClientItemId = clientImportId + "-row-" +
                    items[i].RowNumber.ToString(CultureInfo.InvariantCulture) + "-" +
                    Sha256Hex(items[i].Barcode ?? string.Empty).Substring(0, 8);
            }

            var request = new PosCatalogImportRequest
            {
                AppVersion = TrimOrNull(appVersion, 40),
                Batch = new PosCatalogImportBatchRequest
                {
                    ClientImportId = clientImportId,
                    CreatedAt = batchCreatedAt,
                    IdempotencyKey = idempotencyKey,
                    PreviewFingerprint = TrimOrNull(fingerprint, 128),
                    SourceFileName = RedactFileName(sourceFileName)
                },
                Items = items,
                SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
                Source = Source,
                Summary = new PosCatalogImportSummaryRequest
                {
                    NewProducts = preview.Summary.NewProducts,
                    NoChangeRows = preview.Summary.NoChangeRows,
                    SkippedRows = preview.Summary.SkippedRows,
                    UpdatedProducts = preview.Summary.UpdatedProducts,
                    WarningCount = preview.Summary.WarningCount
                }
            };

            var payloadJson = Serialize(request);
            return new CatalogImportOutboxEntry
            {
                ClientImportId = clientImportId,
                CreatedAt = outboxCreatedAt.ToUnixTimeMilliseconds(),
                IdempotencyKey = idempotencyKey,
                PayloadHash = Sha256Hex(payloadJson),
                PayloadJson = payloadJson,
                SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
                Source = Source
            };
        }

        private static string BuildStableBatchCreatedAt(string importHash)
        {
            var hex = (importHash ?? string.Empty).Length >= 8
                ? importHash.Substring(0, 8)
                : "00000000";
            long parsed;
            if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed))
            {
                parsed = 0;
            }

            var seconds = parsed % (10L * 365 * 24 * 60 * 60);
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(seconds)
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
        }

        public static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }

        private static IEnumerable<PosCatalogImportItemRequest> BuildItems(SupplierImportSyncPreview preview)
        {
            foreach (var row in preview.NewProducts)
            {
                yield return BuildItem("new", row, null);
            }

            foreach (var row in preview.UpdatedProducts)
            {
                yield return BuildItem("updated", row.Updated, row.DiffSummary);
            }
        }

        private static PosCatalogImportItemRequest BuildItem(
            string changeKind,
            SupplierImportProductRow row,
            string diffSummary)
        {
            return new PosCatalogImportItemRequest
            {
                Barcode = TrimOrEmpty(row == null ? null : row.Barcode, 80),
                Category = TrimOrNull(row == null ? null : row.Category, 120),
                ChangeKind = changeKind,
                DiffSummary = TrimOrNull(diffSummary, 500),
                ItemNumber = TrimOrNull(row == null ? null : row.ItemNumber, 120),
                Operation = "upsert_product",
                ProductName = TrimOrNull(row == null ? null : row.ProductName, 240),
                PurchasePrice = TrimOrNull(row == null ? null : row.PurchasePrice, 40),
                Quantity = TrimOrNull(row == null ? null : row.Quantity, 40),
                RetailPrice = TrimOrNull(row == null ? null : row.RetailPrice, 40),
                RowNumber = row == null ? 0 : row.RowNumber,
                SecondProductName = TrimOrNull(row == null ? null : row.SecondProductName, 240),
                Supplier = TrimOrNull(row == null ? null : row.Supplier, 120)
            };
        }

        private static string BuildFallbackFingerprint(IEnumerable<PosCatalogImportItemRequest> items)
        {
            var sb = new StringBuilder();
            foreach (var item in items.OrderBy(x => x.RowNumber).ThenBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(item.RowNumber.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.ChangeKind).Append('|')
                    .Append(item.Barcode).Append('|')
                    .Append(item.ProductName).Append('|')
                    .Append(item.SecondProductName).Append('|')
                    .Append(item.ItemNumber).Append('|')
                    .Append(item.RetailPrice).Append('|')
                    .Append(item.PurchasePrice).Append(';');
            }

            return Sha256Hex(sb.ToString());
        }

        private static string RedactFileName(string sourceFileName)
        {
            var name = Path.GetFileName(sourceFileName ?? string.Empty);
            return TrimOrNull(name, 120);
        }

        private static string TrimOrEmpty(string value, int maxLength)
        {
            return TrimOrNull(value, maxLength) ?? string.Empty;
        }

        private static string TrimOrNull(string value, int maxLength)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return null;
            }

            return normalized.Length > maxLength
                ? normalized.Substring(0, maxLength)
                : normalized;
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
