using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Hardware;

namespace Win7POS.Core.Tests.Hardware;

[TestClass]
public sealed class HardwarePolicyTests
{
    [TestMethod]
    public void ScannerStripsExactAffixesAndOuterWhitespaceWithoutChangingIdentity()
    {
        var settings = new ScannerInputSettings { Prefix = "]C1", Suffix = "END" };
        var raw = " \r\n]C10001 Ab中END\r\n ";
        Assert.IsTrue(ScannerInputPolicy.TryNormalize(raw, settings, out var normalized, out _));
        Assert.AreEqual("0001 Ab中", normalized);
        Assert.AreEqual(" \r\n]C10001 Ab中END\r\n ", raw);
        foreach (var mismatch in new[] { "0001END", "]C0001END", "]C1END", "]C10001EN" })
            Assert.IsFalse(ScannerInputPolicy.TryNormalize(mismatch, settings, out _, out _));
        settings.TrimWhitespace = false;
        Assert.IsFalse(ScannerInputPolicy.TryNormalize(raw, settings, out _, out _));
    }

    [TestMethod]
    public void ScannerBoundsControlsAndSurrogatesAreCheckedBeforeLookup()
    {
        var settings = new ScannerInputSettings { MaximumLength = 256 };
        foreach (var length in new[] { 1, 128, 256 })
            Assert.IsTrue(ScannerInputPolicy.TryNormalize(new string('0', length), settings, out _, out _));
        foreach (var value in new[] { "", new string('0', 257), "00\t01", "00\u000001", "\ud800", "\udc00" })
            Assert.IsFalse(ScannerInputPolicy.TryNormalize(value, settings, out _, out _));
        Assert.IsTrue(ScannerInputPolicy.TryNormalize("00😀01", settings, out var normalized, out _));
        Assert.AreEqual("00😀01", normalized);
        settings.MinimumLength = 128;
        Assert.IsFalse(ScannerInputPolicy.TryNormalize(new string('0', 127), settings, out _, out _));
        settings.MaximumLength = 127;
        Assert.AreNotEqual(0, ScannerInputPolicy.Validate(settings).Count);
        settings.MinimumLength = 0;
        Assert.AreNotEqual(0, ScannerInputPolicy.Validate(settings).Count);
    }

    [TestMethod]
    public void ScannerAffixesRejectControlsUnsupportedUnicodeAndExcessLength()
    {
        foreach (var prefix in new[] { "\t", "\r", "中", new string('A', 17) })
            Assert.AreNotEqual(0, ScannerInputPolicy.Validate(new ScannerInputSettings { Prefix = prefix }).Count);
        Assert.AreEqual(0, ScannerInputPolicy.Validate(new ScannerInputSettings { Prefix = new string('A', 16), Suffix = " !~" }).Count);
    }

    [TestMethod]
    public void ScannerAcceptsOnlyConfiguredTerminator()
    {
        var settings = new ScannerInputSettings();
        Assert.IsTrue(ScannerInputPolicy.Accepts(settings, ScannerTerminator.Enter));
        Assert.IsFalse(ScannerInputPolicy.Accepts(settings, ScannerTerminator.Tab));
        settings.Terminator = ScannerTerminator.Tab;
        Assert.IsFalse(ScannerInputPolicy.Accepts(settings, ScannerTerminator.Enter));
        Assert.IsTrue(ScannerInputPolicy.Accepts(settings, ScannerTerminator.Tab));
        settings.Terminator = ScannerTerminator.EnterOrTab;
        Assert.IsTrue(ScannerInputPolicy.Accepts(settings, ScannerTerminator.Enter));
        Assert.IsTrue(ScannerInputPolicy.Accepts(settings, ScannerTerminator.Tab));
        Assert.IsFalse(ScannerInputPolicy.Accepts(settings, ScannerTerminator.EnterOrTab));
    }

    [TestMethod]
    public void ReceiptProfilesUseMeasuredPaperWidthWithSafeFallback()
    {
        Assert.AreEqual(32, ReceiptProfilePolicy.Columns(ReceiptProfilePolicy.FromLegacy(false)));
        Assert.AreEqual(42, ReceiptProfilePolicy.Columns(ReceiptProfilePolicy.FromLegacy(true)));
        Assert.AreEqual(0, ReceiptProfilePolicy.SelectPaperIndex(ReceiptProfile.Thermal58mm32col, new[] { 228, 315, 827 }));
        Assert.AreEqual(1, ReceiptProfilePolicy.SelectPaperIndex(ReceiptProfile.Thermal80mm42col, new[] { 228, 315, 827 }));
        Assert.AreEqual(-1, ReceiptProfilePolicy.SelectPaperIndex(ReceiptProfile.Thermal58mm32col, new[] { 315, 827 }));
        Assert.AreEqual(-1, ReceiptProfilePolicy.SelectPaperIndex(ReceiptProfile.Thermal80mm42col, new[] { int.MinValue, int.MaxValue }));
        Assert.IsFalse(ReceiptProfilePolicy.TryParse("80", out _));
    }

    [TestMethod]
    public void DrawerPresetsGenerateOnlyValidatedExactPulseShape()
    {
        Assert.IsTrue(CashDrawerPresetPolicy.TryGetBytes(CashDrawerPreset.EscposPin2, "ignored", out var pin2));
        Assert.IsTrue(CashDrawerPresetPolicy.TryGetBytes(CashDrawerPreset.EscposPin5, "ignored", out var pin5));
        CollectionAssert.AreEqual(new byte[] { 27, 112, 0, 25, 250 }, pin2);
        CollectionAssert.AreEqual(new byte[] { 27, 112, 1, 25, 250 }, pin5);
        foreach (var command in new[] { "", "27,112,0,25", "27,112,0,25,250,1", "27,112,2,25,250", "27,112,0,250,25", "27,112,0,25,256", "27,112,0,x,250", "27,112,0,-1,250" })
            Assert.IsFalse(CashDrawerPresetPolicy.TryGetBytes(CashDrawerPreset.Custom, command, out _), command);
        Assert.IsTrue(CashDrawerPresetPolicy.TryGetBytes(CashDrawerPreset.Custom, "27,112,49,0,255", out _));
    }

    [TestMethod]
    public async Task DiagnosticTimeoutRetainsWorkerAndDifferentQueueCannotStartAnother()
    {
        var flight = new SingleFlightDiagnostic<int>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        int Read() { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); return 17; }
        var first = flight.RunAsync("A", Read, TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.AreEqual("timeout", (await first).ResultCode);
            Assert.AreEqual("timeout", (await flight.RunAsync("A", Read, TimeSpan.FromMilliseconds(50))).ResultCode);
            Assert.AreEqual("busy", (await flight.RunAsync("B", Read, TimeSpan.FromMilliseconds(50))).ResultCode);
            Assert.AreEqual(1, calls);
            var pending = flight.RunAsync("A", Read, TimeSpan.FromSeconds(5));
            release.Set();
            Assert.AreEqual(17, (await pending).Value);
            Assert.AreEqual(1, calls);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task DiagnosticFailureIsUnknownAndNextCallCanRecover()
    {
        var flight = new SingleFlightDiagnostic<int>();
        Assert.AreEqual("unavailable", (await flight.RunAsync("A", () => throw new IOException("fixture"), TimeSpan.FromSeconds(1))).ResultCode);
        Assert.AreEqual(23, (await flight.RunAsync("A", () => 23, TimeSpan.FromSeconds(1))).Value);
    }
}
