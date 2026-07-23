using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Repositories
{
    internal sealed class RemotePriceHistoryRepository
    {
        private const int PendingRemotePriceReplayBatchSize = 2000;
        private const int RemotePriceStageRowsPerCommand = 100;
        private readonly SqliteConnectionFactory _factory;

        internal RemotePriceHistoryRepository(SqliteConnectionFactory factory)
        {
            _factory = factory;
        }

        private sealed class PendingRemotePriceRow
        {
            public long Id { get; set; }
            public string Barcode { get; set; } = string.Empty;
            public string EffectiveAt { get; set; } = string.Empty;
            public int Price { get; set; }
            public string RemotePriceId { get; set; } = string.Empty;
            public string RemoteProductId { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }

        private sealed class RemotePriceHistoryRow
        {
            public string Barcode { get; set; } = string.Empty;
            public string EffectiveAt { get; set; } = string.Empty;
            public int Price { get; set; }
            public string Source { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }

        private enum RemotePriceIdEvidenceState
        {
            Collision,
            Applied,
            Queued
        }

        private sealed class RemotePriceIdEvidence
        {
            public string EffectiveAt { get; set; } = string.Empty;
            public long? PendingId { get; set; }
            public RemotePriceIdEvidenceState State { get; set; }
        }

        private sealed class RemotePriceStageRow
        {
            public string EffectiveAt { get; set; } = string.Empty;
            public int Ordinal { get; set; }
            public int Price { get; set; }
            public string RemotePriceId { get; set; } = string.Empty;
            public string RemoteProductId { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }

        internal sealed class RemotePriceBatchApplyResult
        {
            public int Applied { get; set; }
            public int Queued { get; set; }
            public int Skipped { get; set; }
        }

        internal sealed class PendingRemotePriceReplayResult
        {
            public int Applied { get; set; }
            public HashSet<long> CollisionIds { get; } = new HashSet<long>();
        }

        internal static async Task<RemotePriceBatchApplyResult> TryApplyRemotePricesSetBasedInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<RemoteCatalogPriceWrite> prices,
            RemotePriceApplyDiagnostics diagnostics = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            var input = prices ?? Array.Empty<RemoteCatalogPriceWrite>();
            if (input.Count == 0)
            {
                return new RemotePriceBatchApplyResult();
            }

            var staged = new List<RemotePriceStageRow>(input.Count);
            var skipped = 0;
            for (var index = 0; index < input.Count; index += 1)
            {
                var price = input[index];
                if (price == null)
                {
                    skipped += 1;
                    continue;
                }

                var remoteProductId = (price.RemoteProductId ?? string.Empty).Trim();
                var remotePriceId = (price.RemotePriceId ?? string.Empty).Trim();
                var type = (price.Type ?? string.Empty).Trim().ToUpperInvariant();
                if (remoteProductId.Length == 0 || type.Length == 0 || price.Price < 0)
                {
                    skipped += 1;
                    continue;
                }

                // Rows without an immutable remote price id, or without an explicit
                // effective timestamp, retain the legacy per-row path. Those shapes
                // cannot be classified safely by the page-level evidence lookup.
                if (remotePriceId.Length == 0 || string.IsNullOrWhiteSpace(price.EffectiveAt))
                {
                    diagnostics?.RecordFallbackPage();
                    return null;
                }

                staged.Add(new RemotePriceStageRow
                {
                    EffectiveAt = price.EffectiveAt.Trim(),
                    Ordinal = index,
                    Price = price.Price,
                    RemotePriceId = remotePriceId,
                    RemoteProductId = remoteProductId,
                    Source = string.IsNullOrWhiteSpace(price.Source)
                        ? "remote_catalog"
                        : price.Source.Trim(),
                    Type = type
                });
            }

            if (staged.Count == 0)
            {
                return new RemotePriceBatchApplyResult { Skipped = skipped };
            }

            RecordSqlCommand(diagnostics);
            await conn.ExecuteAsync(
                "DELETE FROM temp_catalog_page_remote_prices;",
                transaction: tx).ConfigureAwait(false);

            await InsertRemotePriceStageRowsAsync(
                conn,
                tx,
                staged,
                diagnostics).ConfigureAwait(false);

            RecordSqlCommand(diagnostics);
            var setBasedShapeSupported = await conn.ExecuteScalarAsync<long>(@"
WITH canonical_products AS (
  SELECT product.remote_product_id, MIN(product.id) AS product_id
  FROM products product
  JOIN (
    SELECT DISTINCT remote_product_id
    FROM temp_catalog_page_remote_prices
  ) staged_product
    ON staged_product.remote_product_id = product.remote_product_id
  WHERE COALESCE(product.is_active, 1) = 1
    AND COALESCE(product.remote_product_id, '') <> ''
  GROUP BY product.remote_product_id
),
canonical_stage AS (
  SELECT staged.*
  FROM temp_catalog_page_remote_prices staged
  JOIN (
    SELECT remote_price_id, MIN(ordinal) AS ordinal
    FROM temp_catalog_page_remote_prices
    GROUP BY remote_price_id
  ) first_row
    ON first_row.remote_price_id = staged.remote_price_id
   AND first_row.ordinal = staged.ordinal
)
SELECT CASE WHEN
  EXISTS (
    SELECT 1
    FROM temp_catalog_page_remote_prices
    GROUP BY remote_price_id
    HAVING COUNT(DISTINCT remote_product_id) > 1
        OR COUNT(DISTINCT type) > 1
        OR COUNT(DISTINCT price) > 1
        OR COUNT(DISTINCT effective_at) > 1
        OR COUNT(DISTINCT source) > 1
  )
  OR EXISTS (
    SELECT 1
    FROM temp_catalog_page_remote_prices staged
    JOIN canonical_products canonical
      ON canonical.remote_product_id = staged.remote_product_id
    JOIN products product
      ON product.id = canonical.product_id
    GROUP BY product.barcode, staged.type, staged.price, staged.effective_at, staged.source
    HAVING COUNT(DISTINCT staged.remote_price_id) > 1
  )
  OR EXISTS (
    SELECT 1
    FROM canonical_stage staged
    LEFT JOIN canonical_products canonical
      ON canonical.remote_product_id = staged.remote_product_id
    LEFT JOIN remote_catalog_price_ownership ownership
      ON ownership.remote_price_id = staged.remote_price_id
    LEFT JOIN product_price_history history
      ON history.remote_price_id = staged.remote_price_id
    WHERE canonical.product_id IS NULL
       OR EXISTS (
            SELECT 1
            FROM remote_catalog_pending_prices pending
            WHERE pending.remote_price_id = staged.remote_price_id
          )
       OR (
            ownership.remote_price_id IS NOT NULL
            AND TRIM(COALESCE(ownership.remote_product_id, '')) <> staged.remote_product_id
          )
       OR (
            history.remote_price_id IS NOT NULL
            AND (
                 ownership.remote_price_id IS NULL
                 OR history.type <> staged.type
                 OR history.new_price <> staged.price
                 OR history.timestamp <> staged.effective_at
                 OR COALESCE(history.source, '') <> staged.source
            )
          )
       OR EXISTS (
            SELECT 1
            FROM product_price_history tuple_history
            JOIN products tuple_product
              ON tuple_product.barcode = tuple_history.barcode
             AND tuple_product.id = canonical.product_id
            WHERE tuple_history.timestamp = staged.effective_at
              AND tuple_history.type = staged.type
              AND tuple_history.new_price = staged.price
              AND COALESCE(tuple_history.source, '') = staged.source
              AND COALESCE(tuple_history.remote_price_id, '') <> staged.remote_price_id
          )
  )
THEN 0 ELSE 1 END;",
                transaction: tx).ConfigureAwait(false);

            if (setBasedShapeSupported != 1)
            {
                RecordSqlCommand(diagnostics);
                await conn.ExecuteAsync(
                    "DELETE FROM temp_catalog_page_remote_prices;",
                    transaction: tx).ConfigureAwait(false);
                diagnostics?.RecordFallbackPage();
                return null;
            }

            RecordSqlCommand(diagnostics, statementCount: 2);
            await conn.ExecuteAsync(@"
WITH canonical_stage AS (
  SELECT staged.*
  FROM temp_catalog_page_remote_prices staged
  JOIN (
    SELECT remote_price_id, MIN(ordinal) AS ordinal
    FROM temp_catalog_page_remote_prices
    GROUP BY remote_price_id
  ) first_row
    ON first_row.remote_price_id = staged.remote_price_id
   AND first_row.ordinal = staged.ordinal
),
canonical_products AS (
  SELECT product.remote_product_id, MIN(product.id) AS product_id
  FROM products product
  JOIN (
    SELECT DISTINCT remote_product_id
    FROM temp_catalog_page_remote_prices
  ) staged_product
    ON staged_product.remote_product_id = product.remote_product_id
  WHERE COALESCE(product.is_active, 1) = 1
    AND COALESCE(product.remote_product_id, '') <> ''
  GROUP BY product.remote_product_id
)
INSERT OR IGNORE INTO product_price_history(
  barcode,
  timestamp,
  type,
  old_price,
  new_price,
  source,
  remote_price_id)
SELECT
  product.barcode,
  staged.effective_at,
  staged.type,
  NULL,
  staged.price,
  staged.source,
  staged.remote_price_id
FROM canonical_stage staged
JOIN canonical_products canonical
  ON canonical.remote_product_id = staged.remote_product_id
JOIN products product
  ON product.id = canonical.product_id;

INSERT OR IGNORE INTO remote_catalog_price_ownership(
  remote_price_id,
  remote_product_id)
SELECT DISTINCT remote_price_id, remote_product_id
FROM temp_catalog_page_remote_prices;",
                transaction: tx).ConfigureAwait(false);

            RecordSqlCommand(diagnostics);
            var invalidAppliedRows = await conn.ExecuteScalarAsync<long>(@"
WITH canonical_stage AS (
  SELECT staged.*
  FROM temp_catalog_page_remote_prices staged
  JOIN (
    SELECT remote_price_id, MIN(ordinal) AS ordinal
    FROM temp_catalog_page_remote_prices
    GROUP BY remote_price_id
  ) first_row
    ON first_row.remote_price_id = staged.remote_price_id
   AND first_row.ordinal = staged.ordinal
)
SELECT COUNT(1)
FROM canonical_stage staged
LEFT JOIN remote_catalog_price_ownership ownership
  ON ownership.remote_price_id = staged.remote_price_id
LEFT JOIN product_price_history history
  ON history.remote_price_id = staged.remote_price_id
WHERE ownership.remote_price_id IS NULL
   OR TRIM(COALESCE(ownership.remote_product_id, '')) <> staged.remote_product_id
   OR history.remote_price_id IS NULL
   OR history.type <> staged.type
   OR history.new_price <> staged.price
   OR history.timestamp <> staged.effective_at
   OR COALESCE(history.source, '') <> staged.source;",
                transaction: tx).ConfigureAwait(false);
            if (invalidAppliedRows != 0)
            {
                throw new InvalidOperationException("catalog_remote_price_set_apply_verification_failed");
            }

            RecordSqlCommand(diagnostics);
            await conn.ExecuteAsync(
                "DELETE FROM temp_catalog_page_remote_prices;",
                transaction: tx).ConfigureAwait(false);
            diagnostics?.RecordSetBasedPage(staged.Count);

            return new RemotePriceBatchApplyResult
            {
                Applied = staged.Count,
                Skipped = skipped
            };
        }

        private static async Task InsertRemotePriceStageRowsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<RemotePriceStageRow> rows,
            RemotePriceApplyDiagnostics diagnostics)
        {
            SqliteCommand command = null;
            var preparedRowCount = 0;
            try
            {
                for (var offset = 0; offset < rows.Count;)
                {
                    var rowCount = Math.Min(RemotePriceStageRowsPerCommand, rows.Count - offset);
                    if (command == null || preparedRowCount != rowCount)
                    {
                        command?.Dispose();
                        command = CreateRemotePriceStageInsertCommand(conn, tx, rowCount);
                        command.Prepare();
                        diagnostics?.RecordPreparedCommand();
                        preparedRowCount = rowCount;
                    }

                    for (var rowIndex = 0; rowIndex < rowCount; rowIndex += 1)
                    {
                        var row = rows[offset + rowIndex];
                        command.Parameters[$"@ordinal{rowIndex}"].Value = row.Ordinal;
                        command.Parameters[$"@remotePriceId{rowIndex}"].Value = row.RemotePriceId;
                        command.Parameters[$"@remoteProductId{rowIndex}"].Value = row.RemoteProductId;
                        command.Parameters[$"@type{rowIndex}"].Value = row.Type;
                        command.Parameters[$"@price{rowIndex}"].Value = row.Price;
                        command.Parameters[$"@effectiveAt{rowIndex}"].Value = row.EffectiveAt;
                        command.Parameters[$"@source{rowIndex}"].Value = row.Source;
                    }

                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                    RecordSqlCommand(diagnostics);
                    offset += rowCount;
                }
            }
            finally
            {
                command?.Dispose();
            }
        }

        private static SqliteCommand CreateRemotePriceStageInsertCommand(
            SqliteConnection conn,
            SqliteTransaction tx,
            int rowCount)
        {
            var values = new string[rowCount];
            var command = conn.CreateCommand();
            command.Transaction = tx;
            for (var index = 0; index < rowCount; index += 1)
            {
                values[index] =
                    $"(@ordinal{index}, @remotePriceId{index}, @remoteProductId{index}, " +
                    $"@type{index}, @price{index}, @effectiveAt{index}, @source{index})";
                command.Parameters.Add($"@ordinal{index}", SqliteType.Integer);
                command.Parameters.Add($"@remotePriceId{index}", SqliteType.Text);
                command.Parameters.Add($"@remoteProductId{index}", SqliteType.Text);
                command.Parameters.Add($"@type{index}", SqliteType.Text);
                command.Parameters.Add($"@price{index}", SqliteType.Integer);
                command.Parameters.Add($"@effectiveAt{index}", SqliteType.Text);
                command.Parameters.Add($"@source{index}", SqliteType.Text);
            }

            command.CommandText = @"
INSERT INTO temp_catalog_page_remote_prices(
  ordinal,
  remote_price_id,
  remote_product_id,
  type,
  price,
  effective_at,
  source)
VALUES " + string.Join(",\n", values) + ";";
            return command;
        }

        internal async Task<bool> UpsertRemotePriceHistoryAsync(string remoteProductId, string type, int price, string timestamp, string source)
        {
            var result = await UpsertOrQueueRemotePriceHistoryAsync(
                remoteProductId,
                null,
                type,
                price,
                timestamp,
                source).ConfigureAwait(false);
            return result.Applied;
        }

        internal async Task<RemotePriceHistoryApplyResult> UpsertOrQueueRemotePriceHistoryAsync(
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source)
        {
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedRemoteProductId.Length == 0 || normalizedType.Length == 0 || price < 0)
                return RemotePriceHistoryApplyResult.Skipped();

            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var result = await UpsertOrQueueRemotePriceHistoryInTransactionAsync(
                    conn,
                    tx,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    timestamp,
                    normalizedSource).ConfigureAwait(false);
                tx.Commit();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        internal static async Task<RemotePriceHistoryApplyResult> UpsertOrQueueRemotePriceHistoryInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source,
            RemotePriceApplyDiagnostics diagnostics = null)
        {
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedRemoteProductId.Length == 0 || normalizedType.Length == 0 || price < 0)
                return RemotePriceHistoryApplyResult.Skipped();

            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            if (normalizedRemotePriceId.Length > 0)
            {
                var storedOwner = await LoadRemotePriceOwnerAsync(
                    conn,
                    tx,
                    normalizedRemotePriceId,
                    diagnostics).ConfigureAwait(false);
                if (storedOwner.Length > 0 &&
                    !string.Equals(storedOwner, normalizedRemoteProductId, StringComparison.Ordinal))
                {
                    return RemotePriceHistoryApplyResult.Skipped();
                }
            }

            RecordSqlCommand(diagnostics);
            var barcode = await conn.QuerySingleOrDefaultAsync<string>(
                @"SELECT barcode
	FROM products
	WHERE remote_product_id = @remoteProductId
	  AND COALESCE(is_active, 1) = 1
	ORDER BY id ASC
	LIMIT 1",
	                new { remoteProductId = normalizedRemoteProductId },
                    tx).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(barcode))
            {
                var queuedRows = await QueuePendingRemotePriceAsync(
                    conn,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    normalizedTimestamp,
                    normalizedSource,
                    tx,
                    diagnostics).ConfigureAwait(false);
                if (normalizedRemotePriceId.Length == 0)
                {
                    return RemotePriceHistoryApplyResult.QueuedOk();
                }

                if (queuedRows > 0)
                {
                    if (!await StoreRemotePriceOwnershipAsync(
                            conn,
                            tx,
                            normalizedRemotePriceId,
                            normalizedRemoteProductId,
                            diagnostics).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("catalog_remote_price_owner_write_conflict");
                    }

                    return RemotePriceHistoryApplyResult.QueuedOk();
                }

                var evidence = await EvaluateRemotePriceIdEvidenceAsync(
                    conn,
                    tx,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    timestamp,
                    normalizedTimestamp,
                    normalizedSource,
                    diagnostics).ConfigureAwait(false);
                if (evidence.State == RemotePriceIdEvidenceState.Applied)
                {
                    await DeleteRemotePriceIdEvidencePendingAsync(
                        conn,
                        tx,
                        evidence,
                        normalizedRemoteProductId,
                        normalizedRemotePriceId,
                        normalizedType,
                        price,
                        normalizedSource,
                        diagnostics).ConfigureAwait(false);
                    return RemotePriceHistoryApplyResult.AppliedOk();
                }

                return evidence.State == RemotePriceIdEvidenceState.Queued
                    ? RemotePriceHistoryApplyResult.QueuedOk()
                    : RemotePriceHistoryApplyResult.Skipped();
            }

            var insertedRows = await InsertRemotePriceHistoryAsync(
                conn,
                tx,
                barcode.Trim(),
                normalizedRemotePriceId,
                normalizedType,
                price,
                normalizedTimestamp,
                normalizedSource,
                normalizedRemoteProductId,
                diagnostics).ConfigureAwait(false);

            if (normalizedRemotePriceId.Length == 0)
            {
                await DeletePendingRemotePriceAsync(
                    conn,
                    tx,
                    null,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    normalizedTimestamp,
                    normalizedSource,
                    diagnostics).ConfigureAwait(false);
                return RemotePriceHistoryApplyResult.AppliedOk();
            }

            if (insertedRows > 0)
            {
                if (!await StoreRemotePriceOwnershipAsync(
                        conn,
                        tx,
                        normalizedRemotePriceId,
                        normalizedRemoteProductId,
                        diagnostics).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("catalog_remote_price_owner_write_conflict");
                }

                await DeletePendingRemotePriceAsync(
                    conn,
                    tx,
                    null,
                    normalizedRemoteProductId,
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    normalizedTimestamp,
                    normalizedSource,
                    diagnostics).ConfigureAwait(false);
                return RemotePriceHistoryApplyResult.AppliedOk();
            }

            var conflictEvidence = await EvaluateRemotePriceIdEvidenceAsync(
                conn,
                tx,
                normalizedRemoteProductId,
                normalizedRemotePriceId,
                normalizedType,
                price,
                timestamp,
                normalizedTimestamp,
                normalizedSource,
                diagnostics).ConfigureAwait(false);
            if (conflictEvidence.State == RemotePriceIdEvidenceState.Collision)
            {
                return RemotePriceHistoryApplyResult.Skipped();
            }

            if (conflictEvidence.State == RemotePriceIdEvidenceState.Queued)
            {
                var retryInsertedRows = await InsertRemotePriceHistoryAsync(
                    conn,
                    tx,
                    barcode.Trim(),
                    normalizedRemotePriceId,
                    normalizedType,
                    price,
                    conflictEvidence.EffectiveAt,
                    normalizedSource,
                    normalizedRemoteProductId,
                    diagnostics).ConfigureAwait(false);
                if (retryInsertedRows == 0)
                {
                    conflictEvidence = await EvaluateRemotePriceIdEvidenceAsync(
                        conn,
                        tx,
                        normalizedRemoteProductId,
                        normalizedRemotePriceId,
                        normalizedType,
                        price,
                        conflictEvidence.EffectiveAt,
                        conflictEvidence.EffectiveAt,
                        normalizedSource,
                        diagnostics).ConfigureAwait(false);
                    if (conflictEvidence.State != RemotePriceIdEvidenceState.Applied)
                    {
                        return RemotePriceHistoryApplyResult.Skipped();
                    }
                }
            }

            await DeleteRemotePriceIdEvidencePendingAsync(
                conn,
                tx,
                conflictEvidence,
                normalizedRemoteProductId,
                normalizedRemotePriceId,
                normalizedType,
                price,
                normalizedSource,
                diagnostics).ConfigureAwait(false);

            return RemotePriceHistoryApplyResult.AppliedOk();
        }

        private static async Task<RemotePriceIdEvidence> EvaluateRemotePriceIdEvidenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string requestedEffectiveAt,
            string fallbackEffectiveAt,
            string source,
            RemotePriceApplyDiagnostics diagnostics)
        {
            var existingHistory = await LoadRemotePriceHistoryAsync(
                conn,
                tx,
                remotePriceId,
                diagnostics).ConfigureAwait(false);
            var existingPending = await LoadPendingRemotePriceAsync(
                conn,
                tx,
                remotePriceId,
                diagnostics).ConfigureAwait(false);
            if (!await EnsureRemotePriceOwnershipForEvidenceAsync(
                    conn,
                    tx,
                    remotePriceId,
                    remoteProductId,
                    existingPending,
                    diagnostics).ConfigureAwait(false))
            {
                return new RemotePriceIdEvidence { State = RemotePriceIdEvidenceState.Collision };
            }

            var effectiveAt = string.IsNullOrWhiteSpace(requestedEffectiveAt)
                ? existingHistory?.EffectiveAt ?? existingPending?.EffectiveAt ?? fallbackEffectiveAt
                : fallbackEffectiveAt;

            if (existingHistory != null)
            {
                var historyMatches = RemotePriceHistoryMatches(
                    existingHistory,
                    type,
                    price,
                    effectiveAt,
                    source);
                if (!historyMatches ||
                    (existingPending != null && !PendingRemotePriceMatches(
                        existingPending,
                        remoteProductId,
                        type,
                        price,
                        effectiveAt,
                        source)))
                {
                    return new RemotePriceIdEvidence { State = RemotePriceIdEvidenceState.Collision };
                }

                return new RemotePriceIdEvidence
                {
                    EffectiveAt = effectiveAt,
                    PendingId = existingPending?.Id,
                    State = RemotePriceIdEvidenceState.Applied
                };
            }

            if (existingPending != null && PendingRemotePriceMatches(
                    existingPending,
                    remoteProductId,
                    type,
                    price,
                    effectiveAt,
                    source))
            {
                return new RemotePriceIdEvidence
                {
                    EffectiveAt = effectiveAt,
                    PendingId = existingPending.Id,
                    State = RemotePriceIdEvidenceState.Queued
                };
            }

            return new RemotePriceIdEvidence { State = RemotePriceIdEvidenceState.Collision };
        }

        private static Task DeleteRemotePriceIdEvidencePendingAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RemotePriceIdEvidence evidence,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string source,
            RemotePriceApplyDiagnostics diagnostics)
        {
            if (!evidence.PendingId.HasValue)
            {
                return Task.FromResult(0);
            }

            return DeletePendingRemotePriceAsync(
                conn,
                tx,
                evidence.PendingId,
                remoteProductId,
                remotePriceId,
                type,
                price,
                evidence.EffectiveAt,
                source,
                diagnostics);
        }

        private static Task<RemotePriceHistoryRow> LoadRemotePriceHistoryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId,
            RemotePriceApplyDiagnostics diagnostics)
        {
            RecordSqlCommand(diagnostics);
            return conn.QuerySingleOrDefaultAsync<RemotePriceHistoryRow>(@"
SELECT
  barcode AS Barcode,
  timestamp AS EffectiveAt,
  type AS Type,
  new_price AS Price,
  COALESCE(source, '') AS Source
FROM product_price_history
WHERE remote_price_id = @remotePriceId
LIMIT 1",
                new { remotePriceId },
                tx);
        }

        private static Task<PendingRemotePriceRow> LoadPendingRemotePriceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId,
            RemotePriceApplyDiagnostics diagnostics)
        {
            RecordSqlCommand(diagnostics);
            return conn.QuerySingleOrDefaultAsync<PendingRemotePriceRow>(@"
SELECT
  id AS Id,
  remote_price_id AS RemotePriceId,
  remote_product_id AS RemoteProductId,
  type AS Type,
  price AS Price,
  effective_at AS EffectiveAt,
  COALESCE(source, '') AS Source
FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId
LIMIT 1",
                new { remotePriceId },
                tx);
        }

        /// <summary>
        /// Resolves legacy price-id evidence only while applying an authoritative full refresh.
        /// The original history/pending evidence is copied to an append-only quarantine table;
        /// history rows are retained with a cleared remote id, and only then is the authoritative
        /// owner adopted. Ordinary delta/retry paths never call this method and remain fail-closed.
        /// </summary>
        internal static async Task<bool> PrepareAuthoritativeRemotePriceRepairAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source,
            RemotePriceApplyDiagnostics diagnostics = null)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedRemoteProductId.Length == 0 ||
                normalizedRemotePriceId.Length == 0 ||
                normalizedType.Length == 0 ||
                price < 0)
            {
                return false;
            }

            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedSource = string.IsNullOrWhiteSpace(source)
                ? "remote_catalog"
                : source.Trim();
            var existingHistory = await LoadRemotePriceHistoryAsync(
                conn,
                tx,
                normalizedRemotePriceId,
                diagnostics).ConfigureAwait(false);
            var existingPending = await LoadPendingRemotePriceAsync(
                conn,
                tx,
                normalizedRemotePriceId,
                diagnostics).ConfigureAwait(false);
            var storedOwner = await LoadRemotePriceOwnerAsync(
                conn,
                tx,
                normalizedRemotePriceId,
                diagnostics).ConfigureAwait(false);
            if (storedOwner.Length > 0 &&
                !string.Equals(storedOwner, normalizedRemoteProductId, StringComparison.Ordinal))
            {
                return false;
            }

            if (existingHistory == null && existingPending == null)
            {
                return true;
            }

            var effectiveAt = string.IsNullOrWhiteSpace(timestamp)
                ? existingHistory?.EffectiveAt ?? existingPending?.EffectiveAt ?? normalizedTimestamp
                : normalizedTimestamp;
            var historyMatches = existingHistory != null && RemotePriceHistoryMatches(
                existingHistory,
                normalizedType,
                price,
                effectiveAt,
                normalizedSource);
            var pendingMatches = existingPending != null && PendingRemotePriceMatches(
                existingPending,
                normalizedRemoteProductId,
                normalizedType,
                price,
                effectiveAt,
                normalizedSource);

            if (storedOwner.Length > 0)
            {
                // Once ownership exists, the id is immutable. Even an authoritative
                // full refresh may not quarantine or rewrite same-owner tuple drift.
                return (existingHistory == null || historyMatches) &&
                    (existingPending == null || pendingMatches);
            }

            if (storedOwner.Length == 0 && existingHistory == null && pendingMatches)
            {
                // A pending row records its remote product id explicitly and remains
                // sufficient immutable ownership evidence without quarantine.
                return true;
            }

            var quarantinedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            const string reason = "authoritative_full_refresh_rebind";
            RecordSqlCommand(diagnostics);
            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO remote_catalog_price_evidence_quarantine(
  evidence_kind,
  evidence_row_id,
  remote_price_id,
  remote_product_id,
  barcode,
  effective_at,
  type,
  old_price,
  price,
  source,
  catalog_import_client_item_id,
  catalog_import_idempotency_key,
  original_created_at,
  authoritative_remote_product_id,
  reason,
  quarantined_at)
SELECT
  'history',
  id,
  remote_price_id,
  NULL,
  barcode,
  timestamp,
  type,
  old_price,
  new_price,
  source,
  catalog_import_client_item_id,
  catalog_import_idempotency_key,
  NULL,
  @remoteProductId,
  @reason,
  @quarantinedAt
FROM product_price_history
WHERE remote_price_id = @remotePriceId;",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId,
                    reason,
                    quarantinedAt
                },
                tx).ConfigureAwait(false);
            RecordSqlCommand(diagnostics);
            await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO remote_catalog_price_evidence_quarantine(
  evidence_kind,
  evidence_row_id,
  remote_price_id,
  remote_product_id,
  barcode,
  effective_at,
  type,
  old_price,
  price,
  source,
  catalog_import_client_item_id,
  catalog_import_idempotency_key,
  original_created_at,
  authoritative_remote_product_id,
  reason,
  quarantined_at)
SELECT
  'pending',
  id,
  remote_price_id,
  remote_product_id,
  NULL,
  effective_at,
  type,
  NULL,
  price,
  source,
  NULL,
  NULL,
  created_at,
  @remoteProductId,
  @reason,
  @quarantinedAt
FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId;",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId,
                    reason,
                    quarantinedAt
                },
                tx).ConfigureAwait(false);

            // History is never deleted. Clearing only the ambiguous remote id allows
            // the authoritative row to establish a canonical identity while the
            // quarantine table retains the complete pre-repair evidence.
            RecordSqlCommand(diagnostics, statementCount: 2);
            await conn.ExecuteAsync(@"
UPDATE product_price_history
SET remote_price_id = NULL
WHERE remote_price_id = @remotePriceId;

DELETE FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId;",
                new { remotePriceId = normalizedRemotePriceId },
                tx).ConfigureAwait(false);

            if (!await StoreRemotePriceOwnershipAsync(
                    conn,
                    tx,
                    normalizedRemotePriceId,
                    normalizedRemoteProductId,
                    diagnostics).ConfigureAwait(false))
            {
                throw new InvalidOperationException("catalog_remote_price_owner_repair_conflict");
            }

            // Reuse an exact unbound history tuple for the current authoritative
            // product when possible. Otherwise the normal apply path inserts a fresh
            // canonical row and leaves the retained legacy row unbound.
            RecordSqlCommand(diagnostics);
            await conn.ExecuteAsync(@"
UPDATE product_price_history
SET remote_price_id = @remotePriceId
WHERE id = (
  SELECT history.id
  FROM product_price_history history
  JOIN products product
    ON product.barcode = history.barcode
  WHERE history.remote_price_id IS NULL
    AND product.remote_product_id = @remoteProductId
    AND COALESCE(product.is_active, 1) = 1
    AND history.timestamp = @effectiveAt
    AND history.type = @type
    AND history.new_price = @price
    AND COALESCE(history.source, '') = @source
  ORDER BY history.id DESC
  LIMIT 1
)
AND NOT EXISTS (
  SELECT 1
  FROM product_price_history existing
  WHERE existing.remote_price_id = @remotePriceId
);",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId,
                    effectiveAt,
                    type = normalizedType,
                    price,
                    source = normalizedSource
                },
                tx).ConfigureAwait(false);
            return true;
        }

        private static async Task<string> LoadRemotePriceOwnerAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId,
            RemotePriceApplyDiagnostics diagnostics = null)
        {
            RecordSqlCommand(diagnostics);
            return (await conn.QuerySingleOrDefaultAsync<string>(@"
SELECT remote_product_id
FROM remote_catalog_price_ownership
WHERE remote_price_id = @remotePriceId
LIMIT 1",
                new { remotePriceId = (remotePriceId ?? string.Empty).Trim() },
                tx).ConfigureAwait(false) ?? string.Empty).Trim();
        }

        private static async Task<bool> EnsureRemotePriceOwnershipForEvidenceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId,
            string remoteProductId,
            PendingRemotePriceRow pending,
            RemotePriceApplyDiagnostics diagnostics)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            if (normalizedRemotePriceId.Length == 0 || normalizedRemoteProductId.Length == 0)
            {
                return false;
            }

            var storedOwner = await LoadRemotePriceOwnerAsync(
                conn,
                tx,
                normalizedRemotePriceId,
                diagnostics).ConfigureAwait(false);
            if (storedOwner.Length > 0)
            {
                return string.Equals(storedOwner, normalizedRemoteProductId, StringComparison.Ordinal);
            }

            if (pending == null)
            {
                // Legacy history rows do not record their remote product owner. The
                // current barcode owner is not immutable evidence because barcodes may
                // be renamed and later reused, so history-only evidence stays unclaimed.
                return false;
            }

            var pendingOwner = (pending.RemoteProductId ?? string.Empty).Trim();
            if (!string.Equals(pendingOwner, normalizedRemoteProductId, StringComparison.Ordinal))
            {
                return false;
            }

            return await StoreRemotePriceOwnershipAsync(
                conn,
                tx,
                normalizedRemotePriceId,
                normalizedRemoteProductId,
                diagnostics).ConfigureAwait(false);
        }

        internal static async Task<bool> StoreRemotePriceOwnershipAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string remotePriceId,
            string remoteProductId,
            RemotePriceApplyDiagnostics diagnostics = null)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            if (normalizedRemotePriceId.Length == 0 || normalizedRemoteProductId.Length == 0)
            {
                return false;
            }

            RecordSqlCommand(diagnostics, statementCount: 2);
            var matchingOwners = await conn.ExecuteScalarAsync<long>(@"
INSERT OR IGNORE INTO remote_catalog_price_ownership(
  remote_price_id,
  remote_product_id)
VALUES(@remotePriceId, @remoteProductId);
SELECT COUNT(1)
FROM remote_catalog_price_ownership
WHERE remote_price_id = @remotePriceId
  AND TRIM(remote_product_id) = @remoteProductId;",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId
                },
                tx).ConfigureAwait(false);
            return matchingOwners == 1;
        }

        private static bool RemotePriceHistoryMatches(
            RemotePriceHistoryRow existing,
            string type,
            int price,
            string effectiveAt,
            string source)
        {
            if (existing == null ||
                !string.Equals(existing.Type, type, StringComparison.Ordinal) ||
                existing.Price != price ||
                !string.Equals(existing.EffectiveAt, effectiveAt, StringComparison.Ordinal) ||
                !string.Equals(existing.Source, source, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static bool PendingRemotePriceMatches(
            PendingRemotePriceRow existing,
            string remoteProductId,
            string type,
            int price,
            string effectiveAt,
            string source)
        {
            return existing != null &&
                   string.Equals(
                       (existing.RemoteProductId ?? string.Empty).Trim(),
                       remoteProductId,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       (existing.Type ?? string.Empty).Trim().ToUpperInvariant(),
                       type,
                       StringComparison.Ordinal) &&
                   existing.Price == price &&
                   string.Equals(
                       (existing.EffectiveAt ?? string.Empty).Trim(),
                       effectiveAt,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       (existing.Source ?? string.Empty).Trim(),
                       source,
                       StringComparison.Ordinal);
        }

        internal async Task<int> ApplyPendingRemotePricesAsync()
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                var replay = await ApplyPendingRemotePricesInTransactionAsync(conn, tx).ConfigureAwait(false);
                tx.Commit();
                return replay.Applied;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        internal static async Task<PendingRemotePriceReplayResult> ApplyPendingRemotePricesInTransactionAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            RemotePriceApplyDiagnostics diagnostics = null)
        {
            var result = new PendingRemotePriceReplayResult();

            while (true)
            {
                var blockedByRemotePriceIdCollision = false;
                RecordSqlCommand(diagnostics);
                var rows = (await conn.QueryAsync<PendingRemotePriceRow>(@"
	SELECT
	  p.id AS Id,
	  pr.barcode AS Barcode,
  p.remote_price_id AS RemotePriceId,
  p.remote_product_id AS RemoteProductId,
  p.type AS Type,
  p.price AS Price,
	  p.effective_at AS EffectiveAt,
	  COALESCE(p.source, '') AS Source
	FROM remote_catalog_pending_prices p
	JOIN (
	  SELECT remote_product_id, MIN(id) AS product_id
	  FROM products
	  WHERE COALESCE(is_active, 1) = 1
	    AND COALESCE(remote_product_id, '') <> ''
	  GROUP BY remote_product_id
	) canonical
	  ON canonical.remote_product_id = p.remote_product_id
	JOIN products pr
	  ON pr.id = canonical.product_id
	ORDER BY p.id ASC
	LIMIT @limit", new { limit = PendingRemotePriceReplayBatchSize }, tx).ConfigureAwait(false)).ToList();

                if (rows.Count == 0)
                {
                    break;
                }

                foreach (var row in rows)
                {
                    var normalizedType = (row.Type ?? string.Empty).Trim().ToUpperInvariant();
                    var normalizedEffectiveAt = string.IsNullOrWhiteSpace(row.EffectiveAt)
                        ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : row.EffectiveAt.Trim();
                    var normalizedSource = string.IsNullOrWhiteSpace(row.Source)
                        ? "remote_catalog"
                        : row.Source.Trim();
                    var normalizedRemotePriceId = (row.RemotePriceId ?? string.Empty).Trim();
                    if (normalizedRemotePriceId.Length > 0)
                    {
                        var storedOwner = await LoadRemotePriceOwnerAsync(
                            conn,
                            tx,
                            normalizedRemotePriceId,
                            diagnostics).ConfigureAwait(false);
                        if (storedOwner.Length > 0 &&
                            !string.Equals(
                                storedOwner,
                                (row.RemoteProductId ?? string.Empty).Trim(),
                                StringComparison.Ordinal))
                        {
                            result.CollisionIds.Add(row.Id);
                            blockedByRemotePriceIdCollision = true;
                            continue;
                        }
                    }

                    var insertedRows = await InsertRemotePriceHistoryAsync(
                        conn,
                        tx,
                        row.Barcode,
                        row.RemotePriceId,
                        normalizedType,
                        row.Price,
                        normalizedEffectiveAt,
                        normalizedSource,
                        (row.RemoteProductId ?? string.Empty).Trim(),
                        diagnostics)
                        .ConfigureAwait(false);

                    if (normalizedRemotePriceId.Length > 0 && insertedRows == 0)
                    {
                        var evidence = await EvaluateRemotePriceIdEvidenceAsync(
                            conn,
                            tx,
                            (row.RemoteProductId ?? string.Empty).Trim(),
                            normalizedRemotePriceId,
                            normalizedType,
                            row.Price,
                            row.EffectiveAt,
                            normalizedEffectiveAt,
                            normalizedSource,
                            diagnostics).ConfigureAwait(false);
                        if (evidence.State != RemotePriceIdEvidenceState.Applied)
                        {
                            result.CollisionIds.Add(row.Id);
                            blockedByRemotePriceIdCollision = true;
                            continue;
                        }
                    }
                    else if (normalizedRemotePriceId.Length > 0 &&
                        !await StoreRemotePriceOwnershipAsync(
                                conn,
                                tx,
                                normalizedRemotePriceId,
                                (row.RemoteProductId ?? string.Empty).Trim(),
                                diagnostics)
                            .ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("catalog_remote_price_owner_write_conflict");
                    }

                    await DeletePendingRemotePriceAsync(
                        conn,
                        tx,
                        row.Id,
                        row.RemoteProductId,
                        row.RemotePriceId,
                        row.Type,
                        row.Price,
                        row.EffectiveAt,
                        row.Source,
                        diagnostics).ConfigureAwait(false);
                    result.Applied += 1;
                }

                if (blockedByRemotePriceIdCollision || rows.Count < PendingRemotePriceReplayBatchSize)
                {
                    break;
                }
            }

            return result;
        }

        private static Task<int> InsertRemotePriceHistoryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            string barcode,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source,
            string remoteProductId,
            RemotePriceApplyDiagnostics diagnostics)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedRemoteProductId = (remoteProductId ?? string.Empty).Trim();
            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            RecordSqlCommand(diagnostics);
            return conn.ExecuteAsync(@"
INSERT OR IGNORE INTO product_price_history(
    barcode,
    timestamp,
    type,
    old_price,
    new_price,
    source,
    remote_price_id)
SELECT
    @barcode,
    @timestamp,
    @type,
    NULL,
    @price,
    @source,
    NULLIF(@remotePriceId, '')
WHERE (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM remote_catalog_price_ownership ownership
        WHERE ownership.remote_price_id = @remotePriceId
          AND TRIM(ownership.remote_product_id) <> @remoteProductId
      )
   )
  AND (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM remote_catalog_pending_prices
        WHERE remote_price_id = @remotePriceId
          AND NOT (
              TRIM(COALESCE(remote_product_id, '')) = @remoteProductId
              AND type = @type
              AND price = @price
              AND effective_at = @timestamp
              AND COALESCE(source, '') = @source
          )
      )
   )",
                new
                {
                    barcode = (barcode ?? string.Empty).Trim(),
                    timestamp = normalizedTimestamp,
                    type = normalizedType,
                    price,
                    source = normalizedSource,
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = normalizedRemoteProductId
                },
                tx);
        }

        private static Task<int> QueuePendingRemotePriceAsync(
            SqliteConnection conn,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source,
            SqliteTransaction tx,
            RemotePriceApplyDiagnostics diagnostics)
        {
            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            var normalizedTimestamp = string.IsNullOrWhiteSpace(timestamp)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : timestamp.Trim();
            var normalizedType = (type ?? string.Empty).Trim().ToUpperInvariant();
            var normalizedSource = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim();
            RecordSqlCommand(diagnostics);
            return conn.ExecuteAsync(@"
INSERT OR IGNORE INTO remote_catalog_pending_prices(
    remote_price_id,
    remote_product_id,
    type,
    price,
    effective_at,
    source,
    created_at)
SELECT
    NULLIF(@remotePriceId, ''),
    @remoteProductId,
    @type,
    @price,
    @effectiveAt,
    @source,
    @createdAt
WHERE (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM remote_catalog_price_ownership ownership
        WHERE ownership.remote_price_id = @remotePriceId
          AND TRIM(ownership.remote_product_id) <> @remoteProductId
      )
   )
  AND (
      @remotePriceId = ''
      OR NOT EXISTS (
        SELECT 1
        FROM product_price_history
        WHERE remote_price_id = @remotePriceId
      )
   )",
                new
                {
                    remotePriceId = normalizedRemotePriceId,
                    remoteProductId = (remoteProductId ?? string.Empty).Trim(),
                    type = normalizedType,
                    price,
                    effectiveAt = normalizedTimestamp,
                    source = normalizedSource,
                    createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                tx);
        }

        private static Task<int> DeletePendingRemotePriceAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long? id,
            string remoteProductId,
            string remotePriceId,
            string type,
            int price,
            string timestamp,
            string source,
            RemotePriceApplyDiagnostics diagnostics)
        {
            if (id.HasValue)
            {
                RecordSqlCommand(diagnostics);
                return conn.ExecuteAsync(
                    "DELETE FROM remote_catalog_pending_prices WHERE id = @id",
                    new { id = id.Value },
                    tx);
            }

            var normalizedRemotePriceId = (remotePriceId ?? string.Empty).Trim();
            if (normalizedRemotePriceId.Length > 0)
            {
                RecordSqlCommand(diagnostics);
                return conn.ExecuteAsync(@"
DELETE FROM remote_catalog_pending_prices
WHERE remote_price_id = @remotePriceId
  AND TRIM(COALESCE(remote_product_id, '')) = @remoteProductId
  AND type = @type
  AND price = @price
  AND effective_at = @effectiveAt
  AND COALESCE(source, '') = @source",
                    new
                    {
                        remotePriceId = normalizedRemotePriceId,
                        remoteProductId = (remoteProductId ?? string.Empty).Trim(),
                        type = (type ?? string.Empty).Trim().ToUpperInvariant(),
                        price,
                        effectiveAt = string.IsNullOrWhiteSpace(timestamp)
                            ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                            : timestamp.Trim(),
                        source = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim()
                    },
                    tx);
            }

            RecordSqlCommand(diagnostics);
            return conn.ExecuteAsync(@"
DELETE FROM remote_catalog_pending_prices
WHERE remote_price_id IS NULL
  AND remote_product_id = @remoteProductId
  AND type = @type
  AND effective_at = @effectiveAt
  AND price = @price
  AND COALESCE(source, '') = @source",
                new
                {
                    remoteProductId = (remoteProductId ?? string.Empty).Trim(),
                    type = (type ?? string.Empty).Trim().ToUpperInvariant(),
                    effectiveAt = string.IsNullOrWhiteSpace(timestamp)
                        ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        : timestamp.Trim(),
                    price,
                    source = string.IsNullOrWhiteSpace(source) ? "remote_catalog" : source.Trim()
                },
                tx);
        }

        private static void RecordSqlCommand(
            RemotePriceApplyDiagnostics diagnostics,
            int statementCount = 1)
        {
            diagnostics?.RecordSqlCommand(statementCount);
        }

    }
}
