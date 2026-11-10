using System;
using System.Globalization;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogShopStateRepository
    {
        public static bool IsUnusableDatabase(Exception error) =>
            error is SqliteException sqlite && (sqlite.SqliteErrorCode == 11 || sqlite.SqliteErrorCode == 26);

        // An observation, never a sale-safety or authentication capability. Keeping
        // it under exactness also invalidates it on repair, relink and restore.
        public const string AuthoritativeEmptyPrefix = ExactnessPrefix + "empty.";
        // Unlike the transient observation, this safety fence survives a new
        // pull, repair, restore, relink and generation change. Only an atomic
        // verified nonempty full replacement can remove it.
        public const string FullRefreshRequiredAfterEmptyKey = "pos.catalog.full_refresh_required_after_empty";
        public const string FullRefreshRequiredAfterEmptyCode = PosCatalogOperationPolicy.FullRefreshRequiredAfterEmptyCode;

        public async Task<bool> RequiresFullRefreshAfterEmptyAsync()
        {
            using var conn = _factory.Open();
            return await HasFullRefreshRequiredAfterEmptyWithinTransactionAsync(conn, null)
                .ConfigureAwait(false);
        }

        private static async Task<bool> HasFullRefreshRequiredAfterEmptyWithinTransactionAsync(
            SqliteConnection conn, SqliteTransaction tx)
        {
            // The only valid stored state is presence. Malformed/empty values
            // must not silently remove a previously established safety fence.
            return await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM app_settings WHERE key = @key;",
                new { key = FullRefreshRequiredAfterEmptyKey }, tx).ConfigureAwait(false) != 0;
        }

        public async Task ClearAuthoritativeEmptyAsync(OnlineSyncGeneration generation)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: false);
            await RequireOnlineGenerationAsync(conn, tx, generation).ConfigureAwait(false);
            await ClearAuthoritativeEmptyWithinTransactionAsync(conn, tx).ConfigureAwait(false);
            tx.Commit();
        }

        public async Task<bool> IsAuthoritativelyEmptyAsync(OnlineSyncGeneration generation)
        {
            if (generation == null) return false;
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            var empty = await IsAuthoritativelyEmptyWithinTransactionAsync(conn, tx, generation).ConfigureAwait(false);
            tx.Commit();
            return empty;
        }

        private static async Task<bool> IsAuthoritativelyEmptyWithinTransactionAsync(
            SqliteConnection conn, SqliteTransaction tx, OnlineSyncGeneration generation)
        {
            if (generation == null) return false;
            var proof = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation current
WHERE current.singleton_id = 1 AND current.active = 1
  AND current.generation_id = @GenerationId AND current.fingerprint = @Fingerprint
  AND current.shop_id = @ShopId AND current.shop_code = @ShopCode
  AND current.pos_session_id = @PosSessionId AND current.shop_device_id = @ShopDeviceId
  AND @ShopId <> '' AND @ShopCode <> ''
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'generation_id') = @GenerationId
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'generation_fingerprint') = @Fingerprint
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'shop_id') = @ShopId
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'shop_code') = @ShopCode
  AND (SELECT value FROM app_settings WHERE key = @officialId) = @ShopId
  AND (SELECT value FROM app_settings WHERE key = @officialCode) = @ShopCode
  AND (SELECT value FROM app_settings WHERE key = @boundId) = @ShopId
  AND (SELECT value FROM app_settings WHERE key = @boundCode) = @ShopCode
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'epoch') =
      (SELECT value FROM app_settings WHERE key = @epochKey)
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'previous_cursor') =
      COALESCE((SELECT value FROM app_settings WHERE key = @cursorKey), '')
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'previous_mode') =
      COALESCE((SELECT value FROM app_settings WHERE key = @modeKey), '')
  AND (SELECT value FROM app_settings WHERE key = @prefix || 'committed_revision') =
      COALESCE((SELECT value FROM app_settings WHERE key = @revisionKey), '')
  AND LENGTH((SELECT value FROM app_settings WHERE key = @prefix || 'full_run_id')) > 0
  AND LENGTH((SELECT value FROM app_settings WHERE key = @prefix || 'catalog_version')) > 0
  AND LENGTH((SELECT value FROM app_settings WHERE key = @prefix || 'observed_at')) > 0;",
                new
                {
                    generation.GenerationId, generation.Fingerprint, generation.ShopId,
                    generation.ShopCode, generation.PosSessionId, generation.ShopDeviceId,
                    prefix = AuthoritativeEmptyPrefix,
                    officialId = OutboxShopBinding.OfficialShopIdKey,
                    officialCode = OutboxShopBinding.OfficialShopCodeKey,
                    boundId = BoundShopIdKey, boundCode = BoundShopCodeKey,
                    epochKey = TransitionEpochKey, cursorKey = LastSyncCursorKey,
                    modeKey = LastSyncModeKey, revisionKey = CommittedRevisionKey
                }, tx).ConfigureAwait(false);
            return proof == 1;
        }

        // One SQLite read snapshot: never join the network pull's transition barrier.
        // Attempt status is separate; failed/in-progress pulls do not unpublish data.
        public async Task<PosPublishedCatalogState> ReadPublishedSnapshotAsync(OnlineSyncGeneration generation)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            var safety = await EvaluateSaleSafetyAsync(conn, tx, false).ConfigureAwait(false);
            var empty = await IsAuthoritativelyEmptyWithinTransactionAsync(conn, tx, generation).ConfigureAwait(false);
            var restore = await conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key = 'pos.restore.needs_sync_review';", transaction: tx).ConfigureAwait(false);
            tx.Commit();
            return new PosPublishedCatalogState(safety.IsSaleSafe, empty, restore == "1");
        }

        public async Task StoreAttemptExactnessAsync(string trustedShopId, string trustedShopCode,
            CatalogExactnessResult rejected, long expectedEpoch, string previousCursor, string previousMode,
            OnlineSyncGeneration generation)
        {
            if (rejected == null) throw new ArgumentNullException(nameof(rejected));
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: false);
            await RequireOnlineGenerationAsync(conn, tx, generation).ConfigureAwait(false);
            await RequireCommitStateAsync(conn, tx, trustedShopId, trustedShopCode, expectedEpoch,
                previousCursor, previousMode).ConfigureAwait(false);
            // These keys describe the rejected candidate, never the live snapshot.
            const string prefix = "pos.catalog.attempt.exactness.";
            await SetAsync(conn, tx, prefix + "code", rejected.Code ?? string.Empty).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "status", rejected.Status.ToString()).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "at", DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
            tx.Commit();
        }

        internal static Task<int> ClearAuthoritativeEmptyWithinTransactionAsync(
            SqliteConnection conn, SqliteTransaction tx)
        {
            return conn.ExecuteAsync("DELETE FROM app_settings WHERE key LIKE @prefix;",
                new { prefix = AuthoritativeEmptyPrefix + "%" }, tx);
        }

        internal static async Task<bool> HasAuthoritativeEmptyScopeAsync(
            SqliteConnection conn, SqliteTransaction tx, RemoteCatalogCommitFence fence)
        {
            return await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1) FROM pos_sync_session_generation current
WHERE current.singleton_id = 1 AND current.active = 1
  AND current.generation_id = @GenerationId AND current.fingerprint = @GenerationFingerprint
  AND current.shop_id = @ShopId AND current.shop_code = @ShopCode
  AND current.pos_session_id = @PosSessionId AND current.shop_device_id = @ShopDeviceId
  AND @ShopId <> '' AND @ShopCode <> ''
  AND (SELECT value FROM app_settings WHERE key = @officialId) = @ShopId
  AND (SELECT value FROM app_settings WHERE key = @officialCode) = @ShopCode;",
                new
                {
                    fence.GenerationId, fence.GenerationFingerprint, fence.ShopId,
                    fence.ShopCode, fence.PosSessionId, fence.ShopDeviceId,
                    officialId = OutboxShopBinding.OfficialShopIdKey,
                    officialCode = OutboxShopBinding.OfficialShopCodeKey
                }, tx).ConfigureAwait(false) == 1;
        }

        internal static async Task StoreAuthoritativeEmptyWithinTransactionAsync(
            SqliteConnection conn, SqliteTransaction tx, RemoteCatalogCommitFence fence,
            string fullRunId, string catalogVersion)
        {
            // A previous nonempty local snapshot may be retained for diagnosis,
            // but must not reopen sales after this authoritative zero response,
            // even if a later failed pull invalidates the observation itself.
            await conn.ExecuteAsync("DELETE FROM app_settings WHERE key = @key;",
                new { key = SaleSafeAtKey }, tx).ConfigureAwait(false);
            await SetAsync(conn, tx, FullRefreshRequiredAfterEmptyKey, "1").ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM app_settings WHERE key LIKE 'pos.catalog.attempt.exactness.%';", transaction: tx).ConfigureAwait(false);
            var prefix = AuthoritativeEmptyPrefix;
            await SetAsync(conn, tx, prefix + "generation_id", fence.GenerationId).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "generation_fingerprint", fence.GenerationFingerprint).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "shop_id", fence.ShopId).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "shop_code", fence.ShopCode).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "epoch", fence.ExpectedEpoch.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "previous_cursor", fence.ExpectedPreviousCursor).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "previous_mode", fence.ExpectedPreviousMode).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "committed_revision",
                await GetAsync(conn, tx, CommittedRevisionKey).ConfigureAwait(false)).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "full_run_id", fullRunId).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "catalog_version", catalogVersion).ConfigureAwait(false);
            await SetAsync(conn, tx, prefix + "observed_at", DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
        }
    }
}
