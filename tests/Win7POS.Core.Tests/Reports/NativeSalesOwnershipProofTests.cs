using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Core.Reports;
using Win7POS.Core.Tests.Data;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Reports;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Reports;

// No production projection is forged: every ownership decision is made by the real
// RefreshRangeAsync verifier or the native writer in its own transaction.
[TestClass]
public sealed class NativeSalesOwnershipProofTests
{
    private const string ShopA = "synthetic-proof-shop-a";
    private const string ShopB = "synthetic-proof-shop-b";
    private const string CodeA = "SYNTHETIC-PROOF-A";
    private const string CodeB = "SYNTHETIC-PROOF-B";
    private const string VisibleA = "VISIBLE-NATIVE-A";
    private const string ExcludedB = "EXCLUDED-NATIVE-B";
    private static readonly DateTime BusinessTime = new(2000, 1, 2, 11, 12, 13, DateTimeKind.Unspecified);
    private static readonly long UtcMs = new DateTimeOffset(2000, 1, 2, 11, 12, 13, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [TestMethod]
    public async Task N1_CanonicalOutboxIsVerifiedByColdRefresh_WithoutCurrentShopFallback()
    {
        using var fixture = await Fixture.CreateAsync(ShopB, CodeB);
        var id = await fixture.SeedSourceAsync("valid", 1000, VisibleA, ShopA, CodeA);
        using (var connection = fixture.Db.Factory.Open())
        {
            Assert.AreEqual(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sales_report_native_ownership"));
            Assert.AreEqual(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations WHERE migration_id='0015-historical-card-imports'"));
        }
        var pending = await fixture.Query.OpenAsync(fixture.Filter(ShopA), fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(SalesReportCoverageStatus.PendingOwnershipVerification, pending.Coverage.Status);
        Assert.AreEqual(0, pending.FirstPage.Entries.Count);

        await fixture.RefreshAsync();
        var proof = await fixture.ReadProofAsync(id);
        Assert.AreEqual("Verified", proof.State);
        Assert.AreEqual(ShopA, proof.ShopId);
        Assert.AreEqual(NativeSalesOwnershipRepository.PolicyHash, proof.PolicyHash);
        StringAssert.Matches(proof.Fingerprint, new System.Text.RegularExpressions.Regex("^[a-f0-9]{64}$"));
        var a = await fixture.Query.OpenAsync(fixture.Filter(ShopA), fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(1000L, a.Summary.BalanceClp);
        Assert.AreEqual(1L, a.Summary.NativePositiveReceiptCount);
        Assert.AreEqual(1000L, a.Summary.NativeAverageReceiptClp);
        Assert.AreEqual(id, a.FirstPage.Entries.Single().Key.InternalId);
        var currentShop = await fixture.Query.OpenAsync(fixture.Filter(ShopB), fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(0L, currentShop.Summary.BalanceClp);
        Assert.AreEqual(0, currentShop.FirstPage.Entries.Count);
        await Assert.ThrowsExactlyAsync<ReportSnapshotStaleException>(() => fixture.Query.ValidateAsync(pending.Token, fixture.Filter(ShopA), fixture.Policy, CancellationToken.None));
        var revision = await fixture.RevisionAsync();
        await fixture.RefreshAsync();
        Assert.AreEqual(revision, await fixture.RevisionAsync(), "An unchanged terminal proof must not generate a new report revision.");
        Assert.AreEqual(proof.Fingerprint, (await fixture.ReadProofAsync(id)).Fingerprint);
    }

    [TestMethod]
    [DataRow("code_only", "CodeOnly")]
    [DataRow("snapshot_only", "Unknown")]
    public async Task N2_N3_CodeOrReceiptAndExactCurrentSettingsCannotCreateAuthoritativeOwnership(string mode, string expected)
    {
        // The current ID/code and print snapshot exactly match the apparent shop.
        // Neither can upgrade missing historical origin evidence to N1.
        using var fixture = await Fixture.CreateAsync(ShopA, CodeA);
        var id = await fixture.SeedSourceAsync(mode, 7000, ExcludedB, ShopA, CodeA);
        await fixture.RefreshAsync();
        var proof = await fixture.ReadProofAsync(id);
        Assert.AreEqual(expected, proof.State);
        Assert.IsNull(proof.ShopId);
        await fixture.AssertExcludedAsync(id, ExcludedB);
    }

    [TestMethod]
    [DataRow("receipt_shop")]
    [DataRow("request_shop")]
    [DataRow("origin_code")]
    [DataRow("missing_origin_code")]
    [DataRow("source_client")]
    [DataRow("outbox_client")]
    [DataRow("request_client")]
    [DataRow("outbox_schema")]
    [DataRow("request_schema")]
    [DataRow("outbox_operation")]
    [DataRow("request_operation")]
    [DataRow("operation_identity")]
    [DataRow("batch_identity")]
    [DataRow("payload_hash")]
    [DataRow("payload_amount")]
    [DataRow("line_amount")]
    [DataRow("payment_amount")]
    public async Task NX_InvalidSourceBeforeFirstRefreshIsConflict_AndCannotLeakAcrossReadSurfaces(string mode)
    {
        using var fixture = await Fixture.CreateAsync(ShopA, CodeA);
        var id = await fixture.SeedSourceAsync(mode, 7000, ExcludedB, ShopA, CodeA);
        using (var connection = fixture.Db.Factory.Open())
            Assert.AreEqual(0L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sales_report_native_ownership"));
        await fixture.RefreshAsync();
        var proof = await fixture.ReadProofAsync(id);
        Assert.AreEqual("Conflict", proof.State, mode);
        Assert.IsNull(proof.ShopId, mode);
        await fixture.AssertExcludedAsync(id, ExcludedB);
    }

    [TestMethod]
    public async Task RealProofMixedReport_AdmitsNative1000AndHistorical9000_ExcludesOtherShop7000Everywhere()
    {
        using var fixture = await Fixture.CreateAsync(ShopA, CodeA);
        var visibleId = await fixture.SeedSourceAsync("valid", 1000, VisibleA, ShopA, CodeA);
        var excludedId = await fixture.SeedSourceAsync("valid", 7000, ExcludedB, ShopB, CodeB);
        await fixture.SeedHistorical9000Async();
        await fixture.RefreshAsync();
        Assert.AreEqual(ShopA, (await fixture.ReadProofAsync(visibleId)).ShopId);
        Assert.AreEqual(ShopB, (await fixture.ReadProofAsync(excludedId)).ShopId);
        var filter = fixture.Filter(ShopA);
        var snapshot = await fixture.Query.OpenAsync(filter, fixture.Policy, 1, CancellationToken.None);
        Assert.AreEqual(SalesReportCoverageStatus.VerifiedForScope, snapshot.Coverage.Status);
        Assert.AreEqual(10000L, snapshot.Summary.BalanceClp);
        Assert.AreEqual(10000L, snapshot.Summary.CardClp);
        Assert.AreEqual(0L, snapshot.Summary.CashClp);
        Assert.AreEqual(1000L, snapshot.Summary.NativePositiveSalesClp);
        Assert.AreEqual(1L, snapshot.Summary.NativePositiveReceiptCount);
        Assert.AreEqual(1000L, snapshot.Summary.NativeAverageReceiptClp);
        Assert.AreEqual(1L, snapshot.Summary.HistoricalMovementCount);
        Assert.AreEqual(10000L, snapshot.Days.Single().Summary.BalanceClp);
        Assert.AreEqual(10000L, snapshot.HourlyAmounts[11]);
        Assert.AreEqual(10000L, snapshot.HourlyAmounts.Sum());
        Assert.AreEqual(SalesReportOrigin.Native, snapshot.FirstPage.Entries.Single().Key.Origin);
        Assert.AreEqual(visibleId, snapshot.FirstPage.Entries.Single().Key.InternalId);
        Assert.IsTrue(snapshot.FirstPage.HasMore);
        var second = await fixture.Query.GetPageAsync(snapshot.Token, filter, fixture.Policy, snapshot.FirstPage.NextCursor, 1, CancellationToken.None);
        Assert.AreEqual(SalesReportOrigin.Historical, second.Entries.Single().Key.Origin);
        Assert.AreEqual(9000L, second.Entries.Single().SignedAmountClp);
        Assert.IsFalse(second.HasMore);
        Assert.IsNull(await fixture.Query.GetEntryAsync(snapshot.Token, filter, fixture.Policy, new SalesReportEntryKey(SalesReportOrigin.Native, excludedId), CancellationToken.None));
        Assert.IsNotNull(await fixture.Query.GetEntryAsync(snapshot.Token, filter, fixture.Policy, new SalesReportEntryKey(SalesReportOrigin.Native, visibleId), CancellationToken.None));
        var hiddenSearch = await fixture.Query.OpenAsync(fixture.Filter(ShopA, ExcludedB), fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(0L, hiddenSearch.Summary.BalanceClp);
        Assert.AreEqual(0L, hiddenSearch.Summary.NativePositiveReceiptCount);
        Assert.AreEqual(0L, hiddenSearch.Summary.HistoricalMovementCount);
        Assert.AreEqual(0, hiddenSearch.FirstPage.Entries.Count);
        Assert.IsFalse(hiddenSearch.FirstPage.HasMore);
        var rows = snapshot.FirstPage.Entries.Concat(second.Entries).ToArray();
        var csv = new[] { SalesReportCsvSerializer.Header, SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Metadata(snapshot)) }
            .Concat(rows.Select(row => SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Movement(snapshot.Token, snapshot.Coverage, row)))).ToArray();
        Assert.AreEqual(4, csv.Length); // header + metadata + exactly two admitted movements
        CollectionAssert.AreEquivalent(new[] { "1000", "9000" }, csv.Skip(2).Select(row => row.Split(';')[10]).ToArray());
        Assert.AreEqual("1000", csv[1].Split(';')[23]);
        Assert.AreEqual("1", csv[1].Split(';')[24]);
        Assert.AreEqual("1000", csv[1].Split(';')[25]);
        Assert.IsFalse(string.Join("\n", csv).Contains(ExcludedB, StringComparison.Ordinal));
        Assert.IsFalse(string.Join("\n", csv).Contains(";7000;", StringComparison.Ordinal));
        var otherShop = await fixture.Query.OpenAsync(fixture.Filter(ShopB), fixture.Policy, 200, CancellationToken.None);
        Assert.AreEqual(7000L, otherShop.Summary.BalanceClp, "The coherent source is admitted only for its actual historical origin ID.");
        Assert.AreEqual(excludedId, otherShop.FirstPage.Entries.Single().Key.InternalId);
    }

    [TestMethod]
    public async Task SupportedNativeVoidMutation_ChangesRealProofAndRevision_AndStalesExistingReportToken()
    {
        using var fixture = await Fixture.CreateAsync(ShopA, CodeA);
        using (var connection = fixture.Db.Factory.Open())
            await connection.ExecuteAsync("INSERT INTO product_meta(barcode,stock_qty) VALUES('SYNTHETIC-PROOF-ITEM',5)");
        var repository = new SaleRepository(fixture.Db.Factory);
        var sale = new Sale { Code = VisibleA, CreatedAt = UtcMs, Kind = (int)SaleKind.Sale, Total = 1000, PaidCash = 1000, PaidCard = 0, Change = 0,
            ReceiptShopSnapshotJson = Snapshot(CodeA) };
        var id = await repository.InsertSaleAsync(sale, new[] { new SaleLine { Barcode = "SYNTHETIC-PROOF-ITEM", Name = "Synthetic proof item", Quantity = 1, UnitPrice = 1000 } });
        var before = await fixture.ReadProofAsync(id);
        Assert.AreEqual("Verified", before.State);
        var filter = fixture.Filter(ShopA);
        var refundId = await repository.InsertRefundSaleAsync(new RefundCreateRequest { OriginalSaleId = id, Reason = "Synthetic report token mutation" }, -1000, -1000, 0, 0);
        // Capture after the refund insert, so the later invalidation proves the
        // supported original-sale mutation itself publishes the changed revision.
        var snapshot = await fixture.Query.OpenAsync(filter, fixture.Policy, 200, CancellationToken.None);
        var revision = await fixture.RevisionAsync();
        await repository.MarkSaleVoidedAsync(id, refundId, UtcMs + 99);
        var after = await fixture.ReadProofAsync(id);
        Assert.AreEqual("Verified", after.State);
        Assert.AreEqual(ShopA, after.ShopId);
        Assert.AreNotEqual(before.Fingerprint, after.Fingerprint);
        Assert.IsTrue(await fixture.RevisionAsync() > revision);
        await Assert.ThrowsExactlyAsync<ReportSnapshotStaleException>(() => fixture.Query.ValidateAsync(snapshot.Token, filter, fixture.Policy, CancellationToken.None));
        var fresh = await fixture.Query.OpenAsync(filter, fixture.Policy, 200, CancellationToken.None);
        var entry = await fixture.Query.GetEntryAsync(fresh.Token, filter, fixture.Policy, new SalesReportEntryKey(SalesReportOrigin.Native, id), CancellationToken.None);
        Assert.IsNotNull(entry);
        Assert.IsTrue(entry.IsVoided);
        Assert.AreEqual(1000L, entry.SignedAmountClp);
        using var verify = fixture.Db.Factory.Open();
        Assert.AreEqual(1000L, await verify.ExecuteScalarAsync<long>("SELECT total FROM sales WHERE id=@id", new { id }));
    }

    private static string Snapshot(string code)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(ReceiptShopInfo)).WriteObject(stream, new ReceiptShopInfo { Name = "Synthetic historical shop", ShopCode = code, Source = "synthetic-proof-fixture" });
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class Proof
    {
        public string State { get; set; } = "";
        public string? ShopId { get; set; }
        public string Fingerprint { get; set; } = "";
        public string PolicyHash { get; set; } = "";
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "NProof." + Guid.NewGuid().ToString("N").Substring(0, 12));
        internal OwnedSqliteTestFile Db { get; private set; } = null!;
        internal SalesReportTimePolicy Policy { get; private set; } = null!;
        internal SalesReportQuery Query { get; private set; } = null!;
        private NativeSalesOwnershipRepository _ownership = null!;
        internal static async Task<Fixture> CreateAsync(string currentShop, string currentCode)
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture._root);
                fixture.Db = OwnedSqliteTestFile.Create(Path.Combine(fixture._root, "p.db"));
                DbInitializer.EnsureCreated(fixture.Db.Options);
                await new ShopOfficialSnapshotRepository(fixture.Db.Factory).SaveAsync(new OfficialShopSnapshot { ShopId = currentShop, ShopCode = currentCode, ShopName = "Synthetic current shop", Source = "synthetic-proof-fixture" });
                fixture.Policy = new SalesReportTimePolicy("synthetic-proof-clock", "synthetic-fixed-offset-v1", "literal synthetic UTC offset zero", new[]
                {
                    new SalesReportUtcSegment(new DateTimeOffset(1999,1,1,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds(), new DateTimeOffset(2002,1,1,0,0,0,TimeSpan.Zero).ToUnixTimeMilliseconds(), 0)
                });
                var coordinator = SalesReportSnapshotCoordinator.ForDatabase(fixture.Db.Factory.DbPath);
                fixture.Query = new SalesReportQuery(fixture.Db.Factory, () => fixture.Db.Factory.AuthorityStamp, coordinator);
                fixture._ownership = new NativeSalesOwnershipRepository(fixture.Db.Factory, coordinator, () => fixture.Db.Factory.AuthorityStamp);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        internal SalesReportFilter Filter(string shop, string? search = null) => new(shop, new DateTime(2000, 1, 2), new DateTime(2000, 1, 3), SalesReportOrigins.All,
            true, null, NativeSalesOwnershipRepository.PolicyHash, Policy.Hash, search);
        internal async Task RefreshAsync()
        {
            // Bounded fixture batches use the actual source capture/verify/CAS publication path.
            for (var attempt = 0; attempt < 4; attempt++) if (await _ownership.RefreshRangeAsync(UtcMs - 1, UtcMs + 1, 100, CancellationToken.None)) return;
            Assert.Fail("Synthetic proof sources did not reach a terminal decision within four bounded batches.");
        }
        internal async Task<Proof> ReadProofAsync(long id)
        {
            using var connection = Db.Factory.Open();
            return await connection.QuerySingleAsync<Proof>("SELECT state AS State,shop_id AS ShopId,source_fingerprint AS Fingerprint,policy_hash AS PolicyHash FROM sales_report_native_ownership WHERE sale_id=@id", new { id });
        }
        internal async Task<long> RevisionAsync()
        {
            using var connection = Db.Factory.Open();
            return await connection.ExecuteScalarAsync<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1");
        }
        internal async Task<long> SeedSourceAsync(string mode, long amount, string reference, string originShop, string originCode)
        {
            using var connection = Db.Factory.Open(); using var transaction = connection.BeginTransaction();
            var sale = new Sale { Code = reference, ClientSaleId = "synthetic-client-" + reference, CreatedAt = UtcMs, Kind = (int)SaleKind.Sale,
                Total = amount, PaidCash = 0, PaidCard = amount, Change = 0, ReceiptShopSnapshotJson = Snapshot(mode == "receipt_shop" ? CodeB : originCode) };
            sale.Id = await connection.ExecuteScalarAsync<long>(@"INSERT INTO sales(client_sale_id,code,createdAt,kind,total,paidCash,paidCard,change,receipt_shop_snapshot)
VALUES(@ClientSaleId,@Code,@CreatedAt,@Kind,@Total,@PaidCash,@PaidCard,@Change,@ReceiptShopSnapshotJson); SELECT last_insert_rowid();", sale, transaction);
            var line = new SaleLine { SaleId = sale.Id, Barcode = "SYNTHETIC-PROOF-ITEM", Name = "Synthetic proof item", Quantity = 1, UnitPrice = amount, LineTotal = amount };
            line.Id = await connection.ExecuteScalarAsync<long>(@"INSERT INTO sale_lines(saleId,barcode,name,quantity,unitPrice,lineTotal)
VALUES(@SaleId,@Barcode,@Name,@Quantity,@UnitPrice,@LineTotal); SELECT last_insert_rowid();", line, transaction);
            if (mode != "snapshot_only")
            {
                var item = new SalesSyncOutboxItem { SaleId = sale.Id, ClientSaleId = sale.ClientSaleId, OriginShopId = originShop, OriginShopCode = originCode,
                    OperationType = "sale", SchemaVersion = PosOnlineContract.SalesSchemaVersion, IdempotencyKey = "synthetic-operation-" + reference };
                var request = PosSalesSyncRequestBuilder.BuildCanonical(item, sale, new[] { line }, new Dictionary<long, string>());
                request.Sales[0].BusinessDate = "2000-01-02"; // Explicit fixture clock; no host time-zone oracle.
                item.ClientBatchId = request.Batch.ClientBatchId;
                switch (mode)
                {
                    case "valid": case "receipt_shop": break;
                    case "code_only": item.OriginShopId = null; break;
                    case "request_shop": request.ShopCode = CodeB; break;
                    case "origin_code": item.OriginShopCode = CodeB; break;
                    case "missing_origin_code": item.OriginShopCode = ""; break;
                    case "source_client": await connection.ExecuteAsync("UPDATE sales SET client_sale_id='synthetic-source-client-mismatch' WHERE id=@id", new { id = sale.Id }, transaction); break;
                    case "outbox_client": item.ClientSaleId = "synthetic-outbox-client-mismatch"; break;
                    case "request_client": request.Sales[0].ClientSaleId = "synthetic-request-client-mismatch"; break;
                    case "outbox_schema": item.SchemaVersion = "synthetic-unsupported-schema"; break;
                    case "request_schema": request.SchemaVersion = "synthetic-unsupported-schema"; break;
                    case "outbox_operation": item.OperationType = "refund"; break;
                    case "request_operation": request.Sales[0].Kind = "refund"; break;
                    case "operation_identity": request.Sales[0].IdempotencyKey = "synthetic-different-operation"; break;
                    case "batch_identity": item.ClientBatchId = "synthetic-different-batch"; break;
                    case "payload_hash": break;
                    case "payload_amount": request.Sales[0].Amounts.NetClp = 9000; break;
                    case "line_amount": request.Sales[0].Lines[0].AmountClp = 9000; break;
                    case "payment_amount": request.Sales[0].Payments[0].AmountClp = 9000; break;
                    default: throw new ArgumentOutOfRangeException(nameof(mode));
                }
                item.PayloadJson = PosSalesSyncRequestBuilder.SerializeCanonical(request);
                item.PayloadHash = mode == "payload_hash" ? new string('0', 64) : PosSalesSyncRequestBuilder.Sha256Hex(item.PayloadJson);
                await connection.ExecuteAsync(@"INSERT INTO sales_sync_outbox(sale_id,client_sale_id,client_batch_id,idempotency_key,schema_version,operation_type,origin_shop_id,origin_shop_code,payload_json,payload_hash,status,created_at,updated_at)
VALUES(@SaleId,@ClientSaleId,@ClientBatchId,@IdempotencyKey,@SchemaVersion,@OperationType,@OriginShopId,@OriginShopCode,@PayloadJson,@PayloadHash,'pending',@now,@now);", new
                {
                    item.SaleId, item.ClientSaleId, item.ClientBatchId, item.IdempotencyKey, item.SchemaVersion, item.OperationType,
                    item.OriginShopId, item.OriginShopCode, item.PayloadJson, item.PayloadHash, now = UtcMs
                }, transaction);
            }
            transaction.Commit(); return sale.Id;
        }
        internal async Task AssertExcludedAsync(long id, string reference)
        {
            var filter = Filter(ShopA); var snapshot = await Query.OpenAsync(filter, Policy, 1, CancellationToken.None);
            Assert.AreEqual(SalesReportCoverageStatus.VerifiedSubset, snapshot.Coverage.Status);
            Assert.IsTrue(snapshot.Coverage.CanPublish);
            Assert.AreEqual(0L, snapshot.Summary.BalanceClp);
            Assert.AreEqual(0L, snapshot.Summary.NativePositiveReceiptCount);
            Assert.IsNull(snapshot.Summary.NativeAverageReceiptClp);
            Assert.AreEqual(0L, snapshot.Summary.HistoricalMovementCount);
            Assert.AreEqual(0, snapshot.FirstPage.Entries.Count);
            Assert.IsFalse(snapshot.FirstPage.HasMore);
            Assert.AreEqual(0, snapshot.Days.Count);
            Assert.IsTrue(snapshot.HourlyAmounts.All(value => value == 0));
            Assert.IsNull(await Query.GetEntryAsync(snapshot.Token, filter, Policy, new SalesReportEntryKey(SalesReportOrigin.Native, id), CancellationToken.None));
            var search = await Query.OpenAsync(Filter(ShopA, reference), Policy, 200, CancellationToken.None);
            Assert.AreEqual(0L, search.Summary.BalanceClp);
            Assert.AreEqual(0, search.FirstPage.Entries.Count);
            var metadata = SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Metadata(snapshot));
            Assert.IsFalse(metadata.Contains(reference, StringComparison.Ordinal));
            Assert.IsFalse(metadata.Contains(";7000;", StringComparison.Ordinal));
            Assert.AreEqual("0", metadata.Split(';')[23]);
            Assert.AreEqual("0", metadata.Split(';')[24]);
            Assert.AreEqual("", metadata.Split(';')[25]);
        }
        internal async Task SeedHistorical9000Async()
        {
            using var connection = Db.Factory.Open(); using var transaction = connection.BeginTransaction();
            var dataset = await connection.ExecuteScalarAsync<string>("SELECT dataset_id FROM historical_import_dataset WHERE singleton_id=1", transaction: transaction);
            // Historical fixture economics are literal; this helper does not claim to test import apply.
            await connection.ExecuteAsync(@"INSERT INTO historical_merchant_scope(id,shop_id,provider,evidence_reference,created_at) VALUES('synthetic-scope',@shop,'Getnet','synthetic-source-proof',@utc);
INSERT INTO historical_merchant_binding(id,scope_id,shop_id,provider,alias_version,alias_key,evidence_reference,used_at,is_active)
VALUES('synthetic-binding','synthetic-scope',@shop,'Getnet','synthetic-alias-v1','synthetic-alias','synthetic-source-proof',@utc,1);
INSERT INTO historical_import_operation(operation_id,operation_kind,shop_id,dataset_id,request_fingerprint,batch_id,operator_id,created_at,receipt_json,inserted_count,duplicate_count,signed_amount_clp)
VALUES('synthetic-apply','Apply',@shop,@dataset,'synthetic-request','synthetic-batch',7,@utc,'{}',1,0,9000);
INSERT INTO historical_import_batch(id,shop_id,dataset_id,apply_operation_id,state,revision,created_at) VALUES('synthetic-batch',@shop,@dataset,'synthetic-apply','Active',1,@utc);
INSERT INTO historical_import_file(id,batch_id,source_sha256,profile,parser_version,temporal_policy,evidence_reference,source_rows)
VALUES('synthetic-file','synthetic-batch',@hash,'GetnetLegacy','synthetic-parser-v1','synthetic-fixed-offset-v1;LegacyLocal;CLP=explicitly-approved','synthetic-source-proof',1);
INSERT INTO historical_transactions(batch_id,file_id,shop_id,provider,merchant_scope_id,binding_id,external_namespace,external_id,business_ticks,timestamp_raw,offset_minutes,occurred_at_utc_ms,movement_kind,state,settlement_state,signed_amount_clp,signed_fee_clp,signed_fee_vat_clp,signed_net_clp,original_cashier_id,card_subtype,source_sheet,source_row,is_active)
VALUES('synthetic-batch','synthetic-file',@shop,'Getnet','synthetic-scope','synthetic-binding','synthetic-financial-v1','HISTORICAL-VISIBLE-9000',@ticks,'02/01/2000 11:12:13',0,@utc,'Sale','Abonado','Abonado',9000,0,0,9000,NULL,'Debit','Synthetic',2,1);",
                new { shop = ShopA, dataset, utc = UtcMs, ticks = BusinessTime.Ticks, hash = new string('a', 64) }, transaction);
            transaction.Commit();
        }
        public void Dispose()
        {
            Db?.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
