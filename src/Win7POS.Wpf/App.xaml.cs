using System;
using System.Windows;
using System.Windows.Threading;
using Win7POS.Core;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf
{
    public partial class App : Application
    {
        private static readonly FileLogger _logger = new FileLogger();

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            try
            {
                AppPaths.EnsureCreated();
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "App startup failed while ensuring app paths.");
                MessageBox.Show(
                    "Impossibile creare le directory dati dell'applicazione. L'app verrà chiusa.\n\n" + ex.Message,
                    "Errore avvio Win7POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _logger.LogError(e.Exception, "DispatcherUnhandledException");
            MessageBox.Show(
                "Si è verificato un errore.\n\n" + e.Exception.Message,
                "Errore",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
