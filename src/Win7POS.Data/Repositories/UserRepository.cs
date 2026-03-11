using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Security;
using Win7POS.Core.Util;

namespace Win7POS.Data.Repositories
{
    public sealed class UserRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public UserRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<IReadOnlyList<UserAccount>> ListAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<UserRow>(
                @"SELECT u.id, u.username, u.display_name, u.role_id, u.is_active, u.require_pin_change, u.max_discount_percent,
                         r.code AS role_code, r.name AS role_name
                  FROM users u
                  INNER JOIN roles r ON r.id = u.role_id
                  ORDER BY u.username").ConfigureAwait(false);
            return (rows ?? Enumerable.Empty<UserRow>()).Select(r => MapToAccount(r)).ToList();
        }

        public async Task<UserAccount> GetByIdAsync(int id)
        {
            using var conn = _factory.Open();
            var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
                @"SELECT u.id, u.username, u.display_name, u.role_id, u.is_active, u.require_pin_change, u.max_discount_percent,
                         r.code AS role_code, r.name AS role_name
                  FROM users u
                  INNER JOIN roles r ON r.id = u.role_id
                  WHERE u.id = @id",
                new { id }).ConfigureAwait(false);
            if (row == null) return null;
            var perms = await conn.QueryAsync<string>(
                "SELECT permission_code FROM role_permissions WHERE role_id = @roleId",
                new { roleId = row.RoleId }).ConfigureAwait(false);
            return MapToAccount(row, (perms ?? Enumerable.Empty<string>()).ToList());
        }

        public async Task<UserAccount> GetByUsernameAsync(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            using var conn = _factory.Open();
            var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
                @"SELECT u.id, u.username, u.display_name, u.role_id, u.is_active, u.require_pin_change, u.max_discount_percent,
                         r.code AS role_code, r.name AS role_name
                  FROM users u
                  INNER JOIN roles r ON r.id = u.role_id
                  WHERE u.username = @username",
                new { username }).ConfigureAwait(false);
            if (row == null) return null;
            var perms = await conn.QueryAsync<string>(
                "SELECT permission_code FROM role_permissions WHERE role_id = @roleId",
                new { roleId = row.RoleId }).ConfigureAwait(false);
            return MapToAccount(row, (perms ?? Enumerable.Empty<string>()).ToList());
        }

        private const int LockoutFailedAttempts = 5;
        private const int LockoutDurationSeconds = 900; // 15 minuti

        /// <summary>Risultato verifica PIN: utente se OK, altrimenti null e WasLockedOut indica se il fallimento è per lockout.</summary>
        public sealed class VerifyPinResult
        {
            public UserAccount User { get; set; }
            public bool WasLockedOut { get; set; }
        }

        /// <summary>Verifica PIN e ritorna l'utente con permessi se OK; altrimenti User=null e WasLockedOut=true se account bloccato.</summary>
        public async Task<VerifyPinResult> VerifyPinAsync(string username, string pin)
        {
            var fail = new VerifyPinResult { User = null, WasLockedOut = false };
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin)) return fail;
            using var conn = _factory.Open();
            var row = await conn.QuerySingleOrDefaultAsync<UserPinRow>(
                "SELECT id, username, display_name, pin_hash, pin_salt, role_id, is_active, require_pin_change, max_discount_percent, failed_attempts, lockout_until FROM users WHERE username = @username",
                new { username }).ConfigureAwait(false);
            if (row == null || row.IsActive != 1) return fail;

            var now = UnixTime.NowSeconds();
            if (row.LockoutUntil.HasValue && row.LockoutUntil.Value > now)
                return new VerifyPinResult { User = null, WasLockedOut = true };

            if (!PinHelper.VerifyPin(pin, row.PinSalt, row.PinHash))
            {
                var attempts = row.FailedAttempts + 1;
                var lockoutUntil = attempts >= LockoutFailedAttempts ? (long?)(now + LockoutDurationSeconds) : null;
                await conn.ExecuteAsync(
                    "UPDATE users SET failed_attempts = @attempts, lockout_until = @lockoutUntil WHERE id = @id",
                    new { attempts, lockoutUntil, id = row.Id }).ConfigureAwait(false);
                return fail;
            }

            var role = await conn.QuerySingleOrDefaultAsync<RoleRow>("SELECT code, name FROM roles WHERE id = @id", new { id = row.RoleId }).ConfigureAwait(false);
            var perms = await conn.QueryAsync<string>("SELECT permission_code FROM role_permissions WHERE role_id = @roleId", new { roleId = row.RoleId }).ConfigureAwait(false);
            var permList = (perms ?? Enumerable.Empty<string>()).ToList();

            var account = new UserAccount
            {
                Id = row.Id,
                Username = row.Username,
                DisplayName = row.DisplayName,
                RoleId = row.RoleId,
                RoleCode = role?.Code ?? "",
                RoleName = role?.Name ?? "",
                IsActive = true,
                RequirePinChange = row.RequirePinChange == 1,
                MaxDiscountPercent = row.MaxDiscountPercent,
                CanOverride = permList.Contains(PermissionCodes.SecurityOverride),
                PermissionCodes = permList
            };
            return new VerifyPinResult { User = account, WasLockedOut = false };
        }

        public async Task<int> CreateAsync(string username, string displayName, string pinHash, string pinSalt, int roleId, int maxDiscountPercent = 0, bool requirePinChange = false)
        {
            var now = UnixTime.NowSeconds();
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO users(username, display_name, pin_hash, pin_salt, role_id, is_active, require_pin_change, max_discount_percent, created_at, updated_at)
VALUES(@username, @displayName, @pinHash, @pinSalt, @roleId, 1, @requirePinChange, @maxDiscountPercent, @now, @now)",
                new { username, displayName, pinHash, pinSalt, roleId, requirePinChange = requirePinChange ? 1 : 0, maxDiscountPercent, now }).ConfigureAwait(false);
            return await conn.ExecuteScalarAsync<int>("SELECT last_insert_rowid()").ConfigureAwait(false);
        }

        public async Task<int> CountActiveAdminsAsync()
        {
            using var conn = _factory.Open();
            var adminRoleId = await conn.ExecuteScalarAsync<int?>("SELECT id FROM roles WHERE LOWER(code) = 'admin' LIMIT 1").ConfigureAwait(false);
            if (!adminRoleId.HasValue) return 0;
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM users WHERE role_id = @rid AND is_active = 1",
                new { rid = adminRoleId.Value }).ConfigureAwait(false);
        }

        public async Task<bool> IsAdminRoleAsync(int roleId)
        {
            if (roleId <= 0) return false;
            using var conn = _factory.Open();
            var code = await conn.ExecuteScalarAsync<string>("SELECT code FROM roles WHERE id = @id", new { id = roleId }).ConfigureAwait(false);
            return string.Equals(code, "admin", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Lancia se l'operazione rimuoverebbe o degraderebbe l'ultimo amministratore attivo.</summary>
        public async Task EnsureNotLastActiveAdminAsync(int userId, int newRoleId, bool isActive)
        {
            var user = await GetByIdAsync(userId).ConfigureAwait(false);
            if (user == null) return;
            var wasAdmin = string.Equals(user.RoleCode, "admin", StringComparison.OrdinalIgnoreCase);
            var willStillBeAdmin = await IsAdminRoleAsync(newRoleId).ConfigureAwait(false);
            if (!wasAdmin) return;
            if (isActive && willStillBeAdmin) return;
            var count = await CountActiveAdminsAsync().ConfigureAwait(false);
            if (count <= 1)
                throw new InvalidOperationException("Non è possibile rimuovere o disattivare l'ultimo amministratore attivo.");
        }

        public async Task UpdateAsync(int id, string displayName, int roleId, bool isActive, int maxDiscountPercent, bool requirePinChange)
        {
            await EnsureNotLastActiveAdminAsync(id, roleId, isActive).ConfigureAwait(false);
            var now = UnixTime.NowSeconds();
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
UPDATE users SET display_name = @displayName, role_id = @roleId, is_active = @isActive, max_discount_percent = @maxDiscountPercent, require_pin_change = @requirePinChange, updated_at = @now
WHERE id = @id",
                new { id, displayName, roleId, isActive = isActive ? 1 : 0, maxDiscountPercent, requirePinChange = requirePinChange ? 1 : 0, now }).ConfigureAwait(false);
        }

        public async Task UpdatePinAsync(int id, string pinHash, string pinSalt, bool requirePinChange = false)
        {
            var now = UnixTime.NowSeconds();
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
UPDATE users SET pin_hash = @pinHash, pin_salt = @pinSalt, require_pin_change = @requirePinChange, updated_at = @now WHERE id = @id",
                new { id, pinHash, pinSalt, requirePinChange = requirePinChange ? 1 : 0, now }).ConfigureAwait(false);
        }

        public async Task SetLastLoginAsync(int userId)
        {
            var now = UnixTime.NowSeconds();
            using var conn = _factory.Open();
            await conn.ExecuteAsync("UPDATE users SET last_login_at = @now, failed_attempts = 0, lockout_until = NULL WHERE id = @userId", new { now, userId }).ConfigureAwait(false);
        }

        private static UserAccount MapToAccount(UserRow r, IReadOnlyList<string> perms = null)
        {
            return new UserAccount
            {
                Id = r.Id,
                Username = r.Username,
                DisplayName = r.DisplayName,
                RoleId = r.RoleId,
                RoleCode = r.RoleCode ?? "",
                RoleName = r.RoleName ?? "",
                IsActive = r.IsActive == 1,
                RequirePinChange = r.RequirePinChange == 1,
                MaxDiscountPercent = r.MaxDiscountPercent,
                PermissionCodes = perms ?? new List<string>(),
                CanOverride = (perms ?? new List<string>()).Contains(PermissionCodes.SecurityOverride)
            };
        }

        private sealed class UserRow
        {
            public int Id { get; set; }
            public string Username { get; set; }
            public string DisplayName { get; set; }
            public int RoleId { get; set; }
            public string RoleCode { get; set; }
            public string RoleName { get; set; }
            public int IsActive { get; set; }
            public int RequirePinChange { get; set; }
            public int MaxDiscountPercent { get; set; }
        }

        private sealed class UserPinRow
        {
            public int Id { get; set; }
            public string Username { get; set; }
            public string DisplayName { get; set; }
            public string PinHash { get; set; }
            public string PinSalt { get; set; }
            public int RoleId { get; set; }
            public int IsActive { get; set; }
            public int RequirePinChange { get; set; }
            public int MaxDiscountPercent { get; set; }
            public int FailedAttempts { get; set; }
            public long? LockoutUntil { get; set; }
        }

        private sealed class RoleRow
        {
            public string Code { get; set; }
            public string Name { get; set; }
        }
    }
}
