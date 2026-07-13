using System;
using System.Windows;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SettingsHubDialog : DialogShellWindow
    {
        public event EventHandler ShopDataRequested;
        public event EventHandler PrinterSettingsRequested;
        public event EventHandler DatabaseMaintenanceRequested;
        public event EventHandler UsersRolesRequested;
        public event EventHandler AboutRequested;
        public event EventHandler OnlineAccessRequested;
        public event EventHandler<string> LanguageChangedRequested;

        public SettingsHubDialog(bool recoveryMode = false)
        {
            InitializeComponent();
            if (recoveryMode)
            {
                ShopDataButton.Visibility = Visibility.Collapsed;
                PrinterSettingsButton.Visibility = Visibility.Collapsed;
                UsersRolesButton.Visibility = Visibility.Collapsed;
                OnlineAccessButton.Visibility = Visibility.Visible;
            }
        }

        private void CloseAndRaise(EventHandler handler)
        {
            Close();
            handler?.Invoke(this, EventArgs.Empty);
        }

        private void OnShopDataClick(object sender, RoutedEventArgs e)
        {
            CloseAndRaise(ShopDataRequested);
        }

        private void OnPrinterSettingsClick(object sender, RoutedEventArgs e)
        {
            CloseAndRaise(PrinterSettingsRequested);
        }

        private void OnDatabaseMaintenanceClick(object sender, RoutedEventArgs e)
        {
            CloseAndRaise(DatabaseMaintenanceRequested);
        }

        private void OnUsersRolesClick(object sender, RoutedEventArgs e)
        {
            CloseAndRaise(UsersRolesRequested);
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            CloseAndRaise(AboutRequested);
        }

        private void OnOnlineAccessClick(object sender, RoutedEventArgs e)
        {
            CloseAndRaise(OnlineAccessRequested);
        }

        private void OnLanguageClick(object sender, RoutedEventArgs e)
        {
            var selected = LanguageSettingsDialog.ShowDialog(this);
            if (!string.IsNullOrWhiteSpace(selected))
                LanguageChangedRequested?.Invoke(this, selected);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
