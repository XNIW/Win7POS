using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Win7POS.Data;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Data.Repositories;
using Win7POS.Data.Online;
using Win7POS.Core;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Core.Security;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
        private static readonly Infrastructure.FileLogger _logger = new Infrastructure.FileLogger("MainWindow");
        private static readonly TimeSpan StartupHeartbeatTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StartupSalesSyncTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StartupCatalogImportSyncTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StartupCatalogPullTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StartupWatchdogTimeout = TimeSpan.FromSeconds(5);
        private const string CatalogBootstrapStatusSettingKey = "pos.catalog.bootstrap_status";
        private const string LastCatalogErrorSettingKey = "pos.catalog.last_error";
        private const string LastSalesErrorSettingKey = "pos.sales_sync.last_error";
        private readonly TaskCompletionSource<bool> _contentRendered = new TaskCompletionSource<bool>();
        private DispatcherTimer _syncStatusTimer;
        private DispatcherTimer _networkStatusTimer;
        private bool _backgroundOnlineRefreshQueued;
        private bool _operatorLoginReached;
        private bool _recoveryMode;
        private SqliteConnectionFactory _languageSettingsFactory;
        private SqliteConnectionFactory _recoveryFactory;
        private PosWorkflowService _recoveryWorkflowService;
        private string _startupPhase = "constructed";
        private string _productsDataContextOperatorUsername;
        private PosView PosViewControl;
        public static readonly DependencyProperty ShellTitleProperty = DependencyProperty.Register(
            nameof(ShellTitle), typeof(string), typeof(MainWindow), new PropertyMetadata("Win7POS"));
        public static readonly DependencyProperty CurrentMenuKeyProperty = DependencyProperty.Register(
            nameof(CurrentMenuKey), typeof(string), typeof(MainWindow), new PropertyMetadata("Pos", OnCurrentMenuKeyChanged));

        private static void OnCurrentMenuKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MainWindow w)
                w.UpdateMenuSelectionVisual();
        }

        /// <summary>Chiave della voce di menu attiva (Pos, Prodotti, SalesRegister, DailyReport, ShopSettings, Printer, About).</summary>
        public string CurrentMenuKey
        {
            get => (string)GetValue(CurrentMenuKeyProperty);
            set => SetValue(CurrentMenuKeyProperty, value);
        }

        public string ShellTitle
        {
            get => (string)GetValue(ShellTitleProperty);
            set => SetValue(ShellTitleProperty, string.IsNullOrWhiteSpace(value) ? "Win7POS" : value.Trim());
        }

        public MainWindow()
        {
            Application.Current.MainWindow = this;
            StartupTrace.Write("MainWindow constructor start");
            _logger.LogInfo("MainWindow constructor entered");
            InitializeComponent();
            InitializeLanguageSelector();
            StartupTrace.Write("MainWindow constructor end");
            _logger.LogInfo("MainWindow constructor done");

            MainTabControl.SelectedIndex = 0;

            Loaded += OnLoadedAsync;
            ContentRendered += OnContentRendered;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void InitializeLanguageSelector()
        {
            PosLocalization.Current.LanguageChanged += (_, __) =>
            {
                UpdateNetworkStatusBadge();
                QueueSyncStatusRefresh(_languageSettingsFactory);
            };
        }

        private async Task LoadLanguagePreferenceAsync(SqliteConnectionFactory factory)
        {
            _languageSettingsFactory = factory;
            await PosLocalization.Current
                .LoadAsync(new SettingsRepository(factory))
                .ConfigureAwait(true);
            UpdateLanguageSelector();
        }

        private void UpdateLanguageSelector()
        {
            // Language selection now lives in SettingsHubDialog.
        }

        private async void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = (sender as ComboBox)?.SelectedValue as string;
            await SaveLanguagePreferenceAsync(selected).ConfigureAwait(true);
        }

        private async Task SaveLanguagePreferenceAsync(string selected)
        {
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            try
            {
                var factory = _languageSettingsFactory ?? new SqliteConnectionFactory(PosDbOptions.Default());
                _languageSettingsFactory = factory;
                await PosLocalization.Current
                    .SetLanguageAsync(new SettingsRepository(factory), selected)
                    .ConfigureAwait(true);
                if (GetPosViewModel() != null)
                {
                    GetPosViewModel().SetStatus(
                        PosLocalization.Current.Text("settings.languageSaved"),
                        PosNoticeSeverity.Success);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("OnLanguageSelectionChanged: salvataggio lingua non completato", ex);
                if (GetPosViewModel() != null)
                {
                    GetPosViewModel().SetStatus(
                        PosLocalization.Current.Text("settings.languageSaveError"),
                        PosNoticeSeverity.Error);
                }
            }
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            _startupPhase = "Loaded entered";
            StartupTrace.Write("MainWindow Loaded entered");
            _logger.LogInfo("MainWindow Loaded entered");
            StartStartupWatchdog();
            UpdateMenuSelectionVisual();

            try
            {
                var options = PosDbOptions.Default();
                _startupPhase = "DbInitializer start";
                StartupTrace.Write("DB init start");
                _logger.LogInfo("DbInitializer start");
                DbInitializer.EnsureCreated(options);
                StartupTrace.Write("DB init end");
                _logger.LogInfo("DbInitializer done");

                var factory = new SqliteConnectionFactory(options);
                await LoadLanguagePreferenceAsync(factory).ConfigureAwait(true);
                await RefreshShellTitleAsync(factory).ConfigureAwait(true);
                if (App.IsSafeStart)
                {
                    ApplySafeStartStatus();
                }
                else
                {
                    await RefreshSyncStatusStripAsync(factory).ConfigureAwait(true);
                    StartSyncStatusTimer(factory);
                }
                UpdateNetworkStatusBadge();
                StartNetworkStatusTimer();

                var userRepo = new UserRepository(factory);
                if (OperatorSessionHolder.Current == null)
                {
                    var securityRepo = new SecurityRepository(factory);
                    var operatorSession = new OperatorSession(userRepo, securityRepo);
                    OperatorSessionHolder.Current = operatorSession;
                }

                await WaitForContentRenderedOrTimeoutAsync().ConfigureAwait(true);

                _logger.LogInfo("POS access dialog opening");
                _startupPhase = "POS access opening";
                StartupTrace.Write("POS access dialog about to open");
                var login = new PosOnlineFirstLoginDialog(factory) { Owner = this };
                login.ShowActivated = true;
                Activate();
                _operatorLoginReached = true;
                _startupPhase = "POS access shown";
                StartupTrace.Write("POS access dialog shown");
                if (login.ShowDialog() != true ||
                    OperatorSessionHolder.Current == null ||
                    !OperatorSessionHolder.Current.IsLoggedIn)
                {
                    _logger.LogInfo("POS access dialog cancelled or not authenticated");
                    Close();
                    return;
                }
                _logger.LogInfo("POS access dialog accepted");
                await RefreshShellTitleAsync(factory).ConfigureAwait(true);

                var session = OperatorSessionHolder.Current;
                StartOfDaySyncResult startOfDayResult = null;
                if (session != null)
                {
                    var catalogSaleSafe = await PosCatalogPullService
                        .IsCatalogSaleSafeAsync(factory)
                        .ConfigureAwait(true);
                    var shellMode = PosShellStartupPolicy.Determine(login.AccessMode, catalogSaleSafe);

                    if (shellMode == PosShellMode.Recovery)
                    {
                        await EnterRecoveryModeAsync(factory).ConfigureAwait(true);
                    }
                    else if (!App.IsSafeStart)
                    {
                        startOfDayResult = await RunStartOfDaySyncAsync(factory).ConfigureAwait(true);
                        if (startOfDayResult == null || !startOfDayResult.CanOpenPos)
                        {
                            _logger.LogWarning("category=start_of_day result=blocked reason=" +
                                SafeOnlineCode(startOfDayResult?.BlockingReason));
                            if (startOfDayResult?.RestoreNeedsReview == true)
                            {
                                await ShowRestoreReviewRecoveryAsync().ConfigureAwait(true);
                            }
                            Close();
                            return;
                        }
                    }

                    if (shellMode == PosShellMode.Pos)
                    {
                        EnsurePosViewCreated();
                    }
                    UpdateOperatorDisplay(session);
                    RefreshShellAfterOperatorChange(session);
                    session.SessionChanged += () => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateOperatorDisplay(session);
                        RefreshShellAfterOperatorChange(session);
                    }));
                }

                if (_recoveryMode)
                {
                    StartupTrace.Write("online refresh deferred: recovery mode");
                    _logger.LogInfo("BackgroundOnlineRefresh deferred: recovery mode");
                }
                else if (App.IsSafeStart)
                {
                    StartupTrace.Write("online refresh skipped: safe-start");
                    _logger.LogInfo("BackgroundOnlineRefresh skipped: safe-start");
                    ApplySafeStartStatus();
                }
                else
                {
                    if (startOfDayResult == null || startOfDayResult.ShouldContinueInBackground)
                    {
                        QueueBackgroundOnlineRefresh(factory);
                    }
                    else
                    {
                        QueueSyncStatusRefresh(factory);
                    }
                }
            }
            catch (Exception ex)
            {
                StartupTrace.Write("MainWindow startup failed", ex);
                _logger.LogError(ex, "MainWindow.OnLoadedAsync: avvio fallito (DB/POS access)");
                try
                {
                    ModernMessageDialog.Show(this, "Win7POS",
                        PosLocalization.Current.Text("app.startupError"));
                }
                catch { }
                Close();
            }
        }

        private async Task<StartOfDaySyncResult> RunStartOfDaySyncAsync(SqliteConnectionFactory factory)
        {
            var dialog = new PosStartOfDaySyncDialog(factory)
            {
                Owner = this
            };

            var ok = dialog.ShowDialog() == true;
            await RefreshSyncStatusStripAsync(factory).ConfigureAwait(true);
            return ok && dialog.Result != null
                ? dialog.Result
                : new StartOfDaySyncResult
                {
                    CanOpenPos = false,
                    RequiresOperatorAction = true,
                    BlockingReason = "operator_cancelled",
                    StatusMessage = PosLocalization.T("startOfDay.exit")
                };
        }

        private async Task ShowRestoreReviewRecoveryAsync()
        {
            if (!HasCurrentPermission(PermissionCodes.DbMaintenance) &&
                !await TrySwitchForPermissionAsync(
                    PermissionCodes.DbMaintenance,
                    PosLocalization.Current.Format(
                        "common.permissionDeniedOperation",
                        PosLocalization.Current.Text("operations.dbMaintenance")),
                    "RestoreReview").ConfigureAwait(true))
            {
                return;
            }

            var vm = new DbMaintenanceViewModel(new PosWorkflowService());
            var dialog = new DbMaintenanceDialog(vm, restoreReviewOnly: true)
            {
                Owner = this
            };
            WindowSizingHelper.CapMaxHeightToOwner(dialog);
            dialog.ShowDialog();
        }

        private void QueueBackgroundOnlineRefresh(SqliteConnectionFactory factory)
        {
            if (App.IsSafeStart)
            {
                StartupTrace.Write("online refresh skipped: safe-start");
                ApplySafeStartStatus();
                return;
            }

            if (_backgroundOnlineRefreshQueued)
            {
                return;
            }

            _backgroundOnlineRefreshQueued = true;
            StartupTrace.Write("online refresh queued");
            _logger.LogInfo("BackgroundOnlineRefresh queued");
            Task.Run(async () => await RunBackgroundOnlineRefreshAsync(factory).ConfigureAwait(false));
        }

        private async Task RunBackgroundOnlineRefreshAsync(SqliteConnectionFactory factory)
        {
            StartupTrace.Write("online refresh start");
            _logger.LogInfo("BackgroundOnlineRefresh start");

            try
            {
                await TryRefreshTrustedPosSessionAsync(factory).ConfigureAwait(false);
                StartupTrace.Write("online refresh done");
                _logger.LogInfo("BackgroundOnlineRefresh done");
            }
            catch (OperationCanceledException)
            {
                StartupTrace.Write("online refresh timeout");
                _logger.LogWarning("BackgroundOnlineRefresh timeout");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("online refresh failed", ex);
                _logger.LogWarning("BackgroundOnlineRefresh not completed.", ex);
            }
            finally
            {
                QueueSyncStatusRefresh(factory);
            }
        }

        private async Task TryRefreshTrustedPosSessionAsync(SqliteConnectionFactory factory)
        {
            if (!PosAdminWebOptions.TryLoad(out var options, out _))
            {
                StartupTrace.Write("online refresh skipped: Admin Web config missing");
                _logger.LogInfo("BackgroundOnlineRefresh skipped: Admin Web config missing");
                return;
            }

            var store = new PosTrustedDeviceStore();
            if (!store.TryRead(out var trustedSession))
            {
                StartupTrace.Write("online refresh skipped: trusted session missing");
                _logger.LogInfo("BackgroundOnlineRefresh skipped: trusted session missing");
                return;
            }

            try
            {
                using (var client = new PosAdminWebClient(options))
                using (var heartbeatCts = new CancellationTokenSource(StartupHeartbeatTimeout))
                {
                    StartupTrace.Write("online heartbeat start");
                    _logger.LogInfo("BackgroundOnlineRefresh heartbeat start");
                    var result = await client.HeartbeatAsync(new PosHeartbeatRequest
                    {
                        AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                        DeviceToken = trustedSession.DeviceToken,
                        PosSessionId = trustedSession.PosSessionId,
                        SessionToken = trustedSession.SessionToken,
                        ShopDeviceId = trustedSession.ShopDeviceId,
                    }, heartbeatCts.Token).ConfigureAwait(false);

                    if (result.Success && result.Value != null)
                    {
                        store.SaveHeartbeat(trustedSession, result.Value);
                        StartupTrace.Write("online heartbeat done");
                        _logger.LogInfo(
                            "BackgroundOnlineRefresh heartbeat done: category=online.heartbeat clientRequestId=" +
                            SafeOnlineId(result.ClientRequestId) +
                            " serverRequestId=" + SafeOnlineId(result.ServerRequestId));
                        QueueSyncStatusRefresh(factory);
                        await TrySyncSalesOutboxAsync(options, factory).ConfigureAwait(false);
                        await TrySyncCatalogImportOutboxAsync(options, factory).ConfigureAwait(false);
                        await TryPullCatalogAsync(options, factory).ConfigureAwait(false);
                        return;
                    }

                    if (result.Denied)
                    {
                        store.Clear();
                        await StoreStartupSettingAsync(factory, LastCatalogErrorSettingKey, "auth_denied")
                            .ConfigureAwait(false);
                        await StoreStartupSettingAsync(factory, LastSalesErrorSettingKey, "auth_denied")
                            .ConfigureAwait(false);
                        await StoreStartupSettingAsync(factory, CatalogBootstrapStatusSettingKey, "failed_auth_denied")
                            .ConfigureAwait(false);
                        QueueSyncStatusRefresh(factory);
                    }

                    _logger.LogWarning(
                        "BackgroundOnlineRefresh heartbeat skipped: category=online.heartbeat code=" +
                        SafeOnlineCode(result.Code) +
                        " clientRequestId=" + SafeOnlineId(result.ClientRequestId) +
                        " serverRequestId=" + SafeOnlineId(result.ServerRequestId) +
                        " cfRay=" + SafeOnlineId(result.CfRay));
                }
            }
            catch (OperationCanceledException)
            {
                StartupTrace.Write("online heartbeat timeout");
                _logger.LogWarning("BackgroundOnlineRefresh heartbeat timeout");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("online heartbeat failed", ex);
                _logger.LogWarning("TryRefreshTrustedPosSessionAsync: refresh online non completato", ex);
            }
        }

        private async Task TryPullCatalogAsync(PosAdminWebOptions options, SqliteConnectionFactory factory)
        {
            try
            {
                var catalogPull = new PosCatalogPullService(factory);
                using (var cts = new CancellationTokenSource(StartupCatalogPullTimeout))
                {
                    var completed = await catalogPull.TryPullCatalogAsync(options, cts.Token)
                        .ConfigureAwait(false);
                    if (cts.IsCancellationRequested && !completed)
                    {
                        await StoreStartupSettingAsync(factory, LastCatalogErrorSettingKey, "timeout")
                            .ConfigureAwait(false);
                        StartupTrace.Write("online catalog pull timeout");
                        _logger.LogWarning("BackgroundOnlineRefresh catalog pull timeout");
                    }
                    else
                    {
                        _logger.LogInfo("BackgroundOnlineRefresh catalog pull done: completed=" + completed.ToString());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await StoreStartupSettingAsync(factory, LastCatalogErrorSettingKey, "timeout")
                    .ConfigureAwait(false);
                StartupTrace.Write("online catalog pull timeout");
                _logger.LogWarning("BackgroundOnlineRefresh catalog pull timeout");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("online catalog pull failed", ex);
                _logger.LogWarning("TryPullCatalogAsync: aggiornamento catalogo non completato", ex);
            }
            finally
            {
                QueueSyncStatusRefresh(factory);
            }
        }

        private async Task TrySyncSalesOutboxAsync(PosAdminWebOptions options, SqliteConnectionFactory factory)
        {
            try
            {
                var salesSync = new PosSalesSyncService(factory);
                using (var cts = new CancellationTokenSource(StartupSalesSyncTimeout))
                {
                    var completed = await salesSync.TrySyncPendingAsync(options, cts.Token)
                        .ConfigureAwait(false);
                    _logger.LogInfo("BackgroundOnlineRefresh sales sync done: completed=" + completed.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                await StoreStartupSettingAsync(factory, LastSalesErrorSettingKey, "timeout")
                    .ConfigureAwait(false);
                StartupTrace.Write("online sales sync timeout");
                _logger.LogWarning("BackgroundOnlineRefresh sales sync timeout");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("online sales sync failed", ex);
                _logger.LogWarning("TrySyncSalesOutboxAsync: sales sync non completato", ex);
            }
            finally
            {
                QueueSyncStatusRefresh(factory);
            }
        }

        private async Task TrySyncCatalogImportOutboxAsync(PosAdminWebOptions options, SqliteConnectionFactory factory)
        {
            try
            {
                var store = new PosTrustedDeviceStore();
                if (!store.TryRead(out var trustedSession))
                {
                    return;
                }

                using (var cts = new CancellationTokenSource(StartupCatalogImportSyncTimeout))
                {
                    var result = await new CatalogImportSyncService(factory)
                        .SyncPendingAsync(options, trustedSession, cts.Token)
                        .ConfigureAwait(false);

                    if (result.RequiresTrustClear)
                    {
                        store.Clear();
                        await StoreStartupSettingAsync(factory, LastCatalogErrorSettingKey, "auth_denied")
                            .ConfigureAwait(false);
                        await StoreStartupSettingAsync(factory, LastSalesErrorSettingKey, "auth_denied")
                            .ConfigureAwait(false);
                        await StoreStartupSettingAsync(factory, CatalogBootstrapStatusSettingKey, "failed_auth_denied")
                            .ConfigureAwait(false);
                    }

                    _logger.LogInfo(
                        "BackgroundOnlineRefresh catalog import sync done: total=" + result.Total.ToString() +
                        " acked=" + result.Acked.ToString() +
                        " retried=" + result.Retried.ToString() +
                        " blocked=" + result.Blocked.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                await StoreStartupSettingAsync(factory, LastCatalogErrorSettingKey, "catalog_import_timeout")
                    .ConfigureAwait(false);
                StartupTrace.Write("online catalog import sync timeout");
                _logger.LogWarning("BackgroundOnlineRefresh catalog import sync timeout");
            }
            catch (Exception ex)
            {
                StartupTrace.Write("online catalog import sync failed", ex);
                _logger.LogWarning("TrySyncCatalogImportOutboxAsync: catalog import sync non completato", ex);
            }
            finally
            {
                QueueSyncStatusRefresh(factory);
            }
        }

        private void QueueSyncStatusRefresh(SqliteConnectionFactory factory)
        {
            try
            {
                if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await RefreshSyncStatusStripAsync(factory).ConfigureAwait(true);
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("QueueSyncStatusRefresh skipped.", ex);
            }
        }

        private static async Task StoreStartupSettingAsync(
            SqliteConnectionFactory factory,
            string key,
            string value)
        {
            var settings = new SettingsRepository(factory);
            await settings.SetStringAsync(key, SafeOnlineCode(value)).ConfigureAwait(false);
        }

        private static string SafeOnlineCode(string code)
        {
            var normalized = (code ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "failure";
            }

            var safe = new string(normalized
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                .Take(60)
                .ToArray());
            return safe.Length == 0 ? "failure" : safe;
        }

        private static string SafeOnlineId(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "";
            }

            var safe = new string(normalized
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                .Take(120)
                .ToArray());
            return safe;
        }

        private void ApplySafeStartStatus()
        {
            if (SyncStatusText != null)
            {
                SyncStatusText.Text = PosLocalization.Current.Format("sync.statusPrefix", PosLocalization.Current.Text("sync.safeStart"));
                SyncStatusText.ToolTip = PosLocalization.Current.Text("sync.safeStartTooltip");
            }

            if (SyncStatusPill != null)
            {
                SyncStatusPill.Background = new SolidColorBrush(Color.FromRgb(109, 85, 132));
            }
        }

        private void StartStartupWatchdog()
        {
            var startedAt = DateTimeOffset.UtcNow;
            Task.Delay(StartupWatchdogTimeout).ContinueWith(_ =>
            {
                if (!_operatorLoginReached)
                {
                    StartupTrace.Write("startup watchdog warning: POS access not reached within 5s; phase=" + _startupPhase + "; elapsed_ms=" + (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds.ToString("0"));
                }
            });
        }

        private async Task WaitForContentRenderedOrTimeoutAsync()
        {
            if (_contentRendered.Task.IsCompleted)
            {
                return;
            }

            var completed = await Task.WhenAny(
                _contentRendered.Task,
                Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
            if (completed != _contentRendered.Task)
            {
                StartupTrace.Write("ContentRendered wait timeout; opening POS access");
            }
        }

        private async Task EnterRecoveryModeAsync(SqliteConnectionFactory factory)
        {
            _recoveryMode = true;
            _recoveryFactory = factory ?? throw new ArgumentNullException(nameof(factory));
            RecoveryModeBanner.Visibility = Visibility.Visible;
            RecoveryPlaceholder.Visibility = Visibility.Visible;
            RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.recoveryModeHelp");
            SetRecoveryNavigationState(true);

            var session = OperatorSessionHolder.Current;
            if (HasCurrentPermission(PermissionCodes.CatalogView))
            {
                var products = EnsureProductsViewModel(session);
                await products.LoadAsync().ConfigureAwait(true);
                CurrentMenuKey = "Prodotti";
                MainTabControl.SelectedItem = ProductsTab;
            }
            else
            {
                CurrentMenuKey = "Settings";
                MainTabControl.SelectedIndex = 0;
            }
            SetSideMenuOpen(false);
            _logger.LogWarning("category=pos.recovery result=entered reason=catalog_not_sale_safe", null);
        }

        private async Task<bool> ExitRecoveryModeAsync()
        {
            var factory = _recoveryFactory ?? new SqliteConnectionFactory(PosDbOptions.Default());
            if (!await PosCatalogPullService.IsCatalogSaleSafeAsync(factory).ConfigureAwait(true))
            {
                RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.catalogStillUnsafe");
                return false;
            }

            StartOfDaySyncResult startOfDayResult = null;
            if (!App.IsSafeStart)
            {
                startOfDayResult = await RunStartOfDaySyncAsync(factory).ConfigureAwait(true);
                if (startOfDayResult == null || !startOfDayResult.CanOpenPos)
                {
                    RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.recoveryModeHelp");
                    return false;
                }
            }

            _recoveryMode = false;
            RecoveryModeBanner.Visibility = Visibility.Collapsed;
            RecoveryPlaceholder.Visibility = Visibility.Collapsed;
            SetRecoveryNavigationState(false);
            EnsurePosViewCreated();
            CurrentMenuKey = "Pos";
            MainTabControl.SelectedIndex = 0;
            RefreshShellAfterOperatorChange(OperatorSessionHolder.Current);

            if (App.IsSafeStart)
            {
                ApplySafeStartStatus();
            }
            else if (startOfDayResult == null || startOfDayResult.ShouldContinueInBackground)
            {
                QueueBackgroundOnlineRefresh(factory);
            }
            else
            {
                QueueSyncStatusRefresh(factory);
            }

            _logger.LogInfo("category=pos.recovery result=completed");
            return true;
        }

        private void SetRecoveryNavigationState(bool recoveryMode)
        {
            foreach (var button in FindButtonsByTag(SideMenuPanel))
            {
                var tag = button.Tag?.ToString() ?? string.Empty;
                button.IsEnabled = !recoveryMode ||
                    string.Equals(tag, "Prodotti", StringComparison.Ordinal) ||
                    string.Equals(tag, "Settings", StringComparison.Ordinal);
            }
        }

        private async void OnRetryRecoveryOnlineClick(object sender, RoutedEventArgs e)
        {
            if (!_recoveryMode) return;

            var factory = _recoveryFactory ?? new SqliteConnectionFactory(PosDbOptions.Default());
            var login = new PosOnlineFirstLoginDialog(factory)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(this)
            };
            if (login.ShowDialog() != true ||
                OperatorSessionHolder.Current == null ||
                !OperatorSessionHolder.Current.IsLoggedIn)
            {
                return;
            }

            UpdateOperatorDisplay(OperatorSessionHolder.Current);
            RefreshShellAfterOperatorChange(OperatorSessionHolder.Current);
            await ExitRecoveryModeAsync().ConfigureAwait(true);
        }

        private async void OnVerifyRecoveryCatalogClick(object sender, RoutedEventArgs e)
        {
            if (!_recoveryMode || VerifyRecoveryCatalogButton == null) return;

            VerifyRecoveryCatalogButton.IsEnabled = false;
            try
            {
                var user = OperatorSessionHolder.Current?.CurrentUser;
                if (user == null)
                {
                    RecoveryStatusText.Text = PosLocalization.Current.Text("operator.login.sessionMissing");
                    return;
                }

                var factory = _recoveryFactory ?? new SqliteConnectionFactory(PosDbOptions.Default());
                var approved = await new CatalogRecoveryRepository(factory)
                    .TryApproveLocalCatalogAsync(user.Id)
                    .ConfigureAwait(true);
                if (!approved)
                {
                    RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.catalogStillUnsafe");
                    return;
                }

                RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.catalogApproved");
                await ExitRecoveryModeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow.OnVerifyRecoveryCatalogClick: verification failed");
                RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.catalogStillUnsafe");
            }
            finally
            {
                if (_recoveryMode)
                {
                    VerifyRecoveryCatalogButton.IsEnabled = true;
                }
            }
        }

        private void UpdateOperatorDisplay(IOperatorSession session)
        {
            if (OperatorDisplayText != null)
                OperatorDisplayText.Text = session != null && session.IsLoggedIn ? session.CurrentDisplayName : "—";
            if (OperatorRoleText != null)
                OperatorRoleText.Text = session != null && session.IsLoggedIn ? "(" + session.CurrentRoleName + ")" : "";
        }

        private async Task RefreshShellTitleAsync(SqliteConnectionFactory factory = null)
        {
            try
            {
                factory = factory ?? new SqliteConnectionFactory(PosDbOptions.Default());
                var official = await new ShopOfficialSnapshotRepository(factory).GetAsync().ConfigureAwait(true);
                var fallbackCode = await new SettingsRepository(factory).GetLastPosLoginShopCodeAsync().ConfigureAwait(true);
                var title = FirstNonEmpty(official?.ShopName, fallbackCode, "Win7POS");
                ShellTitle = title;
                Title = string.Equals(title, "Win7POS", StringComparison.OrdinalIgnoreCase)
                    ? "Win7POS"
                    : title + " - Win7POS";
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RefreshShellTitleAsync skipped.", ex);
                ShellTitle = "Win7POS";
                Title = "Win7POS";
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "Win7POS";
        }

        private void StartNetworkStatusTimer()
        {
            if (_networkStatusTimer != null)
            {
                return;
            }

            _networkStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(20)
            };
            _networkStatusTimer.Tick += (_, __) => UpdateNetworkStatusBadge();
            _networkStatusTimer.Start();
        }

        private void UpdateNetworkStatusBadge()
        {
            try
            {
                var status = NetworkStatusService.Read();
                var online = status != null && status.IsNetworkAvailable;
                var foreground = online
                    ? new SolidColorBrush(Color.FromRgb(46, 125, 50))
                    : new SolidColorBrush(Color.FromRgb(198, 40, 40));

                if (ShellNetworkStatusText != null)
                {
                    ShellNetworkStatusText.Text = PosLocalization.Current.Text(online
                        ? "access.login.networkOnlineShort"
                        : "access.login.networkOfflineShort");
                    ShellNetworkStatusText.Foreground = foreground;
                }

                if (ShellNetworkStatusBadge != null)
                {
                    ShellNetworkStatusBadge.Background = online
                        ? new SolidColorBrush(Color.FromRgb(232, 242, 237))
                        : new SolidColorBrush(Color.FromRgb(255, 244, 229));
                    ShellNetworkStatusBadge.BorderBrush = online
                        ? new SolidColorBrush(Color.FromRgb(191, 217, 201))
                        : new SolidColorBrush(Color.FromRgb(239, 190, 130));
                    ShellNetworkStatusBadge.ToolTip = PosLocalization.Current.Text(online
                        ? "access.login.networkOnlineDetail"
                        : "access.login.networkOfflineDetail");
                }

                if (ShellNetworkWifiIcon != null)
                {
                    ShellNetworkWifiIcon.Fill = foreground;
                    ShellNetworkOfflineX.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("UpdateNetworkStatusBadge skipped.", ex);
            }
        }

        private void StartSyncStatusTimer(SqliteConnectionFactory factory)
        {
            if (_syncStatusTimer != null)
            {
                return;
            }

            _syncStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _syncStatusTimer.Tick += async (_, __) =>
            {
                await RefreshSyncStatusStripAsync(factory).ConfigureAwait(true);
            };
            _syncStatusTimer.Start();
        }

        private async Task RefreshSyncStatusStripAsync(SqliteConnectionFactory factory = null)
        {
            try
            {
                factory = factory ?? new SqliteConnectionFactory(PosDbOptions.Default());
                var status = await new PosSyncStatusReader(factory).ReadAsync().ConfigureAwait(true);
                if (SyncStatusText != null)
                {
                    SyncStatusText.Text = PosLocalization.Current.Format("sync.statusPrefix", status.SummaryText);
                    SyncStatusText.ToolTip = status.DeviceText + "\n" +
                        status.StaffText + "\n" +
                        status.LastOnlineText + "\n" +
                        status.LastCatalogSyncText + "\n" +
                        status.CatalogBootstrapText + "\n" +
                        status.CatalogReadinessText + "\n" +
                        status.CatalogVersionText + "\n" +
                        status.PendingSalesText + "\n" +
                        status.PolicyText + "\n" +
                        status.SalesAttentionText + "\n" +
                        status.RestoreReviewText + "\n" +
                        status.CatalogErrorText + "\n" +
                        status.SalesErrorText;
                }

                if (SyncStatusPill != null)
                {
                    SyncStatusPill.Background = StatusBrush(status.ConnectivityState, status.RequiresAttention);
                }

                await RefreshShellTitleAsync(factory).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RefreshSyncStatusStripAsync non completato", ex);
                if (SyncStatusText != null)
                {
                    SyncStatusText.Text = PosLocalization.Current.Text("shell.syncUnavailable");
                }
            }
        }

        private static Brush StatusBrush(string connectivityState, bool requiresAttention)
        {
            if (requiresAttention)
            {
                return new SolidColorBrush(Color.FromRgb(126, 62, 23));
            }

            if (string.Equals(connectivityState, "online", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(42, 111, 72));
            }

            if (string.Equals(connectivityState, "not_connected", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(109, 85, 132));
            }

            return new SolidColorBrush(Color.FromRgb(146, 88, 36));
        }

        private async void OnChangeOperatorClick(object sender, RoutedEventArgs e)
        {
            await ShowOperatorSwitchOrPosAccessAsync().ConfigureAwait(true);
        }

        private async Task<bool> ShowOperatorSwitchOrPosAccessAsync(string requiredPermissionCode = null, string requiredPermissionName = null)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var session = EnsureOperatorSession(factory);
            var switchDlg = new OperatorSwitchDialog(factory, session)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(this)
            };

            await switchDlg.InitializeAsync(requiredPermissionCode, requiredPermissionName).ConfigureAwait(true);
            switchDlg.ShowDialog();

            if (switchDlg.PosAccessRequested)
            {
                return OpenPosAccessForOperatorChange(factory);
            }

            if (!switchDlg.SwitchSucceeded)
            {
                return false;
            }

            UpdateOperatorDisplay(session);
            RefreshShellAfterOperatorChange(session);
            return true;
        }

        private bool OpenPosAccessForOperatorChange(SqliteConnectionFactory factory)
        {
            var loginDlg = new PosOnlineFirstLoginDialog(factory)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(this)
            };
            if (loginDlg.ShowDialog() != true ||
                OperatorSessionHolder.Current == null ||
                !OperatorSessionHolder.Current.IsLoggedIn)
            {
                return false;
            }

            var session = OperatorSessionHolder.Current;
            UpdateOperatorDisplay(session);
            RefreshShellAfterOperatorChange(session);
            _ = RefreshShellTitleAsync(factory);
            if (_recoveryMode && loginDlg.AccessMode == PosAuthenticatedAccessMode.Normal)
            {
                _ = ExitRecoveryModeAsync();
            }
            return true;
        }

        private static IOperatorSession EnsureOperatorSession(SqliteConnectionFactory factory)
        {
            if (OperatorSessionHolder.Current == null)
            {
                OperatorSessionHolder.Current = new OperatorSession(
                    new UserRepository(factory),
                    new SecurityRepository(factory));
            }

            return OperatorSessionHolder.Current;
        }

        private async Task<bool> TrySwitchForPermissionAsync(string permissionCode, string message, string actionName)
        {
            var missingPermission = PermissionDiagnosticName(permissionCode);
            var deniedMessage = BuildPermissionDeniedMessage(message, GetCurrentRoleDiagnostic(), missingPermission);
            LogPermissionDenied(permissionCode, actionName, "initial");

            if (!PermissionDeniedDialog.ShowSwitchPrompt(this, deniedMessage))
            {
                return false;
            }

            if (!await ShowOperatorSwitchOrPosAccessAsync(permissionCode, missingPermission).ConfigureAwait(true))
            {
                return false;
            }

            if (HasCurrentPermission(permissionCode))
            {
                return true;
            }

            LogPermissionDenied(permissionCode, actionName, "after_switch");
            ModernMessageDialog.Show(
                this,
                PosLocalization.Current.Text("common.userPermissionDenied"),
                BuildPermissionDeniedMessage(message, GetCurrentRoleDiagnostic(), missingPermission));
            return false;
        }

        private static bool HasCurrentPermission(string permissionCode)
        {
            var user = OperatorSessionHolder.Current?.CurrentUser;
            if (user == null || string.IsNullOrWhiteSpace(permissionCode))
            {
                return false;
            }

            if (user.IsAdmin)
            {
                return true;
            }

            return user.PermissionCodes?.Contains(permissionCode) == true;
        }

        private static string BuildPermissionDeniedMessage(string message, string currentRole, string missingPermission)
        {
            var diagnostic = PosLocalization.Current.Format(
                "permission.denied.diagnostic",
                string.IsNullOrWhiteSpace(currentRole) ? "none" : currentRole,
                string.IsNullOrWhiteSpace(missingPermission) ? "unknown" : missingPermission);

            return string.IsNullOrWhiteSpace(message)
                ? diagnostic
                : diagnostic + Environment.NewLine + message;
        }

        private static string GetCurrentRoleDiagnostic()
        {
            var user = OperatorSessionHolder.Current?.CurrentUser;
            var role = user?.RoleCode;
            if (string.IsNullOrWhiteSpace(role))
            {
                role = user?.RoleName;
            }

            return SafeDiagnosticToken(string.IsNullOrWhiteSpace(role) ? "none" : role);
        }

        private static string PermissionDiagnosticName(string permissionCode)
        {
            switch (permissionCode)
            {
                case PermissionCodes.CatalogView: return "CatalogView";
                case PermissionCodes.CatalogEdit: return "CatalogEdit";
                case PermissionCodes.CatalogImport: return "CatalogImport";
                case PermissionCodes.CatalogPriceEdit: return "CatalogPriceEdit";
                case PermissionCodes.DailyCloseView: return "DailyCloseView";
                case PermissionCodes.DailyCloseRun: return "DailyCloseRun";
                case PermissionCodes.DailyClosePrint: return "DailyClosePrint";
                case PermissionCodes.DbMaintenance: return "DbMaintenance";
                case PermissionCodes.DbRestore: return "DbRestore";
                case PermissionCodes.SettingsShop: return "SettingsShop";
                case PermissionCodes.SettingsPrinter: return "SettingsPrinter";
                case PermissionCodes.UsersManage: return "UsersManage";
                case PermissionCodes.RolesManage: return "RolesManage";
                case PermissionCodes.RegisterView: return "RegisterView";
                case PermissionCodes.RegisterViewAll: return "RegisterViewAll";
                default:
                    return SafeDiagnosticToken(permissionCode);
            }
        }

        private static string SafeDiagnosticToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var chars = value.Trim()
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                .Take(96)
                .ToArray();
            return new string(chars);
        }

        private static void LogPermissionDenied(string permissionCode, string actionName, string stage)
        {
            var currentRole = SafeDiagnosticToken(GetCurrentRoleDiagnostic());
            var missingPermission = PermissionDiagnosticName(permissionCode);
            _logger.LogWarning(
                "category=permission.denied currentRole=" + currentRole +
                " missingPermission=" + SafeDiagnosticToken(missingPermission) +
                " action=" + SafeDiagnosticToken(actionName) +
                " stage=" + SafeDiagnosticToken(stage),
                null);
        }

        /// <summary>Dopo cambio operatore: ricaricare permessi, aggiornare UI e uscire da tab non consentiti.</summary>
        private void RefreshShellAfterOperatorChange(IOperatorSession session)
        {
            var posVm = GetPosViewModel();
            posVm?.RaiseCanExecuteChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();

            var currentUsername = session?.CurrentUser?.Username ?? "";
            if (UserManagementViewControl?.DataContext is UserManagementViewModel existingVm &&
                !string.Equals(existingVm.CurrentOperatorUsername, currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                UserManagementViewControl.DataContext = null;
            }

            if (session?.CurrentUser == null) return;
            var hasUsersManage = session.CurrentUser.IsAdmin || (session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.UsersManage) == true);
            var hasDailyCloseView = session.CurrentUser.IsAdmin || (session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.DailyCloseView) == true);
            var hasCatalogView = session.CurrentUser.IsAdmin || (session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.CatalogView) == true);

            if (!hasCatalogView ||
                !string.Equals(_productsDataContextOperatorUsername ?? "", currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                ClearProductsViewModel();
            }

            if (MainTabControl?.SelectedItem == UsersRolesTab)
            {
                if (!hasUsersManage)
                {
                    UserManagementViewControl.DataContext = null;
                    MainTabControl.SelectedIndex = 0;
                    CurrentMenuKey = "Pos";
                }
                else if (UserManagementViewControl?.DataContext == null)
                {
                    UserManagementViewControl.DataContext = posVm?.CreateUserManagementViewModel();
                }
            }
            else if (MainTabControl?.SelectedItem == DailyReportTab && !hasDailyCloseView)
            {
                DailyReportViewControl.DataContext = null;
                MainTabControl.SelectedIndex = 0;
                CurrentMenuKey = "Pos";
            }
            else if (MainTabControl?.SelectedItem == ProductsTab && !hasCatalogView)
            {
                ClearProductsViewModel();
                MainTabControl.SelectedIndex = 0;
                CurrentMenuKey = _recoveryMode ? "Settings" : "Pos";
            }
        }

        private async void OnMenuUsersClick(object sender, RoutedEventArgs e)
        {
            if (UserManagementViewControl == null || MainTabControl == null) return;
            var session = OperatorSessionHolder.Current;
            var hasUsersManage = HasCurrentPermission(PermissionCodes.UsersManage);
            if (!hasUsersManage)
            {
                UserManagementViewControl.DataContext = null;
                SetSideMenuOpen(false);
                if (await TrySwitchForPermissionAsync(
                        PermissionCodes.UsersManage,
                        PosLocalization.Current.Text("shell.usersPermissionDenied"),
                        "UsersRoles").ConfigureAwait(true))
                {
                    OnMenuUsersClick(sender, e);
                }
                return;
            }
            session = OperatorSessionHolder.Current;
            var currentUsername = session?.CurrentUser?.Username ?? "";
            if (UserManagementViewControl.DataContext is Pos.Dialogs.UserManagementViewModel existingVm &&
                string.Equals(existingVm.CurrentOperatorUsername, currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                CurrentMenuKey = "UsersRoles";
                MainTabControl.SelectedItem = UsersRolesTab;
                SetSideMenuOpen(false);
                return;
            }
            try
            {
                var vm = GetPosViewModel()?.CreateUserManagementViewModel();
                if (vm == null) return;
                UserManagementViewControl.DataContext = vm;
                CurrentMenuKey = "UsersRoles";
                MainTabControl.SelectedItem = UsersRolesTab;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("OnMenuUsersClick: permesso negato - " + ex.Message, null);
                SetSideMenuOpen(false);
                if (await TrySwitchForPermissionAsync(
                        PermissionCodes.UsersManage,
                        PosLocalization.Current.Text("shell.usersPermissionDenied"),
                        "UsersRoles").ConfigureAwait(true))
                {
                    OnMenuUsersClick(sender, e);
                }
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow.OnMenuUsersClick: errore apertura Utenti e ruoli");
                ModernMessageDialog.Show(
                    Application.Current?.MainWindow,
                    PosLocalization.Current.Text("shell.usersRoles"),
                    PosLocalization.Current.Text("shell.usersOpenLogError"));
            }
            SetSideMenuOpen(false);
        }

        /// <summary>Aggiorna lo sfondo dei pulsanti del menu laterale (voce attiva = SidebarSelectedBrush). Compatibile Win7.</summary>
        private void UpdateMenuSelectionVisual()
        {
            if (SideMenuPanel == null) return;
            try
            {
                var selectedBrush = TryFindResource("SidebarSelectedBrush") as Brush;
                var cardBrush = TryFindResource("SidebarCardBrush") as Brush;
                if (selectedBrush == null || cardBrush == null) return;

                var key = CurrentMenuKey ?? "";
                if (key == "ShopSettings" || key == "Printer" || key == "DbMaintenance" || key == "UsersRoles" || key == "About")
                    key = "Settings";
                foreach (var btn in FindButtonsByTag(SideMenuPanel))
                {
                    var tag = btn.Tag?.ToString() ?? "";
                    btn.Background = string.Equals(tag, key, StringComparison.Ordinal) ? selectedBrush : cardBrush;
                }
            }
            catch
            {
                // ignorare errori di risorse/layout
            }
        }

        private static System.Collections.Generic.IEnumerable<Button> FindButtonsByTag(DependencyObject root)
        {
            if (root == null) yield break;
            if (root is Button b && b.Tag is string)
                yield return b;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                foreach (var x in FindButtonsByTag(child))
                    yield return x;
            }
        }

        private void OnContentRendered(object sender, EventArgs e)
        {
            StartupTrace.Write("ContentRendered fired");
            _contentRendered.TrySetResult(true);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainTabControl.SelectedIndex = 0;
                MainTabControl.UpdateLayout();
                PosViewControl?.InvalidateMeasure();
                PosViewControl?.InvalidateArrange();
                PosViewControl?.UpdateLayout();
            }), DispatcherPriority.Loaded);
        }

        private void MainTabControl_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateCurrentMenuKeyFromTab();
            UpdateShellForCurrentView();
        }

        private void UpdateCurrentMenuKeyFromTab()
        {
            if (MainTabControl == null) return;
            var selected = MainTabControl.SelectedItem;
            if (selected == UsersRolesTab) CurrentMenuKey = "UsersRoles";
            else if (selected == DailyReportTab) CurrentMenuKey = "DailyReport";
            else if (selected == ProductsTab) CurrentMenuKey = "Prodotti";
            else if (selected == PaymentTab) { /* Pagamento: chiave invariata */ }
            else CurrentMenuKey = "Pos";
        }

        private void UpdateShellForCurrentView()
        {
            var isPayment = MainTabControl?.SelectedItem == PaymentTab;
            if (TopHeaderBar != null)
                TopHeaderBar.Visibility = isPayment ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnHamburgerClick(object sender, RoutedEventArgs e)
        {
            if (SideMenuOverlay.Visibility == Visibility.Visible)
            {
                SetSideMenuOpen(false);
            }
            else
            {
                CommandManager.InvalidateRequerySuggested();
                UpdateMenuSelectionVisual();
                SetSideMenuOpen(true);
            }
        }

        private void SetSideMenuOpen(bool isOpen)
        {
            if (SideMenuOverlay == null || SideMenuPanel == null)
                return;

            var transform = SideMenuPanel.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                SideMenuPanel.RenderTransform = transform;
            }

            var duration = TimeSpan.FromMilliseconds(120);
            if (isOpen)
            {
                SideMenuOverlay.Visibility = Visibility.Visible;
                SideMenuPanel.Visibility = Visibility.Visible;
                SideMenuPanel.Opacity = 0;
                transform.X = -18;
                SideMenuPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, duration));
                transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration));
                SideMenuPanel.Focus();
                return;
            }

            if (SideMenuOverlay.Visibility != Visibility.Visible)
            {
                SideMenuOverlay.Visibility = Visibility.Collapsed;
                SideMenuPanel.Visibility = Visibility.Collapsed;
                SideMenuPanel.Opacity = 0;
                transform.X = -18;
                return;
            }

            var fade = new DoubleAnimation(0, duration);
            fade.Completed += (_, __) =>
            {
                SideMenuOverlay.Visibility = Visibility.Collapsed;
                SideMenuPanel.Visibility = Visibility.Collapsed;
                SideMenuPanel.Opacity = 0;
                transform.X = -18;
                HamburgerButton?.Focus();
            };
            SideMenuPanel.BeginAnimation(UIElement.OpacityProperty, fade);
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, duration));
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && SideMenuOverlay?.Visibility == Visibility.Visible)
            {
                SetSideMenuOpen(false);
                e.Handled = true;
            }
        }

        private void OnOverlayClick(object sender, MouseButtonEventArgs e)
        {
            SetSideMenuOpen(false);
        }

        private void OnPanelClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void OnMenuPosClick(object sender, RoutedEventArgs e)
        {
            if (_recoveryMode)
            {
                CurrentMenuKey = "Prodotti";
                MainTabControl.SelectedItem = ProductsTab;
                SetSideMenuOpen(false);
                return;
            }

            CurrentMenuKey = "Pos";
            MainTabControl.SelectedIndex = 0;
            SetSideMenuOpen(false);
        }

        private void OnMenuOpenCashDrawerClick(object sender, RoutedEventArgs e)
        {
            if (_recoveryMode) return;
            GetPosViewModel()?.OpenCashDrawerCommand?.Execute(null);
            SetSideMenuOpen(false);
        }

        private async void OnMenuProdottiClick(object sender, RoutedEventArgs e)
        {
            var session = OperatorSessionHolder.Current;
            var hasCatalogView = HasCurrentPermission(PermissionCodes.CatalogView);
            if (!hasCatalogView)
            {
                SetSideMenuOpen(false);
                if (await TrySwitchForPermissionAsync(
                        PermissionCodes.CatalogView,
                        PosLocalization.Current.Text("shell.productsPermissionDenied"),
                        "Products").ConfigureAwait(true))
                {
                    OnMenuProdottiClick(sender, e);
                }
                return;
            }

            session = OperatorSessionHolder.Current;
            try
            {
                var vm = EnsureProductsViewModel(session);
                await vm.LoadAsync().ConfigureAwait(true);
                CurrentMenuKey = "Prodotti";
                MainTabControl.SelectedItem = ProductsTab;
                SetSideMenuOpen(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow.OnMenuProdottiClick: errore apertura Prodotti");
                ModernMessageDialog.Show(
                    Application.Current?.MainWindow,
                    PosLocalization.Current.Text("shell.products"),
                    PosLocalization.Current.Text("shell.productsOpenLogError"));
                SetSideMenuOpen(false);
            }
        }

        private ProductsViewModel EnsureProductsViewModel(IOperatorSession session)
        {
            var currentUsername = session?.CurrentUser?.Username ?? "";
            if (ProductsViewControl?.DataContext is ProductsViewModel existing &&
                string.Equals(_productsDataContextOperatorUsername ?? "", currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            var vm = new ProductsViewModel();
            ProductsViewControl.DataContext = vm;
            _productsDataContextOperatorUsername = currentUsername;
            return vm;
        }

        private void ClearProductsViewModel()
        {
            if (ProductsViewControl != null)
                ProductsViewControl.DataContext = null;
            _productsDataContextOperatorUsername = null;
        }

        private PosViewModel GetPosViewModel()
        {
            return PosViewControl?.DataContext as PosViewModel;
        }

        private PosView EnsurePosViewCreated()
        {
            if (PosViewControl != null)
            {
                return PosViewControl;
            }

            PosViewControl = new PosView();
            PosTabHost.Children.Clear();
            PosTabHost.Children.Add(PosViewControl);
            return PosViewControl;
        }

        private async void OnMenuDailyReportClick(object sender, RoutedEventArgs e)
        {
            if (_recoveryMode)
            {
                SetSideMenuOpen(false);
                return;
            }

            if (!HasCurrentPermission(PermissionCodes.DailyCloseView))
            {
                SetSideMenuOpen(false);
                if (await TrySwitchForPermissionAsync(
                        PermissionCodes.DailyCloseView,
                        PosLocalization.Current.Format("common.permissionDeniedOperation", PosLocalization.Current.Text("operations.dailyClose")),
                        "DailyClose").ConfigureAwait(true))
                {
                    OnMenuDailyReportClick(sender, e);
                }
                return;
            }

            var posVm = GetPosViewModel();
            if (posVm != null && DailyReportViewControl != null)
            {
                DailyReportViewControl.DataContext = posVm.CreateDailyReportViewModel();
            }
            CurrentMenuKey = "DailyReport";
            MainTabControl.SelectedIndex = 2; // 0=POS, 1=Prodotti, 2=Chiusura cassa, 3=Pagamento
            SetSideMenuOpen(false);
        }

        private void OnMenuSettingsClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "Settings";
            MainTabControl.SelectedIndex = 0;
            SetSideMenuOpen(false);
            ShowSettingsHubDialog();
        }

        private void ShowSettingsHubDialog()
        {
            try
            {
                var dialog = new SettingsHubDialog(_recoveryMode)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };

                dialog.ShopDataRequested += (_, __) => OnMenuShopSettingsClick(this, new RoutedEventArgs());
                dialog.PrinterSettingsRequested += (_, __) => OnMenuPrinterClick(this, new RoutedEventArgs());
                dialog.DatabaseMaintenanceRequested += (_, __) => OnMenuDbClick(this, new RoutedEventArgs());
                dialog.UsersRolesRequested += (_, __) => OnMenuUsersClick(this, new RoutedEventArgs());
                dialog.AboutRequested += (_, __) => OnMenuAboutClick(this, new RoutedEventArgs());
                dialog.OnlineAccessRequested += (_, __) => OnRetryRecoveryOnlineClick(this, new RoutedEventArgs());
                dialog.LanguageChangedRequested += async (_, code) => await SaveLanguagePreferenceAsync(code).ConfigureAwait(true);

                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow.ShowSettingsHubDialog: errore apertura Settings hub");
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.Current.Text("shell.settings"),
                    PosLocalization.Current.Text("settings.openLogError"));
            }
        }

        private async void OnMenuDbClick(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            if (!HasCurrentPermission(PermissionCodes.DbMaintenance))
            {
                SetSideMenuOpen(false);
                if (await TrySwitchForPermissionAsync(
                        PermissionCodes.DbMaintenance,
                        PosLocalization.Current.Format("common.permissionDeniedOperation", PosLocalization.Current.Text("operations.dbMaintenance")),
                        "DatabaseMaintenance").ConfigureAwait(true))
                {
                    OnMenuDbClick(sender, e);
                }
                return;
            }

            if (_recoveryMode)
            {
                ShowRecoveryDbMaintenanceDialog();
            }
            else
            {
                GetPosViewModel()?.DbMaintenanceCommand?.Execute(null);
            }
            SetSideMenuOpen(false);
        }

        private void ShowRecoveryDbMaintenanceDialog()
        {
            if (_recoveryWorkflowService == null)
            {
                _recoveryWorkflowService = new PosWorkflowService();
            }
            var vm = new DbMaintenanceViewModel(
                _recoveryWorkflowService,
                () => Task.FromResult(HasCurrentPermission(PermissionCodes.DbRestore)));
            var dialog = new DbMaintenanceDialog(vm)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(this)
            };
            dialog.ShowDialog();
        }

        private void OnMenuPrinterClick(object sender, RoutedEventArgs e)
        {
            if (_recoveryMode) return;
            CurrentMenuKey = "Printer";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.PrinterSettingsCommand?.Execute(null);
            SetSideMenuOpen(false);
        }

        private void OnMenuShopSettingsClick(object sender, RoutedEventArgs e)
        {
            if (_recoveryMode) return;
            CurrentMenuKey = "ShopSettings";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenShopSettingsCommand?.Execute(null);
            SetSideMenuOpen(false);
        }

        private void OnMenuAboutClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "About";
            MainTabControl.SelectedIndex = 0;
            if (_recoveryMode)
            {
                if (_recoveryWorkflowService == null)
                {
                    _recoveryWorkflowService = new PosWorkflowService();
                }
                var dialog = new AboutSupportDialog(new AboutSupportViewModel(_recoveryWorkflowService))
                {
                    Owner = DialogOwnerHelper.GetSafeOwner(this)
                };
                dialog.ShowDialog();
            }
            else
            {
                GetPosViewModel()?.AboutSupportCommand?.Execute(null);
            }
            SetSideMenuOpen(false);
        }

        private void OnMenuSalesRegisterClick(object sender, RoutedEventArgs e)
        {
            if (_recoveryMode)
            {
                SetSideMenuOpen(false);
                return;
            }
            CurrentMenuKey = "SalesRegister";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenSalesRegisterCommand?.Execute(null);
            SetSideMenuOpen(false);
        }

        /// <summary>Mostra la schermata Pagamento e attende la chiusura (RequestClose).</summary>
        public Task<bool> ShowPaymentScreenAsync(PaymentViewModel vm)
        {
            var tcs = new TaskCompletionSource<bool>();
            var prevIndex = MainTabControl.SelectedIndex;

            void Cleanup(bool ok)
            {
                vm.RequestClose -= OnClose;
                PaymentViewControl.DataContext = null;

                MainTabControl.SelectedIndex = prevIndex;
                UpdateShellForCurrentView();
                HamburgerButton.IsEnabled = true;
                tcs.TrySetResult(ok);
            }

            void OnClose(bool ok)
            {
                Dispatcher.BeginInvoke(new Action(() => Cleanup(ok)));
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                HamburgerButton.IsEnabled = false;
                SetSideMenuOpen(false);

                PaymentViewControl.DataContext = vm;
                vm.RequestClose += OnClose;

                MainTabControl.SelectedIndex = 3; // 0 POS, 1 Prodotti, 2 Chiusura cassa, 3 Pagamento
                UpdateShellForCurrentView();
            }));

            return tcs.Task;
        }
    }
}
