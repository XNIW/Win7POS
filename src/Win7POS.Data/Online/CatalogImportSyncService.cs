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

        public Task<OutboxDrainResult> SyncPendingAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            CancellationToken cancellationToken)
        {
            return SyncPendingAsync(options, trustedSession, MaxOutboxItemsPerRun, cancellationToken);
        }

        public async Task<OutboxDrainResult> SyncPendingAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            int maxItems,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (trustedSession == null) throw new ArgumentNullException(nameof(trustedSession));

            var run = new DrainAccumulator();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!HasRequiredTrust(trustedSession))
            {
                run.SetFailure(SyncFailureKind.Configuration, "missing_trusted_session");
                return await CompleteAsync(run, nowMs).ConfigureAwait(false);
            }

            var take = Math.Max(1, Math.Min(maxItems, MaxOutboxItemsPerRun));
            var pending = await _outbox.GetPendingAsync(take, nowMs).ConfigureAwait(false);

            if (pending.Count == 0)
            {
                return await CompleteAsync(run, nowMs).ConfigureAwait(false);
            }

            using (var client = new PosAdminWebClient(options))
            {
                foreach (var item in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stop = await SyncOneAsync(
                        client,
                        trustedSession,
                        item,
                        run,
                        cancellationToken).ConfigureAwait(false);
                    if (stop)
                    {
                        break;
                    }
                }
            }

            return await CompleteAsync(
                run,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
        }

        private async Task<bool> SyncOneAsync(
            PosAdminWebClient client,
            PosTrustedDeviceSession trustedSession,
            CatalogImportOutboxItem item,
            DrainAccumulator run,
            CancellationToken cancellationToken)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!await _outbox.PrepareAttemptAsync(item, nowMs).ConfigureAwait(false))
            {
                run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain, "catalog_import_claim_lost");
                return false;
            }

            run.Attempted++;
            var preparedAttempt = item.AttemptCount + 1;
            try
            {
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
                    if (await _outbox.MarkBlockedAsync(
                        item.Id,
                        bindingError,
                        nowMs,
                        preparedAttempt).ConfigureAwait(false))
                    {
                        run.Blocked++;
                        run.SetFailure(SyncFailureKind.LocalValidation, bindingError);
                    }

                    return false;
                }

                var validation = CatalogImportOutboxPayloadValidator.Validate(item);
                if (!validation.IsValid)
                {
                    if (await _outbox.MarkBlockedAsync(item.Id, validation.Code, nowMs, preparedAttempt).ConfigureAwait(false))
                    {
                        run.Blocked++;
                        run.SetFailure(SyncFailureKind.LocalValidation, validation.Code);
                    }

                    return false;
                }

                var request = validation.Request;
                AttachTrust(request, trustedSession, item);
                AttachAttemptMetadata(request, item, preparedAttempt);

                var response = await client
                    .CatalogImportAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.Success)
                {
                    return await MarkRemoteFailureAsync(
                        item,
                        preparedAttempt,
                        response.Code,
                        response.Denied,
                        run).ConfigureAwait(false);
                }

                var remote = response.Value;
                var remoteCode = FirstNonEmpty(remote == null ? null : remote.Code, "remote_not_ok");
                if (remote == null || !remote.Ok)
                {
                    return await MarkRemoteFailureAsync(
                        item,
                        preparedAttempt,
                        remoteCode,
                        false,
                        run).ConfigureAwait(false);
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
                        run.SetFailure(SyncFailureKind.PermanentRemote, "response_shop_mismatch");
                    }

                    return false;
                }

                var batchStatus = FirstNonEmpty(remote.Batch == null ? null : remote.Batch.Status, remote.Code);
                if (IsBlockedStatus(batchStatus) || HasBlockedItem(remote.Items))
                {
                    if (await _outbox.MarkBlockedAsync(
                        item.Id,
                        SafeDiagnosticCode(FirstNonEmpty(batchStatus, "remote_blocked")),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        preparedAttempt).ConfigureAwait(false))
                    {
                        run.Blocked++;
                        run.SetFailure(
                            SyncFailureKind.PermanentRemote,
                            SafeDiagnosticCode(FirstNonEmpty(batchStatus, "remote_blocked")));
                    }

                    return false;
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
                            run.SetFailure(SyncFailureKind.PermanentRemote, remoteBatchMismatch);
                        }

                        return false;
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
                    else
                    {
                        run.SetFailureIfNone(
                            SyncFailureKind.ConcurrentDrain,
                            "catalog_import_ack_cas_lost");
                    }

                    return false;
                }

                if (IsAuthDenied(batchStatus))
                {
                    run.SetFailure(SyncFailureKind.AuthenticationDenied, "auth_denied");
                    await MarkRetryOrBlockedAsync(
                        item,
                        preparedAttempt,
                        "auth_denied",
                        SyncFailureKind.AuthenticationDenied,
                        run,
                        allowPermanentBlock: false).ConfigureAwait(false);
                    return true;
                }

                await MarkRetryOrBlockedAsync(
                    item,
                    preparedAttempt,
                    FirstNonEmpty(batchStatus, "remote_retry"),
                    SyncFailureKind.RetryableRemote,
                    run).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    var releaseAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await _outbox.ReleaseAttemptAsync(
                        item.Id,
                        "cancelled",
                        releaseAt,
                        releaseAt,
                        preparedAttempt).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the caller's cancellation; the bounded in-progress
                    // lease makes a failed local transition recoverable.
                }
                throw;
            }
            catch (Exception)
            {
                try
                {
                    await MarkRetryOrBlockedAsync(
                        item,
                        preparedAttempt,
                        run.FailureKind == SyncFailureKind.AuthenticationDenied
                            ? "auth_denied"
                            : "exception",
                        run.FailureKind == SyncFailureKind.AuthenticationDenied
                            ? SyncFailureKind.AuthenticationDenied
                            : SyncFailureKind.Unexpected,
                        run,
                        allowPermanentBlock:
                            run.FailureKind != SyncFailureKind.AuthenticationDenied).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original remote auth-stop or failure classification;
                    // the stale lease remains bounded and recoverable.
                }

                return run.FailureKind == SyncFailureKind.AuthenticationDenied;
            }
        }

        private async Task<bool> MarkRemoteFailureAsync(
            CatalogImportOutboxItem item,
            int preparedAttempt,
            string code,
            bool denied,
            DrainAccumulator run)
        {
            var normalizedCode = denied || IsAuthDenied(code)
                ? "auth_denied"
                : SafeDiagnosticCode(FirstNonEmpty(code, "network_error"));
            if (IsBlockedStatus(normalizedCode))
            {
                if (await _outbox.MarkBlockedAsync(
                    item.Id,
                    normalizedCode,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt).ConfigureAwait(false))
                {
                    run.Blocked++;
                    run.SetFailure(SyncFailureKind.PermanentRemote, normalizedCode);
                }

                return false;
            }

            var failureKind = IsAuthDenied(normalizedCode)
                ? SyncFailureKind.AuthenticationDenied
                : GetRetryableFailureKind(normalizedCode);
            if (failureKind == SyncFailureKind.AuthenticationDenied)
            {
                run.SetFailure(SyncFailureKind.AuthenticationDenied, "auth_denied");
            }
            await MarkRetryOrBlockedAsync(
                item,
                preparedAttempt,
                normalizedCode,
                failureKind,
                run,
                allowPermanentBlock:
                    failureKind != SyncFailureKind.AuthenticationDenied).ConfigureAwait(false);
            return failureKind == SyncFailureKind.AuthenticationDenied;
        }

        private async Task MarkRetryOrBlockedAsync(
            CatalogImportOutboxItem item,
            int preparedAttempt,
            string code,
            SyncFailureKind failureKind,
            DrainAccumulator run,
            bool allowPermanentBlock = true)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var normalizedCode = SafeDiagnosticCode(FirstNonEmpty(code, "retry"));
            if (allowPermanentBlock && preparedAttempt >= MaxAttemptsBeforeBlocked)
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

            run.SetFailure(failureKind, normalizedCode);
        }

        private async Task<OutboxDrainResult> CompleteAsync(DrainAccumulator run, long nowMs)
        {
            var state = await _outbox.GetDrainStateAsync(nowMs).ConfigureAwait(false);
            return new OutboxDrainResult(
                run.Attempted,
                run.Acked,
                run.Retried,
                run.Blocked,
                state.RemainingDue,
                state.NextRetryAt,
                run.FailureKind,
                run.DiagnosticCode);
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
            var explicitRemotePriceSlots = new HashSet<string>(StringComparer.Ordinal);
            var explicitRemotePriceIdentities = new HashSet<string>(StringComparer.Ordinal);
            var explicitRemotePriceWildcardIdentities = new HashSet<string>(StringComparer.Ordinal);

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

                var barcode = ResolveAckBarcode(
                    barcodeByClientItemId,
                    remotePrice.ClientItemId,
                    remotePrice.Barcode);
                var priceType = NormalizeAckPriceType(remotePrice.PriceType);
                remotePriceIds.Add(new CatalogImportRemotePriceId
                {
                    Barcode = barcode,
                    ClientItemId = remotePrice.ClientItemId,
                    PriceType = priceType,
                    RemotePriceId = remotePrice.RemotePriceId
                });
                if (!string.IsNullOrWhiteSpace(remotePrice.RemotePriceId))
                {
                    var priceIdentity = BuildAckPriceIdentityKey(remotePrice.ClientItemId, barcode);
                    explicitRemotePriceSlots.Add(BuildAckPriceSlotKey(
                        remotePrice.ClientItemId,
                        barcode,
                        priceType));
                    explicitRemotePriceIdentities.Add(priceIdentity);
                    if (priceType.Length == 0)
                    {
                        explicitRemotePriceWildcardIdentities.Add(priceIdentity);
                    }
                }
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
                    var priceType = NormalizeAckPriceType(ackItem.PriceType);
                    var priceIdentity = BuildAckPriceIdentityKey(ackItem.ClientItemId, barcode);
                    var priceSlot = BuildAckPriceSlotKey(
                        ackItem.ClientItemId,
                        barcode,
                        priceType);
                    if (explicitRemotePriceSlots.Contains(priceSlot) ||
                        explicitRemotePriceWildcardIdentities.Contains(priceIdentity) ||
                        (priceType.Length == 0 && explicitRemotePriceIdentities.Contains(priceIdentity)))
                    {
                        continue;
                    }

                    remotePriceIds.Add(new CatalogImportRemotePriceId
                    {
                        Barcode = barcode,
                        ClientItemId = ackItem.ClientItemId,
                        PriceType = priceType,
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

        private static string BuildAckPriceSlotKey(
            string clientItemId,
            string barcode,
            string priceType)
        {
            return BuildAckPriceIdentityKey(clientItemId, barcode) +
                "|type:" + NormalizeAckPriceType(priceType);
        }

        private static string BuildAckPriceIdentityKey(string clientItemId, string barcode)
        {
            return string.IsNullOrWhiteSpace(clientItemId)
                ? "barcode:" + (barcode ?? string.Empty).Trim()
                : "client:" + clientItemId.Trim();
        }

        private static string NormalizeAckPriceType(string priceType)
        {
            var normalized = (priceType ?? string.Empty).Trim().ToLowerInvariant();
            return string.Equals(normalized, "purchase", StringComparison.Ordinal) ||
                string.Equals(normalized, "retail", StringComparison.Ordinal)
                ? normalized
                : string.Empty;
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
            return SharedAuthStopPolicy.IsAuthenticationDenied(status);
        }

        private static SyncFailureKind GetRetryableFailureKind(string code)
        {
            if (string.Equals(code, "timeout", StringComparison.OrdinalIgnoreCase))
            {
                return SyncFailureKind.Timeout;
            }

            if (string.Equals(code, "network_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "io_error", StringComparison.OrdinalIgnoreCase))
            {
                return SyncFailureKind.Network;
            }

            return SyncFailureKind.RetryableRemote;
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

        private static string SafeDiagnosticCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized.Length > 96)
            {
                return "outbox_failure";
            }

            for (var index = 0; index < normalized.Length; index++)
            {
                var character = normalized[index];
                var allowed = (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' ||
                    character == '-' ||
                    character == '.';
                if (!allowed)
                {
                    return "outbox_failure";
                }
            }

            return normalized;
        }

        private static string TrimOrNull(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }

    internal sealed class DrainAccumulator
    {
        public int Acked { get; set; }
        public int Attempted { get; set; }
        public int Blocked { get; set; }
        public string DiagnosticCode { get; private set; } = string.Empty;
        public SyncFailureKind FailureKind { get; private set; }
        public int Retried { get; set; }

        public void SetFailure(SyncFailureKind kind, string code)
        {
            if (FailureKind == SyncFailureKind.AuthenticationDenied &&
                kind != SyncFailureKind.AuthenticationDenied)
            {
                return;
            }

            FailureKind = kind;
            DiagnosticCode = code ?? string.Empty;
        }

        public void SetFailureIfNone(SyncFailureKind kind, string code)
        {
            if (FailureKind == SyncFailureKind.None)
            {
                SetFailure(kind, code);
            }
        }
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
