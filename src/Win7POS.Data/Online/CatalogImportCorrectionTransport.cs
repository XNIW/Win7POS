using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    // Pure wire boundary. Claiming, dispatch evidence, credential refresh and
    // local transactions remain owned by the catalog-import sync/recovery flow.
    internal static class CatalogImportCorrectionTransport
    {
        internal const string OperationType = "catalog_import_correction";
        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._:@-]{0,199}$", RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex("^[A-Za-z0-9:_-]{16,128}$", RegexOptions.CultureInvariant);
        private static readonly Regex UuidPattern = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.CultureInvariant);
        private static readonly Regex CanonicalHashPattern = new Regex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        internal static string SerializeSaved(PosCatalogImportCorrectionRequest request, PosCatalogImportReceiptResponse originalReceipt)
        {
            ValidateIntent(request);
            var copy = CatalogImportRecoveryService.Deserialize<PosCatalogImportCorrectionRequest>(CatalogImportRecoveryService.Serialize(request));
            copy.DeviceToken = null;
            copy.SessionToken = null;
            copy.Correction.PayloadHash = null;
            copy.RecoveryOf.OriginalRequest.DeviceToken = null;
            copy.RecoveryOf.OriginalRequest.SessionToken = null;
            var saved = new SavedCorrection { Request = copy, OriginalReceipt = originalReceipt };
            ValidateProof(saved);
            return CatalogImportRecoveryService.Serialize(saved);
        }

        internal static string ValidateSaved(CatalogImportOutboxItem item)
        {
            if (item == null || item.OperationType != OperationType || item.SchemaVersion != PosCatalogImportCorrectionContract.SchemaVersion)
                return "operation_type_mismatch";
            if (string.IsNullOrWhiteSpace(item.PayloadJson) ||
                !string.Equals(CatalogImportOutboxPayloadBuilder.Sha256Hex(item.PayloadJson), item.PayloadHash, StringComparison.Ordinal))
                return "payload_hash_mismatch";
            try
            {
                var saved = ReadSaved(item.PayloadJson);
                var request = saved.Request;
                ValidateIntent(request);
                ValidateProof(saved);
                if (request.Correction.ClientImportId != item.ClientImportId || request.Correction.IdempotencyKey != item.IdempotencyKey ||
                    !string.IsNullOrWhiteSpace(request.Correction.PayloadHash) && request.Correction.PayloadHash != item.PayloadHash)
                    return "payload_invalid";
                if (string.IsNullOrWhiteSpace(item.OriginShopId) || string.IsNullOrWhiteSpace(item.OriginShopCode) ||
                    request.ShopCode != null && request.ShopCode != item.OriginShopCode ||
                    request.RecoveryOf.OriginalRequest.ShopCode != null && request.RecoveryOf.OriginalRequest.ShopCode != item.OriginShopCode ||
                    !string.Equals(saved.OriginalReceipt.ShopId, item.OriginShopId, StringComparison.OrdinalIgnoreCase))
                    return "origin_shop_mismatch";
                return string.Empty;
            }
            catch (CatalogImportRecoveryException ex) { return ex.Code; }
            catch (SerializationException) { return "payload_invalid"; }
            catch (ArgumentException) { return "payload_invalid"; }
        }

        internal static PosCatalogImportRequest ReadIntendedRequest(string payloadJson)
        {
            return ProjectIntent(ReadSaved(payloadJson), false);
        }

        internal static PosCatalogImportCorrectionRequest ReadSavedRequest(string payloadJson)
        {
            var saved = ReadSaved(payloadJson);
            ValidateIntent(saved.Request);
            ValidateProof(saved);
            return saved.Request;
        }

        internal static PosCatalogImportRequest ReadAckIntendedRequest(string payloadJson)
        {
            return ProjectIntent(ReadSaved(payloadJson), true);
        }

        internal static bool MatchesRecoveryOriginal(CatalogImportOutboxItem item, CatalogImportOutboxItem original)
        {
            if (ValidateSaved(item).Length != 0 || original == null || original.OperationType != "catalog_import" ||
                original.SchemaVersion != PosOnlineContract.CatalogImportSchemaVersion ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(original.PayloadJson ?? "") != original.PayloadHash)
                return false;
            try
            {
                var saved = ReadSaved(item.PayloadJson);
                var recovery = saved.Request.RecoveryOf;
                return recovery.ClientImportId == original.ClientImportId && recovery.IdempotencyKey == original.IdempotencyKey &&
                    recovery.PayloadHash == original.PayloadHash && item.OriginShopId == original.OriginShopId &&
                    item.OriginShopCode == original.OriginShopCode &&
                    OriginalBusiness(recovery.OriginalRequest) == OriginalBusiness(CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(original.PayloadJson));
            }
            catch (SerializationException) { return false; }
            catch (ArgumentException) { return false; }
            catch (CatalogImportRecoveryException) { return false; }
        }

        internal static bool MatchesRetirementEvidence(CatalogImportOutboxItem item, string receiptJson)
        {
            if (ValidateSaved(item).Length != 0 || string.IsNullOrWhiteSpace(receiptJson)) return false;
            try
            {
                var saved = ReadSaved(item.PayloadJson);
                var receipt = CatalogImportRecoveryService.Deserialize<PosCatalogImportReceiptResponse>(receiptJson);
                DateTimeOffset retiredAt;
                return receipt != null && receipt.Ok && receipt.Code == "success" && receipt.Status == "retired" &&
                    (receipt.SchemaVersion == PosCatalogImportReceiptContract.SchemaVersion ||
                     receipt.SchemaVersion == PosCatalogImportReceiptContract.RetirementSchemaVersion) &&
                    receipt.OldIdentityBlocked && !receipt.SnapshotOnly && receipt.Receipt == null &&
                    DateTimeOffset.TryParse(receipt.RetiredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out retiredAt) &&
                    receipt.OriginalSchemaVersion == PosCatalogImportCorrectionContract.SchemaVersion &&
                    receipt.ClientImportId == item.ClientImportId && receipt.IdempotencyKey == item.IdempotencyKey &&
                    receipt.PayloadHash == item.PayloadHash && Matches(CanonicalHashPattern, receipt.CanonicalPayloadHash) &&
                    string.Equals(receipt.ShopId, item.OriginShopId, StringComparison.OrdinalIgnoreCase) &&
                    receipt.ShopDeviceId == saved.OriginalReceipt.ShopDeviceId;
            }
            catch (SerializationException) { return false; }
            catch (ArgumentException) { return false; }
            catch (CatalogImportRecoveryException) { return false; }
        }

        private static PosCatalogImportRequest ProjectIntent(SavedCorrection saved, bool omitProvenNoEffectPrices)
        {
            var request = saved.Request;
            var originals = ValidateIntent(request);
            var snapshots = ValidateProof(saved);
            return new PosCatalogImportRequest
            {
                SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
                Source = request.RecoveryOf.OriginalRequest.Source,
                Batch = new PosCatalogImportBatchRequest
                {
                    ClientImportId = request.Correction.ClientImportId,
                    IdempotencyKey = request.Correction.IdempotencyKey,
                    CreatedAt = request.Correction.CreatedAt
                },
                Items = request.Correction.Items.Select(item => new PosCatalogImportItemRequest
                {
                    ClientItemId = item.ClientItemId,
                    Barcode = originals[item.ClientItemId].Barcode,
                    RowNumber = originals[item.ClientItemId].RowNumber,
                    ChangeKind = "updated",
                    Operation = "upsert",
                    RetailPrice = omitProvenNoEffectPrices && ProvenNoEffect(snapshots, item, "retail") ? null : Number(item.Changes.RetailPrice),
                    PurchasePrice = omitProvenNoEffectPrices && ProvenNoEffect(snapshots, item, "purchase") ? null : Number(item.Changes.PurchasePrice),
                    Quantity = Number(item.Changes.QuantityDelta)
                }).ToArray()
            };
        }

        internal static PosCatalogImportCorrectionRequest BuildTransportRequest(CatalogImportOutboxItem item, PosTrustedDeviceSession session)
        {
            var code = ValidateSaved(item);
            if (code.Length != 0) throw new CatalogImportRecoveryException(code);
            if (session == null || string.IsNullOrWhiteSpace(session.DeviceToken) || string.IsNullOrWhiteSpace(session.SessionToken) ||
                string.IsNullOrWhiteSpace(session.PosSessionId) || string.IsNullOrWhiteSpace(session.ShopDeviceId))
                throw new CatalogImportRecoveryException("authentication_required");
            if (OutboxShopBinding.GetMismatchCode(item.OriginShopId, item.OriginShopCode, session.ShopId, session.ShopCode).Length != 0)
                throw new CatalogImportRecoveryException("origin_shop_mismatch");
            var saved = ReadSaved(item.PayloadJson);
            var request = saved.Request; // A fresh transport copy; saved bytes never change.
            if (saved.OriginalReceipt.ShopDeviceId != session.ShopDeviceId)
                throw new CatalogImportRecoveryException("origin_shop_mismatch");
            if (request.RecoveryOf.OriginalRequest.ShopDeviceId != null && request.RecoveryOf.OriginalRequest.ShopDeviceId != session.ShopDeviceId)
                throw new CatalogImportRecoveryException("origin_shop_mismatch");
            request.DeviceToken = session.DeviceToken;
            request.SessionToken = session.SessionToken;
            request.PosSessionId = session.PosSessionId;
            request.ShopDeviceId = session.ShopDeviceId;
            request.ShopCode = item.OriginShopCode;
            request.Correction.PayloadHash = item.PayloadHash;
            request.RecoveryOf.OriginalRequest.PayloadHash = request.RecoveryOf.PayloadHash;
            return request;
        }

        internal static CatalogImportAckResult ValidateResponse(CatalogImportOutboxItem item,
            PosCatalogImportCorrectionRequest request, PosCatalogImportCorrectionResponse response)
        {
            var code = ValidateSaved(item);
            if (code.Length != 0) throw new CatalogImportRecoveryException(code);
            ValidateIntent(request);
            var saved = ReadSaved(item.PayloadJson);
            var originals = ValidateIntent(saved.Request);
            var snapshots = ValidateProof(saved);
            if (BusinessIntent(request.Correction) != BusinessIntent(saved.Request.Correction) ||
                request.RecoveryOf.ClientImportId != saved.Request.RecoveryOf.ClientImportId ||
                request.RecoveryOf.IdempotencyKey != saved.Request.RecoveryOf.IdempotencyKey ||
                request.RecoveryOf.PayloadHash != saved.Request.RecoveryOf.PayloadHash ||
                request.ShopDeviceId != saved.OriginalReceipt.ShopDeviceId)
                throw new CatalogImportRecoveryException("receipt_conflict");
            if (response == null || !response.Ok) throw new CatalogImportRecoveryException("receipt_unavailable");
            if (response.SchemaVersion != PosCatalogImportCorrectionContract.SchemaVersion || response.Code != "success" ||
                response.ClientImportId != item.ClientImportId || response.IdempotencyKey != item.IdempotencyKey ||
                response.PayloadHash != item.PayloadHash || !Matches(CanonicalHashPattern, response.CanonicalPayloadHash))
                throw new CatalogImportRecoveryException("receipt_conflict");
            if (!string.Equals(response.ShopId, item.OriginShopId, StringComparison.OrdinalIgnoreCase) ||
                response.ShopDeviceId != request.ShopDeviceId)
                throw new CatalogImportRecoveryException("response_shop_mismatch");
            if (response.Status == "conflict")
                throw new CatalogImportRecoveryException(response.Reason == "revision_conflict" || response.Reason == "stale_revision"
                    ? "revision_conflict" : "receipt_conflict");
            if (response.Status != "accepted" && response.Status != "duplicate")
                throw new CatalogImportRecoveryException("receipt_conflict");
            var ack = response.Receipt;
            var count = request.Correction.Items.Length;
            if (ack == null || !ack.Ok || ack.Status != "accepted" || !Matches(UuidPattern, ack.BatchId) ||
                ack.Items == null || ack.Items.Length != count || ack.RemoteProductIds == null || ack.RemoteProductIds.Length != count ||
                ack.RemotePriceIds == null || ack.Summary == null || ack.Summary.AcceptedItemCount != count ||
                ack.Summary.DuplicateItemCount != 0 || ack.Summary.ProductCount != count)
                throw new CatalogImportRecoveryException("receipt_incomplete");

            var intended = request.Correction.Items.ToDictionary(row => row.ClientItemId, StringComparer.Ordinal);
            var products = new Dictionary<string, PosCatalogImportPersistedProductAck>(StringComparer.Ordinal);
            var productOwners = new HashSet<string>(StringComparer.Ordinal);
            var mappedProducts = new List<CatalogImportRemoteProductId>();
            foreach (var mapping in ack.RemoteProductIds)
            {
                PosCatalogImportCorrectionItem expected;
                if (mapping == null || !intended.TryGetValue(mapping.ClientItemId ?? "", out expected) ||
                    mapping.RemoteProductId != expected.RemoteProductId || !ValidRevision(mapping.AuthoritativeRevision) ||
                    products.ContainsKey(mapping.ClientItemId) || !productOwners.Add(mapping.RemoteProductId))
                    throw new CatalogImportRecoveryException("receipt_conflict");
                products.Add(mapping.ClientItemId, mapping);
                mappedProducts.Add(new CatalogImportRemoteProductId { ClientItemId = mapping.ClientItemId,
                    Barcode = originals[mapping.ClientItemId].Barcode, RemoteProductId = mapping.RemoteProductId });
            }

            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            var unchangedPrices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in ack.Items)
            {
                PosCatalogImportPersistedProductAck product;
                if (row == null || !products.TryGetValue(row.ClientItemId ?? "", out product) || !seenItems.Add(row.ClientItemId) ||
                    row.Status != "accepted" || row.RemoteProductId != product.RemoteProductId ||
                    !ValidRevision(row.AuthoritativeRevision) || row.AuthoritativeRevision != product.AuthoritativeRevision)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                var fields = row.UnchangedFields ?? Array.Empty<string>();
                if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                foreach (var field in fields)
                {
                    var type = field == "retailPrice" ? "retail" : field == "purchasePrice" ? "purchase" : null;
                    if (type == null || !intended[row.ClientItemId].FieldMask.Contains(field) ||
                        !ProvenNoEffect(snapshots, intended[row.ClientItemId], type))
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    unchangedPrices.Add(PriceKey(row.ClientItemId, type));
                }
            }

            var requiredPrices = new HashSet<string>(StringComparer.Ordinal);
            var permittedPrices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in request.Correction.Items)
            {
                foreach (var type in new[] { "retail", "purchase" })
                {
                    if (!(type == "retail" ? row.Changes.RetailPrice : row.Changes.PurchasePrice).HasValue) continue;
                    var key = PriceKey(row.ClientItemId, type);
                    if (unchangedPrices.Contains(key)) continue;
                    permittedPrices.Add(key);
                    requiredPrices.Add(key);
                }
            }
            if (ack.RemotePriceIds.Length < requiredPrices.Count || ack.RemotePriceIds.Length > permittedPrices.Count)
                throw new CatalogImportRecoveryException("receipt_incomplete");
            var seenPrices = new HashSet<string>(StringComparer.Ordinal);
            var priceOwners = new HashSet<string>(StringComparer.Ordinal);
            var mappedPrices = new List<CatalogImportRemotePriceId>();
            foreach (var price in ack.RemotePriceIds)
            {
                PosCatalogImportPersistedProductAck product;
                if (price == null || !products.TryGetValue(price.ClientItemId ?? "", out product) ||
                    price.RemoteProductId != product.RemoteProductId || !Matches(UuidPattern, price.RemotePriceId) ||
                    price.PriceType != "retail" && price.PriceType != "purchase" ||
                    !permittedPrices.Contains(PriceKey(price.ClientItemId, price.PriceType)) ||
                    !seenPrices.Add(PriceKey(price.ClientItemId, price.PriceType)) || !priceOwners.Add(price.RemotePriceId))
                    throw new CatalogImportRecoveryException("receipt_conflict");
                mappedPrices.Add(new CatalogImportRemotePriceId { ClientItemId = price.ClientItemId,
                    Barcode = originals[price.ClientItemId].Barcode, PriceType = price.PriceType, RemotePriceId = price.RemotePriceId });
            }
            if (!requiredPrices.IsSubsetOf(seenPrices)) throw new CatalogImportRecoveryException("receipt_incomplete");
            return new CatalogImportAckResult { ServerImportId = ack.BatchId, RemoteProductIds = mappedProducts, RemotePriceIds = mappedPrices };
        }

        private static Dictionary<string, PosCatalogImportProductSnapshot> ValidateProof(SavedCorrection saved)
        {
            var request = saved?.Request;
            var proof = saved?.OriginalReceipt;
            if (request?.RecoveryOf == null || proof == null || !proof.Ok || proof.Code != "success" || proof.Status != "accepted" ||
                (proof.SchemaVersion != PosCatalogImportReceiptContract.SchemaVersion && proof.SchemaVersion != PosCatalogImportReceiptContract.RetirementSchemaVersion) ||
                proof.Receipt == null || !proof.Receipt.Ok || (proof.Receipt.Status != "accepted" && proof.Receipt.Status != "duplicate") ||
                proof.OriginalSchemaVersion != PosOnlineContract.CatalogImportSchemaVersion ||
                proof.ClientImportId != request.RecoveryOf.ClientImportId || proof.IdempotencyKey != request.RecoveryOf.IdempotencyKey ||
                proof.PayloadHash != request.RecoveryOf.PayloadHash || !Matches(CanonicalHashPattern, proof.CanonicalPayloadHash) ||
                string.IsNullOrWhiteSpace(proof.ShopId) || string.IsNullOrWhiteSpace(proof.ShopDeviceId) ||
                proof.CurrentProductSnapshots == null || proof.Receipt.RemoteProductIds == null)
                throw new CatalogImportRecoveryException("receipt_required");
            var products = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var product in proof.Receipt.RemoteProductIds)
            {
                if (product == null || product.ClientItemId == null || products.ContainsKey(product.ClientItemId))
                    throw new CatalogImportRecoveryException("receipt_conflict");
                products.Add(product.ClientItemId, product.RemoteProductId);
            }
            var snapshots = new Dictionary<string, PosCatalogImportProductSnapshot>(StringComparer.Ordinal);
            foreach (var snapshot in proof.CurrentProductSnapshots)
            {
                string remoteId;
                if (snapshot == null || snapshot.ClientItemId == null || snapshots.ContainsKey(snapshot.ClientItemId) ||
                    !products.TryGetValue(snapshot.ClientItemId, out remoteId) || snapshot.RemoteProductId != remoteId)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                snapshots.Add(snapshot.ClientItemId, snapshot);
            }
            foreach (var row in request.Correction.Items)
            {
                PosCatalogImportProductSnapshot snapshot;
                if (!snapshots.TryGetValue(row.ClientItemId, out snapshot) || snapshot.SnapshotStatus != "available" ||
                    snapshot.RemoteProductId != row.RemoteProductId || snapshot.BaseRevision != row.BaseRevision ||
                    !ValidRevision(snapshot.BaseRevision) ||
                    row.FieldMask.Contains("retailPrice") && row.BaseSnapshot.RetailPrice != snapshot.RetailPrice ||
                    row.FieldMask.Contains("purchasePrice") && row.BaseSnapshot.PurchasePrice != snapshot.PurchasePrice ||
                    row.FieldMask.Contains("quantityDelta") && row.BaseSnapshot.StockQuantity != snapshot.StockQuantity)
                    throw new CatalogImportRecoveryException("receipt_required");
            }
            return snapshots;
        }

        private static bool ProvenNoEffect(Dictionary<string, PosCatalogImportProductSnapshot> snapshots, PosCatalogImportCorrectionItem row, string priceType)
        {
            var snapshot = snapshots[row.ClientItemId];
            var desired = priceType == "retail" ? row.Changes.RetailPrice : row.Changes.PurchasePrice;
            var current = priceType == "retail" ? snapshot.RetailPrice : snapshot.PurchasePrice;
            return desired.HasValue && current.HasValue && desired.Value == current.Value;
        }

        private static Dictionary<string, PosCatalogImportItemRequest> ValidateIntent(PosCatalogImportCorrectionRequest request)
        {
            var operation = request?.Correction;
            var recovery = request?.RecoveryOf;
            var original = recovery?.OriginalRequest;
            DateTimeOffset createdAt;
            if (request?.SchemaVersion != PosCatalogImportCorrectionContract.SchemaVersion || operation == null || recovery == null ||
                !Matches(IdPattern, operation.ClientImportId) || !Matches(IdPattern, operation.IdempotencyKey) ||
                !Matches(IdPattern, recovery.ClientImportId) || !Matches(IdPattern, recovery.IdempotencyKey) || !Matches(HashPattern, recovery.PayloadHash) ||
                operation.ClientImportId == recovery.ClientImportId || operation.IdempotencyKey == recovery.IdempotencyKey ||
                !DateTimeOffset.TryParse(operation.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out createdAt) ||
                original?.SchemaVersion != PosOnlineContract.CatalogImportSchemaVersion || original.Batch == null ||
                original.Batch.ClientImportId != recovery.ClientImportId || original.Batch.IdempotencyKey != recovery.IdempotencyKey ||
                original.PayloadHash != null && original.PayloadHash != recovery.PayloadHash ||
                original.Items == null || original.Items.Length == 0 || original.Items.Length > 5000 ||
                operation.Items == null || operation.Items.Length == 0 || operation.Items.Length > Math.Min(1000, original.Items.Length))
                throw new CatalogImportRecoveryException("payload_invalid");
            var originals = new Dictionary<string, PosCatalogImportItemRequest>(StringComparer.Ordinal);
            var barcodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in original.Items)
            {
                if (row == null || !Matches(IdPattern, row.ClientItemId) || string.IsNullOrWhiteSpace(row.Barcode) || row.RowNumber <= 0 ||
                    originals.ContainsKey(row.ClientItemId) || !barcodes.Add(row.Barcode))
                    throw new CatalogImportRecoveryException("payload_invalid");
                originals.Add(row.ClientItemId, row);
            }
            var seenRows = new HashSet<string>(StringComparer.Ordinal);
            var seenProducts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in operation.Items)
            {
                if (row == null || !originals.ContainsKey(row.ClientItemId ?? "") || !seenRows.Add(row.ClientItemId) ||
                    !Matches(UuidPattern, row.RemoteProductId) || !seenProducts.Add(row.RemoteProductId) || !ValidRevision(row.BaseRevision) ||
                    row.FieldMask == null || row.FieldMask.Length == 0 || row.FieldMask.Length > 3 || row.Changes == null || row.BaseSnapshot == null)
                    throw new CatalogImportRecoveryException("payload_invalid");
                var mask = new HashSet<string>(row.FieldMask, StringComparer.Ordinal);
                var baseFields = new HashSet<string>(row.BaseSnapshot.Keys, StringComparer.Ordinal);
                var expectedBaseFields = new HashSet<string>(mask.Select(field => field == "quantityDelta" ? "stockQuantity" : field), StringComparer.Ordinal);
                if (mask.Count != row.FieldMask.Length || mask.Any(field => field != "retailPrice" && field != "purchasePrice" && field != "quantityDelta") ||
                    !baseFields.SetEquals(expectedBaseFields) ||
                    mask.Contains("retailPrice") != row.Changes.RetailPrice.HasValue || mask.Contains("purchasePrice") != row.Changes.PurchasePrice.HasValue ||
                    mask.Contains("quantityDelta") != row.Changes.QuantityDelta.HasValue || !ValidPrice(row.Changes.RetailPrice) || !ValidPrice(row.Changes.PurchasePrice) ||
                    row.Changes.QuantityDelta.HasValue && (row.Changes.QuantityDelta.Value == 0 || row.Changes.QuantityDelta.Value < -1000000000m ||
                        row.Changes.QuantityDelta.Value > 1000000000m ||
                        decimal.Round(row.Changes.QuantityDelta.Value, 3) != row.Changes.QuantityDelta.Value))
                    throw new CatalogImportRecoveryException("payload_invalid");
            }
            return originals;
        }

        private static SavedCorrection ReadSaved(string json)
        {
            var saved = CatalogImportRecoveryService.Deserialize<SavedCorrection>(json);
            if (saved?.Request == null) throw new CatalogImportRecoveryException("payload_invalid");
            return saved;
        }
        private static bool Matches(Regex pattern, string value) => value != null && pattern.IsMatch(value);
        private static bool ValidPrice(decimal? value) => !value.HasValue || value.Value >= 0 && value.Value <= 999999999m && decimal.Round(value.Value, 3) == value.Value;
        private static string BusinessIntent(PosCatalogImportCorrectionOperation operation) => CatalogImportRecoveryService.Serialize(
            new PosCatalogImportCorrectionOperation { ClientImportId = operation.ClientImportId, IdempotencyKey = operation.IdempotencyKey,
                CreatedAt = operation.CreatedAt, Items = operation.Items });
        private static string OriginalBusiness(PosCatalogImportRequest request)
        {
            var copy = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(CatalogImportRecoveryService.Serialize(request));
            if (copy == null) throw new CatalogImportRecoveryException("payload_invalid");
            copy.PayloadHash = null;
            copy.DeviceToken = null;
            copy.SessionToken = null;
            return CatalogImportRecoveryService.Serialize(copy);
        }
        private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);
        private static string PriceKey(string itemId, string priceType) => itemId + "\0" + priceType;
        private static bool ValidRevision(string value)
        {
            DateTimeOffset parsed;
            return value != null && DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
        }

        [DataContract]
        private sealed class SavedCorrection
        {
            [DataMember(Name = "request")] public PosCatalogImportCorrectionRequest Request { get; set; }
            [DataMember(Name = "originalReceipt")] public PosCatalogImportReceiptResponse OriginalReceipt { get; set; }
        }
    }
}
