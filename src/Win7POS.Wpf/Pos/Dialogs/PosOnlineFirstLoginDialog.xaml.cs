using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Data;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PosOnlineFirstLoginDialog : DialogShellWindow
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly PosTrustedDeviceStore _trustedDeviceStore = new PosTrustedDeviceStore();

        public PosOnlineFirstLoginDialog()
            : this(new SqliteConnectionFactory(PosDbOptions.Default()))
        {
        }

        public PosOnlineFirstLoginDialog(SqliteConnectionFactory factory)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            DeviceNameBox.Text = Environment.MachineName;

            if (PosAdminWebOptions.TryLoad(out var options, out _))
            {
                BaseUrlBox.Text = options.BaseUri.ToString().TrimEnd('/');
                ShopCodeBox.Focus();
                return;
            }

            BaseUrlBox.Text = string.Empty;
            ShowInfo("Inserisci l'indirizzo del pannello Admin Web per collegare questo POS.");
            BaseUrlBox.Focus();
        }

        private async void OnConnectClick(object sender, RoutedEventArgs e)
        {
            if (!PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out var options, out var reason))
            {
                ShowError(reason);
                return;
            }

            var shopCode = (ShopCodeBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            var staffCode = (StaffCodeBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            var credential = CredentialBox.Password ?? string.Empty;
            var displayName = (DeviceNameBox.Text ?? string.Empty).Trim();

            if (shopCode.Length == 0 || staffCode.Length == 0 || credential.Length == 0)
            {
                ShowError("Inserisci codice negozio, codice staff e PIN/password.");
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
                PosAdminWebOptions.SaveBaseUrl(options.BaseUri);

                var bootstrap = new PosOnlineBootstrapService(
                    _factory,
                    _trustedDeviceStore);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var result = await bootstrap.BootstrapAsync(
                        options,
                        request,
                        credential,
                        cts.Token).ConfigureAwait(true);
                    if (!result.Success)
                    {
                        ShowError(result.Message);
                        ConnectButton.IsEnabled = true;
                        return;
                    }

                    DialogResult = true;
                    Close();
                }
            }
            catch
            {
                ShowError("Collegamento POS online non completato.");
                ConnectButton.IsEnabled = true;
            }
            finally
            {
                request.Credential = string.Empty;
                credential = string.Empty;
                CredentialBox.Clear();
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
