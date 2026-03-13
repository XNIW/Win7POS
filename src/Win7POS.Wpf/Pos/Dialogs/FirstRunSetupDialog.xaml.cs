using System;
using System.Linq;
using System.Threading.Tasks;
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

        /// <summary>Usa le stesse options di MainWindow per garantire lo stesso DB (evita wizard scrive DB A, MainWindow legge DB B).</summary>
        public FirstRunSetupDialog(PosDbOptions options)
        {
            InitializeComponent();
            var opts = options ?? throw new ArgumentNullException(nameof(options));
            _factory = new SqliteConnectionFactory(opts);
            _userRepo = new UserRepository(_factory);
            _roleRepo = new RoleRepository(_factory);
            _securityRepo = new SecurityRepository(_factory);
        }

        private async void OnCreateAdminClick(object sender, RoutedEventArgs e)
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

            var adminRole = await _roleRepo.GetByCodeAsync("admin").ConfigureAwait(true);
            if (adminRole == null)
            {
                ShowError("Ruolo admin non trovato nel database. Verificare DbInitializer.");
                return;
            }

            var existing = await _userRepo.GetByUsernameAsync(username).ConfigureAwait(true);
            if (existing != null)
            {
                ShowError("Username già in uso.");
                return;
            }

            try
            {
                var salt = PinHelper.GenerateSalt();
                var hash = PinHelper.HashPin(pin, salt);

                await _userRepo.CreateAsync(
                    username,
                    displayName,
                    hash,
                    salt,
                    adminRole.Id,
                    0,
                    requirePinChange: false
                ).ConfigureAwait(true);

                await _securityRepo.LogEventAsync(
                    SecurityEventCodes.UserCreated,
                    "username=" + username + ", source=first_run"
                ).ConfigureAwait(true);

                await _securityRepo.LogEventAsync(
                    SecurityEventCodes.FirstRunAdminCreated,
                    "username=" + username
                ).ConfigureAwait(true);

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
