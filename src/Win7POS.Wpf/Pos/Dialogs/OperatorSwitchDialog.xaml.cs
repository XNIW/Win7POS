using System;
using System.Collections.Generic;
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
            var shopCode = await settings.GetLastPosLoginShopCodeAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(shopCode))
            {
                shopCode = await userRepo.GetLastRemoteShopCodeAsync().ConfigureAwait(true);
            }

            var users = await userRepo.ListActiveForOperatorSwitchAsync(shopCode).ConfigureAwait(true);
            var sourceUsers = users ?? Array.Empty<UserAccount>();
            HashSet<string> eligibleUsernames = null;
            if (!string.IsNullOrWhiteSpace(requiredPermissionCode))
            {
                var eligibleUsers = await userRepo.ListUsersWithPermissionAsync(requiredPermissionCode).ConfigureAwait(true);
                eligibleUsernames = new HashSet<string>(
                    (eligibleUsers ?? Array.Empty<UserAccount>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Username))
                    .Select(x => x.Username),
                    StringComparer.OrdinalIgnoreCase);
            }

            var activeUsers = sourceUsers
                .Where(x => x != null && x.IsActive && !string.IsNullOrWhiteSpace(x.Username))
                .OrderBy(x => eligibleUsernames != null && !eligibleUsernames.Contains(x.Username) ? 1 : 0);
            var items = activeUsers
                .Select(x => new OperatorSwitchItem
                {
                    Username = x.Username,
                    DisplayName = x.DisplayName,
                    RoleName = x.RoleName
                })
                .ToList();

            OperatorCombo.ItemsSource = items;
            if (items.Count > 0)
            {
                OperatorCombo.SelectedIndex = 0;
                SwitchButton.IsEnabled = true;
                if (eligibleUsernames != null && eligibleUsernames.Count == 0)
                {
                    var missingPermission = string.IsNullOrWhiteSpace(requiredPermissionName)
                        ? requiredPermissionCode
                        : requiredPermissionName;
                    ShowStatus(PosLocalization.F("operator.switch.noEligibleForPermission", missingPermission));
                    Log("operator_list", "result=no_eligible missingPermission=" + Safe(missingPermission));
                }
                else
                {
                    HideStatus();
                }
                return;
            }

            SwitchButton.IsEnabled = false;
            ShowStatus(PosLocalization.T("operator.switch.noOperators"));
            Log("operator_list", "result=empty");
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
            var selected = OperatorCombo.SelectedItem as OperatorSwitchItem;
            var username = selected?.Username ?? string.Empty;
            var pin = PinBox.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowStatus(PosLocalization.T("operator.login.selectOperator"));
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
                SwitchButton.IsEnabled = OperatorCombo.Items.Count > 0;
            }
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

        private sealed class OperatorSwitchItem
        {
            public string Username { get; set; }
            public string DisplayName { get; set; }
            public string RoleName { get; set; }

            public string DisplayText
            {
                get
                {
                    var name = string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
                    return name + " (@" + Username + ") - " + (RoleName ?? "");
                }
            }
        }
    }
}
