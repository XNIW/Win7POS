using System;
using System.Diagnostics;
using System.Globalization;
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
using Win7POS.Data.Online;
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
        private bool _authenticatedRecoveryAvailable;
        private bool _localRecoveryLoginMode;
        private string _activeSetupStep;
        private string _lastAccessAttemptId;
        private PosAccessRecoveryDecision _recoveryDecision;
        private PosUserBootstrapState _bootstrapState = new PosUserBootstrapState();
        private PosAdminWebOptions _lastOptions;

        public PosAuthenticatedAccessMode AccessMode { get; private set; } = PosAuthenticatedAccessMode.Normal;

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

            try
            {
                _bootstrapState = await new UserRepository(_factory)
                    .GetBootstrapStateAsync()
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogAccessWarning(null, "bootstrap_state", "result=failed exceptionType=" + SafeAuditValue(ex.GetType().Name), ex);
                ShowError(PosLocalization.T("firstRun.localSetupFailed"));
            }

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

            LogAccessInfo(
                null,
                "dialog_open",
                "result=shown resumeCatalogOnly=" + BoolText(_resumeCatalogOnly) +
                " adminUrlConfigured=" + BoolText(_lastOptions != null));
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

            var startupFailure = _lastOptions == null
                ? PosAccessFailureKind.ServerNotConfigured
                : PosAccessFailureKind.None;
            ApplyRecoveryDecision(
                PosAccessRecoveryPolicy.Evaluate(_bootstrapState, startupFailure),
                showTransientHelp: _lastOptions == null);

            FocusInitialInput();
            _initializing = false;
        }

        private async Task PrefillShopCodeAsync()
        {
            try
            {
                var settings = new SettingsRepository(_factory);
                var shopCode = await settings.GetLastPosLoginShopCodeAsync().ConfigureAwait(true);
                var source = "last_pos_login";
                if (string.IsNullOrWhiteSpace(shopCode))
                {
                    source = "remote_mirror";
                    shopCode = await new UserRepository(_factory)
                        .GetLastRemoteShopCodeAsync()
                        .ConfigureAwait(true);
                }

                ShopCodeBox.Text = NormalizeCode(shopCode);
                if (string.IsNullOrWhiteSpace(ShopCodeBox.Text))
                {
                    LogAccessInfo(null, "prefill_shop_code", "result=empty");
                }
                else
                {
                    LogAccessInfo(
                        null,
                        "prefill_shop_code",
                        "result=success source=" + SafeAuditValue(source) +
                        " shopCodePresent=yes");
                }
            }
            catch (Exception ex)
            {
                LogAccessWarning(
                    null,
                    "prefill_shop_code",
                    "result=skipped exceptionType=" + SafeAuditValue(ex.GetType().Name));
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

            if (_localRecoveryLoginMode)
            {
                await RunLocalRecoveryLoginAsync().ConfigureAwait(true);
                return;
            }

            var attemptId = CreateAccessAttemptId();
            _lastAccessAttemptId = attemptId;
            var attemptTimer = Stopwatch.StartNew();
            var attemptFinished = false;
            var shopCode = NormalizeCode(ShopCodeBox.Text);
            var staffCode = NormalizeCode(StaffCodeBox.Text);
            var credential = CredentialBox.Password ?? string.Empty;
            var network = NetworkStatusService.Read();
            UpdateNetworkStatusBadge(network);
            LogAccessInfo(
                attemptId,
                "start",
                "shopCodeProvided=" + BoolText(shopCode.Length > 0) +
                " staffCodeProvided=" + BoolText(staffCode.Length > 0) +
                " network=" + NetworkText(network) +
                " wifiUp=" + BoolText(network != null && network.HasWifiAdapterUp) +
                " adminUrlConfigured=" + BoolText(!string.IsNullOrWhiteSpace(BaseUrlBox.Text)));

            if (shopCode.Length == 0 || staffCode.Length == 0 || credential.Length == 0)
            {
                LogAccessWarning(
                    attemptId,
                    "validation_result",
                    "result=failed reason=missing_required_fields missingShop=" + BoolText(shopCode.Length == 0) +
                    " missingStaff=" + BoolText(staffCode.Length == 0) +
                    " missingAuth=" + BoolText(credential.Length == 0));
                ShowError(PosLocalization.T("onlineFirstLogin.missingCredentials"));
                FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "none", "validation_failed");
                return;
            }

            if (!network.IsNetworkAvailable)
            {
                var offlineDecision = PosAccessRecoveryPolicy.Evaluate(
                    _bootstrapState,
                    PosAccessFailureKind.NetworkUnavailable);
                if (!offlineDecision.CanUseOfflineMirror)
                {
                    ApplyRecoveryDecision(offlineDecision, showTransientHelp: true);
                    FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "blocked", "none", "network_offline");
                    credential = string.Empty;
                    CredentialBox.Clear();
                    return;
                }

                BeginBusySignIn(WifiOfflineNoticeText());
                LogAccessWarning(
                    attemptId,
                    "offline_fallback",
                    "reason=network_offline fallback=offline allowed=yes");
                try
                {
                    if (await TryOfflineSignInAsync(shopCode, staffCode, credential, attemptId).ConfigureAwait(true))
                    {
                        FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "success", "offline", string.Empty);
                        return;
                    }

                    FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "offline", "offline_login_failed");
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
                LogAccessWarning(
                    attemptId,
                    "admin_url_result",
                    "result=failed reason=" + SafeAuditValue(reasonCode) +
                    " adminUrlConfigured=" + BoolText(!string.IsNullOrWhiteSpace(BaseUrlBox.Text)));
                ShowError(LocalizeAdminWebReason(reasonCode, reason));
                FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "none", "admin_url_invalid");
                return;
            }

            _lastOptions = options;
            LogAccessInfo(
                attemptId,
                "admin_url_result",
                "result=success adminUrlConfigured=yes adminUrlSource=" + SafeAuditValue(options.BaseUrlSource.ToString()));
            BeginBusySetup(PosLocalization.T("onlineFirstLogin.connecting"));

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
                    LogAccessInfo(attemptId, "online_bootstrap_start", "adminUrlConfigured=yes");
                    var result = await bootstrap.BootstrapAsync(
                        options,
                        request,
                        credential,
                        _activeCts.Token,
                        progress).ConfigureAwait(true);

                    LogAccessBootstrapResult(attemptId, result);
                    if (result.CanOpenPos)
                    {
                        if (await CompleteOnlineSignInAsync(shopCode, staffCode, credential, attemptId, "online").ConfigureAwait(true))
                        {
                            FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "success", "online", string.Empty);
                            return;
                        }

                        FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "online", "local_login_or_catalog_blocked");
                        EndBusyAllowFreshLogin();
                        return;
                    }

                    if (result.Success && !result.CanOpenPos)
                    {
                        ShowError(result.Message);
                        _authenticatedRecoveryAvailable = await LoginRemoteMirrorForRecoveryAsync(
                            shopCode,
                            staffCode,
                            credential,
                            attemptId).ConfigureAwait(true);
                        if (_authenticatedRecoveryAvailable)
                        {
                            AccessMode = PosAuthenticatedAccessMode.LocalRecovery;
                            RecoveryButtonText.Text = PosLocalization.T("access.login.localRecovery");
                            RecoveryButton.Visibility = Visibility.Visible;
                            RecoveryButton.IsEnabled = true;
                        }

                        FinishAccessAttempt(
                            ref attemptFinished,
                            attemptId,
                            attemptTimer,
                            "blocked",
                            "online",
                            "catalog_not_sale_safe");
                        EndBusyKeepPreparation(retryVisible: result.RequiresRetry);
                        return;
                    }

                    var failureKind = PosAccessRecoveryPolicy.ClassifyOnlineFailure(result.Code, result.Denied);
                    var recoveryDecision = PosAccessRecoveryPolicy.Evaluate(_bootstrapState, failureKind);
                    if (recoveryDecision.NextStep == PosAccessNextStep.Denied || IsAuthorizationDenied(result))
                    {
                        LogAccessWarning(
                            attemptId,
                            "online_bootstrap_denied",
                            "result=blocked reason=auth_denied code=" + SafeAuditValue(result.Code));
                        ShowError(PosLocalization.T("access.login.onlineDeniedNoOfflineFallback"));
                        ApplyRecoveryDecision(recoveryDecision, showTransientHelp: false);
                        FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "blocked", "online", "auth_denied");
                        EndBusyAllowFreshLogin();
                        return;
                    }

                    if (IsOfflineFallbackAllowed(result.Code))
                    {
                        if (!recoveryDecision.CanUseOfflineMirror)
                        {
                            FinishAccessAttempt(
                                ref attemptFinished,
                                attemptId,
                                attemptTimer,
                                "blocked",
                                "none",
                                SafeAuditValue(result.Code));
                            EndBusyAllowFreshLogin();
                            ApplyRecoveryDecision(recoveryDecision, showTransientHelp: true);
                            return;
                        }

                        LogAccessWarning(
                            attemptId,
                            "offline_fallback",
                            "reason=" + SafeAuditValue(result.Code) +
                            " fallback=offline allowed=yes");
                        ShowInfo(ServerOfflineNoticeText());
                        if (await TryOfflineSignInAsync(shopCode, staffCode, credential, attemptId).ConfigureAwait(true))
                        {
                            FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "success", "offline", string.Empty);
                            return;
                        }

                        FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "offline", "offline_login_failed");
                        EndBusyAllowFreshLogin();
                        return;
                    }

                    ShowError(result.Message);
                    if (result.RequiresRetry)
                    {
                        FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "blocked", "online", "catalog_retry_required");
                        EndBusyKeepPreparation(retryVisible: true);
                        return;
                    }

                    FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "online", SafeAuditValue(result.Code));
                    EndBusyAllowFreshLogin();
                }
            }
            catch (OperationCanceledException)
            {
                if (IsVisible)
                {
                    LogAccessWarning(attemptId, "online_timeout", "result=timeout fallback=offline");
                    var timeoutDecision = PosAccessRecoveryPolicy.Evaluate(
                        _bootstrapState,
                        PosAccessFailureKind.Timeout);
                    if (!timeoutDecision.CanUseOfflineMirror)
                    {
                        FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "blocked", "none", "timeout");
                        EndBusyAllowFreshLogin();
                        ApplyRecoveryDecision(timeoutDecision, showTransientHelp: true);
                        return;
                    }

                    ShowInfo(ServerOfflineNoticeText());
                    if (await TryOfflineSignInAsync(shopCode, staffCode, credential, attemptId).ConfigureAwait(true))
                    {
                        FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "success", "offline", string.Empty);
                        return;
                    }

                    FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "offline", "timeout_offline_failed");
                    EndBusyAllowFreshLogin();
                    return;
                }

                FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "cancelled", "online", "dialog_closed");
            }
            catch (Exception ex)
            {
                LogAccessWarning(
                    attemptId,
                    "online_exception",
                    "result=exception fallback=offline exceptionType=" + SafeAuditValue(ex.GetType().Name),
                    ex);
                var exceptionDecision = PosAccessRecoveryPolicy.Evaluate(
                    _bootstrapState,
                    PosAccessFailureKind.ServerUnavailable);
                if (!exceptionDecision.CanUseOfflineMirror)
                {
                    FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "blocked", "none", "exception");
                    EndBusyAllowFreshLogin();
                    ApplyRecoveryDecision(exceptionDecision, showTransientHelp: true);
                    return;
                }

                ShowInfo(ServerOfflineNoticeText());
                if (await TryOfflineSignInAsync(shopCode, staffCode, credential, attemptId).ConfigureAwait(true))
                {
                    FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "success", "offline", string.Empty);
                    return;
                }

                FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "failed", "offline", "exception_offline_failed");
                EndBusyAllowFreshLogin();
            }
            finally
            {
                _activeCts = null;
                request.Credential = string.Empty;
                credential = string.Empty;
                CredentialBox.Clear();

                if (!attemptFinished)
                {
                    FinishAccessAttempt(ref attemptFinished, attemptId, attemptTimer, "cancelled", "unknown", "attempt_exited");
                }
            }
        }

        private async Task<bool> CompleteOnlineSignInAsync(
            string shopCode,
            string staffCode,
            string credential,
            string attemptId,
            string mode)
        {
            LogAccessInfo(attemptId, "offline_mirror_start", "mode=" + SafeAuditValue(mode));
            var username = await new UserRepository(_factory)
                .FindRemoteStaffUsernameAsync(shopCode, staffCode)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(username))
            {
                LogAccessWarning(attemptId, "offline_mirror_result", "result=missing mode=" + SafeAuditValue(mode));
                ShowError(PosLocalization.T("access.login.offlineMirrorMissing"));
                return false;
            }

            LogAccessInfo(attemptId, "offline_mirror_result", "result=success mode=" + SafeAuditValue(mode));
            if (!await LoginLocalUsernameAsync(username, credential, attemptId, mode).ConfigureAwait(true))
            {
                return false;
            }

            if (!await EnsureCatalogSaleSafeForAccessAsync(logoutOnBlock: true, attemptId: attemptId, mode: mode).ConfigureAwait(true))
            {
                return false;
            }

            await SaveLastShopCodeAsync(shopCode).ConfigureAwait(true);
            MarkSetupReady();
            await Task.Delay(450).ConfigureAwait(true);
            AccessMode = PosAuthenticatedAccessMode.Normal;
            DialogResult = true;
            Close();
            return true;
        }

        private async Task<bool> TryOfflineSignInAsync(
            string shopCode,
            string staffCode,
            string credential,
            string attemptId)
        {
            LogAccessInfo(
                attemptId,
                "offline_login_start",
                "shopCodeProvided=" + BoolText(!string.IsNullOrWhiteSpace(shopCode)) +
                " staffCodeProvided=" + BoolText(!string.IsNullOrWhiteSpace(staffCode)));
            try
            {
                PosTrustedDeviceSession trustedSession = null;
                _trustedDeviceStore.TryRead(out trustedSession);
                var offlineShopAuthorized = await new PosShopTransitionGuard(_factory)
                    .IsOfflineShopAuthorizedAsync(
                        trustedSession?.ShopId,
                        trustedSession?.ShopCode,
                        shopCode)
                    .ConfigureAwait(true);
                if (!offlineShopAuthorized)
                {
                    LogAccessWarning(
                        attemptId,
                        "offline_login_result",
                        "result=blocked reason=shop_identity_mismatch");
                    ShowError(PosLocalization.T("access.login.offlineMirrorMissing"));
                    return false;
                }

                LogAccessInfo(attemptId, "offline_mirror_start", "mode=offline");
                var username = await new UserRepository(_factory)
                    .FindRemoteStaffUsernameAsync(shopCode, staffCode)
                    .ConfigureAwait(true);

                if (string.IsNullOrWhiteSpace(username))
                {
                    LogAccessWarning(attemptId, "offline_mirror_result", "result=missing mode=offline");
                    LogAccessWarning(attemptId, "offline_login_result", "result=failed reason=mirror_missing");
                    ShowError(PosLocalization.T("access.login.offlineMirrorMissing"));
                    return false;
                }

                LogAccessInfo(attemptId, "offline_mirror_result", "result=success mode=offline");
                if (!await LoginLocalUsernameAsync(username, credential, attemptId, "offline").ConfigureAwait(true))
                {
                    LogAccessWarning(attemptId, "offline_login_result", "result=failed reason=local_login_failed");
                    return false;
                }

                if (!await EnsureCatalogSaleSafeForAccessAsync(logoutOnBlock: true, attemptId: attemptId, mode: "offline").ConfigureAwait(true))
                {
                    LogAccessWarning(attemptId, "offline_login_result", "result=blocked reason=catalog_not_sale_safe");
                    return false;
                }

                await SaveLastShopCodeAsync(shopCode).ConfigureAwait(true);
                LogAccessInfo(attemptId, "offline_login_result", "result=success");
                AccessMode = PosAuthenticatedAccessMode.Normal;
                DialogResult = true;
                Close();
                return true;
            }
            catch (Exception ex)
            {
                LogAccessWarning(
                    attemptId,
                    "offline_login_result",
                    "result=exception exceptionType=" + SafeAuditValue(ex.GetType().Name),
                    ex);
                ShowError(PosLocalization.T("access.login.invalidCredentials"));
                return false;
            }
        }

        private async Task<bool> LoginLocalUsernameAsync(
            string username,
            string credential,
            string attemptId,
            string mode)
        {
            LogAccessInfo(attemptId, "local_login_start", "mode=" + SafeAuditValue(mode));
            var session = OperatorSessionHolder.Current;
            if (session == null)
            {
                LogAccessWarning(attemptId, "local_login_result", "result=failed reason=session_missing mode=" + SafeAuditValue(mode));
                ShowError(PosLocalization.T("operator.login.sessionMissing"));
                return false;
            }

            var loginResult = await session.LoginAsync(username, credential).ConfigureAwait(true);
            switch (loginResult)
            {
                case LoginResult.LockedOut:
                    LogAccessWarning(attemptId, "local_login_result", "result=failed reason=locked_out mode=" + SafeAuditValue(mode));
                    ShowError(PosLocalization.T("operator.login.locked"));
                    return false;
                case LoginResult.Failed:
                    LogAccessWarning(attemptId, "local_login_result", "result=failed reason=invalid_credentials mode=" + SafeAuditValue(mode));
                    ShowError(PosLocalization.T("access.login.invalidCredentials"));
                    return false;
                case LoginResult.Success:
                    LogAccessInfo(attemptId, "local_login_result", "result=success mode=" + SafeAuditValue(mode));
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
                    LogAccessWarning(attemptId, "local_login_result", "result=blocked reason=pin_change_required mode=" + SafeAuditValue(mode));
                    ShowError(PosLocalization.T("operator.login.pinChangeRequired"));
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> LoginRemoteMirrorForRecoveryAsync(
            string shopCode,
            string staffCode,
            string credential,
            string attemptId)
        {
            var username = await new UserRepository(_factory)
                .FindRemoteStaffUsernameAsync(shopCode, staffCode)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(username))
            {
                LogAccessWarning(attemptId, "recovery_login_result", "result=failed reason=mirror_missing");
                return false;
            }

            var loggedIn = await LoginLocalUsernameAsync(
                username,
                credential,
                attemptId,
                "authenticated_recovery").ConfigureAwait(true);
            LogAccessInfo(
                attemptId,
                "recovery_login_result",
                "result=" + (loggedIn ? "success" : "failed") + " source=online_mirror");
            return loggedIn;
        }

        private async Task RunLocalRecoveryLoginAsync()
        {
            if (_busy)
            {
                return;
            }

            var attemptId = CreateAccessAttemptId();
            var username = (StaffCodeBox.Text ?? string.Empty).Trim();
            var credential = CredentialBox.Password ?? string.Empty;
            if (username.Length == 0 || credential.Length == 0)
            {
                ShowError(PosLocalization.T("onlineFirstLogin.missingCredentials"));
                return;
            }

            try
            {
                BeginBusySignIn(PosLocalization.T("common.loading"));
                if (!await LoginLocalUsernameAsync(username, credential, attemptId, "local_recovery").ConfigureAwait(true))
                {
                    EndBusyAllowFreshLogin();
                    EnterLocalRecoveryLoginMode();
                    ShowError(PosLocalization.T("access.login.invalidCredentials"));
                    return;
                }

                var catalogSaleSafe = await PosCatalogPullService
                    .IsCatalogSaleSafeAsync(_factory)
                    .ConfigureAwait(true);
                AccessMode = catalogSaleSafe
                    ? PosAuthenticatedAccessMode.Normal
                    : PosAuthenticatedAccessMode.LocalRecovery;
                LogAccessInfo(
                    attemptId,
                    "local_recovery_result",
                    "result=success catalogSaleSafe=" + BoolText(catalogSaleSafe));
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                LogAccessWarning(
                    attemptId,
                    "local_recovery_result",
                    "result=exception exceptionType=" + SafeAuditValue(ex.GetType().Name),
                    ex);
                EndBusyAllowFreshLogin();
                EnterLocalRecoveryLoginMode();
                ShowError(PosLocalization.T("access.login.invalidCredentials"));
            }
            finally
            {
                credential = string.Empty;
                CredentialBox.Clear();
            }
        }

        private async void OnRecoveryClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
            {
                return;
            }

            if (_authenticatedRecoveryAvailable &&
                OperatorSessionHolder.Current != null &&
                OperatorSessionHolder.Current.IsLoggedIn)
            {
                AccessMode = PosAuthenticatedAccessMode.LocalRecovery;
                DialogResult = true;
                Close();
                return;
            }

            if (_recoveryDecision != null && _recoveryDecision.CanUseLocalRecoveryLogin)
            {
                EnterLocalRecoveryLoginMode();
                return;
            }

            if (_recoveryDecision == null || !_recoveryDecision.CanCreateLocalAdmin)
            {
                RecoveryButton.Visibility = Visibility.Collapsed;
                return;
            }

            var state = await new UserRepository(_factory).GetBootstrapStateAsync().ConfigureAwait(true);
            _bootstrapState = state;
            var rechecked = PosAccessRecoveryPolicy.Evaluate(state, _recoveryDecision.FailureKind);
            if (!rechecked.CanCreateLocalAdmin)
            {
                ApplyRecoveryDecision(rechecked, showTransientHelp: false);
                ShowError(PosLocalization.T("firstRun.noLongerEligible"));
                return;
            }

            CredentialBox.Clear();
            var dialog = new FirstRunSetupDialog(_factory)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true &&
                OperatorSessionHolder.Current != null &&
                OperatorSessionHolder.Current.IsLoggedIn)
            {
                AccessMode = PosAuthenticatedAccessMode.LocalRecovery;
                LogAccessInfo(null, "local_recovery_created", "result=success source=explicit_action");
                DialogResult = true;
                Close();
                return;
            }

            _bootstrapState = await new UserRepository(_factory).GetBootstrapStateAsync().ConfigureAwait(true);
            ApplyRecoveryDecision(
                PosAccessRecoveryPolicy.Evaluate(_bootstrapState, _recoveryDecision.FailureKind),
                showTransientHelp: false);
        }

        private void EnterLocalRecoveryLoginMode()
        {
            _localRecoveryLoginMode = true;
            _authenticatedRecoveryAvailable = false;
            ShopCodeLabel.Visibility = Visibility.Collapsed;
            ShopCodeBox.Visibility = Visibility.Collapsed;
            StaffCodeLabel.Text = PosLocalization.T("access.login.localUsername");
            ConnectButtonText.Text = PosLocalization.T("access.login.localRecoverySignIn");
            ConnectButton.Visibility = Visibility.Visible;
            ConnectButton.IsEnabled = true;
            RecoveryButton.Visibility = Visibility.Collapsed;
            RetryDownloadButton.Visibility = Visibility.Collapsed;
            ShowInfo(PosLocalization.T("firstRun.localOnlyWarning"));
            StaffCodeBox.Clear();
            CredentialBox.Clear();
            StaffCodeBox.Focus();
        }

        private void ApplyRecoveryDecision(
            PosAccessRecoveryDecision decision,
            bool showTransientHelp)
        {
            _recoveryDecision = decision;
            RecoveryButton.Visibility = Visibility.Collapsed;
            RecoveryButton.IsEnabled = false;

            if (decision == null || decision.NextStep == PosAccessNextStep.Denied)
            {
                return;
            }

            if (decision.NextStep == PosAccessNextStep.ExistingUsersDisabled)
            {
                ShowError(PosLocalization.T("access.login.existingUsersDisabled"));
                return;
            }

            if (decision.CanCreateLocalAdmin)
            {
                RecoveryButtonText.Text = PosLocalization.T("access.login.localRecovery");
                RecoveryButton.Visibility = Visibility.Visible;
                RecoveryButton.IsEnabled = !_busy;
                if (showTransientHelp)
                {
                    ConnectButtonText.Text = PosLocalization.T("access.login.retryOnline");
                    ShowInfo(PosLocalization.T("access.login.localRecoveryHelp"));
                }
                return;
            }

            if (decision.CanUseLocalRecoveryLogin)
            {
                RecoveryButtonText.Text = PosLocalization.T("access.login.localRecoverySignIn");
                RecoveryButton.Visibility = Visibility.Visible;
                RecoveryButton.IsEnabled = !_busy;
                if (showTransientHelp)
                {
                    ConnectButtonText.Text = PosLocalization.T("access.login.retryOnline");
                }
            }
        }

        private async Task<bool> EnsureCatalogSaleSafeForAccessAsync(
            bool logoutOnBlock,
            string attemptId,
            string mode)
        {
            LogAccessInfo(attemptId, "catalog_sale_safe_start", "mode=" + SafeAuditValue(mode));
            if (await PosCatalogPullService.IsCatalogSaleSafeAsync(_factory).ConfigureAwait(true))
            {
                LogAccessInfo(attemptId, "catalog_sale_safe_result", "result=success mode=" + SafeAuditValue(mode));
                return true;
            }

            if (logoutOnBlock)
            {
                OperatorSessionHolder.Current?.LogoutForced();
            }

            LogAccessWarning(attemptId, "catalog_sale_safe_result", "result=blocked reason=catalog_not_sale_safe mode=" + SafeAuditValue(mode));
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

            var catalogRetryId = string.IsNullOrWhiteSpace(_lastAccessAttemptId)
                ? CreateAccessAttemptId()
                : _lastAccessAttemptId;
            var retryTimer = Stopwatch.StartNew();
            var retryFinished = false;
            LogCatalogRetryInfo(catalogRetryId, "start", "result=started");
            var options = _lastOptions;
            if (options == null &&
                !PosAdminWebOptions.TryCreate(BaseUrlBox.Text, out options, out var reason, out var reasonCode))
            {
                FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "failed", SafeAuditValue(reasonCode));
                ShowError(LocalizeAdminWebReason(reasonCode, reason));
                EndBusyAllowFreshLogin();
                return;
            }

            _lastOptions = options;
            LogCatalogRetryInfo(catalogRetryId, "pull_start", "adminUrlConfigured=yes");
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
                    LogCatalogRetryInfo(
                        catalogRetryId,
                        "pull_result",
                        "result=" + (outcome.Completed && outcome.CatalogSaleSafe ? "success" : "incomplete") +
                        " code=" + SafeAuditValue(outcome.StatusCode) +
                        " authDenied=" + BoolText(outcome.AuthDenied) +
                        " catalogSaleSafe=" + BoolText(outcome.CatalogSaleSafe));
                    if (outcome.Completed && outcome.CatalogSaleSafe)
                    {
                        UpdateSetupProgress(PosCatalogPullProgress.ForPhase("finalizing"));
                        if (_resumeCatalogOnly)
                        {
                            FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "success", string.Empty);
                            DialogResult = true;
                            Close();
                            return;
                        }

                        if (OperatorSessionHolder.Current != null &&
                            OperatorSessionHolder.Current.IsLoggedIn)
                        {
                            AccessMode = PosAuthenticatedAccessMode.Normal;
                            FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "success", string.Empty);
                            DialogResult = true;
                            Close();
                            return;
                        }

                        var credential = CredentialBox.Password ?? string.Empty;
                        if (await CompleteOnlineSignInAsync(
                                NormalizeCode(ShopCodeBox.Text),
                                NormalizeCode(StaffCodeBox.Text),
                                credential,
                                catalogRetryId,
                                "catalog_retry").ConfigureAwait(true))
                        {
                            CredentialBox.Clear();
                            FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "success", string.Empty);
                            return;
                        }

                        FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "failed", "local_login_or_catalog_blocked");
                        EndBusyAllowFreshLogin();
                        return;
                    }

                    ShowError(outcome.AuthDenied
                        ? PosLocalization.T("onlineFirstLogin.catalogAuthDenied")
                        : PosLocalization.T("onlineFirstLogin.catalogIncomplete"));

                    if (outcome.AuthDenied || RequiresFreshLogin(outcome))
                    {
                        FinishCatalogRetry(
                            ref retryFinished,
                            catalogRetryId,
                            retryTimer,
                            "blocked",
                            outcome.AuthDenied ? "auth_denied" : "fresh_login_required");
                        EndBusyAllowFreshLogin();
                        return;
                    }

                    FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "failed", SafeAuditValue(outcome.StatusCode));
                    EndBusyKeepPreparation(retryVisible: true);
                }
            }
            catch (OperationCanceledException)
            {
                if (IsVisible)
                {
                    FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "failed", "timeout");
                    ShowError(PosLocalization.T("onlineFirstLogin.timeout"));
                    EndBusyKeepPreparation(retryVisible: true);
                }
            }
            catch (Exception ex)
            {
                LogCatalogRetryWarning(
                    catalogRetryId,
                    "exception",
                    "result=exception exceptionType=" + SafeAuditValue(ex.GetType().Name),
                    ex);
                FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "failed", "exception");
                ShowError(PosLocalization.T("onlineFirstLogin.connectionFailed"));
                EndBusyKeepPreparation(retryVisible: true);
            }
            finally
            {
                _activeCts = null;
                if (!retryFinished)
                {
                    FinishCatalogRetry(ref retryFinished, catalogRetryId, retryTimer, "cancelled", "retry_exited");
                }
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
            CredentialBox.Clear();
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
            RecoveryButton.IsEnabled = false;
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
            RecoveryButton.IsEnabled = false;
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
            RecoveryButton.IsEnabled = RecoveryButton.Visibility == Visibility.Visible;
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
            RecoveryButton.IsEnabled = RecoveryButton.Visibility == Visibility.Visible;
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

        private void LogAccessBootstrapResult(string attemptId, PosOnlineBootstrapResult result)
        {
            var normalizedResult = result != null && result.CanOpenPos
                ? "success"
                : result != null && result.Denied
                    ? "denied"
                    : "failed";
            var details =
                "result=" + normalizedResult +
                " code=" + SafeAuditValue(result?.Code) +
                " clientRequestId=" + SafeAuditValue(result?.ClientRequestId) +
                " serverRequestId=" + SafeAuditValue(result?.ServerRequestId) +
                " cfRay=" + SafeAuditValue(result?.CfRay) +
                " catalogStatus=" + SafeAuditValue(result?.CatalogStatus) +
                " catalogSaleSafe=" + BoolText(result != null && result.CatalogSaleSafe) +
                " requiresRetry=" + BoolText(result != null && result.RequiresRetry);

            if (result != null && result.CanOpenPos)
            {
                LogAccessInfo(attemptId, "online_bootstrap_result", details);
                return;
            }

            LogAccessWarning(attemptId, "online_bootstrap_result", details);
        }

        private void FinishAccessAttempt(
            ref bool attemptFinished,
            string attemptId,
            Stopwatch timer,
            string result,
            string mode,
            string reason)
        {
            if (attemptFinished)
            {
                return;
            }

            attemptFinished = true;
            var details =
                "result=" + SafeAuditValue(result) +
                " mode=" + SafeAuditValue(mode) +
                " durationMs=" + ElapsedMs(timer);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                details += " reason=" + SafeAuditValue(reason);
            }

            if (string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
            {
                LogAccessInfo(attemptId, "end", details);
                return;
            }

            LogAccessWarning(attemptId, "end", details);
        }

        private void LogAccessInfo(string attemptId, string stage, string details)
        {
            _logger.LogInfo(BuildStructuredLogLine("pos.access", attemptId, stage, details));
        }

        private void LogAccessWarning(string attemptId, string stage, string details, Exception ex = null)
        {
            _logger.LogWarning(BuildStructuredLogLine("pos.access", attemptId, stage, details), ex);
        }

        private void LogCatalogRetryInfo(string attemptId, string stage, string details)
        {
            _logger.LogInfo(BuildStructuredLogLine("pos.access.catalog_retry", attemptId, stage, details));
        }

        private void LogCatalogRetryWarning(string attemptId, string stage, string details, Exception ex = null)
        {
            _logger.LogWarning(BuildStructuredLogLine("pos.access.catalog_retry", attemptId, stage, details), ex);
        }

        private void FinishCatalogRetry(
            ref bool retryFinished,
            string attemptId,
            Stopwatch timer,
            string result,
            string reason)
        {
            if (retryFinished)
            {
                return;
            }

            retryFinished = true;
            var details =
                "result=" + SafeAuditValue(result) +
                " durationMs=" + ElapsedMs(timer);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                details += " reason=" + SafeAuditValue(reason);
            }

            if (string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
            {
                LogCatalogRetryInfo(attemptId, "end", details);
                return;
            }

            LogCatalogRetryWarning(attemptId, "end", details);
        }

        private static string BuildStructuredLogLine(
            string category,
            string attemptId,
            string stage,
            string details)
        {
            var line =
                "category=" + SafeAuditValue(category);
            if (!string.IsNullOrWhiteSpace(attemptId))
            {
                line += " attemptId=" + SafeAuditValue(attemptId);
            }

            line += " stage=" + SafeAuditValue(stage);

            if (!string.IsNullOrWhiteSpace(details))
            {
                line += " " + details.Trim();
            }

            return line;
        }

        private static string CreateAccessAttemptId()
        {
            return DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
                "-" +
                Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string ElapsedMs(Stopwatch timer)
        {
            return (timer?.ElapsedMilliseconds ?? 0L).ToString(CultureInfo.InvariantCulture);
        }

        private static string BoolText(bool value)
        {
            return value ? "yes" : "no";
        }

        private static string NetworkText(NetworkStatusSnapshot status)
        {
            return status != null && status.IsNetworkAvailable ? "online" : "offline";
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
