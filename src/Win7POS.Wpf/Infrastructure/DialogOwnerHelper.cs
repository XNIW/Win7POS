using System.Windows;

namespace Win7POS.Wpf.Infrastructure
{
    public static class DialogOwnerHelper
    {
        public static Window GetSafeOwner(Window preferred = null)
        {
            if (preferred != null && preferred.IsVisible)
                return preferred;

            var mainWindow = Application.Current?.MainWindow;
            return mainWindow != null && mainWindow.IsVisible ? mainWindow : null;
        }
    }
}
