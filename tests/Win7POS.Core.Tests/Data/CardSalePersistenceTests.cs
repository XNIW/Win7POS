using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CardSalePersistenceTests
{
    [TestMethod]
    public async Task CardOnlySale_ReopenPreservesPaymentAndCanonicalOutboxEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "Win7POS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));

        try
        {
            DbInitializer.EnsureCreated(options);
            var initialFactory = new SqliteConnectionFactory(options);
            await new ShopOfficialSnapshotRepository(initialFactory).SaveAsync(new OfficialShopSnapshot
            {
                ShopId = "shop-card-test",
                ShopCode = "SHOP-CARD-TEST",
                ShopName = "Card test shop",
                Source = "test"
            });

            const long cardAmount = 2345;
            var saleId = await new SaleRepository(initialFactory).InsertSaleAsync(
                new Sale
                {
                    Code = "CARD-ONLY-REOPEN",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Kind = (int)SaleKind.Sale,
                    Total = cardAmount,
                    PaidCash = 0,
                    PaidCard = cardAmount,
                    Change = 0,
                    PdfPrinted = false
                },
                new[]
                {
                    new SaleLine
                    {
                        Barcode = "CARD-TEST-001",
                        Name = "Card-only test product",
                        Quantity = 1,
                        UnitPrice = cardAmount
                    }
                });

            SqliteConnection.ClearAllPools();
            var reopenedFactory = new SqliteConnectionFactory(options);
            var reopenedSales = new SaleRepository(reopenedFactory);
            var persistedSale = await reopenedSales.GetByIdAsync(saleId);
            Assert.IsNotNull(persistedSale);
            Assert.AreEqual(0L, persistedSale.PaidCash);
            Assert.AreEqual(cardAmount, persistedSale.PaidCard);
            Assert.IsFalse(persistedSale.PdfPrinted);

            var outbox = (await reopenedSales.GetPendingSalesSyncOutboxAsync(
                10,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000)).Single();
            Assert.AreEqual(saleId, outbox.SaleId);
            Assert.AreEqual("pending", outbox.Status);
            Assert.AreEqual(0, outbox.AttemptCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(outbox.PayloadJson));
            Assert.AreEqual(
                PosSalesSyncRequestBuilder.Sha256Hex(outbox.PayloadJson),
                outbox.PayloadHash,
                ignoreCase: true);

            var request = PosSalesSyncRequestBuilder.DeserializeCanonical(outbox.PayloadJson);
            Assert.IsNotNull(request);
            var requestSale = request.Sales.Single();
            var payment = requestSale.Payments.Single();
            Assert.AreEqual(PosOnlineContract.PaymentCard, payment.Method);
            Assert.AreEqual(PosOnlineContract.PaymentCard, payment.ClientPaymentId);
            Assert.AreEqual(cardAmount, payment.AmountClp);
            Assert.AreEqual(0L, payment.ChangeClp);
            Assert.AreEqual(cardAmount, requestSale.Amounts.PaidClp);
            Assert.AreEqual("not_printed_card_policy", requestSale.Fiscal.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // Best-effort test cleanup; assertions above are the regression gate.
            }
        }
    }
}
