using System;
using System.Windows;
using Win7POS.Core.Security;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class OverrideAuthorizationDialog : Window
    {
        private readonly string _requiredPermissionCode;
        private readonly Func<string, string, (bool ok, int? userId)> _verify;

        public int? AuthorizerUserId { get; private set; }

        public OverrideAuthorizationDialog(string operationText, string requiredPermissionCode, Func<string, string, (bool ok, int? userId)> verify)
        {
            InitializeComponent();
            _requiredPermissionCode = requiredPermissionCode ?? "";
            _verify = verify ?? ((_, __) => (false, null));
            MessageText.Text = string.IsNullOrEmpty(operationText)
                ? "Operazione riservata a Supervisore o superiore. Inserire credenziali per autorizzare."
                : "Operazione riservata a Supervisore o superiore: " + operationText + ". Inserire credenziali per autorizzare.";
        }

        private void OnAuthorizeClick(object sender, System.Windows.RoutedEventArgs e)
        {
            var username = (UsernameBox?.Text ?? "").Trim();
            var pin = PinBox?.Password ?? "";

            if (string.IsNullOrEmpty(username))
            {
                ShowError("Inserire operatore.");
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
}
