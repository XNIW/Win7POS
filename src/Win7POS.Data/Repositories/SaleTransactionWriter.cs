using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns complete sale and reversal persistence transactions. The supplied
    /// collaborators participate in the same caller-owned SQLite transaction;
    /// this writer never reconstructs the repository facade.
    /// </summary>
    internal sealed class SaleTransactionWriter
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly SaleStockMovementWriter _stockMovementWriter;
        private readonly SaleReversalWriter _reversalWriter;
        private readonly SalesSyncOutboxRepository _salesSyncOutbox;

        internal SaleTransactionWriter(
            SqliteConnectionFactory factory,
            SaleStockMovementWriter stockMovementWriter,
            SaleReversalWriter reversalWriter,
            SalesSyncOutboxRepository salesSyncOutbox)
        {
            _factory = factory;
            _stockMovementWriter = stockMovementWriter;
            _reversalWriter = reversalWriter;
            _salesSyncOutbox = salesSyncOutbox;
        }

        internal async Task<long> InsertSaleAsync(
            Sale sale,
            IReadOnlyList<SaleLine> lines,
            SaleAuthorizationCommitGuard authorizationCommitGuard = null)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            SalesReceiptContentPolicy.EnsureValid(sale, lines);
            ReceiptDocumentPolicy.EnsureValidSnapshotJson(sale.ReceiptShopSnapshotJson);
            ValidateAuthorizationBinding(sale, authorizationCommitGuard);
            using var conn = await _factory.OpenAsync().ConfigureAwait(false);
            using var tx = conn.BeginTransaction();
            var transactionCommitted = false;
            Action commitTransaction = () =>
            {
                tx.Commit();
                transactionCommitted = true;
            };

            try
            {
                if (sale.Kind == 0)
                    sale.Kind = (int)SaleKind.Sale;

                authorizationCommitGuard?.DemandStillValid();
                var replaySaleId = await TryResolveExactSaleReplayAsync(
                        conn,
                        tx,
                        sale,
                        lines)
                    .ConfigureAwait(false);
                if (replaySaleId.HasValue)
                {
                    if (authorizationCommitGuard == null)
                    {
                        CommitExactSaleReplay(commitTransaction);
                    }
                    else
                    {
                        authorizationCommitGuard.CommitIfStillValid(
                            () => CommitExactSaleReplay(
                                commitTransaction));
                    }
                    return replaySaleId.Value;
                }

                if (sale.Kind == (int)SaleKind.Sale)
                {
                    await CatalogShopStateRepository
                        .RequireSaleSafeForOrdinarySaleAsync(conn, tx)
                        .ConfigureAwait(false);
                }

                await _reversalWriter.ValidateReversalBoundaryAsync(conn, tx, sale, lines)
                    .ConfigureAwait(false);

                authorizationCommitGuard?.DemandStillValid();
                var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, voided_by_sale_id, voided_at, reason, total, paidCash, paidCard, change, operator_id, receipt_shop_snapshot)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @VoidedBySaleId, @VoidedAt, @Reason, @Total, @PaidCash, @PaidCard, @Change, @OperatorId, @ReceiptShopSnapshotJson);
SELECT last_insert_rowid();", sale, tx).ConfigureAwait(false);
                sale.Id = saleId;
                sale.ClientSaleId = await EnsureClientSaleIdAsync(conn, tx, saleId).ConfigureAwait(false);

                foreach (var l in lines.Select((line, index) => new { line, index }))
                {
                    l.line.SaleId = saleId;
                    l.line.LineTotal = l.line.Quantity * l.line.UnitPrice;
                    l.line.Id = await InsertSaleLineAsync(conn, tx, l.line).ConfigureAwait(false);
                }

                await ApplyLocalStockMovementsAsync(conn, tx, sale, lines).ConfigureAwait(false);
                await EnqueueSalesSyncOutboxAsync(conn, tx, saleId, sale.ClientSaleId).ConfigureAwait(false);

                if (authorizationCommitGuard == null)
                {
                    commitTransaction();
                }
                else
                {
                    authorizationCommitGuard.CommitIfStillValid(
                        commitTransaction);
                }
                return saleId;
            }
            catch
            {
                if (!transactionCommitted)
                {
                    try
                    {
                        tx.Rollback();
                    }
                    catch
                    {
                        // Preserve the original failure. A COMMIT can be
                        // durable even when SQLite reports an ambiguous error;
                        // the exact identity is retried idempotently.
                    }
                }
                throw;
            }
        }

        private static void CommitExactSaleReplay(
            Action commit)
        {
            commit();
        }

        private static async Task<long?> TryResolveExactSaleReplayAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Sale sale,
            IReadOnlyList<SaleLine> lines)
        {
            var existing = await connection.QuerySingleOrDefaultAsync<Sale>(@"
SELECT
  id AS Id,
  client_sale_id AS ClientSaleId,
  code AS Code,
  createdAt AS CreatedAt,
  kind AS Kind,
  related_sale_id AS RelatedSaleId,
  voided_by_sale_id AS VoidedBySaleId,
  voided_at AS VoidedAt,
  reason AS Reason,
  total AS Total,
  paidCash AS PaidCash,
  paidCard AS PaidCard,
  change AS Change,
  operator_id AS OperatorId,
  receipt_shop_snapshot AS ReceiptShopSnapshotJson
FROM sales
WHERE code = @Code
LIMIT 1;",
                    new { sale.Code },
                    transaction)
                .ConfigureAwait(false);
            if (existing == null)
                return null;

            var existingLines = (await connection.QueryAsync<SaleLine>(@"
SELECT
  id AS Id,
  saleId AS SaleId,
  productId AS ProductId,
  barcode AS Barcode,
  name AS Name,
  quantity AS Quantity,
  unitPrice AS UnitPrice,
  lineTotal AS LineTotal,
  related_original_line_id AS RelatedOriginalLineId
FROM sale_lines
WHERE saleId = @saleId
ORDER BY id;",
                    new { saleId = existing.Id },
                    transaction)
                .ConfigureAwait(false)).AsList();
            var outboxCount = await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(*)
FROM sales_sync_outbox
WHERE sale_id = @saleId
  AND client_sale_id = @clientSaleId;",
                    new
                    {
                        saleId = existing.Id,
                        clientSaleId = existing.ClientSaleId
                    },
                    transaction)
                .ConfigureAwait(false);

            if (!IsExactSaleReplay(existing, sale) ||
                !IsExactLineReplay(existingLines, lines) ||
                string.IsNullOrWhiteSpace(existing.ClientSaleId) ||
                outboxCount != 1)
            {
                throw new InvalidOperationException(
                    "The sale code already belongs to a different or incomplete sale.");
            }

            sale.Id = existing.Id;
            sale.ClientSaleId = existing.ClientSaleId;
            return existing.Id;
        }

        private static bool IsExactSaleReplay(
            Sale existing,
            Sale candidate)
        {
            return existing != null &&
                candidate != null &&
                string.Equals(
                    existing.Code,
                    candidate.Code,
                    StringComparison.Ordinal) &&
                existing.CreatedAt == candidate.CreatedAt &&
                existing.Kind == candidate.Kind &&
                existing.RelatedSaleId == candidate.RelatedSaleId &&
                existing.VoidedBySaleId == candidate.VoidedBySaleId &&
                existing.VoidedAt == candidate.VoidedAt &&
                string.Equals(
                    existing.Reason,
                    candidate.Reason,
                    StringComparison.Ordinal) &&
                existing.Total == candidate.Total &&
                existing.PaidCash == candidate.PaidCash &&
                existing.PaidCard == candidate.PaidCard &&
                existing.Change == candidate.Change &&
                existing.OperatorId == candidate.OperatorId &&
                string.Equals(
                    existing.ReceiptShopSnapshotJson,
                    candidate.ReceiptShopSnapshotJson,
                    StringComparison.Ordinal);
        }

        private static bool IsExactLineReplay(
            IReadOnlyList<SaleLine> existing,
            IReadOnlyList<SaleLine> candidate)
        {
            if (existing == null ||
                candidate == null ||
                existing.Count != candidate.Count)
            {
                return false;
            }

            for (var index = 0; index < existing.Count; index++)
            {
                var left = existing[index];
                var right = candidate[index];
                if (left == null ||
                    right == null ||
                    left.ProductId != right.ProductId ||
                    !string.Equals(
                        left.Barcode,
                        right.Barcode,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        left.Name,
                        right.Name,
                        StringComparison.Ordinal) ||
                    left.Quantity != right.Quantity ||
                    left.UnitPrice != right.UnitPrice ||
                    left.LineTotal !=
                        right.Quantity * right.UnitPrice ||
                    left.RelatedOriginalLineId !=
                        right.RelatedOriginalLineId)
                {
                    return false;
                }
            }
            return true;
        }

        private static void ValidateAuthorizationBinding(
            Sale sale,
            SaleAuthorizationCommitGuard authorizationCommitGuard)
        {
            if (authorizationCommitGuard == null)
                return;
            if (sale.Kind != 0 &&
                sale.Kind != (int)SaleKind.Sale)
            {
                throw new InvalidOperationException(
                    "The authorization-use token only permits ordinary sales.");
            }
            if (!sale.OperatorId.HasValue ||
                sale.OperatorId.Value != authorizationCommitGuard.OperatorId ||
                authorizationCommitGuard.OperatorId <= 0 ||
                authorizationCommitGuard.AuthorizationEpoch < 0 ||
                string.IsNullOrWhiteSpace(
                    authorizationCommitGuard.GenerationFingerprint) ||
                string.IsNullOrWhiteSpace(
                    authorizationCommitGuard.GenerationId) ||
                (string.IsNullOrWhiteSpace(
                     authorizationCommitGuard.ShopId) &&
                 string.IsNullOrWhiteSpace(
                     authorizationCommitGuard.ShopCode)) ||
                string.IsNullOrWhiteSpace(
                    authorizationCommitGuard.ShopDeviceId) ||
                string.IsNullOrWhiteSpace(
                    authorizationCommitGuard.StaffId) ||
                authorizationCommitGuard.StaffCredentialVersion < 0)
            {
                throw new InvalidOperationException(
                    "The ordinary-sale authorization binding is incomplete.");
            }
        }

        internal async Task MarkPdfPrintedAsync(long saleId)
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync(
                "UPDATE sales SET pdf_printed = 1 WHERE id = @saleId",
                new { saleId }).ConfigureAwait(false);
        }

        internal async Task<long> InsertRefundSaleAsync(
            RefundCreateRequest req,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor)
        {
            if (req == null || req.OriginalSaleId <= 0)
                throw new InvalidOperationException("Refund requires an existing original sale.");
            SalesReceiptContentPolicy.EnsureValidSaleReason(req.Reason);
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var saleId = await InsertRefundSaleAsync(
                    conn,
                    tx,
                    req,
                    totalMinor,
                    paidCashMinor,
                    paidCardMinor,
                    changeMinor).ConfigureAwait(false);
                tx.Commit();
                return saleId;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        internal async Task<long> InsertRefundSaleAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RefundCreateRequest req,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor)
        {
            if (req == null || req.OriginalSaleId <= 0)
                throw new InvalidOperationException("Refund requires an existing original sale.");
            SalesReceiptContentPolicy.EnsureValidSaleReason(req.Reason);
            var originalKind = await conn.ExecuteScalarAsync<int?>(
                "SELECT kind FROM sales WHERE id = @originalSaleId;",
                new { originalSaleId = req.OriginalSaleId },
                tx).ConfigureAwait(false);
            if (originalKind != (int)SaleKind.Sale)
                throw new InvalidOperationException("Refund original sale is missing or incoherent.");

            var sale = new Sale
            {
                Code = "R" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)SaleKind.Refund,
                RelatedSaleId = req?.OriginalSaleId,
                Reason = req?.Reason,
                Total = totalMinor,
                PaidCash = paidCashMinor,
                PaidCard = paidCardMinor,
                Change = changeMinor
            };
            SalesReceiptContentPolicy.EnsureValid(sale, Array.Empty<SaleLine>());

            var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, voided_by_sale_id, voided_at, reason, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @VoidedBySaleId, @VoidedAt, @Reason, @Total, @PaidCash, @PaidCard, @Change, @ReceiptShopSnapshotJson);
SELECT last_insert_rowid();", sale, tx).ConfigureAwait(false);

            return saleId;
        }

        internal async Task<long> InsertRefundOrVoidAsync(
            Sale refundSale,
            IReadOnlyList<SaleLine> refundLines,
            long? originalSaleIdToMarkVoided,
            string auditAction,
            Func<long, string> auditDetailsFactory)
        {
            if (refundSale == null) throw new ArgumentNullException(nameof(refundSale));
            if (refundLines == null) throw new ArgumentNullException(nameof(refundLines));
            SalesReceiptContentPolicy.EnsureValid(refundSale, refundLines);
            ReceiptDocumentPolicy.EnsureValidSnapshotJson(refundSale.ReceiptShopSnapshotJson);

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                await _reversalWriter.ValidateReversalBoundaryAsync(conn, tx, refundSale, refundLines)
                    .ConfigureAwait(false);
                var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, reason, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES(@Code, @CreatedAt, @Kind, @RelatedSaleId, @Reason, @Total, @PaidCash, @PaidCard, @Change, @ReceiptShopSnapshotJson);
SELECT last_insert_rowid();", refundSale, tx).ConfigureAwait(false);

                refundSale.Id = saleId;
                refundSale.ClientSaleId = await EnsureClientSaleIdAsync(conn, tx, saleId).ConfigureAwait(false);

                foreach (var line in refundLines)
                {
                    line.SaleId = saleId;
                }

                await InsertSaleLinesAsync(conn, tx, refundLines).ConfigureAwait(false);
                await ApplyLocalStockMovementsAsync(conn, tx, refundSale, refundLines).ConfigureAwait(false);
                await EnqueueSalesSyncOutboxAsync(conn, tx, saleId, refundSale.ClientSaleId).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(auditAction))
                {
                    await new AuditLogRepository()
                        .AppendAsync(
                            conn,
                            tx,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            auditAction,
                            auditDetailsFactory == null ? string.Empty : auditDetailsFactory(saleId))
                        .ConfigureAwait(false);
                }

                if (originalSaleIdToMarkVoided.HasValue)
                {
                    await _reversalWriter.MarkSaleVoidedAsync(
                        conn,
                        tx,
                        originalSaleIdToMarkVoided.Value,
                        saleId,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
                }

                tx.Commit();
                return saleId;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        internal async Task InsertSaleLinesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<SaleLine> lines)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));
            if (!ReferenceEquals(tx.Connection, conn))
            {
                throw new ArgumentException(
                    "The sale-line transaction must belong to the supplied connection.",
                    nameof(tx));
            }
            SalesReceiptContentPolicy.EnsureValidLines(lines);
            if (lines == null || lines.Count == 0) return;
            foreach (var group in lines.GroupBy(line => line.SaleId))
            {
                var appended = group.ToList();
                var budget = await SaleLineReadRepository
                    .EnsureStoredLineBudgetAsync(conn, tx, group.Key)
                    .ConfigureAwait(false);
                SalesReceiptContentPolicy.EnsureCumulativeLineBudget(
                    budget.LineCount,
                    budget.AggregateNameCharacters,
                    budget.AggregateNameUtf8Bytes,
                    appended);
            }
            foreach (var line in lines)
            {
                line.Id = await InsertSaleLineAsync(conn, tx, line).ConfigureAwait(false);
            }
        }

        internal async Task<string> EnsureClientSaleIdAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId)
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT client_sale_id FROM sales WHERE id = @saleId",
                new { saleId }, tx).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var clientSaleId = BuildClientSaleId(saleId);
            await conn.ExecuteAsync(
                "UPDATE sales SET client_sale_id = @clientSaleId WHERE id = @saleId",
                new { clientSaleId, saleId }, tx).ConfigureAwait(false);
            return clientSaleId;
        }

        internal async Task ApplyLocalStockMovementsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines)
        {
            if (sale == null || lines == null || lines.Count == 0)
            {
                return;
            }

            var clientSaleId = sale.ClientSaleId;
            if (string.IsNullOrWhiteSpace(clientSaleId))
            {
                clientSaleId = await EnsureClientSaleIdAsync(conn, tx, sale.Id).ConfigureAwait(false);
                sale.ClientSaleId = clientSaleId;
            }

            await _stockMovementWriter
                .ApplyAsync(conn, tx, sale, lines, clientSaleId)
                .ConfigureAwait(false);
        }

        internal async Task EnqueueSalesSyncOutboxAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId,
            string clientSaleId)
        {
            var normalizedClientSaleId = string.IsNullOrWhiteSpace(clientSaleId)
                ? BuildClientSaleId(saleId)
                : clientSaleId.Trim();
            await _salesSyncOutbox
                .EnqueueAsync(conn, tx, saleId, normalizedClientSaleId)
                .ConfigureAwait(false);
        }

        private static string BuildClientSaleId(long saleId)
        {
            return "win7pos-sale-" + saleId.ToString(CultureInfo.InvariantCulture);
        }

        private static async Task<long> InsertSaleLineAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            SaleLine line)
        {
            return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id)
VALUES(@SaleId, @ProductId, @Barcode, @Name, @Quantity, @UnitPrice, @LineTotal, @RelatedOriginalLineId);
SELECT last_insert_rowid();", line, tx).ConfigureAwait(false);
        }
    }
}
