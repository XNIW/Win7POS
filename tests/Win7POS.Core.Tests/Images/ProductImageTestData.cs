using Win7POS.Core.Images;

namespace Win7POS.Core.Tests.Images;

internal static class ProductImageTestData
{
    internal const string AccountScope =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static readonly Guid ShopId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid ProductId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    internal static byte[] CreateParserValidJpeg(
        ushort width = 1,
        ushort height = 1)
    {
        var bytes = new List<byte>
        {
            0xff, 0xd8,
            0xff, 0xe0, 0x00, 0x10,
            0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xff, 0xc0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            0x03,
            0x01, 0x11, 0x00,
            0x02, 0x11, 0x00,
            0x03, 0x11, 0x00,
            0xff, 0xda, 0x00, 0x0c, 0x03,
            0x01, 0x00,
            0x02, 0x00,
            0x03, 0x00,
            0x00, 0x3f, 0x00,
            0x00,
            0xff, 0xd9
        };
        return bytes.ToArray();
    }

    internal static ProductImageIdentity CreateIdentity(
        Guid? versionId = null,
        Guid? productId = null)
    {
        var ok = ProductImageIdentity.TryCreate(
            AccountScope,
            ShopId.ToString("D"),
            (productId ?? ProductId).ToString("D"),
            (versionId ?? Guid.Parse("33333333-3333-4333-8333-333333333333"))
                .ToString("D"),
            out var identity,
            out var validation);
        Assert.IsTrue(ok, string.Join(",", validation.Messages));
        return identity!;
    }

    internal static ProductImageReference CreateReference(
        byte[] bytes,
        ProductImageVariant variant = ProductImageVariant.Thumb,
        Guid? versionId = null,
        Guid? productId = null,
        int width = 1,
        int height = 1,
        DateTimeOffset? imageUpdatedAt = null)
    {
        var ok = ProductImageMetadata.TryCreate(
            variant,
            ProductImageContractV1.WireMimeType,
            bytes.Length,
            width,
            height,
            ProductImageHash.Sha256Hex(bytes),
            out var metadata,
            out var validation);
        Assert.IsTrue(ok, string.Join(",", validation.Messages));
        return new ProductImageReference(
            CreateIdentity(versionId, productId),
            variant,
            metadata!,
            imageUpdatedAt ?? DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
    }

    internal static string CreateTempDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "Win7POS-product-image-tests",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static void DeleteTempDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var allowedRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "Win7POS-product-image-tests"));
        Assert.IsTrue(
            fullPath.StartsWith(
                allowedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
