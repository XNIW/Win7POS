using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class FirstRunSetupDialog : DialogShellWindow
    {
        private static readonly FileLogger _logger = new FileLogger("FirstRunSetupDialog");
        private readonly SqliteConnectionFactory _factory;
        private readonly UserRepository _userRepo;
        private bool _creating;

        /// <summary>Usa le stesse options di MainWindow per garantire lo stesso DB (evita wizard scrive DB A, MainWindow legge DB B).</summary>
        public FirstRunSetupDialog(PosDbOptions options)
            : this(new SqliteConnectionFactory(options ?? throw new ArgumentNullException(nameof(options))))
        {
        }

        public FirstRunSetupDialog(SqliteConnectionFactory factory)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _userRepo = new UserRepository(_factory);
        }

        private async void OnCreateAdminClick(object sender, RoutedEventArgs e)
        {
            if (_creating)
            {
                return;
            }

            var displayName = (DisplayNameBox?.Text ?? "").Trim();
            var username = (UsernameBox?.Text ?? "").Trim();
            var pin = PinBox?.Password ?? "";
            var confirm = ConfirmPinBox?.Password ?? "";

            ErrorText.Visibility = Visibility.Collapsed;
            ErrorText.Text = "";

            if (string.IsNullOrWhiteSpace(displayName))
            {
                ShowError(PosLocalization.T("firstRun.operatorNameRequired"));
                return;
            }
            if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
            {
                ShowError(PosLocalization.T("firstRun.usernameInvalid"));
                return;
            }
            if (pin.Length < 4 || pin.Length > 6 || !pin.All(char.IsDigit))
            {
                ShowError(PosLocalization.T("pin.invalidDigits"));
                return;
            }
            if (pin != confirm)
            {
                ShowError(PosLocalization.T("operator.login.pinMismatch"));
                return;
            }

            try
            {
                _creating = true;
                SetFormEnabled(false);
                var salt = PinHelper.GenerateSalt();
                var hash = PinHelper.HashPin(pin, salt);

                var created = await _userRepo.TryCreateFirstRunAdminAsync(
                    username,
                    displayName,
                    hash,
                    salt).ConfigureAwait(true);
                if (!created.CreatedSuccessfully)
                {
                    ShowError(PosLocalization.T("firstRun.noLongerEligible"));
                    return;
                }

                var session = OperatorSessionHolder.Current;
                if (session == null || await session.LoginAsync(username, pin).ConfigureAwait(true) != LoginResult.Success)
                {
                    ShowError(PosLocalization.T("firstRun.sessionFailed"));
                    return;
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? "";
                if (msg.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("username", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ShowError(PosLocalization.T("firstRun.usernameInUseChooseAnother"));
                }
                else
                {
                    _logger.LogError(ex, "Local recovery administrator creation failed.");
                    ShowError(PosLocalization.T("firstRun.localSetupFailed"));
                }
            }
            finally
            {
                pin = string.Empty;
                confirm = string.Empty;
                PinBox.Clear();
                ConfirmPinBox.Clear();
                _creating = false;
                if (IsVisible)
                {
                    SetFormEnabled(true);
                }
            }
        }

        private void SetFormEnabled(bool enabled)
        {
            DisplayNameBox.IsEnabled = enabled;
            UsernameBox.IsEnabled = enabled;
            PinBox.IsEnabled = enabled;
            ConfirmPinBox.IsEnabled = enabled;
            CreateAdminButton.IsEnabled = enabled;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message ?? "";
            ErrorText.Visibility = string.IsNullOrEmpty(ErrorText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
