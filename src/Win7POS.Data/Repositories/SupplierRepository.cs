using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Repositories
{
    public sealed class SupplierRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public SupplierRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<IReadOnlyList<SupplierListItem>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SupplierListItem>(@"
SELECT id AS Id, name AS Name
FROM suppliers
WHERE COALESCE(is_active, 1) = 1
ORDER BY name ASC;").ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<bool> UpsertRemoteAsync(
            string remoteSupplierId,
            string name,
            string remoteUpdatedAt)
        {
            var remoteId = Normalize(remoteSupplierId);
            var normalizedName = Normalize(name);
            var updatedAt = Normalize(remoteUpdatedAt);
            if (remoteId.Length == 0 || normalizedName.Length == 0)
            {
                return false;
            }

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var row = await conn.QueryFirstOrDefaultAsync<RemoteSupplierRow>(@"
SELECT id AS Id,
       remote_updated_at AS RemoteUpdatedAt,
       remote_deleted_at AS RemoteDeletedAt,
       is_active AS IsActive
FROM suppliers
WHERE remote_supplier_id = @remoteId
LIMIT 1;",
                    new { remoteId },
                    tx).ConfigureAwait(false);

                if (row == null)
                {
                    row = await conn.QueryFirstOrDefaultAsync<RemoteSupplierRow>(@"
SELECT id AS Id,
       remote_updated_at AS RemoteUpdatedAt,
       remote_deleted_at AS RemoteDeletedAt,
       is_active AS IsActive
FROM suppliers
WHERE remote_supplier_id IS NULL
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
                        tx.Commit();
                        return false;
                    }

                    await conn.ExecuteAsync(@"
UPDATE suppliers
SET name = @name,
    remote_supplier_id = @remoteId,
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
INSERT INTO suppliers(name, remote_supplier_id, remote_updated_at, remote_deleted_at, is_active)
VALUES(@name, @remoteId, NULLIF(@updatedAt, ''), NULL, 1);",
                        new { name = normalizedName, remoteId, updatedAt },
                        tx).ConfigureAwait(false);
                }

                tx.Commit();
                return true;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public async Task<bool> ApplyRemoteTombstoneAsync(
            string remoteSupplierId,
            string remoteDeletedAt,
            string remoteUpdatedAt)
        {
            var remoteId = Normalize(remoteSupplierId);
            if (remoteId.Length == 0)
            {
                return false;
            }

            var deletedAt = Normalize(remoteDeletedAt);
            if (deletedAt.Length == 0) deletedAt = DateTimeOffset.UtcNow.ToString("O");
            var updatedAt = Normalize(remoteUpdatedAt);
            if (updatedAt.Length == 0) updatedAt = deletedAt;

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var row = await conn.QueryFirstOrDefaultAsync<RemoteSupplierRow>(@"
SELECT id AS Id,
       remote_updated_at AS RemoteUpdatedAt,
       remote_deleted_at AS RemoteDeletedAt,
       is_active AS IsActive
FROM suppliers
WHERE remote_supplier_id = @remoteId
LIMIT 1;",
                    new { remoteId },
                    tx).ConfigureAwait(false);

                if (row != null && IsOlder(updatedAt, row.RemoteUpdatedAt))
                {
                    tx.Commit();
                    return false;
                }

                if (row == null)
                {
                    await conn.ExecuteAsync(@"
INSERT INTO suppliers(name, remote_supplier_id, remote_updated_at, remote_deleted_at, is_active)
VALUES('(remote supplier removed)', @remoteId, @updatedAt, @deletedAt, 0);",
                        new { remoteId, updatedAt, deletedAt },
                        tx).ConfigureAwait(false);
                }
                else
                {
                    await conn.ExecuteAsync(@"
UPDATE suppliers
SET remote_updated_at = @updatedAt,
    remote_deleted_at = @deletedAt,
    is_active = 0
WHERE id = @id;",
                        new { id = row.Id, updatedAt, deletedAt },
                        tx).ConfigureAwait(false);
                }

                tx.Commit();
                return true;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
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

        private sealed class RemoteSupplierRow
        {
            public int Id { get; set; }
            public int IsActive { get; set; }
            public string RemoteDeletedAt { get; set; }
            public string RemoteUpdatedAt { get; set; }
        }
    }

    public sealed class SupplierListItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
