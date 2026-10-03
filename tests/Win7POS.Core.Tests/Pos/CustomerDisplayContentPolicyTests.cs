using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;

namespace Win7POS.Core.Tests.Pos;

[TestClass]
public sealed class CustomerDisplayContentPolicyTests
{
    [TestMethod]
    public void Defaults_ArePrivateAndLegacyBarcodeAliasRemainsCompatible()
    {
        var settings = CustomerDisplaySettings.CreateDefault(2);
        Assert.AreEqual(CustomerDisplayBarcodeMode.Hidden, settings.BarcodeMode);
        Assert.IsFalse(settings.ShowPaidAmount);
        Assert.IsTrue(settings.ShowChangeAmount);
        Assert.AreEqual(15, settings.TestPatternDurationSeconds);
        settings.ShowBarcode = true;
        Assert.AreEqual(CustomerDisplayBarcodeMode.Full, settings.BarcodeMode);
        settings.BarcodeMode = CustomerDisplayBarcodeMode.Last4;
        Assert.IsTrue(settings.ShowBarcode);
        Assert.AreEqual(CustomerDisplayBarcodeMode.Last4, settings.Clone().BarcodeMode);
    }

    [TestMethod]
    [DataRow("DISC:ABC")]
    [DataRow("tax:ABC")]
    [DataRow("MANUAL:ABC")]
    public void ReservedBarcode_IsSuppressedEvenWhenFullOrMasked(string barcode)
    {
        foreach (var mode in new[] { CustomerDisplayBarcodeMode.Full, CustomerDisplayBarcodeMode.Last4 })
            Assert.AreEqual(string.Empty, CustomerDisplayContentPolicy.PublicBarcode(barcode, CustomerDisplayLineKind.Item, mode));
    }

    [TestMethod]
    public void BarcodeMasking_PreservesLastFourAndFailsClosedForUnknownMode()
    {
        Assert.AreEqual("••••1234", CustomerDisplayContentPolicy.PublicBarcode("00001234", CustomerDisplayLineKind.Item, CustomerDisplayBarcodeMode.Last4));
        Assert.AreEqual("012", CustomerDisplayContentPolicy.PublicBarcode("012", CustomerDisplayLineKind.Item, CustomerDisplayBarcodeMode.Last4));
        Assert.AreEqual("00001234", CustomerDisplayContentPolicy.PublicBarcode("00001234", CustomerDisplayLineKind.Item, CustomerDisplayBarcodeMode.Full));
        Assert.AreEqual("••••😀123", CustomerDisplayContentPolicy.PublicBarcode("0000😀123", CustomerDisplayLineKind.Item, CustomerDisplayBarcodeMode.Last4));
        Assert.AreEqual(string.Empty, CustomerDisplayContentPolicy.PublicBarcode("00001234", CustomerDisplayLineKind.Discount, CustomerDisplayBarcodeMode.Full));
        Assert.AreEqual(string.Empty, CustomerDisplayContentPolicy.PublicBarcode("00001234", CustomerDisplayLineKind.Item, (CustomerDisplayBarcodeMode)99));
    }

    [TestMethod]
    public void MessageAndDuration_ValidateBoundsUnicodeAndMarkup()
    {
        Assert.IsTrue(CustomerDisplayContentPolicy.IsSafeMessage("Caffè 欢迎 😀"));
        Assert.IsTrue(CustomerDisplayContentPolicy.IsSafeMessage(new string('x', 120)));
        foreach (var invalid in new[] { new string('x', 121), "line\nbreak", "<b>text</b>", "\uD800" })
            Assert.IsFalse(CustomerDisplayContentPolicy.IsSafeMessage(invalid));
        var settings = CustomerDisplaySettings.CreateDefault(2);
        foreach (var duration in new[] { 5, 60 }) { settings.TestPatternDurationSeconds = duration; Assert.AreEqual(0, settings.Validate().Count); }
        foreach (var duration in new[] { 4, 61 }) { settings.TestPatternDurationSeconds = duration; CollectionAssert.Contains(settings.Validate().ToArray(), "test_pattern_duration"); }
    }

    [TestMethod]
    public void ManagedLogoReference_AcceptsOnlyContentBoundBasenames()
    {
        var hash = new string('a', 64);
        Assert.IsTrue(CustomerDisplayContentPolicy.IsManagedLogoReference("logo_" + hash + ".png", hash));
        Assert.IsTrue(CustomerDisplayContentPolicy.IsManagedLogoReference("", ""));
        foreach (var invalid in new[] { "../logo.png", @"\\server\logo.png", "https://example/logo.png", "logo_" + hash + ".gif", "logo_" + hash + ".svg" })
            Assert.IsFalse(CustomerDisplayContentPolicy.IsManagedLogoReference(invalid, hash));
    }

    [TestMethod]
    public void LogoHeader_BoundsPixelsAndBytesBeforeDecoderAllocation()
    {
        Assert.AreEqual(".png", CustomerDisplayContentPolicy.InspectLogoHeader(PngHeader(2000, 2000), out var width, out var height));
        Assert.AreEqual(2000, width); Assert.AreEqual(2000, height);
        Assert.ThrowsExactly<InvalidDataException>(() => CustomerDisplayContentPolicy.InspectLogoHeader(PngHeader(2001, 2000), out _, out _));
        Assert.ThrowsExactly<InvalidDataException>(() => CustomerDisplayContentPolicy.InspectLogoHeader(PngHeader(0, 100), out _, out _));
        Assert.ThrowsExactly<InvalidDataException>(() => CustomerDisplayContentPolicy.InspectLogoHeader(new byte[CustomerDisplayContentPolicy.MaximumLogoBytes + 1], out _, out _));
        Assert.ThrowsExactly<InvalidDataException>(() => CustomerDisplayContentPolicy.InspectLogoHeader(new byte[24], out _, out _));
    }

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[40];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        bytes[11] = 13; bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        for (var i = 0; i < 4; i++) { bytes[16 + i] = (byte)(width >> (24 - i * 8)); bytes[20 + i] = (byte)(height >> (24 - i * 8)); }
        return bytes;
    }
}
