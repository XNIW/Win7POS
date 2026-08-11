using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns reversal reads, economics/dependency policy, and reversal writes.
    /// Caller-owned reversal operations deliberately receive the supplied
    /// connection and transaction and never create or settle a transaction.
    /// </summary>
    internal sealed class SaleReversalWriter
    {
        private readonly SqliteConnectionFactory _factory;

        internal SaleReversalWriter(SqliteConnectionFactory factory)
        {
            _factory = factory;
        }

        internal async Task<bool> IsVoidedAsync(long saleId)
        {
            using var conn = _factory.Open();
            var voidedBy = await conn.QuerySingleOrDefaultAsync<long?>(
                "SELECT voided_by_sale_id FROM sales WHERE id = @saleId",
                new { saleId }).ConfigureAwait(false);
            return voidedBy.HasValue;
        }

        internal async Task<int> GetRefundedQtyAsync(long originalSaleId, long originalLineId)
        {
            using var conn = _factory.Open();
            var qty = await conn.QuerySingleAsync<int>(@"
SELECT COALESCE(SUM(ABS(sl.quantity)), 0)
FROM sale_lines sl
JOIN sales s ON s.id = sl.saleId
WHERE s.kind IN (@kindRefund, @kindVoid)
  AND s.related_sale_id = @originalSaleId
  AND sl.related_original_line_id = @originalLineId",
                new
                {
                    kindRefund = (int)SaleKind.Refund,
                    kindVoid = (int)SaleKind.Void,
                    originalSaleId,
                    originalLineId
                }).ConfigureAwait(false);
            return qty;
        }

        internal async Task<List<SaleLineReturnableDto>> GetReturnableLinesAsync(long saleId)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SaleLineReturnableDto>(@"
SELECT
  l.id AS OriginalLineId,
  l.saleId AS OriginalSaleId,
  l.productId,
  l.barcode,
  l.name,
  l.unitPrice,
  l.quantity AS SoldQty,
  COALESCE((
    SELECT SUM(ABS(rl.quantity))
    FROM sale_lines rl
    JOIN sales rs ON rs.id = rl.saleId
    WHERE rs.kind IN (@kindRefund, @kindVoid)
      AND rs.related_sale_id = @saleId
      AND rl.related_original_line_id = l.id
  ), 0) AS RefundedQty
FROM sale_lines l
WHERE l.saleId = @saleId
  AND COALESCE(l.barcode, '') NOT LIKE @discountPrefix
  AND COALESCE(l.barcode, '') NOT LIKE @taxPrefix
ORDER BY l.id ASC",
                new
                {
                    saleId,
                    kindRefund = (int)SaleKind.Refund,
                    kindVoid = (int)SaleKind.Void,
                    discountPrefix = DiscountKeys.Prefix + "%",
                    taxPrefix = DiscountKeys.TaxPrefix + "%"
                }).ConfigureAwait(false);

            var list = rows.ToList();
            foreach (var x in list)
            {
                x.RemainingQty = x.SoldQty - x.RefundedQty;
                if (x.RemainingQty < 0) x.RemainingQty = 0;
            }

            return list;
        }

        internal async Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotAsync(
            long originalSaleId)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            return await PosReversalEconomicsReader
                .LoadAsync(conn, null, originalSaleId, null)
                .ConfigureAwait(false);
        }

        internal async Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotExcludingAsync(
            long originalSaleId,
            long excludedReversalSaleId)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            return await PosReversalEconomicsReader
                .LoadAsync(conn, null, originalSaleId, excludedReversalSaleId)
                .ConfigureAwait(false);
        }

        internal async Task<string> GetPersistedReversalEconomicsErrorAsync(
            long saleId,
            PosSalesSyncRequest request)
        {
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            var row = await conn.QuerySingleOrDefaultAsync<PersistedReversalRow>(@"
SELECT kind AS Kind, related_sale_id AS RelatedSaleId, total AS SaleTotal
FROM sales
WHERE id = @saleId;",
                new { saleId }).ConfigureAwait(false);
            if (row == null ||
                (row.Kind != (int)SaleKind.Refund && row.Kind != (int)SaleKind.Void))
            {
                return null;
            }

            if (!row.RelatedSaleId.HasValue ||
                !PosSalesSyncRequestBuilder.TryGetReversalGross(request, out var grossClp))
            {
                return ReversalEconomicsPolicy.MismatchCode;
            }

            try
            {
                var snapshot = await PosReversalEconomicsReader
                    .LoadAsync(conn, null, row.RelatedSaleId.Value, saleId)
                    .ConfigureAwait(false);
                var expected = ReversalEconomicsPolicy.Calculate(snapshot, grossClp);
                return row.SaleTotal == expected.NetClp &&
                    PosSalesSyncRequestBuilder.HasExpectedReversalEconomics(request, expected)
                        ? null
                        : ReversalEconomicsPolicy.MismatchCode;
            }
            catch (InvalidOperationException ex) when (IsReversalEconomicsCode(ex.Message))
            {
                return ex.Message;
            }
        }

        internal async Task MarkSaleVoidedAsync(long originalSaleId, long refundSaleId, long nowMs)
        {
            using var conn = _factory.Open();
            await MarkSaleVoidedAsync(conn, null, originalSaleId, refundSaleId, nowMs)
                .ConfigureAwait(false);
        }

        internal async Task<bool> IsReversalDependencyReadyAsync(long saleId)
        {
            var decision = await EvaluateReversalDependencyAsync(saleId).ConfigureAwait(false);
            return decision.State == ReversalDependencyState.Ready;
        }

        internal async Task<ReversalDependencyDecision> EvaluateReversalDependencyAsync(long saleId)
        {
            using var conn = _factory.Open();
            var row = await conn.QuerySingleOrDefaultAsync<ReversalDependencyRow>(@"
SELECT
  current.kind AS Kind,
  current.related_sale_id AS RelatedSaleId,
  original.kind AS OriginalKind,
  original_outbox.status AS OriginalOutboxStatus,
  original_outbox.origin_shop_id AS OriginalOriginShopId,
  original_outbox.origin_shop_code AS OriginalOriginShopCode,
  current_outbox.origin_shop_id AS CurrentOriginShopId,
  current_outbox.origin_shop_code AS CurrentOriginShopCode,
  official_id.value AS OfficialShopId,
  official_code.value AS OfficialShopCode,
  (
    SELECT COUNT(1)
    FROM sales prior
    LEFT JOIN sales_sync_outbox prior_outbox ON prior_outbox.sale_id = prior.id
    WHERE prior.related_sale_id = current.related_sale_id
      AND prior.kind IN (@kindRefund, @kindVoid)
      AND prior.id < current.id
      AND COALESCE(prior_outbox.status, '') <> 'acked'
  ) AS PriorReversalNotAcked
  ,(
    SELECT COUNT(1)
    FROM sales prior
    LEFT JOIN sales_sync_outbox prior_outbox ON prior_outbox.sale_id = prior.id
    WHERE prior.related_sale_id = current.related_sale_id
      AND prior.kind IN (@kindRefund, @kindVoid)
      AND prior.id < current.id
      AND (prior_outbox.status IS NULL OR prior_outbox.status = 'failed_blocked')
  ) AS PriorReversalPermanent
FROM sales current
LEFT JOIN sales original ON original.id = current.related_sale_id
LEFT JOIN sales_sync_outbox original_outbox ON original_outbox.sale_id = original.id
LEFT JOIN sales_sync_outbox current_outbox ON current_outbox.sale_id = current.id
LEFT JOIN app_settings official_id ON official_id.key = 'pos.official_shop.shop_id'
LEFT JOIN app_settings official_code ON official_code.key = 'pos.official_shop.shop_code'
WHERE current.id = @saleId;",
                new
                {
                    saleId,
                    kindRefund = (int)SaleKind.Refund,
                    kindVoid = (int)SaleKind.Void
                }).ConfigureAwait(false);
            if (row == null)
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "missing_sale");
            }

            if (row.Kind != (int)SaleKind.Refund && row.Kind != (int)SaleKind.Void)
            {
                return Dependency(ReversalDependencyState.Ready, string.Empty);
            }

            if (!row.RelatedSaleId.HasValue || row.OriginalKind != (int)SaleKind.Sale)
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "original_sale_missing");
            }

            var currentBindingError = OutboxShopBinding.GetMismatchCode(
                row.OriginalOriginShopId,
                row.OriginalOriginShopCode,
                row.CurrentOriginShopId,
                row.CurrentOriginShopCode);
            if (!string.IsNullOrWhiteSpace(currentBindingError))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, currentBindingError);
            }

            var officialBindingError = OutboxShopBinding.GetMismatchCode(
                row.OriginalOriginShopId,
                row.OriginalOriginShopCode,
                row.OfficialShopId,
                row.OfficialShopCode);
            if (!string.IsNullOrWhiteSpace(officialBindingError))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, officialBindingError);
            }

            if (string.Equals(row.OriginalOutboxStatus, "failed_blocked", StringComparison.Ordinal))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "original_sale_blocked");
            }

            if (string.IsNullOrWhiteSpace(row.OriginalOutboxStatus))
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "original_sale_outbox_missing");
            }

            if (!string.Equals(row.OriginalOutboxStatus, "acked", StringComparison.Ordinal))
            {
                return Dependency(ReversalDependencyState.Wait, "original_sale_not_acked");
            }

            if (row.PriorReversalPermanent > 0)
            {
                return Dependency(ReversalDependencyState.PermanentBlock, "prior_reversal_blocked");
            }

            if (row.PriorReversalNotAcked > 0)
            {
                return Dependency(ReversalDependencyState.Wait, "prior_reversal_not_acked");
            }

            return Dependency(ReversalDependencyState.Ready, string.Empty);
        }

        internal async Task ValidateReversalBoundaryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            if (sale.Kind != (int)SaleKind.Refund && sale.Kind != (int)SaleKind.Void)
            {
                return;
            }

            if (!sale.RelatedSaleId.HasValue || sale.RelatedSaleId.Value <= 0)
            {
                throw new InvalidOperationException("Reversal requires an existing original sale.");
            }

            var originalKind = await conn.ExecuteScalarAsync<int?>(
                "SELECT kind FROM sales WHERE id = @originalSaleId;",
                new { originalSaleId = sale.RelatedSaleId.Value },
                tx).ConfigureAwait(false);
            if (originalKind != (int)SaleKind.Sale)
            {
                throw new InvalidOperationException("Reversal original sale is missing or incoherent.");
            }

            var ackedOrigin = await conn.QuerySingleOrDefaultAsync<OriginalAckBindingRow>(@"
SELECT
  status AS Status,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode
FROM sales_sync_outbox
WHERE sale_id = @originalSaleId;",
                new { originalSaleId = sale.RelatedSaleId.Value },
                tx).ConfigureAwait(false);
            if (string.Equals(ackedOrigin?.Status, "acked", StringComparison.Ordinal))
            {
                var official = await OutboxShopBinding.ResolveRequiredAsync(conn, tx).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    ackedOrigin.OriginShopId,
                    ackedOrigin.OriginShopCode,
                    official.ShopId,
                    official.ShopCode)))
                {
                    throw new InvalidOperationException("Reversal original ACK belongs to a different official shop.");
                }
            }

            var reversalLines = (lines ?? Array.Empty<SaleLine>()).ToArray();
            if (reversalLines.Length == 0 || reversalLines.Any(line =>
                line == null ||
                line.Quantity <= 0 ||
                line.UnitPrice < 0 ||
                DiscountKeys.IsEconomicAdjustment(line.Barcode) ||
                !line.RelatedOriginalLineId.HasValue ||
                line.RelatedOriginalLineId.Value <= 0))
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
            }

            var originalLineIds = reversalLines
                .Select(line => line.RelatedOriginalLineId.Value)
                .Distinct()
                .ToArray();
            var originalLines = (await conn.QueryAsync<BoundaryOriginalLineRow>(@"
SELECT
  original.id AS OriginalLineId,
  original.quantity AS SoldQty,
  original.unitPrice AS UnitPrice,
  original.barcode AS Barcode,
  COALESCE(SUM(CASE WHEN reversal_sale.id IS NOT NULL THEN ABS(reversal.quantity) ELSE 0 END), 0) AS ReversedQty
FROM sale_lines original
LEFT JOIN sale_lines reversal ON reversal.related_original_line_id = original.id
LEFT JOIN sales reversal_sale ON reversal_sale.id = reversal.saleId
  AND reversal_sale.related_sale_id = @originalSaleId
  AND reversal_sale.kind IN (@kindRefund, @kindVoid)
WHERE original.saleId = @originalSaleId
  AND original.id IN @originalLineIds
GROUP BY original.id, original.quantity, original.unitPrice, original.barcode;",
                    new
                    {
                        originalSaleId = sale.RelatedSaleId.Value,
                        originalLineIds,
                        kindRefund = (int)SaleKind.Refund,
                        kindVoid = (int)SaleKind.Void
                    },
                    tx).ConfigureAwait(false)).ToArray();
            if (originalLines.Length != originalLineIds.Length)
            {
                throw new InvalidOperationException("Reversal lines do not belong to the original sale.");
            }

            var originalById = originalLines.ToDictionary(line => line.OriginalLineId);
            foreach (var group in reversalLines.GroupBy(line => line.RelatedOriginalLineId.Value))
            {
                var source = originalById[group.Key];
                long requestedQty;
                try
                {
                    requestedQty = group.Aggregate(
                        0L,
                        (total, line) => checked(total + (long)line.Quantity));
                }
                catch (OverflowException)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
                }

                if (DiscountKeys.IsEconomicAdjustment(source.Barcode) ||
                    group.Any(line => line.UnitPrice != source.UnitPrice) ||
                    requestedQty > source.SoldQty - source.ReversedQty)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
                }
            }

            var snapshot = await PosReversalEconomicsReader
                .LoadAsync(conn, tx, sale.RelatedSaleId.Value, null)
                .ConfigureAwait(false);
            var economics = ReversalEconomicsPolicy.Calculate(
                snapshot,
                ReversalEconomicsPolicy.CalculateItemGross(reversalLines));
            long paidTotal;
            try
            {
                paidTotal = checked(sale.PaidCash + sale.PaidCard);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
            }

            if (sale.Total != economics.NetClp ||
                sale.PaidCash > 0 ||
                sale.PaidCard > 0 ||
                sale.Change != 0 ||
                paidTotal != sale.Total)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
            }
        }

        internal async Task MarkSaleVoidedAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long originalSaleId,
            long refundSaleId,
            long nowMs)
        {
            await conn.ExecuteAsync(
                @"UPDATE sales
                  SET voided_by_sale_id = @refundSaleId,
                      voided_at = @nowMs
                  WHERE id = @originalSaleId",
                new { originalSaleId, refundSaleId, nowMs }, tx).ConfigureAwait(false);
        }

        private static ReversalDependencyDecision Dependency(
            ReversalDependencyState state,
            string code)
        {
            return new ReversalDependencyDecision(state, code);
        }

        private static bool IsReversalEconomicsCode(string code)
        {
            return string.Equals(code, ReversalEconomicsPolicy.InvalidOriginalCode, StringComparison.Ordinal) ||
                string.Equals(code, ReversalEconomicsPolicy.InvalidHistoryCode, StringComparison.Ordinal) ||
                string.Equals(code, ReversalEconomicsPolicy.PriorSyncUnresolvedCode, StringComparison.Ordinal) ||
                string.Equals(code, ReversalEconomicsPolicy.MismatchCode, StringComparison.Ordinal);
        }

        internal sealed class ReversalDependencyRow
        {
            public int Kind { get; set; }
            public long? RelatedSaleId { get; set; }
            public int? OriginalKind { get; set; }
            public string OriginalOutboxStatus { get; set; }
            public string OriginalOriginShopId { get; set; }
            public string OriginalOriginShopCode { get; set; }
            public string CurrentOriginShopId { get; set; }
            public string CurrentOriginShopCode { get; set; }
            public string OfficialShopId { get; set; }
            public string OfficialShopCode { get; set; }
            public long PriorReversalNotAcked { get; set; }
            public long PriorReversalPermanent { get; set; }
        }

        internal sealed class PersistedReversalRow
        {
            public int Kind { get; set; }
            public long? RelatedSaleId { get; set; }
            public long SaleTotal { get; set; }
        }

        internal sealed class BoundaryOriginalLineRow
        {
            public string Barcode { get; set; }
            public long OriginalLineId { get; set; }
            public long ReversedQty { get; set; }
            public long SoldQty { get; set; }
            public long UnitPrice { get; set; }
        }

        internal sealed class OriginalAckBindingRow
        {
            public string Status { get; set; }
            public string OriginShopId { get; set; }
            public string OriginShopCode { get; set; }
        }
    }
}
