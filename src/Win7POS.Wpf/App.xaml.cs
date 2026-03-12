using System;
using System.Diagnostics;
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
        private static readonly FileLogger _logger = new FileLogger();

        private static void EnsureIe11WebBrowser()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.ModuleName;
                using (var k = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    // IE11 Edge mode
                    k?.SetValue(exe, 11001, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            EnsureIe11WebBrowser();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            try
            {
                AppPaths.EnsureCreated();
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "App startup failed while ensuring app paths.");
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Errore avvio Win7POS",
                    "Impossibile creare le directory dati dell'applicazione. L'app verrà chiusa.\n\n" + ex.Message);
                Shutdown(-1);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _logger.LogError(e.Exception, "DispatcherUnhandledException");
            ModernMessageDialog.Show(Application.Current?.MainWindow, "Errore",
                "Si è verificato un errore.\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
