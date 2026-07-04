using System.Windows;
using System.Linq;

namespace Win7POS.Wpf.Infrastructure
{
    public static class DialogOwnerHelper
    {
        public static Window GetSafeOwner(Window preferred = null)
        {
            if (IsSafeOwner(preferred))
                return preferred;

            var windows = Application.Current?.Windows.OfType<Window>().ToList();
            if (windows != null)
            {
                var active = windows.FirstOrDefault(window => IsSafeOwner(window) && window.IsActive);
                if (active != null)
                    return active;

                var lastVisible = windows.LastOrDefault(IsSafeOwner);
                if (lastVisible != null)
                    return lastVisible;
            }

            var mainWindow = Application.Current?.MainWindow;
            return IsSafeOwner(mainWindow) ? mainWindow : null;
        }

        private static bool IsSafeOwner(Window window)
        {
            return window != null && window.IsVisible && window.IsEnabled;
        }
    }
}
