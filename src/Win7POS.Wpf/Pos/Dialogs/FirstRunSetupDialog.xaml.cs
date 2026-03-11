using System;
using System.Linq;
using System.Windows;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class FirstRunSetupDialog : Window
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly UserRepository _userRepo;
        private readonly RoleRepository _roleRepo;
        private readonly SecurityRepository _securityRepo;

        public FirstRunSetupDialog(SqliteConnectionFactory factory)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _userRepo = new UserRepository(_factory);
            _roleRepo = new RoleRepository(_factory);
            _securityRepo = new SecurityRepository(_factory);
        }

        private void OnCreateAdminClick(object sender, RoutedEventArgs e)
        {
            var displayName = (DisplayNameBox?.Text ?? "").Trim();
            var username = (UsernameBox?.Text ?? "").Trim();
            var pin = PinBox?.Password ?? "";
            var confirm = ConfirmPinBox?.Password ?? "";

            ErrorText.Visibility = Visibility.Collapsed;
            ErrorText.Text = "";

            if (string.IsNullOrWhiteSpace(displayName))
            {
                ShowError("Inserisci il nome operatore.");
                return;
            }
            if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
            {
                ShowError("Username non valido (almeno 3 caratteri).");
                return;
            }
            if (pin.Length < 4 || pin.Length > 6 || !pin.All(char.IsDigit))
            {
                ShowError("Il PIN deve avere 4-6 cifre.");
                return;
            }
            if (pin != confirm)
            {
                ShowError("La conferma PIN non corrisponde.");
                return;
            }

            var adminRole = _roleRepo.GetByCodeAsync("admin").GetAwaiter().GetResult();
            if (adminRole == null)
            {
                ShowError("Ruolo admin non trovato nel database. Verificare DbInitializer.");
                return;
            }

            var existing = _userRepo.GetByUsernameAsync(username).GetAwaiter().GetResult();
            if (existing != null)
            {
                ShowError("Username già in uso.");
                return;
            }

            try
            {
                var salt = PinHelper.GenerateSalt();
                var hash = PinHelper.HashPin(pin, salt);

                _userRepo.CreateAsync(
                    username,
                    displayName,
                    hash,
                    salt,
                    adminRole.Id,
                    0,
                    requirePinChange: false
                ).GetAwaiter().GetResult();

                _securityRepo.LogEventAsync(
                    SecurityEventCodes.UserCreated,
                    "username=" + username + ", source=first_run"
                ).GetAwaiter().GetResult();

                _securityRepo.LogEventAsync(
                    SecurityEventCodes.FirstRunAdminCreated,
                    "username=" + username
                ).GetAwaiter().GetResult();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? "";
                if (msg.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("username", StringComparison.OrdinalIgnoreCase) >= 0)
                    msg = "Username già in uso. Scegline un altro.";
                ShowError(msg);
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message ?? "";
            ErrorText.Visibility = string.IsNullOrEmpty(ErrorText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
