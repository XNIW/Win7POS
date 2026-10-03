using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CsvAbsentStockConcurrencyTests
{
    [TestMethod]
    public async Task OmittedStockRenamePreservesMutationCommittedWhileWaitingForGate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Win7POS.CsvStockRace." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = PosDbOptions.ForPath(Path.Combine(directory, "pos.db"));
        DbInitializer.EnsureCreated(options);
        var factory = new SqliteConnectionFactory(options);
        Task<UpsertOutcome>? rename = null;
        var gateHeld = false;
        try
        {
            using (var connection = factory.Open())
                await connection.ExecuteAsync("INSERT INTO products(barcode,name,unitPrice) VALUES('STOCK-RACE','Original',1500); INSERT INTO product_meta(barcode,stock_qty) VALUES('STOCK-RACE',1.234);");

            await CatalogMutationGate.Instance.WaitAsync();
            gateHeld = true;
            var row = new ImportRow { Barcode = "STOCK-RACE", Name = "Renamed", UnitPrice = 1500, Stock = null };
            // Microsoft.Data.Sqlite executes its reads synchronously; the returned
            // incomplete operation is waiting for the already-held mutation gate.
            rename = new ProductUpserterAdapter(new ProductRepository(factory)).UpsertAsync(row);
            Assert.IsFalse(rename.IsCompleted, "The rename must wait for the preceding mutation.");

            // This fixture owns the gate and completes the preceding stock mutation
            // before handing it to the waiting adapter, without timing-based sleeps.
            using (var connection = factory.Open())
                await connection.ExecuteAsync("UPDATE product_meta SET stock_qty=3.456 WHERE barcode='STOCK-RACE';");
            CatalogMutationGate.Instance.Release();
            gateHeld = false;
            Assert.AreEqual(UpsertOutcome.Updated, await rename.WaitAsync(TimeSpan.FromSeconds(10)));

            SqliteConnection.ClearAllPools();
            var reopened = new ProductRepository(new SqliteConnectionFactory(options));
            var product = await reopened.GetDetailsByBarcodeAsync(row.Barcode);
            Assert.AreEqual("Renamed", product.Name);
            Assert.AreEqual(3.456m, product.StockQty);
        }
        finally
        {
            if (gateHeld) CatalogMutationGate.Instance.Release();
            if (rename != null) await rename.WaitAsync(TimeSpan.FromSeconds(10));
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
