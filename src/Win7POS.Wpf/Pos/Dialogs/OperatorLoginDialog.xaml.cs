using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class OperatorLoginDialog : Window
    {
        private readonly SqliteConnectionFactory _factory;
        private List<OperatorLoginItem> _operators;

        public OperatorLoginDialog(SqliteConnectionFactory factory)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Loaded += OnLoaded;
        }

        /// <summary>Costruttore per chiamate che non hanno la factory (es. Cambia operatore). Usa il path dati di default.</summary>
        public OperatorLoginDialog() : this(new SqliteConnectionFactory(PosDbOptions.Default())) { }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var userRepo = new UserRepository(_factory);
            var users = userRepo.ListAsync().GetAwaiter().GetResult();
            _operators = users.Where(u => u.IsActive).Select(u => new OperatorLoginItem(u.Username, u.DisplayName)).ToList();
            OperatorCombo.ItemsSource = _operators;
            if (_operators.Count == 1)
                OperatorCombo.SelectedIndex = 0;
            OperatorCombo.Focus();
        }

        private void OnLoginClick(object sender, RoutedEventArgs e)
        {
            var typed = (OperatorCombo.Text ?? "").Trim();
            var selected = OperatorCombo.SelectedItem as OperatorLoginItem;

            string username = null;
            if (selected != null && string.Equals(selected.DisplayName, typed, StringComparison.OrdinalIgnoreCase))
                username = selected.Username;
            else if (!string.IsNullOrWhiteSpace(typed))
            {
                var byUsername = _operators?.FirstOrDefault(o => string.Equals(o.Username, typed, StringComparison.OrdinalIgnoreCase));
                if (byUsername != null)
                    username = byUsername.Username;
            }

            var pin = PinBox?.Password ?? "";

            if (string.IsNullOrEmpty(username))
            {
                ShowError(selected == null && _operators?.Count > 0
                    ? "Seleziona un operatore dalla lista."
                    : "Operatore non valido. Seleziona dalla lista o verifica l'username.");
                return;
            }

            var session = OperatorSessionHolder.Current;
            if (session == null)
            {
                ShowError("Sessione non inizializzata.");
                return;
            }

            if (session.Login(username, pin))
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
            public OperatorLoginItem(string username, string displayName)
            {
                Username = username ?? "";
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName;
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
