using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Data.Online;
using Win7POS.Data.Reports;
using Win7POS.Core.Reports;

namespace Win7POS.Data.Repositories
{
    public sealed class NativeSalesOwnershipRepository
    {
        private const int MaxPayloadCharacters = 262144;
        public static readonly string PolicyHash = PosSalesSyncRequestBuilder.Sha256Hex("win7pos-native-ownership-v2:canonical-hash;single-outbox;identity;CLP;date;lines;payments;snapshot-code;void-marker-fingerprint;no-current-shop-fallback");
        private readonly SqliteConnectionFactory _factory;
        private readonly SalesReportSnapshotCoordinator _coordinator;
        private readonly Func<SalesReportAuthorityStamp> _authority;
        public NativeSalesOwnershipRepository(SqliteConnectionFactory factory, SalesReportSnapshotCoordinator coordinator, Func<SalesReportAuthorityStamp> authority)
        { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator)); _authority = authority ?? throw new ArgumentNullException(nameof(authority)); }

        /// <summary>Refreshes one bounded private batch. False means another batch is necessary; no excluded totals are returned.</summary>
        public async Task<bool> RefreshRangeAsync(long fromUtcMs, long toUtcMs, int batchSize, CancellationToken cancellationToken)
        {
            if (fromUtcMs >= toUtcMs || batchSize < 1 || batchSize > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
            var snapshots = new List<SourceSnapshot>();
            using (var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction(deferred: true))
            {
                var ids = (await connection.QueryAsync<long>(new CommandDefinition(@"SELECT s.id FROM sales s
LEFT JOIN sales_report_native_ownership p ON p.sale_id=s.id
WHERE s.createdAt>=@fromUtcMs AND s.createdAt<@toUtcMs AND (p.sale_id IS NULL OR p.state='Dirty' OR p.policy_hash<>@policy)
ORDER BY s.id LIMIT @batchSize", new { fromUtcMs, toUtcMs, batchSize, policy = PolicyHash }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
                foreach (var id in ids) { cancellationToken.ThrowIfCancellationRequested(); snapshots.Add(await CaptureAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)); }
                transaction.Commit();
            }
            var verified = snapshots.Select(snapshot => new { Snapshot = snapshot, Decision = Verify(snapshot) }).ToArray();
            using (var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var item in verified)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = await CaptureAsync(connection, transaction, item.Snapshot.Source.Id, cancellationToken).ConfigureAwait(false);
                    if (current.Fingerprint != item.Snapshot.Fingerprint) continue;
                    await SaveAsync(connection, transaction, current.Source.Id, current.Fingerprint, item.Decision, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var authority = _authority();
                // Match the native/import writer order: SQLite transaction first, publication gate only for commit.
                using (await _coordinator.EnterPublicationAsync(cancellationToken).ConfigureAwait(false))
                    _coordinator.CommitAndPublishReportRevision(connection, transaction, () => transaction.Commit(), authority);
            }
            using (var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
                return !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"SELECT EXISTS(SELECT 1 FROM sales s LEFT JOIN sales_report_native_ownership p ON p.sale_id=s.id
WHERE s.createdAt>=@fromUtcMs AND s.createdAt<@toUtcMs AND (p.sale_id IS NULL OR p.state='Dirty' OR p.policy_hash<>@policy))", new { fromUtcMs, toUtcMs, policy = PolicyHash }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        /// <summary>For the existing native writer: source and proof share its transaction and final commit guard.</summary>
        public static async Task RefreshForSaleInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, long saleId, CancellationToken cancellationToken)
        {
            if (transaction == null || transaction.Connection != connection) throw new ArgumentException("The native transaction is required.");
            var snapshot = await CaptureAsync(connection, transaction, saleId, cancellationToken).ConfigureAwait(false);
            await SaveAsync(connection, transaction, saleId, snapshot.Fingerprint, Verify(snapshot), cancellationToken).ConfigureAwait(false);
        }

        private static Task<int> SaveAsync(SqliteConnection connection, SqliteTransaction transaction, long id, string fingerprint, Decision decision, CancellationToken cancellationToken)
        {
            return connection.ExecuteAsync(new CommandDefinition(@"INSERT INTO sales_report_native_ownership(sale_id,state,shop_id,source_fingerprint,policy_hash)
VALUES(@id,@state,@shop,@fingerprint,@policy)
ON CONFLICT(sale_id) DO UPDATE SET state=excluded.state,shop_id=excluded.shop_id,source_fingerprint=excluded.source_fingerprint,policy_hash=excluded.policy_hash
WHERE state IS NOT excluded.state OR shop_id IS NOT excluded.shop_id OR source_fingerprint IS NOT excluded.source_fingerprint OR policy_hash IS NOT excluded.policy_hash",
                new { id, state = decision.State, shop = decision.ShopId, fingerprint, policy = PolicyHash }, transaction, cancellationToken: cancellationToken));
        }

        private static async Task<SourceSnapshot> CaptureAsync(SqliteConnection connection, SqliteTransaction transaction, long id, CancellationToken cancellationToken)
        {
            var source = await connection.QuerySingleAsync<SourceRow>(new CommandDefinition(@"SELECT s.id AS Id,s.client_sale_id AS ClientSaleId,s.code AS Code,s.createdAt AS CreatedAt,s.kind AS Kind,
s.total AS Total,s.paidCash AS PaidCash,s.paidCard AS PaidCard,s.change AS Change,s.related_sale_id AS RelatedSaleId,
s.voided_by_sale_id AS VoidedBySaleId,s.voided_at AS VoidedAt,
CASE WHEN length(s.receipt_shop_snapshot)<=16384 THEN s.receipt_shop_snapshot ELSE NULL END AS ReceiptSnapshot,
length(s.receipt_shop_snapshot) AS ReceiptLength,
(SELECT COUNT(*) FROM sales_sync_outbox x WHERE x.sale_id=s.id) AS OutboxCount,
(SELECT COUNT(*) FROM sale_lines l WHERE l.saleId=s.id) AS LineCount,
o.client_sale_id AS OutboxClientSaleId,o.client_batch_id AS ClientBatchId,o.idempotency_key AS IdempotencyKey,
o.origin_shop_id AS OriginShopId,o.origin_shop_code AS OriginShopCode,o.schema_version AS SchemaVersion,o.operation_type AS OperationType,
CASE WHEN length(o.payload_json)<=262144 THEN o.payload_json ELSE NULL END AS PayloadJson,
length(o.payload_json) AS PayloadLength,o.payload_hash AS PayloadHash
FROM sales s LEFT JOIN sales_sync_outbox o ON o.id=(SELECT x.id FROM sales_sync_outbox x WHERE x.sale_id=s.id ORDER BY x.id LIMIT 1)
WHERE s.id=@id", new { id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var lines = source.LineCount <= SalesReceiptContentPolicy.MaxSaleLines
                ? (await connection.QueryAsync<SaleLine>(new CommandDefinition(@"SELECT id,saleId,productId,barcode,name,quantity,unitPrice,lineTotal,related_original_line_id AS RelatedOriginalLineId
FROM sale_lines WHERE saleId=@id ORDER BY id LIMIT 513", new { id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray()
                : Array.Empty<SaleLine>();
            var body = new StringBuilder();
            foreach (var value in new object[] { source.Id, source.ClientSaleId, source.Code, source.CreatedAt, source.Kind, source.Total, source.PaidCash, source.PaidCard, source.Change, source.RelatedSaleId, source.VoidedBySaleId, source.VoidedAt,
                source.ReceiptSnapshot, source.ReceiptLength, source.OutboxCount, source.LineCount, source.OutboxClientSaleId, source.ClientBatchId, source.IdempotencyKey, source.OriginShopId, source.OriginShopCode,
                source.SchemaVersion, source.OperationType, source.PayloadJson, source.PayloadLength, source.PayloadHash }) Append(body, value);
            foreach (var line in lines) foreach (var value in new object[] { line.Id, line.ProductId, line.Barcode, line.Name, line.Quantity, line.UnitPrice, line.LineTotal, line.RelatedOriginalLineId }) Append(body, value);
            return new SourceSnapshot { Source = source, Lines = lines, Fingerprint = PosSalesSyncRequestBuilder.Sha256Hex(body.ToString()) };
        }
        private static void Append(StringBuilder body, object value) { var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty; body.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text); }

        private static Decision Verify(SourceSnapshot snapshot)
        {
            var source = snapshot.Source;
            if (source.OutboxCount == 0) return new Decision("Unknown", null);
            if (source.OutboxCount != 1 || source.LineCount == 0 || source.LineCount > SalesReceiptContentPolicy.MaxSaleLines || source.ReceiptLength > ReceiptDocumentPolicy.MaxSnapshotJsonCharacters ||
                source.PayloadLength <= 0 || source.PayloadLength > MaxPayloadCharacters || string.IsNullOrWhiteSpace(source.PayloadJson)) return new Decision("Conflict", null);
            try
            {
                if (!string.Equals(PosSalesSyncRequestBuilder.Sha256Hex(source.PayloadJson), source.PayloadHash, StringComparison.OrdinalIgnoreCase)) return new Decision("Conflict", null);
                var request = PosSalesSyncRequestBuilder.DeserializeCanonical(source.PayloadJson);
                var sale = request?.Sales != null && request.Sales.Length == 1 ? request.Sales[0] : null;
                var expectedKind = source.Kind == 0 ? "sale" : source.Kind == 1 ? "refund" : source.Kind == 2 ? "void" : string.Empty;
                var originCode = OutboxShopBinding.NormalizeCode(source.OriginShopCode);
                if (sale == null || sale.Amounts == null || request.Batch == null || originCode.Length == 0 ||
                    source.SchemaVersion != PosOnlineContract.SalesSchemaVersion || request.SchemaVersion != source.SchemaVersion || source.OperationType != expectedKind || sale.Kind != expectedKind ||
                    string.IsNullOrWhiteSpace(source.ClientSaleId) || source.ClientSaleId != source.OutboxClientSaleId || sale.ClientSaleId != source.ClientSaleId ||
                    sale.IdempotencyKey != source.IdempotencyKey || request.Batch.ClientBatchId != source.ClientBatchId || request.Batch.IdempotencyKey != source.ClientBatchId ||
                    OutboxShopBinding.NormalizeCode(request.ShopCode) != originCode || sale.Currency != "CLP" || sale.SaleNumber != source.Code || sale.Amounts.NetClp != source.Total ||
                    sale.Amounts.ChangeClp != source.Change || sale.Amounts.PaidClp != checked(source.PaidCash + source.PaidCard) ||
                    !DateTimeOffset.TryParse(sale.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurred) || occurred.ToUnixTimeMilliseconds() != source.CreatedAt ||
                    occurred.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond != 0 || sale.Lines == null || sale.Lines.Length != snapshot.Lines.Length || sale.Payments == null)
                    return new Decision("Conflict", null);
                if (source.Kind != 0 && sale.ClientOriginalSaleId != "win7pos-sale-" + source.RelatedSaleId?.ToString(CultureInfo.InvariantCulture)) return new Decision("Conflict", null);
                long cash = 0, card = 0, change = 0;
                foreach (var payment in sale.Payments)
                {
                    if (payment == null) return new Decision("Conflict", null);
                    if (payment.Method == PosOnlineContract.PaymentCash) { cash = checked(cash + payment.AmountClp); change = checked(change + payment.ChangeClp); }
                    else if (payment.Method == PosOnlineContract.PaymentCard) card = checked(card + payment.AmountClp);
                    else return new Decision("Conflict", null);
                }
                if (cash != source.PaidCash || card != source.PaidCard || change != source.Change) return new Decision("Conflict", null);
                for (var index = 0; index < snapshot.Lines.Length; index++)
                {
                    var local = snapshot.Lines[index]; var remote = sale.Lines[index];
                    var amount = source.Kind != 0 || DiscountKeys.IsDiscount(local.Barcode) ? checked(-Math.Abs(local.LineTotal)) : Math.Abs(local.LineTotal);
                    if (remote == null || remote.ClientLineId != "line-" + local.Id.ToString(CultureInfo.InvariantCulture) || remote.LinePosition != index + 1 || remote.AmountClp != amount ||
                        remote.Quantity != Math.Abs(local.Quantity) || remote.UnitAmountClp != Math.Abs(local.UnitPrice) ||
                        remote.ClientOriginalLineId != (source.Kind == 0 ? null : "line-" + local.RelatedOriginalLineId?.ToString(CultureInfo.InvariantCulture))) return new Decision("Conflict", null);
                }
                if (!string.IsNullOrWhiteSpace(source.ReceiptSnapshot))
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source.ReceiptSnapshot));
                    var receipt = new DataContractJsonSerializer(typeof(ReceiptShopInfo)).ReadObject(stream) as ReceiptShopInfo;
                    if (receipt == null || (!string.IsNullOrWhiteSpace(receipt.ShopCode) && OutboxShopBinding.NormalizeCode(receipt.ShopCode) != originCode)) return new Decision("Conflict", null);
                }
                return string.IsNullOrWhiteSpace(source.OriginShopId) ? new Decision("CodeOnly", null) : new Decision("Verified", source.OriginShopId.Trim());
            }
            catch (Exception exception) when (exception is OverflowException || exception is ArgumentException || exception is System.Runtime.Serialization.SerializationException || exception is System.Xml.XmlException)
            { return new Decision("Conflict", null); }
        }

        private sealed class Decision { internal Decision(string state, string shop) { State = state; ShopId = shop; } internal string State { get; } internal string ShopId { get; } }
        private sealed class SourceSnapshot { internal SourceRow Source { get; set; } internal SaleLine[] Lines { get; set; } internal string Fingerprint { get; set; } }
        private sealed class SourceRow
        {
            public long Id { get; set; } public string ClientSaleId { get; set; } public string Code { get; set; } public long CreatedAt { get; set; } public int Kind { get; set; }
            public long Total { get; set; } public long PaidCash { get; set; } public long PaidCard { get; set; } public long Change { get; set; } public long? RelatedSaleId { get; set; }
            public long? VoidedBySaleId { get; set; } public long? VoidedAt { get; set; }
            public string ReceiptSnapshot { get; set; } public long? ReceiptLength { get; set; } public long OutboxCount { get; set; } public long LineCount { get; set; }
            public string OutboxClientSaleId { get; set; } public string ClientBatchId { get; set; } public string IdempotencyKey { get; set; } public string OriginShopId { get; set; } public string OriginShopCode { get; set; }
            public string SchemaVersion { get; set; } public string OperationType { get; set; } public string PayloadJson { get; set; } public long? PayloadLength { get; set; } public string PayloadHash { get; set; }
        }
    }
}
