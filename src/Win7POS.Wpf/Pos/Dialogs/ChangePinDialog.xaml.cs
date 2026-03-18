using System.Linq;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class ChangePinDialog : DialogShellWindow
    {
        private readonly int _userId;
        private readonly UserRepository _userRepo;
        private readonly SecurityRepository _securityRepo;

        public ChangePinDialog(int userId, string username)
        {
            InitializeComponent();
            _userId = userId;
            var options = PosDbOptions.Default();
            var factory = new SqliteConnectionFactory(options);
            _userRepo = new UserRepository(factory);
            _securityRepo = new SecurityRepository(factory);
            if (!string.IsNullOrEmpty(username))
                Title = "Cambio PIN – " + username;
            NewPinBox.Focus();
            Loaded += (s, e) => NewPinBox.Focus();
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var newPin = NewPinBox?.Password ?? "";
            var confirm = ConfirmPinBox?.Password ?? "";
            if (newPin.Length < 4 || newPin.Length > 6 || !newPin.All(char.IsDigit))
            {
                ShowError("Il PIN deve essere di 4-6 cifre numeriche.");
                return;
            }
            if (newPin != confirm)
            {
                ShowError("I PIN non coincidono.");
                ConfirmPinBox.Clear();
                return;
            }

            var salt = PinHelper.GenerateSalt();
            var hash = PinHelper.HashPin(newPin, salt);
            await _userRepo.UpdatePinAsync(_userId, hash, salt, requirePinChange: false).ConfigureAwait(true);
            await _securityRepo.LogEventAsync(SecurityEventCodes.PinChanged, "userId=" + _userId).ConfigureAwait(true);
            DialogResult = true;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
