using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class OperatorSwitchDialog : DialogShellWindow
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly IOperatorSession _session;
        private readonly FileLogger _logger = new FileLogger("OperatorSwitchDialog");
        private readonly string _attemptId;
        private string _shopCode = string.Empty;
        private bool _finished;

        public OperatorSwitchDialog(SqliteConnectionFactory factory, IOperatorSession session)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _session = session;
            _attemptId = CreateAttemptId();
        }

        public bool PosAccessRequested { get; private set; }
        public bool SwitchSucceeded { get; private set; }

        public async Task InitializeAsync(string requiredPermissionCode = null, string requiredPermissionName = null)
        {
            Log("start", "result=shown");
            var userRepo = new UserRepository(_factory);
            var settings = new SettingsRepository(_factory);
            _shopCode = await settings.GetLastPosLoginShopCodeAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(_shopCode))
            {
                _shopCode = await userRepo.GetLastRemoteShopCodeAsync().ConfigureAwait(true);
            }

            var current = _session?.CurrentUser;
            if (current != null)
            {
                CurrentOperatorText.Text = PosLocalization.F(
                    "operator.switch.currentOperator",
                    string.IsNullOrWhiteSpace(current.DisplayName) ? current.Username : current.DisplayName,
                    current.RoleName ?? string.Empty);
            }
            else
            {
                CurrentOperatorText.Text = PosLocalization.T("operator.switch.noCurrentOperator");
            }

            if (!string.IsNullOrWhiteSpace(requiredPermissionCode))
            {
                var missingPermission = string.IsNullOrWhiteSpace(requiredPermissionName)
                    ? requiredPermissionCode
                    : requiredPermissionName;
                ShowStatus(PosLocalization.F("operator.switch.permissionHint", missingPermission));
            }
            else
            {
                HideStatus();
            }

            SwitchButton.IsEnabled = true;
            StaffCodeBox.Focus();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (!_finished)
            {
                Finish("cancelled", "dialog_closed");
            }

            base.OnClosed(e);
        }

        private async void OnSwitchClick(object sender, RoutedEventArgs e)
        {
            var staffCode = (StaffCodeBox.Text ?? string.Empty).Trim();
            var pin = PinBox.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(staffCode))
            {
                ShowStatus(PosLocalization.T("operator.switch.staffCodeRequired"));
                StaffCodeBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(pin))
            {
                ShowStatus(PosLocalization.T("operator.switch.pinRequired"));
                PinBox.Focus();
                return;
            }

            if (_session == null)
            {
                ShowStatus(PosLocalization.T("operator.login.sessionMissing"));
                Finish("failed", "session_missing");
                return;
            }

            try
            {
                SwitchButton.IsEnabled = false;
                var username = await ResolveUsernameAsync(staffCode).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(username))
                {
                    pin = string.Empty;
                    PinBox.Clear();
                    ShowStatus(PosLocalization.T("operator.switch.notAvailableOffline"));
                    Log("login_result", "result=failed reason=not_available_offline");
                    StaffCodeBox.Focus();
                    return;
                }

                Log("login_start", "username=" + Safe(username));
                var result = await _session.LoginAsync(username, pin).ConfigureAwait(true);
                pin = string.Empty;
                PinBox.Clear();

                if (result == LoginResult.Success)
                {
                    SwitchSucceeded = true;
                    Finish("success", "username=" + Safe(username));
                    DialogResult = true;
                    Close();
                    return;
                }

                var reason = result == LoginResult.LockedOut ? "locked" : "invalid";
                ShowStatus(result == LoginResult.LockedOut
                    ? PosLocalization.T("operator.login.locked")
                    : PosLocalization.T("operator.login.invalid"));
                Log("login_result", "result=failed reason=" + reason + " username=" + Safe(username));
                PinBox.Focus();
            }
            catch (Exception ex)
            {
                ShowStatus(PosLocalization.T("operator.switch.failed"));
                _logger.LogWarning(BuildLine("exception", "result=failed exceptionType=" + Safe(ex.GetType().Name)), ex);
                Log("login_result", "result=failed reason=exception");
            }
            finally
            {
                SwitchButton.IsEnabled = true;
            }
        }

        private async Task<string> ResolveUsernameAsync(string staffCode)
        {
            var normalized = (staffCode ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            var userRepo = new UserRepository(_factory);
            if (!string.IsNullOrWhiteSpace(_shopCode))
            {
                var remoteUsername = await userRepo
                    .FindRemoteStaffUsernameAsync(_shopCode, normalized)
                    .ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(remoteUsername))
                {
                    return remoteUsername;
                }
            }

            var localUser = await userRepo.GetByUsernameAsync(normalized).ConfigureAwait(true);
            return localUser != null && localUser.IsActive ? localUser.Username : string.Empty;
        }

        private void OnPosAccessClick(object sender, RoutedEventArgs e)
        {
            PosAccessRequested = true;
            Finish("cancelled", "pos_access_requested");
            DialogResult = false;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Finish("cancelled", "user_cancelled");
            DialogResult = false;
            Close();
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message ?? string.Empty;
            StatusBanner.Visibility = Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusText.Text = string.Empty;
            StatusBanner.Visibility = Visibility.Collapsed;
        }

        private void Finish(string result, string details)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            Log("end", "result=" + Safe(result) + (string.IsNullOrWhiteSpace(details) ? string.Empty : " " + details));
        }

        private void Log(string stage, string details)
        {
            _logger.LogInfo(BuildLine(stage, details));
        }

        private string BuildLine(string stage, string details)
        {
            return "category=operator.switch attemptId=" + Safe(_attemptId) +
                " stage=" + Safe(stage) +
                (string.IsNullOrWhiteSpace(details) ? string.Empty : " " + details.Trim());
        }

        private static string CreateAttemptId()
        {
            return DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
                "-" +
                Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var chars = value.Trim()
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.' || ch == ':')
                .Take(96)
                .ToArray();
            return new string(chars);
        }
    }
}
