using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SaleTransactionWriterTests
{
    private const long CreatedAt = 1700000000000L;
    private const string ShopCode = "F6-SHOP";
    private const string ShopId = "f6-shop-id";

    [TestMethod]
    public async Task SaleTransactionWriter_AndSaleFacade_PreserveFullSalePersistenceStockOutboxAndHash()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        const string barcode = "F6-FULL-SALE";

        await SaveShopAsync(directDb.Factory);
        await SaveShopAsync(facadeDb.Factory);
        await SeedStockAsync(directDb.Factory, barcode, 5);
        await SeedStockAsync(facadeDb.Factory, barcode, 5);

        var directSale = NewOrdinarySale("F6-FULL-SALE");
        var facadeSale = NewOrdinarySale("F6-FULL-SALE");
        var directLines = new[] { NewLine(barcode, 2, 100) };
        var facadeLines = new[] { NewLine(barcode, 2, 100) };

        var directSaleId = await new DirectTransactionSurface(directDb.Factory)
            .InsertSaleAsync(directSale, directLines);
        var facadeSaleId = await new FacadeTransactionSurface(facadeDb.Factory)
            .InsertSaleAsync(facadeSale, facadeLines);

        var direct = await LoadFullSaleSnapshotAsync(directDb.Factory, directSaleId, barcode);
        var facade = await LoadFullSaleSnapshotAsync(facadeDb.Factory, facadeSaleId, barcode);

        AssertFullSaleSnapshotEqual(direct, facade);
        Assert.AreEqual("win7pos-sale-" + directSaleId.ToString(CultureInfo.InvariantCulture), direct.Sale.ClientSaleId);
        Assert.AreEqual(direct.Sale.ClientSaleId, direct.Outbox.ClientSaleId);
        Assert.AreEqual(3L, direct.StockQty);
        Assert.AreEqual(1, direct.Movements.Count);
        Assert.AreEqual(-2L, direct.Movements[0].QuantityDelta);
        Assert.AreEqual(direct.Sale.ClientSaleId + ":1:sale_decrement", direct.Movements[0].MovementKey);
        Assert.AreEqual(
            PosSalesSyncRequestBuilder.Sha256Hex(direct.Outbox.PayloadJson),
            direct.Outbox.PayloadHash,
            ignoreCase: true);
        Assert.AreEqual("pending", direct.Outbox.Status);
    }

    [TestMethod]
    public async Task SaleTransactionWriter_AndSaleFacade_CallerOwnedLineAndClientIdRollback()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();

        await VerifyCallerOwnedLineAndClientIdRollbackAsync(
            new DirectTransactionSurface(directDb.Factory),
            directDb.Factory,
            "F6-DIRECT-CALLER");
        await VerifyCallerOwnedLineAndClientIdRollbackAsync(
            new FacadeTransactionSurface(facadeDb.Factory),
            facadeDb.Factory,
            "F6-FACADE-CALLER");
    }

    [TestMethod]
    public async Task SaleTransactionWriter_AndSaleFacade_CallerOwnedLegacyRefundRollback()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();

        await VerifyCallerOwnedLegacyRefundRollbackAsync(
            new DirectTransactionSurface(directDb.Factory),
            directDb.Factory,
            "F6-DIRECT-LEGACY");
        await VerifyCallerOwnedLegacyRefundRollbackAsync(
            new FacadeTransactionSurface(facadeDb.Factory),
            facadeDb.Factory,
            "F6-FACADE-LEGACY");
    }

    [TestMethod]
    public async Task SaleTransactionWriter_AndSaleFacade_CommitVoidAuditAndMarkAtomically()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        const string barcode = "F6-VOID-COMMIT";

        var direct = await CommitVoidAndLoadAsync(
            new DirectTransactionSurface(directDb.Factory),
            directDb.Factory,
            barcode,
            "F6-VOID");
        var facade = await CommitVoidAndLoadAsync(
            new FacadeTransactionSurface(facadeDb.Factory),
            facadeDb.Factory,
            barcode,
            "F6-VOID");

        AssertVoidSnapshotEqual(direct, facade);
        Assert.IsTrue(direct.OriginalVoidedAt.HasValue);
        Assert.AreEqual(direct.VoidSale.Id, direct.OriginalVoidedBySaleId);
        Assert.AreEqual("f6_void", direct.Audit.Action);
        Assert.AreEqual("voided:" + direct.VoidSale.Id.ToString(CultureInfo.InvariantCulture), direct.Audit.Details);
        Assert.AreEqual(5L, direct.StockQty);
        Assert.AreEqual(2, direct.Movements.Count);
        Assert.AreEqual(1L, direct.Movements.Single(row => row.MovementKind == "sale_decrement").QuantityDelta * -1L);
        Assert.AreEqual(1L, direct.Movements.Single(row => row.MovementKind == "void_reverse").QuantityDelta);
        Assert.AreEqual(
            PosSalesSyncRequestBuilder.Sha256Hex(direct.Outbox.PayloadJson),
            direct.Outbox.PayloadHash,
            ignoreCase: true);
    }

    [TestMethod]
    public async Task SaleTransactionWriter_AndSaleFacade_RollBackVoidAuditAndMarkWhenMarkFails()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        const string barcode = "F6-VOID-ROLLBACK";

        await VerifyVoidRollbackWhenMarkFailsAsync(
            new DirectTransactionSurface(directDb.Factory),
            directDb.Factory,
            barcode,
            "F6-DIRECT-VOID-ROLLBACK");
        await VerifyVoidRollbackWhenMarkFailsAsync(
            new FacadeTransactionSurface(facadeDb.Factory),
            facadeDb.Factory,
            barcode,
            "F6-FACADE-VOID-ROLLBACK");
    }

    [TestMethod]
    public async Task SaleTransactionWriter_AndSaleFacade_RejectLegacyRefundBeforeMutation()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();

        await VerifyLegacyRefundValidationAsync(
            new DirectTransactionSurface(directDb.Factory),
            directDb.Factory);
        await VerifyLegacyRefundValidationAsync(
            new FacadeTransactionSurface(facadeDb.Factory),
            facadeDb.Factory);
    }

    [TestMethod]
    public async Task SaleTransactionWriter_AndSaleFacade_PreservePdfPrintedNarrowUpdate()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();

        var direct = await VerifyPdfPrintedNarrowUpdateAsync(
            new DirectTransactionSurface(directDb.Factory),
            directDb.Factory,
            "F6-PDF");
        var facade = await VerifyPdfPrintedNarrowUpdateAsync(
            new FacadeTransactionSurface(facadeDb.Factory),
            facadeDb.Factory,
            "F6-PDF");

        Assert.AreEqual(direct.Target.Code, facade.Target.Code);
        Assert.AreEqual(direct.Target.Total, facade.Target.Total);
        Assert.AreEqual(direct.Target.SyncStatus, facade.Target.SyncStatus);
        Assert.IsTrue(direct.Target.PdfPrinted);
        Assert.IsTrue(facade.Target.PdfPrinted);
        Assert.IsFalse(direct.Control.PdfPrinted);
        Assert.IsFalse(facade.Control.PdfPrinted);
    }

    private static async Task VerifyCallerOwnedLineAndClientIdRollbackAsync(
        ITransactionSurface surface,
        SqliteConnectionFactory factory,
        string code)
    {
        var saleId = await InsertRawSaleAsync(factory, code);
        var expectedClientSaleId = "win7pos-sale-" + saleId.ToString(CultureInfo.InvariantCulture);

        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var clientSaleId = await surface.EnsureClientSaleIdAsync(conn, tx, saleId);
            Assert.AreEqual(expectedClientSaleId, clientSaleId);
            Assert.AreEqual(expectedClientSaleId, await conn.ExecuteScalarAsync<string>(
                "SELECT client_sale_id FROM sales WHERE id = @saleId;",
                new { saleId },
                tx));

            await surface.InsertSaleLinesAsync(conn, tx, new[]
            {
                new SaleLine
                {
                    SaleId = saleId,
                    Barcode = code + "-LINE",
                    Name = "F6 caller-owned line",
                    Quantity = 1,
                    UnitPrice = 100,
                    LineTotal = 100
                }
            });
            Assert.AreEqual(1L, await CountAsync(
                conn,
                "SELECT COUNT(1) FROM sale_lines WHERE saleId = @saleId;",
                new { saleId },
                tx));

            tx.Rollback();
        }

        using var verify = factory.Open();
        Assert.IsNull(await verify.ExecuteScalarAsync<string>(
            "SELECT client_sale_id FROM sales WHERE id = @saleId;",
            new { saleId }));
        Assert.AreEqual(0L, await CountAsync(
            verify,
            "SELECT COUNT(1) FROM sale_lines WHERE saleId = @saleId;",
            new { saleId }));
    }

    private static async Task VerifyCallerOwnedLegacyRefundRollbackAsync(
        ITransactionSurface surface,
        SqliteConnectionFactory factory,
        string code)
    {
        var originalSaleId = await InsertRawSaleAsync(factory, code + "-ORIGINAL");

        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var refundSaleId = await surface.InsertRefundSaleAsync(
                conn,
                tx,
                new RefundCreateRequest
                {
                    OriginalSaleId = originalSaleId,
                    Reason = "F6 caller-owned legacy refund"
                },
                -100,
                -100,
                0,
                0);
            Assert.IsTrue(refundSaleId > originalSaleId);
            Assert.AreEqual(1L, await CountAsync(
                conn,
                "SELECT COUNT(1) FROM sales WHERE id = @refundSaleId AND related_sale_id = @originalSaleId AND kind = @kind;",
                new { refundSaleId, originalSaleId, kind = (int)SaleKind.Refund },
                tx));

            tx.Rollback();
        }

        using var verify = factory.Open();
        Assert.AreEqual(1L, await CountAsync(verify, "SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(0L, await CountAsync(
            verify,
            "SELECT COUNT(1) FROM sales WHERE related_sale_id = @originalSaleId;",
            new { originalSaleId }));
    }

    private static async Task<VoidSnapshot> CommitVoidAndLoadAsync(
        ITransactionSurface surface,
        SqliteConnectionFactory factory,
        string barcode,
        string codePrefix)
    {
        await SaveShopAsync(factory);
        await SeedStockAsync(factory, barcode, 5);

        var originalSaleId = await surface.InsertSaleAsync(
            NewOrdinarySale(codePrefix + "-ORIGINAL", 100),
            new[] { NewLine(barcode, 1, 100) });
        var originalLineId = await LoadOnlyLineIdAsync(factory, originalSaleId);
        var voidSale = new Sale
        {
            Code = codePrefix + "-VOID",
            CreatedAt = CreatedAt + 1,
            Kind = (int)SaleKind.Void,
            RelatedSaleId = originalSaleId,
            Reason = "F6 full void",
            Total = -100,
            PaidCash = -100,
            PaidCard = 0,
            Change = 0
        };

        var voidSaleId = await surface.InsertRefundOrVoidAsync(
            voidSale,
            new[]
            {
                new SaleLine
                {
                    Barcode = barcode,
                    Name = "F6 sale item",
                    Quantity = 1,
                    UnitPrice = 100,
                    LineTotal = -100,
                    RelatedOriginalLineId = originalLineId
                }
            },
            originalSaleId,
            "f6_void",
            saleId => "voided:" + saleId.ToString(CultureInfo.InvariantCulture));

        return await LoadVoidSnapshotAsync(factory, originalSaleId, voidSaleId, barcode);
    }

    private static async Task VerifyVoidRollbackWhenMarkFailsAsync(
        ITransactionSurface surface,
        SqliteConnectionFactory factory,
        string barcode,
        string codePrefix)
    {
        await SaveShopAsync(factory);
        await SeedStockAsync(factory, barcode, 5);
        var originalSaleId = await surface.InsertSaleAsync(
            NewOrdinarySale(codePrefix + "-ORIGINAL", 100),
            new[] { NewLine(barcode, 1, 100) });
        var originalLineId = await LoadOnlyLineIdAsync(factory, originalSaleId);

        using (var conn = factory.Open())
        {
            await conn.ExecuteAsync(@"
CREATE TRIGGER fail_f6_void_mark
BEFORE UPDATE OF voided_by_sale_id ON sales
WHEN NEW.voided_by_sale_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, 'f6 injected void mark failure');
END;");
        }

        var exception = await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            surface.InsertRefundOrVoidAsync(
                new Sale
                {
                    Code = codePrefix + "-VOID",
                    CreatedAt = CreatedAt + 2,
                    Kind = (int)SaleKind.Void,
                    RelatedSaleId = originalSaleId,
                    Reason = "F6 rollback void",
                    Total = -100,
                    PaidCash = -100,
                    PaidCard = 0,
                    Change = 0
                },
                new[]
                {
                    new SaleLine
                    {
                        Barcode = barcode,
                        Name = "F6 sale item",
                        Quantity = 1,
                        UnitPrice = 100,
                        LineTotal = -100,
                        RelatedOriginalLineId = originalLineId
                    }
                },
                originalSaleId,
                "f6_void_rollback",
                saleId => "voided:" + saleId.ToString(CultureInfo.InvariantCulture)));
        StringAssert.Contains(exception.Message, "f6 injected void mark failure");

        using var verify = factory.Open();
        var original = await verify.QuerySingleAsync<VoidedSaleRow>(@"
SELECT voided_by_sale_id AS VoidedBySaleId,
       voided_at AS VoidedAt
FROM sales
WHERE id = @originalSaleId;", new { originalSaleId });
        Assert.IsNull(original.VoidedBySaleId);
        Assert.IsNull(original.VoidedAt);
        Assert.AreEqual(1L, await CountAsync(verify, "SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(1L, await CountAsync(verify, "SELECT COUNT(1) FROM sale_lines;"));
        Assert.AreEqual(1L, await CountAsync(verify, "SELECT COUNT(1) FROM sales_sync_outbox;"));
        Assert.AreEqual(0L, await CountAsync(
            verify,
            "SELECT COUNT(1) FROM audit_log WHERE action = 'f6_void_rollback';"));
        Assert.AreEqual(1L, await CountAsync(verify, "SELECT COUNT(1) FROM local_stock_movements;"));
        Assert.AreEqual(4L, await verify.ExecuteScalarAsync<long>(
            "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
            new { barcode }));
    }

    private static async Task VerifyLegacyRefundValidationAsync(
        ITransactionSurface surface,
        SqliteConnectionFactory factory)
    {
        var invalidReason = new string(
            'r',
            SalesReceiptContentPolicy.MaxSaleReasonCharacters + 1);
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            surface.InsertRefundSaleAsync(
                new RefundCreateRequest
                {
                    OriginalSaleId = 42,
                    Reason = invalidReason
                },
                -100,
                -100,
                0,
                0));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            surface.InsertRefundSaleAsync(
                new RefundCreateRequest
                {
                    OriginalSaleId = 42,
                    Reason = "F6 missing original"
                },
                -100,
                -100,
                0,
                0));

        using var verify = factory.Open();
        Assert.AreEqual(0L, await CountAsync(verify, "SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(0L, await CountAsync(verify, "SELECT COUNT(1) FROM sale_lines;"));
        Assert.AreEqual(0L, await CountAsync(verify, "SELECT COUNT(1) FROM sales_sync_outbox;"));
    }

    private static async Task<PdfSnapshot> VerifyPdfPrintedNarrowUpdateAsync(
        ITransactionSurface surface,
        SqliteConnectionFactory factory,
        string codePrefix)
    {
        var targetSaleId = await InsertRawSaleAsync(factory, codePrefix + "-TARGET", "pending");
        var controlSaleId = await InsertRawSaleAsync(factory, codePrefix + "-CONTROL", "acked");

        await surface.MarkPdfPrintedAsync(targetSaleId);

        using var conn = factory.Open();
        return new PdfSnapshot
        {
            Control = await LoadPdfSaleAsync(conn, controlSaleId),
            Target = await LoadPdfSaleAsync(conn, targetSaleId)
        };
    }

    private static Sale NewOrdinarySale(string code, long total = 200)
    {
        return new Sale
        {
            Code = code,
            CreatedAt = CreatedAt,
            Kind = (int)SaleKind.Sale,
            Total = total,
            PaidCash = total,
            PaidCard = 0,
            Change = 0
        };
    }

    private static SaleLine NewLine(string barcode, int quantity, long unitPrice)
    {
        return new SaleLine
        {
            Barcode = barcode,
            Name = "F6 sale item",
            Quantity = quantity,
            UnitPrice = unitPrice
        };
    }

    private static async Task SaveShopAsync(SqliteConnectionFactory factory)
    {
        await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopId = ShopId,
            ShopCode = ShopCode,
            ShopName = "F6 test shop",
            Source = "test"
        });
    }

    private static async Task SeedStockAsync(
        SqliteConnectionFactory factory,
        string barcode,
        long stockQty)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO product_meta(barcode, stock_qty)
VALUES(@barcode, @stockQty);", new { barcode, stockQty });
    }

    private static async Task<long> InsertRawSaleAsync(
        SqliteConnectionFactory factory,
        string code,
        string syncStatus = "pending")
    {
        using var conn = factory.Open();
        return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, total, paidCash, paidCard, change, sync_status, pdf_printed)
VALUES(@code, @createdAt, @kind, 100, 100, 0, 0, @syncStatus, 0);
SELECT last_insert_rowid();",
            new
            {
                code,
                createdAt = CreatedAt - 1,
                kind = (int)SaleKind.Sale,
                syncStatus
            });
    }

    private static async Task<long> LoadOnlyLineIdAsync(
        SqliteConnectionFactory factory,
        long saleId)
    {
        using var conn = factory.Open();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM sale_lines WHERE saleId = @saleId;",
            new { saleId });
    }

    private static async Task<FullSaleSnapshot> LoadFullSaleSnapshotAsync(
        SqliteConnectionFactory factory,
        long saleId,
        string barcode)
    {
        using var conn = factory.Open();
        return new FullSaleSnapshot
        {
            Lines = (await conn.QueryAsync<SaleLineRow>(@"
SELECT id AS Id, saleId AS SaleId, productId AS ProductId, barcode AS Barcode,
       name AS Name, quantity AS Quantity, unitPrice AS UnitPrice, lineTotal AS LineTotal,
       related_original_line_id AS RelatedOriginalLineId
FROM sale_lines
WHERE saleId = @saleId
ORDER BY id ASC;", new { saleId })).ToArray(),
            Movements = (await conn.QueryAsync<StockMovementRow>(@"
SELECT movement_key AS MovementKey, sale_id AS SaleId, sale_line_id AS SaleLineId,
       barcode AS Barcode, quantity_delta AS QuantityDelta, movement_kind AS MovementKind
FROM local_stock_movements
WHERE sale_id = @saleId
ORDER BY id ASC;", new { saleId })).ToArray(),
            Outbox = await LoadOutboxAsync(conn, saleId),
            Sale = await LoadSaleAsync(conn, saleId),
            StockQty = await conn.ExecuteScalarAsync<long>(
                "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
                new { barcode })
        };
    }

    private static async Task<VoidSnapshot> LoadVoidSnapshotAsync(
        SqliteConnectionFactory factory,
        long originalSaleId,
        long voidSaleId,
        string barcode)
    {
        using var conn = factory.Open();
        var original = await conn.QuerySingleAsync<VoidedSaleRow>(@"
SELECT voided_by_sale_id AS VoidedBySaleId,
       voided_at AS VoidedAt
FROM sales
WHERE id = @originalSaleId;", new { originalSaleId });
        return new VoidSnapshot
        {
            Audit = await conn.QuerySingleAsync<AuditRow>(@"
SELECT action AS Action, details AS Details
FROM audit_log
WHERE action = 'f6_void'
ORDER BY id ASC;"),
            Movements = (await conn.QueryAsync<StockMovementRow>(@"
SELECT movement_key AS MovementKey, sale_id AS SaleId, sale_line_id AS SaleLineId,
       barcode AS Barcode, quantity_delta AS QuantityDelta, movement_kind AS MovementKind
FROM local_stock_movements
WHERE sale_id IN (@originalSaleId, @voidSaleId)
ORDER BY id ASC;", new { originalSaleId, voidSaleId })).ToArray(),
            OriginalVoidedAt = original.VoidedAt,
            OriginalVoidedBySaleId = original.VoidedBySaleId,
            Outbox = await LoadOutboxAsync(conn, voidSaleId),
            StockQty = await conn.ExecuteScalarAsync<long>(
                "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
                new { barcode }),
            VoidSale = await LoadSaleAsync(conn, voidSaleId)
        };
    }

    private static Task<SaleRow> LoadSaleAsync(SqliteConnection conn, long saleId)
    {
        return conn.QuerySingleAsync<SaleRow>(@"
SELECT id AS Id, client_sale_id AS ClientSaleId, code AS Code, kind AS Kind,
       related_sale_id AS RelatedSaleId, total AS Total, paidCash AS PaidCash,
       paidCard AS PaidCard, change AS Change, sync_status AS SyncStatus,
       COALESCE(pdf_printed, 0) AS PdfPrinted
FROM sales
WHERE id = @saleId;", new { saleId });
    }

    private static Task<OutboxRow> LoadOutboxAsync(SqliteConnection conn, long saleId)
    {
        return conn.QuerySingleAsync<OutboxRow>(@"
SELECT sale_id AS SaleId, client_sale_id AS ClientSaleId, client_batch_id AS ClientBatchId,
       idempotency_key AS IdempotencyKey, schema_version AS SchemaVersion,
       operation_type AS OperationType, origin_shop_id AS OriginShopId,
       origin_shop_code AS OriginShopCode, payload_json AS PayloadJson,
       payload_hash AS PayloadHash, status AS Status
FROM sales_sync_outbox
WHERE sale_id = @saleId;", new { saleId });
    }

    private static Task<PdfSaleRow> LoadPdfSaleAsync(SqliteConnection conn, long saleId)
    {
        return conn.QuerySingleAsync<PdfSaleRow>(@"
SELECT code AS Code, total AS Total, sync_status AS SyncStatus,
       COALESCE(pdf_printed, 0) AS PdfPrinted
FROM sales
WHERE id = @saleId;", new { saleId });
    }

    private static async Task<long> CountAsync(
        SqliteConnection conn,
        string sql,
        object? parameters = null,
        SqliteTransaction? tx = null)
    {
        return await conn.ExecuteScalarAsync<long>(sql, parameters, tx);
    }

    private static void AssertFullSaleSnapshotEqual(
        FullSaleSnapshot expected,
        FullSaleSnapshot actual)
    {
        AssertSaleEqual(expected.Sale, actual.Sale);
        Assert.AreEqual(expected.StockQty, actual.StockQty);
        AssertOutboxEqual(expected.Outbox, actual.Outbox);
        Assert.AreEqual(expected.Lines.Count, actual.Lines.Count);
        for (var index = 0; index < expected.Lines.Count; index++)
        {
            var expectedLine = expected.Lines[index];
            var actualLine = actual.Lines[index];
            Assert.AreEqual(expectedLine.Id, actualLine.Id);
            Assert.AreEqual(expectedLine.SaleId, actualLine.SaleId);
            Assert.AreEqual(expectedLine.ProductId, actualLine.ProductId);
            Assert.AreEqual(expectedLine.Barcode, actualLine.Barcode);
            Assert.AreEqual(expectedLine.Name, actualLine.Name);
            Assert.AreEqual(expectedLine.Quantity, actualLine.Quantity);
            Assert.AreEqual(expectedLine.UnitPrice, actualLine.UnitPrice);
            Assert.AreEqual(expectedLine.LineTotal, actualLine.LineTotal);
            Assert.AreEqual(expectedLine.RelatedOriginalLineId, actualLine.RelatedOriginalLineId);
        }
        AssertStockMovementsEqual(expected.Movements, actual.Movements);
    }

    private static void AssertVoidSnapshotEqual(VoidSnapshot expected, VoidSnapshot actual)
    {
        Assert.AreEqual(expected.OriginalVoidedBySaleId, actual.OriginalVoidedBySaleId);
        Assert.AreEqual(expected.OriginalVoidedAt.HasValue, actual.OriginalVoidedAt.HasValue);
        AssertSaleEqual(expected.VoidSale, actual.VoidSale);
        Assert.AreEqual(expected.Audit.Action, actual.Audit.Action);
        Assert.AreEqual(expected.Audit.Details, actual.Audit.Details);
        Assert.AreEqual(expected.StockQty, actual.StockQty);
        AssertOutboxEqual(expected.Outbox, actual.Outbox);
        AssertStockMovementsEqual(expected.Movements, actual.Movements);
    }

    private static void AssertSaleEqual(SaleRow expected, SaleRow actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.ClientSaleId, actual.ClientSaleId);
        Assert.AreEqual(expected.Code, actual.Code);
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.RelatedSaleId, actual.RelatedSaleId);
        Assert.AreEqual(expected.Total, actual.Total);
        Assert.AreEqual(expected.PaidCash, actual.PaidCash);
        Assert.AreEqual(expected.PaidCard, actual.PaidCard);
        Assert.AreEqual(expected.Change, actual.Change);
        Assert.AreEqual(expected.SyncStatus, actual.SyncStatus);
        Assert.AreEqual(expected.PdfPrinted, actual.PdfPrinted);
    }

    private static void AssertOutboxEqual(OutboxRow expected, OutboxRow actual)
    {
        Assert.AreEqual(expected.SaleId, actual.SaleId);
        Assert.AreEqual(expected.ClientSaleId, actual.ClientSaleId);
        Assert.AreEqual(expected.ClientBatchId, actual.ClientBatchId);
        Assert.AreEqual(expected.IdempotencyKey, actual.IdempotencyKey);
        Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(expected.OperationType, actual.OperationType);
        Assert.AreEqual(expected.OriginShopId, actual.OriginShopId);
        Assert.AreEqual(expected.OriginShopCode, actual.OriginShopCode);
        Assert.AreEqual(expected.PayloadJson, actual.PayloadJson);
        Assert.AreEqual(expected.PayloadHash, actual.PayloadHash);
        Assert.AreEqual(expected.Status, actual.Status);
    }

    private static void AssertStockMovementsEqual(
        IReadOnlyList<StockMovementRow> expected,
        IReadOnlyList<StockMovementRow> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index].MovementKey, actual[index].MovementKey);
            Assert.AreEqual(expected[index].SaleId, actual[index].SaleId);
            Assert.AreEqual(expected[index].SaleLineId, actual[index].SaleLineId);
            Assert.AreEqual(expected[index].Barcode, actual[index].Barcode);
            Assert.AreEqual(expected[index].QuantityDelta, actual[index].QuantityDelta);
            Assert.AreEqual(expected[index].MovementKind, actual[index].MovementKind);
        }
    }

    private interface ITransactionSurface
    {
        Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines);

        Task MarkPdfPrintedAsync(long saleId);

        Task<long> InsertRefundSaleAsync(
            RefundCreateRequest request,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor);

        Task<long> InsertRefundSaleAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RefundCreateRequest request,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor);

        Task<long> InsertRefundOrVoidAsync(
            Sale refundSale,
            IReadOnlyList<SaleLine> refundLines,
            long? originalSaleIdToMarkVoided,
            string auditAction,
            Func<long, string> auditDetailsFactory);

        Task InsertSaleLinesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<SaleLine> lines);

        Task<string> EnsureClientSaleIdAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId);
    }

    private sealed class DirectTransactionSurface : ITransactionSurface
    {
        private readonly SaleTransactionWriter _writer;

        public DirectTransactionSurface(SqliteConnectionFactory factory)
        {
            _writer = new SaleTransactionWriter(
                factory,
                new SaleStockMovementWriter(),
                new SaleReversalWriter(factory),
                new SalesSyncOutboxRepository(
                    factory,
                    SaleRepository.SalesSyncInProgressLeaseMilliseconds));
        }

        public Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines) =>
            _writer.InsertSaleAsync(sale, lines);

        public Task MarkPdfPrintedAsync(long saleId) => _writer.MarkPdfPrintedAsync(saleId);

        public Task<long> InsertRefundSaleAsync(
            RefundCreateRequest request,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor) =>
            _writer.InsertRefundSaleAsync(
                request,
                totalMinor,
                paidCashMinor,
                paidCardMinor,
                changeMinor);

        public Task<long> InsertRefundSaleAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RefundCreateRequest request,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor) =>
            _writer.InsertRefundSaleAsync(
                conn,
                tx,
                request,
                totalMinor,
                paidCashMinor,
                paidCardMinor,
                changeMinor);

        public Task<long> InsertRefundOrVoidAsync(
            Sale refundSale,
            IReadOnlyList<SaleLine> refundLines,
            long? originalSaleIdToMarkVoided,
            string auditAction,
            Func<long, string> auditDetailsFactory) =>
            _writer.InsertRefundOrVoidAsync(
                refundSale,
                refundLines,
                originalSaleIdToMarkVoided,
                auditAction,
                auditDetailsFactory);

        public Task InsertSaleLinesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<SaleLine> lines) =>
            _writer.InsertSaleLinesAsync(conn, tx, lines);

        public Task<string> EnsureClientSaleIdAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId) =>
            _writer.EnsureClientSaleIdAsync(conn, tx, saleId);
    }

    private sealed class FacadeTransactionSurface : ITransactionSurface
    {
        private readonly SaleRepository _repository;

        public FacadeTransactionSurface(SqliteConnectionFactory factory)
        {
            _repository = new SaleRepository(factory);
        }

        public Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines) =>
            _repository.InsertSaleAsync(sale, lines);

        public Task MarkPdfPrintedAsync(long saleId) => _repository.MarkPdfPrintedAsync(saleId);

        public Task<long> InsertRefundSaleAsync(
            RefundCreateRequest request,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor) =>
            _repository.InsertRefundSaleAsync(
                request,
                totalMinor,
                paidCashMinor,
                paidCardMinor,
                changeMinor);

        public Task<long> InsertRefundSaleAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RefundCreateRequest request,
            long totalMinor,
            long paidCashMinor,
            long paidCardMinor,
            long changeMinor) =>
            _repository.InsertRefundSaleAsync(
                conn,
                tx,
                request,
                totalMinor,
                paidCashMinor,
                paidCardMinor,
                changeMinor);

        public Task<long> InsertRefundOrVoidAsync(
            Sale refundSale,
            IReadOnlyList<SaleLine> refundLines,
            long? originalSaleIdToMarkVoided,
            string auditAction,
            Func<long, string> auditDetailsFactory) =>
            _repository.InsertRefundOrVoidAsync(
                refundSale,
                refundLines,
                originalSaleIdToMarkVoided,
                auditAction,
                auditDetailsFactory);

        public Task InsertSaleLinesAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<SaleLine> lines) =>
            _repository.InsertSaleLinesAsync(conn, tx, lines);

        public Task<string> EnsureClientSaleIdAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long saleId) =>
            _repository.EnsureClientSaleIdAsync(conn, tx, saleId);
    }

    private sealed class FullSaleSnapshot
    {
        public IReadOnlyList<SaleLineRow> Lines { get; set; } = Array.Empty<SaleLineRow>();
        public IReadOnlyList<StockMovementRow> Movements { get; set; } = Array.Empty<StockMovementRow>();
        public OutboxRow Outbox { get; set; } = null!;
        public SaleRow Sale { get; set; } = null!;
        public long StockQty { get; set; }
    }

    private sealed class VoidSnapshot
    {
        public AuditRow Audit { get; set; } = null!;
        public IReadOnlyList<StockMovementRow> Movements { get; set; } = Array.Empty<StockMovementRow>();
        public long? OriginalVoidedAt { get; set; }
        public long? OriginalVoidedBySaleId { get; set; }
        public OutboxRow Outbox { get; set; } = null!;
        public long StockQty { get; set; }
        public SaleRow VoidSale { get; set; } = null!;
    }

    private sealed class PdfSnapshot
    {
        public PdfSaleRow Control { get; set; } = null!;
        public PdfSaleRow Target { get; set; } = null!;
    }

    private sealed class SaleRow
    {
        public long Id { get; set; }
        public string ClientSaleId { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public int Kind { get; set; }
        public long? RelatedSaleId { get; set; }
        public long Total { get; set; }
        public long PaidCash { get; set; }
        public long PaidCard { get; set; }
        public long Change { get; set; }
        public string SyncStatus { get; set; } = string.Empty;
        public bool PdfPrinted { get; set; }
    }

    private sealed class SaleLineRow
    {
        public long Id { get; set; }
        public long SaleId { get; set; }
        public long? ProductId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal { get; set; }
        public long? RelatedOriginalLineId { get; set; }
    }

    private sealed class StockMovementRow
    {
        public string MovementKey { get; set; } = string.Empty;
        public long SaleId { get; set; }
        public long? SaleLineId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public long QuantityDelta { get; set; }
        public string MovementKind { get; set; } = string.Empty;
    }

    private sealed class OutboxRow
    {
        public long SaleId { get; set; }
        public string ClientSaleId { get; set; } = string.Empty;
        public string ClientBatchId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string OperationType { get; set; } = string.Empty;
        public string OriginShopId { get; set; } = string.Empty;
        public string OriginShopCode { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private sealed class AuditRow
    {
        public string Action { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    private sealed class VoidedSaleRow
    {
        public long? VoidedAt { get; set; }
        public long? VoidedBySaleId { get; set; }
    }

    private sealed class PdfSaleRow
    {
        public string Code { get; set; } = string.Empty;
        public long Total { get; set; }
        public string SyncStatus { get; set; } = string.Empty;
        public bool PdfPrinted { get; set; }
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "win7pos-sale-transaction-writer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
