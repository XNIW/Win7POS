using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PosOnlineFirstLoginDialog : DialogShellWindow
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly PosTrustedDeviceStore _trustedDeviceStore = new PosTrustedDeviceStore();
        private CancellationTokenSource _activeCts;
        private bool _baseUrlEditedByUser;
        private bool _busy;
        private bool _initializing;
        private readonly bool _resumeCatalogOnly;
        private PosAdminWebOptions _lastOptions;

        public PosOnlineFirstLoginDialog()
            : this(new SqliteConnectionFactory(PosDbOptions.Default()))
        {
        }

        public PosOnlineFirstLoginDialog(SqliteConnectionFactory factory)
            : this(factory, false)
        {
        }

        public PosOnlineFirstLoginDialog(SqliteConnectionFactory factory, bool resumeCatalogOnly)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _resumeCatalogOnly = resumeCatalogOnly;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _initializing = true;
            DeviceNameText.Text = PosDeviceIdentity.GetStableDisplayName();
            ResetSetupSteps();

            if (PosAdminWebOptions.TryLoad(out var options, out _))
            {
                _lastOptions = options;
                BaseUrlBox.Text = options.BaseUri.ToString().TrimEnd('/');
                ServerStatusText.Text = PosLocalization.T("onlineFirstLogin.serverConfigured");
                ServerStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                ShowInsecureLanWarningIfNeeded(options);
                ConnectButton.IsEnabled = true;
                if (_resumeCatalogOnly)
                {
                    SetInputEnabled(false);
                    ConnectButton.Visibility = Visibility.Collapsed;
                    ShowProgressPanel();
                    _initializing = false;
                    _ = RunCatalogRetryAsync();
                    return;
                }

                ShopCodeBox.Focus();
                _initializing = false;
                return;
            }

            BaseUrlBox.Text = string.Empty;
            ServerStatusText.Text = PosLocalization.T("onlineFirstLogin.serverNotConfigured");
            ServerStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            AdvancedExpander.IsExpanded = true;
            ConnectButton.IsEnabled = false;
            ShowInfo(PosLocalization.T("onlineFirstLogin.configurationRequired"));
            _initializing = false;
        }

        private async void OnConnectClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            if (!PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out var options, out var reason, out var reasonCode))
            {
                ShowError(LocalizeAdminWebReason(reasonCode, reason));
                return;
            }

            var shopCode = (ShopCodeBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            var staffCode = (StaffCodeBox.Text ?? string.Empty).Trim().ToUpperInvariant();
            var credential = CredentialBox.Password ?? string.Empty;
            var displayName = PosDeviceIdentity.GetStableDisplayName();

            if (shopCode.Length == 0 || staffCode.Length == 0 || credential.Length == 0)
            {
                ShowError(PosLocalization.T("onlineFirstLogin.missingCredentials"));
                return;
            }

            _lastOptions = options;
            BeginBusySetup(PosLocalization.T("onlineFirstLogin.connecting"));

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
                using (_activeCts = new CancellationTokenSource(TimeSpan.FromMinutes(6)))
                {
                    IProgress<PosCatalogPullProgress> progress = new Progress<PosCatalogPullProgress>(UpdateSetupProgress);
                    var result = await bootstrap.BootstrapAsync(
                        options,
                        request,
                        credential,
                        _activeCts.Token,
                        progress).ConfigureAwait(true);
                    if (result.CanOpenPos)
                    {
                        DialogResult = true;
                        Close();
                        return;
                    }

                    ShowError(result.Message);
                    if (result.RequiresRetry)
                    {
                        EndBusyKeepPreparation(retryVisible: true);
                        return;
                    }

                    EndBusyAllowFreshLogin();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                if (IsVisible)
                {
                    ShowError(PosLocalization.T("onlineFirstLogin.timeout"));
                    EndBusyKeepPreparation(retryVisible: true);
                }
            }
            catch
            {
                ShowError(PosLocalization.T("onlineFirstLogin.connectionFailed"));
                EndBusyAllowFreshLogin();
            }
            finally
            {
                _activeCts = null;
                request.Credential = string.Empty;
                credential = string.Empty;
                CredentialBox.Clear();
            }
        }

        private async void OnRetryDownloadClick(object sender, RoutedEventArgs e)
        {
            await RunCatalogRetryAsync().ConfigureAwait(true);
        }

        private async Task RunCatalogRetryAsync()
        {
            if (_busy)
            {
                return;
            }

            var options = _lastOptions;
            if (options == null &&
                !PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out options, out var reason, out var reasonCode))
            {
                ShowError(LocalizeAdminWebReason(reasonCode, reason));
                EndBusyAllowFreshLogin();
                return;
            }

            _lastOptions = options;
            BeginBusySetup(PosLocalization.T("onlineFirstLogin.downloadRetry"));

            try
            {
                using (_activeCts = new CancellationTokenSource(TimeSpan.FromMinutes(6)))
                {
                    IProgress<PosCatalogPullProgress> progress = new Progress<PosCatalogPullProgress>(UpdateSetupProgress);
                    progress.Report(PosCatalogPullProgress.ForPhase("catalog"));
                    var catalogPull = new PosCatalogPullService(_factory);
                    var outcome = await catalogPull
                        .TryPullInitialCatalogAsync(options, _activeCts.Token, progress)
                        .ConfigureAwait(true);
                    if (outcome.Completed && outcome.CatalogSaleSafe)
                    {
                        UpdateSetupProgress(PosCatalogPullProgress.ForPhase("finalizing"));
                        DialogResult = true;
                        Close();
                        return;
                    }

                    ShowError(outcome.AuthDenied
                        ? PosLocalization.T("onlineFirstLogin.catalogAuthDenied")
                        : PosLocalization.T("onlineFirstLogin.catalogIncomplete"));

                    if (outcome.AuthDenied || RequiresFreshLogin(outcome))
                    {
                        EndBusyAllowFreshLogin();
                        return;
                    }

                    EndBusyKeepPreparation(retryVisible: true);
                }
            }
            catch (OperationCanceledException)
            {
                if (IsVisible)
                {
                    ShowError(PosLocalization.T("onlineFirstLogin.timeout"));
                    EndBusyKeepPreparation(retryVisible: true);
                }
            }
            catch
            {
                ShowError(PosLocalization.T("onlineFirstLogin.connectionFailed"));
                EndBusyKeepPreparation(retryVisible: true);
            }
            finally
            {
                _activeCts = null;
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
            _activeCts?.Cancel();
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

            if (PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out var options, out var reason, out var reasonCode))
            {
                ServerStatusText.Text = PosLocalization.T("onlineFirstLogin.serverConfigured");
                ServerStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
                ConnectButton.IsEnabled = true;
                ShowInsecureLanWarningIfNeeded(options);
                return;
            }

            ServerStatusText.Text = LocalizeAdminWebReason(reasonCode, reason);
            ServerStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            ConnectButton.IsEnabled = false;
        }

        private void BeginBusySetup(string message)
        {
            _busy = true;
            SetInputEnabled(false);
            RetryDownloadButton.Visibility = Visibility.Collapsed;
            RetryDownloadButton.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            ShowProgressPanel();
            ShowInfo(message);
        }

        private void EndBusyAllowFreshLogin()
        {
            _busy = false;
            SetInputEnabled(true);
            RetryDownloadButton.Visibility = Visibility.Collapsed;
            RetryDownloadButton.IsEnabled = false;
            ConnectButton.Visibility = Visibility.Visible;
            ConnectButton.IsEnabled = PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out _, out _, out _);
        }

        private void EndBusyKeepPreparation(bool retryVisible)
        {
            _busy = false;
            SetInputEnabled(false);
            ConnectButton.Visibility = Visibility.Collapsed;
            ConnectButton.IsEnabled = false;
            RetryDownloadButton.Visibility = retryVisible ? Visibility.Visible : Visibility.Collapsed;
            RetryDownloadButton.IsEnabled = retryVisible;
        }

        private void SetInputEnabled(bool enabled)
        {
            ShopCodeBox.IsEnabled = enabled;
            StaffCodeBox.IsEnabled = enabled;
            CredentialBox.IsEnabled = enabled;
            AdvancedExpander.IsEnabled = enabled;
            BaseUrlBox.IsEnabled = enabled;
        }

        private void ShowProgressPanel()
        {
            ProgressPanel.Visibility = Visibility.Visible;
            SetupProgressBar.IsIndeterminate = true;
            if (string.IsNullOrWhiteSpace(SetupPhaseText.Text))
            {
                SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.setupStarting");
            }
        }

        private void ResetSetupSteps()
        {
            SetStepText(StepAccessText, "pending", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
            SetStepText(StepDeviceText, "pending", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
            SetStepText(StepOperatorText, "pending", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
            SetStepText(StepCatalogText, "pending", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
            SetStepText(StepFinalizeText, "pending", PosLocalization.T("onlineFirstLogin.stepFinalizeLocalDb"));
            SetupPhaseText.Text = string.Empty;
            SetupCountsText.Text = string.Empty;
        }

        private void UpdateSetupProgress(PosCatalogPullProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            ShowProgressPanel();
            switch ((progress.Phase ?? string.Empty).Trim())
            {
                case "access_verified":
                    SetStepText(StepAccessText, "ok", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.stepAccessVerified");
                    break;
                case "device_linked":
                    SetStepText(StepAccessText, "ok", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetStepText(StepDeviceText, "ok", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.stepDeviceLinked");
                    break;
                case "operator_configured":
                    SetStepText(StepAccessText, "ok", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetStepText(StepDeviceText, "ok", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    SetStepText(StepOperatorText, "ok", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.stepOperatorConfigured");
                    break;
                case "finalizing":
                    SetStepText(StepCatalogText, "ok", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
                    SetStepText(StepFinalizeText, "ok", PosLocalization.T("onlineFirstLogin.stepFinalizeLocalDb"));
                    SetupProgressBar.IsIndeterminate = false;
                    SetupProgressBar.Value = 100;
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.catalogSetupComplete");
                    break;
                default:
                    SetStepText(StepCatalogText, "active", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
                    if (progress.Page > 0)
                    {
                        SetupPhaseText.Text = PosLocalization.F("onlineFirstLogin.catalogPage", progress.Page);
                    }
                    else
                    {
                        SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.stepCatalogDownload");
                    }
                    break;
            }

            if (progress.ProductsApplied > 0 ||
                progress.CategoriesReceived > 0 ||
                progress.SuppliersReceived > 0 ||
                progress.PricesApplied > 0 ||
                progress.PricesQueued > 0 ||
                progress.TombstonesReceived > 0)
            {
                SetupCountsText.Text = PosLocalization.F(
                    "onlineFirstLogin.setupCounts",
                    progress.ProductsApplied,
                    progress.CategoriesReceived,
                    progress.SuppliersReceived,
                    progress.PricesApplied,
                    progress.PricesQueued,
                    progress.PendingPricesApplied,
                    progress.TombstonesReceived,
                    progress.TombstonesApplied);
            }
        }

        private static void SetStepText(System.Windows.Controls.TextBlock target, string state, string label)
        {
            var prefix = "[ ] ";
            if (string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "[OK] ";
            }
            else if (string.Equals(state, "active", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "[..] ";
            }

            target.Text = prefix + (label ?? string.Empty);
        }

        private static bool RequiresFreshLogin(PosCatalogPullOutcome outcome)
        {
            var status = (outcome?.StatusCode ?? string.Empty).Trim();
            return string.Equals(status, "trusted_session_missing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "invalid_session", StringComparison.OrdinalIgnoreCase);
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
                ShowInfo(PosLocalization.T("onlineFirstLogin.insecureLanWarning"));
                return;
            }

            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
        }

        private static string LocalizeAdminWebReason(string reasonCode, string reason)
        {
            switch ((reasonCode ?? string.Empty).Trim())
            {
                case PosAdminWebOptions.ReasonMissingBaseUrl:
                    return PosLocalization.T("onlineFirstLogin.serverNotConfigured");
                case PosAdminWebOptions.ReasonInvalidUrl:
                    return PosLocalization.T("onlineFirstLogin.invalidUrl");
                case PosAdminWebOptions.ReasonInvalidScheme:
                    return PosLocalization.T("onlineFirstLogin.invalidScheme");
                case PosAdminWebOptions.ReasonUrlIncludesCredentials:
                    return PosLocalization.T("onlineFirstLogin.urlNoCredentials");
                case PosAdminWebOptions.ReasonUrlBaseOnly:
                    return PosLocalization.T("onlineFirstLogin.urlBaseOnly");
                case PosAdminWebOptions.ReasonHttpLoopbackOnly:
                    return PosLocalization.T("onlineFirstLogin.httpLoopbackOnly");
                default:
                    return string.IsNullOrWhiteSpace(reason)
                        ? PosLocalization.T("onlineFirstLogin.serverNotConfigured")
                        : reason;
            }
        }
    }
}
