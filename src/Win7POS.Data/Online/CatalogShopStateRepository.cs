using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Online
{
    public sealed class CatalogShopStateRepository
    {
        public const string BoundShopCodeKey = "pos.catalog.bound_shop_code";
        public const string BoundShopIdKey = "pos.catalog.bound_shop_id";
        public const string CompletenessCodeKey = "pos.catalog.exactness.code";
        public const string CompletenessStatusKey = "pos.catalog.exactness.status";
        public const string ExactnessActiveCategoriesKey = "pos.catalog.exactness.actual_active_categories";
        public const string ExactnessActiveProductsKey = "pos.catalog.exactness.actual_active_products";
        public const string ExactnessActiveSuppliersKey = "pos.catalog.exactness.actual_active_suppliers";
        public const string ExactnessCatalogVersionKey = "pos.catalog.exactness.catalog_version";
        public const string ExactnessCursorFingerprintKey = "pos.catalog.exactness.cursor_fingerprint";
        public const string ExactnessEvaluatedAtKey = "pos.catalog.exactness.evaluated_at";
        public const string ExactnessPagesKey = "pos.catalog.exactness.pages";
        public const string ExactnessPrefix = "pos.catalog.exactness.";
        public const string ExactnessShopCodeKey = "pos.catalog.exactness.shop_code";
        public const string ExactnessShopIdKey = "pos.catalog.exactness.shop_id";
        public const string ExactnessVerifiedAtKey = "pos.catalog.exactness.verified_at";
        public const string InitialCompletedAtKey = "pos.catalog.initial_completed_at";
        public const string LastSyncAtKey = "pos.catalog.last_sync_at";
        public const string LastSyncCursorKey = "pos.catalog.last_sync_cursor";
        public const string LastSyncModeKey = "pos.catalog.last_sync_mode";
        public const string RepairRequiredKey = "pos.catalog.exactness.repair_required";
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

                    // Exactness evidence is shop-bound. A transition deliberately removes
                    // the catalog binding, so stale diagnostics must not follow the next shop.
                    await ClearExactnessAsync(conn, tx).ConfigureAwait(false);

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
                await ClearExactnessAsync(conn, tx).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public async Task RequestFullRepairAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            using (await new CatalogShopTransitionBarrier(_factory).EnterAsync().ConfigureAwait(false))
            {
                await RequestFullRepairWhileBarrierHeldAsync(
                    trustedShopId,
                    trustedShopCode,
                    expectedEpoch).ConfigureAwait(false);
            }
        }

        // Use only from a flow that already owns CatalogShopTransitionBarrier. The public
        // RequestFullRepairAsync wrapper is the safe entry point for all other callers.
        public async Task RequestFullRepairWhileBarrierHeldAsync(
            string trustedShopId,
            string trustedShopCode,
            long expectedEpoch)
        {
            var normalizedShopId = Normalize(trustedShopId);
            var normalizedShopCode = OutboxShopBinding.NormalizeCode(trustedShopCode);
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    normalizedShopId,
                    normalizedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (@CursorKey, @SaleSafeKey, @InitialCompletedKey);",
                    new
                    {
                        CursorKey = LastSyncCursorKey,
                        InitialCompletedKey = InitialCompletedAtKey,
                        SaleSafeKey = SaleSafeAtKey
                    },
                    tx).ConfigureAwait(false);
                await ClearExactnessAsync(conn, tx).ConfigureAwait(false);
                var requestedAt = DateTimeOffset.UtcNow.ToString("O");
                await SetAsync(conn, tx, CompletenessStatusKey, CatalogCompletenessStatus.Unverified.ToString()).ConfigureAwait(false);
                await SetAsync(conn, tx, CompletenessCodeKey, "catalog_full_repair_requested").ConfigureAwait(false);
                await SetAsync(conn, tx, RepairRequiredKey, "1").ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessEvaluatedAtKey, requestedAt).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessVerifiedAtKey, string.Empty).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopIdKey, SafeOpaque(normalizedShopId, 128)).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopCodeKey, normalizedShopCode).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public async Task StoreExactnessAsync(
            string trustedShopId,
            string trustedShopCode,
            CatalogExactnessResult exactness,
            long expectedEpoch = -1)
        {
            if (exactness == null) throw new ArgumentNullException(nameof(exactness));

            // Re-evaluate instead of trusting a mutable DTO supplied by a caller. This keeps
            // the persisted Verified state tied to the fail-closed verifier invariants.
            var canonical = CatalogExactnessVerifier.Evaluate(
                exactness.Expected,
                exactness.Audit,
                exactness.Context);
            var audit = canonical.Audit ?? new CatalogFullRefreshResult();
            var context = canonical.Context ?? new CatalogExactnessRunContext();
            var expected = canonical.Expected;
            var normalizedShopId = Normalize(trustedShopId);
            var normalizedShopCode = OutboxShopBinding.NormalizeCode(trustedShopCode);

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await RequireBindingAsync(
                    conn,
                    tx,
                    normalizedShopId,
                    normalizedShopCode,
                    expectedEpoch).ConfigureAwait(false);
                await ClearExactnessAsync(conn, tx).ConfigureAwait(false);

                await SetAsync(conn, tx, CompletenessStatusKey, canonical.Status.ToString()).ConfigureAwait(false);
                await SetAsync(conn, tx, CompletenessCodeKey, SafeCode(canonical.Code)).ConfigureAwait(false);
                await SetAsync(conn, tx, RepairRequiredKey, canonical.RepairRequired ? "1" : "0").ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessEvaluatedAtKey, canonical.EvaluatedAt).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessVerifiedAtKey,
                    canonical.Status == CatalogCompletenessStatus.Verified ? canonical.EvaluatedAt : string.Empty)
                    .ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopIdKey, SafeOpaque(normalizedShopId, 128)).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessShopCodeKey, normalizedShopCode).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessCatalogVersionKey,
                    SafeOpaque(context.CatalogVersion, 128)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessCursorFingerprintKey,
                    Fingerprint(context.SyncCursor)).ConfigureAwait(false);
                await SetAsync(conn, tx, ExactnessPrefix + "sync_mode", SafeCode(context.SyncMode)).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPagesKey, context.Pages).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duration_ms", context.DurationMilliseconds).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "rows_per_second",
                    canonical.RowsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)).ConfigureAwait(false);

                await SetLongAsync(conn, tx, ExactnessPrefix + "received_products", canonical.ProductsReceived).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_categories", canonical.CategoriesReceived).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_suppliers", canonical.SuppliersReceived).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_prices", canonical.PricesReceived).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "received_tombstones", context.TombstonesReceived).ConfigureAwait(false);

                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_products", expected?.Products).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_active_products", expected?.ActiveProducts).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_categories", expected?.Categories).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_suppliers", expected?.Suppliers).ConfigureAwait(false);
                await SetNullableLongAsync(conn, tx, ExactnessPrefix + "expected_prices", expected?.Prices).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "expected_checksum_fingerprint",
                    Fingerprint(expected?.Checksum)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "expected_checksum_algorithm",
                    SafeOpaque(expected?.ChecksumAlgorithm, 64)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "actual_checksum_fingerprint",
                    Fingerprint(context.ActualChecksum)).ConfigureAwait(false);
                await SetAsync(
                    conn,
                    tx,
                    ExactnessPrefix + "actual_checksum_algorithm",
                    SafeOpaque(context.ActualChecksumAlgorithm, 64)).ConfigureAwait(false);

                await SetLongAsync(conn, tx, ExactnessActiveProductsKey, audit.ActiveRemoteProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "actual_distinct_product_ids", audit.DistinctActiveRemoteProductIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessActiveCategoriesKey, audit.ActiveRemoteCategories).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "actual_distinct_category_ids", audit.DistinctActiveRemoteCategoryIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessActiveSuppliersKey, audit.ActiveRemoteSuppliers).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "actual_distinct_supplier_ids", audit.DistinctActiveRemoteSupplierIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_product_ids", audit.DuplicateAuthoritativeProductIds + audit.DuplicateActiveRemoteProductIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_category_ids", audit.DuplicateAuthoritativeCategoryIds + audit.DuplicateActiveRemoteCategoryIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_supplier_ids", audit.DuplicateAuthoritativeSupplierIds + audit.DuplicateActiveRemoteSupplierIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "duplicate_active_barcodes", audit.DuplicateActiveBarcodes).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_product_ids", audit.InvalidAuthoritativeProductIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_category_ids", audit.InvalidAuthoritativeCategoryIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_supplier_ids", audit.InvalidAuthoritativeSupplierIds).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "products_without_meta", audit.RemoteProductsWithoutMeta).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "invalid_active_products", audit.InvalidActiveRemoteProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "orphan_category_refs", audit.OrphanCategoryReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "orphan_supplier_refs", audit.OrphanSupplierReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_category_refs", audit.InactiveCategoryReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_supplier_refs", audit.InactiveSupplierReferences).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "pending_prices", audit.PendingRemotePrices).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_products", audit.InactiveRemoteProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_categories", audit.InactiveRemoteCategories).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "inactive_suppliers", audit.InactiveRemoteSuppliers).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "non_authoritative_active_products", audit.NonAuthoritativeActiveProducts).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "non_authoritative_active_categories", audit.NonAuthoritativeActiveCategories).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "non_authoritative_active_suppliers", audit.NonAuthoritativeActiveSuppliers).ConfigureAwait(false);
                await SetLongAsync(conn, tx, ExactnessPrefix + "remote_price_history_rows", audit.RemotePriceHistoryRows).ConfigureAwait(false);

                if (canonical.RepairRequired || canonical.Status == CatalogCompletenessStatus.Mismatch)
                {
                    await conn.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key IN (@SaleSafeKey, @InitialCompletedKey);",
                        new
                        {
                            InitialCompletedKey = InitialCompletedAtKey,
                            SaleSafeKey = SaleSafeAtKey
                        },
                        tx).ConfigureAwait(false);
                }

                tx.Commit();
            }
        }

        public async Task<CatalogExactnessState> LoadExactnessAsync()
        {
            using (var conn = _factory.Open())
            {
                var rawStatus = await GetAsync(conn, null, CompletenessStatusKey).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(rawStatus))
                {
                    return CatalogExactnessState.Unverified("catalog_exactness_not_evaluated");
                }

                if (!Enum.TryParse(rawStatus, true, out CatalogCompletenessStatus status))
                {
                    return CatalogExactnessState.Mismatch("catalog_exactness_state_invalid");
                }

                var exactnessShopId = await GetAsync(conn, null, ExactnessShopIdKey).ConfigureAwait(false);
                var exactnessShopCode = await GetAsync(conn, null, ExactnessShopCodeKey).ConfigureAwait(false);
                var boundShopId = await GetAsync(conn, null, BoundShopIdKey).ConfigureAwait(false);
                var boundShopCode = await GetAsync(conn, null, BoundShopCodeKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    exactnessShopId,
                    exactnessShopCode,
                    boundShopId,
                    boundShopCode)))
                {
                    return CatalogExactnessState.Mismatch("catalog_exactness_shop_binding_mismatch");
                }

                return new CatalogExactnessState
                {
                    ActiveCategories = ParseLong(await GetAsync(conn, null, ExactnessActiveCategoriesKey).ConfigureAwait(false)),
                    ActiveProducts = ParseLong(await GetAsync(conn, null, ExactnessActiveProductsKey).ConfigureAwait(false)),
                    ActiveSuppliers = ParseLong(await GetAsync(conn, null, ExactnessActiveSuppliersKey).ConfigureAwait(false)),
                    CatalogVersion = await GetAsync(conn, null, ExactnessCatalogVersionKey).ConfigureAwait(false) ?? string.Empty,
                    Code = await GetAsync(conn, null, CompletenessCodeKey).ConfigureAwait(false) ?? string.Empty,
                    EvaluatedAt = await GetAsync(conn, null, ExactnessEvaluatedAtKey).ConfigureAwait(false) ?? string.Empty,
                    RepairRequired = ParseBool(await GetAsync(conn, null, RepairRequiredKey).ConfigureAwait(false)),
                    ShopCode = OutboxShopBinding.NormalizeCode(exactnessShopCode),
                    ShopId = Normalize(exactnessShopId),
                    Status = status,
                    VerifiedAt = await GetAsync(conn, null, ExactnessVerifiedAtKey).ConfigureAwait(false) ?? string.Empty
                };
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
                await RequireExactnessSaleSafetyAsync(
                    conn,
                    tx,
                    trustedShopId,
                    trustedShopCode).ConfigureAwait(false);
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
                if (string.IsNullOrWhiteSpace(saleSafeAt) ||
                    !string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                        boundId,
                        boundCode,
                        officialId,
                        officialCode)))
                {
                    return false;
                }

                var exactnessStatus = await GetAsync(conn, null, CompletenessStatusKey).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(exactnessStatus))
                {
                    // Backward compatibility for databases created before exactness evidence.
                    return true;
                }

                if (!Enum.TryParse(exactnessStatus, true, out CatalogCompletenessStatus status) ||
                    status == CatalogCompletenessStatus.Mismatch ||
                    ParseBool(await GetAsync(conn, null, RepairRequiredKey).ConfigureAwait(false)))
                {
                    return false;
                }

                var exactnessId = await GetAsync(conn, null, ExactnessShopIdKey).ConfigureAwait(false);
                var exactnessCode = await GetAsync(conn, null, ExactnessShopCodeKey).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                    exactnessId,
                    exactnessCode,
                    officialId,
                    officialCode));
            }
        }

        private static Task<int> ClearExactnessAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx)
        {
            return conn.ExecuteAsync(
                "DELETE FROM app_settings WHERE key LIKE @prefix;",
                new { prefix = ExactnessPrefix + "%" },
                tx);
        }

        private static string Fingerprint(string value)
        {
            var normalized = Normalize(value);
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static long ParseLong(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static bool ParseBool(string value)
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task RequireExactnessSaleSafetyAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string trustedShopId,
            string trustedShopCode)
        {
            var exactnessStatus = await GetAsync(conn, tx, CompletenessStatusKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(exactnessStatus))
            {
                return;
            }

            if (!Enum.TryParse(exactnessStatus, true, out CatalogCompletenessStatus status) ||
                status == CatalogCompletenessStatus.Mismatch ||
                ParseBool(await GetAsync(conn, tx, RepairRequiredKey).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("Catalog exactness requires repair before sale-safe can be stored.");
            }

            var exactnessShopId = await GetAsync(conn, tx, ExactnessShopIdKey).ConfigureAwait(false);
            var exactnessShopCode = await GetAsync(conn, tx, ExactnessShopCodeKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(OutboxShopBinding.GetMismatchCode(
                exactnessShopId,
                exactnessShopCode,
                trustedShopId,
                trustedShopCode)))
            {
                throw new InvalidOperationException("Catalog exactness shop binding mismatch.");
            }
        }

        private static string SafeCode(string value)
        {
            var normalized = Normalize(value);
            var safe = new StringBuilder(Math.Min(normalized.Length, 80));
            foreach (var character in normalized)
            {
                if (safe.Length >= 80)
                {
                    break;
                }

                if (char.IsLetterOrDigit(character) ||
                    character == '_' ||
                    character == '-' ||
                    character == '.')
                {
                    safe.Append(character);
                }
            }

            return safe.Length == 0 ? "catalog_exactness_unknown" : safe.ToString();
        }

        private static string SafeOpaque(string value, int maximumLength)
        {
            var normalized = Normalize(value);
            var safe = new StringBuilder(Math.Min(normalized.Length, maximumLength));
            foreach (var character in normalized)
            {
                if (safe.Length >= maximumLength)
                {
                    break;
                }

                if (!char.IsControl(character))
                {
                    safe.Append(character);
                }
            }

            return safe.ToString();
        }

        private static Task<int> SetLongAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key,
            long value)
        {
            return SetAsync(conn, tx, key, value.ToString(CultureInfo.InvariantCulture));
        }

        private static Task<int> SetNullableLongAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string key,
            long? value)
        {
            return SetAsync(
                conn,
                tx,
                key,
                value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
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

    public sealed class CatalogExactnessState
    {
        public long ActiveCategories { get; set; }
        public long ActiveProducts { get; set; }
        public long ActiveSuppliers { get; set; }
        public string CatalogVersion { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string EvaluatedAt { get; set; } = string.Empty;
        public bool RepairRequired { get; set; }
        public string ShopCode { get; set; } = string.Empty;
        public string ShopId { get; set; } = string.Empty;
        public CatalogCompletenessStatus Status { get; set; }
        public string VerifiedAt { get; set; } = string.Empty;

        public static CatalogExactnessState Mismatch(string code)
        {
            return new CatalogExactnessState
            {
                Code = code ?? string.Empty,
                RepairRequired = true,
                Status = CatalogCompletenessStatus.Mismatch
            };
        }

        public static CatalogExactnessState Unverified(string code)
        {
            return new CatalogExactnessState
            {
                Code = code ?? string.Empty,
                Status = CatalogCompletenessStatus.Unverified
            };
        }
    }
}
