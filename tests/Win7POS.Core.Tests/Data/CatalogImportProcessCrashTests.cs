using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogImportProcessCrashTests
{
    [TestMethod]
    public async Task OrdinaryAndRecovery_KilledBeforeSqliteCommit_RollBackAllTablesAndRetryExactlyOnce()
    {
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var cli = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Win7POS.Cli", "bin", configuration, "net10.0", "Win7POS.Cli.dll"));
        // The canonical solution build supplies the exact current CLI; no
        // skip, old binary or in-process exception substitutes for a real kill.
        Assert.IsTrue(File.Exists(cli), "Current CLI dependency has not been built: " + cli);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host) || !File.Exists(host))
            host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        Assert.IsTrue(File.Exists(host), "Cannot resolve the current net10 runtime host.");
        var evidence = Path.Combine(Path.GetTempPath(), "Win7POS.TxCrashRegression." + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("--catalog-import-process-crash-selftest");
        start.ArgumentList.Add("--output-dir");
        start.ArgumentList.Add(evidence);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // The CLI independently bounds its handshake/child lifetime and owns
        // their cleanup. A test timeout must never kill a discovered POS process.
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        var output = await stdout;
        var error = await stderr;
        Assert.AreEqual(0, process.ExitCode, output + "\n" + error + "\nEvidence: " + evidence);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "qualification.json")));
        Assert.AreEqual("PASS", receipt.RootElement.GetProperty("status").GetString());
        foreach (var scenario in new[] { "ordinary", "recovery" })
        {
            var result = receipt.RootElement.GetProperty(scenario);
            Assert.IsTrue(result.GetProperty("killedInsideTransaction").GetBoolean());
            Assert.AreNotEqual(0, result.GetProperty("exitCode").GetInt32());
            Assert.AreEqual(0, result.GetProperty("partialPersistedEffects").GetInt32());
            Assert.AreEqual(1001, result.GetProperty("rows").GetInt32());
            Assert.AreEqual(2, result.GetProperty("partCount").GetInt32());
            Assert.IsTrue(result.GetProperty("backupPreserved").GetBoolean());
            Assert.IsTrue(result.GetProperty("originalOutboxUnchanged").GetBoolean());
            Assert.IsTrue(result.GetProperty("retryAppliedOnce").GetBoolean());
            Assert.AreEqual(result.GetProperty("before").GetRawText(), result.GetProperty("afterKill").GetRawText());
        }
        Assert.IsTrue(receipt.RootElement.GetProperty("recovery").GetProperty("draftPreservedAfterKill").GetBoolean());
        Console.WriteLine("CRASH_EVIDENCE=" + evidence);
    }
}
