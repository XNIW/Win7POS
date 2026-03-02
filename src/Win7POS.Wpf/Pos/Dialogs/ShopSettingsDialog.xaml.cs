using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ShopSettingsDialog : Window
    {
        public ShopSettingsDialog(ShopSettingsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
