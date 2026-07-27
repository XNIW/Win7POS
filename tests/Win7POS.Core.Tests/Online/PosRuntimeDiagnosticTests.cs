using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosRuntimeDiagnosticTests
{
    [TestMethod]
    public void CatalogServerFailure_ProducesBoundedSupportTextWithoutSecrets()
    {
        var diagnostic = new PosRuntimeDiagnostic(
            operation: "catalog.pull",
            stage: "server_response",
            code: "db_failure",
            httpStatus: 500,
            retryable: true,
            authenticationDenied: false,
            attemptNumber: 2,
            pageNumber: 1,
            pagesProcessed: 0,
            rowsReceived: 0,
            rowsApplied: 0,
            hasMore: false,
            catalogSaleSafe: false,
            clientRequestId: "win7pos-catalog-pull-123",
            serverRequestId: "server-500",
            cfRay: "ray-123",
            localIncidentId: "inc-local-123",
            occurredAtUtc: new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero),
            elapsedMilliseconds: 1234,
            exceptionType: "",
            safeSummary: "Server response stopped the catalog pull.");

        var text = diagnostic.ToSafeSupportText();

        StringAssert.Contains(text, "Operation: catalog.pull");
        StringAssert.Contains(text, "Stage: server_response");
        StringAssert.Contains(text, "Code: db_failure");
        StringAssert.Contains(text, "HTTP: 500");
        StringAssert.Contains(text, "Pages processed: 0");
        StringAssert.Contains(text, "Rows applied: 0");
        StringAssert.Contains(text, "Sale safe: no");
        StringAssert.Contains(text, "Retryable: yes");
        StringAssert.Contains(text, "Support ID: sha256:");
        Assert.IsFalse(text.Contains("server-500", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void UnsafeValues_AreAllowlistedAndUnknownOperationStageAreBounded()
    {
        var diagnostic = new PosRuntimeDiagnostic(
            operation: "catalog.pull?shop=private",
            stage: string.Concat("server response ", "body"),
            code: "bad code\r\n",
            httpStatus: 900,
            retryable: true,
            authenticationDenied: true,
            attemptNumber: -1,
            pageNumber: 0,
            pagesProcessed: -2,
            rowsReceived: -3,
            rowsApplied: -4,
            hasMore: false,
            catalogSaleSafe: false,
            clientRequestId: "id with spaces and / separators",
            serverRequestId: "",
            cfRay: "",
            localIncidentId: "incident",
            occurredAtUtc: default,
            elapsedMilliseconds: -1,
            exceptionType: "Unexpected Exception!",
            safeSummary: "safe");

        Assert.AreEqual("unknown", diagnostic.Operation);
        Assert.AreEqual("unknown", diagnostic.Stage);
        Assert.AreEqual("badcode", diagnostic.Code);
        Assert.IsNull(diagnostic.HttpStatus);
        Assert.IsFalse(diagnostic.Retryable);
        Assert.AreEqual(0, diagnostic.AttemptNumber);
        Assert.IsNull(diagnostic.PageNumber);
        Assert.AreEqual(0, diagnostic.PagesProcessed);
        Assert.AreEqual(0, diagnostic.RowsApplied);
        Assert.IsTrue(diagnostic.ClientRequestId.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.ClientRequestId.Contains("spaces", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LongCorrelationValues_AreTruncatedAndCannotInjectSupportText()
    {
        var diagnostic = new PosRuntimeDiagnostic(
            operation: "catalog.pull",
            stage: "server_response",
            code: "db_failure",
            httpStatus: 500,
            retryable: true,
            authenticationDenied: false,
            attemptNumber: 1,
            pageNumber: 1,
            pagesProcessed: 0,
            rowsReceived: 0,
            rowsApplied: 0,
            hasMore: false,
            catalogSaleSafe: false,
            clientRequestId: string.Concat("token", "=should-not-survive\r\n", new string('x', 180)),
            serverRequestId: "server/unsafe " + new string('y', 180),
            cfRay: "ray\r\n" + new string('z', 180),
            localIncidentId: "incident\r\n" + new string('q', 180),
            occurredAtUtc: new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero),
            elapsedMilliseconds: 1,
            exceptionType: "Unexpected\r\nException",
            safeSummary: "unused");

        var supportText = diagnostic.ToSafeSupportText();

        Assert.IsTrue(diagnostic.ClientRequestId.Length <= 100);
        Assert.IsTrue(diagnostic.ServerRequestId.Length <= 100);
        Assert.IsTrue(diagnostic.CfRay.Length <= 100);
        Assert.IsTrue(diagnostic.LocalIncidentId.Length <= 100);
        Assert.IsFalse(diagnostic.ClientRequestId.Contains('\r'));
        Assert.IsFalse(diagnostic.ServerRequestId.Contains('\r'));
        Assert.IsFalse(diagnostic.CfRay.Contains('\r'));
        Assert.IsFalse(diagnostic.LocalIncidentId.Contains('\r'));
        Assert.IsFalse(supportText.Contains(string.Concat("token", "="), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(supportText.Contains("/", StringComparison.Ordinal));
        Assert.IsTrue(diagnostic.ClientRequestId.StartsWith("sha256:", StringComparison.Ordinal));
    }
}
