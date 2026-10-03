using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CsvFractionalStockCloseoutTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CsvRenameAndExplicitQuantityPreserveFractionAcrossReopen(bool transactionAdapter)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Win7POS.CsvStock." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = PosDbOptions.ForPath(Path.Combine(directory, "pos.db"));
        DbInitializer.EnsureCreated(options);
        var factory = new SqliteConnectionFactory(options);
        try
        {
            using (var connection = factory.Open())
                await connection.ExecuteAsync("INSERT INTO products(barcode,name,unitPrice) VALUES('00a B条码','Original',1500); INSERT INTO product_meta(barcode,stock_qty) VALUES('00a B条码',1.234);");
            var rows = new[] { CsvImportParser.Parse("00a B条码;Renamed;1500;300").Rows.Single(), CsvImportParser.Parse("00a B条码;Renamed again;1500;300;2.345").Rows.Single() };
            foreach (var row in rows)
            {
                if (transactionAdapter)
                {
                    using var connection = factory.Open();
                    using var transaction = connection.BeginTransaction();
                    await new ProductUpserterAdapter(connection, transaction).UpsertAsync(row);
                    transaction.Commit();
                }
                else await new ProductUpserterAdapter(new ProductRepository(factory)).UpsertAsync(row);
                SqliteConnection.ClearAllPools();
                var reopened = new SqliteConnectionFactory(options);
                var product = await new ProductRepository(reopened).GetDetailsByBarcodeAsync(row.Barcode);
                Assert.AreEqual(row.Stock ?? 1.234m, product.StockQty);
                Assert.AreEqual(row.Name, product.Name);
                Assert.AreEqual("00a B条码", product.Barcode);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
