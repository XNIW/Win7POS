using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Win7POS.Core;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf
{
    public partial class App : Application
    {
        private static readonly FileLogger _logger = new FileLogger("App");

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
            EnsureIe11WebBrowser();
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            try
            {
                AppPaths.EnsureCreated();
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "App.OnStartup: EnsureCreated/avvio fallito");
                try
                {
                    ModernMessageDialog.Show(Application.Current?.MainWindow, "Errore avvio Win7POS",
                        "Impossibile creare le directory dati. L'app verrà chiusa.\n\n" + ex.Message);
                }
                catch { }
                Shutdown(-1);
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            _logger.LogError(ex, $"AppDomain.UnhandledException [IsTerminating={e.IsTerminating}]");
            if (e.IsTerminating)
            {
                try
                {
                    ModernMessageDialog.Show(null, "Errore fatale",
                        "Errore non gestito. L'applicazione verrà chiusa.\n\nLog: " + AppPaths.LogPath);
                }
                catch { }
            }
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            _logger.LogError(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _logger.LogError(e.Exception, "DispatcherUnhandledException [UI thread]");
            try
            {
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Errore",
                    "Si è verificato un errore.\n\n" + e.Exception?.Message + "\n\nDettagli in: " + AppPaths.LogPath);
            }
            catch (Exception showEx)
            {
                _logger.LogError(showEx, "OnDispatcherUnhandledException: impossibile mostrare dialog");
            }
            e.Handled = true;
        }
    }
}
