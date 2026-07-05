using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Online
{
    public sealed class CatalogImportOutboxRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public CatalogImportOutboxRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<long> EnqueueAsync(CatalogImportOutboxEntry entry)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var id = await EnqueueAsync(conn, tx, entry).ConfigureAwait(false);
                tx.Commit();
                return id;
            }
        }

        public static async Task<long> EnqueueAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            CatalogImportOutboxEntry entry)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.ClientImportId)) throw new ArgumentException("client import id is required.");
            if (string.IsNullOrWhiteSpace(entry.IdempotencyKey)) throw new ArgumentException("idempotency key is required.");
            if (string.IsNullOrWhiteSpace(entry.PayloadJson)) throw new ArgumentException("payload json is required.");
            if (string.IsNullOrWhiteSpace(entry.PayloadHash)) throw new ArgumentException("payload hash is required.");

            var nowMs = entry.CreatedAt > 0 ? entry.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO catalog_import_outbox(
  client_import_id, idempotency_key, schema_version, source, payload_json, payload_hash,
  status, attempt_count, next_retry_at, created_at, updated_at)
VALUES(
  @ClientImportId, @IdempotencyKey, @SchemaVersion, @Source, @PayloadJson, @PayloadHash,
  'pending', 0, 0, @NowMs, @NowMs);",
                new
                {
                    entry.ClientImportId,
                    entry.IdempotencyKey,
                    entry.SchemaVersion,
                    entry.Source,
                    entry.PayloadJson,
                    entry.PayloadHash,
                    NowMs = nowMs
                },
                tx).ConfigureAwait(false);

            var existing = await conn.QuerySingleAsync<CatalogImportExistingRow>(@"
SELECT
  id AS Id,
  client_import_id AS ClientImportId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  source AS Source,
  payload_hash AS PayloadHash
FROM catalog_import_outbox
WHERE client_import_id = @ClientImportId
   OR idempotency_key = @IdempotencyKey
ORDER BY id ASC
LIMIT 1;",
                new { entry.ClientImportId, entry.IdempotencyKey },
                tx).ConfigureAwait(false);

            if (!string.Equals(existing.ClientImportId, entry.ClientImportId, StringComparison.Ordinal) ||
                !string.Equals(existing.IdempotencyKey, entry.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existing.SchemaVersion, entry.SchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(existing.Source, entry.Source, StringComparison.Ordinal) ||
                !string.Equals(existing.PayloadHash, entry.PayloadHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Catalog import outbox idempotency conflict.");
            }

            return existing.Id;
        }

        public async Task<IReadOnlyList<CatalogImportOutboxItem>> GetPendingAsync(int take, long nowMs)
        {
            if (take <= 0) take = 1;
            if (take > 50) take = 50;

            using (var conn = _factory.Open())
            {
                var rows = await conn.QueryAsync<CatalogImportOutboxItem>(@"
SELECT
  id AS Id,
  client_import_id AS ClientImportId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  source AS Source,
  payload_json AS PayloadJson,
  payload_hash AS PayloadHash,
  status AS Status,
  attempt_count AS AttemptCount,
  next_retry_at AS NextRetryAt,
  last_error_code AS LastErrorCode
FROM catalog_import_outbox
WHERE status IN ('pending', 'retry', 'in_progress')
  AND next_retry_at <= @nowMs
ORDER BY id ASC
LIMIT @take;",
                    new { take, nowMs }).ConfigureAwait(false);
                return rows.ToList();
            }
        }

        public async Task<CatalogImportOutboxSummary> GetSummaryAsync()
        {
            using (var conn = _factory.Open())
            {
                var rows = (await conn.QueryAsync<CatalogImportStatusCount>(@"
SELECT status AS Status, COUNT(1) AS Count
FROM catalog_import_outbox
GROUP BY status;").ConfigureAwait(false)).ToList();

                var lastAckedAt = await conn.ExecuteScalarAsync<long?>(@"
SELECT updated_at
FROM catalog_import_outbox
WHERE status = 'acked'
ORDER BY updated_at DESC
LIMIT 1;").ConfigureAwait(false);

                long CountFor(string status)
                {
                    return rows
                        .Where(row => string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase))
                        .Select(row => row.Count)
                        .DefaultIfEmpty(0)
                        .Sum();
                }

                return new CatalogImportOutboxSummary
                {
                    Acked = CountFor("acked"),
                    Blocked = CountFor("failed_blocked"),
                    InProgress = CountFor("in_progress"),
                    LastAckedAt = lastAckedAt,
                    Pending = CountFor("pending"),
                    Retry = CountFor("retry")
                };
            }
        }

        public async Task<bool> HasUnresolvedAsync()
        {
            using (var conn = _factory.Open())
            {
                var count = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_import_outbox
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked');").ConfigureAwait(false);
                return count > 0;
            }
        }

        public async Task PrepareAttemptAsync(long outboxId, long nowMs)
        {
            using (var conn = _factory.Open())
            {
                await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'in_progress',
    attempt_count = attempt_count + 1,
    last_attempt_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId
  AND status IN ('pending', 'retry', 'in_progress');",
                    new { outboxId, nowMs }).ConfigureAwait(false);
            }
        }

        public async Task MarkAckedAsync(long outboxId, string serverImportId, long nowMs)
        {
            using (var conn = _factory.Open())
            {
                await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'acked',
    server_import_id = @serverImportId,
    last_error_code = NULL,
    last_error_at = NULL,
    updated_at = @nowMs
WHERE id = @outboxId;",
                    new { outboxId, serverImportId, nowMs }).ConfigureAwait(false);
            }
        }

        public async Task MarkRetryAsync(long outboxId, string errorCode, long nextRetryAt, long nowMs)
        {
            using (var conn = _factory.Open())
            {
                await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'retry',
    next_retry_at = @nextRetryAt,
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId;",
                    new { outboxId, errorCode, nextRetryAt, nowMs }).ConfigureAwait(false);
            }
        }

        public async Task MarkBlockedAsync(long outboxId, string errorCode, long nowMs)
        {
            using (var conn = _factory.Open())
            {
                await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'failed_blocked',
    last_error_code = @errorCode,
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @outboxId;",
                    new { outboxId, errorCode, nowMs }).ConfigureAwait(false);
            }
        }

        private sealed class CatalogImportStatusCount
        {
            public long Count { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        private sealed class CatalogImportExistingRow
        {
            public string ClientImportId { get; set; } = string.Empty;
            public long Id { get; set; }
            public string IdempotencyKey { get; set; } = string.Empty;
            public string PayloadHash { get; set; } = string.Empty;
            public string SchemaVersion { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
        }
    }

    public sealed class CatalogImportOutboxEntry
    {
        public string ClientImportId { get; set; } = string.Empty;
        public long CreatedAt { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public sealed class CatalogImportOutboxItem
    {
        public int AttemptCount { get; set; }
        public string ClientImportId { get; set; } = string.Empty;
        public long Id { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string LastErrorCode { get; set; } = string.Empty;
        public long NextRetryAt { get; set; }
        public string PayloadHash { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public sealed class CatalogImportOutboxSummary
    {
        public long Acked { get; set; }
        public long Blocked { get; set; }
        public long InProgress { get; set; }
        public long? LastAckedAt { get; set; }
        public long Pending { get; set; }
        public long PendingOrRetry => Pending + Retry + InProgress;
        public long Retry { get; set; }
    }
}
