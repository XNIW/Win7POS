using System;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Online
{
    public sealed class CatalogShopStateRepository
    {
        public const string BoundShopCodeKey = "pos.catalog.bound_shop_code";
        public const string BoundShopIdKey = "pos.catalog.bound_shop_id";
        public const string InitialCompletedAtKey = "pos.catalog.initial_completed_at";
        public const string LastSyncAtKey = "pos.catalog.last_sync_at";
        public const string LastSyncCursorKey = "pos.catalog.last_sync_cursor";
        public const string LastSyncModeKey = "pos.catalog.last_sync_mode";
        public const string TransitionEpochKey = "pos.catalog.transition_epoch";
        public const string SaleSafeAtKey = "pos.catalog.sale_safe_at";

        private readonly SqliteConnectionFactory _factory;

        public CatalogShopStateRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<string> ValidateCapturedSessionAsync(
            string capturedShopId,
            string capturedShopCode)
        {
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var officialId = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopIdKey)
                    .ConfigureAwait(false);
                var officialCode = await GetAsync(conn, tx, OutboxShopBinding.OfficialShopCodeKey)
                    .ConfigureAwait(false);
                var mismatch = OutboxShopBinding.GetMismatchCode(
                    capturedShopId,
                    capturedShopCode,
                    officialId,
                    officialCode);
                tx.Commit();
                return string.IsNullOrWhiteSpace(mismatch)
                    ? string.Empty
                    : "catalog_session_shop_changed";
            }
        }

        public async Task<CatalogShopBindingResult> EnsureAndLoadCursorAsync(
            string trustedShopId,
            string trustedShopCode)
        {
            var normalizedCode = OutboxShopBinding.NormalizeCode(trustedShopCode);
            var normalizedId = Normalize(trustedShopId);
            if (normalizedCode.Length == 0)
            {
                return CatalogShopBindingResult.Failure("catalog_shop_binding_missing");
            }

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var boundCode = OutboxShopBinding.NormalizeCode(await GetAsync(conn, tx, BoundShopCodeKey).ConfigureAwait(false));
                var boundId = Normalize(await GetAsync(conn, tx, BoundShopIdKey).ConfigureAwait(false));
                var hasExistingState = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM app_settings
WHERE key IN (@CursorKey, @SaleSafeKey, @LastSyncKey)
  AND TRIM(value) <> '';",
                    new
                    {
                        CursorKey = LastSyncCursorKey,
                        SaleSafeKey = SaleSafeAtKey,
                        LastSyncKey = LastSyncAtKey
                    },
                    tx).ConfigureAwait(false) > 0;

                if (boundCode.Length == 0)
                {
                    if (hasExistingState)
                    {
                        await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (@CursorKey, @SaleSafeKey, @LastSyncKey, @InitialCompletedKey);",
                            new
                            {
                                CursorKey = LastSyncCursorKey,
                                InitialCompletedKey = InitialCompletedAtKey,
                                LastSyncKey = LastSyncAtKey,
                                SaleSafeKey = SaleSafeAtKey
                            },
                            tx).ConfigureAwait(false);
                    }

                    await SetAsync(conn, tx, BoundShopCodeKey, normalizedCode).ConfigureAwait(false);
                    await SetAsync(conn, tx, BoundShopIdKey, normalizedId).ConfigureAwait(false);
                    boundCode = normalizedCode;
                    boundId = normalizedId;
                }
                else if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    boundId,
                    boundCode,
                    normalizedId,
                    normalizedCode)))
                {
                    tx.Rollback();
                    return CatalogShopBindingResult.Failure("catalog_shop_binding_mismatch");
                }

                if (boundId.Length == 0 && normalizedId.Length > 0)
                {
                    await SetAsync(conn, tx, BoundShopIdKey, normalizedId).ConfigureAwait(false);
                }

                var epoch = await LoadEpochAsync(conn, tx).ConfigureAwait(false);
                var cursor = await GetAsync(conn, tx, LastSyncCursorKey).ConfigureAwait(false);
                tx.Commit();
                return CatalogShopBindingResult.Success(cursor, epoch);
            }
        }

        public async Task StoreLastSyncAsync(
            string trustedShopId,
            string trustedShopCode,
            string syncCursor,
            string generatedAt,
            long expectedEpoch = -1,
            string syncMode = null)
        {
            var value = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();
            var cursor = string.IsNullOrWhiteSpace(syncCursor) ? value : syncCursor.Trim();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(conn, tx, trustedShopId, trustedShopCode, expectedEpoch).ConfigureAwait(false);
                await SetAsync(conn, tx, LastSyncAtKey, value).ConfigureAwait(false);
                await SetAsync(conn, tx, LastSyncCursorKey, cursor).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(syncMode))
                {
                    await SetAsync(conn, tx, LastSyncModeKey, syncMode.Trim()).ConfigureAwait(false);
                }
                tx.Commit();
            }
        }

        public async Task<bool> StorePullCursorAsync(
            string trustedShopId,
            string trustedShopCode,
            string syncCursor,
            string generatedAt,
            long expectedEpoch,
            string syncMode,
            bool authoritativeSnapshotCommitted)
        {
            if (string.Equals(syncMode, "full_refresh", StringComparison.OrdinalIgnoreCase) &&
                !authoritativeSnapshotCommitted)
            {
                return false;
            }

            await StoreLastSyncAsync(
                trustedShopId,
                trustedShopCode,
                syncCursor,
                generatedAt,
                expectedEpoch,
                syncMode).ConfigureAwait(false);
            return true;
        }

        public async Task ResetForRestoreReviewAsync(
            string trustedShopId,
            string trustedShopCode)
        {
            var binding = await EnsureAndLoadCursorAsync(trustedShopId, trustedShopCode).ConfigureAwait(false);
            if (!binding.IsValid)
            {
                throw new InvalidOperationException(binding.Code);
            }

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode,
                    binding.Epoch).ConfigureAwait(false);
                await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (@CursorKey, @SaleSafeKey, @LastSyncKey, @InitialCompletedKey, @LastSyncModeKey);",
                    new
                    {
                        CursorKey = LastSyncCursorKey,
                        InitialCompletedKey = InitialCompletedAtKey,
                        LastSyncKey = LastSyncAtKey,
                        LastSyncModeKey,
                        SaleSafeKey = SaleSafeAtKey
                    },
                    tx).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public async Task StoreSaleSafeAsync(
            string trustedShopId,
            string trustedShopCode,
            string generatedAt,
            long expectedEpoch = -1)
        {
            var value = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(conn, tx, trustedShopId, trustedShopCode, expectedEpoch).ConfigureAwait(false);
                await SetAsync(conn, tx, SaleSafeAtKey, value).ConfigureAwait(false);
                var initialCompleted = await GetAsync(conn, tx, InitialCompletedAtKey).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(initialCompleted))
                {
                    await SetAsync(conn, tx, InitialCompletedAtKey, value).ConfigureAwait(false);
                }

                tx.Commit();
            }
        }

        public async Task<bool> IsSaleSafeForOfficialShopAsync()
        {
            using (var conn = _factory.Open())
            {
                var saleSafeAt = await GetAsync(conn, null, SaleSafeAtKey).ConfigureAwait(false);
                var boundCode = await GetAsync(conn, null, BoundShopCodeKey).ConfigureAwait(false);
                var boundId = await GetAsync(conn, null, BoundShopIdKey).ConfigureAwait(false);
                var officialCode = await GetAsync(conn, null, OutboxShopBinding.OfficialShopCodeKey).ConfigureAwait(false);
                var officialId = await GetAsync(conn, null, OutboxShopBinding.OfficialShopIdKey).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(saleSafeAt) &&
                    string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                        boundId,
                        boundCode,
                        officialId,
                        officialCode));
            }
        }

        private static async Task RequireBindingAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            var boundCode = await GetAsync(conn, tx, BoundShopCodeKey).ConfigureAwait(false);
            var boundId = await GetAsync(conn, tx, BoundShopIdKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                boundId,
                boundCode,
                trustedShopId,
                trustedShopCode)))
            {
                throw new InvalidOperationException("Catalog state shop binding mismatch.");
            }

            if (expectedEpoch >= 0 &&
                await LoadEpochAsync(conn, tx).ConfigureAwait(false) != expectedEpoch)
            {
                throw new InvalidOperationException("Catalog state transition epoch mismatch.");
            }
        }

        private static async Task<long> LoadEpochAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            var value = await GetAsync(conn, tx, TransitionEpochKey).ConfigureAwait(false);
            if (!long.TryParse(value, out var epoch) || epoch < 0)
            {
                epoch = 0;
                await SetAsync(conn, tx, TransitionEpochKey, "0").ConfigureAwait(false);
            }

            return epoch;
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

        private static Task<int> SetAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key,
            string value)
        {
            return conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty },
                tx);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }
    }

    public sealed class CatalogShopBindingResult
    {
        public string Code { get; private set; } = string.Empty;
        public string Cursor { get; private set; } = string.Empty;
        public long Epoch { get; private set; }
        public bool IsValid { get; private set; }

        public static CatalogShopBindingResult Failure(string code)
        {
            return new CatalogShopBindingResult { Code = code ?? string.Empty };
        }

        public static CatalogShopBindingResult Success(string cursor, long epoch)
        {
            return new CatalogShopBindingResult
            {
                Cursor = cursor ?? string.Empty,
                Epoch = epoch,
                IsValid = true
            };
        }
    }
}
