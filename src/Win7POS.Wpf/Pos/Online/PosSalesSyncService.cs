using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosSalesSyncService
    {
        private const int MaxOutboxItemsPerRun = 25;
        private const int MaxAttemptsBeforeBlocked = 12;
        private const string LastSalesErrorSettingKey = "pos.sales_sync.last_error";
        private const string SalesSyncInProgressSettingKey = "pos.sales_sync.in_progress";
        private const string LastSalesSyncSettingKey = "pos.sales_sync.last_success_at";
        private int _syncInFlight;

        private readonly SqliteConnectionFactory _factory;
        private readonly FileLogger _logger;
        private readonly SaleRepository _sales;
        private readonly PosTrustedDeviceStore _store;

        public PosSalesSyncService(SqliteConnectionFactory factory)
            : this(
                factory,
                new SaleRepository(factory),
                new PosTrustedDeviceStore(),
                new FileLogger("PosSalesSyncService"))
        {
        }

        internal PosSalesSyncService(
            SqliteConnectionFactory factory,
            SaleRepository sales,
            PosTrustedDeviceStore store,
            FileLogger logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _sales = sales ?? throw new ArgumentNullException(nameof(sales));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<OutboxDrainResult> TrySyncPendingAsync(
            PosAdminWebOptions options,
            CancellationToken cancellationToken)
        {
            return await TrySyncPendingAsync(options, null, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<OutboxDrainResult> TrySyncPendingAsync(
            PosAdminWebOptions options,
            OnlineSyncGeneration generation,
            CancellationToken cancellationToken)
        {
            return await TrySyncPendingAsync(
                options,
                generation,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<OutboxDrainResult> TrySyncPendingAsync(
            PosAdminWebOptions options,
            OnlineSyncGeneration generation,
            OnlineSyncLaneExecutionContext executionContext,
            CancellationToken cancellationToken)
        {
            var run = new SalesDrainAccumulator();
            if (options == null)
            {
                run.SetFailure(SyncFailureKind.Configuration, "missing_admin_web_options");
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0)
            {
                _logger.LogInfo("Sales sync skipped: already running.");
                run.SetFailure(SyncFailureKind.ConcurrentDrain, "sales_sync_already_running");
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            await StoreSalesSyncInProgressAsync(true, generation).ConfigureAwait(false);

            try
            {
                if (!_store.TryRead(out var trustedSession))
                {
                    run.SetFailure(SyncFailureKind.Configuration, "missing_trusted_session");
                    return await CompleteAsync(run).ConfigureAwait(false);
                }
                if (generation != null &&
                    !_store.TryReadGeneration(
                        generation,
                        out trustedSession,
                        out _))
                {
                    run.SetFailure(SyncFailureKind.AuthenticationDenied, "trusted_generation_changed");
                    return await CompleteAsync(run).ConfigureAwait(false);
                }

                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var pending = await _sales
                    .GetPendingSalesSyncOutboxAsync(MaxOutboxItemsPerRun, nowMs)
                    .ConfigureAwait(false);

                if (pending.Count == 0)
                {
                    return await CompleteAsync(run).ConfigureAwait(false);
                }

                using (var client = new PosAdminWebClient(options))
                {
                    foreach (var item in pending)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var preparedAttempt = 0;
                        OnlineSyncAttemptFence fence = null;

                        try
                        {
                            var claimAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var claimToken = generation == null
                                ? null
                                : OnlineSyncAttemptFence.CreateClaimToken();
                            if (!await _sales.PrepareSalesSyncAttemptAsync(
                                item.Id,
                                item.ClientBatchId,
                                item.PayloadJson,
                                item.PayloadHash,
                                claimAt,
                                item.AttemptCount,
                                item.Status,
                                item.NextRetryAt,
                                item.LeaseObservedAt,
                                generation,
                                claimToken).ConfigureAwait(false))
                            {
                                run.SetFailureIfNone(
                                    SyncFailureKind.ConcurrentDrain,
                                    "sales_sync_claim_lost");
                                continue;
                            }

                            preparedAttempt = item.AttemptCount + 1;
                            fence = generation == null
                                ? null
                                : new OnlineSyncAttemptFence(
                                    generation,
                                    claimToken,
                                    preparedAttempt);
                            run.Attempted++;

                            var bindingError = OutboxShopBinding.GetMismatchCode(
                                item.OriginShopId,
                                item.OriginShopCode,
                                trustedSession.ShopId,
                                trustedSession.ShopCode);
                            if (string.IsNullOrWhiteSpace(bindingError) &&
                                !string.Equals(item.SchemaVersion, PosOnlineContract.SalesSchemaVersion, StringComparison.Ordinal))
                            {
                                bindingError = "schema_mismatch";
                            }

                            if (!string.IsNullOrWhiteSpace(bindingError))
                            {
                                await MarkBlockedAsync(
                                    item,
                                    bindingError,
                                    claimAt,
                                    preparedAttempt,
                                    run,
                                    SyncFailureKind.LocalValidation,
                                    fence).ConfigureAwait(false);
                                continue;
                            }

                            var sale = await _sales.GetByIdAsync(item.SaleId).ConfigureAwait(false);
                            var lines = await _sales.GetLinesBySaleIdAsync(item.SaleId).ConfigureAwait(false);
                            if (sale == null || lines.Count == 0)
                            {
                                await MarkBlockedAsync(item, "missing_sale", claimAt, preparedAttempt, run, SyncFailureKind.LocalValidation, fence)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            if (!string.Equals(item.OperationType, GetOperationType(sale), StringComparison.Ordinal))
                            {
                                await MarkBlockedAsync(item, "operation_type_mismatch", claimAt, preparedAttempt, run, SyncFailureKind.LocalValidation, fence)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            var dependency = await _sales.EvaluateReversalDependencyAsync(item.SaleId)
                                .ConfigureAwait(false);
                            if (dependency.State == ReversalDependencyState.PermanentBlock)
                            {
                                await MarkBlockedAsync(
                                    item,
                                    dependency.Code,
                                    claimAt,
                                    preparedAttempt,
                                    run,
                                    SyncFailureKind.LocalValidation,
                                    fence).ConfigureAwait(false);
                                continue;
                            }

                            if (dependency.State == ReversalDependencyState.Wait)
                            {
                                _logger.LogInfo(
                                    "Sales sync deferred: category=sales.sync code=" + SafeCode(dependency.Code) +
                                    " clientSaleId=" + SafeId(item.ClientSaleId));
                                await MarkDependencyDeferredAsync(
                                    item,
                                    dependency.Code,
                                    preparedAttempt,
                                    run,
                                    fence).ConfigureAwait(false);
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(sale.ClientSaleId))
                            {
                                sale.ClientSaleId = item.ClientSaleId;
                            }

                            if (string.IsNullOrWhiteSpace(item.PayloadJson) ||
                                string.IsNullOrWhiteSpace(item.PayloadHash) ||
                                !string.Equals(
                                    PosSalesSyncRequestBuilder.Sha256Hex(item.PayloadJson),
                                    item.PayloadHash,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                await MarkBlockedAsync(item, "payload_hash_mismatch", claimAt, preparedAttempt, run, SyncFailureKind.LocalValidation, fence)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            var request = PosSalesSyncRequestBuilder.DeserializeCanonical(item.PayloadJson);
                            if (!IsExpectedPersistedRequest(request, item, sale))
                            {
                                await MarkBlockedAsync(item, "persisted_payload_mismatch", claimAt, preparedAttempt, run, SyncFailureKind.LocalValidation, fence)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            if (!PosSalesSyncRequestBuilder.HasCompleteReversalBindings(request))
                            {
                                await MarkBlockedAsync(item, "reversal_original_line_missing", claimAt, preparedAttempt, run, SyncFailureKind.LocalValidation, fence)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            var economicsError = await _sales
                                .GetPersistedReversalEconomicsErrorAsync(item.SaleId, request)
                                .ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(economicsError))
                            {
                                await MarkBlockedAsync(item, economicsError, claimAt, preparedAttempt, run, SyncFailureKind.LocalValidation, fence)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            request.AppVersion = typeof(PosSalesSyncService).Assembly.GetName().Version?.ToString();
                            request.DeviceToken = trustedSession.DeviceToken;
                            request.PosSessionId = trustedSession.PosSessionId;
                            request.SessionToken = trustedSession.SessionToken;
                            request.ShopDeviceId = trustedSession.ShopDeviceId;
                            var syncAttemptId = CreateSyncAttemptId();
                            var payloadJson = PosSalesSyncRequestBuilder.SerializeRedacted(request);
                            var payloadHash = PosSalesSyncRequestBuilder.Sha256Hex(payloadJson);
                            if (!string.Equals(payloadJson, item.PayloadJson, StringComparison.Ordinal) ||
                                !string.Equals(payloadHash, item.PayloadHash, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(request.Batch.ClientBatchId, item.ClientBatchId, StringComparison.Ordinal))
                            {
                                await MarkBlockedAsync(item, "payload_hash_mismatch", claimAt, preparedAttempt, run, SyncFailureKind.LocalValidation, fence)
                                    .ConfigureAwait(false);
                                continue;
                            }

                            _logger.LogInfo(
                                "Sales sync started: category=sales.sync syncAttemptId=" + syncAttemptId +
                                " clientBatchId=" + SafeId(request.Batch?.ClientBatchId) +
                                " clientSaleId=" + SafeId(sale.ClientSaleId ?? item.ClientSaleId) +
                                " attempt=" + preparedAttempt.ToString());
                            var result = executionContext == null
                                ? await client.SalesSyncAsync(request, cancellationToken).ConfigureAwait(false)
                                : await SendWithFreshCredentialsAsync(
                                    client,
                                    request,
                                    executionContext,
                                    cancellationToken).ConfigureAwait(false);
                            _logger.LogInfo(
                                "Sales sync response: category=sales.sync syncAttemptId=" + syncAttemptId +
                                " code=" + SafeCode(result.Code) +
                                " clientRequestId=" + SafeId(result.ClientRequestId) +
                                " serverRequestId=" + SafeId(result.ServerRequestId) +
                                " cfRay=" + SafeId(result.CfRay));
                            var authenticationDenied = await ApplyResultAsync(
                                item,
                                sale,
                                result,
                                syncAttemptId,
                                request.Batch.ClientBatchId,
                                preparedAttempt,
                                run,
                                fence).ConfigureAwait(false);
                            if (authenticationDenied)
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            if (preparedAttempt > 0)
                            {
                                try
                                {
                                    var releaseAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                    await _sales.ReleaseSalesSyncAttemptAsync(
                                        item.Id,
                                        item.SaleId,
                                        "cancelled",
                                        releaseAt,
                                        releaseAt,
                                        preparedAttempt,
                                        fence).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    // A local transition failure must not replace the caller's
                                    // cancellation. The bounded in-progress lease remains recoverable.
                                    _logger.LogWarning(
                                        "Sales sync cancellation retry persistence failed.",
                                        ex);
                                }
                            }
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Sales sync item skipped.", ex);
                            if (preparedAttempt > 0)
                            {
                                await MarkRetryAsync(
                                    item,
                                    "exception",
                                    preparedAttempt,
                                    run,
                                    SyncFailureKind.Unexpected,
                                    fence: fence).ConfigureAwait(false);
                            }
                            else
                            {
                                run.SetFailureIfNone(SyncFailureKind.Unexpected, "local_preflight_failed");
                            }
                        }
                    }
                }

                return await CompleteAsync(run).ConfigureAwait(false);
            }
            finally
            {
                await StoreSalesSyncInProgressAsync(false, generation).ConfigureAwait(false);
                Interlocked.Exchange(ref _syncInFlight, 0);
            }
        }

        private static bool IsExpectedPersistedRequest(
            PosSalesSyncRequest request,
            SalesSyncOutboxItem item,
            Sale sale)
        {
            var persistedSale = request?.Sales != null && request.Sales.Length == 1
                ? request.Sales[0]
                : null;
            return request != null &&
                request.Batch != null &&
                persistedSale != null &&
                string.Equals(request.SchemaVersion, item.SchemaVersion, StringComparison.Ordinal) &&
                string.Equals(request.ShopCode, item.OriginShopCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.Batch.ClientBatchId, item.ClientBatchId, StringComparison.Ordinal) &&
                string.Equals(request.Batch.IdempotencyKey, item.ClientBatchId, StringComparison.Ordinal) &&
                string.Equals(persistedSale.ClientSaleId, item.ClientSaleId, StringComparison.Ordinal) &&
                string.Equals(persistedSale.ClientSaleId, sale.ClientSaleId, StringComparison.Ordinal) &&
                string.Equals(persistedSale.IdempotencyKey, item.IdempotencyKey, StringComparison.Ordinal) &&
                string.Equals(persistedSale.Kind, item.OperationType, StringComparison.Ordinal);
        }

        private async Task<bool> ApplyResultAsync(
            SalesSyncOutboxItem item,
            Sale sale,
            PosOnlineResult<PosSalesSyncResponse> result,
            string syncAttemptId,
            string expectedClientBatchId,
            int preparedAttempt,
            SalesDrainAccumulator run,
            OnlineSyncAttemptFence fence)
        {
            if (!result.Success || result.Value == null || !result.Value.Ok)
            {
                var responseCode = ResolveFailureCode(result);
                if (result.Denied || SharedAuthStopPolicy.IsAuthenticationDenied(responseCode))
                {
                    // Authentication is a transport-generation fact. Preserve it even if
                    // another worker wins the local retry transition CAS.
                    run.SetFailure(SyncFailureKind.AuthenticationDenied, "auth_denied");
                    try
                    {
                        if (fence == null)
                            _store.Clear();
                        else
                            _store.TryClear(fence.Generation.GenerationId);
                        await MarkRetryAsync(
                            item,
                            "auth_denied",
                            preparedAttempt,
                            run,
                            SyncFailureKind.AuthenticationDenied,
                            allowPermanentBlock: false,
                            fence: fence).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "Sales sync auth-stop local persistence failed; batch remains stopped.",
                            ex);
                    }
                    _logger.LogWarning(
                        "Sales sync auth denied: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                        " clientSaleId=" + SafeId(item.ClientSaleId) +
                        " serverRequestId=" + SafeId(result.ServerRequestId));
                    return true;
                }

                if (IsBlockedFailure(responseCode))
                {
                    await MarkBlockedAsync(
                        item,
                        responseCode,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        preparedAttempt,
                        run,
                        SyncFailureKind.PermanentRemote,
                        fence)
                        .ConfigureAwait(false);
                    _logger.LogWarning(
                        "Sales sync blocked: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                        " clientSaleId=" + SafeId(item.ClientSaleId) +
                        " code=" + responseCode +
                        " serverRequestId=" + SafeId(result.ServerRequestId));
                    return false;
                }

                var failureKind = GetRetryableFailureKind(responseCode);
                await MarkRetryAsync(
                    item,
                    responseCode,
                    preparedAttempt,
                    run,
                    failureKind,
                    fence: fence).ConfigureAwait(false);
                _logger.LogWarning(
                    "Sales sync retry scheduled: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                    " clientSaleId=" + SafeId(item.ClientSaleId) +
                    " code=" + responseCode +
                    " serverRequestId=" + SafeId(result.ServerRequestId));
                return false;
            }

            var ack = (result.Value.Sales ?? Array.Empty<PosSalesSyncSaleAck>())
                .FirstOrDefault(row => string.Equals(
                    row.ClientSaleId,
                    sale.ClientSaleId ?? item.ClientSaleId,
                    StringComparison.Ordinal));

            if (ack == null)
            {
                await MarkRetryAsync(
                    item,
                    "missing_ack",
                    preparedAttempt,
                    run,
                    SyncFailureKind.RetryableRemote,
                    fence: fence).ConfigureAwait(false);
                _logger.LogWarning(
                    "Sales sync missing ack: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                    " clientSaleId=" + SafeId(item.ClientSaleId) +
                    " clientBatchId=" + SafeId(result.Value.Batch?.ClientBatchId));
                return false;
            }

            if (!IsAcceptedAckStatus(ack.Status))
            {
                var ackCode = "ack_" + SafeCode(ack.Status);
                if (IsBlockedAckStatus(ack.Status))
                {
                    await MarkBlockedAsync(
                        item,
                        ackCode,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        preparedAttempt,
                        run,
                        SyncFailureKind.PermanentRemote,
                        fence)
                        .ConfigureAwait(false);
                    _logger.LogWarning(
                        "Sales sync ack blocked: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                        " clientSaleId=" + SafeId(item.ClientSaleId) +
                        " ackStatus=" + SafeCode(ack.Status) +
                        " serverRequestId=" + SafeId(result.ServerRequestId));
                    return false;
                }

                await MarkRetryAsync(
                    item,
                    ackCode,
                    preparedAttempt,
                    run,
                    SyncFailureKind.RetryableRemote,
                    fence: fence).ConfigureAwait(false);
                _logger.LogWarning(
                    "Sales sync ack unknown: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                    " clientSaleId=" + SafeId(item.ClientSaleId) +
                    " ackStatus=" + SafeCode(ack.Status) +
                    " serverRequestId=" + SafeId(result.ServerRequestId));
                return false;
            }

            if (result.Value.Batch == null ||
                !string.Equals(result.Value.Batch.ClientBatchId, expectedClientBatchId, StringComparison.Ordinal))
            {
                await MarkBlockedAsync(
                    item,
                    "client_batch_mismatch",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt,
                    run,
                    SyncFailureKind.PermanentRemote,
                    fence).ConfigureAwait(false);
                return false;
            }

            try
            {
                ReceiptShopMetadataPolicy.EnsureValidRemoteShop(result.Value.Shop);
            }
            catch (ReceiptContentValidationException ex)
            {
                await MarkBlockedAsync(
                    item,
                    "response_shop_metadata_invalid",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt,
                    run,
                    SyncFailureKind.PermanentRemote,
                    fence).ConfigureAwait(false);
                _logger.LogWarning(
                    "Sales sync shop metadata rejected: category=sales.sync code=" +
                    SafeCode(ex.Code) + " field=" + SafeCode(ex.Field));
                return false;
            }

            var responseBindingError = OutboxShopBinding.GetMismatchCode(
                item.OriginShopId,
                item.OriginShopCode,
                result.Value.Shop?.ShopId,
                result.Value.Shop?.ShopCode);
            if (!string.IsNullOrWhiteSpace(responseBindingError))
            {
                await MarkBlockedAsync(
                    item,
                    "response_shop_mismatch",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preparedAttempt,
                    run,
                    SyncFailureKind.PermanentRemote,
                    fence).ConfigureAwait(false);
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var acked = await _sales.MarkSalesSyncAckedAsync(
                item.Id,
                item.SaleId,
                result.Value.Batch?.PosSalesSyncBatchId,
                ack.PosSaleId,
                nowMs,
                preparedAttempt,
                fence).ConfigureAwait(false);
            if (!acked)
            {
                run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain, "sales_sync_ack_cas_lost");
                return false;
            }

            run.Acked++;

            await PosOnlineShopSnapshot.SaveAsync(
                _factory,
                result.Value.Shop,
                fence?.Generation).ConfigureAwait(false);
            await StoreSalesSyncSuccessAsync(
                result.Value.ServerTime,
                fence?.Generation).ConfigureAwait(false);
            _logger.LogInfo(
                "Sales sync acked: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                " clientSaleId=" + SafeId(item.ClientSaleId) +
                " clientBatchId=" + SafeId(result.Value.Batch?.ClientBatchId) +
                " ackStatus=" + SafeCode(ack.Status) +
                " serverRequestId=" + SafeId(result.ServerRequestId));
            return false;
        }

        private static async Task<PosOnlineResult<PosSalesSyncResponse>>
            SendWithFreshCredentialsAsync(
                PosAdminWebClient client,
                PosSalesSyncRequest request,
                OnlineSyncLaneExecutionContext executionContext,
                CancellationToken cancellationToken)
        {
            for (var credentialAttempt = 0; credentialAttempt < 2; credentialAttempt++)
            {
                try
                {
                    return await executionContext.ExecuteCredentialedRequestAsync(
                        (credentials, token) =>
                        {
                            request.DeviceToken = credentials.DeviceToken;
                            request.PosSessionId = credentials.PosSessionId;
                            request.SessionToken = credentials.SessionToken;
                            request.ShopDeviceId = credentials.ShopDeviceId;
                            return client.SalesSyncAsync(request, token);
                        },
                        result =>
                        {
                            var code = ResolveFailureCode(result);
                            return result.Denied ||
                                SharedAuthStopPolicy.IsAuthenticationDenied(code)
                                    ? code
                                    : string.Empty;
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OnlineSyncCredentialsChangedException) when (credentialAttempt == 0)
                {
                    // A heartbeat rotated the token while this request was in flight.
                }
            }

            throw new OnlineSyncCredentialsChangedException();
        }

        private async Task<bool> MarkRetryAsync(
            SalesSyncOutboxItem item,
            string errorCode,
            int preparedAttempt,
            SalesDrainAccumulator run,
            SyncFailureKind failureKind,
            bool allowPermanentBlock = true,
            OnlineSyncAttemptFence fence = null)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var attempts = Math.Max(1, preparedAttempt);
            if (allowPermanentBlock && attempts >= MaxAttemptsBeforeBlocked)
            {
                return await MarkBlockedAsync(
                    item,
                    errorCode,
                    nowMs,
                    preparedAttempt,
                    run,
                    failureKind,
                    fence).ConfigureAwait(false);
            }

            var delaySeconds = Math.Min(300, 10 * attempts);
            var transitioned = await _sales.MarkSalesSyncRetryAsync(
                item.Id,
                item.SaleId,
                SafeCode(errorCode),
                nowMs + delaySeconds * 1000L,
                nowMs,
                preparedAttempt,
                fence).ConfigureAwait(false);
            if (transitioned)
            {
                await StoreSalesSyncFailureAsync(
                    errorCode,
                    fence?.Generation).ConfigureAwait(false);
                run.Retried++;
                run.SetFailure(failureKind, SafeCode(errorCode));
            }
            else
            {
                run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain, "sales_sync_retry_cas_lost");
            }

            return transitioned;
        }

        private async Task<bool> MarkDependencyDeferredAsync(
            SalesSyncOutboxItem item,
            string errorCode,
            int preparedAttempt,
            SalesDrainAccumulator run,
            OnlineSyncAttemptFence fence)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var transitioned = await _sales.DeferSalesSyncDependencyAsync(
                item.Id,
                item.SaleId,
                SafeCode(errorCode),
                nowMs + 5000L,
                nowMs,
                preparedAttempt,
                fence).ConfigureAwait(false);
            if (transitioned)
            {
                await StoreSalesSyncFailureAsync(
                    errorCode,
                    fence?.Generation).ConfigureAwait(false);
                run.Retried++;
                run.SetFailure(SyncFailureKind.RetryableRemote, SafeCode(errorCode));
            }
            else
            {
                run.SetFailureIfNone(
                    SyncFailureKind.ConcurrentDrain,
                    "sales_sync_dependency_cas_lost");
            }

            return transitioned;
        }

        private async Task<bool> MarkBlockedAsync(
            SalesSyncOutboxItem item,
            string errorCode,
            long nowMs,
            int preparedAttempt,
            SalesDrainAccumulator run,
            SyncFailureKind failureKind,
            OnlineSyncAttemptFence fence = null)
        {
            var transitioned = await _sales.MarkSalesSyncBlockedAsync(
                item.Id,
                item.SaleId,
                SafeCode(errorCode),
                nowMs,
                preparedAttempt,
                fence).ConfigureAwait(false);
            if (transitioned)
            {
                await StoreSalesSyncFailureAsync(
                    errorCode,
                    fence?.Generation).ConfigureAwait(false);
                run.Blocked++;
                run.SetFailure(failureKind, SafeCode(errorCode));
            }
            else
            {
                run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain, "sales_sync_block_cas_lost");
            }

            return transitioned;
        }

        private async Task<OutboxDrainResult> CompleteAsync(SalesDrainAccumulator run)
        {
            var state = await _sales.GetSalesSyncDrainStateAsync(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
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

        private static string GetOperationType(Sale sale)
        {
            return sale.Kind == (int)SaleKind.Void
                ? "void"
                : sale.Kind == (int)SaleKind.Refund
                    ? "refund"
                    : "sale";
        }

        private async Task StoreSalesSyncSuccessAsync(
            string serverTime,
            OnlineSyncGeneration generation)
        {
            var settings = new SettingsRepository(_factory);
            var value = string.IsNullOrWhiteSpace(serverTime)
                ? DateTimeOffset.UtcNow.ToString("O")
                : serverTime.Trim();
            if (!await settings.SetStringIfGenerationCurrentAsync(
                    LastSalesSyncSettingKey,
                    value,
                    generation).ConfigureAwait(false))
            {
                return;
            }
            await settings.SetStringIfGenerationCurrentAsync(
                LastSalesErrorSettingKey,
                string.Empty,
                generation).ConfigureAwait(false);
        }

        private async Task StoreSalesSyncFailureAsync(
            string code,
            OnlineSyncGeneration generation)
        {
            var settings = new SettingsRepository(_factory);
            await settings.SetStringIfGenerationCurrentAsync(
                LastSalesErrorSettingKey,
                SafeCode(code),
                generation).ConfigureAwait(false);
        }

        private async Task StoreSalesSyncInProgressAsync(
            bool inProgress,
            OnlineSyncGeneration generation)
        {
            try
            {
                var settings = new SettingsRepository(_factory);
                await settings.SetBoolIfGenerationCurrentAsync(
                    SalesSyncInProgressSettingKey,
                    inProgress,
                    generation)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Sales sync in-progress state not stored.", ex);
            }
        }

        private static bool IsBlockedFailure(string code)
        {
            return string.Equals(code, "validation_failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "conflict", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveFailureCode(PosOnlineResult<PosSalesSyncResponse> result)
        {
            if (result == null)
            {
                return "remote_rejected";
            }

            if (result.Value != null && !result.Value.Ok)
            {
                var bodyCode = (result.Value.Code ?? string.Empty).Trim();
                if (bodyCode.Length == 0 ||
                    string.Equals(bodyCode, "success", StringComparison.OrdinalIgnoreCase))
                {
                    return "remote_rejected";
                }

                return SafeCode(bodyCode);
            }

            return result.Success ? "remote_rejected" : SafeCode(result.Code);
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

        private static bool IsAcceptedAckStatus(string status)
        {
            return string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "duplicate", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "acked", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "synced", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "idempotent", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBlockedAckStatus(string status)
        {
            return string.Equals(status, "conflict", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "validation_failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "failed_blocked", StringComparison.OrdinalIgnoreCase);
        }

        private static string CreateSyncAttemptId()
        {
            return "winsync-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static string SafeCode(string code)
        {
            var normalized = (TrimOrNull(code, 80) ?? string.Empty).ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return "failure";
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
                    return "failure";
                }
            }

            return normalized;
        }

        private static string SafeId(string value)
        {
            var normalized = TrimOrNull(value, 80);
            return string.IsNullOrWhiteSpace(normalized) ? "none" : normalized;
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

        private sealed class SalesDrainAccumulator
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
    }
}
