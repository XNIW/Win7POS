using Win7POS.Core.Images;

namespace Win7POS.Core.Tests.Images;

[TestClass]
public sealed class ProductImageBinaryPolicyTests
{
    [TestMethod]
    public void Inspect_ReadsBoundedJpegDimensions()
    {
        var bytes = ProductImageTestData.CreateParserValidJpeg(320, 180);
        var result = ProductImageBinaryPolicy.Inspect(
            bytes,
            ProductImageContractV1.InputMaximumBytes,
            ProductImageContractV1.InputMaximumPixels,
            out var header);

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(header);
        Assert.AreEqual(ProductImageInputFormat.Jpeg, header.Format);
        Assert.AreEqual(320, header.Width);
        Assert.AreEqual(180, header.Height);
        Assert.AreEqual(1, header.Orientation);
    }

    [TestMethod]
    public void Inspect_RejectsPixelBombAndCorruptInput()
    {
        var bomb = ProductImageTestData.CreateParserValidJpeg(
            ushort.MaxValue,
            ushort.MaxValue);
        var bombResult = ProductImageBinaryPolicy.Inspect(
            bomb,
            ProductImageContractV1.InputMaximumBytes,
            ProductImageContractV1.InputMaximumPixels,
            out _);
        Assert.AreEqual(
            ProductImageValidationCode.InvalidDimensions,
            bombResult.Code);

        var corruptResult = ProductImageBinaryPolicy.Inspect(
            new byte[] { 0xff, 0xd8, 0xff },
            1024,
            ProductImageContractV1.InputMaximumPixels,
            out _);
        Assert.AreEqual(
            ProductImageValidationCode.CorruptImage,
            corruptResult.Code);
    }

    [TestMethod]
    public void Inspect_RejectsJpegWithMultipleStartOfFrameSegments()
    {
        var firstSofIsOversized = ProductImageTestData.CreateParserValidJpeg(
            ushort.MaxValue,
            ushort.MaxValue);
        var secondSof = ProductImageTestData.CreateParserValidJpeg(1, 1)
            .Skip(20)
            .Take(19)
            .ToArray();
        var scanOffset = Array.IndexOf(firstSofIsOversized, (byte)0xda);
        Assert.IsTrue(scanOffset > 0);
        var beforeScan = firstSofIsOversized
            .Take(scanOffset - 1)
            .Concat(secondSof)
            .Concat(firstSofIsOversized.Skip(scanOffset - 1))
            .ToArray();

        var firstSofIsSmall = ProductImageTestData.CreateParserValidJpeg(1, 1);
        var endOfImageOffset = firstSofIsSmall.Length - 2;
        var afterScan = firstSofIsSmall
            .Take(endOfImageOffset)
            .Concat(ProductImageTestData.CreateParserValidJpeg(
                    ushort.MaxValue,
                    ushort.MaxValue)
                .Skip(20)
                .Take(19))
            .Concat(firstSofIsSmall.Skip(endOfImageOffset))
            .ToArray();

        var beforeScanResult = ProductImageBinaryPolicy.Inspect(
            beforeScan,
            ProductImageContractV1.InputMaximumBytes,
            ProductImageContractV1.InputMaximumPixels,
            out _);
        var afterScanResult = ProductImageBinaryPolicy.Inspect(
            afterScan,
            ProductImageContractV1.InputMaximumBytes,
            ProductImageContractV1.InputMaximumPixels,
            out _);

        Assert.AreEqual(
            ProductImageValidationCode.CorruptImage,
            beforeScanResult.Code);
        Assert.AreEqual(
            ProductImageValidationCode.CorruptImage,
            afterScanResult.Code);
    }

    [TestMethod]
    public void CanonicalValidation_RejectsTrailingBytesAndForbiddenMetadata()
    {
        var bytes = ProductImageTestData.CreateParserValidJpeg();
        var reference = ProductImageTestData.CreateReference(bytes);
        Assert.IsTrue(ProductImageBinaryPolicy
            .ValidateCanonicalWireJpeg(bytes, reference.Metadata)
            .IsValid);

        var trailing = bytes.Concat(new byte[] { 0x00 }).ToArray();
        Assert.IsFalse(ProductImageBinaryPolicy
            .ValidateCanonicalWireJpeg(trailing, reference.Metadata)
            .IsValid);

        var app1 = new byte[]
        {
            0xff, 0xd8,
            0xff, 0xe1, 0x00, 0x04, 0x00, 0x00
        }.Concat(bytes.Skip(2)).ToArray();
        Assert.IsTrue(ProductImageBinaryPolicy.HasForbiddenJpegMetadata(app1));
        var stripped = ProductImageBinaryPolicy.RemoveForbiddenJpegMetadata(app1);
        Assert.IsFalse(ProductImageBinaryPolicy.HasForbiddenJpegMetadata(stripped));
        CollectionAssert.AreEqual(bytes, stripped);
    }

    [TestMethod]
    public void CanonicalValidation_RejectsChecksumMismatch()
    {
        var bytes = ProductImageTestData.CreateParserValidJpeg();
        var reference = ProductImageTestData.CreateReference(bytes);
        var mutated = (byte[])bytes.Clone();
        mutated[mutated.Length - 3] ^= 0x01;

        Assert.AreEqual(
            ProductImageValidationCode.InvalidChecksum,
            ProductImageBinaryPolicy
                .ValidateCanonicalWireJpeg(mutated, reference.Metadata)
                .Code);
    }
}
