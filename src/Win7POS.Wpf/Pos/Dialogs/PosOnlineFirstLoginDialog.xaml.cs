using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PosOnlineFirstLoginDialog : DialogShellWindow
    {
        private static readonly FileLogger _logger = new FileLogger("PosAccessDialog");

        private readonly SqliteConnectionFactory _factory;
        private readonly bool _resumeCatalogOnly;
        private readonly PosTrustedDeviceStore _trustedDeviceStore = new PosTrustedDeviceStore();
        private readonly DispatcherTimer _networkStatusTimer;
        private CancellationTokenSource _activeCts;
        private bool _baseUrlEditedByUser;
        private bool _busy;
        private bool _initializing;
        private bool _serverOfflineNoticeShown;
        private bool _wifiOfflineNoticeShown;
        private string _activeSetupStep;
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
            _networkStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(20)
            };
            _networkStatusTimer.Tick += (_, __) => UpdateNetworkStatusBadge();
            Loaded += OnLoaded;
            Closed += (_, __) =>
            {
                _networkStatusTimer.Stop();
                _activeCts?.Cancel();
            };
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            _initializing = true;
            DeviceNameText.Text = PosDeviceIdentity.GetStableDisplayName();
            ResetSetupSteps();
            UpdateNetworkStatusBadge();
            _networkStatusTimer.Start();

            if (PosAdminWebOptions.TryLoad(out var options, out _))
            {
                _lastOptions = options;
                BaseUrlBox.Text = options.BaseUri.ToString().TrimEnd('/');
                ServerStatusText.Text = PosLocalization.T("onlineFirstLogin.serverConfigured");
                ServerStatusText.Foreground = Brushes.DarkGreen;
                ShowInsecureLanWarningIfNeeded(options);
            }
            else
            {
                BaseUrlBox.Text = string.Empty;
                ServerStatusText.Text = PosLocalization.T("onlineFirstLogin.serverNotConfigured");
                ServerStatusText.Foreground = Brushes.Firebrick;
                AdvancedExpander.IsExpanded = true;
                ShowInfo(PosLocalization.T("onlineFirstLogin.configurationRequired"));
            }

            ConnectButton.IsEnabled = true;
            await PrefillShopCodeAsync().ConfigureAwait(true);

            if (_resumeCatalogOnly)
            {
                SetInputEnabled(false);
                SetPrimaryInputFieldsVisible(false);
                SetSecondaryInputDetailsVisible(false);
                ConnectButton.Visibility = Visibility.Collapsed;
                ShowProgressPanel();
                _initializing = false;
                _ = RunCatalogRetryAsync();
                return;
            }

            FocusInitialInput();
            _initializing = false;
        }

        private async Task PrefillShopCodeAsync()
        {
            try
            {
                var settings = new SettingsRepository(_factory);
                var shopCode = await settings.GetLastPosLoginShopCodeAsync().ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(shopCode))
                {
                    shopCode = await new UserRepository(_factory)
                        .GetLastRemoteShopCodeAsync()
                        .ConfigureAwait(true);
                }

                ShopCodeBox.Text = NormalizeCode(shopCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Prefill shop code skipped.", ex);
            }
        }

        private void FocusInitialInput()
        {
            if (string.IsNullOrWhiteSpace(ShopCodeBox.Text))
            {
                ShopCodeBox.Focus();
                return;
            }

            StaffCodeBox.Focus();
        }

        private async void OnConnectClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            var shopCode = NormalizeCode(ShopCodeBox.Text);
            var staffCode = NormalizeCode(StaffCodeBox.Text);
            var credential = CredentialBox.Password ?? string.Empty;

            if (shopCode.Length == 0 || staffCode.Length == 0 || credential.Length == 0)
            {
                ShowError(PosLocalization.T("onlineFirstLogin.missingCredentials"));
                return;
            }

            var network = NetworkStatusService.Read();
            UpdateNetworkStatusBadge(network);
            if (!network.IsNetworkAvailable)
            {
                BeginBusySignIn(WifiOfflineNoticeText());
                try
                {
                    if (await TryOfflineSignInAsync(shopCode, staffCode, credential).ConfigureAwait(true))
                    {
                        return;
                    }

                    EndBusyAllowFreshLogin();
                    return;
                }
                finally
                {
                    credential = string.Empty;
                    CredentialBox.Clear();
                }
            }

            if (!PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out var options, out var reason, out var reasonCode))
            {
                ShowError(LocalizeAdminWebReason(reasonCode, reason));
                return;
            }

            _lastOptions = options;
            BeginBusySetup(PosLocalization.T("onlineFirstLogin.connecting"));

            var keepCredentialForRetry = false;
            var request = new PosFirstLoginRequest
            {
                Credential = credential,
                Device = new PosFirstLoginDevice
                {
                    AppVersion = GetAppVersion(),
                    DeviceIdentifier = PosDeviceIdentity.GetOrCreateDeviceIdentifier(),
                    DisplayName = PosDeviceIdentity.GetStableDisplayName(),
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
                        if (await CompleteOnlineSignInAsync(shopCode, staffCode, credential).ConfigureAwait(true))
                        {
                            return;
                        }

                        EndBusyAllowFreshLogin();
                        return;
                    }

                    if (IsAuthorizationDenied(result))
                    {
                        ShowError(PosLocalization.T("access.login.onlineDeniedNoOfflineFallback"));
                        EndBusyAllowFreshLogin();
                        return;
                    }

                    if (IsOfflineFallbackAllowed(result.Code))
                    {
                        ShowInfo(ServerOfflineNoticeText());
                        if (await TryOfflineSignInAsync(shopCode, staffCode, credential).ConfigureAwait(true))
                        {
                            return;
                        }

                        EndBusyAllowFreshLogin();
                        return;
                    }

                    ShowError(result.Message);
                    if (result.RequiresRetry)
                    {
                        keepCredentialForRetry = true;
                        EndBusyKeepPreparation(retryVisible: true);
                        return;
                    }

                    EndBusyAllowFreshLogin();
                }
            }
            catch (OperationCanceledException)
            {
                if (IsVisible)
                {
                    ShowInfo(ServerOfflineNoticeText());
                    if (await TryOfflineSignInAsync(shopCode, staffCode, credential).ConfigureAwait(true))
                    {
                        return;
                    }

                    EndBusyAllowFreshLogin();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "POS access online attempt failed: category=pos.access.online code=exception shopCode=" +
                    SafeAuditValue(shopCode) + " staffCode=" + SafeAuditValue(staffCode),
                    ex);
                ShowInfo(ServerOfflineNoticeText());
                if (await TryOfflineSignInAsync(shopCode, staffCode, credential).ConfigureAwait(true))
                {
                    return;
                }

                EndBusyAllowFreshLogin();
            }
            finally
            {
                _activeCts = null;
                request.Credential = string.Empty;
                if (!keepCredentialForRetry)
                {
                    credential = string.Empty;
                    CredentialBox.Clear();
                }
            }
        }

        private async Task<bool> CompleteOnlineSignInAsync(
            string shopCode,
            string staffCode,
            string credential)
        {
            var username = await new UserRepository(_factory)
                .FindRemoteStaffUsernameAsync(shopCode, staffCode)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError(PosLocalization.T("access.login.offlineMirrorMissing"));
                return false;
            }

            if (!await LoginLocalUsernameAsync(username, credential).ConfigureAwait(true))
            {
                return false;
            }

            if (!await EnsureCatalogSaleSafeForAccessAsync(logoutOnBlock: true).ConfigureAwait(true))
            {
                return false;
            }

            await SaveLastShopCodeAsync(shopCode).ConfigureAwait(true);
            MarkSetupReady();
            await Task.Delay(450).ConfigureAwait(true);
            DialogResult = true;
            Close();
            return true;
        }

        private async Task<bool> TryOfflineSignInAsync(
            string shopCode,
            string staffCode,
            string credential)
        {
            try
            {
                var username = await new UserRepository(_factory)
                    .FindRemoteStaffUsernameAsync(shopCode, staffCode)
                    .ConfigureAwait(true);

                if (string.IsNullOrWhiteSpace(username))
                {
                    ShowError(PosLocalization.T("access.login.offlineMirrorMissing"));
                    return false;
                }

                if (!await LoginLocalUsernameAsync(username, credential).ConfigureAwait(true))
                {
                    return false;
                }

                if (!await EnsureCatalogSaleSafeForAccessAsync(logoutOnBlock: true).ConfigureAwait(true))
                {
                    return false;
                }

                await SaveLastShopCodeAsync(shopCode).ConfigureAwait(true);
                _logger.LogInfo(
                    "POS access offline success: category=pos.access.offline shopCode=" +
                    SafeAuditValue(shopCode) + " staffCode=" + SafeAuditValue(staffCode));
                DialogResult = true;
                Close();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "POS access offline attempt failed: category=pos.access.offline code=exception shopCode=" +
                    SafeAuditValue(shopCode) + " staffCode=" + SafeAuditValue(staffCode),
                    ex);
                ShowError(PosLocalization.T("access.login.invalidCredentials"));
                return false;
            }
        }

        private async Task<bool> LoginLocalUsernameAsync(string username, string credential)
        {
            var session = OperatorSessionHolder.Current;
            if (session == null)
            {
                ShowError(PosLocalization.T("operator.login.sessionMissing"));
                return false;
            }

            var loginResult = await session.LoginAsync(username, credential).ConfigureAwait(true);
            switch (loginResult)
            {
                case LoginResult.LockedOut:
                    ShowError(PosLocalization.T("operator.login.locked"));
                    return false;
                case LoginResult.Failed:
                    ShowError(PosLocalization.T("access.login.invalidCredentials"));
                    return false;
                case LoginResult.Success:
                    break;
            }

            if (session.CurrentUser != null && session.CurrentUser.RequirePinChange)
            {
                session.LogSecurityEvent(
                    SecurityEventCodes.RequirePinChangeEnforced,
                    "userId=" + session.CurrentUser.Id);
                var changePinDlg = new ChangePinDialog(
                    session.CurrentUser.Id,
                    session.CurrentUser.Username)
                {
                    Owner = this
                };

                if (changePinDlg.ShowDialog() != true)
                {
                    session.LogoutForced();
                    ShowError(PosLocalization.T("operator.login.pinChangeRequired"));
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> EnsureCatalogSaleSafeForAccessAsync(bool logoutOnBlock)
        {
            if (await PosCatalogPullService.IsCatalogSaleSafeAsync(_factory).ConfigureAwait(true))
            {
                return true;
            }

            if (logoutOnBlock)
            {
                OperatorSessionHolder.Current?.LogoutForced();
            }

            ShowError(PosLocalization.T("onlineFirstLogin.catalogIncomplete"));
            return false;
        }

        private async Task SaveLastShopCodeAsync(string shopCode)
        {
            try
            {
                await new SettingsRepository(_factory)
                    .SetLastPosLoginShopCodeAsync(shopCode)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Save last shop code skipped.", ex);
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
                        if (_resumeCatalogOnly)
                        {
                            DialogResult = true;
                            Close();
                            return;
                        }

                        var credential = CredentialBox.Password ?? string.Empty;
                        if (await CompleteOnlineSignInAsync(
                                NormalizeCode(ShopCodeBox.Text),
                                NormalizeCode(StaffCodeBox.Text),
                                credential).ConfigureAwait(true))
                        {
                            CredentialBox.Clear();
                            return;
                        }

                        EndBusyAllowFreshLogin();
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
                ServerStatusText.Foreground = Brushes.DarkGreen;
                ConnectButton.IsEnabled = !_busy;
                ShowInsecureLanWarningIfNeeded(options);
                return;
            }

            ServerStatusText.Text = LocalizeAdminWebReason(reasonCode, reason);
            ServerStatusText.Foreground = Brushes.Firebrick;
            ConnectButton.IsEnabled = !_busy;
        }

        private void BeginBusySetup(string message)
        {
            _busy = true;
            _activeSetupStep = "access";
            SetInputEnabled(false);
            SetPrimaryInputFieldsVisible(false);
            SetSecondaryInputDetailsVisible(false);
            RetryDownloadButton.Visibility = Visibility.Collapsed;
            RetryDownloadButton.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            ShowProgressPanel();
            SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "active", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
            SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseVerify");
            ShowInfo(message);
        }

        private void BeginBusySignIn(string message)
        {
            _busy = true;
            SetInputEnabled(false);
            RetryDownloadButton.Visibility = Visibility.Collapsed;
            RetryDownloadButton.IsEnabled = false;
            ConnectButton.IsEnabled = false;
            ShowInfo(message);
        }

        private void EndBusyAllowFreshLogin()
        {
            _busy = false;
            SetInputEnabled(true);
            SetPrimaryInputFieldsVisible(true);
            SetSecondaryInputDetailsVisible(true);
            ProgressPanel.Visibility = Visibility.Collapsed;
            RetryDownloadButton.Visibility = Visibility.Collapsed;
            RetryDownloadButton.IsEnabled = false;
            ConnectButton.Visibility = Visibility.Visible;
            ConnectButton.IsEnabled = true;
        }

        private void EndBusyKeepPreparation(bool retryVisible)
        {
            _busy = false;
            SetInputEnabled(false);
            SetPrimaryInputFieldsVisible(false);
            SetSecondaryInputDetailsVisible(false);
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

        private void SetPrimaryInputFieldsVisible(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Collapsed;
            ShopCodeLabel.Visibility = state;
            ShopCodeBox.Visibility = state;
            StaffCodeLabel.Visibility = state;
            StaffCodeBox.Visibility = state;
            CredentialLabel.Visibility = state;
            CredentialBox.Visibility = state;
            DeviceNameLabel.Visibility = state;
            DeviceNameText.Visibility = state;
        }

        private void SetSecondaryInputDetailsVisible(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Collapsed;
            SecurityNoteText.Visibility = state;
            AdvancedExpander.Visibility = state;
        }

        private void ShowProgressPanel()
        {
            ProgressPanel.Visibility = Visibility.Visible;
            SetupProgressBar.IsIndeterminate = true;
            if (string.IsNullOrWhiteSpace(SetupPhaseTitleText.Text))
            {
                SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseConnecting");
            }

            if (string.IsNullOrWhiteSpace(SetupPhaseText.Text))
            {
                SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.setupStarting");
            }

            if (string.IsNullOrWhiteSpace(SetupCountsText.Text))
            {
                UpdateSetupCounts(null);
            }
        }

        private void ResetSetupSteps()
        {
            _activeSetupStep = null;
            SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "pending", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
            SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "pending", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
            SetStepState(StepOperatorGlyph, StepOperatorGlyphText, StepOperatorText, "pending", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
            SetStepState(StepCatalogGlyph, StepCatalogGlyphText, StepCatalogText, "pending", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
            SetStepState(StepFinalizeGlyph, StepFinalizeGlyphText, StepFinalizeText, "pending", PosLocalization.T("onlineFirstLogin.stepFinalizeLocalDb"));
            SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseConnecting");
            SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.setupStarting");
            SetupProgressBar.IsIndeterminate = true;
            SetupProgressBar.Value = 0;
            UpdateSetupCounts(null);
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
                    _activeSetupStep = "device";
                    SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "done", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "active", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    SetupProgressBar.IsIndeterminate = true;
                    SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseDevice");
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.stepAccessVerified");
                    break;
                case "device_linked":
                    _activeSetupStep = "operator";
                    SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "done", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "done", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    SetStepState(StepOperatorGlyph, StepOperatorGlyphText, StepOperatorText, "active", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
                    SetupProgressBar.IsIndeterminate = true;
                    SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseOperator");
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.stepDeviceLinked");
                    break;
                case "operator_configured":
                    _activeSetupStep = "catalog";
                    SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "done", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "done", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    SetStepState(StepOperatorGlyph, StepOperatorGlyphText, StepOperatorText, "done", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
                    SetStepState(StepCatalogGlyph, StepCatalogGlyphText, StepCatalogText, "active", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
                    SetupProgressBar.IsIndeterminate = true;
                    SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseCatalog");
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.stepOperatorConfigured");
                    break;
                case "finalizing":
                    _activeSetupStep = "finalize";
                    SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "done", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "done", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    SetStepState(StepOperatorGlyph, StepOperatorGlyphText, StepOperatorText, "done", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
                    SetStepState(StepCatalogGlyph, StepCatalogGlyphText, StepCatalogText, "done", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
                    SetStepState(StepFinalizeGlyph, StepFinalizeGlyphText, StepFinalizeText, "active", PosLocalization.T("onlineFirstLogin.stepFinalizeLocalDb"));
                    SetupProgressBar.IsIndeterminate = false;
                    SetupProgressBar.Value = 92;
                    SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseFinalizing");
                    SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.catalogSetupComplete");
                    break;
                default:
                    _activeSetupStep = "catalog";
                    SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "done", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "done", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    SetStepState(StepOperatorGlyph, StepOperatorGlyphText, StepOperatorText, "done", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
                    SetStepState(StepCatalogGlyph, StepCatalogGlyphText, StepCatalogText, "active", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
                    SetupProgressBar.IsIndeterminate = true;
                    SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseCatalog");
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

            UpdateSetupCounts(progress);
        }

        private void MarkSetupReady()
        {
            ShowProgressPanel();
            _activeSetupStep = null;
            SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "done", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
            SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "done", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
            SetStepState(StepOperatorGlyph, StepOperatorGlyphText, StepOperatorText, "done", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
            SetStepState(StepCatalogGlyph, StepCatalogGlyphText, StepCatalogText, "done", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
            SetStepState(StepFinalizeGlyph, StepFinalizeGlyphText, StepFinalizeText, "done", PosLocalization.T("onlineFirstLogin.stepFinalizeLocalDb"));
            SetupProgressBar.IsIndeterminate = false;
            SetupProgressBar.Value = 100;
            SetupPhaseTitleText.Text = PosLocalization.T("onlineFirstLogin.phaseReady");
            SetupPhaseText.Text = PosLocalization.T("onlineFirstLogin.readyMessage");
            ShowInfo(PosLocalization.T("onlineFirstLogin.readyMessage"));
        }

        private void MarkActiveSetupStepFailed()
        {
            switch ((_activeSetupStep ?? string.Empty).Trim())
            {
                case "access":
                    SetStepState(StepAccessGlyph, StepAccessGlyphText, StepAccessText, "failed", PosLocalization.T("onlineFirstLogin.stepAccessVerified"));
                    break;
                case "device":
                    SetStepState(StepDeviceGlyph, StepDeviceGlyphText, StepDeviceText, "failed", PosLocalization.T("onlineFirstLogin.stepDeviceLinked"));
                    break;
                case "operator":
                    SetStepState(StepOperatorGlyph, StepOperatorGlyphText, StepOperatorText, "failed", PosLocalization.T("onlineFirstLogin.stepOperatorConfigured"));
                    break;
                case "catalog":
                    SetStepState(StepCatalogGlyph, StepCatalogGlyphText, StepCatalogText, "failed", PosLocalization.T("onlineFirstLogin.stepCatalogDownload"));
                    break;
                case "finalize":
                    SetStepState(StepFinalizeGlyph, StepFinalizeGlyphText, StepFinalizeText, "failed", PosLocalization.T("onlineFirstLogin.stepFinalizeLocalDb"));
                    break;
            }
        }

        private void UpdateSetupCounts(PosCatalogPullProgress progress)
        {
            var p = progress ?? new PosCatalogPullProgress();
            SetupCountsText.Text = PosLocalization.F(
                "onlineFirstLogin.setupCounts",
                p.ProductsApplied,
                p.CategoriesReceived,
                p.SuppliersReceived,
                p.PricesApplied,
                p.PricesQueued,
                p.PendingPricesApplied,
                p.TombstonesReceived,
                p.TombstonesApplied);
        }

        private static void SetStepState(Border glyph, TextBlock glyphText, TextBlock labelText, string state, string label)
        {
            var normalized = (state ?? string.Empty).Trim().ToLowerInvariant();
            labelText.Text = label ?? string.Empty;
            labelText.FontWeight = FontWeights.Normal;
            labelText.Foreground = new SolidColorBrush(Color.FromRgb(81, 74, 94));

            glyphText.Text = string.Empty;
            glyph.BorderBrush = new SolidColorBrush(Color.FromRgb(184, 176, 194));
            glyph.Background = Brushes.White;
            glyphText.Foreground = new SolidColorBrush(Color.FromRgb(184, 176, 194));

            if (normalized == "active")
            {
                glyph.BorderBrush = new SolidColorBrush(Color.FromRgb(123, 90, 142));
                glyph.Background = new SolidColorBrush(Color.FromRgb(241, 232, 248));
                glyphText.Foreground = new SolidColorBrush(Color.FromRgb(94, 63, 134));
                glyphText.Text = "...";
                labelText.FontWeight = FontWeights.SemiBold;
                labelText.Foreground = new SolidColorBrush(Color.FromRgb(75, 46, 103));
                return;
            }

            if (normalized == "done")
            {
                glyph.BorderBrush = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                glyph.Background = new SolidColorBrush(Color.FromRgb(232, 242, 237));
                glyphText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                glyphText.Text = "✓";
                return;
            }

            if (normalized == "failed")
            {
                glyph.BorderBrush = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                glyph.Background = new SolidColorBrush(Color.FromRgb(253, 236, 234));
                glyphText.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                glyphText.Text = "X";
                labelText.FontWeight = FontWeights.SemiBold;
                labelText.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
            }
        }

        private void UpdateNetworkStatusBadge()
        {
            UpdateNetworkStatusBadge(NetworkStatusService.Read());
        }

        private void UpdateNetworkStatusBadge(NetworkStatusSnapshot status)
        {
            var online = status != null && status.IsNetworkAvailable;
            var foreground = online
                ? new SolidColorBrush(Color.FromRgb(46, 125, 50))
                : new SolidColorBrush(Color.FromRgb(198, 40, 40));

            NetworkStatusText.Text = PosLocalization.T(online
                ? "access.login.networkOnlineShort"
                : "access.login.networkOfflineShort");
            NetworkStatusText.Foreground = foreground;
            NetworkWifiArcLarge.Stroke = foreground;
            NetworkWifiArcSmall.Stroke = foreground;
            NetworkWifiDot.Fill = foreground;
            NetworkOfflineX.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
            NetworkOfflineHintText.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
            NetworkStatusBadge.ToolTip = PosLocalization.T(online
                ? "access.login.networkOnlineDetail"
                : "access.login.networkOfflineDetail");
            NetworkStatusBadge.Background = online
                ? new SolidColorBrush(Color.FromRgb(232, 242, 237))
                : new SolidColorBrush(Color.FromRgb(255, 244, 229));
            NetworkStatusBadge.BorderBrush = online
                ? new SolidColorBrush(Color.FromRgb(191, 217, 201))
                : new SolidColorBrush(Color.FromRgb(239, 190, 130));
        }

        private static bool RequiresFreshLogin(PosCatalogPullOutcome outcome)
        {
            var status = (outcome?.StatusCode ?? string.Empty).Trim();
            return string.Equals(status, "trusted_session_missing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "invalid_session", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAuthorizationDenied(PosOnlineBootstrapResult result)
        {
            return result != null &&
                (result.Denied || IsAuthorizationDeniedCode(result.Code));
        }

        private static bool IsAuthorizationDeniedCode(string code)
        {
            var normalized = NormalizeSafeCode(code);
            return string.Equals(normalized, "authorizationfailed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "authorization_failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "denied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "invalid_credential", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "invalid_credentials", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "revoked", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "auth_denied", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOfflineFallbackAllowed(string code)
        {
            var normalized = NormalizeSafeCode(code);
            return string.Equals(normalized, "timeout", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "network_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "io_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "connection_failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "failure", StringComparison.OrdinalIgnoreCase);
        }

        private string WifiOfflineNoticeText()
        {
            if (_wifiOfflineNoticeShown)
            {
                return PosLocalization.T("common.loading");
            }

            _wifiOfflineNoticeShown = true;
            return PosLocalization.T("access.login.offlineNoticeWifiUnavailable");
        }

        private string ServerOfflineNoticeText()
        {
            if (_serverOfflineNoticeShown)
            {
                return PosLocalization.T("common.loading");
            }

            _serverOfflineNoticeShown = true;
            return PosLocalization.T("access.login.offlineNoticeServerUnavailable");
        }

        private void ShowInfo(string message)
        {
            if (StatusBanner != null)
            {
                StatusBanner.Background = new SolidColorBrush(Color.FromRgb(245, 237, 249));
                StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(218, 201, 232));
                StatusBanner.Visibility = Visibility.Visible;
            }

            StatusText.Foreground = Brushes.DarkSlateBlue;
            StatusText.Text = message ?? string.Empty;
            StatusText.Visibility = Visibility.Visible;
        }

        private void ShowError(string message)
        {
            if (ProgressPanel != null && ProgressPanel.Visibility == Visibility.Visible)
            {
                MarkActiveSetupStepFailed();
            }

            if (StatusBanner != null)
            {
                StatusBanner.Background = new SolidColorBrush(Color.FromRgb(253, 236, 234));
                StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 170, 165));
                StatusBanner.Visibility = Visibility.Visible;
            }

            StatusText.Foreground = Brushes.Firebrick;
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

            if (StatusBanner != null)
            {
                StatusBanner.Visibility = Visibility.Collapsed;
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

        private static string NormalizeCode(string value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string NormalizeSafeCode(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string SafeAuditValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();

            if (normalized.Length > 80)
            {
                return normalized.Substring(0, 80);
            }

            return normalized;
        }
    }
}
