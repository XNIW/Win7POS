using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SupplierImportDataTests
{
    [TestMethod]
    public async Task ProductRepository_UpsertAsync_ReactivatesSoftDeletedBarcode()
    {
        using var db = TestDb.Create();
        var products = new ProductRepository(db.Factory);
        var id = await products.UpsertAsync(new Product { Barcode = "SOFT-001", Name = "Old", UnitPrice = 100 });
        Assert.IsTrue(await products.DeleteByBarcodeAsync("SOFT-001"));
        Assert.IsNull(await products.GetByBarcodeAsync("SOFT-001"));

        var updatedId = await products.UpsertAsync(new Product { Barcode = "SOFT-001", Name = "New", UnitPrice = 200 });

        Assert.AreEqual(id, updatedId);
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM products WHERE barcode = 'SOFT-001'"));
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COALESCE(is_active, 1) FROM products WHERE barcode = 'SOFT-001'"));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_deleted_at, '') FROM products WHERE barcode = 'SOFT-001'"));
        var product = await products.GetByBarcodeAsync("SOFT-001");
        Assert.AreEqual("New", product!.Name);
        Assert.AreEqual(200, product.UnitPrice);
    }

    [TestMethod]
    public async Task SupplierExcelImportApplier_Apply_ReactivatesSoftDeletedExistingProduct()
    {
        using var db = TestDb.Create();
        var products = new ProductRepository(db.Factory);
        await products.UpsertProductAndMetaInTransactionAsync(
            new Product { Barcode = "SOFT-APPLY", Name = "Old", UnitPrice = 100 },
            "OLD",
            "Old second",
            50,
            null,
            "Old supplier",
            null,
            "Old category",
            1);
        Assert.IsTrue(await products.DeleteByBarcodeAsync("SOFT-APPLY"));

        var existing = await products.ListDetailsByBarcodesAsync(new[] { "SOFT-APPLY" });
        Assert.AreEqual(1, existing.Count);
        Assert.IsFalse(existing[0].IsActive);

        var row = new SupplierImportEditableRow
        {
            RowNumber = 2,
            Barcode = "SOFT-APPLY",
            ItemNumber = "NEW",
            ProductName = "Reactivated",
            SecondProductName = "New second",
            PurchasePrice = "80",
            RetailPrice = "180",
            Quantity = "3"
        };
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, existing);
        Assert.AreEqual(1, preview.UpdatedProducts.Count);
        Assert.AreEqual(0, preview.NewProducts.Count);

        var result = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { InsertNew = true });

        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(0, result.Inserted);
        Assert.AreEqual(1, result.Updated);
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COALESCE(is_active, 1) FROM products WHERE barcode = 'SOFT-APPLY'"));
        Assert.AreEqual("", await ScalarStringAsync(db.Factory, "SELECT COALESCE(remote_deleted_at, '') FROM products WHERE barcode = 'SOFT-APPLY'"));
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM products WHERE barcode = 'SOFT-APPLY'"));
        Assert.IsTrue(await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM product_price_history WHERE barcode = 'SOFT-APPLY'") >= 2);
    }

    [TestMethod]
    public async Task CatalogImportOutboxRepository_EnqueueAsync_DuplicateSameEntryReturnsSameId_ConflictFails()
    {
        using var db = TestDb.Create();
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var entry = BuildCatalogEntry("same", 1);

        var first = await repository.EnqueueAsync(entry);
        var second = await repository.EnqueueAsync(entry);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM catalog_import_outbox"));

        var conflict = BuildCatalogEntry("different", 2);
        conflict.ClientImportId = entry.ClientImportId;
        conflict.IdempotencyKey = entry.IdempotencyKey;
        await AssertThrowsInvalidOperationAsync(() => repository.EnqueueAsync(conflict));
    }

    private static CatalogImportOutboxEntry BuildCatalogEntry(string name, int rowNumber)
    {
        var request = new PosCatalogImportRequest
        {
            Batch = new PosCatalogImportBatchRequest
            {
                ClientImportId = "test-import-" + name,
                CreatedAt = "2026-01-01T00:00:00.0000000Z",
                IdempotencyKey = "test-import-" + name + ":pos-catalog-import-v1",
                PreviewFingerprint = "fingerprint-" + name,
                SourceFileName = "supplier.xlsx"
            },
            Items = new[]
            {
                new PosCatalogImportItemRequest
                {
                    Barcode = "CAT-" + rowNumber.ToString(CultureInfo.InvariantCulture),
                    ChangeKind = "new",
                    ClientItemId = "test-import-" + name + "-item",
                    Operation = "upsert_product",
                    ProductName = "Catalog " + name,
                    PurchasePrice = "100",
                    RetailPrice = "180",
                    RowNumber = rowNumber
                }
            },
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel",
            Summary = new PosCatalogImportSummaryRequest { NewProducts = 1 }
        };
        var payload = Serialize(request);
        return new CatalogImportOutboxEntry
        {
            ClientImportId = request.Batch.ClientImportId,
            CreatedAt = 1767225600000,
            IdempotencyKey = request.Batch.IdempotencyKey,
            PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(payload),
            PayloadJson = payload,
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel"
        };
    }

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task AssertThrowsInvalidOperationAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Fail("Expected InvalidOperationException.");
    }

    private static async Task<long> ScalarLongAsync(SqliteConnectionFactory factory, string sql)
    {
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnectionFactory factory, string sql)
    {
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            Factory = new SqliteConnectionFactory(PosDbOptions.ForPath(Path.Combine(root, "pos.db")));
            DbInitializer.EnsureCreated(PosDbOptions.ForPath(Path.Combine(root, "pos.db")));
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-tests-" + Guid.NewGuid().ToString("N"));
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
