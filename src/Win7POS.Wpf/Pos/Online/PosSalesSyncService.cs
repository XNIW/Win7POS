using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;
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
                        var payloadJson = PosSalesSyncRequestBuilder.SerializeRedacted(request);
                        var payloadHash = PosSalesSyncRequestBuilder.Sha256Hex(payloadJson);

                        await _sales.PrepareSalesSyncAttemptAsync(
                            item.Id,
                            request.Batch.ClientBatchId,
                            payloadJson,
                            payloadHash,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);

                        var result = await client.SalesSyncAsync(request, cancellationToken).ConfigureAwait(false);
                        if (await ApplyResultAsync(item, sale, result).ConfigureAwait(false))
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
            PosOnlineResult<PosSalesSyncResponse> result)
        {
            if (!result.Success || result.Value == null || !result.Value.Ok)
            {
                if (result.Denied)
                {
                    _store.Clear();
                    await MarkRetryAsync(item, "auth_denied").ConfigureAwait(false);
                    return false;
                }

                if (IsBlockedFailure(result.Code))
                {
                    await MarkBlockedAsync(item, result.Code, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        .ConfigureAwait(false);
                    return false;
                }

                await MarkRetryAsync(item, result.Code).ConfigureAwait(false);
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
                return false;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _sales.MarkSalesSyncAckedAsync(
                item.Id,
                item.SaleId,
                result.Value.Batch?.PosSalesSyncBatchId,
                ack.PosSaleId,
                nowMs).ConfigureAwait(false);
            _logger.LogInfo("Sales sync acked: " + item.ClientSaleId);
            return true;
        }

        private async Task MarkRetryAsync(SalesSyncOutboxItem item, string errorCode)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
            await _sales.MarkSalesSyncBlockedAsync(
                item.Id,
                item.SaleId,
                SafeCode(errorCode),
                nowMs).ConfigureAwait(false);
        }

        private static bool IsBlockedFailure(string code)
        {
            return string.Equals(code, "validation_failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "conflict", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeCode(string code)
        {
            var normalized = TrimOrNull(code, 80);
            return string.IsNullOrWhiteSpace(normalized) ? "failure" : normalized;
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
