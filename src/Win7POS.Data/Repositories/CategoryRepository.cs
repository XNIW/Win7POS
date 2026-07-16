using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Repositories
{
    public sealed class CategoryRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public CategoryRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<IReadOnlyList<CategoryListItem>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<CategoryListItem>(@"
SELECT id AS Id, name AS Name
FROM categories
WHERE COALESCE(is_active, 1) = 1
ORDER BY name ASC;").ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<bool> UpsertRemoteAsync(
            string remoteCategoryId,
            string name,
            string remoteUpdatedAt)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var applied = await UpsertRemoteInTransactionAsync(
                    conn,
                    tx,
                    remoteCategoryId,
                    name,
                    remoteUpdatedAt).ConfigureAwait(false);
                tx.Commit();
                return applied;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        internal static async Task<bool> UpsertRemoteInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteCategoryId,
            string name,
            string remoteUpdatedAt)
        {
            var remoteId = Normalize(remoteCategoryId);
            var normalizedName = Normalize(name);
            var updatedAt = Normalize(remoteUpdatedAt);
            if (remoteId.Length == 0 || normalizedName.Length == 0)
            {
                return false;
            }

            var row = await conn.QueryFirstOrDefaultAsync<RemoteCategoryRow>(@"
SELECT id AS Id,
       remote_updated_at AS RemoteUpdatedAt,
       remote_deleted_at AS RemoteDeletedAt,
       is_active AS IsActive
FROM categories
WHERE remote_category_id = @remoteId
LIMIT 1;",
                new { remoteId },
                tx).ConfigureAwait(false);

            if (row == null)
            {
                row = await conn.QueryFirstOrDefaultAsync<RemoteCategoryRow>(@"
SELECT id AS Id,
       remote_updated_at AS RemoteUpdatedAt,
       remote_deleted_at AS RemoteDeletedAt,
       is_active AS IsActive
FROM categories
WHERE remote_category_id IS NULL
  AND LOWER(TRIM(name)) = LOWER(@name)
ORDER BY id ASC
LIMIT 1;",
                    new { name = normalizedName },
                    tx).ConfigureAwait(false);
            }

            if (row != null)
            {
                if (IsOlder(updatedAt, row.RemoteUpdatedAt) ||
                    (row.IsActive == 0 && IsNotNewerThanTombstone(updatedAt, row.RemoteDeletedAt)))
                {
                    return false;
                }

                await conn.ExecuteAsync(@"
UPDATE categories
SET name = @name,
    remote_category_id = @remoteId,
    remote_updated_at = NULLIF(@updatedAt, ''),
    remote_deleted_at = NULL,
    is_active = 1
WHERE id = @id;",
                    new { id = row.Id, name = normalizedName, remoteId, updatedAt },
                    tx).ConfigureAwait(false);
            }
            else
            {
                await conn.ExecuteAsync(@"
INSERT INTO categories(name, remote_category_id, remote_updated_at, remote_deleted_at, is_active)
VALUES(@name, @remoteId, NULLIF(@updatedAt, ''), NULL, 1);",
                    new { name = normalizedName, remoteId, updatedAt },
                    tx).ConfigureAwait(false);
            }

            return true;
        }

        public async Task<bool> ApplyRemoteTombstoneAsync(
            string remoteCategoryId,
            string remoteDeletedAt,
            string remoteUpdatedAt)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var applied = await ApplyRemoteTombstoneInTransactionAsync(
                    conn,
                    tx,
                    remoteCategoryId,
                    remoteDeletedAt,
                    remoteUpdatedAt).ConfigureAwait(false);
                tx.Commit();
                return applied;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        internal static async Task<bool> ApplyRemoteTombstoneInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteCategoryId,
            string remoteDeletedAt,
            string remoteUpdatedAt)
        {
            var remoteId = Normalize(remoteCategoryId);
            if (remoteId.Length == 0)
            {
                return false;
            }

            var deletedAt = Normalize(remoteDeletedAt);
            if (deletedAt.Length == 0) deletedAt = DateTimeOffset.UtcNow.ToString("O");
            var updatedAt = Normalize(remoteUpdatedAt);
            if (updatedAt.Length == 0) updatedAt = deletedAt;

            var row = await conn.QueryFirstOrDefaultAsync<RemoteCategoryRow>(@"
SELECT id AS Id,
       remote_updated_at AS RemoteUpdatedAt,
       remote_deleted_at AS RemoteDeletedAt,
       is_active AS IsActive
FROM categories
WHERE remote_category_id = @remoteId
LIMIT 1;",
                new { remoteId },
                tx).ConfigureAwait(false);

            if (row != null && IsOlder(updatedAt, row.RemoteUpdatedAt))
            {
                return false;
            }

            if (row == null)
            {
                await conn.ExecuteAsync(@"
INSERT INTO categories(name, remote_category_id, remote_updated_at, remote_deleted_at, is_active)
VALUES('(remote category removed)', @remoteId, @updatedAt, @deletedAt, 0);",
                    new { remoteId, updatedAt, deletedAt },
                    tx).ConfigureAwait(false);
            }
            else
            {
                await conn.ExecuteAsync(@"
UPDATE categories
SET remote_updated_at = @updatedAt,
    remote_deleted_at = @deletedAt,
    is_active = 0
WHERE id = @id;",
                    new { id = row.Id, updatedAt, deletedAt },
                    tx).ConfigureAwait(false);
            }

            return true;
        }

        private static bool IsOlder(string incoming, string current)
        {
            var normalizedCurrent = Normalize(current);
            if (normalizedCurrent.Length == 0) return false;
            var normalizedIncoming = Normalize(incoming);
            if (normalizedIncoming.Length == 0) return true;

            if (DateTimeOffset.TryParse(normalizedIncoming, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var incomingTime) &&
                DateTimeOffset.TryParse(normalizedCurrent, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var currentTime))
            {
                return incomingTime < currentTime;
            }

            return string.CompareOrdinal(normalizedIncoming, normalizedCurrent) < 0;
        }

        private static bool IsNotNewerThanTombstone(string incoming, string tombstone)
        {
            var normalizedIncoming = Normalize(incoming);
            var normalizedTombstone = Normalize(tombstone);
            if (normalizedTombstone.Length == 0) return false;
            if (normalizedIncoming.Length == 0) return true;

            if (DateTimeOffset.TryParse(normalizedIncoming, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var incomingTime) &&
                DateTimeOffset.TryParse(normalizedTombstone, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var tombstoneTime))
            {
                return incomingTime <= tombstoneTime;
            }

            return string.CompareOrdinal(normalizedIncoming, normalizedTombstone) <= 0;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class RemoteCategoryRow
        {
            public int Id { get; set; }
            public int IsActive { get; set; }
            public string RemoteDeletedAt { get; set; }
            public string RemoteUpdatedAt { get; set; }
        }
    }

    public sealed class CategoryListItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
