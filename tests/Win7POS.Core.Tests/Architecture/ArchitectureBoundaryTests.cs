using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    public void ProjectTargets_AreLayeredAndWin7Compatible()
    {
        var root = FindRepoRoot();

        Assert.AreEqual("netstandard2.0", ProjectProperty(root, "src/Win7POS.Core/Win7POS.Core.csproj", "TargetFramework"));
        Assert.AreEqual("netstandard2.0", ProjectProperty(root, "src/Win7POS.Data/Win7POS.Data.csproj", "TargetFramework"));
        Assert.AreEqual("8.0", ProjectProperty(root, "src/Win7POS.Data/Win7POS.Data.csproj", "LangVersion"));
        Assert.AreEqual("net48", ProjectProperty(root, "src/Win7POS.Wpf/Win7POS.Wpf.csproj", "TargetFramework"));
        Assert.AreEqual("true", ProjectProperty(root, "src/Win7POS.Wpf/Win7POS.Wpf.csproj", "UseWPF"));
        Assert.AreEqual("x86", ProjectProperty(root, "src/Win7POS.Wpf/Win7POS.Wpf.csproj", "Platforms"));
        Assert.AreEqual("x86", ProjectProperty(root, "src/Win7POS.Wpf/Win7POS.Wpf.csproj", "PlatformTarget"));
        Assert.AreEqual("true", ProjectProperty(root, "src/Win7POS.Wpf/Win7POS.Wpf.csproj", "Prefer32Bit"));
    }

    [TestMethod]
    public void ProjectReferences_AreLayered()
    {
        var root = FindRepoRoot();

        CollectionAssert.AreEqual(Array.Empty<string>(), ProjectReferences(root, "src/Win7POS.Core/Win7POS.Core.csproj").ToArray());
        CollectionAssert.AreEqual(
            new[] { @"..\Win7POS.Core\Win7POS.Core.csproj" },
            ProjectReferences(root, "src/Win7POS.Data/Win7POS.Data.csproj").ToArray());
        CollectionAssert.AreEqual(
            new[] { @"..\Win7POS.Core\Win7POS.Core.csproj", @"..\Win7POS.Data\Win7POS.Data.csproj" },
            ProjectReferences(root, "src/Win7POS.Wpf/Win7POS.Wpf.csproj").ToArray());
        CollectionAssert.AreEqual(
            new[] { @"..\Win7POS.Core\Win7POS.Core.csproj", @"..\Win7POS.Data\Win7POS.Data.csproj" },
            ProjectReferences(root, "src/Win7POS.Cli/Win7POS.Cli.csproj").ToArray());
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

    [TestMethod]
    public void DataProject_DoesNotReferenceWpfOrDesktopUi()
    {
        var root = FindRepoRoot();

        AssertNoMatches(
            root,
            "src/Win7POS.Data",
            @"\bSystem\.Windows\b|\bWindows\.Forms\b|\bMicrosoft\.Win32\b|\bPrintDialog\b|\bPrintQueue\b|\bLocalPrintServer\b|\bPresentationFramework\b",
            ".cs",
            ".csproj");
    }

    [TestMethod]
    public void WpfProject_DoesNotUseDirectSqliteOrDapper()
    {
        var root = FindRepoRoot();

        AssertNoMatches(
            root,
            "src/Win7POS.Wpf",
            @"\bMicrosoft\.Data\.Sqlite\b|\busing\s+Dapper\s*;|\bDapper\b|\bSqliteConnection\b|\bSqliteTransaction\b",
            ".cs",
            ".csproj");
    }

    [TestMethod]
    public void PosSource_DoesNotContainSupabaseDirectClientOrSecretMarkers()
    {
        var root = FindRepoRoot();

        AssertNoMatches(
            root,
            "src",
            @"(?i)(SUPABASE_SERVICE_ROLE_KEY|NEXT_PUBLIC_SUPABASE|createClient\s*\(|supabase\.co|supabaseUrl|supabaseKey|\bservice_role\b|anon key)",
            ".cs",
            ".csproj",
            ".xaml",
            ".config",
            ".json",
            ".props",
            ".targets");
    }

    [TestMethod]
    public void PersistedOutboxPayloadBuilders_RedactSecretsAndWorkbookPaths()
    {
        var root = FindRepoRoot();
        var catalogBuilder = File.ReadAllText(Path.Combine(root, "src", "Win7POS.Data", "Online", "CatalogImportOutboxPayloadBuilder.cs"));
        var salesBuilder = File.ReadAllText(Path.Combine(root, "src", "Win7POS.Data", "Online", "PosSalesSyncRequestBuilder.cs"));

        StringAssert.DoesNotMatch(
            catalogBuilder,
            new Regex(@"deviceToken|sessionToken|trustedDeviceToken|pin|password|credential", RegexOptions.IgnoreCase));
        StringAssert.Contains(catalogBuilder, "Path.GetFileName(");
        StringAssert.Contains(salesBuilder, "SerializeRedacted");
        StringAssert.Contains(salesBuilder, "DeviceToken = null");
        StringAssert.Contains(salesBuilder, "SessionToken = null");
        StringAssert.DoesNotMatch(
            salesBuilder,
            new Regex(@"trustedDeviceToken|pin|password|credential", RegexOptions.IgnoreCase));
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

    private static string ProjectProperty(string root, string relativeProjectPath, string propertyName)
    {
        var path = Path.Combine(root, relativeProjectPath);
        var document = XDocument.Load(path);

        return document
            .Descendants(propertyName)
            .Select(node => (node.Value ?? string.Empty).Trim())
            .LastOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
    }

    private static IReadOnlyList<string> ProjectReferences(string root, string relativeProjectPath)
    {
        var path = Path.Combine(root, relativeProjectPath);
        var document = XDocument.Load(path);

        return document
            .Descendants("ProjectReference")
            .Select(node => ((string?)node.Attribute("Include") ?? string.Empty).Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static void AssertNoMatches(string root, string relativeRoot, string pattern, params string[] extensions)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        var matches = new List<string>();
        var sourceRoot = Path.Combine(root, relativeRoot);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (IsGeneratedPath(file) || !extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (regex.IsMatch(line))
                {
                    matches.Add(Path.GetRelativePath(root, file) + ":" + lineNumber);
                }
            }
        }

        Assert.AreEqual(
            0,
            matches.Count,
            "Forbidden architecture marker(s) found:" + Environment.NewLine + string.Join(Environment.NewLine, matches.Take(25)));
    }

    private static bool IsGeneratedPath(string path)
    {
        return path.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
