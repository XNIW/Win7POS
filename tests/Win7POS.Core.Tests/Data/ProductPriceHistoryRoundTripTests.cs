using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.Sqlite;
using Win7POS.Core.ImportDb;
using Win7POS.Data;
using Win7POS.Data.ImportDb;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ProductPriceHistoryRoundTripTests
{
    [TestMethod]
    [DataRow(0L)]
    [DataRow(2147483647L)]
    [DataRow(2147483648L)]
    [DataRow(4294967296L)]
    [DataRow(long.MaxValue)]
    public async Task WorkbookExportAndBothReaders_PreserveInt64ProductAndHistory(long price)
    {
        var path = Path.Combine(Path.GetTempPath(), "price-history-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            ProductDbExcelWriter.Write(path, new ProductDbWorkbook
            {
                Products = new[] { new ProductRow { Barcode = "HISTORY64", Name = "Exact price", RetailPrice = price, PurchasePrice = 100,
                    SupplierName = "Vendor 123", CategoryName = "Food", SupplierId = 1, CategoryId = 1 } },
                Suppliers = new[] { new SupplierRow { Id = 1, Name = "Vendor 123" } },
                Categories = new[] { new CategoryRow { Id = 1, Name = "Food" } },
                PriceHistory = new[]
                {
                    new PriceHistoryRow { ProductBarcode = "HISTORY64", Timestamp = "2026-10-08 10:00:00", Type = "retail", OldPrice = null, NewPrice = price, Source = "IMPORT" },
                    new PriceHistoryRow { ProductBarcode = "HISTORY64", Timestamp = "2026-10-08 10:00:01", Type = "retail", OldPrice = long.MaxValue, NewPrice = price, Source = "IMPORT" }
                }
            });
            foreach (var workbook in new[] { ProductDbExcelReader.Read(path), ProductDbExcelReader.ReadWithExcelDataReader(path) })
            {
                Assert.AreEqual(price, workbook.Products.Single().RetailPrice);
                Assert.AreEqual("Vendor 123", workbook.Products.Single().SupplierName);
                Assert.AreEqual("Food", workbook.Products.Single().CategoryName);
                Assert.AreEqual(0L, workbook.Products.Single().RetailOld);
                Assert.AreEqual(0, workbook.Products.Single().PurchaseOld);
                Assert.IsNull(workbook.PriceHistory[0].OldPrice);
                Assert.AreEqual(price, workbook.PriceHistory[0].NewPrice);
                Assert.AreEqual(long.MaxValue, workbook.PriceHistory[1].OldPrice);
                Assert.AreEqual(price, workbook.PriceHistory[1].NewPrice);
                var dbPath = path + Guid.NewGuid().ToString("N") + ".db";
                try
                {
                    var options = PosDbOptions.ForPath(dbPath);
                    DbInitializer.EnsureCreated(options);
                    var factory = new SqliteConnectionFactory(options);
                    var result = await new ProductDbImporter(factory).ImportAsync(workbook, false);
                    Assert.AreEqual(0, result.Errors.Count, string.Join(";", result.Errors));
                    var products = new ProductRepository(factory);
                    Assert.AreEqual(price, (await products.GetByBarcodeAsync("HISTORY64"))!.UnitPrice);
                    var history = await products.GetPriceHistoryByBarcodeAsync("HISTORY64");
                    Assert.AreEqual(2, history.Count);
                    Assert.IsTrue(history.Any(row => row.OldPrice == long.MaxValue && row.NewPrice == price));
                    Assert.IsTrue(history.Any(row => !row.OldPrice.HasValue && row.NewPrice == price));
                }
                finally { SqliteConnection.ClearAllPools(); if (File.Exists(dbPath)) File.Delete(dbPath); }
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
