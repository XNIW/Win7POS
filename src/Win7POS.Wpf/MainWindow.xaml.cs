using System.Windows;

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnCashierModeChecked(object sender, RoutedEventArgs e)
        {
            ImportTab.IsEnabled = false;
            ProductsTab.IsEnabled = false;
        }

        private void OnCashierModeUnchecked(object sender, RoutedEventArgs e)
        {
            ImportTab.IsEnabled = true;
            ProductsTab.IsEnabled = true;
        }
    }
}
