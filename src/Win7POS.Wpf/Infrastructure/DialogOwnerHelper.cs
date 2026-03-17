using System.Windows;

namespace Win7POS.Wpf.Infrastructure
{
    public static class DialogOwnerHelper
    {
        public static Window GetSafeOwner(Window preferred = null)
        {
            return preferred ?? Application.Current?.MainWindow;
        }
    }
}
