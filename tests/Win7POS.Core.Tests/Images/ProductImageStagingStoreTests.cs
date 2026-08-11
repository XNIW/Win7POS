using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Images;
using Win7POS.Data.Images;

namespace Win7POS.Core.Tests.Images;

[TestClass]
public sealed class ProductImageStagingStoreTests
{
    [TestMethod]
    public async Task StageReadAndDeletePair_UsesOpaqueCanonicalFiles()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var pair = await store.StagePairAsync(
                Variant(ProductImageVariant.Main, 8, 6),
                Variant(ProductImageVariant.Thumb, 4, 3));

            StringAssert.Matches(pair.MainIdentity, new System.Text.RegularExpressions.Regex(
                "^stage-[0-9a-f]{32}-main\\.jpg$"));
            StringAssert.Matches(pair.ThumbIdentity, new System.Text.RegularExpressions.Regex(
                "^stage-[0-9a-f]{32}-thumb\\.jpg$"));
            using (var stream = await store.OpenVerifiedReadAsync(
                pair.MainIdentity,
                ProductImageVariant.Main,
                Variant(ProductImageVariant.Main, 8, 6).Metadata))
            {
                Assert.IsTrue(stream.Length > 0);
            }

            await store.DeletePairAsync(pair.MainIdentity, pair.ThumbIdentity);
            Assert.AreEqual(0, Directory.EnumerateFiles(root).Count());
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task VerifiedRead_RejectsTamperWithoutDeletingOtherVariant()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var main = Variant(ProductImageVariant.Main, 8, 6);
            var thumb = Variant(ProductImageVariant.Thumb, 4, 3);
            var pair = await store.StagePairAsync(main, thumb);
            var mainPath = Path.Combine(root, pair.MainIdentity);
            var tampered = await File.ReadAllBytesAsync(mainPath);
            tampered[tampered.Length - 3] ^= 0x01;
            await File.WriteAllBytesAsync(mainPath, tampered);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                store.OpenVerifiedReadAsync(
                    pair.MainIdentity,
                    ProductImageVariant.Main,
                    main.Metadata));
            Assert.IsTrue(File.Exists(Path.Combine(root, pair.ThumbIdentity)));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task CleanupOrphans_DeletesOnlyOldUnreferencedOpaqueFiles()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            var pair = await store.StagePairAsync(
                Variant(ProductImageVariant.Main, 8, 6),
                Variant(ProductImageVariant.Thumb, 4, 3));
            var now = DateTimeOffset.UtcNow;
            File.SetLastWriteTimeUtc(
                Path.Combine(root, pair.MainIdentity),
                now.AddHours(-2).UtcDateTime);
            File.SetLastWriteTimeUtc(
                Path.Combine(root, pair.ThumbIdentity),
                now.AddHours(-2).UtcDateTime);

            var deleted = await store.CleanupOrphansAsync(
                new[] { pair.ThumbIdentity },
                now);

            Assert.AreEqual(1, deleted);
            Assert.IsFalse(File.Exists(Path.Combine(root, pair.MainIdentity)));
            Assert.IsTrue(File.Exists(Path.Combine(root, pair.ThumbIdentity)));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task IdentitiesAndRoots_FailClosed()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ProductImageStagingOptions(Path.GetPathRoot(Path.GetTempPath())!));
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var store = CreateStore(root);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                store.DeletePairAsync("../escape.jpg", "stage-safe-thumb.jpg"));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    private static ProductImageStagingStore CreateStore(string root) =>
        new(new ProductImageStagingOptions(root, TimeSpan.FromMinutes(5)));

    private static ProductImageProcessedVariant Variant(
        ProductImageVariant variant,
        ushort width,
        ushort height)
    {
        var bytes = ProductImageTestData.CreateParserValidJpeg(width, height);
        Assert.IsTrue(ProductImageMetadata.TryCreate(
            variant,
            ProductImageContractV1.WireMimeType,
            bytes.Length,
            width,
            height,
            ProductImageHash.Sha256Hex(bytes),
            out var metadata,
            out var validation),
            string.Join(",", validation.Messages));
        return new ProductImageProcessedVariant(variant, bytes, metadata!);
    }
}
