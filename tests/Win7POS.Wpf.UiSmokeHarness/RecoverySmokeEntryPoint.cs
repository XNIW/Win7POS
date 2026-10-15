using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // The canonical isolated case must not JIT the 700-line launcher and its
    // async callback, whose unselected branches reference unrelated fixtures.
    // All eleven Recovery groups, resources and process budget remain identical.
    internal static class RecoverySmokeEntryPoint
    {
        internal static bool TryRun(string[] args)
        {
            if (args.Length != 5 || args[0] != "--data-dir" ||
                args[2] != "--functional-completion-smoke" || args[3] != "--scenario" ||
                args[4] != "IMPORT_RECOVERY_completion") return false;
            if (Environment.GetEnvironmentVariable("WIN7POS_QA_RECOVERY_LEGACY_ENTRY") == "1") return false;
            Run(Path.GetFullPath(args[1]));
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", dataDir);
            Environment.SetEnvironmentVariable("WIN7POS_SAFE_START", "1");
            var errorPath = Path.Combine(dataDir, "harness-error.txt");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { File.WriteAllText(errorPath, Convert.ToString(e.ExceptionObject)); } catch { }
            };
            if (Application.ResourceAssembly == null) Application.ResourceAssembly = typeof(ProductEditDialog).Assembly;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Program.AddApplicationResources(app);
            app.DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                try { File.WriteAllText(errorPath, e.Exception.ToString()); } catch { }
                app.Shutdown(2);
            };
            app.Startup += async (_, __) =>
            {
                RecordStartup(dataDir);
                string result;
                try
                {
                    // The original isolated branch also omits the unused default
                    // database. Recovery initializes its private schema in-budget.
                    await CatalogImportRecoveryCompletionSmoke.RunAsync();
                    result = "PASS" + Environment.NewLine + "PASS IMPORT_RECOVERY_completion";
                }
                catch (Exception ex)
                {
                    File.WriteAllText(errorPath, ex.ToString());
                    result = "FAIL" + Environment.NewLine + "FAIL IMPORT_RECOVERY_completion " + ex.Message;
                }
                File.WriteAllText(Path.Combine(dataDir, "functional-completion.txt"), result, Encoding.UTF8);
                app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
            };
            app.Run();
        }

        private static void RecordStartup(string dataDir)
        {
            using (var process = Process.GetCurrentProcess())
                File.WriteAllLines(Path.Combine(dataDir, "recovery-entry.txt"), new[] {
                    "entry=isolated; process_creation_to_startup_wall_ms=" + (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds,
                    "clr=" + Environment.Version + "; pointer_bytes=" + IntPtr.Size,
                    "assemblies=" + string.Join(",", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n)) });
        }
    }
}
