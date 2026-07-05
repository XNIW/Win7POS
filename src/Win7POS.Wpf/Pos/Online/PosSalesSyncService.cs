using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
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
        private static int _syncInFlight;

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

        public async Task<bool> TrySyncPendingAsync(
            PosAdminWebOptions options,
            CancellationToken cancellationToken)
        {
            if (options == null)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0)
            {
                _logger.LogInfo("Sales sync skipped: already running.");
                return false;
            }

            await StoreSalesSyncInProgressAsync(true).ConfigureAwait(false);

            try
            {
            if (!_store.TryRead(out var trustedSession))
            {
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pending = await _sales
                .GetPendingSalesSyncOutboxAsync(MaxOutboxItemsPerRun, nowMs)
                .ConfigureAwait(false);

            if (pending.Count == 0)
            {
                return true;
            }

            var syncedAny = false;
            using (var client = new PosAdminWebClient(options))
            {
                foreach (var item in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var sale = await _sales.GetByIdAsync(item.SaleId).ConfigureAwait(false);
                        var lines = await _sales.GetLinesBySaleIdAsync(item.SaleId).ConfigureAwait(false);
                        if (sale == null || lines.Count == 0)
                        {
                            await MarkBlockedAsync(item, "missing_sale", nowMs).ConfigureAwait(false);
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(sale.ClientSaleId))
                        {
                            sale.ClientSaleId = item.ClientSaleId;
                        }

                        var request = await BuildRequestAsync(
                            trustedSession,
                            item,
                            sale,
                            lines.ToArray()).ConfigureAwait(false);
                        var syncAttemptId = CreateSyncAttemptId();
                        var payloadJson = PosSalesSyncRequestBuilder.SerializeRedacted(request);
                        var payloadHash = PosSalesSyncRequestBuilder.Sha256Hex(payloadJson);

                        await _sales.PrepareSalesSyncAttemptAsync(
                            item.Id,
                            request.Batch.ClientBatchId,
                            payloadJson,
                            payloadHash,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);

                        _logger.LogInfo(
                            "Sales sync started: category=sales.sync syncAttemptId=" + syncAttemptId +
                            " clientBatchId=" + SafeId(request.Batch?.ClientBatchId) +
                            " clientSaleId=" + SafeId(sale.ClientSaleId ?? item.ClientSaleId) +
                            " attempt=" + (item.AttemptCount + 1).ToString());
                        var result = await client.SalesSyncAsync(request, cancellationToken).ConfigureAwait(false);
                        _logger.LogInfo(
                            "Sales sync response: category=sales.sync syncAttemptId=" + syncAttemptId +
                            " code=" + SafeCode(result.Code) +
                            " clientRequestId=" + SafeId(result.ClientRequestId) +
                            " serverRequestId=" + SafeId(result.ServerRequestId) +
                            " cfRay=" + SafeId(result.CfRay));
                        if (await ApplyResultAsync(item, sale, result, syncAttemptId).ConfigureAwait(false))
                        {
                            syncedAny = true;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Sales sync item skipped.", ex);
                        await MarkRetryAsync(item, "exception").ConfigureAwait(false);
                    }
                }
            }

            return syncedAny;
            }
            finally
            {
                await StoreSalesSyncInProgressAsync(false).ConfigureAwait(false);
                Interlocked.Exchange(ref _syncInFlight, 0);
            }
        }

        private async Task<PosSalesSyncRequest> BuildRequestAsync(
            PosTrustedDeviceSession trustedSession,
            SalesSyncOutboxItem item,
            Sale sale,
            SaleLine[] lines)
        {
            return await PosSalesSyncRequestBuilder.BuildAsync(
                trustedSession,
                item,
                sale,
                lines,
                _sales,
                typeof(PosSalesSyncService).Assembly.GetName().Version?.ToString()).ConfigureAwait(false);
        }

        private async Task<bool> ApplyResultAsync(
            SalesSyncOutboxItem item,
            Sale sale,
            PosOnlineResult<PosSalesSyncResponse> result,
            string syncAttemptId)
        {
            if (!result.Success || result.Value == null || !result.Value.Ok)
            {
                if (result.Denied)
                {
                    _store.Clear();
                    await StoreSalesSyncFailureAsync("auth_denied").ConfigureAwait(false);
                    await MarkRetryAsync(item, "auth_denied").ConfigureAwait(false);
                    _logger.LogWarning(
                        "Sales sync auth denied: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                        " clientSaleId=" + SafeId(item.ClientSaleId) +
                        " serverRequestId=" + SafeId(result.ServerRequestId));
                    return false;
                }

                if (IsBlockedFailure(result.Code))
                {
                    await StoreSalesSyncFailureAsync(result.Code).ConfigureAwait(false);
                    await MarkBlockedAsync(item, result.Code, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        .ConfigureAwait(false);
                    _logger.LogWarning(
                        "Sales sync blocked: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                        " clientSaleId=" + SafeId(item.ClientSaleId) +
                        " code=" + SafeCode(result.Code) +
                        " serverRequestId=" + SafeId(result.ServerRequestId));
                    return false;
                }

                await StoreSalesSyncFailureAsync(result.Code).ConfigureAwait(false);
                await MarkRetryAsync(item, result.Code).ConfigureAwait(false);
                _logger.LogWarning(
                    "Sales sync retry scheduled: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                    " clientSaleId=" + SafeId(item.ClientSaleId) +
                    " code=" + SafeCode(result.Code) +
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
                await MarkRetryAsync(item, "missing_ack").ConfigureAwait(false);
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
                    await MarkBlockedAsync(item, ackCode, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        .ConfigureAwait(false);
                    _logger.LogWarning(
                        "Sales sync ack blocked: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                        " clientSaleId=" + SafeId(item.ClientSaleId) +
                        " ackStatus=" + SafeCode(ack.Status) +
                        " serverRequestId=" + SafeId(result.ServerRequestId));
                    return false;
                }

                await MarkRetryAsync(item, ackCode).ConfigureAwait(false);
                _logger.LogWarning(
                    "Sales sync ack unknown: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                    " clientSaleId=" + SafeId(item.ClientSaleId) +
                    " ackStatus=" + SafeCode(ack.Status) +
                    " serverRequestId=" + SafeId(result.ServerRequestId));
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _sales.MarkSalesSyncAckedAsync(
                item.Id,
                item.SaleId,
                result.Value.Batch?.PosSalesSyncBatchId,
                ack.PosSaleId,
                nowMs).ConfigureAwait(false);
            await PosOnlineShopSnapshot.SaveAsync(_factory, result.Value.Shop).ConfigureAwait(false);
            await StoreSalesSyncSuccessAsync(result.Value.ServerTime).ConfigureAwait(false);
            _logger.LogInfo(
                "Sales sync acked: category=sales.sync syncAttemptId=" + SafeId(syncAttemptId) +
                " clientSaleId=" + SafeId(item.ClientSaleId) +
                " clientBatchId=" + SafeId(result.Value.Batch?.ClientBatchId) +
                " ackStatus=" + SafeCode(ack.Status) +
                " serverRequestId=" + SafeId(result.ServerRequestId));
            return true;
        }

        private async Task MarkRetryAsync(SalesSyncOutboxItem item, string errorCode)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await StoreSalesSyncFailureAsync(errorCode).ConfigureAwait(false);
            var attempts = Math.Max(1, item.AttemptCount + 1);
            if (attempts >= MaxAttemptsBeforeBlocked)
            {
                await MarkBlockedAsync(item, errorCode, nowMs).ConfigureAwait(false);
                return;
            }

            var delaySeconds = Math.Min(300, 10 * attempts);
            await _sales.MarkSalesSyncRetryAsync(
                item.Id,
                item.SaleId,
                SafeCode(errorCode),
                nowMs + delaySeconds * 1000L,
                nowMs).ConfigureAwait(false);
        }

        private async Task MarkBlockedAsync(SalesSyncOutboxItem item, string errorCode, long nowMs)
        {
            await StoreSalesSyncFailureAsync(errorCode).ConfigureAwait(false);
            await _sales.MarkSalesSyncBlockedAsync(
                item.Id,
                item.SaleId,
                SafeCode(errorCode),
                nowMs).ConfigureAwait(false);
        }

        private async Task StoreSalesSyncSuccessAsync(string serverTime)
        {
            var settings = new SettingsRepository(_factory);
            var value = string.IsNullOrWhiteSpace(serverTime)
                ? DateTimeOffset.UtcNow.ToString("O")
                : serverTime.Trim();
            await settings.SetStringAsync(LastSalesSyncSettingKey, value).ConfigureAwait(false);
            await settings.SetStringAsync(LastSalesErrorSettingKey, string.Empty).ConfigureAwait(false);
        }

        private async Task StoreSalesSyncFailureAsync(string code)
        {
            var settings = new SettingsRepository(_factory);
            await settings.SetStringAsync(LastSalesErrorSettingKey, SafeCode(code)).ConfigureAwait(false);
        }

        private async Task StoreSalesSyncInProgressAsync(bool inProgress)
        {
            try
            {
                var settings = new SettingsRepository(_factory);
                await settings.SetBoolAsync(SalesSyncInProgressSettingKey, inProgress)
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
            var normalized = TrimOrNull(code, 80);
            return string.IsNullOrWhiteSpace(normalized) ? "failure" : normalized;
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
    }
}
