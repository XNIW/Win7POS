using System;
using System.ComponentModel;
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
using Win7POS.Wpf.Infrastructure.Displays;
using Win7POS.Wpf.Pos.CustomerDisplay;

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
        private static readonly Infrastructure.FileLogger _logger = new Infrastructure.FileLogger("MainWindow");
        private static readonly TimeSpan StartupWatchdogTimeout = TimeSpan.FromSeconds(5);
        private readonly TaskCompletionSource<bool> _contentRendered = new TaskCompletionSource<bool>();
        private DispatcherTimer _syncStatusTimer;
        private DispatcherTimer _networkStatusTimer;
        private DispatcherTimer _authorizationLeaseTimer;
        private bool _authorizationLeaseBlockHandled;
        private PosStartupCoordinator _startupCoordinator;
        private bool? _lastNetworkOnline;
        private DateTimeOffset _lastForegroundSyncTrigger = DateTimeOffset.MinValue;
        private bool _operatorLoginReached;
        private bool _recoveryTabClampActive;
        private SqliteConnectionFactory _languageSettingsFactory;
        private PosWorkflowService _recoveryWorkflowService;
        private string _startupPhase = "constructed";
        private string _productsDataContextOperatorUsername;
        private bool _productsDataContextRecoveryMode;
        private bool _productsDataContextLeaseFreeLocalRecovery;
        private IOperatorSession _productsDataContextSession;
        private PosView PosViewControl;
        private Action<bool> _activePaymentCleanup;
        private CustomerDisplayManager _customerDisplayManager;
        private bool _programmaticClose;
        private bool _sessionEnding;
        private bool _closeConfirmationOpen;
        private bool _cleanupCompleted;
        private EventHandler _languageChangedHandler;
        private IOperatorSession _observedOperatorSession;
        private Action _operatorSessionChangedHandler;
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

        private SqliteConnectionFactory OnlineSchedulerFactory => _startupCoordinator?.Factory;

        private PosOnlineSyncSupervisorHost OnlineSyncHost => _startupCoordinator?.Host;

        private PosAuthenticatedAccessMode CurrentAccessMode =>
            _startupCoordinator?.AccessMode ?? PosAuthenticatedAccessMode.Normal;

        private bool IsRecoveryMode => _startupCoordinator?.IsRecoveryMode == true;

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
            Activated += OnShellActivated;
            Closed += OnShellClosed;
            Application.Current.SessionEnding += OnApplicationSessionEnding;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // The POS shell is a full-screen workstation surface. It may be minimized,
            // but restoring it to a resizable normal window would expose clipped layouts.
            if (WindowState == WindowState.Normal)
            {
                SetCurrentValue(WindowStateProperty, WindowState.Maximized);
            }

            _customerDisplayManager?.SetCashierMinimized(WindowState == WindowState.Minimized);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            var state = CollectCloseState();
            var decision = MainShellClosePolicy.Decide(state);
            if (decision == MainShellCloseDecision.Allow ||
                decision == MainShellCloseDecision.BypassForSystemShutdown)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            if (_closeConfirmationOpen)
            {
                base.OnClosing(e);
                return;
            }

            if (decision == MainShellCloseDecision.BlockUntilOperationCompletes)
            {
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(this),
                    PosLocalization.Current.Text("exit.title"),
                    PosLocalization.Current.Text("exit.operationInProgress"));
                base.OnClosing(e);
                return;
            }

            _closeConfirmationOpen = true;
            try
            {
                var vm = GetPosViewModel();
                var dialog = new ExitConfirmationDialog(vm?.ItemsCount ?? 0, vm?.Total ?? 0)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner(this)
                };
                dialog.ShowDialog();
                switch (dialog.Choice)
                {
                    case ExitConfirmationChoice.CloseApplication:
                        _programmaticClose = true;
                        e.Cancel = false;
                        break;
                    case ExitConfirmationChoice.Minimize:
                        WindowState = WindowState.Minimized;
                        break;
                    default:
                        SetCurrentValue(WindowStateProperty, WindowState.Maximized);
                        Dispatcher.BeginInvoke(new Action(() => PosViewControl?.RestoreScannerFocus()));
                        break;
                }
            }
            finally
            {
                _closeConfirmationOpen = false;
            }
            base.OnClosing(e);
        }

        public void CloseWithoutUserPrompt()
        {
            _programmaticClose = true;
            Close();
        }

        internal void PrepareForSessionEnding()
        {
            _sessionEnding = true;
            CleanupBestEffort();
        }

        private MainShellCloseState CollectCloseState()
        {
            if (_sessionEnding) return MainShellCloseState.SessionEnding;
            if (_programmaticClose) return MainShellCloseState.ProgrammaticClose;

            var state = MainShellCloseState.Idle;
            var vm = GetPosViewModel();
            if (vm?.CartItems.Count > 0) state |= MainShellCloseState.CartNotEmpty;
            if (PaymentViewControl?.DataContext != null || vm?.IsPaymentCommitInProgress == true)
                state |= MainShellCloseState.PaymentInProgress;
            var syncSnapshot = _startupCoordinator?.GetSnapshot();
            if (syncSnapshot != null && syncSnapshot.Lanes.Any(lane => lane.InFlight))
            {
                state |= MainShellCloseState.IncrementalSyncInProgress;
            }
            if (_startupCoordinator?.IsFullCatalogRepairInProgress == true)
                state |= MainShellCloseState.FullCatalogRepairInProgress;
            return state;
        }

        private void OnApplicationSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            PrepareForSessionEnding();
        }

        private void OnShellClosed(object sender, EventArgs e)
        {
            CleanupBestEffort();
        }

        private void CleanupBestEffort()
        {
            if (_cleanupCompleted) return;
            _cleanupCompleted = true;
            try { Application.Current.SessionEnding -= OnApplicationSessionEnding; } catch { }
            try
            {
                if (_languageChangedHandler != null)
                    PosLocalization.Current.LanguageChanged -= _languageChangedHandler;
            }
            catch { }
            _languageChangedHandler = null;
            try
            {
                if (_observedOperatorSession != null && _operatorSessionChangedHandler != null)
                    _observedOperatorSession.SessionChanged -= _operatorSessionChangedHandler;
            }
            catch { }
            _observedOperatorSession = null;
            _operatorSessionChangedHandler = null;
            try { _authorizationLeaseTimer?.Stop(); } catch { }
            try { _syncStatusTimer?.Stop(); } catch { }
            try { _networkStatusTimer?.Stop(); } catch { }
            try
            {
                _startupCoordinator?.Dispose();
                _startupCoordinator = null;
            }
            catch { }
            try { DailyReportViewControl.DataContext = null; } catch { }
            try { GetPosViewModel()?.Dispose(); } catch { }
            try { _customerDisplayManager?.Dispose(); } catch { }
            _customerDisplayManager = null;
        }

        private void InitializeLanguageSelector()
        {
            _languageChangedHandler = (_, __) =>
            {
                if (_cleanupCompleted) return;
                UpdateNetworkStatusBadge();
                QueueSyncStatusRefresh(_languageSettingsFactory);
            };
            PosLocalization.Current.LanguageChanged += _languageChangedHandler;
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
                var factory = _languageSettingsFactory ?? _startupCoordinator?.Factory;
                if (factory == null)
                {
                    return;
                }
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
                _startupPhase = "Startup coordinator initialization";
                StartupTrace.Write("DB init start");
                _startupCoordinator = new PosStartupCoordinator(
                    () => App.IsSafeStart,
                    _logger.LogInfo);
                var factory = _startupCoordinator.Initialize();
                StartupTrace.Write("DB init end");
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

                await WaitForContentRenderedOrTimeoutAsync().ConfigureAwait(true);

                _logger.LogInfo("POS access dialog opening");
                _startupPhase = "POS access opening";
                StartupTrace.Write("POS access dialog about to open");
                var login = new PosOnlineFirstLoginDialog(
                    factory,
                    resumeCatalogOnly: false,
                    OnlineSyncHost) { Owner = this };
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
                    CloseWithoutUserPrompt();
                    return;
                }
                _logger.LogInfo("POS access dialog accepted");
                var access = await _startupCoordinator
                    .AcceptAuthenticatedAccessAsync(login.AccessMode)
                    .ConfigureAwait(true);
                await RefreshShellTitleAsync(factory).ConfigureAwait(true);

                var session = access.Session;
                StartOfDaySyncResult startOfDayResult = null;
                if (session != null)
                {
                    // Authentication is already complete at this point. Reflect it in the shell
                    // before the modal sync gate so a later data-safety block is not mistaken for
                    // a failed sign-in.
                    UpdateOperatorDisplay(session);
                    var shellMode = access.ShellMode;

                    if (shellMode == PosShellMode.Recovery)
                    {
                        await EnterRecoveryModeAsync(factory).ConfigureAwait(true);
                    }
                    else
                    {
                        if (!session.EnsureAuthorizationValid())
                        {
                            HandleAuthorizationLeaseDenied(session);
                            return;
                        }

                        RefreshAuthorizationLeaseSchedule(session);
                        if (!App.IsSafeStart)
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
                                CloseWithoutUserPrompt();
                                return;
                            }
                        }
                    }

                    if (shellMode == PosShellMode.Pos)
                    {
                        EnsurePosViewCreated();
                        await EnsureCustomerDisplayManagerAsync(factory).ConfigureAwait(true);
                    }
                    UpdateOperatorDisplay(session);
                    RefreshShellAfterOperatorChange(session);
                    if (_observedOperatorSession != null && _operatorSessionChangedHandler != null)
                        _observedOperatorSession.SessionChanged -= _operatorSessionChangedHandler;
                    _observedOperatorSession = session;
                    _operatorSessionChangedHandler = () =>
                    {
                        if (_cleanupCompleted) return;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_cleanupCompleted) return;
                            UpdateOperatorDisplay(session);
                            RefreshShellAfterOperatorChange(session);
                        }));
                    };
                    session.SessionChanged += _operatorSessionChangedHandler;
                }

                if (IsRecoveryMode)
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
                    QueueBackgroundOnlineRefresh(factory);
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
                CloseWithoutUserPrompt();
            }
        }

        private async Task<StartOfDaySyncResult> RunStartOfDaySyncAsync(SqliteConnectionFactory factory)
        {
            var dialog = new PosStartOfDaySyncDialog(factory, OnlineSyncHost)
            {
                Owner = this
            };
            dialog.SyncDetailsRequested += (_, __) => ShowSyncCenterDialog(dialog);

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

            var vm = new DbMaintenanceViewModel(
                new PosWorkflowService(),
                () => Task.FromResult(false),
                () => false,
                () => false,
                () => HasCurrentPermission(PermissionCodes.DbMaintenance));
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

            StartupTrace.Write("online sync supervisor start");
            _startupCoordinator?.StartBackground();
        }

        private void StartAdaptiveOnlineScheduler(
            SqliteConnectionFactory factory,
            CatalogSyncTrigger initialTrigger)
        {
            if (factory == null)
            {
                return;
            }

            _startupCoordinator?.StartAdaptive(factory, initialTrigger);
        }


        private Task<CatalogSyncRunResult> TriggerAdaptiveOnlineRefreshAsync(
            SqliteConnectionFactory factory,
            CatalogSyncTrigger requestedTrigger,
            CancellationToken cancellationToken,
            bool administratorRepairAuthorized = false,
            bool allowFullDecision = true)
        {
            var coordinator = _startupCoordinator;
            if (coordinator == null)
            {
                return Task.FromResult(new CatalogSyncRunResult(
                    success: false,
                    authenticationDenied: true,
                    code: "trusted_session_missing"));
            }

            return coordinator.TriggerAdaptiveOnlineRefreshAsync(
                requestedTrigger,
                cancellationToken,
                administratorRepairAuthorized,
                allowFullDecision);
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
            var coordinator = _startupCoordinator;
            if (coordinator == null)
            {
                return;
            }

            await coordinator.EnterRecoveryAsync(factory).ConfigureAwait(true);
            if (HasLeaseFreeLocalRecoveryAccess())
            {
                _authorizationLeaseTimer?.Stop();
            }
            else
            {
                RefreshAuthorizationLeaseSchedule(OperatorSessionHolder.Current);
            }
            _syncStatusTimer?.Stop();
            CancelActivePaymentForRecovery();
            DailyReportViewControl.DataContext = null;
            UserManagementViewControl.DataContext = null;
            SuspendPosViewForRecovery();
            ClearProductsViewModel();
            RecoveryModeBanner.Visibility = Visibility.Visible;
            RecoveryPlaceholder.Visibility = Visibility.Visible;
            var localRecoveryAccess =
                CurrentAccessMode == PosAuthenticatedAccessMode.LocalRecovery;
            RecoveryStatusText.Text = PosLocalization.Current.Text(
                localRecoveryAccess ? "access.login.recoveryOnlineRequired"
                    : "access.login.recoveryModeHelp");
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
            _logger.LogWarning(
                "category=pos.recovery result=entered reason=" +
                (localRecoveryAccess ? "local_recovery_access" : "catalog_not_sale_safe"),
                null);
        }

        private async Task<bool> ExitRecoveryModeAsync()
        {
            var coordinator = _startupCoordinator;
            if (coordinator == null)
            {
                return false;
            }

            var session = coordinator.Session;
            var validation = await coordinator
                .ValidateRecoveryExitAsync(session)
                .ConfigureAwait(true);
            if (!validation.CanExit)
            {
                RecoveryStatusText.Text = validation.Code == "catalog_still_unsafe"
                    ? PosLocalization.Current.Text("access.login.catalogStillUnsafe")
                    : PosLocalization.Current.Text("access.login.recoveryOnlineRequired");
                _logger.LogInfo("category=pos.recovery result=blocked reason=" + validation.Code);
                return false;
            }

            var factory = coordinator.GetRecoveryFactory();
            StartOfDaySyncResult startOfDayResult = null;
            if (!App.IsSafeStart)
            {
                startOfDayResult = await RunStartOfDaySyncAsync(factory).ConfigureAwait(true);
                if (startOfDayResult == null || !startOfDayResult.CanOpenPos)
                {
                    await coordinator.StopAsync().ConfigureAwait(true);
                    RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.recoveryModeHelp");
                    return false;
                }
            }

            var completion = await coordinator
                .CompleteRecoveryExitAsync(coordinator.Session)
                .ConfigureAwait(true);
            if (!completion.CanExit)
            {
                RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.recoveryOnlineRequired");
                _logger.LogInfo("category=pos.recovery result=blocked reason=authorization_changed");
                return false;
            }

            ClearProductsViewModel();
            _syncStatusTimer?.Start();
            RefreshAuthorizationLeaseSchedule(OperatorSessionHolder.Current);
            RecoveryModeBanner.Visibility = Visibility.Collapsed;
            RecoveryPlaceholder.Visibility = Visibility.Collapsed;
            SetRecoveryNavigationState(false);
            EnsurePosViewCreated();
            _customerDisplayManager?.SetOperatorLocked(false);
            CurrentMenuKey = "Pos";
            MainTabControl.SelectedIndex = 0;
            RefreshShellAfterOperatorChange(OperatorSessionHolder.Current);

            if (App.IsSafeStart)
            {
                ApplySafeStartStatus();
            }
            else
            {
                QueueBackgroundOnlineRefresh(factory);
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

        private bool HasLeaseFreeLocalRecoveryAccess()
        {
            return _startupCoordinator?.HasLeaseFreeLocalRecoveryAccess() == true;
        }

        private async void OnRetryRecoveryOnlineClick(object sender, RoutedEventArgs e)
        {
            if (!IsRecoveryMode) return;

            var coordinator = _startupCoordinator;
            if (coordinator == null) return;
            var factory = coordinator.GetRecoveryFactory();
            var login = new PosOnlineFirstLoginDialog(
                factory,
                resumeCatalogOnly: false,
                syncHost: OnlineSyncHost)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(this)
            };
            if (login.ShowDialog() != true ||
                OperatorSessionHolder.Current == null ||
                !OperatorSessionHolder.Current.IsLoggedIn)
            {
                if (_startupCoordinator != null)
                    await _startupCoordinator.StopAsync().ConfigureAwait(true);
                return;
            }

            coordinator.SetAuthenticatedAccessMode(login.AccessMode);
            UpdateOperatorDisplay(OperatorSessionHolder.Current);
            if (login.AccessMode == PosAuthenticatedAccessMode.Normal)
            {
                RefreshAuthorizationLeaseSchedule(OperatorSessionHolder.Current);
            }
            RefreshShellAfterOperatorChange(OperatorSessionHolder.Current);
            if (login.AccessMode != PosAuthenticatedAccessMode.Normal)
            {
                if (_startupCoordinator != null)
                    await _startupCoordinator.StopAsync().ConfigureAwait(true);
                RecoveryStatusText.Text = PosLocalization.Current.Text("access.login.recoveryOnlineRequired");
                return;
            }

            await ExitRecoveryModeAsync().ConfigureAwait(true);
        }

        private async void OnVerifyRecoveryCatalogClick(object sender, RoutedEventArgs e)
        {
            if (!IsRecoveryMode || VerifyRecoveryCatalogButton == null) return;

            VerifyRecoveryCatalogButton.IsEnabled = false;
            try
            {
                if (!HasCurrentPermission(PermissionCodes.DbMaintenance))
                {
                    RecoveryStatusText.Text = PosLocalization.Current.Format(
                        "common.permissionDeniedOperation",
                        PosLocalization.Current.Text("operations.dbMaintenance"));
                    return;
                }

                var user = OperatorSessionHolder.Current?.CurrentUser;
                if (user == null)
                {
                    RecoveryStatusText.Text = PosLocalization.Current.Text("operator.login.sessionMissing");
                    return;
                }

                var approved = await _startupCoordinator
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
                if (IsRecoveryMode)
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
                factory = factory ?? _startupCoordinator?.Factory ?? _languageSettingsFactory;
                if (factory == null)
                {
                    return;
                }
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

        private async void OnShellActivated(object sender, EventArgs e)
        {
            if (!_operatorLoginReached ||
                IsRecoveryMode ||
                App.IsSafeStart ||
                OperatorSessionHolder.Current == null ||
                !OperatorSessionHolder.Current.IsLoggedIn)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastForegroundSyncTrigger < TimeSpan.FromSeconds(60))
            {
                return;
            }

            var factory = OnlineSchedulerFactory ?? _languageSettingsFactory;
            if (factory == null)
            {
                return;
            }

            try
            {
                var lastSuccess = await new SettingsRepository(factory).GetStringAsync(
                    CatalogSyncDiagnosticsRepository.Prefix + "last_success_at").ConfigureAwait(true);
                if (DateTimeOffset.TryParse(lastSuccess, out var parsed) &&
                    now - parsed.ToUniversalTime() <= TimeSpan.FromSeconds(60))
                {
                    return;
                }

                _lastForegroundSyncTrigger = now;
                StartAdaptiveOnlineScheduler(factory, CatalogSyncTrigger.Foreground);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Foreground sync trigger skipped.", ex);
            }
        }

        private void UpdateNetworkStatusBadge()
        {
            try
            {
                var status = NetworkStatusService.Read();
                var online = status != null && status.IsNetworkAvailable;
                var wasOnline = _lastNetworkOnline;
                _lastNetworkOnline = online;
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

                if (online && wasOnline == false)
                {
                    var factory = OnlineSchedulerFactory ?? _languageSettingsFactory;
                    if (factory != null &&
                        OperatorSessionHolder.Current != null &&
                        OperatorSessionHolder.Current.IsLoggedIn)
                    {
                        StartAdaptiveOnlineScheduler(factory, CatalogSyncTrigger.NetworkRecovered);
                    }
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
                var session = OperatorSessionHolder.Current;
                if (session != null && session.IsLoggedIn)
                {
                    RefreshAuthorizationLeaseSchedule(session);
                    if (!session.IsLoggedIn)
                    {
                        return;
                    }
                }

                await RefreshSyncStatusStripAsync(factory).ConfigureAwait(true);
            };
            _syncStatusTimer.Start();
        }

        private void RefreshAuthorizationLeaseSchedule(IOperatorSession session)
        {
            if (HasLeaseFreeLocalRecoveryAccess())
            {
                _authorizationLeaseTimer?.Stop();
                return;
            }

            if (session == null)
            {
                return;
            }

            var decision = session.EvaluateAuthorizationLease();
            if (!decision.Allowed ||
                !decision.EffectiveExpiresAt.HasValue ||
                !decision.EstimatedServerNow.HasValue)
            {
                HandleAuthorizationLeaseDenied(session);
                return;
            }

            var remaining = decision.EffectiveExpiresAt.Value - decision.EstimatedServerNow.Value;
            if (remaining <= TimeSpan.Zero)
            {
                HandleAuthorizationLeaseDenied(session);
                return;
            }

            if (_authorizationLeaseTimer == null)
            {
                _authorizationLeaseTimer = new DispatcherTimer();
                _authorizationLeaseTimer.Tick += (_, __) =>
                {
                    _authorizationLeaseTimer.Stop();
                    RefreshAuthorizationLeaseSchedule(OperatorSessionHolder.Current);
                };
            }

            _authorizationLeaseTimer.Stop();
            _authorizationLeaseTimer.Interval = remaining < TimeSpan.FromMilliseconds(1)
                ? TimeSpan.FromMilliseconds(1)
                : remaining;
            _authorizationLeaseTimer.Start();
        }

        private void HandleAuthorizationLeaseDenied(IOperatorSession session)
        {
            _authorizationLeaseTimer?.Stop();
            if (HasLeaseFreeLocalRecoveryAccess())
            {
                return;
            }

            session?.EnsureAuthorizationValid();
            if (_authorizationLeaseBlockHandled)
            {
                return;
            }

            _authorizationLeaseBlockHandled = true;
            ModernMessageDialog.Show(
                this,
                PosLocalization.Current.Text("access.login.title"),
                PosLocalization.Current.Text("access.login.authorizationExpired"));
            CloseWithoutUserPrompt();
        }

        private async Task RefreshSyncStatusStripAsync(SqliteConnectionFactory factory = null)
        {
            try
            {
                factory = factory ?? _startupCoordinator?.Factory ?? _languageSettingsFactory;
                if (factory == null)
                {
                    return;
                }
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

        private async Task<bool> ShowOperatorSwitchOrPosAccessAsync(
            string requiredPermissionCode = null,
            string requiredPermissionName = null,
            Window dialogOwner = null)
        {
            var coordinator = _startupCoordinator;
            var factory = coordinator?.Factory;
            if (factory == null || coordinator == null)
            {
                return false;
            }

            var session = coordinator.EnsureSession();
            var switchDlg = new OperatorSwitchDialog(factory, session)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(dialogOwner ?? this)
            };

            await switchDlg.InitializeAsync(requiredPermissionCode, requiredPermissionName).ConfigureAwait(true);
            _customerDisplayManager?.SetOperatorLocked(true);
            try
            {
                switchDlg.ShowDialog();

                if (switchDlg.PosAccessRequested)
                {
                    return await OpenPosAccessForOperatorChangeAsync(factory, dialogOwner)
                        .ConfigureAwait(true);
                }

                if (!switchDlg.SwitchSucceeded)
                {
                    return false;
                }

                UpdateOperatorDisplay(session);
                RefreshShellAfterOperatorChange(session);
                return true;
            }
            finally
            {
                _customerDisplayManager?.SetOperatorLocked(IsRecoveryMode);
            }
        }

        private async Task<bool> OpenPosAccessForOperatorChangeAsync(
            SqliteConnectionFactory factory,
            Window dialogOwner = null)
        {
            var loginDlg = new PosOnlineFirstLoginDialog(
                factory,
                resumeCatalogOnly: false,
                syncHost: OnlineSyncHost)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(dialogOwner ?? this)
            };
            var accessAccepted = loginDlg.ShowDialog() == true;
            var currentSession = OperatorSessionHolder.Current;
            if (!accessAccepted || currentSession == null || !currentSession.IsLoggedIn)
            {
                var existingSession = currentSession;
                if (CurrentAccessMode == PosAuthenticatedAccessMode.Normal &&
                    existingSession != null &&
                    existingSession.IsLoggedIn)
                {
                    var identityStillBound = await IsSessionBoundToCurrentTrustedIdentityAsync(
                        factory,
                        existingSession).ConfigureAwait(true);
                    if (!identityStillBound)
                    {
                        _logger.LogWarning(
                            "category=operator_change result=blocked reason=trusted_identity_changed_after_cancel",
                            null);
                        existingSession.LogoutForced();
                        HandleAuthorizationLeaseDenied(existingSession);
                        return false;
                    }

                    if (!existingSession.EvaluateAuthorizationLease().Allowed)
                    {
                        HandleAuthorizationLeaseDenied(existingSession);
                    }
                }
                return false;
            }

            var session = OperatorSessionHolder.Current;
            _startupCoordinator?.SetAuthenticatedAccessMode(loginDlg.AccessMode);
            UpdateOperatorDisplay(session);
            _ = RefreshShellTitleAsync(factory);

            if (loginDlg.AccessMode == PosAuthenticatedAccessMode.LocalRecovery)
            {
                await EnterRecoveryModeAsync(factory).ConfigureAwait(true);
                RefreshShellAfterOperatorChange(session);
                return true;
            }

            if (IsRecoveryMode)
            {
                RefreshAuthorizationLeaseSchedule(session);
                RefreshShellAfterOperatorChange(session);
                return await ExitRecoveryModeAsync().ConfigureAwait(true);
            }

            RefreshAuthorizationLeaseSchedule(session);
            RefreshShellAfterOperatorChange(session);
            return true;
        }

        internal static async Task<bool> IsSessionBoundToCurrentTrustedIdentityAsync(
            SqliteConnectionFactory factory,
            IOperatorSession session)
        {
            if (factory == null || session?.CurrentUser == null)
            {
                return false;
            }

            try
            {
                var store = new PosTrustedDeviceStore();
                if (!store.TryRead(out var trustedSession) || trustedSession == null)
                {
                    return false;
                }

                var users = new UserRepository(factory);
                var trustedUsername = await users
                    .FindTrustedRemoteStaffUsernameAsync(
                        trustedSession.ShopId,
                        trustedSession.ShopCode,
                        trustedSession.StaffId,
                        trustedSession.StaffCode,
                        trustedSession.StaffCredentialVersion)
                    .ConfigureAwait(true);
                if (!string.Equals(
                        session.CurrentUser.Username,
                        trustedUsername,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var durableAccount = await users.GetByUsernameAsync(trustedUsername)
                    .ConfigureAwait(true);
                return HasSameDurableAuthority(session.CurrentUser, durableAccount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "category=operator_change result=blocked reason=trusted_identity_check_failed",
                    ex);
                return false;
            }
        }

        private static bool HasSameDurableAuthority(
            UserAccount cached,
            UserAccount durable)
        {
            if (cached == null || durable == null)
            {
                return false;
            }

            var cachedPermissions = (cached.PermissionCodes ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal);
            var durablePermissions = (durable.PermissionCodes ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal);

            return cached.Id == durable.Id &&
                string.Equals(cached.Username, durable.Username, StringComparison.Ordinal) &&
                string.Equals(cached.DisplayName, durable.DisplayName, StringComparison.Ordinal) &&
                cached.RoleId == durable.RoleId &&
                string.Equals(cached.RoleCode, durable.RoleCode, StringComparison.Ordinal) &&
                string.Equals(cached.RoleName, durable.RoleName, StringComparison.Ordinal) &&
                cached.IsActive == durable.IsActive &&
                cached.RequirePinChange == durable.RequirePinChange &&
                cached.MaxDiscountPercent == durable.MaxDiscountPercent &&
                cached.CanOverride == durable.CanOverride &&
                cachedPermissions.SequenceEqual(durablePermissions, StringComparer.Ordinal);
        }

        private async Task<bool> TrySwitchForPermissionAsync(
            string permissionCode,
            string message,
            string actionName,
            Window dialogOwner = null)
        {
            if (IsRecoveryMode && !LocalRecoveryPermissionPolicy.IsAllowed(permissionCode))
            {
                LogPermissionDenied(permissionCode, actionName, "recovery_scope");
                return false;
            }

            var owner = DialogOwnerHelper.GetSafeOwner(dialogOwner ?? this);
            var missingPermission = PermissionDiagnosticName(permissionCode);
            var deniedMessage = BuildPermissionDeniedMessage(message, GetCurrentRoleDiagnostic(), missingPermission);
            LogPermissionDenied(permissionCode, actionName, "initial");

            if (!PermissionDeniedDialog.ShowSwitchPrompt(owner, deniedMessage))
            {
                return false;
            }

            if (!await ShowOperatorSwitchOrPosAccessAsync(
                    permissionCode,
                    missingPermission,
                    owner).ConfigureAwait(true))
            {
                return false;
            }

            if (HasCurrentPermission(permissionCode))
            {
                return true;
            }

            LogPermissionDenied(permissionCode, actionName, "after_switch");
            ModernMessageDialog.Show(
                owner,
                PosLocalization.Current.Text("common.userPermissionDenied"),
                BuildPermissionDeniedMessage(message, GetCurrentRoleDiagnostic(), missingPermission));
            return false;
        }

        private bool HasCurrentPermission(string permissionCode)
        {
            var session = OperatorSessionHolder.Current;
            if (session == null || !session.IsLoggedIn)
            {
                return false;
            }

            var user = session.CurrentUser;
            if (user == null || string.IsNullOrWhiteSpace(permissionCode))
            {
                return false;
            }

            if (IsRecoveryMode)
            {
                if (!HasLeaseFreeLocalRecoveryAccess() &&
                    !session.EnsureAuthorizationValid())
                {
                    return false;
                }

                return LocalRecoveryPermissionPolicy.IsGranted(user, permissionCode);
            }

            if (!session.EnsureAuthorizationValid())
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
            var posVm = IsRecoveryMode ? null : GetPosViewModel();
            if (!IsRecoveryMode)
            {
                posVm?.RaiseCanExecuteChanged();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }

            var currentUsername = session?.CurrentUser?.Username ?? "";
            if (UserManagementViewControl?.DataContext is UserManagementViewModel existingVm &&
                !string.Equals(existingVm.CurrentOperatorUsername, currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                UserManagementViewControl.DataContext = null;
            }

            if (session?.CurrentUser == null)
            {
                ClearProductsViewModel();
                UserManagementViewControl.DataContext = null;
                DailyReportViewControl.DataContext = null;
                MainTabControl.SelectedIndex = 0;
                CurrentMenuKey = IsRecoveryMode ? "Settings" : "Pos";
                return;
            }
            var hasUsersManage = !IsRecoveryMode &&
                (session.CurrentUser.IsAdmin ||
                 session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.UsersManage) == true);
            var hasDailyCloseView = !IsRecoveryMode &&
                (session.CurrentUser.IsAdmin ||
                 session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.DailyCloseView) == true);
            var hasCatalogView = IsRecoveryMode
                ? HasCurrentPermission(PermissionCodes.CatalogView)
                : session.CurrentUser.IsAdmin ||
                  session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.CatalogView) == true;

            var productsAuthorizationChanged =
                !ReferenceEquals(_productsDataContextSession, session) ||
                !string.Equals(
                    _productsDataContextOperatorUsername ?? "",
                    currentUsername,
                    StringComparison.OrdinalIgnoreCase) ||
                _productsDataContextLeaseFreeLocalRecovery != HasLeaseFreeLocalRecoveryAccess();
            if (!hasCatalogView)
            {
                ClearProductsViewModel();
            }
            else if (productsAuthorizationChanged)
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
                CurrentMenuKey = IsRecoveryMode ? "Settings" : "Pos";
            }

            if (IsRecoveryMode)
            {
                if (hasCatalogView && ProductsViewControl?.DataContext == null)
                {
                    var products = EnsureProductsViewModel(session);
                    _ = LoadRecoveryProductsAsync(products);
                }
                ClampRecoveryTabSelection();
            }
        }

        private async Task LoadRecoveryProductsAsync(ProductsViewModel products)
        {
            try
            {
                await products.LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("category=pos.recovery products=load_failed", ex);
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
            if (IsRecoveryMode && !IsRecoveryTab(MainTabControl?.SelectedItem))
            {
                ClampRecoveryTabSelection();
                return;
            }

            UpdateCurrentMenuKeyFromTab();
            UpdateShellForCurrentView();
        }

        private bool IsRecoveryTab(object selectedItem)
        {
            return selectedItem == ProductsTab ||
                (MainTabControl != null &&
                 MainTabControl.Items.Count > 0 &&
                 selectedItem == MainTabControl.Items[0]);
        }

        private void ClampRecoveryTabSelection()
        {
            if (!IsRecoveryMode || _recoveryTabClampActive || MainTabControl == null)
            {
                return;
            }

            _recoveryTabClampActive = true;
            try
            {
                if (HasCurrentPermission(PermissionCodes.CatalogView))
                {
                    MainTabControl.SelectedItem = ProductsTab;
                    CurrentMenuKey = "Prodotti";
                }
                else
                {
                    MainTabControl.SelectedIndex = 0;
                    CurrentMenuKey = "Settings";
                }

                UpdateShellForCurrentView();
            }
            finally
            {
                _recoveryTabClampActive = false;
            }
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
            if (IsRecoveryMode)
            {
                ClampRecoveryTabSelection();
                SetSideMenuOpen(false);
                return;
            }

            CurrentMenuKey = "Pos";
            MainTabControl.SelectedIndex = 0;
            SetSideMenuOpen(false);
        }

        private void OnMenuOpenCashDrawerClick(object sender, RoutedEventArgs e)
        {
            if (IsRecoveryMode) return;
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
            var leaseFreeLocalRecovery = HasLeaseFreeLocalRecoveryAccess();
            if (ProductsViewControl?.DataContext is ProductsViewModel existing &&
                ReferenceEquals(_productsDataContextSession, session) &&
                string.Equals(_productsDataContextOperatorUsername ?? "", currentUsername, StringComparison.OrdinalIgnoreCase) &&
                _productsDataContextRecoveryMode == IsRecoveryMode &&
                _productsDataContextLeaseFreeLocalRecovery == leaseFreeLocalRecovery)
            {
                return existing;
            }

            IPermissionService permissionService = null;
            if (IsRecoveryMode)
            {
                permissionService = leaseFreeLocalRecovery
                    ? (IPermissionService)new LocalRecoveryPermissionService(session)
                    : new PermissionService(session);
            }
            var vm = new ProductsViewModel(permissionService);
            ProductsViewControl.DataContext = vm;
            _productsDataContextOperatorUsername = currentUsername;
            _productsDataContextRecoveryMode = IsRecoveryMode;
            _productsDataContextLeaseFreeLocalRecovery = leaseFreeLocalRecovery;
            _productsDataContextSession = session;
            return vm;
        }

        private void ClearProductsViewModel()
        {
            if (ProductsViewControl != null)
                ProductsViewControl.DataContext = null;
            _productsDataContextOperatorUsername = null;
            _productsDataContextRecoveryMode = false;
            _productsDataContextLeaseFreeLocalRecovery = false;
            _productsDataContextSession = null;
        }

        private PosViewModel GetPosViewModel()
        {
            return PosViewControl?.DataContext as PosViewModel;
        }

        private PosView EnsurePosViewCreated()
        {
            if (PosViewControl != null)
            {
                if (!PosTabHost.Children.Contains(PosViewControl))
                {
                    PosTabHost.Children.Insert(0, PosViewControl);
                }
                _customerDisplayManager?.Attach(GetPosViewModel());
                return PosViewControl;
            }

            PosViewControl = new PosView();
            PosTabHost.Children.Insert(0, PosViewControl);
            _customerDisplayManager?.Attach(GetPosViewModel());
            return PosViewControl;
        }

        private void SuspendPosViewForRecovery()
        {
            _customerDisplayManager?.Attach(null);
            if (PosViewControl == null)
            {
                return;
            }

            PosTabHost.Children.Remove(PosViewControl);
        }

        private async Task EnsureCustomerDisplayManagerAsync(SqliteConnectionFactory factory)
        {
            if (_customerDisplayManager == null)
            {
                var manager = new CustomerDisplayManager(
                    new WindowsDisplayTopologyProvider(),
                    new CustomerDisplaySettingsRepository(factory),
                    Dispatcher);
                manager.WarningRaised += code =>
                    GetPosViewModel()?.SetStatus(
                        PosLocalization.Current.Text("customerDisplay.error." + (code ?? "actionFailed")),
                        PosNoticeSeverity.Warning);
                try
                {
                    await manager.InitializeAsync().ConfigureAwait(true);
                    _customerDisplayManager = manager;
                }
                catch (Exception ex)
                {
                    try { manager.Dispose(); } catch { }
                    _logger.LogWarning(
                        "category=customer_display initialization=failed mode=best_effort",
                        ex);
                    GetPosViewModel()?.SetStatus(
                        PosLocalization.Current.Text("customerDisplay.error.actionFailed"),
                        PosNoticeSeverity.Warning);
                    return;
                }
            }
            var posViewModel = GetPosViewModel();
            posViewModel?.SetCustomerDisplayShopName(ShellTitle);
            _customerDisplayManager.Attach(posViewModel);
        }

        private async void OnMenuDailyReportClick(object sender, RoutedEventArgs e)
        {
            if (IsRecoveryMode)
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

        private void OnSyncStatusPillClick(object sender, RoutedEventArgs e)
        {
            if (App.IsSafeStart ||
                CurrentAccessMode == PosAuthenticatedAccessMode.LocalRecovery) return;
            ShowSyncCenterDialog();
        }

        private void ShowSettingsHubDialog()
        {
            try
            {
                var dialog = new SettingsHubDialog(
                    IsRecoveryMode,
                    App.IsSafeStart ||
                    CurrentAccessMode == PosAuthenticatedAccessMode.LocalRecovery)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };

                dialog.ShopDataRequested += (_, __) => OnMenuShopSettingsClick(this, new RoutedEventArgs());
                dialog.PrinterSettingsRequested += (_, __) => OnMenuPrinterClick(this, new RoutedEventArgs());
                dialog.DatabaseMaintenanceRequested += (_, __) => OnMenuDbClick(this, new RoutedEventArgs());
                dialog.UsersRolesRequested += (_, __) => OnMenuUsersClick(this, new RoutedEventArgs());
                dialog.AboutRequested += (_, __) => OnMenuAboutClick(this, new RoutedEventArgs());
                dialog.OnlineAccessRequested += (_, __) => OnRetryRecoveryOnlineClick(this, new RoutedEventArgs());
                dialog.SyncCenterRequested += (_, __) => ShowSyncCenterDialog();
                dialog.CustomerDisplayRequested += async (_, __) => await ShowCustomerDisplaySettingsAsync().ConfigureAwait(true);
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

        private async Task ShowCustomerDisplaySettingsAsync()
        {
            if (IsRecoveryMode) return;
            if (!HasCurrentPermission(PermissionCodes.SettingsPrinter) &&
                !await TrySwitchForPermissionAsync(
                    PermissionCodes.SettingsPrinter,
                    PosLocalization.Current.Text("customerDisplay.settings.permissionDenied"),
                    "CustomerDisplaySettings").ConfigureAwait(true))
            {
                PosViewControl?.RestoreScannerFocus();
                return;
            }

            var factory = OnlineSchedulerFactory ?? _languageSettingsFactory;
            if (factory == null)
            {
                return;
            }
            await EnsureCustomerDisplayManagerAsync(factory).ConfigureAwait(true);
            if (_customerDisplayManager == null)
            {
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(this),
                    PosLocalization.Current.Text("customerDisplay.settings.title"),
                    PosLocalization.Current.Text("customerDisplay.error.actionFailed"));
                PosViewControl?.RestoreScannerFocus();
                return;
            }
            var topology = new WindowsDisplayTopologyProvider();
            var vm = new CustomerDisplaySettingsViewModel(
                _customerDisplayManager.Settings,
                topology.GetMonitors());
            var dialog = new CustomerDisplaySettingsDialog(
                vm,
                () => _customerDisplayManager.IdentifyMonitors(),
                () => _customerDisplayManager.Preview(),
                () => _customerDisplayManager.OpenDisplay(),
                () => _customerDisplayManager.CloseDisplay())
            {
                Owner = DialogOwnerHelper.GetSafeOwner(this)
            };
            WindowSizingHelper.CapMaxHeightToOwner(dialog);

            if (dialog.ShowDialog() == true && dialog.Result != null)
            {
                try
                {
                    await _customerDisplayManager.SaveAndApplyAsync(dialog.Result).ConfigureAwait(true);
                    GetPosViewModel()?.SetStatus(
                        PosLocalization.Current.Text("customerDisplay.settings.saved"),
                        PosNoticeSeverity.Success);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("category=customer_display settings=apply_failed", ex);
                    ModernMessageDialog.Show(
                        DialogOwnerHelper.GetSafeOwner(this),
                        PosLocalization.Current.Text("customerDisplay.settings.title"),
                        PosLocalization.Current.Text("customerDisplay.error.actionFailed"));
                }
            }
            PosViewControl?.RestoreScannerFocus();
        }

        private void ShowSyncCenterDialog(Window owner = null)
        {
            if (App.IsSafeStart ||
                CurrentAccessMode == PosAuthenticatedAccessMode.LocalRecovery)
            {
                _logger.LogInfo("category=sync.center result=blocked reason=safe_or_recovery_mode");
                return;
            }

            var factory = OnlineSchedulerFactory ?? _languageSettingsFactory;
            if (factory == null)
            {
                return;
            }
            try
            {
                var dialog = new SyncCenterDialog(
                    factory,
                    (trigger, administratorRepairAuthorized, token) =>
                        TriggerAdaptiveOnlineRefreshAsync(
                            factory,
                            trigger,
                            token,
                            administratorRepairAuthorized,
                            allowFullDecision: administratorRepairAuthorized),
                    AuthorizeFullCatalogRepairAsync)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner(owner ?? this)
                };
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow.ShowSyncCenterDialog: errore apertura Sync Center");
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(this),
                    PosLocalization.T("sync.center.title"),
                    PosLocalization.T("settings.openLogError"));
            }
            finally
            {
                PosViewControl?.RestoreScannerFocus();
                _ = RefreshSyncStatusStripAsync(factory);
            }
        }

        private async Task<bool> AuthorizeFullCatalogRepairAsync(Window dialogOwner)
        {
            if (App.IsSafeStart ||
                CurrentAccessMode == PosAuthenticatedAccessMode.LocalRecovery) return false;

            if (HasCurrentPermission(PermissionCodes.DbMaintenance))
            {
                return true;
            }

            return await TrySwitchForPermissionAsync(
                    PermissionCodes.DbMaintenance,
                    PosLocalization.T("sync.center.repairPermissionMessage"),
                    "CatalogFullRepair",
                    dialogOwner)
                .ConfigureAwait(true);
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

            if (IsRecoveryMode)
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
                () => Task.FromResult(HasCurrentPermission(PermissionCodes.DbRestore)),
                () => HasCurrentPermission(PermissionCodes.DbBackup),
                () => HasCurrentPermission(PermissionCodes.CatalogImport),
                () => HasCurrentPermission(PermissionCodes.DbMaintenance));
            var dialog = new DbMaintenanceDialog(vm)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(this)
            };
            dialog.ShowDialog();
        }

        private void OnMenuPrinterClick(object sender, RoutedEventArgs e)
        {
            if (IsRecoveryMode) return;
            CurrentMenuKey = "Printer";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.PrinterSettingsCommand?.Execute(null);
            SetSideMenuOpen(false);
        }

        private void OnMenuShopSettingsClick(object sender, RoutedEventArgs e)
        {
            if (IsRecoveryMode) return;
            CurrentMenuKey = "ShopSettings";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenShopSettingsCommand?.Execute(null);
            SetSideMenuOpen(false);
        }

        private void OnMenuAboutClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "About";
            MainTabControl.SelectedIndex = 0;
            if (IsRecoveryMode)
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
            if (IsRecoveryMode)
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
            if (IsRecoveryMode || vm == null || _activePaymentCleanup != null)
            {
                return Task.FromResult(false);
            }

            var tcs = new TaskCompletionSource<bool>();
            var prevIndex = MainTabControl.SelectedIndex;
            var cleaned = false;

            void Cleanup(bool ok)
            {
                if (cleaned)
                {
                    return;
                }

                cleaned = true;
                vm.RequestClose -= OnClose;
                PaymentViewControl.DataContext = null;
                _activePaymentCleanup = null;
                if (IsRecoveryMode)
                {
                    ClampRecoveryTabSelection();
                }
                else
                {
                    MainTabControl.SelectedIndex = prevIndex;
                }
                UpdateShellForCurrentView();
                HamburgerButton.IsEnabled = true;
                tcs.TrySetResult(ok);
            }

            void OnClose(bool ok)
            {
                Dispatcher.BeginInvoke(new Action(() => Cleanup(ok)));
            }

            _activePaymentCleanup = Cleanup;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsRecoveryMode)
                {
                    Cleanup(false);
                    return;
                }

                HamburgerButton.IsEnabled = false;
                SetSideMenuOpen(false);

                PaymentViewControl.DataContext = vm;
                vm.RequestClose += OnClose;

                MainTabControl.SelectedIndex = 3; // 0 POS, 1 Prodotti, 2 Chiusura cassa, 3 Pagamento
                UpdateShellForCurrentView();
            }));

            return tcs.Task;
        }

        private void CancelActivePaymentForRecovery()
        {
            _activePaymentCleanup?.Invoke(false);
        }

    }
}
