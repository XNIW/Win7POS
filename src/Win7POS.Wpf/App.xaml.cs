using System;
using System.Windows;
using Win7POS.Core;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                AppPaths.EnsureCreated();
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                new FileLogger().LogError(ex, "App startup failed while ensuring app paths.");
                MessageBox.Show(
                    "初始化应用数据目录失败，程序将退出。\n" + ex.Message,
                    "Win7POS 启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(-1);
            }
        }
    }
}
