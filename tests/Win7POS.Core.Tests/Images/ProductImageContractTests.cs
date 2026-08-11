using Win7POS.Core.Images;

namespace Win7POS.Core.Tests.Images;

[TestClass]
public sealed class ProductImageContractTests
{
    [TestMethod]
#pragma warning disable MSTEST0032
    public void ContractConstants_MatchConfirmedSharedV1()
    {
        Assert.AreEqual(25 * 1024 * 1024, ProductImageContractV1.InputMaximumBytes);
        Assert.AreEqual(64_000_000L, ProductImageContractV1.InputMaximumPixels);
        Assert.AreEqual(1600, ProductImageContractV1.MainMaximumSide);
        Assert.AreEqual(1024 * 1024, ProductImageContractV1.MainMaximumBytes);
        Assert.AreEqual(384, ProductImageContractV1.ThumbMaximumSide);
        Assert.AreEqual(90 * 1024, ProductImageContractV1.ThumbMaximumBytes);
        Assert.AreEqual("image/jpeg", ProductImageContractV1.WireMimeType);
        Assert.AreEqual("product-images", ProductImageContractV1.BucketName);
        Assert.AreEqual(16, ProductImageContractV1.ReadBatchMaximum);
        Assert.AreEqual(2, ProductImageContractV1.ReadRequestConcurrency);
        Assert.AreEqual(4, ProductImageContractV1.DownloadConcurrency);
    }
#pragma warning restore MSTEST0032

    [TestMethod]
    public void Identity_RejectsNonCanonicalScopeAndIdentifiers()
    {
        Assert.IsFalse(ProductImageIdentity.TryCreate(
            ProductImageTestData.AccountScope.ToUpperInvariant(),
            ProductImageTestData.ShopId.ToString("D"),
            ProductImageTestData.ProductId.ToString("D"),
            Guid.NewGuid().ToString("D"),
            out _,
            out var scopeValidation));
        Assert.AreEqual(
            ProductImageValidationCode.InvalidScope,
            scopeValidation.Code);

        Assert.IsFalse(ProductImageIdentity.TryCreate(
            ProductImageTestData.AccountScope,
            "../shop",
            ProductImageTestData.ProductId.ToString("D"),
            Guid.NewGuid().ToString("D"),
            out _,
            out var identityValidation));
        Assert.AreEqual(
            ProductImageValidationCode.InvalidIdentity,
            identityValidation.Code);
    }

    [TestMethod]
    public void Metadata_ReturnsExplicitMimeAndNumericFailures()
    {
        Assert.IsFalse(ProductImageMetadata.TryCreate(
            ProductImageVariant.Thumb,
            "image/webp",
            20,
            1,
            1,
            new string('a', 64),
            out _,
            out var mimeValidation));
        Assert.AreEqual(
            ProductImageValidationCode.UnsupportedMimeType,
            mimeValidation.Code);

        Assert.IsFalse(ProductImageMetadata.TryCreate(
            ProductImageVariant.Thumb,
            "image/jpeg",
            ProductImageContractV1.ThumbMaximumBytes + 1L,
            1,
            1,
            new string('a', 64),
            out _,
            out var sizeValidation));
        Assert.AreEqual(
            ProductImageValidationCode.InvalidByteSize,
            sizeValidation.Code);

        Assert.IsFalse(ProductImageMetadata.TryCreate(
            ProductImageVariant.Thumb,
            "image/jpeg",
            20,
            ProductImageContractV1.ThumbMaximumSide + 1,
            1,
            new string('a', 64),
            out _,
            out var dimensionValidation));
        Assert.AreEqual(
            ProductImageValidationCode.InvalidDimensions,
            dimensionValidation.Code);
    }

    [TestMethod]
    public void ObjectPath_RequiresExactCanonicalPath()
    {
        var identity = ProductImageTestData.CreateIdentity();
        var canonical =
            $"shops/{identity.ShopId:D}/products/{identity.ProductId:D}/primary/{identity.VersionId:D}/thumb.jpg";

        Assert.IsTrue(ProductImageObjectPathPolicy
            .Validate(canonical, identity, ProductImageVariant.Thumb)
            .IsValid);
        Assert.AreEqual(
            ProductImageValidationCode.InvalidObjectPath,
            ProductImageObjectPathPolicy
                .Validate(
                    $"shops/{identity.ShopId:D}/products/../secrets/thumb.jpg",
                    identity,
                    ProductImageVariant.Thumb)
                .Code);
        Assert.AreEqual(
            ProductImageValidationCode.InvalidObjectPath,
            ProductImageObjectPathPolicy
                .Validate(canonical.Replace('/', '\\'), identity, ProductImageVariant.Thumb)
                .Code);
        Assert.AreEqual(
            ProductImageValidationCode.InvalidObjectPath,
            ProductImageObjectPathPolicy
                .Validate("/" + canonical, identity, ProductImageVariant.Thumb)
                .Code);
    }

    [TestMethod]
    public void CacheKey_IsDeterministicVersionedAndSecretFree()
    {
        var bytes = ProductImageTestData.CreateParserValidJpeg();
        var reference = ProductImageTestData.CreateReference(bytes);
        var first = ProductImageCacheKey.FromReference(reference);
        var second = ProductImageCacheKey.FromReference(reference);

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.FileStem, second.FileStem);
        Assert.AreEqual(64, first.FileStem.Length);
        StringAssert.Contains(first.CanonicalValue, reference.Identity.VersionId.ToString("D"));
        Assert.IsFalse(first.CanonicalValue.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(first.CanonicalValue.Contains("token", StringComparison.OrdinalIgnoreCase));

        var replacement = ProductImageTestData.CreateReference(
            bytes,
            versionId: Guid.Parse("44444444-4444-4444-8444-444444444444"));
        Assert.AreNotEqual(
            first.FileStem,
            ProductImageCacheKey.FromReference(replacement).FileStem);
    }

    [TestMethod]
    public void InputFormat_IsDetectedFromMagicNotFilename()
    {
        var jpeg = ProductImageTestData.CreateParserValidJpeg();
        var png = new byte[]
        {
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00
        };

        Assert.AreEqual(
            ProductImageInputFormat.Jpeg,
            ProductImageInputPolicy.DetectFormat(jpeg));
        Assert.AreEqual(
            ProductImageInputFormat.Png,
            ProductImageInputPolicy.DetectFormat(png));
        Assert.AreEqual(
            ProductImageInputFormat.Unknown,
            ProductImageInputPolicy.DetectFormat(new byte[] { 1, 2, 3 }));
    }

    [TestMethod]
    public void UndefinedVariant_IsRejectedAndCannotAliasThumbKey()
    {
        var invalidVariant = (ProductImageVariant)99;
        Assert.IsFalse(ProductImageMetadata.TryCreate(
            invalidVariant,
            "image/jpeg",
            20,
            1,
            1,
            new string('a', 64),
            out _,
            out var validation));
        Assert.AreEqual(
            ProductImageValidationCode.UnsupportedVariant,
            validation.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductImageContractV1.VariantName(invalidVariant));

        var bytes = ProductImageTestData.CreateParserValidJpeg();
        var thumb = ProductImageTestData.CreateReference(bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductImageReference(
                thumb.Identity,
                invalidVariant,
                thumb.Metadata));
    }

    [TestMethod]
    public void Win7PreprocessDefault_IsStricterThanPortablePixelCeiling()
    {
        var options = new ProductImagePreprocessOptions();
        Assert.AreEqual(
            ProductImagePreprocessOptions.Win7DefaultMaximumSourcePixels,
            options.MaximumSourcePixels);
        Assert.IsTrue(
            options.MaximumSourcePixels <
            ProductImageContractV1.InputMaximumPixels);
        Assert.IsTrue(options.MaximumSourcePixels <= 16_000_000L);
    }
}
