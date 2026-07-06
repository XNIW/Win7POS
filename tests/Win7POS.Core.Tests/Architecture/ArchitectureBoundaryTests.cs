using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Win7POS.Core.Tests.Architecture;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
    [TestMethod]
    public void ArchitectureBoundaryScript_Passes()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "scripts", "check-architecture-boundaries.ps1");
        Assert.IsTrue(File.Exists(script), "Architecture boundary script missing.");

        var start = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = "-NoProfile -File \"" + script + "\"",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(start);
        Assert.IsNotNull(process, "Failed to start PowerShell.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.AreEqual(
            0,
            process.ExitCode,
            "Architecture boundary script failed." + Environment.NewLine + output + Environment.NewLine + error);
    }

    [TestMethod]
    public void CoreProject_DoesNotOwnInfrastructurePackages()
    {
        var root = FindRepoRoot();
        var coreProject = File.ReadAllText(Path.Combine(root, "src", "Win7POS.Core", "Win7POS.Core.csproj"));
        var dataProject = File.ReadAllText(Path.Combine(root, "src", "Win7POS.Data", "Win7POS.Data.csproj"));

        StringAssert.DoesNotMatch(coreProject, new Regex("ClosedXML|ExcelDataReader|Microsoft\\.Data\\.Sqlite|Dapper"));
        StringAssert.Contains(dataProject, "ClosedXML");
        StringAssert.Contains(dataProject, "ExcelDataReader");
        StringAssert.Contains(dataProject, "Microsoft.Data.Sqlite");
        StringAssert.Contains(dataProject, "Dapper");
    }

    [TestMethod]
    public void InfrastructureAdapters_AreOwnedByDataProject()
    {
        var root = FindRepoRoot();

        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "Win7POS.Core", "Online", "PosAdminWebClient.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "Win7POS.Core", "Import", "SupplierExcelImportReader.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "Win7POS.Core", "ImportDb", "ProductDbExcelReader.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "src", "Win7POS.Core", "ImportDb", "ProductDbExcelWriter.cs")));

        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "Win7POS.Data", "Online", "PosAdminWebClient.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "Win7POS.Data", "Import", "SupplierExcelImportReader.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "Win7POS.Data", "ImportDb", "ProductDbExcelReader.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "src", "Win7POS.Data", "ImportDb", "ProductDbExcelWriter.cs")));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Win7POS.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        Assert.Fail("Could not locate repository root from " + AppContext.BaseDirectory);
        return string.Empty;
    }
}
