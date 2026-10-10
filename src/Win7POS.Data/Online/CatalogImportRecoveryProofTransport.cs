using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    // The proof is an authenticated transport of the exact persisted bytes. It
    // neither edits the outbox nor records delivery of an economic operation.
    internal static partial class CatalogImportRecoveryProofTransport
    {
        private static readonly Regex Uuid = new Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex CanonicalHash = new Regex("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex DeclaredHash = new Regex("^[A-Za-z0-9:_-]{16,128}\\z", RegexOptions.CultureInvariant);

        internal static PosCatalogImportRecoveryPlanChildRequest ProjectionForTransport(
            PosCatalogImportCorrectionRequest savedCorrection, string verifiedOriginalId)
        {
            if (savedCorrection?.Correction == null || !IsUuid(verifiedOriginalId))
                throw new CatalogImportRecoveryException("payload_invalid");
            return new PosCatalogImportRecoveryPlanChildRequest
            {
                SchemaVersion = PosCatalogImportCorrectionContract.SchemaVersion,
                RecoveryOf = new PosCatalogImportRecoveryReference { VerifiedOriginalId = verifiedOriginalId },
                Correction = CatalogImportRecoveryService.Deserialize<PosCatalogImportCorrectionOperation>(
                    CatalogImportRecoveryService.Serialize(savedCorrection.Correction))
            };
        }

        internal static PosCatalogImportRecoveryPlanChildRequest ProjectionForTransport(
            PosCatalogImportRequest original, string payloadHash)
        {
            if (original?.Batch == null) throw new CatalogImportRecoveryException("payload_invalid");
            var copy = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(CatalogImportRecoveryService.Serialize(original));
            copy.Batch.AttemptCount = 1;
            return new PosCatalogImportRecoveryPlanChildRequest
            {
                SchemaVersion = copy.SchemaVersion, AppVersion = copy.AppVersion, Source = copy.Source,
                Batch = copy.Batch, Summary = copy.Summary, Items = copy.Items, PayloadHash = payloadHash
            };
        }

        internal static string CreateUploadId(string shopId, string shopDeviceId, string rawSha256, string mode)
        {
            if (!IsUuid(shopId) || !IsUuid(shopDeviceId) || !IsHash(rawSha256) || (mode != "original" && mode != "plan"))
                throw new CatalogImportRecoveryException("payload_invalid");
            var hash = CatalogImportOutboxPayloadBuilder.Sha256Hex("win7pos-recovery-upload-v1\n" +
                shopId.ToLowerInvariant() + "\n" + shopDeviceId.ToLowerInvariant() + "\n" + mode + "\n" + rawSha256);
            // The RFC 4122 version/variant bits are fixed; all other bits are
            // derived from immutable scope and bytes, including after a restart.
            return hash.Substring(0, 8) + "-" + hash.Substring(8, 4) + "-4" + hash.Substring(13, 3) +
                "-8" + hash.Substring(17, 3) + "-" + hash.Substring(20, 12);
        }

        internal static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> UploadAsync(
            PosAdminWebClient client, string rawJson, string uploadId, string mode,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken cancellationToken,
            string originalKind = null, string declaredPayloadHash = null, string verifiedOriginalId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phased = RequiresPhasedUpload(rawJson);
            var manifest = BuildManifest(rawJson, cancellationToken, phased);
            return phased
                ? await UploadPhasedAsync(client, rawJson, manifest, uploadId, mode, freshSession, cancellationToken,
                    originalKind, declaredPayloadHash, verifiedOriginalId).ConfigureAwait(false)
                : await UploadCoreAsync(client, rawJson, manifest, uploadId, mode, freshSession,
                    cancellationToken, originalKind, declaredPayloadHash).ConfigureAwait(false);
        }

        private static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> UploadCoreAsync(
            PosAdminWebClient client, string rawJson, UploadManifest manifest, string uploadId, string mode,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken cancellationToken,
            string originalKind, string declaredPayloadHash)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (rawJson == null || !IsUuid(uploadId) || (mode != "original" && mode != "plan"))
                throw new CatalogImportRecoveryException("payload_invalid");
            if (mode == "original" ?
                (originalKind != "ordinary" && originalKind != "correction") || declaredPayloadHash == null || !DeclaredHash.IsMatch(declaredPayloadHash) :
                originalKind != null || declaredPayloadHash != null)
                throw new CatalogImportRecoveryException("payload_invalid");
            var binding = new SessionBinding(freshSession);
            PosOnlineResult<PosCatalogImportRecoveryMultipartResponse> result = null;
            string manifestHash = null;
            var index = 0;
            foreach (var bytes in ReadUtf8Chunks(rawJson))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = Authenticate(new PosCatalogImportRecoveryUploadRequest
                {
                    UploadId = uploadId, Mode = mode, TotalByteLength = manifest.TotalByteLength, RawSha256 = manifest.RawSha256,
                    OriginalKind = originalKind, DeclaredPayloadHash = declaredPayloadHash,
                    Parts = manifest.Parts, PartIndex = index,
                    ContentBase64 = Convert.ToBase64String(bytes)
                }, binding.Read());
                result = await client.CatalogImportRecoveryUploadAsync(request, cancellationToken).ConfigureAwait(false);
                if (!result.Success) return result;
                var response = result.Value;
                ValidateCommon(response, binding);
                if (response.Status != "uploaded" || response.UploadId != uploadId || response.PartIndex != index ||
                    !IsHash(response.ManifestSha256) || manifestHash != null && manifestHash != response.ManifestSha256)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                manifestHash = response.ManifestSha256;
                index++;
            }
            return result;
        }

        internal static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> EnsureProofAsync(
            PosAdminWebClient client, CatalogImportOutboxItem original,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken cancellationToken)
        {
            if (original == null || string.IsNullOrEmpty(original.PayloadJson))
                throw new CatalogImportRecoveryException("payload_invalid");
            cancellationToken.ThrowIfCancellationRequested();
            var phased = RequiresPhasedUpload(original.PayloadJson);
            var manifest = BuildManifest(original.PayloadJson, cancellationToken, phased);
            if (!string.Equals(manifest.RawSha256.Substring(7), original.PayloadHash, StringComparison.Ordinal))
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var binding = new SessionBinding(freshSession);
            var session = binding.Read();
            if (OutboxShopBinding.GetMismatchCode(original.OriginShopId, original.OriginShopCode, session.ShopId, session.ShopCode).Length != 0)
                throw new CatalogImportRecoveryException("origin_shop_mismatch");
            Identity expected;
            if (phased)
            {
                var streamed = ReadPhasedIdentity(original, cancellationToken);
                expected = new Identity { SchemaVersion = streamed.SchemaVersion, ItemCount = streamed.ItemCount };
            }
            else expected = ReadIdentity(original);
            if (expected.ItemCount < 1 || expected.ItemCount > PosCatalogImportRecoveryMultipartContract.MaximumOriginalItems)
                throw new CatalogImportRecoveryException("payload_invalid");
            var rawHash = manifest.RawSha256;
            var uploadId = CreateUploadId(session.ShopId, session.ShopDeviceId, rawHash, "original");
            var kind = expected.SchemaVersion == PosCatalogImportCorrectionContract.SchemaVersion ? "correction" : "ordinary";
            var upload = phased
                ? await UploadPhasedAsync(client, original.PayloadJson, manifest, uploadId, "original", binding.Read,
                    cancellationToken, kind, original.PayloadHash, null).ConfigureAwait(false)
                : await UploadCoreAsync(client, original.PayloadJson, manifest, uploadId, "original", binding.Read,
                    cancellationToken, kind, original.PayloadHash).ConfigureAwait(false);
            if (!upload.Success) return upload;
            cancellationToken.ThrowIfCancellationRequested();
            var response = phased
                ? await CompletePhasedAsync(client, uploadId, manifest.RawSha256, false, binding.Read, cancellationToken).ConfigureAwait(false)
                : await client.CatalogImportRecoveryFinalizeAsync(Authenticate(new PosCatalogImportRecoveryHandleRequest
                    { UploadId = uploadId }, binding.Read()), cancellationToken).ConfigureAwait(false);
            if (!response.Success) return response;
            var proof = response.Value;
            ValidateCommon(proof, binding);
            if (proof.Status != "verified" || proof.VerifiedOriginalId != uploadId || proof.RawSha256 != rawHash ||
                proof.OriginalSchemaVersion != expected.SchemaVersion || proof.ClientImportId != original.ClientImportId ||
                proof.IdempotencyKey != original.IdempotencyKey || proof.PayloadHash != original.PayloadHash ||
                proof.ItemCount != expected.ItemCount || !IsHash(proof.CanonicalPayloadHash))
                throw new CatalogImportRecoveryException("receipt_conflict");
            return response;
        }

        internal static async Task<PosOnlineResult<PosCatalogImportReceiptResponse>> QueryReceiptAsync(
            PosAdminWebClient client, PosCatalogImportRecoveryMultipartResponse proof, bool retire,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken cancellationToken)
        {
            var binding = new SessionBinding(freshSession);
            ValidateCommon(proof, binding);
            if (!IsUuid(proof.VerifiedOriginalId) || !IsHash(proof.CanonicalPayloadHash) || !proof.ItemCount.HasValue)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var request = Authenticate(new PosCatalogImportRecoveryHandleRequest { VerifiedOriginalId = proof.VerifiedOriginalId }, binding.Read());
            var result = retire
                ? await client.CatalogImportRecoveryRetireAsync(request, cancellationToken).ConfigureAwait(false)
                : await client.CatalogImportRecoveryReceiptAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return Failure<PosCatalogImportReceiptResponse>(result);
            var receipt = result.Value;
            ValidateCommon(receipt, binding);
            if (receipt.VerifiedOriginalId != proof.VerifiedOriginalId || receipt.OriginalSchemaVersion != proof.OriginalSchemaVersion ||
                receipt.ClientImportId != proof.ClientImportId || receipt.IdempotencyKey != proof.IdempotencyKey ||
                receipt.PayloadHash != proof.PayloadHash || receipt.CanonicalPayloadHash != proof.CanonicalPayloadHash)
                throw new CatalogImportRecoveryException("receipt_conflict");
            if (receipt.Status == "accepted")
            {
                if (receipt.TotalItemCount != proof.ItemCount) throw new CatalogImportRecoveryException("receipt_incomplete");
                var complete = await ReadCompleteReceiptAsync(client, receipt, binding.Read, cancellationToken).ConfigureAwait(false);
                if (!complete.Success) return Failure<PosCatalogImportReceiptResponse>(complete);
                receipt = complete.Value;
            }
            else if (receipt.Status == "not_found")
            {
                if (retire || receipt.SnapshotOnly != true || receipt.ReplacementAllowed != false || receipt.Receipt != null)
                    throw new CatalogImportRecoveryException("receipt_conflict");
            }
            else if (receipt.Status == "retired")
            {
                if (receipt.OldIdentityBlocked != true || string.IsNullOrWhiteSpace(receipt.RetiredAt) || receipt.Receipt != null)
                    throw new CatalogImportRecoveryException("receipt_conflict");
            }
            else throw new CatalogImportRecoveryException(receipt.Reason == "revision_conflict" ? "revision_conflict" : "receipt_conflict");
            return PosOnlineResult<PosCatalogImportReceiptResponse>.Ok(new PosCatalogImportReceiptResponse
            {
                Ok = true, Code = "success", SchemaVersion = retire ? PosCatalogImportReceiptContract.RetirementSchemaVersion : PosCatalogImportReceiptContract.SchemaVersion,
                OriginalSchemaVersion = receipt.OriginalSchemaVersion, Status = receipt.Status,
                ShopId = receipt.ShopId, ShopDeviceId = receipt.ShopDeviceId, ClientImportId = receipt.ClientImportId,
                IdempotencyKey = receipt.IdempotencyKey, PayloadHash = receipt.PayloadHash, CanonicalPayloadHash = receipt.CanonicalPayloadHash,
                SnapshotOnly = receipt.SnapshotOnly == true, ReplacementAllowed = receipt.ReplacementAllowed == true,
                OldIdentityBlocked = receipt.OldIdentityBlocked == true, RetiredAt = receipt.RetiredAt, Reason = receipt.Reason,
                Receipt = receipt.Receipt, CurrentProductSnapshots = receipt.CurrentProductSnapshots
            }, result.ClientRequestId, result.ServerRequestId, result.CfRay, result.HttpStatus,
                result.ElapsedMilliseconds, result.ResponseContentType, result.ResponseLength);
        }

        internal static async Task<PosOnlineResult<PosCatalogImportReceiptResponse>> QueryPlannedReceiptAsync(
            PosAdminWebClient client, CatalogImportSavedRemotePlan saved, CatalogImportOutboxItem child, bool retire,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken cancellationToken)
        {
            var binding = new SessionBinding(freshSession);
            var session = binding.Read();
            if (child == null || string.IsNullOrEmpty(child.PayloadJson) ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(child.PayloadJson) != child.PayloadHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            if (OutboxShopBinding.GetMismatchCode(child.OriginShopId, child.OriginShopCode, session.ShopId, session.ShopCode).Length != 0)
                throw new CatalogImportRecoveryException("origin_shop_mismatch");
            var document = saved?.Document;
            var planReceipt = saved?.Receipt;
            if (document == null || document.SchemaVersion != PosCatalogImportRecoveryMultipartContract.PlanSchemaVersion ||
                !IsUuid(document.PlanId) || !IsUuid(document.VerifiedOriginalId) || document.Parts == null ||
                saved.PartIndex < 0 || saved.PartIndex >= document.Parts.Length)
                throw new CatalogImportRecoveryException("receipt_conflict");
            ValidateCommon(planReceipt, binding);
            if (planReceipt.Status != "planned" || planReceipt.PlanId != document.PlanId ||
                planReceipt.VerifiedOriginalId != document.VerifiedOriginalId || !IsHash(planReceipt.PlanCanonicalHash) ||
                planReceipt.PartCount != document.Parts.Length || planReceipt.Parts?.Length != document.Parts.Length)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var descriptions = planReceipt.Parts.Where(part => part != null && part.Index == saved.PartIndex).ToArray();
            var projections = document.Parts.Where(part => part != null && part.Index == saved.PartIndex).ToArray();
            if (descriptions.Length != 1 || projections.Length != 1 || projections[0].Request == null)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var expected = descriptions[0];
            var projected = projections[0].Request;
            if (expected.ClientImportId != child.ClientImportId || expected.IdempotencyKey != child.IdempotencyKey ||
                expected.DeclaredPayloadHash != child.PayloadHash || !IsHash(expected.PayloadHash) ||
                projected.SchemaVersion != child.SchemaVersion)
                throw new CatalogImportRecoveryException("receipt_conflict");
            PosCatalogImportRecoveryPlanChildRequest localProjection;
            if (child.OperationType == CatalogImportCorrectionTransport.OperationType)
            {
                // Resolve the shared local proof only for validation/projection.
                // Its hydrated JSON is never hashed as, or uploaded as, the child.
                var validation = CatalogImportCorrectionTransport.ValidateSaved(child);
                if (validation.Length != 0) throw new CatalogImportRecoveryException(validation);
                localProjection = ProjectionForTransport(CatalogImportCorrectionTransport.ReadSavedRequest(child.PayloadJson, child.SharedProof), document.VerifiedOriginalId);
                localProjection.Correction.PayloadHash = child.PayloadHash;
            }
            else if (child.OperationType == "catalog_import")
            {
                var validation = CatalogImportOutboxPayloadValidator.Validate(child);
                if (!validation.IsValid) throw new CatalogImportRecoveryException(validation.Code);
                localProjection = ProjectionForTransport(validation.Request, child.PayloadHash);
            }
            else throw new CatalogImportRecoveryException("operation_type_mismatch");
            if (CatalogImportRecoveryService.Serialize(localProjection) != CatalogImportRecoveryService.Serialize(projected))
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var itemIds = projected.Correction == null
                ? projected.Items?.Select(item => item.ClientItemId).ToArray()
                : projected.Correction.Items?.Select(item => item.ClientItemId).ToArray();
            if (itemIds == null || itemIds.Length < 1 || itemIds.Length > CatalogImportPlanBuilder.MaximumRowsPerRequest ||
                expected.ItemCount != itemIds.Length || itemIds.Distinct(StringComparer.Ordinal).Count() != itemIds.Length)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var request = Authenticate(new PosCatalogImportRecoveryHandleRequest { PlanId = document.PlanId, PartIndex = saved.PartIndex }, binding.Read());
            var result = retire
                ? await client.CatalogImportRecoveryRetireAsync(request, cancellationToken).ConfigureAwait(false)
                : await client.CatalogImportRecoveryReceiptAsync(request, cancellationToken).ConfigureAwait(false);
            // A 404 has no authoritative child binding. Preserve the transport
            // failure; never turn it into a not_found economic receipt.
            if (!result.Success) return Failure<PosCatalogImportReceiptResponse>(result);
            var receipt = result.Value;
            ValidateCommon(receipt, binding);
            if (receipt.PlanId != document.PlanId || receipt.PartIndex != saved.PartIndex || receipt.VerifiedOriginalId != null ||
                receipt.OriginalSchemaVersion != child.SchemaVersion || receipt.ClientImportId != child.ClientImportId ||
                receipt.IdempotencyKey != child.IdempotencyKey || receipt.PayloadHash != child.PayloadHash ||
                receipt.CanonicalPayloadHash != expected.PayloadHash || receipt.ParentStatus != null ||
                receipt.PartCount.HasValue || receipt.AcceptedPartCount.HasValue)
                throw new CatalogImportRecoveryException("receipt_conflict");
            if (receipt.Status == "accepted")
            {
                // The child receipt/retire endpoint returns the complete <=1000
                // row ACK. Do not page or adopt an external ACK implicitly.
                if (receipt.TotalItemCount != itemIds.Length || receipt.Complete != true)
                    throw new CatalogImportRecoveryException("receipt_incomplete");
                var complete = await ReadCompleteReceiptAsync(client, receipt, binding.Read, cancellationToken).ConfigureAwait(false);
                if (!complete.Success) return Failure<PosCatalogImportReceiptResponse>(complete);
                receipt = complete.Value;
                if (!new HashSet<string>(itemIds, StringComparer.Ordinal).SetEquals(receipt.Receipt.Items.Select(item => item.ClientItemId)))
                    throw new CatalogImportRecoveryException("receipt_conflict");
            }
            else if (receipt.Status == "not_found")
            {
                if (retire || receipt.SnapshotOnly != true || receipt.ReplacementAllowed != false || receipt.Receipt != null)
                    throw new CatalogImportRecoveryException("receipt_conflict");
            }
            else if (receipt.Status == "retired")
            {
                if (!retire || receipt.OldIdentityBlocked != true || string.IsNullOrWhiteSpace(receipt.RetiredAt) || receipt.Receipt != null)
                    throw new CatalogImportRecoveryException("receipt_conflict");
            }
            else throw new CatalogImportRecoveryException(receipt.Reason == "revision_conflict" ? "revision_conflict" : "receipt_conflict");
            return PosOnlineResult<PosCatalogImportReceiptResponse>.Ok(new PosCatalogImportReceiptResponse
            {
                Ok = true, Code = "success", SchemaVersion = retire ? PosCatalogImportReceiptContract.RetirementSchemaVersion : PosCatalogImportReceiptContract.SchemaVersion,
                OriginalSchemaVersion = receipt.OriginalSchemaVersion, Status = receipt.Status, ShopId = receipt.ShopId,
                ShopDeviceId = receipt.ShopDeviceId, ClientImportId = receipt.ClientImportId, IdempotencyKey = receipt.IdempotencyKey,
                PayloadHash = receipt.PayloadHash, CanonicalPayloadHash = receipt.CanonicalPayloadHash,
                SnapshotOnly = receipt.SnapshotOnly == true, ReplacementAllowed = receipt.ReplacementAllowed == true,
                OldIdentityBlocked = receipt.OldIdentityBlocked == true, RetiredAt = receipt.RetiredAt,
                Receipt = receipt.Receipt, CurrentProductSnapshots = receipt.CurrentProductSnapshots
            }, result.ClientRequestId, result.ServerRequestId, result.CfRay, result.HttpStatus,
                result.ElapsedMilliseconds, result.ResponseContentType, result.ResponseLength);
        }

        internal static void ValidateReceiptSelector(PosCatalogImportRecoveryHandleRequest request)
        {
            if (request == null || request.UploadId != null ||
                !(IsUuid(request.VerifiedOriginalId) && request.PlanId == null && !request.PartIndex.HasValue ||
                  request.VerifiedOriginalId == null && IsUuid(request.PlanId) && request.PartIndex.HasValue && request.PartIndex >= 0 &&
                  request.PartIndex < PosCatalogImportRecoveryMultipartContract.MaximumPlanChildren))
                throw new CatalogImportRecoveryException("payload_invalid");
        }

        // Each range is bound to the same server-computed hash of the durable
        // PostgreSQL ACK. Live snapshots are deliberately outside that hash.
        internal static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> ReadCompleteReceiptAsync(
            PosAdminWebClient client, PosCatalogImportRecoveryMultipartResponse first,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken cancellationToken)
        {
            var binding = new SessionBinding(freshSession);
            ValidateCommon(first, binding);
            if (first.Status != "accepted" || !IsHash(first.ReceiptSha256) || first.ReceiptEncoding != "postgres-jsonb-text-v1" ||
                first.TotalItemCount < 1 || first.TotalItemCount > PosCatalogImportRecoveryMultipartContract.MaximumOriginalItems ||
                !first.TotalItemCount.HasValue || first.Offset != 0 ||
                !(IsUuid(first.VerifiedOriginalId) && first.PlanId == null && !first.PartIndex.HasValue ||
                  first.VerifiedOriginalId == null && IsUuid(first.PlanId) && first.PartIndex.HasValue && first.PartIndex >= 0 &&
                  first.PartIndex < PosCatalogImportRecoveryMultipartContract.MaximumPlanChildren))
                throw new CatalogImportRecoveryException("receipt_incomplete");
            var allItems = new List<PosCatalogImportPersistedItemAck>();
            var allProducts = new List<PosCatalogImportPersistedProductAck>();
            var allPrices = new List<PosCatalogImportPersistedPriceAck>();
            var allSnapshots = new List<PosCatalogImportProductSnapshot>();
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            var seenProducts = new HashSet<string>(StringComparer.Ordinal);
            var seenPrices = new HashSet<string>(StringComparer.Ordinal);
            var seenSnapshots = new HashSet<string>(StringComparer.Ordinal);
            var page = first;
            var offset = 0;
            var summary = first.Receipt?.Summary;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateCommon(page, binding);
                if (offset > 0 && first.PlanId != null &&
                    (page.PlanId != first.PlanId || page.PartIndex != first.PartIndex || page.VerifiedOriginalId != null ||
                     page.OriginalSchemaVersion != null || page.ClientImportId != first.ClientImportId ||
                     page.IdempotencyKey != first.IdempotencyKey || page.PayloadHash != first.PayloadHash ||
                     page.CanonicalPayloadHash != first.CanonicalPayloadHash ||
                     !page.PartCount.HasValue || page.PartCount < 1 || !page.AcceptedPartCount.HasValue ||
                     page.AcceptedPartCount < 1 || page.AcceptedPartCount > page.PartCount ||
                     (page.ParentStatus != "partial" && page.ParentStatus != "complete") ||
                     (page.ParentStatus == "complete") != (page.AcceptedPartCount == page.PartCount) ||
                     first.PartCount.HasValue && page.PartCount != first.PartCount))
                    throw new CatalogImportRecoveryException("receipt_conflict");
                var ack = page.Receipt;
                if (page.Status != "accepted" || page.ReceiptSha256 != first.ReceiptSha256 || page.ReceiptEncoding != first.ReceiptEncoding ||
                    page.TotalItemCount != first.TotalItemCount || page.Offset != offset || page.Limit < 1 ||
                    page.Limit > PosCatalogImportRecoveryMultipartContract.MaximumReceiptPageItems || !page.Limit.HasValue ||
                    ack == null || !ack.Ok || ack.Status != "accepted" || !IsUuid(ack.BatchId) || ack.BatchId != first.Receipt.BatchId ||
                    ack.Items == null || ack.Items.Length != Math.Min(page.Limit.Value, first.TotalItemCount.Value - offset) ||
                    ack.RemoteProductIds == null || ack.RemotePriceIds == null || ack.Summary == null || summary == null ||
                    ack.Summary.AcceptedItemCount != summary.AcceptedItemCount || ack.Summary.ProductCount != summary.ProductCount ||
                    ack.Summary.DuplicateItemCount != summary.DuplicateItemCount ||
                    page.Complete != (offset + ack.Items.Length == first.TotalItemCount.Value))
                    throw new CatalogImportRecoveryException("receipt_incomplete");
                var pageIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in ack.Items)
                {
                    if (row == null || string.IsNullOrEmpty(row.ClientItemId) || !pageIds.Add(row.ClientItemId) || !seenItems.Add(row.ClientItemId))
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    allItems.Add(row);
                }
                foreach (var row in ack.RemoteProductIds)
                {
                    if (row == null || !pageIds.Contains(row.ClientItemId ?? "") || !seenProducts.Add(row.ClientItemId))
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    allProducts.Add(row);
                }
                foreach (var row in ack.RemotePriceIds)
                {
                    if (row == null || !pageIds.Contains(row.ClientItemId ?? "") || !seenPrices.Add(row.ClientItemId + "\0" + row.PriceType))
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    allPrices.Add(row);
                }
                foreach (var row in page.CurrentProductSnapshots ?? Array.Empty<PosCatalogImportProductSnapshot>())
                {
                    if (row == null || !pageIds.Contains(row.ClientItemId ?? "") || !seenSnapshots.Add(row.ClientItemId))
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    allSnapshots.Add(row);
                }
                offset += ack.Items.Length;
                if (offset == first.TotalItemCount.Value) break;
                var request = Authenticate(new PosCatalogImportRecoveryReceiptPageRequest
                {
                    VerifiedOriginalId = IsUuid(first.VerifiedOriginalId) ? first.VerifiedOriginalId : null,
                    PlanId = IsUuid(first.VerifiedOriginalId) ? null : first.PlanId,
                    PartIndex = IsUuid(first.VerifiedOriginalId) ? (int?)null : first.PartIndex,
                    ReceiptSha256 = first.ReceiptSha256, Offset = offset,
                    Limit = PosCatalogImportRecoveryMultipartContract.MaximumReceiptPageItems
                }, binding.Read());
                var result = await client.CatalogImportRecoveryReceiptPageAsync(request, cancellationToken).ConfigureAwait(false);
                if (!result.Success) return result;
                page = result.Value;
            } while (true);
            if (allProducts.Count != allItems.Count || summary.AcceptedItemCount != allItems.Count || summary.ProductCount != allItems.Count)
                throw new CatalogImportRecoveryException("receipt_incomplete");
            var complete = CatalogImportRecoveryService.Deserialize<PosCatalogImportRecoveryMultipartResponse>(CatalogImportRecoveryService.Serialize(first));
            complete.Receipt.Items = allItems.ToArray(); complete.Receipt.RemoteProductIds = allProducts.ToArray();
            complete.Receipt.RemotePriceIds = allPrices.ToArray(); complete.CurrentProductSnapshots = allSnapshots.ToArray();
            complete.Complete = true;
            return PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>.Ok(complete);
        }

        private static PosOnlineResult<T> Failure<T>(PosOnlineResult<PosCatalogImportRecoveryMultipartResponse> result) where T : class =>
            PosOnlineResult<T>.Failure(result.Code, result.Message, result.Denied, result.ClientRequestId, result.ServerRequestId,
                result.CfRay, result.HttpStatus, result.ElapsedMilliseconds, result.ResponseContentType, result.ResponseLength,
                result.RequestReachedServer, result.Retryable, result.ExceptionType);

        internal static PosCatalogImportRecoveryUploadPart[] BuildManifest(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > PosCatalogImportRecoveryMultipartContract.MaximumRawBytes)
                throw new CatalogImportRecoveryException("recovery_payload_too_large");
            var size = PosCatalogImportRecoveryMultipartContract.MaximumRawPartBytes;
            var parts = new PosCatalogImportRecoveryUploadPart[(bytes.Length + size - 1) / size];
            for (var index = 0; index < parts.Length; index++)
            {
                var count = Math.Min(size, bytes.Length - index * size);
                parts[index] = new PosCatalogImportRecoveryUploadPart { Index = index, ByteLength = count, Sha256 = Hash(bytes, index * size, count) };
            }
            return parts;
        }

        private static UploadManifest BuildManifest(string rawJson, CancellationToken cancellationToken, bool phased = false)
        {
            if (string.IsNullOrEmpty(rawJson)) throw new CatalogImportRecoveryException("payload_invalid");
            var total = Encoding.UTF8.GetByteCount(rawJson);
            if (total > (phased ? PosCatalogImportRecoveryMultipartContract.MaximumPhasedRawBytes : PosCatalogImportRecoveryMultipartContract.MaximumRawBytes))
                throw new CatalogImportRecoveryException("recovery_payload_too_large");
            var parts = new List<PosCatalogImportRecoveryUploadPart>();
            using (var sha = SHA256.Create())
            {
                foreach (var bytes in ReadUtf8Chunks(rawJson))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                    parts.Add(new PosCatalogImportRecoveryUploadPart { Index = parts.Count, ByteLength = bytes.Length, Sha256 = Hash(bytes) });
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return new UploadManifest { Parts = parts.ToArray(), TotalByteLength = total,
                    RawSha256 = "sha256:" + BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant() };
            }
        }

        // Bound the additional x86 memory by the chunk size, even when the
        // immutable source string is large. Encoder state preserves surrogate
        // pairs across character blocks; byte chunks may split UTF-8 codepoints
        // because the server reconstructs all raw bytes before decoding them.
        internal static IEnumerable<byte[]> ReadUtf8Chunks(string rawJson)
        {
            if (rawJson == null) throw new ArgumentNullException(nameof(rawJson));
            var encoder = Encoding.UTF8.GetEncoder();
            var characters = new char[4096];
            var encoded = new byte[Encoding.UTF8.GetMaxByteCount(characters.Length)];
            var chunk = new byte[PosCatalogImportRecoveryMultipartContract.MaximumRawPartBytes];
            var filled = 0;
            for (var offset = 0; offset < rawJson.Length;)
            {
                var count = Math.Min(characters.Length, rawJson.Length - offset);
                rawJson.CopyTo(offset, characters, 0, count);
                offset += count;
                var length = encoder.GetBytes(characters, 0, count, encoded, 0, offset == rawJson.Length);
                for (var byteOffset = 0; byteOffset < length;)
                {
                    var take = Math.Min(chunk.Length - filled, length - byteOffset);
                    Buffer.BlockCopy(encoded, byteOffset, chunk, filled, take);
                    filled += take; byteOffset += take;
                    if (filled == chunk.Length)
                    {
                        yield return chunk;
                        chunk = new byte[PosCatalogImportRecoveryMultipartContract.MaximumRawPartBytes];
                        filled = 0;
                    }
                }
            }
            if (filled > 0)
            {
                var last = new byte[filled]; Buffer.BlockCopy(chunk, 0, last, 0, filled);
                yield return last;
            }
        }

        private sealed class UploadManifest
        {
            internal PosCatalogImportRecoveryUploadPart[] Parts;
            internal int TotalByteLength;
            internal string RawSha256;
        }

        internal static T Authenticate<T>(T request, PosTrustedDeviceSession session) where T : PosCatalogImportRecoveryMultipartRequest
        {
            ValidateSession(session);
            request.DeviceToken = session.DeviceToken; request.SessionToken = session.SessionToken;
            request.PosSessionId = session.PosSessionId; request.ShopDeviceId = session.ShopDeviceId; request.ShopCode = session.ShopCode;
            return request;
        }

        private static Identity ReadIdentity(CatalogImportOutboxItem original)
        {
            if (original.OperationType == CatalogImportCorrectionTransport.OperationType)
            {
                var request = CatalogImportCorrectionTransport.ReadSavedRequest(original.PayloadJson);
                if (request.Correction.ClientImportId != original.ClientImportId || request.Correction.IdempotencyKey != original.IdempotencyKey)
                    throw new CatalogImportRecoveryException("payload_invalid");
                return new Identity { SchemaVersion = request.SchemaVersion, ItemCount = request.Correction.Items?.Length ?? 0 };
            }
            var ordinary = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(original.PayloadJson);
            if (ordinary?.Batch == null || ordinary.Batch.ClientImportId != original.ClientImportId || ordinary.Batch.IdempotencyKey != original.IdempotencyKey ||
                ordinary.SchemaVersion != PosOnlineContract.CatalogImportSchemaVersion)
                throw new CatalogImportRecoveryException("payload_invalid");
            return new Identity { SchemaVersion = ordinary.SchemaVersion, ItemCount = ordinary.Items?.Length ?? 0 };
        }

        private static void ValidateCommon(PosCatalogImportRecoveryMultipartResponse response, SessionBinding binding)
        {
            if (response == null || !response.Ok || response.Code != "success" || response.SchemaVersion != PosCatalogImportRecoveryMultipartContract.SchemaVersion)
                throw new CatalogImportRecoveryException("receipt_unavailable");
            if (!string.Equals(response.ShopId, binding.ShopId, StringComparison.OrdinalIgnoreCase) || response.ShopDeviceId != binding.ShopDeviceId)
                throw new CatalogImportRecoveryException("response_shop_mismatch");
        }

        internal static string Hash(byte[] bytes) => Hash(bytes, 0, bytes.Length);
        private static string Hash(byte[] bytes, int offset, int count)
        {
            using (var sha = SHA256.Create()) return "sha256:" + BitConverter.ToString(sha.ComputeHash(bytes, offset, count)).Replace("-", "").ToLowerInvariant();
        }
        private static bool IsUuid(string value) => value != null && Uuid.IsMatch(value);
        internal static bool IsHash(string value) => value != null && CanonicalHash.IsMatch(value);

        private sealed class Identity
        {
            internal string SchemaVersion;
            internal int ItemCount;
        }

        private sealed class SessionBinding
        {
            private readonly Func<PosTrustedDeviceSession> _fresh;
            private readonly string _generation;
            private readonly string _shopCode;
            internal string ShopId { get; }
            internal string ShopDeviceId { get; }
            internal SessionBinding(Func<PosTrustedDeviceSession> fresh)
            {
                _fresh = fresh ?? throw new ArgumentNullException(nameof(fresh));
                var session = fresh(); ValidateSession(session);
                if (!IsUuid(session.ShopId)) throw new CatalogImportRecoveryException("origin_shop_mismatch");
                ShopId = session.ShopId; ShopDeviceId = session.ShopDeviceId;
                _generation = session.GenerationId; _shopCode = session.ShopCode;
            }
            internal PosTrustedDeviceSession Read()
            {
                var session = _fresh(); ValidateSession(session);
                if (session.ShopDeviceId != ShopDeviceId || !string.Equals(session.ShopId, ShopId, StringComparison.OrdinalIgnoreCase) ||
                    session.ShopCode != _shopCode || session.GenerationId != _generation)
                    throw new CatalogImportRecoveryException("origin_shop_mismatch");
                return session;
            }
        }

        internal static byte[] Encode<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
                    { UseSimpleDictionaryFormat = true }).WriteObject(stream, value);
                if (stream.Length > PosCatalogImportRecoveryMultipartContract.MaximumHttpBytes)
                    throw new CatalogImportRecoveryException("recovery_payload_too_large");
                return stream.ToArray();
            }
        }

        internal static void ValidateSession(PosTrustedDeviceSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.DeviceToken) || session.DeviceToken.Length > 256 ||
                string.IsNullOrWhiteSpace(session.SessionToken) || session.SessionToken.Length > 256 ||
                session.PosSessionId == null || !Uuid.IsMatch(session.PosSessionId) ||
                session.ShopDeviceId == null || !Uuid.IsMatch(session.ShopDeviceId) ||
                string.IsNullOrWhiteSpace(session.ShopCode) || session.ShopCode.Length > 80)
                throw new CatalogImportRecoveryException("authentication_required");
        }
    }
}
