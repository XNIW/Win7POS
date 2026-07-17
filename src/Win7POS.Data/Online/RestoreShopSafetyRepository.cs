using System;
using System.Globalization;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Online
{
    public sealed class RestoreShopSafetyRepository
    {
        public const string RestoreCompletedAtKey = "pos.restore.last_completed_at";
        public const string RestoreNeedsReviewKey = "pos.restore.needs_sync_review";

        private readonly SqliteConnectionFactory _factory;

        public RestoreShopSafetyRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<RestoreSafetyResult> ValidateCandidateAsync(
            string expectedShopId,
            string expectedShopCode)
        {
            using (var conn = _factory.Open())
            {
                var candidateId = await GetAsync(conn, null, OutboxShopBinding.OfficialShopIdKey).ConfigureAwait(false);
                var candidateCode = await GetAsync(conn, null, OutboxShopBinding.OfficialShopCodeKey).ConfigureAwait(false);
                var shopError = OutboxShopBinding.GetMismatchCode(
                    expectedShopId,
                    expectedShopCode,
                    candidateId,
                    candidateCode);
                if (!string.IsNullOrWhiteSpace(shopError))
                {
                    return RestoreSafetyResult.Failure("restore_shop_mismatch");
                }

                var boundId = await GetAsync(conn, null, CatalogShopStateRepository.BoundShopIdKey).ConfigureAwait(false);
                var boundCode = await GetAsync(conn, null, CatalogShopStateRepository.BoundShopCodeKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    candidateId,
                    candidateCode,
                    boundId,
                    boundCode)))
                {
                    return RestoreSafetyResult.Failure("restore_catalog_shop_mismatch");
                }

                var unresolvedOutbox = await conn.ExecuteScalarAsync<long>(@"
SELECT
  (SELECT COUNT(1)
   FROM sales_sync_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'))
  +
  (SELECT COUNT(1)
   FROM catalog_import_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'));"
                ).ConfigureAwait(false);
                return unresolvedOutbox == 0
                    ? RestoreSafetyResult.Success()
                    : RestoreSafetyResult.Failure("restore_candidate_outbox_unresolved");
            }
        }

        public async Task<RestoreSafetyResult> ValidateLivePreSwapAsync(
            string expectedShopId,
            string expectedShopCode,
            long expectedCatalogEpoch)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var officialId = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopIdKey).ConfigureAwait(false);
                var officialCode = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopCodeKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    expectedShopId,
                    expectedShopCode,
                    officialId,
                    officialCode)))
                {
                    return RestoreSafetyResult.Failure("restore_live_shop_changed");
                }

                var boundId = await GetAsync(conn, tx, CatalogShopStateRepository.BoundShopIdKey).ConfigureAwait(false);
                var boundCode = await GetAsync(conn, tx, CatalogShopStateRepository.BoundShopCodeKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    officialId,
                    officialCode,
                    boundId,
                    boundCode)))
                {
                    return RestoreSafetyResult.Failure("restore_live_catalog_shop_mismatch");
                }

                var rawEpoch = await GetAsync(conn, tx, CatalogShopStateRepository.TransitionEpochKey).ConfigureAwait(false);
                if (!long.TryParse(
                    rawEpoch,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var liveCatalogEpoch) ||
                    liveCatalogEpoch != expectedCatalogEpoch)
                {
                    return RestoreSafetyResult.Failure("restore_live_catalog_epoch_changed");
                }

                var unresolvedSales = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM sales_sync_outbox
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked');",
                    transaction: tx).ConfigureAwait(false);
                if (unresolvedSales > 0)
                {
                    return RestoreSafetyResult.Failure("restore_live_sales_outbox_unresolved");
                }

                var unresolvedCatalogImports = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM catalog_import_outbox
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked');",
                    transaction: tx).ConfigureAwait(false);
                return unresolvedCatalogImports == 0
                    ? RestoreSafetyResult.Success()
                    : RestoreSafetyResult.Failure("restore_live_catalog_outbox_unresolved");
            }
        }

        public async Task<RestoreSafetyResult> CompleteReviewAsync()
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var integrity = await conn.ExecuteScalarAsync<string>("PRAGMA integrity_check;", transaction: tx)
                    .ConfigureAwait(false);
                if (!string.Equals((integrity ?? string.Empty).Trim(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return RestoreSafetyResult.Failure("restore_review_integrity_failed");
                }

                var needsReview = await GetAsync(conn, tx, RestoreNeedsReviewKey).ConfigureAwait(false);
                if (!string.Equals(needsReview, "true", StringComparison.OrdinalIgnoreCase) && needsReview != "1")
                {
                    tx.Commit();
                    return RestoreSafetyResult.Success();
                }

                var officialId = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopIdKey).ConfigureAwait(false);
                var officialCode = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopCodeKey).ConfigureAwait(false);
                var boundId = await GetAsync(conn, tx, CatalogShopStateRepository.BoundShopIdKey).ConfigureAwait(false);
                var boundCode = await GetAsync(conn, tx, CatalogShopStateRepository.BoundShopCodeKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    officialId,
                    officialCode,
                    boundId,
                    boundCode)))
                {
                    tx.Rollback();
                    return RestoreSafetyResult.Failure("restore_review_catalog_shop_mismatch");
                }

                var unresolved = await conn.ExecuteScalarAsync<long>(@"
SELECT
  (SELECT COUNT(1) FROM sales_sync_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'))
  +
  (SELECT COUNT(1) FROM catalog_import_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'));",
                    transaction: tx).ConfigureAwait(false);
                if (unresolved > 0)
                {
                    tx.Rollback();
                    return RestoreSafetyResult.Failure("restore_review_outbox_unresolved");
                }

                var restoredAt = await GetAsync(conn, tx, RestoreCompletedAtKey).ConfigureAwait(false);
                var saleSafeAt = await GetAsync(conn, tx, CatalogShopStateRepository.SaleSafeAtKey).ConfigureAwait(false);
                var lastSyncAt = await GetAsync(conn, tx, CatalogShopStateRepository.LastSyncAtKey).ConfigureAwait(false);
                var lastSyncMode = await GetAsync(conn, tx, CatalogShopStateRepository.LastSyncModeKey).ConfigureAwait(false);
                if (!AtOrAfter(saleSafeAt, restoredAt) ||
                    !AtOrAfter(lastSyncAt, restoredAt) ||
                    !string.Equals(lastSyncMode, "full_refresh", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return RestoreSafetyResult.Failure("restore_review_catalog_not_reconciled");
                }

                await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, 'false')
ON CONFLICT(key) DO UPDATE SET value = 'false';",
                    new { key = RestoreNeedsReviewKey },
                    tx).ConfigureAwait(false);
                tx.Commit();
                return RestoreSafetyResult.Success();
            }
        }

        private static bool AtOrAfter(string candidate, string baseline)
        {
            return DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var candidateTime) &&
                DateTimeOffset.TryParse(baseline, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var baselineTime) &&
                candidateTime >= baselineTime;
        }

        private static Task<string> GetAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key)
        {
            return conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key;",
                new { key },
                tx);
        }
    }

    public sealed class RestoreSafetyResult
    {
        public string Code { get; private set; } = string.Empty;
        public bool IsValid { get; private set; }

        public static RestoreSafetyResult Failure(string code)
        {
            return new RestoreSafetyResult { Code = code ?? string.Empty };
        }

        public static RestoreSafetyResult Success()
        {
            return new RestoreSafetyResult { IsValid = true };
        }
    }
}
