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
                    "初始化应用数据目录失败，程序将退出。\n" + ex.Message,
                    "Win7POS 启动失败",
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
