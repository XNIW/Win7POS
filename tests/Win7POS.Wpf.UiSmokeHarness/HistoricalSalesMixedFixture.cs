using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Core.Reports;
using Win7POS.Data.Online;
using Win7POS.Data.Reports;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>
    /// Synthetic source records for F-MIXED-v1 and its small coverage variant.
    /// Setup is deliberately outside the measured cold ownership refresh. No
    /// ownership projection is inserted here: the production verifier must derive
    /// N1 from the canonical outbox payload, its hash and the persisted source.
    /// </summary>
    internal static class HistoricalSalesMixedFixture
    {
        internal const string NativeVisibleA = "NATIVE-VISIBLE-A";
        internal const string UnknownU = "EXCLUDED-UNKNOWN-U";
        internal const string EvidenceReference = "synthetic-mixed-fixture-v1; fixed UTC offset 0; no real shop time evidence";
        private const long ReceiptClp = 1000;
        private static readonly IReadOnlyDictionary<long, string> NoRemoteProducts = new Dictionary<long, string>();
        private static readonly DateTimeOffset PositiveStart = new DateTimeOffset(2000, 1, 2, 11, 12, 13, TimeSpan.Zero);
        private static readonly DateTimeOffset ReversalStart = new DateTimeOffset(2000, 1, 3, 11, 12, 13, TimeSpan.Zero);

        internal static SalesReportTimePolicy CreatePolicy()
        {
            // Two contiguous frozen segments exercise interval composition without
            // consulting the host time zone. Both have the manifest's offset 0.
            var from = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var split = new DateTimeOffset(2000, 1, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(2002, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            return new SalesReportTimePolicy("synthetic-mixed-clock", "synthetic-fixed-offset-v1", EvidenceReference,
                new[] { new SalesReportUtcSegment(from, split, 0), new SalesReportUtcSegment(split, to, 0) });
        }

        /// <summary>
        /// Inserts positiveCount card receipts and reversalCount complete refunds
        /// of distinct originals from this same call. F-MIXED uses (9000,1000):
        /// 10000 native movements, balance 8000000 CLP, POS average 1000 CLP.
        /// Call the bounded production ownership refresher afterwards, including
        /// that refresh in the cold-proof measurement rather than this setup.
        /// </summary>
        internal static Task SeedAsync(HistoricalSalesFoundationRunner.Fixture fixture, int positiveCount,
            int reversalCount, CancellationToken cancellationToken)
        {
            if (positiveCount < 0 || reversalCount < 0 || reversalCount > positiveCount ||
                (long)positiveCount + reversalCount > 10000) throw new ArgumentOutOfRangeException(nameof(positiveCount));
            return SeedCoreAsync(fixture, positiveCount, reversalCount, null, cancellationToken);
        }

        /// <summary>Persists a valid native receipt with no outbox tenant evidence; its derived state is Unknown.</summary>
        internal static Task SeedUnattributedAsync(HistoricalSalesFoundationRunner.Fixture fixture, long amount = 7000,
            CancellationToken cancellationToken = default)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            return SeedCoreAsync(fixture, 0, 0, amount, cancellationToken);
        }

        private static async Task SeedCoreAsync(HistoricalSalesFoundationRunner.Fixture fixture, int positiveCount,
            int reversalCount, long? unattributedAmount, CancellationToken cancellationToken)
        {
            if (fixture == null || fixture.Factory == null || fixture.Operator?.CurrentUser == null ||
                string.IsNullOrWhiteSpace(fixture.ShopId) || string.IsNullOrWhiteSpace(fixture.ShopCode))
                throw new ArgumentException("An initialized synthetic historical-sales fixture is required.", nameof(fixture));
            cancellationToken.ThrowIfCancellationRequested();
            if (positiveCount == 0 && reversalCount == 0 && !unattributedAmount.HasValue) return;
            var coordinator = SalesReportSnapshotCoordinator.ForDatabase(fixture.Factory.DbPath);
            using (await coordinator.EnterPublicationAsync(cancellationToken).ConfigureAwait(false))
            using (var connection = await fixture.Factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var transaction = connection.BeginTransaction())
            using (var insert = CreateInsert(connection, transaction))
            {
                var firstSaleId = checked(await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COALESCE(MAX(id),0) FROM sales", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) + 1);
                var firstLineId = checked(await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COALESCE(MAX(id),0) FROM sale_lines", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false) + 1);
                var totalCount = unattributedAmount.HasValue ? 1 : checked(positiveCount + reversalCount);
                for (var index = 0; index < totalCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reversal = !unattributedAmount.HasValue && index >= positiveCount;
                    var originalIndex = index - positiveCount;
                    var amount = unattributedAmount ?? (reversal ? -ReceiptClp : ReceiptClp);
                    var id = checked(firstSaleId + index);
                    var lineId = checked(firstLineId + index);
                    var clock = reversal ? ReversalStart.AddSeconds(originalIndex) : PositiveStart.AddSeconds(index);
                    var reference = unattributedAmount.HasValue ? UnknownU :
                        id == 1 && !reversal ? NativeVisibleA : "SYNTHETIC-MIXED-" + id.ToString("D8", CultureInfo.InvariantCulture);
                    var sale = new Sale
                    {
                        Id = id, ClientSaleId = "synthetic-mixed-native-" + id.ToString(CultureInfo.InvariantCulture),
                        Code = reference, CreatedAt = clock.ToUnixTimeMilliseconds(), Kind = (int)(reversal ? SaleKind.Refund : SaleKind.Sale),
                        RelatedSaleId = reversal ? (long?)checked(firstSaleId + originalIndex) : null,
                        Reason = reversal ? "Synthetic complete refund" : null,
                        Total = amount, PaidCash = 0, PaidCard = amount, Change = 0,
                        OperatorId = fixture.Operator.CurrentUser.Id
                    };
                    var line = new SaleLine
                    {
                        Id = lineId, SaleId = id, Barcode = "SYNTHETIC-MIXED-ITEM", Name = "Synthetic card receipt",
                        Quantity = 1, UnitPrice = Math.Abs(amount), LineTotal = amount,
                        RelatedOriginalLineId = reversal ? (long?)checked(firstLineId + originalIndex) : null
                    };
                    var item = new SalesSyncOutboxItem
                    {
                        SaleId = id, ClientSaleId = sale.ClientSaleId,
                        IdempotencyKey = "synthetic-mixed-operation-" + id.ToString(CultureInfo.InvariantCulture),
                        SchemaVersion = PosOnlineContract.SalesSchemaVersion, OperationType = reversal ? "refund" : "sale",
                        OriginShopId = fixture.ShopId, OriginShopCode = fixture.ShopCode
                    };
                    if (!unattributedAmount.HasValue)
                    {
                        var economics = reversal ? ReversalEconomicsPolicy.Calculate(new ReversalEconomicsSnapshot
                        { OriginalGrossClp = ReceiptClp, OriginalNetClp = ReceiptClp }, ReceiptClp) : null;
                        var request = PosSalesSyncRequestBuilder.BuildCanonical(item, sale, new[] { line }, NoRemoteProducts, economics);
                        // Freeze the business-date field to the explicit fixture
                        // clock as well; BuildCanonical's legacy host-zone field
                        // must not make this synthetic evidence machine-dependent.
                        request.Sales[0].BusinessDate = clock.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        item.ClientBatchId = request.Batch.ClientBatchId;
                        item.PayloadJson = PosSalesSyncRequestBuilder.SerializeCanonical(request);
                        item.PayloadHash = PosSalesSyncRequestBuilder.Sha256Hex(item.PayloadJson);
                    }
                    Set(insert, "$id", id); Set(insert, "$lineId", lineId); Set(insert, "$client", sale.ClientSaleId);
                    Set(insert, "$code", sale.Code); Set(insert, "$time", sale.CreatedAt); Set(insert, "$kind", sale.Kind);
                    Set(insert, "$original", sale.RelatedSaleId); Set(insert, "$originalLine", line.RelatedOriginalLineId);
                    Set(insert, "$reason", sale.Reason); Set(insert, "$amount", amount); Set(insert, "$unit", line.UnitPrice);
                    Set(insert, "$operator", sale.OperatorId); Set(insert, "$barcode", line.Barcode); Set(insert, "$name", line.Name);
                    Set(insert, "$proof", unattributedAmount.HasValue ? 0 : 1); Set(insert, "$batch", item.ClientBatchId);
                    Set(insert, "$key", item.IdempotencyKey); Set(insert, "$schema", item.SchemaVersion); Set(insert, "$operation", item.OperationType);
                    Set(insert, "$shop", item.OriginShopId); Set(insert, "$shopCode", item.OriginShopCode);
                    Set(insert, "$payload", item.PayloadJson); Set(insert, "$hash", item.PayloadHash);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                // The fixture bypasses native operational side effects, not the
                // report publication contract. The cold verifier will subsequently
                // publish the genuine per-sale ownership decisions and revisions.
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1",
                    transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                coordinator.CommitAndPublishReportRevision(connection, transaction, transaction.Commit, fixture.Factory.AuthorityStamp);
            }
        }

        private static SqliteCommand CreateInsert(SqliteConnection connection, SqliteTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO sales(id,client_sale_id,code,createdAt,kind,related_sale_id,reason,total,paidCash,paidCard,change,operator_id)
VALUES($id,$client,$code,$time,$kind,$original,$reason,$amount,0,$amount,0,$operator);
INSERT INTO sale_lines(id,saleId,productId,barcode,name,quantity,unitPrice,lineTotal,related_original_line_id)
VALUES($lineId,$id,NULL,$barcode,$name,1,$unit,$amount,$originalLine);
INSERT INTO sales_sync_outbox(sale_id,client_sale_id,client_batch_id,idempotency_key,schema_version,operation_type,origin_shop_id,origin_shop_code,payload_json,payload_hash,status,created_at,updated_at)
SELECT $id,$client,$batch,$key,$schema,$operation,$shop,$shopCode,$payload,$hash,'pending',$time,$time WHERE $proof=1;";
            foreach (var name in new[] { "$id", "$lineId", "$time", "$kind", "$original", "$originalLine", "$amount", "$unit", "$operator", "$proof" })
                command.Parameters.Add(name, SqliteType.Integer);
            foreach (var name in new[] { "$client", "$code", "$reason", "$barcode", "$name", "$batch", "$key", "$schema", "$operation", "$shop", "$shopCode", "$payload", "$hash" })
                command.Parameters.Add(name, SqliteType.Text);
            command.Prepare();
            return command;
        }

        private static void Set(SqliteCommand command, string name, object value) => command.Parameters[name].Value = value ?? DBNull.Value;
    }
}
