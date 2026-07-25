using System;
using System.Collections.Generic;
using System.Linq;
using Win7POS.Core.Security;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed class PermissionService : IPermissionService
    {
        private readonly IOperatorSession _session;
        private readonly OperatorSession _operatorSession;

        public PermissionService(IOperatorSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _operatorSession = session as OperatorSession;
        }

        public bool Has(string permissionCode)
        {
            if (string.IsNullOrEmpty(permissionCode)) return false;
            return TryGetAuthorizationBoundUser(out var user) &&
                HasPermission(user, permissionCode);
        }

        public void Demand(string permissionCode, string operationText)
        {
            if (!TryGetAuthorizationBoundUser(out var user))
            {
                var code = _session.LastAuthorizationFailureCode;
                throw new PosAuthorizationLeaseException(
                    string.IsNullOrWhiteSpace(code)
                        ? "sync_generation_inactive"
                        : code,
                    PosLocalization.T("access.login.authorizationExpired"));
            }

            if (!HasPermission(user, permissionCode))
                throw new InvalidOperationException("Permesso negato: " + (operationText ?? permissionCode));
        }

        public bool CanOverride(string permissionCode)
        {
            return TryGetAuthorizationBoundUser(out var user) &&
                user.CanOverride &&
                HasPermission(user, PermissionCodes.SecurityOverride);
        }

        private bool TryGetAuthorizationBoundUser(
            out UserAccount user)
        {
            user = null;
            return _operatorSession != null &&
                _operatorSession.TryGetAuthorizationBoundUser(
                    out user);
        }

        private static bool HasPermission(
            UserAccount user,
            string permissionCode)
        {
            if (user == null ||
                string.IsNullOrWhiteSpace(permissionCode))
            {
                return false;
            }
            if (user.IsAdmin)
                return true;
            var codes = user.PermissionCodes;
            return codes != null &&
                ((IEnumerable<string>)codes).Any(
                    permission => string.Equals(
                        permission,
                        permissionCode,
                        StringComparison.Ordinal));
        }
    }

    public sealed class PosAuthorizationLeaseException : InvalidOperationException
    {
        public PosAuthorizationLeaseException(string code, string message)
            : base(message)
        {
            Code = code ?? string.Empty;
        }

        public string Code { get; }
        internal long OperatorAuthorityVersion { get; private set; } =
            long.MinValue;

        internal void BindOperatorAuthorityVersion(long version)
        {
            if (OperatorAuthorityVersion == long.MinValue)
                OperatorAuthorityVersion = version;
        }
    }
}
