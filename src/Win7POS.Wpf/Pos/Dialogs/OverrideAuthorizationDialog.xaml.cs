using System;
using System.Collections.Generic;
using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class OverrideAuthorizationDialog : Window
    {
        private readonly Func<string, string, (bool ok, int? userId)> _verify;

        public int? AuthorizerUserId { get; private set; }

        /// <summary>Crea il dialog con la lista filtrata di utenti autorizzabili. Verifica usa sempre username.</summary>
        public OverrideAuthorizationDialog(string operationText, IReadOnlyList<OverrideOperatorItem> authorizableUsers, Func<string, string, (bool ok, int? userId)> verify)
        {
            InitializeComponent();
            _verify = verify ?? ((_, __) => (false, null));
            MessageText.Text = string.IsNullOrEmpty(operationText)
                ? "Operazione riservata a Supervisore o superiore. Inserire credenziali per autorizzare."
                : "Operazione riservata a Supervisore o superiore: " + operationText + ". Inserire credenziali per autorizzare.";

            OperatorCombo.ItemsSource = authorizableUsers ?? new List<OverrideOperatorItem>();
            if (OperatorCombo.Items.Count > 0)
                OperatorCombo.SelectedIndex = 0;
        }

        private void OnAuthorizeClick(object sender, RoutedEventArgs e)
        {
            var selected = OperatorCombo.SelectedItem as OverrideOperatorItem;
            var username = selected?.Username ?? "";
            var pin = PinBox?.Password ?? "";

            if (string.IsNullOrEmpty(username))
            {
                ShowError("Seleziona un operatore dalla lista.");
                return;
            }

            var (ok, userId) = _verify(username, pin);
            if (ok)
            {
                AuthorizerUserId = userId;
                ErrorText.Visibility = Visibility.Collapsed;
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError("Credenziali non valide o operatore senza permesso per questa operazione.");
                PinBox.Clear();
                PinBox.Focus();
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Elemento per combo autorizzazione: Nome (@username) - Ruolo</summary>
    public sealed class OverrideOperatorItem
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string RoleName { get; set; }
        public string DisplayText => string.IsNullOrWhiteSpace(DisplayName) ? "@" + Username + " - " + RoleName : DisplayName + " (@" + Username + ") - " + RoleName;
    }
}
