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
        private bool _baseUrlEditedByUser;
        private bool _initializing;

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
            _initializing = true;
            DeviceNameText.Text = PosDeviceIdentity.GetStableDisplayName();

            if (PosAdminWebOptions.TryLoad(out var options, out _))
            {
                BaseUrlBox.Text = options.BaseUri.ToString().TrimEnd('/');
                ServerStatusText.Text = "Server Admin Web configurato.";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                ShowInsecureLanWarningIfNeeded(options);
                ConnectButton.IsEnabled = true;
                ShopCodeBox.Focus();
                _initializing = false;
                return;
            }

            BaseUrlBox.Text = string.Empty;
            ServerStatusText.Text = "URL Admin Web non configurato. Configura il server nelle impostazioni avanzate o tramite WIN7POS_ADMIN_WEB_BASE_URL.";
            ServerStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            AdvancedExpander.IsExpanded = true;
            ConnectButton.IsEnabled = false;
            ShowInfo("Il collegamento richiede prima la configurazione del server Admin Web.");
            _initializing = false;
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
            var displayName = PosDeviceIdentity.GetStableDisplayName();

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
                    DisplayName = displayName,
                },
                ShopCode = shopCode,
                StaffCode = staffCode,
            };

            try
            {
                if (_baseUrlEditedByUser)
                {
                    PosAdminWebOptions.SaveBaseUrl(options.BaseUri);
                }

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

        private void OnBaseUrlChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_initializing)
            {
                return;
            }

            _baseUrlEditedByUser = true;

            if (PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out var options, out var reason))
            {
                ServerStatusText.Text = "Server Admin Web configurato.";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                ConnectButton.IsEnabled = true;
                ShowInsecureLanWarningIfNeeded(options);
                return;
            }

            ServerStatusText.Text = reason;
            ServerStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            ConnectButton.IsEnabled = false;
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

        private void ShowInsecureLanWarningIfNeeded(PosAdminWebOptions options)
        {
            if (options != null &&
                options.BaseUri.Scheme == Uri.UriSchemeHttp &&
                !options.BaseUri.IsLoopback &&
                PosAdminWebOptions.AllowInsecureLanAdminWeb())
            {
                ShowInfo("Attenzione: HTTP LAN abilitato solo per test manuale locale. Per workers.dev/staging usa HTTPS.");
                return;
            }

            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
        }
    }
}
