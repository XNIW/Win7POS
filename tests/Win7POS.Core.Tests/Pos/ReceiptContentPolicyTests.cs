using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class ReceiptContentPolicyTests
{
    [TestMethod]
    [DataRow(nameof(ReceiptShopMetadata.ShopName), ReceiptShopMetadataPolicy.MaxShopNameCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.BusinessAddress), ReceiptShopMetadataPolicy.MaxAddressCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.BusinessCity), ReceiptShopMetadataPolicy.MaxCityCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.CompanyRut), ReceiptShopMetadataPolicy.MaxRutCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.BusinessGiro), ReceiptShopMetadataPolicy.MaxGiroCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.LegalRepresentativeRut), ReceiptShopMetadataPolicy.MaxRutCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.BusinessPhone), ReceiptShopMetadataPolicy.MaxPhoneCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.Footer), ReceiptShopMetadataPolicy.MaxFooterCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.ShopId), ReceiptShopMetadataPolicy.MaxShopIdCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.ShopCode), ReceiptShopMetadataPolicy.MaxShopCodeCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.ShopStatus), ReceiptShopMetadataPolicy.MaxShopStatusCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.Source), ReceiptShopMetadataPolicy.MaxSourceCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.SyncedAtUtc), ReceiptShopMetadataPolicy.MaxTimestampCharacters)]
    [DataRow(nameof(ReceiptShopMetadata.UpdatedAt), ReceiptShopMetadataPolicy.MaxTimestampCharacters)]
    public void ShopMetadata_FieldMaximumIsAccepted_AndMaximumPlusOneIsRejected(
        string propertyName,
        int maximum)
    {
        var property = typeof(ReceiptShopMetadata).GetProperty(propertyName)!;
        var metadata = new ReceiptShopMetadata();
        property.SetValue(metadata, new string('x', maximum));
        ReceiptShopMetadataPolicy.EnsureValidSnapshot(metadata);

        property.SetValue(metadata, new string('x', maximum + 1));
        var exception = Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
            ReceiptShopMetadataPolicy.EnsureValidSnapshot(metadata));
        Assert.AreEqual("shop_metadata_field_too_large", exception.Code);
    }

    [TestMethod]
    public void ShopMetadata_VisibleAggregateAndUnicodeFailClosedWithoutTruncation()
    {
        var exact = new ReceiptShopMetadata
        {
            ShopName = new string('n', 128),
            BusinessAddress = new string('a', 256),
            BusinessCity = new string('c', 128),
            BusinessGiro = new string('g', 256),
            Footer = new string('f', 256)
        };
        ReceiptShopMetadataPolicy.EnsureValidSnapshot(exact);

        exact.BusinessPhone = "x";
        var aggregate = Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
            ReceiptShopMetadataPolicy.EnsureValidSnapshot(exact));
        Assert.AreEqual("receipt_shop_metadata_budget_exceeded", aggregate.Code);

        var control = new ReceiptShopMetadata { ShopName = "safe\nforged" };
        Assert.AreEqual(
            "shop_metadata_control_character",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                ReceiptShopMetadataPolicy.EnsureValidSnapshot(control)).Code);

        var malformed = new ReceiptShopMetadata { ShopName = "bad\uD800" };
        Assert.AreEqual(
            "shop_metadata_invalid_unicode",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                ReceiptShopMetadataPolicy.EnsureValidSnapshot(malformed)).Code);
    }

    [TestMethod]
    public void FourHugeRemoteFields_AreRejectedBeforeReceiptFormatting()
    {
        var huge = new string('x', 500_000);
        var shop = new ReceiptShopInfo
        {
            Name = huge,
            Address = huge,
            BusinessGiro = huge,
            Footer = huge
        };

        var exception = Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
            FiscalBoletaTextRenderer.Render(shop, 0, 1, 1000, 190, 42));
        Assert.AreEqual("shop_metadata_field_too_large", exception.Code);
    }

    [TestMethod]
    public void SnapshotJsonAndDocumentBudgets_AcceptBoundaryAndRejectOverflow()
    {
        ReceiptDocumentPolicy.EnsureValidSnapshotJson(
            new string('x', ReceiptDocumentPolicy.MaxSnapshotJsonCharacters));
        Assert.AreEqual(
            "receipt_shop_snapshot_too_large",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                ReceiptDocumentPolicy.EnsureValidSnapshotJson(
                    new string('x', ReceiptDocumentPolicy.MaxSnapshotJsonCharacters + 1))).Code);

        ReceiptDocumentPolicy.EnsureValidDocument(
            new string('x', ReceiptDocumentPolicy.MaxCharactersPerLine));
        Assert.AreEqual(
            "receipt_document_line_too_large",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                ReceiptDocumentPolicy.EnsureValidDocument(
                    new string('x', ReceiptDocumentPolicy.MaxCharactersPerLine + 1))).Code);

        var exactLines = string.Join(
            "\n",
            Enumerable.Repeat("x", ReceiptDocumentPolicy.MaxLogicalLines));
        ReceiptDocumentPolicy.EnsureValidDocument(exactLines);
        Assert.AreEqual(
            "receipt_document_too_many_lines",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                ReceiptDocumentPolicy.EnsureValidDocument(exactLines + "\nx")).Code);

        ReceiptDocumentPolicy.EnsureValidDocument(
            BuildBoundedDocument(ReceiptDocumentPolicy.MaxDocumentCharacters));
        Assert.AreEqual(
            "receipt_document_too_large",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                ReceiptDocumentPolicy.EnsureValidDocument(
                    BuildBoundedDocument(ReceiptDocumentPolicy.MaxDocumentCharacters + 1))).Code);
    }

    [TestMethod]
    public void SaleLinePreflight_AcceptsBoundariesAndRejectsAmplificationOrUnsafeText()
    {
        SalesReceiptContentPolicy.EnsureValidLines(
            Enumerable.Range(0, SalesReceiptContentPolicy.MaxSaleLines)
                .Select(_ => new SaleLine { Name = string.Empty })
                .ToArray());
        Assert.AreEqual(
            "receipt_sale_line_count_exceeded",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptContentPolicy.EnsureValidLines(
                    Enumerable.Range(0, SalesReceiptContentPolicy.MaxSaleLines + 1)
                        .Select(_ => new SaleLine { Name = string.Empty })
                        .ToArray())).Code);

        SalesReceiptContentPolicy.EnsureValidLines(new[]
        {
            new SaleLine
            {
                Barcode = "BOUNDARY",
                Name = new string('n', SalesReceiptContentPolicy.MaxSaleLineNameCharacters)
            }
        });
        Assert.AreEqual(
            "receipt_sale_line_field_too_large",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptContentPolicy.EnsureValidLines(new[]
                {
                    new SaleLine
                    {
                        Barcode = "OVERSIZED",
                        Name = new string(
                            'n',
                            SalesReceiptContentPolicy.MaxSaleLineNameCharacters + 1)
                    }
                })).Code);

        var exactAggregate = Enumerable.Range(
                0,
                SalesReceiptContentPolicy.MaxAggregateLineNameCharacters /
                SalesReceiptContentPolicy.MaxSaleLineNameCharacters)
            .Select(index => new SaleLine
            {
                Barcode = "AGGREGATE-" + index,
                Name = new string('a', SalesReceiptContentPolicy.MaxSaleLineNameCharacters)
            })
            .ToArray();
        SalesReceiptContentPolicy.EnsureValidLines(exactAggregate);
        Assert.AreEqual(
            "receipt_sale_line_budget_exceeded",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptContentPolicy.EnsureValidLines(
                    exactAggregate.Concat(new[]
                    {
                        new SaleLine { Barcode = "AGGREGATE-OVER", Name = "x" }
                    }).ToArray())).Code);

        var encodedBoundary = Enumerable.Range(0, 10)
            .Select(index => new SaleLine
            {
                Barcode = "UTF8-" + index,
                Name = new string('\u754C', SalesReceiptContentPolicy.MaxSaleLineNameCharacters)
            })
            .ToArray();
        SalesReceiptContentPolicy.EnsureValidLines(encodedBoundary);
        Assert.AreEqual(
            "receipt_sale_line_budget_exceeded",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptContentPolicy.EnsureValidLines(
                    encodedBoundary.Concat(new[]
                    {
                        new SaleLine
                        {
                            Barcode = "UTF8-OVER",
                            Name = new string(
                                '\u754C',
                                SalesReceiptContentPolicy.MaxSaleLineNameCharacters)
                        }
                    }).ToArray())).Code);

        SalesReceiptContentPolicy.EnsureValidLines(new[]
        {
            new SaleLine
            {
                Barcode = new string('b', SalesReceiptContentPolicy.MaxSaleLineBarcodeCharacters),
                Name = "Barcode boundary"
            }
        });
        Assert.AreEqual(
            "receipt_sale_line_field_too_large",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptContentPolicy.EnsureValidLines(new[]
                {
                    new SaleLine
                    {
                        Barcode = new string(
                            'b',
                            SalesReceiptContentPolicy.MaxSaleLineBarcodeCharacters + 1),
                        Name = "Oversized barcode"
                    }
                })).Code);

        Assert.AreEqual(
            "receipt_content_control_character",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptContentPolicy.EnsureValidLines(new[]
                {
                    new SaleLine { Barcode = "CONTROL", Name = "forged\nline" }
                })).Code);
        Assert.AreEqual(
            "receipt_content_invalid_unicode",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptContentPolicy.EnsureValidLines(new[]
                {
                    new SaleLine { Barcode = "UNICODE", Name = "bad\uD800" }
                })).Code);
    }

    [TestMethod]
    public void SalesReceiptRenderModel_RejectsUnsafeLinesBeforeFormatting()
    {
        var sale = new Sale
        {
            Code = "RENDER-PREFLIGHT",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Total = 100
        };
        var oversized = new SaleLine
        {
            Barcode = "RENDER-OVERSIZED",
            Name = new string('x', SalesReceiptContentPolicy.MaxSaleLineNameCharacters + 1),
            Quantity = 1,
            UnitPrice = 100,
            LineTotal = 100
        };

        Assert.AreEqual(
            "receipt_sale_line_field_too_large",
            Assert.ThrowsExactly<ReceiptContentValidationException>(() =>
                SalesReceiptRenderModel.Create(sale, new[] { oversized })).Code);
    }

    [TestMethod]
    public async Task OfficialSnapshot_InvalidReplacementPreservesLastKnownGood()
    {
        using var db = TestDb.Create();
        var repository = new ShopOfficialSnapshotRepository(db.Factory);
        await repository.SaveAsync(new OfficialShopSnapshot
        {
            ShopId = "shop-valid",
            ShopCode = "VALID",
            ShopName = "Valid shop",
            Source = "test"
        });

        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.SaveAsync(new OfficialShopSnapshot
            {
                ShopId = "shop-invalid",
                ShopCode = "INVALID",
                ShopName = new string('x', ReceiptShopMetadataPolicy.MaxShopNameCharacters + 1),
                Source = "test"
            }));

        var retained = await repository.GetAsync();
        Assert.AreEqual("shop-valid", retained.ShopId);
        Assert.AreEqual("Valid shop", retained.ShopName);
    }

    [TestMethod]
    public async Task OversizedSaleAndRefundSnapshots_AreRejectedBeforeDatabaseMutation()
    {
        using var db = TestDb.Create();
        var oversized = new string('x', ReceiptDocumentPolicy.MaxSnapshotJsonCharacters + 1);
        var repository = new SaleRepository(db.Factory);
        var sale = new Sale
        {
            Code = "OVERSIZED-SALE",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = (int)SaleKind.Sale,
            Total = 1000,
            PaidCash = 1000,
            ReceiptShopSnapshotJson = oversized
        };
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.InsertSaleAsync(sale, Array.Empty<SaleLine>()));

        var refund = new Sale
        {
            Code = "OVERSIZED-REFUND",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = (int)SaleKind.Refund,
            Total = -1000,
            PaidCash = -1000,
            ReceiptShopSnapshotJson = oversized
        };
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.InsertRefundOrVoidAsync(
                refund,
                Array.Empty<SaleLine>(),
                null,
                string.Empty,
                null));

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sale_lines;"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales_sync_outbox;"));
    }

    [TestMethod]
    public async Task UnsafeSaleAndRefundLines_AreRejectedBeforeDatabaseMutation()
    {
        using var db = TestDb.Create();
        var repository = new SaleRepository(db.Factory);
        var invalidLine = new SaleLine
        {
            Barcode = "UNSAFE-LINE",
            Name = new string('x', SalesReceiptContentPolicy.MaxSaleLineNameCharacters + 1),
            Quantity = 1,
            UnitPrice = 100,
            LineTotal = 100
        };
        var sale = new Sale
        {
            Code = "UNSAFE-SALE",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = (int)SaleKind.Sale,
            Total = 100,
            PaidCash = 100
        };
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.InsertSaleAsync(sale, new[] { invalidLine }));

        var refund = new Sale
        {
            Code = "UNSAFE-REFUND",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = (int)SaleKind.Refund,
            Total = -100,
            PaidCash = -100
        };
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.InsertRefundOrVoidAsync(
                refund,
                new[] { invalidLine },
                null,
                string.Empty,
                null));

        using (var conn = db.Factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
                repository.InsertSaleLinesAsync(conn, tx, new[] { invalidLine }));
            tx.Rollback();
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sale_lines;"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales_sync_outbox;"));
    }

    [TestMethod]
    public async Task ProductIdentityPolicy_RejectsProgrammaticBypassBeforeDatabaseMutation()
    {
        using var db = TestDb.Create();
        var repository = new ProductRepository(db.Factory);
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.UpsertAsync(new Product
            {
                Barcode = new string(
                    'b',
                    SalesReceiptContentPolicy.MaxSaleLineBarcodeCharacters + 1),
                Name = "Unsafe barcode",
                UnitPrice = 100
            }));
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.UpsertAsync(new Product
            {
                Barcode = "UNSAFE-NAME",
                Name = "bad\uD800",
                UnitPrice = 100
            }));

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM products;"));
    }

    [TestMethod]
    public async Task LegacyRefundIngress_RejectsUnsafeReasonBeforeMutation()
    {
        using var db = TestDb.Create();
        var repository = new SaleRepository(db.Factory);
        await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            repository.InsertRefundSaleAsync(
                new RefundCreateRequest
                {
                    OriginalSaleId = 1,
                    Reason = new string(
                        'r',
                        SalesReceiptContentPolicy.MaxSaleReasonCharacters + 1)
                },
                -100,
                -100,
                0,
                0));

        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;"));
    }

    [TestMethod]
    public async Task RepeatedSaleLineBatches_CannotExceedCumulativeBudget()
    {
        using var db = TestDb.Create();
        var repository = new SaleRepository(db.Factory);
        long saleId;
        using (var conn = db.Factory.Open())
        {
            saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, total, paidCash, paidCard, change)
VALUES('BATCH-BOUNDARY', 1, 2, 0, 0, 0, 0);
SELECT last_insert_rowid();");
            using (var firstTx = conn.BeginTransaction())
            {
                var firstBatch = Enumerable.Range(0, 300)
                    .Select(index => new SaleLine
                    {
                        Barcode = "FIRST-" + index,
                        Name = string.Empty,
                        SaleId = saleId
                    })
                    .ToArray();
                await repository.InsertSaleLinesAsync(conn, firstTx, firstBatch);
                firstTx.Commit();
            }

            using (var secondTx = conn.BeginTransaction())
            {
                var overflowBatch = Enumerable.Range(0, 213)
                    .Select(index => new SaleLine
                    {
                        Barcode = "SECOND-" + index,
                        Name = string.Empty,
                        SaleId = saleId
                    })
                    .ToArray();
                await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
                    repository.InsertSaleLinesAsync(conn, secondTx, overflowBatch));
                secondTx.Rollback();
            }
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual(300L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM sale_lines WHERE saleId = @saleId;",
            new { saleId }));
    }

    [TestMethod]
    public async Task DirectSaleLineInsert_RequiresTheOwningTransaction()
    {
        using var db = TestDb.Create();
        var repository = new SaleRepository(db.Factory);
        var lines = new[]
        {
            new SaleLine
            {
                Barcode = "TX-BOUNDARY",
                Name = "Transaction boundary",
                SaleId = 1
            }
        };

        using var conn = db.Factory.Open();
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() =>
            repository.InsertSaleLinesAsync(conn, null, lines));

        using (var otherConnection = db.Factory.Open())
        using (var otherTransaction = otherConnection.BeginTransaction())
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                repository.InsertSaleLinesAsync(conn, otherTransaction, lines));
            otherTransaction.Rollback();
        }

        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM sale_lines;"));
    }

    [TestMethod]
    public async Task HistoricalCorruptSaleLines_AreRejectedBeforeRowMaterialization()
    {
        using var db = TestDb.Create();
        long saleId;
        using (var seed = db.Factory.Open())
        {
            saleId = await seed.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, total, paidCash, paidCard, change)
VALUES('CORRUPT-HISTORY', 1, 2, 0, 0, 0, 0);
SELECT last_insert_rowid();");
            await seed.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, 'CORRUPT-LINE', @name, 1, 0, 0);",
                new { saleId, name = new string('x', 500_000) });
        }

        var exception = await Assert.ThrowsExactlyAsync<ReceiptContentValidationException>(() =>
            new SaleRepository(db.Factory).GetLinesBySaleIdAsync(saleId));
        Assert.AreEqual("receipt_sale_line_field_too_large", exception.Code);
    }

    private static string BuildBoundedDocument(int characters)
    {
        var builder = new System.Text.StringBuilder(characters);
        while (builder.Length < characters)
        {
            var remaining = characters - builder.Length;
            var chunk = Math.Min(ReceiptDocumentPolicy.MaxCharactersPerLine, remaining);
            builder.Append('x', chunk);
            if (builder.Length < characters) builder.Append('\n');
        }
        return builder.ToString();
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
                "Win7POS.ReceiptPolicy",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
