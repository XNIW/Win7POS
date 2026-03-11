using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Sec = Win7POS.Core.Security.SecurityEventCodes;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed class OperatorSession : IOperatorSession, INotifyPropertyChanged
    {
        private readonly UserRepository _userRepo;
        private readonly SecurityRepository _securityRepo;
        private UserAccount _currentUser;

        public OperatorSession(UserRepository userRepo, SecurityRepository securityRepo)
        {
            _userRepo = userRepo ?? throw new ArgumentNullException(nameof(userRepo));
            _securityRepo = securityRepo ?? throw new ArgumentNullException(nameof(securityRepo));
        }

        public UserAccount CurrentUser => _currentUser;
        public bool IsLoggedIn => _currentUser != null && _currentUser.IsActive;
        public bool CurrentUserIsAdmin => _currentUser?.IsAdmin ?? false;
        public string CurrentDisplayName => _currentUser?.DisplayName ?? "—";
        public string CurrentRoleName => _currentUser?.RoleName ?? "—";

        public event Action SessionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public bool Login(string username, string pin)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin))
                return false;

            var result = _userRepo.VerifyPinAsync(username, pin).GetAwaiter().GetResult();
            if (result.User == null)
            {
                if (result.WasLockedOut)
                    _ = _securityRepo.LogEventAsync(Sec.LoginLocked, "username=" + username);
                else
                    _ = _securityRepo.LogEventAsync(Sec.LoginFailed, "username=" + username);
                return false;
            }

            _currentUser = result.User;
            _ = _userRepo.SetLastLoginAsync(result.User.Id);
            _ = _securityRepo.LogEventAsync(Sec.LoginSuccess, "userId=" + result.User.Id + ", username=" + result.User.Username);
            RaiseSessionChanged();
            return true;
        }

        public void Logout()
        {
            LogoutInternal(forced: false);
        }

        public void LogoutForced()
        {
            LogoutInternal(forced: true);
        }

        private void LogoutInternal(bool forced)
        {
            if (_currentUser != null)
            {
                if (forced)
                    _ = _securityRepo.LogEventAsync(Sec.ForcedLogout, "userId=" + _currentUser.Id);
                _ = _securityRepo.LogEventAsync(Sec.Logout, "userId=" + _currentUser.Id);
                _currentUser = null;
                RaiseSessionChanged();
            }
        }

        public void LogOverride(string permissionCode, string operationText, int authorizerUserId)
        {
            var by = _currentUser != null ? " byOperator=" + _currentUser.Id : "";
            _ = _securityRepo.LogEventAsync(Sec.Override, "permission=" + (permissionCode ?? "") + " op=" + (operationText ?? "") + " authorizerId=" + authorizerUserId + by);
        }

        public void LogSecurityEvent(string eventType, string details)
        {
            var userId = _currentUser?.Id;
            _ = _securityRepo.LogEventAsync(eventType ?? "", details ?? "", userId);
        }

        public void SetUserForTesting(UserAccount user)
        {
            _currentUser = user;
            RaiseSessionChanged();
        }

        private void RaiseSessionChanged()
        {
            OnPropertyChanged(nameof(CurrentUser));
            OnPropertyChanged(nameof(IsLoggedIn));
            OnPropertyChanged(nameof(CurrentUserIsAdmin));
            OnPropertyChanged(nameof(CurrentDisplayName));
            OnPropertyChanged(nameof(CurrentRoleName));
            SessionChanged?.Invoke();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
