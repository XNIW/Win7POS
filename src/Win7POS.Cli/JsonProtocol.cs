using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Win7POS.Core.Import;
using Win7POS.Core.Reports;

internal static class JsonProtocol
{
    public const int SchemaVersion = 1;

    public sealed class JsonError
    {
        public string Code = string.Empty;
        public int? Line;
        public string Message = string.Empty;
    }

    public static string BuildAnalyze(
        string dbPath,
        string csvPath,
        ImportAnalysis analysis,
        IReadOnlyList<JsonError> errors)
    {
        return BuildEnvelope(
            "analyze",
            dbPath,
            BuildAnalyzeInput(csvPath),
            BuildAnalysis(analysis),
            "null",
            "null",
            errors);
    }

    public static string BuildDiff(
        string dbPath,
        string csvPath,
        int maxItems,
        ImportAnalysis analysis,
        ImportDiffResult diff,
        IReadOnlyList<JsonError> errors)
    {
        return BuildEnvelope(
            "diff",
            dbPath,
            BuildDiffInput(csvPath, maxItems),
            BuildAnalysis(analysis),
            BuildDiffObject(diff),
            "null",
            errors);
    }

    public static string BuildApply(
        string dbPath,
        string csvPath,
        int maxItems,
        ImportAnalysis analysis,
        ImportDiffResult diff,
        ImportApplyOptions options,
        ImportApplyResult result,
        bool rolledBack,
        IReadOnlyList<JsonError> errors)
    {
        return BuildEnvelope(
            "apply",
            dbPath,
            BuildApplyInput(csvPath, maxItems),
            BuildAnalysis(analysis),
            BuildDiffObject(diff),
            BuildApplyObject(options, result, rolledBack, maxItems),
            errors);
    }

    public static string BuildDaily(
        string dbPath,
        DateTime date,
        DailyTakings report,
        IReadOnlyList<JsonError> errors)
    {
        return BuildEnvelope(
            "daily",
            dbPath,
            BuildDailyInput(date),
            "null",
            "null",
            BuildDailyApplyPlaceholder(report),
            errors);
    }

    public static string BuildSelfTest(
        string dbPath,
        bool keepDb,
        bool passed,
        IReadOnlyList<JsonError> errors)
    {
        return BuildEnvelope(
            "selftest",
            dbPath,
            BuildSelfTestInput(keepDb),
            "null",
            BuildEmptyDiffObject(),
            BuildSelfTestApply(passed),
            errors);
    }

    public static IReadOnlyList<JsonError> FromParseErrors(IReadOnlyList<ImportParseError> parseErrors)
    {
        var list = new List<JsonError>();
        if (parseErrors == null) return list;
        for (var i = 0; i < parseErrors.Count; i++)
        {
            list.Add(new JsonError
            {
                Code = "ParseError",
                Line = parseErrors[i].LineNumber,
                Message = parseErrors[i].Message ?? string.Empty
            });
        }
        return list;
    }

    public static JsonError RuntimeError(string message)
    {
        return new JsonError { Code = "RuntimeError", Message = message ?? string.Empty };
    }

    private static string BuildEnvelope(
        string command,
        string dbPath,
        string inputJson,
        string analysisJson,
        string diffJson,
        string applyJson,
        IReadOnlyList<JsonError> errors)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"schemaVersion\":").Append(SchemaVersion).Append(",");
        sb.Append("\"timestampUtc\":\"").Append(Escape(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append("\",");
        sb.Append("\"command\":\"").Append(Escape(command)).Append("\",");
        sb.Append("\"dbPath\":\"").Append(Escape(dbPath)).Append("\",");
        sb.Append("\"input\":").Append(inputJson).Append(",");
        sb.Append("\"analysis\":").Append(analysisJson).Append(",");
        sb.Append("\"diff\":").Append(diffJson).Append(",");
        sb.Append("\"apply\":").Append(applyJson).Append(",");
        sb.Append("\"errors\":").Append(BuildErrors(errors));
        sb.Append("}");
        return sb.ToString();
    }

    private static string BuildErrors(IReadOnlyList<JsonError> errors)
    {
        var sb = new StringBuilder();
        sb.Append("[");
        if (errors != null)
        {
            for (var i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var e = errors[i];
                sb.Append("{");
                sb.Append("\"code\":\"").Append(Escape(e.Code)).Append("\",");
                sb.Append("\"line\":").Append(e.Line.HasValue ? e.Line.Value.ToString() : "null").Append(",");
                sb.Append("\"message\":\"").Append(Escape(e.Message)).Append("\"");
                sb.Append("}");
            }
        }
        sb.Append("]");
        return sb.ToString();
    }

    private static string BuildAnalyzeInput(string csvPath)
    {
        return "{\"csvPath\":\"" + Escape(csvPath) + "\"}";
    }

    private static string BuildDiffInput(string csvPath, int maxItems)
    {
        return "{\"csvPath\":\"" + Escape(csvPath) + "\",\"maxItems\":" + maxItems + "}";
    }

    private static string BuildApplyInput(string csvPath, int maxItems)
    {
        return "{\"csvPath\":\"" + Escape(csvPath) + "\",\"maxItems\":" + maxItems + "}";
    }

    private static string BuildDailyInput(DateTime date)
    {
        return "{\"date\":\"" + Escape(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) + "\"}";
    }

    private static string BuildSelfTestInput(bool keepDb)
    {
        return "{\"keepDb\":" + (keepDb ? "true" : "false") + "}";
    }

    private static string BuildAnalysis(ImportAnalysis a)
    {
        return "{" +
               "\"totalRows\":" + a.TotalRows + "," +
               "\"validRows\":" + a.ValidRows + "," +
               "\"duplicates\":" + a.Duplicates + "," +
               "\"missingBarcode\":" + a.MissingBarcode + "," +
               "\"invalidPrice\":" + a.InvalidPrice + "," +
               "\"errorRows\":" + a.ErrorRows +
               "}";
    }

    private static string BuildDiffObject(ImportDiffResult diff)
    {
        var s = diff.Summary;
        return "{" +
               "\"summary\":{" +
               "\"new\":" + s.NewProduct + "," +
               "\"updatePrice\":" + s.UpdatePrice + "," +
               "\"updateName\":" + s.UpdateName + "," +
               "\"updateBoth\":" + s.UpdateBoth + "," +
               "\"noChange\":" + s.NoChange + "," +
               "\"invalidRow\":" + s.InvalidRow +
               "}," +
               "\"items\":" + BuildDiffItems(diff.Items) +
               "}";
    }

    private static string BuildEmptyDiffObject()
    {
        return "{\"summary\":{\"new\":0,\"updatePrice\":0,\"updateName\":0,\"updateBoth\":0,\"noChange\":0,\"invalidRow\":0},\"items\":[]}";
    }

    private static string BuildDiffItems(IReadOnlyList<ImportDiffItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("[");
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(",");
            var x = items[i];
            sb.Append("{");
            sb.Append("\"kind\":\"").Append(Escape(x.Kind.ToString())).Append("\",");
            sb.Append("\"barcode\":\"").Append(Escape(x.Barcode)).Append("\",");
            sb.Append("\"oldName\":").Append(x.ExistingName == null ? "null" : ("\"" + Escape(x.ExistingName) + "\"")).Append(",");
            sb.Append("\"oldPriceMinor\":").Append(x.ExistingPrice.HasValue ? x.ExistingPrice.Value.ToString() : "null").Append(",");
            sb.Append("\"newName\":\"").Append(Escape(x.IncomingName)).Append("\",");
            sb.Append("\"newPriceMinor\":").Append(x.IncomingPrice);
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private static string BuildApplyObject(ImportApplyOptions options, ImportApplyResult result, bool rolledBack, int maxItems)
    {
        var r = result ?? new ImportApplyResult();
        var previewCount = r.ChangedBarcodes.Count < maxItems ? r.ChangedBarcodes.Count : maxItems;
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"options\":{");
        sb.Append("\"dryRun\":").Append(options.DryRun ? "true" : "false").Append(",");
        sb.Append("\"insertNew\":").Append(options.InsertNew ? "true" : "false").Append(",");
        sb.Append("\"updatePrice\":").Append(options.UpdatePrice ? "true" : "false").Append(",");
        sb.Append("\"updateName\":").Append(options.UpdateName ? "true" : "false");
        sb.Append("},");
        sb.Append("\"result\":{");
        sb.Append("\"appliedInserted\":").Append(r.AppliedInserted).Append(",");
        sb.Append("\"appliedUpdated\":").Append(r.AppliedUpdated).Append(",");
        sb.Append("\"noChange\":").Append(r.NoChange).Append(",");
        sb.Append("\"skipped\":").Append(r.Skipped).Append(",");
        sb.Append("\"errorsCount\":").Append(r.ErrorsCount).Append(",");
        sb.Append("\"rolledBack\":").Append(rolledBack ? "true" : "false");
        sb.Append("},");
        sb.Append("\"preview\":{");
        sb.Append("\"maxItems\":").Append(maxItems).Append(",");
        sb.Append("\"items\":[");
        for (var i = 0; i < previewCount; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"barcode\":\"").Append(Escape(r.ChangedBarcodes[i])).Append("\"}");
        }
        sb.Append("]}");
        sb.Append("}");
        return sb.ToString();
    }

    private static string BuildDailyApplyPlaceholder(DailyTakings report)
    {
        return "{" +
               "\"options\":null," +
               "\"result\":{" +
               "\"appliedInserted\":0," +
               "\"appliedUpdated\":0," +
               "\"noChange\":0," +
               "\"skipped\":0," +
               "\"errorsCount\":0," +
               "\"rolledBack\":false" +
               "}," +
               "\"preview\":{\"maxItems\":0,\"items\":[]}," +
               "\"daily\":{" +
               "\"totalSalesCount\":" + report.TotalSalesCount + "," +
               "\"grossTotal\":" + report.GrossTotal + "," +
               "\"cashTotal\":" + report.CashTotal + "," +
               "\"cardTotal\":" + report.CardTotal + "," +
               "\"changeTotal\":" + report.ChangeTotal +
               "}}";
    }

    private static string BuildSelfTestApply(bool passed)
    {
        return "{" +
               "\"options\":null," +
               "\"result\":{" +
               "\"appliedInserted\":0," +
               "\"appliedUpdated\":0," +
               "\"noChange\":0," +
               "\"skipped\":0," +
               "\"errorsCount\":0," +
               "\"rolledBack\":false" +
               "}," +
               "\"preview\":{\"maxItems\":0,\"items\":[]}," +
               "\"selftest\":{\"passed\":" + (passed ? "true" : "false") + "}}";
    }

    private static string Escape(string value)
    {
        var text = value ?? string.Empty;
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
