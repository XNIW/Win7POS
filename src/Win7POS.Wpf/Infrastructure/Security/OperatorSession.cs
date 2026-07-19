using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos.Online;
using Sec = Win7POS.Core.Security.SecurityEventCodes;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed class OperatorSession : IOperatorSession, INotifyPropertyChanged
    {
        private readonly UserRepository _userRepo;
        private readonly SecurityRepository _securityRepo;
        private readonly PosOfflineAuthorizationLeaseGuard _authorizationLeaseGuard;
        private UserAccount _currentUser;

        public OperatorSession(UserRepository userRepo, SecurityRepository securityRepo)
            : this(userRepo, securityRepo, new PosOfflineAuthorizationLeaseGuard())
        {
        }

        internal OperatorSession(
            UserRepository userRepo,
            SecurityRepository securityRepo,
            PosOfflineAuthorizationLeaseGuard authorizationLeaseGuard)
        {
            _userRepo = userRepo ?? throw new ArgumentNullException(nameof(userRepo));
            _securityRepo = securityRepo ?? throw new ArgumentNullException(nameof(securityRepo));
            _authorizationLeaseGuard = authorizationLeaseGuard ?? throw new ArgumentNullException(nameof(authorizationLeaseGuard));
        }

        public UserAccount CurrentUser => _currentUser;
        public bool IsLoggedIn => _currentUser != null && _currentUser.IsActive;
        public bool CurrentUserIsAdmin => _currentUser?.IsAdmin ?? false;
        public string CurrentDisplayName => _currentUser?.DisplayName ?? "—";
        public string CurrentRoleName => _currentUser?.RoleName ?? "—";
        public string LastAuthorizationFailureCode { get; private set; } = string.Empty;

        public event Action SessionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public async Task<LoginResult> LoginAsync(string username, string pin)
        {
            return await LoginInternalAsync(
                username,
                pin,
                requireAuthorizationLease: true,
                requireLocalRecoveryUser: false).ConfigureAwait(true);
        }

        public async Task<LoginResult> LoginLocalRecoveryAsync(string username, string pin)
        {
            return await LoginInternalAsync(
                username,
                pin,
                requireAuthorizationLease: false,
                requireLocalRecoveryUser: true).ConfigureAwait(true);
        }

        private async Task<LoginResult> LoginInternalAsync(
            string username,
            string pin,
            bool requireAuthorizationLease,
            bool requireLocalRecoveryUser)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin))
                return LoginResult.Failed;

            PosTrustedDeviceSession trustedSession = null;
            if (requireAuthorizationLease)
            {
                var authorization = EvaluateAuthorizationLease(out trustedSession);
                if (!authorization.Allowed)
                {
                    LogAuthorizationDenied(authorization.Code);
                    return LoginResult.AuthorizationExpired;
                }

                var trustedUsername = await _userRepo
                    .FindTrustedRemoteStaffUsernameAsync(
                        trustedSession.ShopId,
                        trustedSession.ShopCode,
                        trustedSession.StaffId,
                        trustedSession.StaffCode,
                        trustedSession.StaffCredentialVersion)
                    .ConfigureAwait(true);
                if (!string.Equals(username, trustedUsername, StringComparison.Ordinal))
                {
                    _ = _securityRepo.LogEventAsync(
                        Sec.LoginFailed,
                        "username=" + username + ", mode=trusted_remote_mirror, reason=identity_mismatch");
                    return LoginResult.Failed;
                }
            }

            if (requireLocalRecoveryUser &&
                !await _userRepo.IsLocalRecoveryUserAsync(username).ConfigureAwait(true))
            {
                _ = _securityRepo.LogEventAsync(Sec.LoginFailed, "username=" + username + ", mode=local_recovery");
                return LoginResult.Failed;
            }

            var result = await _userRepo.VerifyPinAsync(username, pin).ConfigureAwait(true);
            if (result.User == null)
            {
                if (result.WasLockedOut)
                {
                    _ = _securityRepo.LogEventAsync(Sec.LoginLocked, "username=" + username);
                    return LoginResult.LockedOut;
                }
                else
                    _ = _securityRepo.LogEventAsync(Sec.LoginFailed, "username=" + username);
                return LoginResult.Failed;
            }

            _currentUser = result.User;
            _ = _userRepo.SetLastLoginAsync(result.User.Id);
            _ = _securityRepo.LogEventAsync(
                Sec.LoginSuccess,
                "userId=" + result.User.Id +
                ", username=" + result.User.Username +
                (requireLocalRecoveryUser ? ", mode=local_recovery" : string.Empty));
            RaiseSessionChanged();
            return LoginResult.Success;
        }

        public PosOfflineAuthorizationLeaseDecision EvaluateAuthorizationLease()
        {
            PosTrustedDeviceSession ignoredSession;
            return EvaluateAuthorizationLease(out ignoredSession);
        }

        private PosOfflineAuthorizationLeaseDecision EvaluateAuthorizationLease(
            out PosTrustedDeviceSession trustedSession)
        {
            var decision = _authorizationLeaseGuard.Evaluate(out trustedSession);
            LastAuthorizationFailureCode = decision.Allowed
                ? string.Empty
                : decision.Code ?? "authorization_lease_denied";
            return decision;
        }

        public bool EnsureAuthorizationValid()
        {
            var decision = EvaluateAuthorizationLease();
            if (decision.Allowed)
            {
                return true;
            }

            LogAuthorizationDenied(decision.Code);
            if (_currentUser != null)
            {
                LogoutForced();
            }

            return false;
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

        private void LogAuthorizationDenied(string code)
        {
            _ = _securityRepo.LogEventAsync(
                Sec.PosAuthorizationLeaseDenied,
                "code=" + SafeCode(code),
                _currentUser?.Id);
        }

        private static string SafeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length > 64)
            {
                normalized = normalized.Substring(0, 64);
            }

            return normalized;
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
