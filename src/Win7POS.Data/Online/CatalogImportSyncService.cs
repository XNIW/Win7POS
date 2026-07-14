using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed class CatalogImportSyncService
    {
        private const int MaxOutboxItemsPerRun = 10;
        private const int MaxAttemptsBeforeBlocked = 12;

        private readonly CatalogImportOutboxRepository _outbox;

        public CatalogImportSyncService(SqliteConnectionFactory factory)
            : this(new CatalogImportOutboxRepository(factory))
        {
        }

        internal CatalogImportSyncService(CatalogImportOutboxRepository outbox)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        }

        public Task<CatalogImportSyncRunResult> SyncPendingAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            CancellationToken cancellationToken)
        {
            return SyncPendingAsync(options, trustedSession, MaxOutboxItemsPerRun, cancellationToken);
        }

        public async Task<CatalogImportSyncRunResult> SyncPendingAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            int maxItems,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (trustedSession == null) throw new ArgumentNullException(nameof(trustedSession));

            var result = new CatalogImportSyncRunResult();
            if (!HasRequiredTrust(trustedSession))
            {
                result.LastErrorCode = "missing_trusted_session";
                result.Skipped = true;
                return result;
            }

            var take = Math.Max(1, Math.Min(maxItems, MaxOutboxItemsPerRun));
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pending = await _outbox.GetPendingAsync(take, nowMs).ConfigureAwait(false);
            result.Total = pending.Count;

            if (pending.Count == 0)
            {
                return result;
            }

            using (var client = new PosAdminWebClient(options))
            {
                foreach (var item in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await SyncOneAsync(client, trustedSession, item, result, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }

        private async Task SyncOneAsync(
            PosAdminWebClient client,
            PosTrustedDeviceSession trustedSession,
            CatalogImportOutboxItem item,
            CatalogImportSyncRunResult run,
            CancellationToken cancellationToken)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var bindingError = OutboxShopBinding.GetMismatchCode(
                item.OriginShopId,
                item.OriginShopCode,
                trustedSession.ShopId,
                trustedSession.ShopCode);
            if (string.IsNullOrWhiteSpace(bindingError) &&
                !string.Equals(item.OperationType, "catalog_import", StringComparison.Ordinal))
            {
                bindingError = "operation_type_mismatch";
            }

            if (!string.IsNullOrWhiteSpace(bindingError))
            {
                if (await _outbox.MarkOriginBlockedAsync(item.Id, bindingError, nowMs).ConfigureAwait(false))
                {
                    run.Blocked++;
                    run.LastErrorCode = bindingError;
                }

                return;
            }

            if (!await _outbox.PrepareAttemptAsync(item.Id, nowMs).ConfigureAwait(false))
            {
                return;
            }

            run.Prepared++;

            var preparedAttempt = item.AttemptCount + 1;
            var validation = CatalogImportOutboxPayloadValidator.Validate(item);
            if (!validation.IsValid)
            {
                if (await _outbox.MarkBlockedAsync(item.Id, validation.Code, nowMs, preparedAttempt).ConfigureAwait(false))
                {
                    run.Blocked++;
                    run.InvalidPayload++;
                    run.LastErrorCode = validation.Code;
                }

                return;
            }

            var request = validation.Request;
            AttachTrust(request, trustedSession, item);
            AttachAttemptMetadata(request, item, preparedAttempt);

            var response = await client.CatalogImportAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                await MarkRemoteFailureAsync(item, preparedAttempt, response.Code, response.Denied, run).ConfigureAwait(false);
                return;
            }

            var remote = response.Value;
            var remoteCode = FirstNonEmpty(remote == null ? null : remote.Code, "remote_not_ok");
            if (remote == null || !remote.Ok)
            {
                await MarkRemoteFailureAsync(item, preparedAttempt, remoteCode, false, run).ConfigureAwait(false);
                return;
            }

            var responseShopError = GetResponseShopMismatchCode(item, remote);
            if (!string.IsNullOrWhiteSpace(responseShopError))
            {
                if (await _outbox.MarkBlockedAsync(
                    item.Id,
                    "response_shop_mismatch",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt).ConfigureAwait(false))
                {
                    run.Blocked++;
                    run.LastErrorCode = "response_shop_mismatch";
                }

                return;
            }

            var batchStatus = FirstNonEmpty(remote.Batch == null ? null : remote.Batch.Status, remote.Code);
            if (IsBlockedStatus(batchStatus) || HasBlockedItem(remote.Items))
            {
                if (await _outbox.MarkBlockedAsync(
                    item.Id,
                    FirstNonEmpty(batchStatus, "remote_blocked"),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt).ConfigureAwait(false))
                {
                    run.Blocked++;
                    run.LastErrorCode = FirstNonEmpty(batchStatus, "remote_blocked");
                }

                return;
            }

            if (IsAckStatus(batchStatus))
            {
                var remoteBatchMismatch = GetRemoteBatchMismatchCode(item, remote.Batch);
                if (!string.IsNullOrWhiteSpace(remoteBatchMismatch))
                {
                    if (await _outbox.MarkBlockedAsync(
                        item.Id,
                        remoteBatchMismatch,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        preparedAttempt).ConfigureAwait(false))
                    {
                        run.Blocked++;
                        run.LastErrorCode = remoteBatchMismatch;
                    }

                    return;
                }

                var ack = BuildAckResult(item, request, remote, response.ServerRequestId);
                if (await _outbox.MarkAckedAsync(
                    item.Id,
                    ack,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt).ConfigureAwait(false))
                {
                    run.Acked++;
                }

                return;
            }

            if (IsAuthDenied(batchStatus))
            {
                run.RequiresTrustClear = true;
                await MarkRetryOrBlockedAsync(item, preparedAttempt, "auth_denied", run).ConfigureAwait(false);
                return;
            }

            await MarkRetryOrBlockedAsync(item, preparedAttempt, FirstNonEmpty(batchStatus, "remote_retry"), run).ConfigureAwait(false);
        }

        private async Task MarkRemoteFailureAsync(
            CatalogImportOutboxItem item,
            int preparedAttempt,
            string code,
            bool denied,
            CatalogImportSyncRunResult run)
        {
            var normalizedCode = denied || IsAuthDenied(code) ? "auth_denied" : FirstNonEmpty(code, "network_error");
            if (IsBlockedStatus(normalizedCode))
            {
                if (await _outbox.MarkBlockedAsync(
                    item.Id,
                    normalizedCode,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt).ConfigureAwait(false))
                {
                    run.Blocked++;
                    run.LastErrorCode = normalizedCode;
                }

                return;
            }

            if (IsAuthDenied(normalizedCode))
            {
                run.RequiresTrustClear = true;
            }

            await MarkRetryOrBlockedAsync(item, preparedAttempt, normalizedCode, run).ConfigureAwait(false);
        }

        private async Task MarkRetryOrBlockedAsync(
            CatalogImportOutboxItem item,
            int preparedAttempt,
            string code,
            CatalogImportSyncRunResult run)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var normalizedCode = FirstNonEmpty(code, "retry");
            if (preparedAttempt >= MaxAttemptsBeforeBlocked)
            {
                if (await _outbox.MarkBlockedAsync(item.Id, normalizedCode, nowMs, preparedAttempt).ConfigureAwait(false))
                {
                    run.Blocked++;
                }
            }
            else
            {
                if (await _outbox.MarkRetryAsync(
                    item.Id,
                    normalizedCode,
                    ComputeNextRetryAt(nowMs, preparedAttempt),
                    nowMs,
                    preparedAttempt).ConfigureAwait(false))
                {
                    run.Retried++;
                }
            }

            run.LastErrorCode = normalizedCode;
        }

        private static long ComputeNextRetryAt(long nowMs, int preparedAttempt)
        {
            var boundedAttempt = Math.Max(1, Math.Min(preparedAttempt, 8));
            var seconds = Math.Min(15 * 60, 30 * (1 << (boundedAttempt - 1)));
            return nowMs + seconds * 1000L;
        }

        private static bool HasRequiredTrust(PosTrustedDeviceSession trustedSession)
        {
            return !string.IsNullOrWhiteSpace(trustedSession.DeviceToken) &&
                !string.IsNullOrWhiteSpace(trustedSession.SessionToken) &&
                !string.IsNullOrWhiteSpace(trustedSession.PosSessionId) &&
                !string.IsNullOrWhiteSpace(trustedSession.ShopDeviceId);
        }

        private static bool IsExpectedRemoteBatch(
            CatalogImportOutboxItem item,
            PosCatalogImportBatchResponse batch)
        {
            return string.IsNullOrWhiteSpace(GetRemoteBatchMismatchCode(item, batch));
        }

        private static string GetRemoteBatchMismatchCode(
            CatalogImportOutboxItem item,
            PosCatalogImportBatchResponse batch)
        {
            if (item == null || batch == null)
            {
                return "client_import_mismatch";
            }

            if (!string.Equals(batch.ClientImportId, item.ClientImportId, StringComparison.Ordinal))
            {
                return "client_import_mismatch";
            }

            if (!string.Equals(batch.IdempotencyKey, item.IdempotencyKey, StringComparison.Ordinal))
            {
                return "idempotency_key_mismatch";
            }

            if (string.IsNullOrWhiteSpace(batch.PayloadHash) ||
                !string.Equals(batch.PayloadHash.Trim(), item.PayloadHash, StringComparison.Ordinal))
            {
                return "payload_hash_mismatch";
            }

            if (batch.AttemptCount != item.AttemptCount + 1)
            {
                return "attempt_count_mismatch";
            }

            return string.Empty;
        }

        private static string GetResponseShopMismatchCode(
            CatalogImportOutboxItem item,
            PosCatalogImportResponse response)
        {
            if (item == null || response == null)
            {
                return "response_shop_mismatch";
            }

            return string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                item.OriginShopId,
                item.OriginShopCode,
                response.Shop?.ShopId,
                response.Shop?.ShopCode))
                ? string.Empty
                : "response_shop_mismatch";
        }

        private static CatalogImportAckResult BuildAckResult(
            CatalogImportOutboxItem item,
            PosCatalogImportRequest request,
            PosCatalogImportResponse remote,
            string transportServerRequestId)
        {
            var barcodeByClientItemId = BuildBarcodeMap(request);
            var remoteProductIds = new List<CatalogImportRemoteProductId>();
            var remotePriceIds = new List<CatalogImportRemotePriceId>();

            foreach (var remoteProduct in remote.RemoteProductIds ?? Array.Empty<PosCatalogImportRemoteProductIdAck>())
            {
                if (remoteProduct == null)
                {
                    continue;
                }

                remoteProductIds.Add(new CatalogImportRemoteProductId
                {
                    Barcode = ResolveAckBarcode(barcodeByClientItemId, remoteProduct.ClientItemId, remoteProduct.Barcode),
                    ClientItemId = remoteProduct.ClientItemId,
                    RemoteProductId = remoteProduct.RemoteProductId
                });
            }

            foreach (var remotePrice in remote.RemotePriceIds ?? Array.Empty<PosCatalogImportRemotePriceIdAck>())
            {
                if (remotePrice == null)
                {
                    continue;
                }

                remotePriceIds.Add(new CatalogImportRemotePriceId
                {
                    Barcode = ResolveAckBarcode(barcodeByClientItemId, remotePrice.ClientItemId, remotePrice.Barcode),
                    ClientItemId = remotePrice.ClientItemId,
                    PriceType = remotePrice.PriceType,
                    RemotePriceId = remotePrice.RemotePriceId
                });
            }

            foreach (var ackItem in remote.Items ?? Array.Empty<PosCatalogImportItemAck>())
            {
                if (ackItem == null)
                {
                    continue;
                }

                var barcode = ResolveAckBarcode(barcodeByClientItemId, ackItem.ClientItemId, ackItem.Barcode);
                if (!string.IsNullOrWhiteSpace(ackItem.RemoteProductId))
                {
                    remoteProductIds.Add(new CatalogImportRemoteProductId
                    {
                        Barcode = barcode,
                        ClientItemId = ackItem.ClientItemId,
                        RemoteProductId = ackItem.RemoteProductId
                    });
                }

                if (!string.IsNullOrWhiteSpace(ackItem.RemotePriceId))
                {
                    remotePriceIds.Add(new CatalogImportRemotePriceId
                    {
                        Barcode = barcode,
                        ClientItemId = ackItem.ClientItemId,
                        PriceType = ackItem.PriceType,
                        RemotePriceId = ackItem.RemotePriceId
                    });
                }
            }

            return new CatalogImportAckResult
            {
                RemotePriceIds = remotePriceIds,
                RemoteProductIds = remoteProductIds,
                ServerImportId = FirstNonEmpty(
                    remote.ServerImportId,
                    FirstNonEmpty(remote.Batch.ServerImportId, remote.Batch.PosCatalogImportBatchId)),
                ServerRequestId = FirstNonEmpty(
                    remote.ServerRequestId,
                    FirstNonEmpty(remote.Batch.ServerRequestId, transportServerRequestId))
            };
        }

        private static IReadOnlyDictionary<string, string> BuildBarcodeMap(PosCatalogImportRequest request)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var requestItem in request.Items ?? Array.Empty<PosCatalogImportItemRequest>())
            {
                if (requestItem == null ||
                    string.IsNullOrWhiteSpace(requestItem.ClientItemId) ||
                    string.IsNullOrWhiteSpace(requestItem.Barcode))
                {
                    continue;
                }

                if (!result.ContainsKey(requestItem.ClientItemId))
                {
                    result.Add(requestItem.ClientItemId, requestItem.Barcode);
                }
            }

            return result;
        }

        private static string BarcodeFor(IReadOnlyDictionary<string, string> barcodeByClientItemId, string clientItemId)
        {
            if (string.IsNullOrWhiteSpace(clientItemId) || barcodeByClientItemId == null)
            {
                return string.Empty;
            }

            string barcode;
            return barcodeByClientItemId.TryGetValue(clientItemId, out barcode)
                ? barcode
                : string.Empty;
        }

        private static string ResolveAckBarcode(
            IReadOnlyDictionary<string, string> barcodeByClientItemId,
            string clientItemId,
            string serverBarcode)
        {
            var localBarcode = BarcodeFor(barcodeByClientItemId, clientItemId);
            return FirstNonEmpty(localBarcode, serverBarcode);
        }

        private static void AttachTrust(
            PosCatalogImportRequest request,
            PosTrustedDeviceSession trustedSession,
            CatalogImportOutboxItem item)
        {
            request.DeviceToken = TrimOrNull(trustedSession.DeviceToken);
            request.PosSessionId = TrimOrNull(trustedSession.PosSessionId);
            request.SessionToken = TrimOrNull(trustedSession.SessionToken);
            request.ShopCode = TrimOrNull(item.OriginShopCode);
            request.ShopDeviceId = TrimOrNull(trustedSession.ShopDeviceId);
        }

        private static void AttachAttemptMetadata(
            PosCatalogImportRequest request,
            CatalogImportOutboxItem item,
            int preparedAttempt)
        {
            if (request == null || item == null)
            {
                return;
            }

            request.PayloadHash = TrimOrNull(item.PayloadHash);
            if (request.Batch != null)
            {
                request.Batch.AttemptCount = preparedAttempt;
            }
        }

        private static bool HasBlockedItem(IEnumerable<PosCatalogImportItemAck> items)
        {
            foreach (var item in items ?? Array.Empty<PosCatalogImportItemAck>())
            {
                if (IsBlockedStatus(item == null ? null : item.Status) ||
                    IsBlockedStatus(item == null ? null : item.Code))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAckStatus(string status)
        {
            return IsOneOf(status, "accepted", "duplicate", "idempotent", "acked", "ok", "success");
        }

        private static bool IsBlockedStatus(string status)
        {
            return IsOneOf(status, "validation_failed", "conflict", "failed_blocked", "invalid_payload", "schema_mismatch", "hash_mismatch", "client_import_mismatch", "idempotency_key_mismatch", "payload_hash_mismatch", "attempt_count_mismatch");
        }

        private static bool IsAuthDenied(string status)
        {
            return IsOneOf(status, "auth_denied", "unauthorized", "forbidden");
        }

        private static bool IsOneOf(string value, params string[] candidates)
        {
            var normalized = (value ?? string.Empty).Trim();
            foreach (var candidate in candidates)
            {
                if (string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FirstNonEmpty(string left, string right)
        {
            return string.IsNullOrWhiteSpace(left) ? right : left.Trim();
        }

        private static string TrimOrNull(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }

    public sealed class CatalogImportSyncRunResult
    {
        public int Acked { get; set; }
        public int Blocked { get; set; }
        public int InvalidPayload { get; set; }
        public string LastErrorCode { get; set; } = string.Empty;
        public int Prepared { get; set; }
        public bool RequiresTrustClear { get; set; }
        public int Retried { get; set; }
        public bool Skipped { get; set; }
        public int Total { get; set; }
    }

    public static class CatalogImportOutboxPayloadValidator
    {
        public static CatalogImportOutboxPayloadValidationResult Validate(CatalogImportOutboxItem item)
        {
            if (item == null)
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("missing_outbox_item");
            }

            var actualHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(item.PayloadJson);
            if (!string.Equals(actualHash, item.PayloadHash, StringComparison.Ordinal))
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("hash_mismatch");
            }

            PosCatalogImportRequest request;
            try
            {
                request = Deserialize(item.PayloadJson);
            }
            catch
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("invalid_payload_json");
            }

            if (request == null)
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("invalid_payload_json");
            }

            if (!string.Equals(request.SchemaVersion, item.SchemaVersion, StringComparison.Ordinal))
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("schema_mismatch");
            }

            if (request.Batch == null)
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("missing_batch");
            }

            if (!string.Equals(request.Batch.ClientImportId, item.ClientImportId, StringComparison.Ordinal) ||
                !string.Equals(request.Batch.IdempotencyKey, item.IdempotencyKey, StringComparison.Ordinal))
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("idempotency_mismatch");
            }

            if (!string.IsNullOrWhiteSpace(request.DeviceToken) ||
                !string.IsNullOrWhiteSpace(request.SessionToken) ||
                !string.IsNullOrWhiteSpace(request.PosSessionId) ||
                !string.IsNullOrWhiteSpace(request.ShopDeviceId))
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("payload_contains_auth");
            }

            if (request.Batch.SourceFileName != null &&
                !string.Equals(request.Batch.SourceFileName, Path.GetFileName(request.Batch.SourceFileName), StringComparison.Ordinal))
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("source_file_not_redacted");
            }

            var items = request.Items ?? Array.Empty<PosCatalogImportItemRequest>();
            if (items.Length == 0)
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("missing_items");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var newCount = 0;
            var updatedCount = 0;
            foreach (var payloadItem in items)
            {
                if (payloadItem == null)
                {
                    return CatalogImportOutboxPayloadValidationResult.Fail("invalid_item");
                }

                if (string.IsNullOrWhiteSpace(payloadItem.ClientItemId) ||
                    !ids.Add(payloadItem.ClientItemId))
                {
                    return CatalogImportOutboxPayloadValidationResult.Fail("duplicate_client_item_id");
                }

                if (string.IsNullOrWhiteSpace(payloadItem.Barcode) || payloadItem.RowNumber <= 0)
                {
                    return CatalogImportOutboxPayloadValidationResult.Fail("invalid_item_identity");
                }

                if (!string.Equals(payloadItem.Operation, "upsert_product", StringComparison.Ordinal))
                {
                    return CatalogImportOutboxPayloadValidationResult.Fail("invalid_operation");
                }

                if (string.Equals(payloadItem.ChangeKind, "new", StringComparison.OrdinalIgnoreCase))
                {
                    newCount++;
                }
                else if (string.Equals(payloadItem.ChangeKind, "updated", StringComparison.OrdinalIgnoreCase))
                {
                    updatedCount++;
                }
                else
                {
                    return CatalogImportOutboxPayloadValidationResult.Fail("invalid_change_kind");
                }
            }

            if (request.Summary == null ||
                request.Summary.NewProducts != newCount ||
                request.Summary.UpdatedProducts != updatedCount)
            {
                return CatalogImportOutboxPayloadValidationResult.Fail("summary_mismatch");
            }

            return CatalogImportOutboxPayloadValidationResult.Ok(request);
        }

        private static PosCatalogImportRequest Deserialize(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(PosCatalogImportRequest));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
            {
                return serializer.ReadObject(stream) as PosCatalogImportRequest;
            }
        }
    }

    public sealed class CatalogImportOutboxPayloadValidationResult
    {
        private CatalogImportOutboxPayloadValidationResult(bool isValid, string code, PosCatalogImportRequest request)
        {
            Code = code ?? string.Empty;
            IsValid = isValid;
            Request = request;
        }

        public string Code { get; }
        public bool IsValid { get; }
        public PosCatalogImportRequest Request { get; }

        public static CatalogImportOutboxPayloadValidationResult Fail(string code)
        {
            return new CatalogImportOutboxPayloadValidationResult(false, code, null);
        }

        public static CatalogImportOutboxPayloadValidationResult Ok(PosCatalogImportRequest request)
        {
            return new CatalogImportOutboxPayloadValidationResult(true, string.Empty, request);
        }
    }
}
