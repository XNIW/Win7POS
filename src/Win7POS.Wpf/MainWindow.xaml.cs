using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Win7POS.Data;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Data.Repositories;
using Win7POS.Core;
using Win7POS.Core.Security;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
        private static readonly Infrastructure.FileLogger _logger = new Infrastructure.FileLogger("MainWindow");
        private static readonly TimeSpan StartupHeartbeatTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StartupSalesSyncTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan StartupCatalogPullTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StartupWatchdogTimeout = TimeSpan.FromSeconds(5);
        private const string LastCatalogErrorSettingKey = "pos.catalog.last_error";
        private const string LastSalesErrorSettingKey = "pos.sales_sync.last_error";
        private readonly TaskCompletionSource<bool> _contentRendered = new TaskCompletionSource<bool>();
        private DispatcherTimer _syncStatusTimer;
        private bool _backgroundOnlineRefreshQueued;
        private bool _operatorLoginReached;
        private string _startupPhase = "constructed";
        private string _productsDataContextOperatorUsername;
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

        public MainWindow()
        {
            Application.Current.MainWindow = this;
            StartupTrace.Write("MainWindow constructor start");
            _logger.LogInfo("MainWindow constructor entered");
            InitializeComponent();
            StartupTrace.Write("MainWindow constructor end");
            _logger.LogInfo("MainWindow constructor done");

            MainTabControl.SelectedIndex = 0;

            Loaded += OnLoadedAsync;
            ContentRendered += OnContentRendered;
        }

        /// <summary>True se non esiste almeno un utente loggabile (attivo, con username valido). Copre DB inesistente, users vuota, solo utenti disabilitati.</summary>
        private async Task<bool> RequiresFirstRunAsync(SqliteConnectionFactory factory)
        {
            var userRepo = new UserRepository(factory);
            var users = await userRepo.ListAsync().ConfigureAwait(true);

            var loginableUsers = users
                .Where(x => x != null
                    && !string.IsNullOrWhiteSpace(x.Username)
                    && x.IsActive)
                .ToList();

            return loginableUsers.Count == 0;
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
                if (App.IsSafeStart)
                {
                    ApplySafeStartStatus();
                }
                else
                {
                    await RefreshSyncStatusStripAsync(factory).ConfigureAwait(true);
                    StartSyncStatusTimer(factory);
                }

                _startupPhase = "FirstRun check start";
                StartupTrace.Write("first-run check start");
                _logger.LogInfo("FirstRun check start");
                var needFirstRun = await RequiresFirstRunAsync(factory).ConfigureAwait(true);
                StartupTrace.Write("first-run check end: required=" + needFirstRun.ToString());
                _logger.LogInfo("FirstRun check done: required=" + needFirstRun.ToString());
                await WaitForContentRenderedOrTimeoutAsync().ConfigureAwait(true);

                if (needFirstRun && !App.IsSafeStart)
                {
                    if (await TryOnlineBootstrapFirstRunAsync(factory).ConfigureAwait(true))
                    {
                        _logger.LogInfo("FirstRun online bootstrap completed; rechecking local operators");
                        needFirstRun = await RequiresFirstRunAsync(factory).ConfigureAwait(true);
                        _logger.LogInfo("FirstRun check done: required=" + needFirstRun.ToString());
                    }
                }

                if (needFirstRun && App.IsSafeStart)
                {
                    StartupTrace.Write("first-run online bootstrap skipped: safe-start");
                }

                if (needFirstRun)
                {
                    var wizard = new FirstRunSetupDialog(options) { Owner = this };
                    var ok = wizard.ShowDialog() == true;

                    needFirstRun = await RequiresFirstRunAsync(factory).ConfigureAwait(true);
                    if (!ok || needFirstRun)
                    {
                        ModernMessageDialog.Show(this, "Win7POS",
                            "Configurazione iniziale non completata.");
                        Close();
                        return;
                    }
                }

                var userRepo = new UserRepository(factory);
                if (OperatorSessionHolder.Current == null)
                {
                    var securityRepo = new SecurityRepository(factory);
                    var operatorSession = new OperatorSession(userRepo, securityRepo);
                    OperatorSessionHolder.Current = operatorSession;
                }

                _logger.LogInfo("OperatorLogin dialog opening");
                _startupPhase = "OperatorLogin opening";
                StartupTrace.Write("operator login dialog about to open");
                var login = new OperatorLoginDialog(factory) { Owner = this };
                login.ShowActivated = true;
                Activate();
                _operatorLoginReached = true;
                _startupPhase = "OperatorLogin shown";
                StartupTrace.Write("operator login dialog shown");
                if (login.ShowDialog() != true)
                {
                    _logger.LogInfo("OperatorLogin dialog cancelled");
                    Close();
                    return;
                }
                _logger.LogInfo("OperatorLogin dialog accepted");

                var session = OperatorSessionHolder.Current;
                if (session != null)
                {
                    UpdateOperatorDisplay(session);
                    RefreshShellAfterOperatorChange(session);
                    session.SessionChanged += () => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateOperatorDisplay(session);
                        RefreshShellAfterOperatorChange(session);
                    }));
                }

                if (App.IsSafeStart)
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
                _logger.LogError(ex, "MainWindow.OnLoadedAsync: avvio fallito (DB/FirstRun/Login)");
                try
                {
                    ModernMessageDialog.Show(this, "Win7POS",
                        "Errore in avvio. Controlla il log applicativo.");
                }
                catch { }
                Close();
            }
        }

        private async Task<bool> TryOnlineBootstrapFirstRunAsync(SqliteConnectionFactory factory)
        {
            var dialog = new PosOnlineFirstLoginDialog(factory)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            return !await RequiresFirstRunAsync(factory).ConfigureAwait(true);
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
                        _logger.LogInfo("BackgroundOnlineRefresh heartbeat done");
                        QueueSyncStatusRefresh(factory);
                        await TrySyncSalesOutboxAsync(options, factory).ConfigureAwait(false);
                        await TryPullCatalogAsync(options, factory).ConfigureAwait(false);
                        return;
                    }

                    if (result.Denied)
                    {
                        store.Clear();
                    }

                    _logger.LogWarning("BackgroundOnlineRefresh heartbeat skipped: " + SafeOnlineCode(result.Code));
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

        private void ApplySafeStartStatus()
        {
            if (SyncStatusText != null)
            {
                SyncStatusText.Text = "Sync: Safe start: online sync disabled for this launch";
                SyncStatusText.ToolTip = "Safe start: heartbeat, sales sync, catalog pull and trusted-session refresh are disabled for this launch.";
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
                    StartupTrace.Write("startup watchdog warning: operator login not reached within 5s; phase=" + _startupPhase + "; elapsed_ms=" + (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds.ToString("0"));
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
                StartupTrace.Write("ContentRendered wait timeout; opening operator login");
            }
        }

        private void UpdateOperatorDisplay(IOperatorSession session)
        {
            if (OperatorDisplayText != null)
                OperatorDisplayText.Text = session != null && session.IsLoggedIn ? session.CurrentDisplayName : "—";
            if (OperatorRoleText != null)
                OperatorRoleText.Text = session != null && session.IsLoggedIn ? "(" + session.CurrentRoleName + ")" : "";
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
                    SyncStatusText.Text = "Sync: " + status.SummaryText;
                    SyncStatusText.ToolTip = status.DeviceText + "\n" +
                        status.StaffText + "\n" +
                        status.LastOnlineText + "\n" +
                        status.PendingSalesText + "\n" +
                        status.CatalogErrorText + "\n" +
                        status.SalesErrorText;
                }

                if (SyncStatusPill != null)
                {
                    SyncStatusPill.Background = StatusBrush(status.ConnectivityText);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RefreshSyncStatusStripAsync non completato", ex);
                if (SyncStatusText != null)
                {
                    SyncStatusText.Text = "Sync: stato non disponibile";
                }
            }
        }

        private static Brush StatusBrush(string connectivityText)
        {
            if (string.Equals(connectivityText, "Online", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(42, 111, 72));
            }

            if (string.Equals(connectivityText, "Non collegato", StringComparison.OrdinalIgnoreCase))
            {
                return new SolidColorBrush(Color.FromRgb(109, 85, 132));
            }

            return new SolidColorBrush(Color.FromRgb(146, 88, 36));
        }

        private void OnChangeOperatorClick(object sender, RoutedEventArgs e)
        {
            var loginDlg = new OperatorLoginDialog { Owner = this };
            if (loginDlg.ShowDialog() != true || OperatorSessionHolder.Current == null)
                return;
            var session = OperatorSessionHolder.Current;
            UpdateOperatorDisplay(session);
            RefreshShellAfterOperatorChange(session);
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
                CurrentMenuKey = "Pos";
            }
        }

        private void OnMenuUsersClick(object sender, RoutedEventArgs e)
        {
            if (UserManagementViewControl == null || MainTabControl == null) return;
            var session = OperatorSessionHolder.Current;
            var hasUsersManage = session?.CurrentUser != null && (session.CurrentUser.IsAdmin || session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.UsersManage) == true);
            if (!hasUsersManage)
            {
                UserManagementViewControl.DataContext = null;
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Permesso negato", "Non hai il permesso di accedere a Utenti e ruoli.");
                SideMenuOverlay.Visibility = Visibility.Collapsed;
                return;
            }
            var currentUsername = session?.CurrentUser?.Username ?? "";
            if (UserManagementViewControl.DataContext is Pos.Dialogs.UserManagementViewModel existingVm &&
                string.Equals(existingVm.CurrentOperatorUsername, currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                CurrentMenuKey = "UsersRoles";
                MainTabControl.SelectedItem = UsersRolesTab;
                SideMenuOverlay.Visibility = Visibility.Collapsed;
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
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Permesso negato", "Non hai il permesso di accedere a Utenti e ruoli.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow.OnMenuUsersClick: errore apertura Utenti e ruoli");
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Utenti e ruoli", "Errore apertura Utenti e ruoli. Controlla il log applicativo.");
            }
            SideMenuOverlay.Visibility = Visibility.Collapsed;
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
            if (SideMenuOverlay.Visibility == System.Windows.Visibility.Visible)
            {
                SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                // Mantieni evidenziata la sezione attiva: il cassiere vede subito "dove sono"
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                var cmd = GetPosViewModel()?.OpenCashDrawerCommand;
                OpenCashDrawerMenuButton.IsEnabled = cmd?.CanExecute(null) ?? false;
                SideMenuOverlay.Visibility = System.Windows.Visibility.Visible;
            }
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
            CurrentMenuKey = "Pos";
            MainTabControl.SelectedIndex = 0;
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuOpenCashDrawerClick(object sender, RoutedEventArgs e)
        {
            GetPosViewModel()?.OpenCashDrawerCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private async void OnMenuProdottiClick(object sender, RoutedEventArgs e)
        {
            var session = OperatorSessionHolder.Current;
            var hasCatalogView = session?.CurrentUser != null &&
                (session.CurrentUser.IsAdmin || session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.CatalogView) == true);
            if (!hasCatalogView)
            {
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Permesso negato", "Non hai il permesso di accedere ai prodotti.");
                SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            try
            {
                var vm = EnsureProductsViewModel(session);
                await vm.LoadAsync().ConfigureAwait(true);
                CurrentMenuKey = "Prodotti";
                MainTabControl.SelectedItem = ProductsTab;
                SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MainWindow.OnMenuProdottiClick: errore apertura Prodotti");
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Prodotti", "Errore apertura Prodotti. Controlla il log applicativo.");
                SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
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

        private void OnMenuDailyReportClick(object sender, RoutedEventArgs e)
        {
            var posVm = GetPosViewModel();
            if (posVm != null && DailyReportViewControl != null)
            {
                DailyReportViewControl.DataContext = posVm.CreateDailyReportViewModel();
            }
            CurrentMenuKey = "DailyReport";
            MainTabControl.SelectedIndex = 2; // 0=POS, 1=Prodotti, 2=Chiusura cassa, 3=Pagamento
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
            CurrentMenuKey = "Printer";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.PrinterSettingsCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuShopSettingsClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "ShopSettings";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenShopSettingsCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuAboutClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "About";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.AboutSupportCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void OnMenuSalesRegisterClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "SalesRegister";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenSalesRegisterCommand?.Execute(null);
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
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
                SideMenuOverlay.Visibility = Visibility.Collapsed;

                PaymentViewControl.DataContext = vm;
                vm.RequestClose += OnClose;

                MainTabControl.SelectedIndex = 3; // 0 POS, 1 Prodotti, 2 Chiusura cassa, 3 Pagamento
                UpdateShellForCurrentView();
            }));

            return tcs.Task;
        }
    }
}
