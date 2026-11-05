using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class HistoricalSalesValueRulesTests
{
    [TestMethod]
    [DataRow("9223372036854775807", long.MaxValue)]
    [DataRow("-9223372036854775808", long.MinValue)]
    [DataRow("1200.000", 1200L)]
    [DataRow("12e2", 1200L)]
    [DataRow("0000012", 12L)]
    public void Clp_IsAnExactSignedInteger(string raw, long expected) => Assert.AreEqual(expected, HistoricalSalesValueRules.ParseClp(raw));

    [TestMethod]
    [DataRow("1.00000000000000000000000000001")]
    [DataRow("0.001")]
    [DataRow("1,200")]
    [DataRow("1 200")]
    [DataRow("NaN")]
    public void FractionalOrCulturalMoney_IsNotRounded(string raw) => Assert.ThrowsExactly<FormatException>(() => HistoricalSalesValueRules.ParseClp(raw));

    [TestMethod]
    public void Overflow_IsExplicitAndIdentifiersPreserveLexicalValue()
    {
        Assert.ThrowsExactly<OverflowException>(() => HistoricalSalesValueRules.ParseClp("9223372036854775808"));
        Assert.AreEqual("000AB7", HistoricalSalesValueRules.ParseIdentifier("000AB7"));
        Assert.AreEqual("00000127", HistoricalSalesValueRules.ParseIdentifier("127", true, "00000000"));
        Assert.ThrowsExactly<FormatException>(() => HistoricalSalesValueRules.ParseIdentifier("1234567890123456", true));
        Assert.ThrowsExactly<FormatException>(() => HistoricalSalesValueRules.ParseIdentifier("4.2E9", true));
        Assert.ThrowsExactly<FormatException>(() => HistoricalSalesValueRules.ParseIdentifier("−"));
        Assert.AreEqual("COMISION DEL ABONO", HistoricalSalesValueRules.NormalizeHeader("  comisión\t del   abono "));
    }

    [TestMethod]
    public void TimePolicies_DoNotUseTheMachineZoneAndPreserveTicks()
    {
        var options = new HistoricalSalesImportOptions { ModernTimeInterpretation = HistoricalSalesModernTimeInterpretation.IsoUtc, TemporalEvidenceReference = "synthetic-evidence" };
        var first = HistoricalSalesTimeRules.ParseModern("2024-02-01T01:02:03.1234567Z", "-4", options);
        Assert.AreEqual(new DateTime(2024, 1, 31, 21, 2, 3).AddTicks(1234567), first.BusinessDateTime);
        Assert.AreEqual(DateTimeKind.Unspecified, first.BusinessDateTime!.Value.Kind);
        var summer = HistoricalSalesTimeRules.ParseModern("2024-02-01T01:02:03.1234567Z", "-3", options);
        Assert.AreEqual(TimeSpan.FromHours(1), summer.BusinessDateTime - first.BusinessDateTime);
        options.ModernTimeInterpretation = HistoricalSalesModernTimeInterpretation.ExportedLocal;
        var local = HistoricalSalesTimeRules.ParseModern("2024-02-01T01:02:03.1234567Z", "-4", options);
        Assert.AreEqual(new DateTime(2024, 2, 1, 1, 2, 3).AddTicks(1234567), local.BusinessDateTime);
        Assert.AreEqual(5, local.OccurredAtUtc!.Value.Hour);
        Assert.ThrowsExactly<FormatException>(() => HistoricalSalesTimeRules.ParseModern("2024-02-01T01:02:03.12345678Z", "-4", options));
        Assert.ThrowsExactly<FormatException>(() => HistoricalSalesTimeRules.ParseModern("2024-02-01T01:02:03Z", "-14:01", options));
    }
}
