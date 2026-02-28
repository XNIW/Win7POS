using System.Windows;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly CashierModeService _cashierService = new CashierModeService();
        private bool _loading;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoadedAsync;
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            _loading = true;
            try
            {
                var enabled = await _cashierService.GetCashierModeAsync().ConfigureAwait(true);
                CashierModeCheckBox.IsChecked = enabled;
                ApplyCashierMode(enabled);
            }
            finally
            {
                _loading = false;
            }
        }

        private async void OnCashierModeChecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            _loading = true;
            try
            {
                ApplyCashierMode(true);
                await _cashierService.SetCashierModeAsync(true).ConfigureAwait(true);
            }
            finally
            {
                _loading = false;
            }
        }

        private async void OnCashierModeUnchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

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
