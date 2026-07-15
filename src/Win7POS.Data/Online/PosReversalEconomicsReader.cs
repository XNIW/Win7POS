using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;

namespace Win7POS.Data.Online
{
    internal static class PosReversalEconomicsReader
    {
        public static async Task<ReversalEconomicsSnapshot> LoadAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long originalSaleId,
            long? excludeReversalSaleId)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (originalSaleId <= 0)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
            }

            var original = await conn.QuerySingleOrDefaultAsync<OriginalEconomicsRow>(@"
SELECT
  s.kind AS Kind,
  s.total AS SaleTotal,
  o.payload_json AS PayloadJson,
  o.payload_hash AS PayloadHash,
  o.status AS OutboxStatus
FROM sales s
LEFT JOIN sales_sync_outbox o ON o.sale_id = s.id
WHERE s.id = @originalSaleId;",
                new { originalSaleId },
                tx).ConfigureAwait(false);
            var originalRequest = ReadVerifiedRequest(
                original?.PayloadJson,
                original?.PayloadHash,
                ReversalEconomicsPolicy.InvalidOriginalCode);
            if (original == null ||
                original.Kind != (int)SaleKind.Sale ||
                !IsResolvableStatus(original.OutboxStatus))
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
            }

            var originalEconomics = ReadOriginalEconomics(originalRequest, original.SaleTotal);
            var snapshot = new ReversalEconomicsSnapshot
            {
                OriginalGrossClp = originalEconomics.GrossClp,
                OriginalDiscountClp = originalEconomics.DiscountClp,
                OriginalTaxClp = originalEconomics.TaxClp,
                OriginalNetClp = originalEconomics.NetClp
            };

            var priorRows = (await conn.QueryAsync<PriorReversalEconomicsRow>(@"
SELECT
  s.id AS SaleId,
  s.kind AS Kind,
  s.total AS SaleTotal,
  o.payload_json AS PayloadJson,
  o.payload_hash AS PayloadHash,
  o.status AS OutboxStatus
FROM sales s
LEFT JOIN sales_sync_outbox o ON o.sale_id = s.id
WHERE s.related_sale_id = @originalSaleId
  AND s.kind IN (@kindRefund, @kindVoid)
  AND (@excludeReversalSaleId IS NULL OR s.id < @excludeReversalSaleId)
ORDER BY s.id ASC;",
                new
                {
                    originalSaleId,
                    kindRefund = (int)SaleKind.Refund,
                    kindVoid = (int)SaleKind.Void,
                    excludeReversalSaleId
                },
                tx).ConfigureAwait(false)).ToArray();

            foreach (var prior in priorRows)
            {
                if (!IsResolvableStatus(prior.OutboxStatus))
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.PriorSyncUnresolvedCode);
                }

                var priorRequest = ReadVerifiedRequest(
                    prior.PayloadJson,
                    prior.PayloadHash,
                    ReversalEconomicsPolicy.InvalidHistoryCode);
                var priorGross = ReadReversalGross(priorRequest, originalSaleId, prior.Kind);
                var expected = ReversalEconomicsPolicy.Calculate(snapshot, priorGross);
                if (prior.SaleTotal != expected.NetClp ||
                    !PosSalesSyncRequestBuilder.HasExpectedReversalEconomics(priorRequest, expected))
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidHistoryCode);
                }

                try
                {
                    snapshot.PriorGrossClp = checked(snapshot.PriorGrossClp + expected.GrossClp);
                    snapshot.ActualPriorDiscountClp = checked(
                        snapshot.ActualPriorDiscountClp + expected.DiscountClp);
                    snapshot.ActualPriorTaxClp = checked(
                        snapshot.ActualPriorTaxClp + expected.TaxClp);
                }
                catch (OverflowException)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidHistoryCode);
                }
            }

            ReversalEconomicsPolicy.ValidateSnapshot(snapshot);
            return snapshot;
        }

        private static ReversalEconomicsResult ReadOriginalEconomics(
            PosSalesSyncRequest request,
            long persistedSaleTotal)
        {
            var sales = request?.Sales;
            if (sales == null || sales.Length != 1 || sales[0] == null)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
            }

            var sale = sales[0];
            if (!string.Equals(sale.Kind, "sale", StringComparison.Ordinal) ||
                sale.Amounts == null ||
                sale.Lines == null ||
                sale.Lines.Length == 0)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
            }

            try
            {
                var gross = 0L;
                var discount = 0L;
                var tax = 0L;
                foreach (var line in sale.Lines)
                {
                    if (line == null || line.Quantity <= 0 || line.UnitAmountClp < 0)
                    {
                        throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                    }

                    var expectedAmount = checked((long)line.Quantity * line.UnitAmountClp);
                    if (string.Equals(line.LineType, "item", StringComparison.Ordinal))
                    {
                        if (line.AmountClp != expectedAmount)
                        {
                            throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                        }

                        gross = checked(gross + line.AmountClp);
                    }
                    else if (string.Equals(line.LineType, "discount", StringComparison.Ordinal))
                    {
                        if (line.AmountClp != -expectedAmount)
                        {
                            throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                        }

                        discount = checked(discount + Math.Abs(line.AmountClp));
                    }
                    else if (string.Equals(line.LineType, "tax", StringComparison.Ordinal))
                    {
                        if (line.AmountClp != expectedAmount)
                        {
                            throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                        }

                        tax = checked(tax + line.AmountClp);
                    }
                    else
                    {
                        throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                    }
                }

                var net = checked(checked(gross - discount) + tax);
                if (sale.Amounts.GrossClp != gross ||
                    sale.Amounts.DiscountClp != discount ||
                    sale.Amounts.TaxClp != tax ||
                    sale.Amounts.NetClp != net ||
                    persistedSaleTotal != net)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                }

                return new ReversalEconomicsResult
                {
                    GrossClp = gross,
                    DiscountClp = discount,
                    TaxClp = tax,
                    NetClp = net
                };
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
            }
        }

        private static long ReadReversalGross(
            PosSalesSyncRequest request,
            long originalSaleId,
            int kind)
        {
            var sales = request?.Sales;
            if (sales == null || sales.Length != 1 || sales[0] == null)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidHistoryCode);
            }

            var sale = sales[0];
            var expectedKind = kind == (int)SaleKind.Void ? "void" : "refund";
            var expectedOriginalId = "win7pos-sale-" +
                originalSaleId.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(sale.Kind, expectedKind, StringComparison.Ordinal) ||
                !string.Equals(sale.ClientOriginalSaleId, expectedOriginalId, StringComparison.Ordinal) ||
                sale.Lines == null ||
                sale.Lines.Length == 0)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidHistoryCode);
            }

            try
            {
                return sale.Lines.Aggregate(0L, (total, line) =>
                {
                    if (line == null ||
                        !string.Equals(line.LineType, "item", StringComparison.Ordinal) ||
                        line.AmountClp >= 0)
                    {
                        throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidHistoryCode);
                    }

                    return checked(total + Math.Abs(line.AmountClp));
                });
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidHistoryCode);
            }
        }

        private static PosSalesSyncRequest ReadVerifiedRequest(
            string payloadJson,
            string payloadHash,
            string errorCode)
        {
            if (string.IsNullOrWhiteSpace(payloadJson) ||
                string.IsNullOrWhiteSpace(payloadHash) ||
                !string.Equals(
                    PosSalesSyncRequestBuilder.Sha256Hex(payloadJson),
                    payloadHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(errorCode);
            }

            var request = PosSalesSyncRequestBuilder.DeserializeCanonical(payloadJson);
            if (request == null)
            {
                throw new InvalidOperationException(errorCode);
            }

            return request;
        }

        private static bool IsResolvableStatus(string status)
        {
            return string.Equals(status, "acked", StringComparison.Ordinal) ||
                string.Equals(status, "pending", StringComparison.Ordinal) ||
                string.Equals(status, "retry", StringComparison.Ordinal) ||
                string.Equals(status, "in_progress", StringComparison.Ordinal);
        }

        private class OriginalEconomicsRow
        {
            public int Kind { get; set; }
            public long SaleTotal { get; set; }
            public string PayloadJson { get; set; }
            public string PayloadHash { get; set; }
            public string OutboxStatus { get; set; }
        }

        private sealed class PriorReversalEconomicsRow : OriginalEconomicsRow
        {
            public long SaleId { get; set; }
        }
    }
}
