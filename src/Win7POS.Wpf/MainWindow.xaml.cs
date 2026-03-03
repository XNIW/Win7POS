using System;
using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly CashierModeService _cashierService = new CashierModeService();
        private readonly FileLogger _logger = new FileLogger();
        private bool _loading;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoadedAsync;
        }

        private void OnHamburgerClick(object sender, RoutedEventArgs e)
        {
            SideMenuOverlay.Visibility = SideMenuOverlay.Visibility == System.Windows.Visibility.Visible
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }

        private void OnOverlayClick(object sender, MouseButtonEventArgs e)
        {
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnPanelClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void OnMenuPosClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuImportClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 1;
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuProdottiClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 2;
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private PosViewModel GetPosViewModel()
        {
            return PosViewControl?.DataContext as PosViewModel;
        }

        private void OnMenuDailyReportClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.DailyReportCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuDbClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.DbMaintenanceCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuPrinterClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.PrinterSettingsCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuBackupClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.BackupDbCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuShopSettingsClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenShopSettingsCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuAboutClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.AboutSupportCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuSalesRegisterClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenSalesRegisterCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuPrintLastClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.PrintLastReceiptCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            try
            {
                _loading = true;
                var enabled = await _cashierService.GetCashierModeAsync().ConfigureAwait(true);
                CashierModeCheckBox.IsChecked = enabled;
                ApplyCashierMode(enabled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow OnLoaded failed");
                MessageBox.Show(this, "Si è verificato un errore durante il caricamento. Controlla i log in C:\\ProgramData\\Win7POS\\logs\\app.log.", "Win7POS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _loading = false;
            }
        }

        private async void OnCashierModeChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                _loading = true;
                ApplyCashierMode(true);
                await _cashierService.SetCashierModeAsync(true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow OnCashierModeChecked failed");
                MessageBox.Show(this, "Si è verificato un errore. Controlla i log in C:\\ProgramData\\Win7POS\\logs\\app.log.", "Win7POS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _loading = false;
            }
        }

        private async void OnCashierModeUnchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            try
            {
                var pinEnabled = await _cashierService.IsPinEnabledAsync().ConfigureAwait(true);
                if (pinEnabled)
                {
                    var prompt = new PinPromptDialog("Inserisci PIN (4 cifre)")
                    {
                        Owner = this
                    };
                    var ok = prompt.ShowDialog() == true;
                    if (!ok)
                    {
                        RevertCashierModeCheck();
                        return;
                    }

                    var result = await _cashierService.VerifyPinWithLockoutAsync(prompt.Pin).ConfigureAwait(true);
                    if (!result.Ok)
                    {
                        var message = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "PIN errato." : result.ErrorMessage;
                        MessageBox.Show(this, message, "PIN", MessageBoxButton.OK, MessageBoxImage.Warning);
                        RevertCashierModeCheck();
                        return;
                    }
                }

                _loading = true;
                try
                {
                    ApplyCashierMode(false);
                    await _cashierService.SetCashierModeAsync(false).ConfigureAwait(true);
                }
                finally
                {
                    _loading = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow OnCashierModeUnchecked failed");
                MessageBox.Show(this, "Si è verificato un errore. Controlla i log in C:\\ProgramData\\Win7POS\\logs\\app.log.", "Win7POS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RevertCashierModeCheck()
        {
            _loading = true;
            try
            {
                CashierModeCheckBox.IsChecked = true;
                ApplyCashierMode(true);
            }
            finally
            {
                _loading = false;
            }
        }

        private void ApplyCashierMode(bool enabled)
        {
            ImportTab.IsEnabled = !enabled;
            ProductsTab.IsEnabled = !enabled;
        }
    }
}
