using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Win7POS.Core;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf
{
    public partial class App : Application
    {
        private static readonly FileLogger _logger = new FileLogger("App");
        private const string SingleInstanceMutexName = @"Local\Win7POS.Wpf.SingleInstance";
        private const string SafeStartEnvironmentVariable = "WIN7POS_SAFE_START";
        private static Mutex _singleInstanceMutex;

        public App()
        {
            StartupTrace.Write("process entry / App constructed");
        }

        internal static bool IsSafeStart { get; private set; }

        private static void EnsureIe11WebBrowser()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.ModuleName;
                using (var k = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    k?.SetValue(exe, 11001, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("EnsureIe11WebBrowser fallito (non critico): " + ex.Message, ex);
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            StartupTrace.Write("App.OnStartup entered");
            _logger.LogInfo("App.OnStartup entered");
            IsSafeStart = IsSafeStartRequested(e?.Args);
            if (IsSafeStart)
            {
                StartupTrace.Write("safe-start enabled");
                _logger.LogInfo("Safe-start enabled");
            }

            if (!TryAcquireSingleInstance())
            {
                Shutdown(0);
                return;
            }

            EnsureTls12();
            EnsureIe11WebBrowser();
            PosLocalization.Current.SetLanguage(PosLocalization.DefaultLanguage);
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            try
            {
                StartupTrace.Write("AppPaths.EnsureCreated start");
                _logger.LogInfo("AppPaths.EnsureCreated start");
                AppPaths.EnsureCreated();
                StartupTrace.Write("AppPaths.EnsureCreated end");
                _logger.LogInfo("AppPaths.EnsureCreated done");
                base.OnStartup(e);
                StartupTrace.Write("MainWindow explicit create start");
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                StartupTrace.Write("MainWindow explicit show start");
                mainWindow.Show();
                StartupTrace.Write("MainWindow explicit show end");
                _logger.LogInfo("App.OnStartup base completed");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("App.OnStartup startup failed", ex);
                _logger.LogError(ex, "App.OnStartup: EnsureCreated/avvio fallito");
                try
                {
                    ModernMessageDialog.Show(
                        DialogOwnerHelper.GetSafeOwner(),
                        PosLocalization.T("app.startupErrorTitle"),
                        PosLocalization.T("app.localDataPrepareFailed"));
                }
                catch { }
                Shutdown(-1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_singleInstanceMutex != null)
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Single-instance guard release skipped.", ex);
            }

            base.OnExit(e);
        }

        private static bool TryAcquireSingleInstance()
        {
            try
            {
                bool createdNew;
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
                if (createdNew)
                {
                    StartupTrace.Write("single-instance mutex acquired");
                    _logger.LogInfo("Single-instance guard acquired");
                    return true;
                }

                StartupTrace.Write("single-instance mutex rejected duplicate");
                _logger.LogWarning("Single-instance guard blocked duplicate startup.");
                try
                {
                    ModernMessageDialog.Show(
                        null,
                        PosLocalization.T("app.alreadyOpenTitle"),
                        PosLocalization.T("app.alreadyOpenMessage"));
                }
                catch { }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }
            catch (Exception ex)
            {
                StartupTrace.Write("single-instance mutex failed; startup continues", ex);
                _logger.LogWarning("Single-instance guard unavailable; startup continues.", ex);
                _singleInstanceMutex = null;
                return true;
            }
        }

        private static void EnsureTls12()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
                StartupTrace.Write("TLS settings applied");
                _logger.LogInfo("TLS 1.2 ensured");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("TLS settings failed", ex);
                _logger.LogWarning("TLS 1.2 setup skipped.", ex);
            }
        }

        private static bool IsSafeStartRequested(string[] args)
        {
            foreach (var arg in args ?? new string[0])
            {
                if (string.Equals(arg, "--safe-start", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var value = Environment.GetEnvironmentVariable(SafeStartEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            StartupTrace.Write("unhandled exception", ex);
            _logger.LogError(ex, $"AppDomain.UnhandledException [IsTerminating={e.IsTerminating}]");
            if (e.IsTerminating)
            {
                try
                {
                    ModernMessageDialog.Show(
                        null,
                        PosLocalization.T("app.fatalErrorTitle"),
                        PosLocalization.T("app.unhandledErrorClose"));
                }
                catch { }
            }
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            StartupTrace.Write("unobserved task exception", e.Exception);
            _logger.LogError(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            StartupTrace.Write("dispatcher unhandled exception", e.Exception);
            _logger.LogError(e.Exception, "DispatcherUnhandledException [UI thread]");
            var mainWindow = Current?.MainWindow;
            if (mainWindow == null || !mainWindow.IsVisible)
            {
                e.Handled = true;
                Shutdown(-1);
                return;
            }

            try
            {
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("app.genericErrorTitle"),
                    PosLocalization.T("app.genericErrorCheckLog"));
            }
            catch (Exception showEx)
            {
                _logger.LogError(showEx, "OnDispatcherUnhandledException: impossibile mostrare dialog");
            }
            e.Handled = true;
        }
    }
}
