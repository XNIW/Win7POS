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
        // Admin catalog-import-sync.ts nonNegativeNumber, contract v1.
        public const long MaximumAdminPrice = 999999999L;

        public static CatalogImportOutboxPlan BuildSupplierExcelPlan(SupplierImportSyncPreview preview, string sourceFileName, string appVersion)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            ValidateRepresentableRows(preview.ValidatedRows);
            foreach (var row in preview.NewProducts.Concat(preview.UpdatedProducts.Select(r => r.Updated))) ValidateRepresentableRow(row);
            return CatalogImportPlanBuilder.Split(BuildSupplierExcelEntry(preview, sourceFileName, appVersion));
        }

        internal static void ValidateRepresentableRows(IEnumerable<SupplierImportEditableRow> rows)
        {
            foreach(var row in rows.Where(row=>row!=null && !row.IsSkipped))
                ValidateRepresentableRow(new SupplierImportProductRow { RowNumber=row.RowNumber,Barcode=row.Barcode,ProductName=row.ProductName,
                    SecondProductName=row.SecondProductName,ItemNumber=row.ItemNumber,Supplier=row.Supplier,Category=row.Category,
                    RetailPrice=row.RetailPrice,PurchasePrice=row.PurchasePrice,Quantity=row.Quantity });
        }

        private static void ValidateRepresentableRow(SupplierImportProductRow row)
        {
            var fields = new[] { Tuple.Create("barcode", row.Barcode, 80), Tuple.Create("productName", row.ProductName, 240),
                Tuple.Create("secondProductName", row.SecondProductName, 240), Tuple.Create("itemNumber", row.ItemNumber, 120),
                Tuple.Create("supplier", row.Supplier, 120), Tuple.Create("category", row.Category, 120),
                Tuple.Create("retailPrice",row.RetailPrice,40),Tuple.Create("purchasePrice",row.PurchasePrice,40),Tuple.Create("quantity",row.Quantity,40) };
            foreach (var field in fields)
            {
                var value=field.Item2??string.Empty;
                for(var index=0;index<value.Length;index++)
                {
                    var character=value[index];
                    if(character=='\0' || char.IsLowSurrogate(character) ||
                        char.IsHighSurrogate(character) && (index+1==value.Length || !char.IsLowSurrogate(value[index+1])))
                        throw new CatalogImportRecoveryException("supplier_import_field_invalid_unicode|"+row.RowNumber+"|"+field.Item1+"|"+row.Barcode);
                    if(char.IsHighSurrogate(character)) index++;
                }
                if ((field.Item2 ?? string.Empty).Trim().Length > field.Item3)
                    throw new CatalogImportRecoveryException("supplier_import_field_too_long|" + row.RowNumber + "|" + field.Item1 + "|" + field.Item3 + "|" + row.Barcode);
            }
        }

        internal static CatalogImportOutboxPlan BuildRecoveryPlan(SupplierImportSyncPreview preview, PosCatalogImportRequest original,
            string originalHash, bool accepted, IReadOnlyList<CatalogImportRecoveryContribution> contributions, PosCatalogImportReceiptResponse receipt,
            PosCatalogImportRequest comparisonIntent = null, IReadOnlyCollection<string> acknowledgedBarcodes = null, string operationScope = null)
        {
            var acknowledged=new HashSet<string>(acknowledgedBarcodes ?? Array.Empty<string>(),StringComparer.Ordinal);
            var all = preview.ValidatedRows.Where(r=>!acknowledged.Contains(r.Barcode)).Select(r => r.Barcode).ToArray();
            foreach (var row in preview.NewProducts.Concat(preview.UpdatedProducts.Select(r => r.Updated)).Concat(preview.NoChangeRows.Select(r => r.Updated))) ValidateRepresentableRow(row);
            var entries = new List<CatalogImportOutboxEntry>();
            var sharedProof = accepted && all.Length > 1000 ? CatalogImportCorrectionSharedProof.Create(original, receipt) : null;
            for (var offset = 0; offset < all.Length; offset += CatalogImportPlanBuilder.MaximumRowsPerRequest)
            {
                var set = new HashSet<string>(all.Skip(offset).Take(CatalogImportPlanBuilder.MaximumRowsPerRequest), StringComparer.Ordinal);
                var part = new SupplierImportSyncPreview { Fingerprint = preview.Fingerprint, OperationCreatedAtUtc = preview.OperationCreatedAtUtc };
                part.NewProducts.AddRange(preview.NewProducts.Where(r => set.Contains(r.Barcode)));
                part.UpdatedProducts.AddRange(preview.UpdatedProducts.Where(r => set.Contains(r.Updated.Barcode)));
                part.NoChangeRows.AddRange(preview.NoChangeRows.Where(r => set.Contains(r.Updated.Barcode)));
                part.ValidatedRows.AddRange(preview.ValidatedRows.Where(r => set.Contains(r.Barcode)));
                var entry = BuildRecoveryEntry(part, original, originalHash, accepted, contributions, receipt, comparisonIntent, sharedProof, operationScope);
                var plan = CatalogImportPlanBuilder.Split(entry);
                if (plan != null) entries.AddRange(plan.Entries);
            }
            return entries.Count == 0 ? null : CatalogImportPlanBuilder.Plan(entries);
        }

        public static void ValidateSupplierExcelPreview(SupplierImportSyncPreview preview)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            foreach (var row in preview.NewProducts.Concat(preview.UpdatedProducts.Select(item => item.Updated)))
            {
                ValidateAdminPrice(preview, row, "purchasePrice", row.PurchasePrice);
                ValidateAdminPrice(preview, row, "retailPrice", row.RetailPrice);
            }
            preview.Summary.ErrorCount = preview.Errors.Count;
        }

        private static void ValidateAdminPrice(SupplierImportSyncPreview preview, SupplierImportProductRow row, string field, string value)
        {
            if (IsAdminPrice(value)) return;
            var message = "supplier_import_invalid_price|" + field + "|" +
                MaximumAdminPrice.ToString(CultureInfo.InvariantCulture);
            if (!preview.Errors.Any(error => error.RowIndex == row.RowNumber && error.Barcode == row.Barcode && error.Message == message))
                preview.Errors.Add(new SupplierImportError(message, row.RowNumber, row.Barcode));
        }

        internal static bool IsAdminPrice(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            decimal parsed;
            return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out parsed) && parsed >= 0 && parsed <= MaximumAdminPrice;
        }

        public static CatalogImportOutboxEntry BuildSupplierExcelEntry(
            SupplierImportSyncPreview preview,
            string sourceFileName,
            string appVersion)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            ValidateSupplierExcelPreview(preview);
            if (preview.Errors.Count > 0)
                throw new InvalidOperationException("supplier_import_admin_price_invalid: " +
                    string.Join("; ", preview.Errors.Select(error => error.Message)));
            var items = BuildItems(preview).ToArray();
            if (items.Length == 0)
            {
                return null;
            }

            var fingerprint = string.IsNullOrWhiteSpace(preview.Fingerprint)
                ? BuildFallbackFingerprint(items)
                : preview.Fingerprint.Trim();
            var batchCreatedAt = preview.OperationCreatedAtUtc;
            var importHash = Sha256Hex(PosOnlineContract.CatalogImportSchemaVersion + "|" + batchCreatedAt + "|" + fingerprint + "|" + BuildFallbackFingerprint(items));
            var clientImportId = "win7pos-catalog-import-" + importHash.Substring(0, 24);
            var idempotencyKey = clientImportId + ":" + PosOnlineContract.CatalogImportSchemaVersion;
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

        internal static CatalogImportOutboxEntry BuildRecoveryEntry(
            SupplierImportSyncPreview preview, PosCatalogImportRequest original, string originalHash, bool accepted,
            IReadOnlyList<CatalogImportRecoveryContribution> contributions,PosCatalogImportReceiptResponse receipt=null,
            PosCatalogImportRequest comparisonIntent=null, CatalogImportCorrectionSharedProof sharedProof=null, string operationScope=null)
        {
            var batchCreatedAt = preview.OperationCreatedAtUtc;
            var rows = preview.NewProducts.Concat(preview.UpdatedProducts.Select(row => row.Updated))
                .Concat(preview.NoChangeRows.Select(row => row.Updated)).OrderBy(row => row.RowNumber).ToArray();
            var items = new List<PosCatalogImportItemRequest>();
            var corrections=new List<PosCatalogImportCorrectionItem>();
            var originals = (comparisonIntent ?? original).Items.ToDictionary(item => item.Barcode, StringComparer.Ordinal);
            var edits=preview.ValidatedRows.ToDictionary(row=>row.Barcode,StringComparer.Ordinal);
            var snapshotsById=(receipt?.CurrentProductSnapshots ?? Array.Empty<PosCatalogImportProductSnapshot>())
                .GroupBy(snapshot=>snapshot.ClientItemId,StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.Ordinal);
            var ownersById=(receipt?.Receipt?.RemoteProductIds ?? Array.Empty<PosCatalogImportPersistedProductAck>())
                .GroupBy(product=>product.ClientItemId,StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.Ordinal);
            var fulfilled = contributions.SelectMany(contribution => contribution.Request.Items)
                .GroupBy(item => item.Barcode,StringComparer.Ordinal).ToDictionary(group => group.Key,group => group.ToArray(),StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var before = originals[row.Barcode];
                var edit=edits[row.Barcode];
                var item = BuildItem("updated", row, "legacy_recovery");
                // Canonical merge rows include current local fallback values. They
                // are not evidence that an absent/unchanged original field is owed
                // remotely. Preserve the immutable intent for every unedited field.
                if(TextEqual(edit.ProductName,before.ProductName)) item.ProductName=before.ProductName;
                if(TextEqual(edit.SecondProductName,before.SecondProductName)) item.SecondProductName=before.SecondProductName;
                if(TextEqual(edit.ItemNumber,before.ItemNumber)) item.ItemNumber=before.ItemNumber;
                if(TextEqual(edit.Supplier,before.Supplier)) item.Supplier=before.Supplier;
                if(TextEqual(edit.Category,before.Category)) item.Category=before.Category;
                if(EqualNumber(edit.RetailPrice,before.RetailPrice)) item.RetailPrice=before.RetailPrice;
                if(EqualNumber(edit.PurchasePrice,before.PurchasePrice)) item.PurchasePrice=before.PurchasePrice;
                if(EqualNumber(edit.Quantity,before.Quantity)) item.Quantity=before.Quantity;
                if (!IsAdminPrice(item.RetailPrice) || !IsAdminPrice(item.PurchasePrice))
                    throw new CatalogImportRecoveryException("validation_failed");
                if (fulfilled.TryGetValue(row.Barcode,out var matches) && matches.Any(match => SameIntent(item,match))) continue;
                if (accepted)
                {
                    if (!TextEqual(item.ProductName,before.ProductName) || !TextEqual(item.SecondProductName,before.SecondProductName) ||
                        !TextEqual(item.ItemNumber,before.ItemNumber) || !TextEqual(item.Supplier,before.Supplier) || !TextEqual(item.Category,before.Category))
                        throw new CatalogImportRecoveryException("recovery_metadata_changed");
                    var mask=new List<string>();
                    var changes=new PosCatalogImportCorrectionChanges();
                    if (!EqualNumber(item.RetailPrice,before.RetailPrice)) { mask.Add("retailPrice");changes.RetailPrice=RequiredNumber(item.RetailPrice); }
                    if (!EqualNumber(item.PurchasePrice,before.PurchasePrice)) { mask.Add("purchasePrice");changes.PurchasePrice=RequiredNumber(item.PurchasePrice); }
                    if (!EqualNumber(item.Quantity,before.Quantity)) { mask.Add("quantityDelta");changes.QuantityDelta=RequiredNumber(item.Quantity)-QuantityOrZero(before.Quantity); }
                    if (mask.Count==0) continue;
                    if (!snapshotsById.TryGetValue(before.ClientItemId,out var snapshots) || !ownersById.TryGetValue(before.ClientItemId,out var owners) ||
                        snapshots.Length!=1 || owners.Length!=1 || snapshots[0].SnapshotStatus!="available" ||
                        snapshots[0].RemoteProductId!=owners[0].RemoteProductId || string.IsNullOrWhiteSpace(snapshots[0].BaseRevision))
                        throw new CatalogImportRecoveryException("receipt_snapshot_unavailable");
                    var baseSnapshot=new PosCatalogImportCorrectionBaseSnapshot();
                    if(mask.Contains("retailPrice")) baseSnapshot.RetailPrice=snapshots[0].RetailPrice;
                    if(mask.Contains("purchasePrice")) baseSnapshot.PurchasePrice=snapshots[0].PurchasePrice;
                    if(mask.Contains("quantityDelta")) baseSnapshot.StockQuantity=snapshots[0].StockQuantity;
                    corrections.Add(new PosCatalogImportCorrectionItem { ClientItemId=before.ClientItemId,
                        RemoteProductId=snapshots[0].RemoteProductId,BaseRevision=snapshots[0].BaseRevision,
                        BaseSnapshot=baseSnapshot,
                        FieldMask=mask.OrderBy(field=>field,StringComparer.Ordinal).ToArray(),Changes=changes });
                    continue;
                }
                items.Add(item);
            }
            if (accepted)
            {
                if (corrections.Count==0) return null;
                if(corrections.Count>1000) throw new CatalogImportRecoveryException("recovery_payload_too_large");
                var correctionHash=Sha256Hex("correction-v1|"+batchCreatedAt+"|"+original.Batch.ClientImportId+"|"+originalHash+"|"+
                    Serialize(corrections.ToArray())+"|"+(sharedProof?.Hash ?? Sha256Hex(Serialize(receipt)))+
                    (operationScope==null ? "" : "|successor-of:"+operationScope));
                var correctionId="win7pos-correction-"+correctionHash.Substring(0,32);
                var correction=new PosCatalogImportCorrectionRequest { RecoveryOf=new PosCatalogImportRecoveryOf
                    { ClientImportId=original.Batch.ClientImportId,IdempotencyKey=original.Batch.IdempotencyKey,
                        PayloadHash=originalHash,OriginalRequest=original },
                    Correction=new PosCatalogImportCorrectionOperation { ClientImportId=correctionId,
                        IdempotencyKey=correctionId+":"+PosCatalogImportCorrectionContract.SchemaVersion,
                        CreatedAt=batchCreatedAt,Items=corrections.ToArray() } };
                var correctionJson=CatalogImportCorrectionTransport.SerializeSaved(correction,receipt,sharedProof);
                return new CatalogImportOutboxEntry { SharedProof=sharedProof,OperationType="catalog_import_correction",ClientImportId=correctionId,
                    IdempotencyKey=correction.Correction.IdempotencyKey,SchemaVersion=correction.SchemaVersion,Source=Source,
                    CreatedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),PayloadJson=correctionJson,PayloadHash=Sha256Hex(correctionJson) };
            }
            if (items.Count == 0) return null;
            var hash = Sha256Hex(batchCreatedAt + "|" + original.Batch.ClientImportId + "|" + originalHash + "|" + Serialize(items.ToArray())+
                (operationScope==null ? "" : "|successor-of:"+operationScope));
            var id = "win7pos-recovery-" + hash.Substring(0, 32);
            foreach (var item in items) item.ClientItemId = id + "-row-" + item.RowNumber.ToString(CultureInfo.InvariantCulture);
            var request = new PosCatalogImportRequest
            {
                SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = Source,
                Batch = new PosCatalogImportBatchRequest { ClientImportId = id, IdempotencyKey = id + ":" + PosOnlineContract.CatalogImportSchemaVersion,
                    CreatedAt = batchCreatedAt, SourceFileName = "recovery.xlsx", PreviewFingerprint = Sha256Hex(preview.Fingerprint) },
                Items = items.ToArray(), Summary = new PosCatalogImportSummaryRequest { UpdatedProducts = items.Count }
            };
            var json = Serialize(request);
            return new CatalogImportOutboxEntry { ClientImportId = id, IdempotencyKey = request.Batch.IdempotencyKey,
                SchemaVersion = request.SchemaVersion, Source = Source, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PayloadJson = json, PayloadHash = Sha256Hex(json) };
        }

        internal static bool EqualNumber(string first, string second)
        {
            decimal a, b;
            return string.Equals(first ?? "", second ?? "", StringComparison.Ordinal) ||
                decimal.TryParse(first, NumberStyles.Number, CultureInfo.InvariantCulture, out a) &&
                decimal.TryParse(second, NumberStyles.Number, CultureInfo.InvariantCulture, out b) && a == b;
        }
        private static decimal RequiredNumber(string value)
        {
            decimal number;
            if (!decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out number))
                throw new CatalogImportRecoveryException("validation_failed");
            return number;
        }
        internal static decimal QuantityOrZero(string value) => string.IsNullOrWhiteSpace(value) ? 0 : RequiredNumber(value);
        internal static bool SameIntent(PosCatalogImportItemRequest first,PosCatalogImportItemRequest second)
        {
            return first.Barcode == second.Barcode && TextEqual(first.ProductName,second.ProductName) &&
                TextEqual(first.SecondProductName,second.SecondProductName) && TextEqual(first.ItemNumber,second.ItemNumber) &&
                TextEqual(first.Supplier,second.Supplier) && TextEqual(first.Category,second.Category) &&
                EqualNumber(first.RetailPrice,second.RetailPrice) && EqualNumber(first.PurchasePrice,second.PurchasePrice) &&
                EqualNumber(first.Quantity,second.Quantity);
        }
        private static bool TextEqual(string first,string second) => (first ?? "").Trim() == (second ?? "").Trim();

        public static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                using(var sink=new CryptoStream(Stream.Null,sha,CryptoStreamMode.Write))
                using(var writer=new StreamWriter(sink,new UTF8Encoding(false),4096,true))
                {
                    writer.Write(value??string.Empty);writer.Flush();sink.FlushFinalBlock();
                }
                var bytes=sha.Hash;
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
            if(row!=null) ValidateRepresentableRow(row);
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
            var portablePath = (sourceFileName ?? string.Empty).Replace('\\', '/');
            var name = Path.GetFileName(portablePath);
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
            var serializer = new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat=true });
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.GetBuffer(),0,checked((int)stream.Length));
            }
        }
    }
}
