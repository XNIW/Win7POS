using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Products;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class ProductLiteralSearchTests
{
    [TestMethod]
    [DataRow("70%", "Alcool 70%", "Alcool 700")]
    [DataRow("ART_01", "ART_01 article", "ARTX01 article")]
    [DataRow("a\\b", "a\\b article", "ab article")]
    [DataRow("a!b", "a!b article", "ab article")]
    [DataRow("[a]", "Item [a]", "Item a")]
    [DataRow("l'acqua", "l'acqua fresca", "acqua fresca")]
    [DataRow("two words", "two words here", "two other words")]
    [DataRow("中文", "中文商品", "商品")]
    public async Task SearchCountAndPaging_TreatInputLiterally(string query, string matching, string other)
    {
        var root = Path.Combine(Path.GetTempPath(), "Win7POS-Literal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
        try
        {
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            using (var connection = factory.Open())
                connection.Execute("INSERT INTO products(barcode,name,unitPrice) VALUES(@barcode,@name,1000)",
                    new[] { new { barcode = "001", name = matching }, new { barcode = "002", name = other },
                        new { barcode = query, name = "exact barcode precedence" } });
            var repository = new ProductRepository(factory);
            CollectionAssert.AreEqual(new[] { query, "001" }, (await repository.SearchAsync(query, 20)).Select(x => x.Barcode).ToArray());
            CollectionAssert.AreEqual(new[] { query, "001" }, (await repository.SearchDetailsAsync(query, 20)).Select(x => x.Barcode).ToArray());
            Assert.AreEqual(2, await repository.CountDetailsAsync(query));
            var filter = new ProductPageFilter(query, null, null, 1, 1);
            var paging = new ProductPagingCoordinator();
            var page = await repository.SearchDetailsPageAsync(filter, paging.Plan(filter, 1));
            Assert.AreEqual(2, page.TotalCount);
            Assert.AreEqual(query, page.Items.Single().Barcode);
            Assert.AreEqual(0, await repository.CountDetailsAsync("no match"));
        }
        finally
        {
            SqliteConnectionFactory.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
