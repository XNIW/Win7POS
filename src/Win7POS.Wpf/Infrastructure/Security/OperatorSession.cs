using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;
using Sec = Win7POS.Core.Security.SecurityEventCodes;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed class OperatorSession : IOperatorSession, INotifyPropertyChanged
    {
        private readonly UserRepository _userRepo;
        private readonly SecurityRepository _securityRepo;
        private readonly PosOfflineAuthorizationLeaseGuard _authorizationLeaseGuard;
        private Action<string, int> _authorizationUseTestHook;
        private UserAccount _currentUser;
        private bool _currentUserCanUsePosAuthorization;
        private long _operatorAuthorityVersion;

        internal OperatorSession(UserRepository userRepo, SecurityRepository securityRepo)
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

            var operatorAuthorityVersionAtLoginStart =
                Interlocked.Read(ref _operatorAuthorityVersion);
            PosTrustedDeviceSession trustedSession = null;
            PosOfflineAuthorizationLeaseEvaluation initialEvaluation = null;
            PosOfflineAuthorizationLeaseEvaluation committedEvaluation = null;
            if (requireAuthorizationLease)
            {
                initialEvaluation = await _authorizationLeaseGuard.PreflightAsync()
                    .ConfigureAwait(true);
                var authorization = initialEvaluation.Decision;
                trustedSession = initialEvaluation.TrustedSession;
                SetAuthorizationDecision(authorization);
                if (!authorization.Allowed)
                {
                    LogAuthorizationDenied(authorization.Code, null);
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

            if (requireAuthorizationLease)
            {
                var finalEvaluation = await _authorizationLeaseGuard
                    .PreflightAsync().ConfigureAwait(true);
                committedEvaluation = await _authorizationLeaseGuard
                    .CommitAuthenticationAsync(initialEvaluation, finalEvaluation)
                    .ConfigureAwait(true);
                SetAuthorizationDecision(committedEvaluation.Decision);
                if (!committedEvaluation.Decision.Allowed ||
                    !IsSameTrustedGeneration(
                        trustedSession,
                        committedEvaluation.TrustedSession))
                {
                    LogAuthorizationDenied(
                        committedEvaluation.Decision.Allowed
                            ? "sync_generation_changed"
                            : committedEvaluation.Decision.Code,
                        null);
                    return LoginResult.AuthorizationExpired;
                }
            }

            var operatorAuthorityChanged = false;
            if (requireAuthorizationLease)
            {
                _authorizationUseTestHook?.Invoke(
                    "before_operator_authority_bind",
                    0);
                operatorAuthorityChanged =
                    committedEvaluation?.Token != null &&
                    PosOnlineSyncRevocationLatch.TryChangeOperatorAuthority(
                        committedEvaluation.Token.AuthorizationEpoch,
                        committedEvaluation.Token.GenerationFingerprint,
                        () =>
                        {
                            _currentUser = result.User;
                            _currentUserCanUsePosAuthorization = true;
                            SetAuthorizationDecision(
                                committedEvaluation.Decision);
                            Interlocked.Increment(
                                ref _operatorAuthorityVersion);
                        });
            }
            else
            {
                PosOnlineSyncRevocationLatch.ChangeOperatorAuthority(
                    () =>
                    {
                        _currentUser = result.User;
                        _currentUserCanUsePosAuthorization = false;
                        Interlocked.Increment(
                            ref _operatorAuthorityVersion);
                    },
                    invalidateAuthorization:
                        requireLocalRecoveryUser);
                operatorAuthorityChanged = true;
            }
            if (!operatorAuthorityChanged)
            {
                const string code = "sync_generation_inactive";
                HandleAuthorizationUseDenied(
                    code,
                    operatorAuthorityVersionAtLoginStart);
                return LoginResult.AuthorizationExpired;
            }
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
            SetAuthorizationDecision(decision);
            return decision;
        }

        private void SetAuthorizationDecision(
            PosOfflineAuthorizationLeaseDecision decision)
        {
            LastAuthorizationFailureCode = decision.Allowed
                ? string.Empty
                : decision.Code ?? "authorization_lease_denied";
        }

        public bool EnsureAuthorizationValid()
        {
            var expectedOperatorAuthorityVersion =
                Interlocked.Read(ref _operatorAuthorityVersion);
            var hadOperatorAuthority = _currentUser != null;
            var decision = _authorizationLeaseGuard.Evaluate(
                out _);
            if (decision.Allowed &&
                _currentUserCanUsePosAuthorization &&
                Interlocked.Read(ref _operatorAuthorityVersion) ==
                    expectedOperatorAuthorityVersion)
            {
                SetAuthorizationDecision(decision);
                return true;
            }
            if (decision.Allowed)
            {
                decision = PosOfflineAuthorizationLeaseDecision.Deny(
                    "sync_generation_inactive");
            }

            _authorizationUseTestHook?.Invoke(
                "after_authorization_evaluation_denied",
                0);
            if (hadOperatorAuthority)
            {
                HandleAuthorizationUseDenied(
                    decision.Code,
                    expectedOperatorAuthorityVersion);
            }
            else
            {
                if (Interlocked.Read(ref _operatorAuthorityVersion) ==
                    expectedOperatorAuthorityVersion)
                {
                    SetAuthorizationDecision(decision);
                }
                LogAuthorizationDenied(decision.Code, null);
            }

            return false;
        }

        internal bool TryGetAuthorizationBoundUser(
            out UserAccount authorizedUser)
        {
            authorizedUser = null;
            if (!PosOnlineSyncRevocationLatch
                    .TryCaptureAuthorizationEpoch(
                        out var expectedAuthorizationEpoch))
            {
                LogAuthorizationDenied(
                    "sync_generation_inactive",
                    null);
                return false;
            }
            var expectedOperatorAuthorityVersion =
                Interlocked.Read(ref _operatorAuthorityVersion);
            if (!EnsureAuthorizationValid())
                return false;

            _authorizationUseTestHook?.Invoke(
                "after_authorization_valid_before_operator_capture",
                0);
            UserAccount capturedUser = null;
            var captured =
                PosOnlineSyncRevocationLatch
                    .TryReadOperatorAuthorityIf(
                        expectedAuthorizationEpoch,
                        () =>
                            Interlocked.Read(
                                ref _operatorAuthorityVersion) ==
                                expectedOperatorAuthorityVersion &&
                            _currentUserCanUsePosAuthorization &&
                            _currentUser != null &&
                            _currentUser.IsActive,
                        () => capturedUser = _currentUser);
            if (!captured)
            {
                LogAuthorizationDenied(
                    "sync_generation_inactive",
                    null);
                return false;
            }

            authorizedUser = capturedUser;
            return true;
        }

        internal void HandleAuthorizationUseDenied(
            string code,
            long expectedOperatorAuthorityVersion)
        {
            var normalizedCode = string.IsNullOrWhiteSpace(code)
                ? "authorization_lease_denied"
                : code.Trim();
            UserAccount deniedUser = null;
            var invalidated =
                PosOnlineSyncRevocationLatch
                    .TryChangeOperatorAuthorityIf(
                        () => Interlocked.Read(
                            ref _operatorAuthorityVersion) ==
                            expectedOperatorAuthorityVersion,
                        () =>
                        {
                            deniedUser = _currentUser;
                            _currentUser = null;
                            _currentUserCanUsePosAuthorization = false;
                            SetAuthorizationDecision(
                                PosOfflineAuthorizationLeaseDecision.Deny(
                                    normalizedCode));
                            Interlocked.Increment(
                                ref _operatorAuthorityVersion);
                        },
                        invalidateAuthorization: true);
            if (!invalidated)
            {
                LogAuthorizationDenied(normalizedCode, null);
                return;
            }

            LogAuthorizationDenied(
                normalizedCode,
                deniedUser?.Id);
            if (deniedUser != null)
            {
                _ = _securityRepo.LogEventAsync(
                    Sec.ForcedLogout,
                    "userId=" + deniedUser.Id);
                _ = _securityRepo.LogEventAsync(
                    Sec.Logout,
                    "userId=" + deniedUser.Id);
                RaiseSessionChanged();
            }
        }

        internal async Task<IPosAuthorizationUseLease> BeginAuthorizationUseAsync(
            string permissionCode,
            string operationText)
        {
            var operatorAuthorityVersion =
                Interlocked.Read(ref _operatorAuthorityVersion);
            _authorizationUseTestHook?.Invoke(
                "before_authorization_use_gate",
                0);
            var latchLease = await PosOnlineSyncRevocationLatch
                .EnterAuthorizationUseAsync().ConfigureAwait(false);
            try
            {
                var decision = _authorizationLeaseGuard
                    .EvaluateAuthorizationUse(
                        out var trustedSession,
                        out var commitExpiryGuard);
                SetAuthorizationDecision(decision);
                if (!decision.Allowed ||
                    trustedSession == null ||
                    commitExpiryGuard == null ||
                    !PosOnlineSyncSupervisorHost.TryCreateGeneration(
                        trustedSession,
                        out var generation) ||
                    !PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(
                        out var authorizationEpoch) ||
                    PosOnlineSyncRevocationLatch.IsRevoked(generation))
                {
                    var code = decision.Allowed
                        ? "sync_generation_inactive"
                        : decision.Code;
                    throw new PosAuthorizationLeaseException(
                        code,
                        PosLocalization.T(
                            "access.login.authorizationExpired"));
                }

                var user = _currentUser;
                if (!_currentUserCanUsePosAuthorization)
                {
                    throw new PosAuthorizationLeaseException(
                        "sync_generation_inactive",
                        PosLocalization.T(
                            "access.login.authorizationExpired"));
                }
                if (user == null ||
                    !user.IsActive ||
                    !HasPermission(user, permissionCode))
                {
                    throw new InvalidOperationException(
                        "Permesso negato: " +
                        (operationText ?? permissionCode));
                }

                var lease = new AuthorizationUseLease(
                    this,
                    latchLease,
                    user.Id,
                    permissionCode,
                    operationText,
                    authorizationEpoch,
                    generation,
                    commitExpiryGuard,
                    operatorAuthorityVersion);
                lease.CommitGuard.DemandStillValid();
                return lease;
            }
            catch (PosAuthorizationLeaseException ex)
            {
                ex.BindOperatorAuthorityVersion(
                    operatorAuthorityVersion);
                latchLease.Dispose();
                throw;
            }
            catch
            {
                latchLease.Dispose();
                throw;
            }
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
            UserAccount loggedOutUser = null;
            PosOnlineSyncRevocationLatch.ChangeOperatorAuthority(
                () =>
                {
                    loggedOutUser = _currentUser;
                    _currentUser = null;
                    _currentUserCanUsePosAuthorization = false;
                    Interlocked.Increment(
                        ref _operatorAuthorityVersion);
                },
                invalidateAuthorization: true);
            if (loggedOutUser != null)
            {
                if (forced)
                    _ = _securityRepo.LogEventAsync(
                        Sec.ForcedLogout,
                        "userId=" + loggedOutUser.Id);
                _ = _securityRepo.LogEventAsync(
                    Sec.Logout,
                    "userId=" + loggedOutUser.Id);
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

        internal void SetUserForTesting(UserAccount user)
        {
            PosOnlineSyncRevocationLatch.ChangeOperatorAuthority(
                () =>
                {
                    _currentUser = user;
                    _currentUserCanUsePosAuthorization = true;
                    Interlocked.Increment(
                        ref _operatorAuthorityVersion);
                },
                invalidateAuthorization: true);
            RaiseSessionChanged();
        }

        internal void SetAuthorizationUseTestHookForTesting(
            Action<string, int> hook)
        {
            _authorizationUseTestHook = hook;
        }

        private void LogAuthorizationDenied(
            string code,
            int? userId)
        {
            _ = _securityRepo.LogEventAsync(
                Sec.PosAuthorizationLeaseDenied,
                "code=" + SafeCode(code),
                userId);
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

        private static bool IsSameTrustedGeneration(
            PosTrustedDeviceSession expected,
            PosTrustedDeviceSession current)
        {
            if (!PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    expected,
                    out var expectedGeneration) ||
                !PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    current,
                    out var currentGeneration))
            {
                return false;
            }

            return string.Equals(
                expectedGeneration.Fingerprint,
                currentGeneration.Fingerprint,
                StringComparison.Ordinal);
        }

        private void DemandAuthorizationUseStillValid(
            int expectedOperatorId,
            string permissionCode,
            string operationText,
            long expectedAuthorizationEpoch,
            OnlineSyncGeneration expectedGeneration,
            PosAuthorizationCommitExpiryGuard commitExpiryGuard,
            long expectedOperatorAuthorityVersion,
            TimeSpan minimumRemaining)
        {
            DemandCommitExpiryStillValid(
                commitExpiryGuard,
                minimumRemaining);
            if (expectedGeneration == null ||
                !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                    expectedAuthorizationEpoch) ||
                PosOnlineSyncRevocationLatch.IsRevokedFingerprint(
                    expectedGeneration.Fingerprint))
            {
                throw new PosAuthorizationLeaseException(
                    "sync_generation_inactive",
                    PosLocalization.T(
                        "access.login.authorizationExpired"));
            }

            var decision = _authorizationLeaseGuard.Evaluate(
                out var trustedSession);
            if (!decision.Allowed)
            {
                throw new PosAuthorizationLeaseException(
                    decision.Code,
                    PosLocalization.T(
                        "access.login.authorizationExpired"));
            }
            if (trustedSession == null ||
                !PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    trustedSession,
                    out var currentGeneration) ||
                !HasSameAuthorizationBinding(
                    expectedGeneration,
                    currentGeneration) ||
                !PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(
                    expectedAuthorizationEpoch) ||
                PosOnlineSyncRevocationLatch.IsRevokedFingerprint(
                    expectedGeneration.Fingerprint))
            {
                throw new PosAuthorizationLeaseException(
                    "sync_generation_inactive",
                    PosLocalization.T(
                        "access.login.authorizationExpired"));
            }

            DemandOperatorAuthorityStillCurrent(
                expectedOperatorId,
                permissionCode,
                operationText,
                expectedOperatorAuthorityVersion);
        }

        private static bool HasSameAuthorizationBinding(
            OnlineSyncGeneration expected,
            OnlineSyncGeneration current)
        {
            return expected != null &&
                current != null &&
                string.Equals(
                    expected.Fingerprint,
                    current.Fingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(
                    expected.GenerationId,
                    current.GenerationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    expected.ShopId,
                    current.ShopId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    expected.ShopCode,
                    current.ShopCode,
                    StringComparison.Ordinal) &&
                string.Equals(
                    expected.ShopDeviceId,
                    current.ShopDeviceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    expected.StaffId,
                    current.StaffId,
                    StringComparison.Ordinal) &&
                expected.StaffCredentialVersion ==
                    current.StaffCredentialVersion;
        }

        private static bool HasPermission(
            UserAccount user,
            string permissionCode)
        {
            if (user == null ||
                !user.IsActive ||
                string.IsNullOrWhiteSpace(permissionCode))
            {
                return false;
            }
            if (user.IsAdmin)
                return true;
            if (user.PermissionCodes == null)
                return false;
            foreach (var code in user.PermissionCodes)
            {
                if (string.Equals(
                    code,
                    permissionCode,
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private void CommitAuthorizationUseStillValid(
            int expectedOperatorId,
            string permissionCode,
            string operationText,
            long expectedAuthorizationEpoch,
            OnlineSyncGeneration expectedGeneration,
            PosAuthorizationCommitExpiryGuard commitExpiryGuard,
            long expectedOperatorAuthorityVersion,
            TimeSpan minimumRemaining,
            int demandCount,
            Action commit)
        {
            // Preserve the global lock order: the lease guard owns its _sync
            // while consulting the latch, so no lease/store evaluation may run
            // while the latch Gate is held.
            DemandAuthorizationUseStillValid(
                expectedOperatorId,
                permissionCode,
                operationText,
                expectedAuthorizationEpoch,
                expectedGeneration,
                commitExpiryGuard,
                expectedOperatorAuthorityVersion,
                minimumRemaining);
            var committed = PosOnlineSyncRevocationLatch
                .CommitIfAuthorizationCurrent(
                    expectedAuthorizationEpoch,
                    expectedGeneration?.Fingerprint,
                    () =>
                    {
                        _authorizationUseTestHook?.Invoke(
                            "inside_commit_gate",
                            demandCount);
                        DemandCommitExpiryStillValid(
                            commitExpiryGuard,
                            minimumRemaining);
                        DemandOperatorAuthorityStillCurrent(
                            expectedOperatorId,
                            permissionCode,
                            operationText,
                            expectedOperatorAuthorityVersion);
                    },
                    commit);
            if (!committed)
            {
                throw new PosAuthorizationLeaseException(
                    "sync_generation_inactive",
                    PosLocalization.T(
                        "access.login.authorizationExpired"));
            }
            _authorizationUseTestHook?.Invoke(
                "after_commit_before_return",
                demandCount);
        }

        private void DemandOperatorAuthorityStillCurrent(
            int expectedOperatorId,
            string permissionCode,
            string operationText,
            long expectedOperatorAuthorityVersion)
        {
            var user = _currentUser;
            if (Interlocked.Read(ref _operatorAuthorityVersion) !=
                    expectedOperatorAuthorityVersion ||
                !_currentUserCanUsePosAuthorization ||
                user == null ||
                !user.IsActive ||
                user.Id != expectedOperatorId ||
                !HasPermission(user, permissionCode))
            {
                throw new InvalidOperationException(
                    "Permesso negato: " +
                    (operationText ?? permissionCode));
            }
        }

        private static void DemandCommitExpiryStillValid(
            PosAuthorizationCommitExpiryGuard commitExpiryGuard,
            TimeSpan minimumRemaining)
        {
            var decision = commitExpiryGuard?.Evaluate(minimumRemaining) ??
                PosOfflineAuthorizationLeaseDecision.Deny(
                    "trusted_time_continuity_lost");
            if (!decision.Allowed)
            {
                throw new PosAuthorizationLeaseException(
                    decision.Code,
                    PosLocalization.T(
                        "access.login.authorizationExpired"));
            }
        }

        private sealed class AuthorizationUseLease : IPosAuthorizationUseLease
        {
            private readonly OperatorSession _owner;
            private readonly string _operationText;
            private readonly string _permissionCode;
            private readonly OnlineSyncGeneration _generation;
            private readonly PosAuthorizationCommitExpiryGuard
                _commitExpiryGuard;
            private IDisposable _latchLease;
            private int _demandCount;

            public AuthorizationUseLease(
                OperatorSession owner,
                IDisposable latchLease,
                int operatorId,
                string permissionCode,
                string operationText,
                long authorizationEpoch,
                OnlineSyncGeneration generation,
                PosAuthorizationCommitExpiryGuard commitExpiryGuard,
                long operatorAuthorityVersion)
            {
                _owner = owner ??
                    throw new ArgumentNullException(nameof(owner));
                _latchLease = latchLease ??
                    throw new ArgumentNullException(nameof(latchLease));
                OperatorId = operatorId;
                _permissionCode = permissionCode ?? string.Empty;
                _operationText = operationText;
                AuthorizationEpoch = authorizationEpoch;
                OperatorAuthorityVersion = operatorAuthorityVersion;
                _generation = generation ??
                    throw new ArgumentNullException(nameof(generation));
                _commitExpiryGuard = commitExpiryGuard ??
                    throw new ArgumentNullException(
                        nameof(commitExpiryGuard));
                CommitGuard = new SaleAuthorizationCommitGuard(
                    AuthorizationEpoch,
                    _generation.Fingerprint,
                    _generation.GenerationId,
                    OperatorId,
                    _generation.ShopCode,
                    _generation.ShopDeviceId,
                    _generation.ShopId,
                    _generation.StaffCredentialVersion,
                    _generation.StaffId,
                    DemandStillValid,
                    CommitIfStillValid);
            }

            private long AuthorizationEpoch { get; }
            private long OperatorAuthorityVersion { get; }
            public SaleAuthorizationCommitGuard CommitGuard { get; }
            private int OperatorId { get; }

            private void DemandStillValid()
            {
                if (_latchLease == null)
                {
                    throw new ObjectDisposedException(
                        nameof(AuthorizationUseLease));
                }
                var demandCount = Interlocked.Increment(ref _demandCount);
                _owner._authorizationUseTestHook?.Invoke(
                    "before_demand",
                    demandCount);
                try
                {
                    _owner.DemandAuthorizationUseStillValid(
                        OperatorId,
                        _permissionCode,
                        _operationText,
                        AuthorizationEpoch,
                        _generation,
                        _commitExpiryGuard,
                        OperatorAuthorityVersion,
                        TimeSpan.Zero);
                }
                catch (PosAuthorizationLeaseException ex)
                {
                    ex.BindOperatorAuthorityVersion(
                        OperatorAuthorityVersion);
                    throw;
                }
            }

            private void CommitIfStillValid(
                TimeSpan minimumRemaining,
                Action commit)
            {
                if (_latchLease == null)
                {
                    throw new ObjectDisposedException(
                        nameof(AuthorizationUseLease));
                }
                var demandCount = Interlocked.Increment(ref _demandCount);
                _owner._authorizationUseTestHook?.Invoke(
                    "before_demand",
                    demandCount);
                try
                {
                    _owner.CommitAuthorizationUseStillValid(
                        OperatorId,
                        _permissionCode,
                        _operationText,
                        AuthorizationEpoch,
                        _generation,
                        _commitExpiryGuard,
                        OperatorAuthorityVersion,
                        minimumRemaining,
                        demandCount,
                        commit);
                }
                catch (PosAuthorizationLeaseException ex)
                {
                    ex.BindOperatorAuthorityVersion(
                        OperatorAuthorityVersion);
                    throw;
                }
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _latchLease, null)?.Dispose();
            }
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
