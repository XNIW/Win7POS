using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Diagnostic arm only. The selected functional entry and every Recovery
    // group are shared with the legacy launcher, including fixture setup.
    internal static class RecoverySmokeEntryPoint
    {
        internal static bool TryRun(string[] args)
        {
            if (Environment.GetEnvironmentVariable("WIN7POS_QA_RECOVERY_ISOLATED_ENTRY") != "1") return false;
            if (args.Length != 5 || args[0] != "--data-dir" ||
                args[2] != "--functional-completion-smoke" || args[3] != "--scenario" ||
                args[4] != "IMPORT_RECOVERY_completion") return false;
            Run(args[1]);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Run(string dataDir)
        {
            RecoveryStartupTimeline.Record("isolated_launcher_entered");
            Directory.CreateDirectory(dataDir);
            RecoveryStartupTimeline.Attach(dataDir);
            Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", dataDir);
            Environment.SetEnvironmentVariable("WIN7POS_SAFE_START", "1");
            var errorPath = Path.Combine(dataDir, "harness-error.txt");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { File.WriteAllText(errorPath, Convert.ToString(e.ExceptionObject)); } catch { }
            };
            if (Application.ResourceAssembly == null) Application.ResourceAssembly = typeof(ProductEditDialog).Assembly;
            RecoveryStartupTimeline.Record("before_wpf_application");
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
                RecoveryStartupTimeline.Record("wpf_startup_callback_entered");
                try
                {
                    RecoveryStartupTimeline.Record("before_functional_call");
                    var result = await FunctionalCompletionSmoke.RunAsync(dataDir, false, null,
                        "IMPORT_RECOVERY_completion").ConfigureAwait(true);
                    RecoveryStartupTimeline.Record("functional_await_completed");
                    File.WriteAllText(Path.Combine(dataDir, "functional-completion.txt"), result, Encoding.UTF8);
                    RecoveryStartupTimeline.Record("result_file_written");
                    app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(errorPath, ex.ToString()); } catch { }
                    app.Shutdown(2);
                }
            };
            app.Run();
            RecoveryStartupTimeline.Record("app_run_returned");
        }
    }
}
