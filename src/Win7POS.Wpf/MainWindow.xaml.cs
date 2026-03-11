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
                    var wizard = new FirstRunSetupDialog(factory) { Owner = this };
                    var ok = wizard.ShowDialog() == true;

                    needFirstRun = await RequiresFirstRunAsync(factory).ConfigureAwait(true);
                    if (!ok || needFirstRun)
                    {
                        MessageBox.Show(this,
                            "Configurazione iniziale non completata.",
                            "Win7POS",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
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
                    session.SessionChanged += () => Dispatcher.BeginInvoke(new Action(() => UpdateOperatorDisplay(session)));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Errore in avvio.\nControlla i log in " + AppPaths.LogsDirectory + "\n\n" + ex.Message,
                    "Win7POS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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
            if (loginDlg.ShowDialog() == true && OperatorSessionHolder.Current != null)
                UpdateOperatorDisplay(OperatorSessionHolder.Current);
        }

        private void OnMenuUsersClick(object sender, RoutedEventArgs e)
        {
            CurrentMenuKey = "UsersRoles";
            MainTabControl.SelectedIndex = 0;
            GetPosViewModel()?.OpenUserManagementCommand?.Execute(null);
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
            var idx = MainTabControl.SelectedIndex;
            if (idx == 0) CurrentMenuKey = "Pos";
            else if (idx == 1) CurrentMenuKey = "Prodotti";
            else if (idx == 2) CurrentMenuKey = "DailyReport";
            // tab 3 = Pagamento: lasciamo la chiave invariata
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
