using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class OperatorLoginDialog : DialogShellWindow
    {
        private readonly SqliteConnectionFactory _factory;
        private List<OperatorLoginItem> _operators;

        public OperatorLoginDialog(SqliteConnectionFactory factory)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Loaded += OnLoadedAsync;
        }

        /// <summary>Costruttore per chiamate che non hanno la factory (es. Cambia operatore). Usa il path dati di default.</summary>
        public OperatorLoginDialog() : this(new SqliteConnectionFactory(PosDbOptions.Default())) { }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            var userRepo = new UserRepository(_factory);
            var users = await userRepo.ListAsync().ConfigureAwait(true);

            var loginable = users
                .Where(x => x != null
                    && !string.IsNullOrWhiteSpace(x.Username)
                    && x.IsActive)
                .Select(u => new OperatorLoginItem(u.Username, u.DisplayName))
                .ToList();

            _operators = loginable;
            OperatorCombo.ItemsSource = _operators;

            if (_operators.Count == 0)
            {
                Win7POS.Wpf.Import.ModernMessageDialog.Show(this, "Win7POS",
                    "Non esistono operatori configurati. Verrà avviata la configurazione iniziale.");
                DialogResult = false;
                Close();
                return;
            }

            if (_operators.Count == 1)
                OperatorCombo.SelectedIndex = 0;
            OperatorCombo.Focus();
        }

        private async void OnLoginClick(object sender, RoutedEventArgs e)
        {
            var selected = OperatorCombo.SelectedItem as OperatorLoginItem;
            // Autenticazione sempre su username (identità univoca), mai solo nome
            var username = selected?.Username;

            var pin = PinBox?.Password ?? "";

            if (string.IsNullOrEmpty(username))
            {
                ShowError("Seleziona un operatore dalla lista.");
                return;
            }

            var session = OperatorSessionHolder.Current;
            if (session == null)
            {
                ShowError("Sessione non inizializzata.");
                return;
            }

            if (await session.LoginAsync(username, pin).ConfigureAwait(true))
            {
                if (session.CurrentUser != null && session.CurrentUser.RequirePinChange)
                {
                    session.LogSecurityEvent(SecurityEventCodes.RequirePinChangeEnforced, "userId=" + session.CurrentUser.Id);
                    var changePinDlg = new ChangePinDialog(session.CurrentUser.Id, session.CurrentUser.Username)
                    {
                        Owner = this
                    };
                    if (changePinDlg.ShowDialog() != true)
                    {
                        session.LogoutForced();
                        ShowError("È obbligatorio cambiare il PIN per accedere.");
                        PinBox.Clear();
                        PinBox.Focus();
                        return;
                    }
                }
                ErrorText.Visibility = Visibility.Collapsed;
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError("Operatore o PIN non validi.");
                PinBox.Clear();
                PinBox.Focus();
            }
        }

        private sealed class OperatorLoginItem
        {
            public string Username { get; }
            public string DisplayName { get; }
            /// <summary>Formato per combo: Nome operatore (@username)</summary>
            public string DisplayText => string.IsNullOrWhiteSpace(DisplayName) ? "@" + Username : DisplayName + " (@" + Username + ")";

            public OperatorLoginItem(string username, string displayName)
            {
                Username = username ?? "";
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
