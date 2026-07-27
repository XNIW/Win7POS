using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogDisplayRecoveryPolicyTests
{
    [TestMethod]
    public void SharedCatalogTextPolicyFixture_IsVendoredByteForByte()
    {
        var path = FindFixture("catalog-text-policy-v1.json");
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        var actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();

        Assert.AreEqual(
            "1cec15e9c623fb78ce7cfc27225e135fe5afea78be3b9ff1653369a0366ae9a6",
            actual);
    }

    [TestMethod]
    public void DisplayWhitespace_IsCanonicalized()
    {
        AssertRecovered("Coffee\nBeans", "Coffee Beans");
        AssertRecovered("Coffee\rBeans", "Coffee Beans");
        AssertRecovered("Coffee\r\nBeans", "Coffee Beans");
        AssertRecovered("Coffee\tBeans", "Coffee Beans");
        AssertRecovered("  Coffee\u00A0  Beans  ", "Coffee Beans");
    }

    [TestMethod]
    public void DisplayFormatAndControlCharacters_AreRemovedWithoutLeakingValues()
    {
        var result = CatalogDisplayRecoveryPolicy.RecoverDisplayText(
            "A\u200BB\uFEFFC\u202ED\u0001E\u0085F",
            512);

        Assert.IsTrue(result.IsUsable);
        Assert.AreEqual("ABCDEF", result.Value);
        Assert.AreEqual("catalog_display_text_normalized", result.Warnings[0].Code);
        CollectionAssert.Contains(
            result.Warnings.Select(warning => warning.Code).ToArray(),
            "catalog_display_text_control_removed");
        Assert.AreEqual(1, typeof(CatalogDataQualityWarning).GetProperties().Length);
    }

    [TestMethod]
    public void ValidInternationalUnicode_IsPreservedAndRecoveryIsIdempotent()
    {
        const string source = "咖啡 São José — 👩‍💻";
        var once = CatalogDisplayRecoveryPolicy.RecoverDisplayText(source, 512);
        var twice = CatalogDisplayRecoveryPolicy.RecoverDisplayText(once.Value, 512);

        Assert.AreEqual(source, once.Value);
        Assert.AreEqual(once.Value, twice.Value);
        Assert.AreEqual(0, twice.Warnings.Count);
    }

    [TestMethod]
    public void UnpairedSurrogates_AreReplacedRatherThanBlockingDisplayRecovery()
    {
        var high = CatalogDisplayRecoveryPolicy.RecoverDisplayText("A\uD800B", 512);
        var low = CatalogDisplayRecoveryPolicy.RecoverDisplayText("A\uDC00B", 512);

        Assert.AreEqual("A\uFFFDB", high.Value);
        Assert.AreEqual("A\uFFFDB", low.Value);
        CollectionAssert.Contains(high.Warnings.Select(warning => warning.Code).ToArray(),
            "catalog_display_text_replacement_used");
    }

    [TestMethod]
    public void OverLimitDisplayText_IsNotTruncatedAndUsesExistingProductFallback()
    {
        var assessment = CatalogDisplayRecoveryPolicy.Recover(Response(
            productName: new string('p', 513),
            secondName: "Second name",
            barcode: "BAR-1"));

        Assert.IsTrue(assessment.CanContinue);
        Assert.AreEqual(string.Empty, assessment.RecoveredResponse.Catalog.Products[0].ProductName);
        Assert.AreEqual("Second name", assessment.RecoveredResponse.Catalog.Products[0].SecondProductName);
        Assert.AreEqual(1, assessment.WarningSummary.ProductsAffected);
        Assert.IsTrue(assessment.WarningSummary.FallbackCount > 0);
        CollectionAssert.Contains(
            CatalogDisplayRecoveryPolicy.RecoverDisplayText(new string('p', 513), 512)
                .Warnings.Select(warning => warning.Code).ToArray(),
            "catalog_display_text_over_limit_fallback");
    }

    [TestMethod]
    public void InvalidPrimaryAndSecondNames_FallBackToBarcodeWithoutDroppingTheProduct()
    {
        var assessment = CatalogDisplayRecoveryPolicy.Recover(Response(
            productName: "\u202E",
            secondName: new string('s', 513),
            barcode: "BAR-1"));
        var recovered = assessment.RecoveredResponse.Catalog.Products[0];
        var batch = RemoteCatalogBatchMapper.BuildRemoteCatalogBatch(
            assessment.RecoveredResponse,
            authoritativeFullRefresh: false,
            stagePage: null);

        Assert.IsTrue(assessment.CanContinue);
        Assert.AreEqual(string.Empty, recovered.ProductName);
        Assert.AreEqual(string.Empty, recovered.SecondProductName);
        Assert.AreEqual("BAR-1", batch.Products[0].Name);
        Assert.IsTrue(assessment.WarningSummary.FallbackCount > 0);
        Assert.AreEqual(1, batch.Products.Count);
    }

    [TestMethod]
    public void InvalidSecondName_IsClearedWhileValidPrimaryNameIsRetained()
    {
        var assessment = CatalogDisplayRecoveryPolicy.Recover(Response(
            productName: "Primary name",
            secondName: "\u202E",
            barcode: "BAR-1"));

        Assert.IsTrue(assessment.CanContinue);
        Assert.AreEqual("Primary name", assessment.RecoveredResponse.Catalog.Products[0].ProductName);
        Assert.AreEqual(string.Empty, assessment.RecoveredResponse.Catalog.Products[0].SecondProductName);
        Assert.AreEqual(0, assessment.WarningSummary.FallbackCount);
    }

    [TestMethod]
    public void CategoryAndSupplierDisplayFallbacks_UseTheirExistingRemoteIds()
    {
        var response = Response("Product", "", "BAR-1");
        response.Catalog.Categories = new[]
        {
            new PosCatalogCategoryResponse { CategoryId = "category-1", Name = new string('c', 513) }
        };
        response.Catalog.Suppliers = new[]
        {
            new PosCatalogSupplierResponse { SupplierId = "supplier-1", Name = "\u202E" }
        };

        var assessment = CatalogDisplayRecoveryPolicy.Recover(response);

        Assert.AreEqual("category-1", assessment.RecoveredResponse.Catalog.Categories[0].Name);
        Assert.AreEqual("supplier-1", assessment.RecoveredResponse.Catalog.Suppliers[0].Name);
        Assert.IsTrue(assessment.WarningSummary.CategoriesAffected > 0);
        Assert.IsTrue(assessment.WarningSummary.SuppliersAffected > 0);
    }

    [TestMethod]
    public void BlankCategoryOrSupplierName_UsesItsRemoteIdAndRecordsFallbackWarning()
    {
        var response = Response("Product", "", "BAR-1");
        response.Catalog.Categories = new[]
        {
            new PosCatalogCategoryResponse { CategoryId = "category-1", Name = string.Empty }
        };
        response.Catalog.Suppliers = new[]
        {
            new PosCatalogSupplierResponse { SupplierId = "supplier-1", Name = string.Empty }
        };

        var assessment = CatalogDisplayRecoveryPolicy.Recover(response);

        Assert.AreEqual("category-1", assessment.RecoveredResponse.Catalog.Categories[0].Name);
        Assert.AreEqual("supplier-1", assessment.RecoveredResponse.Catalog.Suppliers[0].Name);
        Assert.AreEqual(1, assessment.WarningSummary.CategoriesAffected);
        Assert.AreEqual(1, assessment.WarningSummary.SuppliersAffected);
        Assert.AreEqual(2, assessment.WarningSummary.FallbackCount);
    }

    [TestMethod]
    public void CompatibilityAssessment_UsesRecoveredCopyAndLeavesRawResponseUntouched()
    {
        var response = Response("Cold\nBrew", "Second\tname", "BAR-1");

        var assessment = PosOnlineCompatibilityValidator.AssessCatalogPull(response);

        Assert.IsTrue(assessment.CanContinue);
        Assert.AreEqual("Cold\nBrew", response.Catalog.Products[0].ProductName);
        Assert.AreEqual("Cold Brew", assessment.RecoveredResponse.Catalog.Products[0].ProductName);
        Assert.AreEqual("Second name", assessment.RecoveredResponse.Catalog.Products[0].SecondProductName);
        Assert.AreEqual(string.Empty,
            PosOnlineCompatibilityValidator.ValidateCatalogPull(assessment.RecoveredResponse));
        Assert.IsFalse(CatalogRetryPolicy.IsDeterministicRevisionFailure(assessment.BlockingCode));
        Assert.IsTrue(assessment.SaleSafeCandidate);
    }

    [TestMethod]
    public void MapperUsesRecoveredDisplayTextForPersistenceAndStageFingerprints()
    {
        var batch = RemoteCatalogBatchMapper.BuildRemoteCatalogBatch(
            Response("Cold\nBrew", "", "BAR-1"),
            authoritativeFullRefresh: false,
            stagePage: null);

        Assert.AreEqual("Cold Brew", batch.Products[0].Name);
        Assert.AreEqual("barcode:BAR-1", RemoteCatalogBatchMapper.ProductStageFingerprint(batch.Products[0]));
    }

    [TestMethod]
    public void BarcodeAndRemoteIdentityControls_RemainBlocking()
    {
        var barcode = PosOnlineCompatibilityValidator.AssessCatalogPull(Response("Product", "", "BAR\n1"));
        var remoteId = Response("Product", "", "BAR-1");
        remoteId.Catalog.Products[0].ProductId = "product\t1";
        var id = PosOnlineCompatibilityValidator.AssessCatalogPull(remoteId);

        Assert.IsFalse(barcode.CanContinue);
        Assert.AreEqual("catalog_product_row_invalid", barcode.BlockingCode);
        Assert.IsFalse(id.CanContinue);
        Assert.AreEqual("catalog_product_row_invalid", id.BlockingCode);
    }

    [TestMethod]
    public void RecoveredDisplayName_IsSafeForReceiptLinesWithoutLineInjection()
    {
        var recovered = CatalogDisplayRecoveryPolicy.RecoverDisplayText(
            "Receipt\nproduct\tname",
            512);

        Assert.IsTrue(recovered.IsUsable);
        Assert.IsFalse(recovered.Value.Contains("\n"));
        Assert.IsFalse(recovered.Value.Contains("\r"));
        SalesReceiptContentPolicy.EnsureValidLines(new[]
        {
            new SaleLine
            {
                Barcode = "RECEIPT-1",
                Name = recovered.Value,
                Quantity = 1,
                UnitPrice = 100,
                LineTotal = 100
            }
        });
    }

    private static PosCatalogPullResponse Response(string productName, string secondName, string barcode)
    {
        return new PosCatalogPullResponse
        {
            Catalog = new PosCatalogPayload
            {
                Products = new[]
                {
                    new PosCatalogProductResponse
                    {
                        Barcode = barcode,
                        ProductId = "product-1",
                        ProductName = productName,
                        RetailPrice = 100,
                        SecondProductName = secondName
                    }
                }
            },
            CatalogVersion = "catalog-v1",
            Ok = true,
            Policy = new PosPolicyResponse
            {
                Capabilities = new PosPolicyCapabilitiesResponse
                {
                    CatalogPull = PosOnlineContract.CatalogCapabilityVersion,
                    OfflineSales = true,
                    SalesSync = PosOnlineContract.SalesSchemaVersion
                },
                ContractVersion = PosOnlineContract.PolicyContractVersion,
                PaymentPolicy = new PosPaymentPolicyResponse
                {
                    Currency = "CLP",
                    SupportedMethods = new[] { PosOnlineContract.PaymentCash }
                }
            },
            SchemaVersion = PosOnlineContract.CatalogPullSchemaVersion,
            SyncCursor = "cursor-1",
            SyncMode = "delta"
        };
    }

    private static void AssertRecovered(string raw, string expected)
    {
        var result = CatalogDisplayRecoveryPolicy.RecoverDisplayText(raw, 512);
        Assert.IsTrue(result.IsUsable);
        Assert.AreEqual(expected, result.Value);
        Assert.IsTrue(result.Warnings.Count > 0);
    }

    private static string FindFixture(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "tests",
                "fixtures",
                "CATALOG-TEXT-001",
                name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException("Fixture missing.", name);
    }
}
