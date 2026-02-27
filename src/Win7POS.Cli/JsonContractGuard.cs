using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

internal static class JsonContractGuard
{
    private static readonly string[] TopLevelOrder = new[]
    {
        "schemaVersion",
        "timestampUtc",
        "command",
        "dbPath",
        "input",
        "analysis",
        "diff",
        "apply",
        "errors"
    };

    public static bool Run(out string errorMessage)
    {
        try
        {
            RunOrThrow();
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static void RunOrThrow()
    {
        var diffFixture = ReadFixture("diff_v1.json");
        ValidateJson(diffFixture, "diff", false);
        var applyFixture = ReadFixture("apply_dryrun_v1.json");
        ValidateJson(applyFixture, "apply", true);
    }

    public static void ValidateGeneratedAgainstFixture(string currentJson, string fixtureFileName, string expectedCommand, bool requireApplyResult)
    {
        var fixtureJson = ReadFixture(fixtureFileName);
        ValidateJson(fixtureJson, expectedCommand, requireApplyResult);
        ValidateJson(currentJson, expectedCommand, requireApplyResult);

        using (var fixtureDoc = JsonDocument.Parse(fixtureJson))
        using (var currentDoc = JsonDocument.Parse(currentJson))
        {
            var fixtureKeys = GetTopLevelKeys(fixtureDoc.RootElement);
            var currentKeys = GetTopLevelKeys(currentDoc.RootElement);
            Ensure(fixtureKeys.Count == currentKeys.Count, "Top-level field count mismatch.");
            for (var i = 0; i < fixtureKeys.Count; i++)
                Ensure(fixtureKeys[i] == currentKeys[i], "Top-level field order mismatch.");
        }
    }

    public static void ValidateJson(string json, string expectedCommand, bool requireApplyResult)
    {
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            var keys = GetTopLevelKeys(root);
            Ensure(keys.Count == TopLevelOrder.Length, "Top-level field count is invalid.");
            for (var i = 0; i < TopLevelOrder.Length; i++)
                Ensure(keys[i] == TopLevelOrder[i], "Top-level field order is invalid.");

            Ensure(root.GetProperty("schemaVersion").ValueKind == JsonValueKind.Number, "schemaVersion type must be number.");
            Ensure(root.GetProperty("schemaVersion").GetInt32() == JsonProtocol.SchemaVersion, "schemaVersion mismatch.");
            Ensure(root.GetProperty("command").ValueKind == JsonValueKind.String, "command type must be string.");
            Ensure(root.GetProperty("command").GetString() == expectedCommand, "command mismatch.");
            Ensure(root.GetProperty("dbPath").ValueKind == JsonValueKind.String, "dbPath type must be string.");
            Ensure(root.GetProperty("errors").ValueKind == JsonValueKind.Array, "errors type must be array.");

            var ts = root.GetProperty("timestampUtc").GetString();
            Ensure(DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _), "timestampUtc must be ISO8601.");

            var errors = root.GetProperty("errors");
            foreach (var e in errors.EnumerateArray())
            {
                Ensure(e.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String, "errors[].code invalid.");
                Ensure(e.TryGetProperty("line", out var line) && (line.ValueKind == JsonValueKind.Number || line.ValueKind == JsonValueKind.Null), "errors[].line invalid.");
                Ensure(e.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String, "errors[].message invalid.");
            }

            var diff = root.GetProperty("diff");
            if (diff.ValueKind == JsonValueKind.Object)
            {
                Ensure(diff.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array, "diff.items missing.");
            }

            var apply = root.GetProperty("apply");
            if (requireApplyResult)
            {
                Ensure(apply.ValueKind == JsonValueKind.Object, "apply must be object.");
                Ensure(apply.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object, "apply.result missing.");
            }
        }
    }

    private static string ReadFixture(string fixtureFileName)
    {
        var path = ResolveFixturePath(fixtureFileName);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static List<string> GetTopLevelKeys(JsonElement obj)
    {
        var list = new List<string>();
        foreach (var p in obj.EnumerateObject()) list.Add(p.Name);
        return list;
    }

    private static string ResolveFixturePath(string fixtureFileName)
    {
        var fromCwd = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src/Win7POS.Cli/fixtures", fixtureFileName));
        if (File.Exists(fromCwd)) return fromCwd;
        var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src/Win7POS.Cli/fixtures", fixtureFileName));
        if (File.Exists(fromBase)) return fromBase;
        throw new FileNotFoundException("JSON fixture not found.", fixtureFileName);
    }

    private static void Ensure(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
