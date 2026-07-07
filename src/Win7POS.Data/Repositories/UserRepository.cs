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
                @"SELECT u.id AS Id, u.username AS Username, u.display_name AS DisplayName, u.role_id AS RoleId,
                         u.is_active AS IsActive, u.require_pin_change AS RequirePinChange, u.max_discount_percent AS MaxDiscountPercent,
                         r.code AS RoleCode, r.name AS RoleName
                  FROM users u
                  INNER JOIN roles r ON r.id = u.role_id
                  ORDER BY u.username").ConfigureAwait(false);
            return (rows ?? Enumerable.Empty<UserRow>()).Select(r => MapToAccount(r)).ToList();
        }

        public async Task<IReadOnlyList<UserAccount>> ListActiveForOperatorSwitchAsync(string preferredShopCode)
        {
            var shop = Normalize(preferredShopCode);
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<UserRow>(
                @"SELECT u.id AS Id, u.username AS Username, u.display_name AS DisplayName, u.role_id AS RoleId,
                         u.is_active AS IsActive, u.require_pin_change AS RequirePinChange, u.max_discount_percent AS MaxDiscountPercent,
                         r.code AS RoleCode, r.name AS RoleName
                  FROM users u
                  INNER JOIN roles r ON r.id = u.role_id
                  WHERE u.is_active = 1
                  ORDER BY
                    CASE
                      WHEN @shop <> '' AND UPPER(TRIM(COALESCE(u.remote_shop_code, ''))) = UPPER(@shop) THEN 0
                      WHEN TRIM(COALESCE(u.remote_shop_code, '')) = '' THEN 1
                      ELSE 2
                    END,
                    u.display_name,
                    u.username",
                new { shop }).ConfigureAwait(false);
            return (rows ?? Enumerable.Empty<UserRow>()).Select(r => MapToAccount(r)).ToList();
        }

        public async Task<UserAccount> GetByIdAsync(int id)
        {
            using var conn = _factory.Open();
            var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
                @"SELECT u.id AS Id, u.username AS Username, u.display_name AS DisplayName, u.role_id AS RoleId,
                         u.is_active AS IsActive, u.require_pin_change AS RequirePinChange, u.max_discount_percent AS MaxDiscountPercent,
                         r.code AS RoleCode, r.name AS RoleName
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
                @"SELECT u.id AS Id, u.username AS Username, u.display_name AS DisplayName, u.role_id AS RoleId,
                         u.is_active AS IsActive, u.require_pin_change AS RequirePinChange, u.max_discount_percent AS MaxDiscountPercent,
                         r.code AS RoleCode, r.name AS RoleName
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
                @"SELECT id AS Id, username AS Username, display_name AS DisplayName, pin_hash AS PinHash, pin_salt AS PinSalt,
                         role_id AS RoleId, is_active AS IsActive, require_pin_change AS RequirePinChange, max_discount_percent AS MaxDiscountPercent,
                         failed_attempts AS FailedAttempts, lockout_until AS LockoutUntil
                  FROM users WHERE username = @username",
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

        /// <summary>Restituisce true se esiste almeno un utente nel database.</summary>
        public async Task<bool> HasAnyUsersAsync()
        {
            using var conn = _factory.Open();
            var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users").ConfigureAwait(false);
            return count > 0;
        }

        /// <summary>Elenco utenti attivi con ruolo admin. Per autorizzazioni riservate ai soli amministratori.</summary>
        public async Task<IReadOnlyList<UserAccount>> ListAdminUsersAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<UserRow>(
                @"SELECT u.id AS Id, u.username AS Username, u.display_name AS DisplayName, u.role_id AS RoleId,
                         u.is_active AS IsActive, u.require_pin_change AS RequirePinChange, u.max_discount_percent AS MaxDiscountPercent,
                         r.code AS RoleCode, r.name AS RoleName
                  FROM users u
                  INNER JOIN roles r ON r.id = u.role_id
                  WHERE LOWER(r.code) = 'admin' AND u.is_active = 1
                  ORDER BY u.display_name, u.username",
                new { }).ConfigureAwait(false);
            return (rows ?? Enumerable.Empty<UserRow>()).Select(r => MapToAccount(r)).ToList();
        }

        /// <summary>Elenco utenti attivi il cui ruolo ha il permesso richiesto (o SecurityOverride).</summary>
        public async Task<IReadOnlyList<UserAccount>> ListUsersWithPermissionAsync(string requiredPermissionCode)
        {
            if (string.IsNullOrWhiteSpace(requiredPermissionCode)) return Array.Empty<UserAccount>();
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<UserRow>(
                @"SELECT DISTINCT u.id AS Id, u.username AS Username, u.display_name AS DisplayName, u.role_id AS RoleId,
                         u.is_active AS IsActive, u.require_pin_change AS RequirePinChange, u.max_discount_percent AS MaxDiscountPercent,
                         r.code AS RoleCode, r.name AS RoleName
                  FROM users u
                  INNER JOIN roles r ON r.id = u.role_id
                  INNER JOIN role_permissions rp ON rp.role_id = r.id AND (rp.permission_code = @code OR rp.permission_code = @override)
                  WHERE u.is_active = 1
                  ORDER BY u.display_name, u.username",
                new { code = requiredPermissionCode, @override = PermissionCodes.SecurityOverride }).ConfigureAwait(false);
            return (rows ?? Enumerable.Empty<UserRow>()).Select(r => MapToAccount(r)).ToList();
        }

        /// <summary>Restituisce il primo utente admin attivo, se presente.</summary>
        public async Task<UserAccount> GetFirstAdminAsync()
        {
            using var conn = _factory.Open();
            var id = await conn.ExecuteScalarAsync<int?>(
                @"SELECT u.id FROM users u INNER JOIN roles r ON r.id = u.role_id WHERE LOWER(r.code) = 'admin' AND u.is_active = 1 LIMIT 1").ConfigureAwait(false);
            if (!id.HasValue) return null;
            return await GetByIdAsync(id.Value).ConfigureAwait(false);
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

        public async Task<string> GetLastRemoteShopCodeAsync()
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<string>(
                @"SELECT remote_shop_code
                  FROM users
                  WHERE is_active = 1
                    AND remote_shop_code IS NOT NULL
                    AND TRIM(remote_shop_code) <> ''
                  ORDER BY COALESCE(remote_synced_at, updated_at, created_at, 0) DESC, id DESC
                  LIMIT 1").ConfigureAwait(false);
        }

        public async Task<string> FindRemoteStaffUsernameAsync(string shopCode, string staffCode)
        {
            var shop = Normalize(shopCode);
            var staff = Normalize(staffCode);
            if (shop.Length == 0 || staff.Length == 0)
            {
                return null;
            }

            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<string>(
                @"SELECT username
                  FROM users
                  WHERE is_active = 1
                    AND remote_shop_code IS NOT NULL
                    AND remote_staff_code IS NOT NULL
                    AND UPPER(TRIM(remote_shop_code)) = UPPER(@shop)
                    AND UPPER(TRIM(remote_staff_code)) = UPPER(@staff)
                  ORDER BY COALESCE(remote_synced_at, updated_at, created_at, 0) DESC, id DESC
                  LIMIT 1",
                new { shop, staff }).ConfigureAwait(false);
        }

        public async Task<int> UpsertRemoteStaffMirrorAsync(RemoteStaffMirrorInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrWhiteSpace(input.Credential)) throw new ArgumentException("Credential is empty.");
            if (string.IsNullOrWhiteSpace(input.RemoteStaffId)) throw new ArgumentException("Remote staff id is empty.");
            if (string.IsNullOrWhiteSpace(input.StaffCode)) throw new ArgumentException("Staff code is empty.");

            var now = UnixTime.NowSeconds();
            var username = BuildRemoteStaffUsername(input.ShopCode, input.StaffCode);
            var displayName = Normalize(input.DisplayName);
            if (displayName.Length == 0) displayName = Normalize(input.StaffCode);

            var salt = PinHelper.GenerateSalt();
            var hash = PinHelper.HashPin(input.Credential, salt);
            var roleCode = MapRemoteRoleKey(input.RemoteRoleKey);

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var roleId = await conn.ExecuteScalarAsync<int?>(
                    "SELECT id FROM roles WHERE LOWER(code) = @code LIMIT 1",
                    new { code = roleCode },
                    tx).ConfigureAwait(false);

                if (!roleId.HasValue)
                {
                    roleId = await conn.ExecuteScalarAsync<int?>(
                        "SELECT id FROM roles WHERE LOWER(code) = 'cashier' LIMIT 1",
                        transaction: tx).ConfigureAwait(false);
                }

                if (!roleId.HasValue)
                {
                    throw new InvalidOperationException("Ruolo locale cashier non trovato.");
                }

                var existingId = await conn.ExecuteScalarAsync<int?>(
                    @"SELECT id
                      FROM users
                      WHERE remote_staff_id = @remoteStaffId OR username = @username
                      ORDER BY CASE WHEN remote_staff_id = @remoteStaffId THEN 0 ELSE 1 END
                      LIMIT 1",
                    new
                    {
                        remoteStaffId = Normalize(input.RemoteStaffId),
                        username
                    },
                    tx).ConfigureAwait(false);

                if (existingId.HasValue)
                {
                    await conn.ExecuteAsync(@"
UPDATE users
SET username = @username,
    display_name = @displayName,
    pin_hash = @pinHash,
    pin_salt = @pinSalt,
    role_id = @roleId,
    is_active = 1,
    require_pin_change = 0,
    failed_attempts = 0,
    lockout_until = NULL,
    remote_staff_id = @remoteStaffId,
    remote_staff_code = @remoteStaffCode,
    remote_shop_id = @remoteShopId,
    remote_shop_code = @remoteShopCode,
    remote_role_key = @remoteRoleKey,
    remote_credential_version = @remoteCredentialVersion,
    remote_synced_at = @now,
    updated_at = @now
WHERE id = @id",
                        new
                        {
                            id = existingId.Value,
                            username,
                            displayName,
                            pinHash = hash,
                            pinSalt = salt,
                            roleId = roleId.Value,
                            remoteStaffId = Normalize(input.RemoteStaffId),
                            remoteStaffCode = Normalize(input.StaffCode),
                            remoteShopId = Normalize(input.RemoteShopId),
                            remoteShopCode = Normalize(input.ShopCode),
                            remoteRoleKey = Normalize(input.RemoteRoleKey),
                            remoteCredentialVersion = input.CredentialVersion,
                            now
                        },
                        tx).ConfigureAwait(false);

                    tx.Commit();
                    return existingId.Value;
                }

                await conn.ExecuteAsync(@"
INSERT INTO users(
    username,
    display_name,
    pin_hash,
    pin_salt,
    role_id,
    is_active,
    require_pin_change,
    max_discount_percent,
    created_at,
    updated_at,
    failed_attempts,
    lockout_until,
    remote_staff_id,
    remote_staff_code,
    remote_shop_id,
    remote_shop_code,
    remote_role_key,
    remote_credential_version,
    remote_synced_at)
VALUES(
    @username,
    @displayName,
    @pinHash,
    @pinSalt,
    @roleId,
    1,
    0,
    0,
    @now,
    @now,
    0,
    NULL,
    @remoteStaffId,
    @remoteStaffCode,
    @remoteShopId,
    @remoteShopCode,
    @remoteRoleKey,
    @remoteCredentialVersion,
    @now)",
                    new
                    {
                        username,
                        displayName,
                        pinHash = hash,
                        pinSalt = salt,
                        roleId = roleId.Value,
                        remoteStaffId = Normalize(input.RemoteStaffId),
                        remoteStaffCode = Normalize(input.StaffCode),
                        remoteShopId = Normalize(input.RemoteShopId),
                        remoteShopCode = Normalize(input.ShopCode),
                        remoteRoleKey = Normalize(input.RemoteRoleKey),
                        remoteCredentialVersion = input.CredentialVersion,
                        now
                    },
                    tx).ConfigureAwait(false);

                var createdId = await conn.ExecuteScalarAsync<int>(
                    "SELECT last_insert_rowid()",
                    transaction: tx).ConfigureAwait(false);

                tx.Commit();
                return createdId;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
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

        private static string BuildRemoteStaffUsername(string shopCode, string staffCode)
        {
            var shop = NormalizeForUsername(shopCode);
            var staff = NormalizeForUsername(staffCode);

            if (shop.Length == 0) shop = "shop";
            if (staff.Length == 0) staff = "staff";

            return ("pos_" + shop + "_" + staff).ToLowerInvariant();
        }

        private static string MapRemoteRoleKey(string roleKey)
        {
            var normalized = Normalize(roleKey).ToLowerInvariant();

            switch (normalized)
            {
                case "admin":
                case "shop_owner":
                    return "admin";
                case "manager":
                case "shop_manager":
                    return "manager";
                case "supervisor":
                    return "supervisor";
                case "cashier":
                    return "cashier";
                default:
                    return "cashier";
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string NormalizeForUsername(string value)
        {
            var normalized = Normalize(value);
            var chars = normalized
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                .Take(48)
                .ToArray();

            return new string(chars);
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

    public sealed class RemoteStaffMirrorInput
    {
        public string Credential { get; set; }
        public int CredentialVersion { get; set; }
        public string DisplayName { get; set; }
        public string RemoteRoleKey { get; set; }
        public string RemoteShopId { get; set; }
        public string RemoteStaffId { get; set; }
        public string ShopCode { get; set; }
        public string StaffCode { get; set; }
    }
}
