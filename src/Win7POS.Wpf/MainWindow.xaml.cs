using System;
using System.Linq;
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

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
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
            InitializeComponent();

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
            UpdateMenuSelectionVisual();

            try
            {
                var options = PosDbOptions.Default();
                DbInitializer.EnsureCreated(options);

                var factory = new SqliteConnectionFactory(options);

                var needFirstRun = await RequiresFirstRunAsync(factory).ConfigureAwait(true);
                if (needFirstRun)
                {
                    var wizard = new FirstRunSetupDialog(options) { Owner = this };
                    var ok = wizard.ShowDialog() == true;

                    needFirstRun = await RequiresFirstRunAsync(factory).ConfigureAwait(true);
                    if (!ok || needFirstRun)
                    {
                        var dbPathInfo = " DB: " + options.DbPath;
                        ModernMessageDialog.Show(this, "Win7POS",
                            "Configurazione iniziale non completata." + dbPathInfo);
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

                var login = new OperatorLoginDialog(factory) { Owner = this };
                if (login.ShowDialog() != true)
                {
                    Close();
                    return;
                }

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
            }
            catch (Exception ex)
            {
                ModernMessageDialog.Show(this, "Win7POS",
                    "Errore in avvio.\nControlla i log in " + AppPaths.LogsDirectory + "\n\n" + ex.Message);
                Close();
            }
        }

        private void UpdateOperatorDisplay(IOperatorSession session)
        {
            if (OperatorDisplayText != null)
                OperatorDisplayText.Text = session != null && session.IsLoggedIn ? session.CurrentDisplayName : "—";
            if (OperatorRoleText != null)
                OperatorRoleText.Text = session != null && session.IsLoggedIn ? "(" + session.CurrentRoleName + ")" : "";
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

            if (session?.CurrentUser == null) return;
            var hasUsersManage = session.CurrentUser.IsAdmin || (session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.UsersManage) == true);
            var hasDailyCloseView = session.CurrentUser.IsAdmin || (session.CurrentUser.PermissionCodes?.Contains(PermissionCodes.DailyCloseView) == true);

            if (MainTabControl?.SelectedItem == UsersRolesTab && !hasUsersManage)
            {
                UserManagementViewControl.DataContext = null;
                MainTabControl.SelectedIndex = 0;
                CurrentMenuKey = "Pos";
            }
            else if (MainTabControl?.SelectedItem == DailyReportTab && !hasDailyCloseView)
            {
                DailyReportViewControl.DataContext = null;
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
            if (UserManagementViewControl.DataContext is Pos.Dialogs.UserManagementViewModel)
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
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Permesso negato", ex.Message);
            }
            catch (Exception ex)
            {
                ModernMessageDialog.Show(Application.Current?.MainWindow, "Utenti e ruoli", "Errore apertura Utenti e ruoli.\n\n" + ex.Message);
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
                CurrentMenuKey = ""; // reset evidenziazione: nessuna voce selezionata alla riapertura del menu
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

        private void OnMenuProdottiClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "Prodotti";
            MainTabControl.SelectedIndex = 1;
            SideMenuOverlay.Visibility = System.Windows.Visibility.Collapsed;
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
