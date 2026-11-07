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
        // An observation, never a sale-safety or authentication capability. Keeping
        // it under exactness also invalidates it on repair, relink and restore.
        public const string AuthoritativeEmptyPrefix = ExactnessPrefix + "empty.";

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
            tx.Commit();
            return proof == 1;
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
