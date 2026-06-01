using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PosOnlineFirstLoginDialog : DialogShellWindow
    {
        private readonly PosTrustedDeviceStore _trustedDeviceStore = new PosTrustedDeviceStore();
        private PosAdminWebOptions _options;

        public PosOnlineFirstLoginDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            DeviceNameBox.Text = Environment.MachineName;

            if (PosAdminWebOptions.TryLoad(out _options, out var reason))
            {
                BaseUrlText.Text = _options.BaseUri.ToString();
                ShopCodeBox.Focus();
                return;
            }

            BaseUrlText.Text = "Non configurato";
            ShowError(reason);
            ConnectButton.IsEnabled = false;
        }

        private async void OnConnectClick(object sender, RoutedEventArgs e)
        {
            if (_options == null)
            {
                ShowError("Configura Admin Web POS prima di collegare il dispositivo.");
                return;
            }

            var shopCode = (ShopCodeBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            var staffCode = (StaffCodeBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            var credential = CredentialBox.Password ?? string.Empty;
            var displayName = (DeviceNameBox.Text ?? string.Empty).Trim();

            if (shopCode.Length == 0 || staffCode.Length == 0 || credential.Length == 0)
            {
                ShowError("Inserisci shop code, staff code e PIN/password.");
                return;
            }

            ConnectButton.IsEnabled = false;
            ShowInfo("Collegamento in corso...");

            var request = new PosFirstLoginRequest
            {
                Credential = credential,
                Device = new PosFirstLoginDevice
                {
                    AppVersion = GetAppVersion(),
                    DeviceIdentifier = PosDeviceIdentity.GetOrCreateDeviceIdentifier(),
                    DisplayName = string.IsNullOrWhiteSpace(displayName)
                        ? Environment.MachineName
                        : displayName,
                },
                ShopCode = shopCode,
                StaffCode = staffCode,
            };

            try
            {
                using (var client = new PosAdminWebClient(_options))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    var result = await client.FirstLoginAsync(request, cts.Token).ConfigureAwait(true);
                    CredentialBox.Clear();

                    if (!result.Success || result.Value == null)
                    {
                        ShowError(result.Message);
                        ConnectButton.IsEnabled = true;
                        return;
                    }

                    _trustedDeviceStore.SaveFirstLogin(result.Value);
                    DialogResult = true;
                    Close();
                }
            }
            catch
            {
                CredentialBox.Clear();
                ShowError("Collegamento POS online non completato.");
                ConnectButton.IsEnabled = true;
            }
        }

        private static string GetAppVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowInfo(string message)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.DarkSlateBlue;
            StatusText.Text = message ?? string.Empty;
            StatusText.Visibility = Visibility.Visible;
        }

        private void ShowError(string message)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = message ?? string.Empty;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
