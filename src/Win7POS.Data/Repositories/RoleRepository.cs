using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Security;

namespace Win7POS.Data.Repositories
{
    public sealed class RoleRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public RoleRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<IReadOnlyList<UserRole>> GetAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<UserRole>(
                "SELECT id AS Id, code AS Code, name AS Name, is_system AS IsSystem FROM roles ORDER BY id").ConfigureAwait(false);
            return (rows ?? Enumerable.Empty<UserRole>()).ToList();
        }

        public async Task<UserRole> GetByIdAsync(int id)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<UserRole>(
                "SELECT id AS Id, code AS Code, name AS Name, is_system AS IsSystem FROM roles WHERE id = @id",
                new { id }).ConfigureAwait(false);
        }

        public async Task<UserRole> GetByCodeAsync(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<UserRole>(
                "SELECT id AS Id, code AS Code, name AS Name, is_system AS IsSystem FROM roles WHERE code = @code",
                new { code }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<string>> GetPermissionCodesAsync(int roleId)
        {
            using var conn = _factory.Open();
            var list = await conn.QueryAsync<string>(
                "SELECT permission_code FROM role_permissions WHERE role_id = @roleId ORDER BY permission_code",
                new { roleId }).ConfigureAwait(false);
            return (list ?? Enumerable.Empty<string>()).ToList();
        }

        public async Task SetPermissionsAsync(int roleId, IReadOnlyList<string> permissionCodes)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    "DELETE FROM role_permissions WHERE role_id = @roleId",
                    new { roleId }, tx).ConfigureAwait(false);
                foreach (var code in permissionCodes ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    await conn.ExecuteAsync(
                        "INSERT INTO role_permissions(role_id, permission_code) VALUES(@roleId, @code)",
                        new { roleId, code }, tx).ConfigureAwait(false);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public async Task<int> CreateAsync(string code, string name)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Code e nome obbligatori.");
            code = code.Trim().ToLowerInvariant();
            name = name.Trim();
            using var conn = _factory.Open();
            await conn.ExecuteAsync(
                "INSERT INTO roles(code, name, is_system) VALUES(@code, @name, 0)",
                new { code, name }).ConfigureAwait(false);
            return await conn.ExecuteScalarAsync<int>("SELECT last_insert_rowid()").ConfigureAwait(false);
        }

        public async Task DuplicateAsync(int sourceRoleId, string newCode, string newName)
        {
            var perms = await GetPermissionCodesAsync(sourceRoleId).ConfigureAwait(false);
            var newId = await CreateAsync(newCode, newName).ConfigureAwait(false);
            await SetPermissionsAsync(newId, perms).ConfigureAwait(false);
        }

        public async Task UpdateNameAsync(int roleId, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome obbligatorio.");
            var role = await GetByIdAsync(roleId).ConfigureAwait(false);
            if (role == null) throw new InvalidOperationException("Ruolo non trovato.");
            if (role.IsSystem) throw new InvalidOperationException("Non è possibile rinominare un ruolo di sistema.");
            name = name.Trim();
            using var conn = _factory.Open();
            await conn.ExecuteAsync("UPDATE roles SET name = @name WHERE id = @id", new { name, id = roleId }).ConfigureAwait(false);
        }

        public async Task<int> CountUsersWithRoleAsync(int roleId)
        {
            using var conn = _factory.Open();
            return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM users WHERE role_id = @roleId", new { roleId }).ConfigureAwait(false);
        }

        public async Task DeleteAsync(int roleId)
        {
            var role = await GetByIdAsync(roleId).ConfigureAwait(false);
            if (role == null) throw new InvalidOperationException("Ruolo non trovato.");
            if (role.IsSystem) throw new InvalidOperationException("Non è possibile eliminare un ruolo di sistema.");
            var count = await CountUsersWithRoleAsync(roleId).ConfigureAwait(false);
            if (count > 0) throw new InvalidOperationException("Impossibile eliminare il ruolo: " + count + " utente/i assegnati. Assegna un altro ruolo agli utenti.");
            using var conn = _factory.Open();
            await conn.ExecuteAsync("DELETE FROM role_permissions WHERE role_id = @roleId", new { roleId }).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM roles WHERE id = @roleId", new { roleId }).ConfigureAwait(false);
        }
    }
}
