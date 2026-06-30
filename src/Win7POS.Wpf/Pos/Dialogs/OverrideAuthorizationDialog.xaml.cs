using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class OverrideAuthorizationDialog : DialogShellWindow
    {
        private readonly Func<string, string, Task<(bool ok, int? userId)>> _verifyAsync;

        public int? AuthorizerUserId { get; private set; }

        /// <summary>Crea il dialog con la lista filtrata di utenti autorizzabili. Verifica usa sempre username.</summary>
        /// <param name="isAdminOnly">Se true, mostra messaggio "Operazione riservata ad amministratore".</param>
        public OverrideAuthorizationDialog(string operationText, IReadOnlyList<OverrideOperatorItem> authorizableUsers, Func<string, string, Task<(bool ok, int? userId)>> verifyAsync, bool isAdminOnly = false)
        {
            InitializeComponent();
            _verifyAsync = verifyAsync ?? ((_, __) => Task.FromResult((false, (int?)null)));
            if (isAdminOnly)
                MessageText.Text = string.IsNullOrEmpty(operationText)
                    ? PosLocalization.T("override.adminOnlyMessage")
                    : PosLocalization.F("override.adminOnlyMessageWithOperation", operationText);
            else
                MessageText.Text = string.IsNullOrEmpty(operationText)
                    ? PosLocalization.T("override.supervisorMessage")
                    : PosLocalization.F("override.supervisorMessageWithOperation", operationText);

            OperatorCombo.ItemsSource = authorizableUsers ?? new List<OverrideOperatorItem>();
            if (OperatorCombo.Items.Count > 0)
                OperatorCombo.SelectedIndex = 0;
        }

        private async void OnAuthorizeClick(object sender, RoutedEventArgs e)
        {
            var selected = OperatorCombo.SelectedItem as OverrideOperatorItem;
            var username = selected?.Username ?? "";
            var pin = PinBox?.Password ?? "";

            if (string.IsNullOrEmpty(username))
            {
                ShowError(PosLocalization.T("override.selectOperator"));
                return;
            }

            var (ok, userId) = await _verifyAsync(username, pin).ConfigureAwait(true);
            if (ok)
            {
                AuthorizerUserId = userId;
                ErrorText.Visibility = Visibility.Collapsed;
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError(PosLocalization.T("override.invalidCredentials"));
                PinBox.Clear();
                PinBox.Focus();
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

    /// <summary>Elemento per combo autorizzazione: Nome (@username) - Ruolo</summary>
    public sealed class OverrideOperatorItem
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string RoleName { get; set; }
        public string DisplayText => string.IsNullOrWhiteSpace(DisplayName) ? "@" + Username + " - " + RoleName : DisplayName + " (@" + Username + ") - " + RoleName;
    }
}
