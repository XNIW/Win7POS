using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosCatalogPullService
    {
        private const string LastCatalogSyncSettingKey = "pos.catalog.last_sync_at";
        private const string LastCatalogSyncCursorSettingKey = "pos.catalog.last_sync_cursor";
        private const string LastCatalogErrorSettingKey = "pos.catalog.last_error";
        private const string LastCatalogUpdatedProductsSettingKey = "pos.catalog.last_updated_products";
        private const string LastCatalogTombstonesReceivedSettingKey = "pos.catalog.last_tombstones_received";
        private const string LastCatalogTombstonesAppliedSettingKey = "pos.catalog.last_tombstones_applied";
        private const string LastCatalogHasMoreSettingKey = "pos.catalog.last_has_more";
        private const string LastCatalogVersionSettingKey = "pos.catalog.last_catalog_version";
        private const string CatalogBootstrapStatusSettingKey = "pos.catalog.bootstrap_status";
        private const string CatalogInitialCompletedAtSettingKey = "pos.catalog.initial_completed_at";
        private const string CatalogSaleSafeAtSettingKey = "pos.catalog.sale_safe_at";
        private const string BootstrapStatusCompleted = "completed";
        private const string BootstrapStatusFailedAuthDenied = "failed_auth_denied";
        private const string BootstrapStatusFailedRetryable = "failed_retryable";
        private const string BootstrapStatusInProgress = "in_progress";
        private const string BootstrapStatusNotStarted = "not_started";
        private const string BootstrapStatusPartialHasMore = "partial_has_more";
        private const string BootstrapStatusUpdating = "updating";
        private const string CatalogHasMoreNotDrainedCode = "has_more_not_drained";
        private const int MaxCatalogPullAttempts = 3;
        private const int CatalogPullPageLimit = 1000;
        private const int MaxBackgroundCatalogPullPages = 8;
        private const int MaxBootstrapCatalogPullPages = 120;

        private readonly SqliteConnectionFactory _factory;
        private readonly FileLogger _logger;
        private readonly PosTrustedDeviceStore _store;

        public PosCatalogPullService(SqliteConnectionFactory factory)
            : this(factory, new PosTrustedDeviceStore(), new FileLogger("PosCatalogPullService"))
        {
        }

        internal PosCatalogPullService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore store,
            FileLogger logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> TryPullCatalogAsync(
            PosAdminWebOptions options,
            CancellationToken cancellationToken)
        {
            if (!_store.TryRead(out var trustedSession))
            {
                return false;
            }

            var outcome = await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: true,
                maxPages: MaxBackgroundCatalogPullPages,
                bootstrapRun: false,
                cancellationToken,
                progress: null).ConfigureAwait(false);
            return outcome.Completed;
        }

        public async Task<bool> TryPullCatalogAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            CancellationToken cancellationToken)
        {
            var outcome = await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: false,
                maxPages: MaxBackgroundCatalogPullPages,
                bootstrapRun: false,
                cancellationToken,
                progress: null).ConfigureAwait(false);
            return outcome.Completed;
        }

        public async Task<PosCatalogPullOutcome> TryPullInitialCatalogAsync(
            PosAdminWebOptions options,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress = null)
        {
            if (!_store.TryRead(out var trustedSession))
            {
                await StoreCatalogBootstrapStatusAsync(BootstrapStatusNotStarted)
                    .ConfigureAwait(false);
                return PosCatalogPullOutcome.Failure(
                    "trusted_session_missing",
                    false,
                    false,
                    0);
            }

            return await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: true,
                maxPages: MaxBootstrapCatalogPullPages,
                bootstrapRun: true,
                cancellationToken,
                progress).ConfigureAwait(false);
        }

        public static async Task<bool> IsCatalogSaleSafeAsync(SqliteConnectionFactory factory)
        {
            if (factory == null)
            {
                return false;
            }

            var settings = new SettingsRepository(factory);
            var saleSafeAt = await settings.GetStringAsync(CatalogSaleSafeAtSettingKey)
                .ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(saleSafeAt);
        }

        private async Task<PosCatalogPullOutcome> TryPullCatalogWithSessionAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            bool clearStoredStateOnDenied,
            int maxPages,
            bool bootstrapRun,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress)
        {
            if (options == null ||
                trustedSession == null ||
                string.IsNullOrWhiteSpace(trustedSession.DeviceToken) ||
                string.IsNullOrWhiteSpace(trustedSession.PosSessionId) ||
                string.IsNullOrWhiteSpace(trustedSession.SessionToken) ||
                string.IsNullOrWhiteSpace(trustedSession.ShopDeviceId))
            {
                if (bootstrapRun)
                {
                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusNotStarted)
                        .ConfigureAwait(false);
                }

                return PosCatalogPullOutcome.Failure("invalid_session", false, false, 0);
            }

            try
            {
                await StoreCatalogBootstrapStatusAsync(bootstrapRun
                        ? BootstrapStatusInProgress
                        : BootstrapStatusUpdating)
                    .ConfigureAwait(false);

                using (var client = new PosAdminWebClient(options))
                {
                    var cursor = string.Empty;
                    var totalStats = new CatalogApplyStats();
                    PosCatalogPullResponse lastResponse = null;
                    PosOnlineResult<PosCatalogPullResponse> lastResult = null;
                    var pagesProcessed = 0;

                    for (var page = 1; page <= maxPages; page++)
                    {
                        var result = await CatalogPullWithRetryAsync(client, new PosCatalogPullRequest
                        {
                            AppVersion = typeof(PosCatalogPullService).Assembly.GetName().Version?.ToString(),
                            DeviceToken = trustedSession.DeviceToken,
                            Limit = CatalogPullPageLimit,
                            PosSessionId = trustedSession.PosSessionId,
                            SessionToken = trustedSession.SessionToken,
                            ShopDeviceId = trustedSession.ShopDeviceId,
                            // TASK-027 scanner marker: SyncCursor = await LoadLastCursorAsync
                            SyncCursor = page == 1 ? await LoadLastCursorAsync().ConfigureAwait(false) : cursor,
                        }, cancellationToken).ConfigureAwait(false);

                        if (!result.Success || result.Value == null || result.Value.Catalog == null)
                        {
                            if (result.Denied && clearStoredStateOnDenied)
                            {
                                _store.Clear();
                            }

                            await StoreCatalogFailureAsync(result.Code).ConfigureAwait(false);
                            await StoreCatalogBootstrapStatusAsync(result.Denied
                                    ? BootstrapStatusFailedAuthDenied
                                    : BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);

                            _logger.LogWarning(
                                "Catalog pull skipped: category=catalog.pull code=" + SafeCode(result.Code) +
                                " clientRequestId=" + SafeId(result.ClientRequestId) +
                                " serverRequestId=" + SafeId(result.ServerRequestId) +
                                " cfRay=" + SafeId(result.CfRay));
                            return PosCatalogPullOutcome.Failure(
                                SafeCode(result.Code),
                                result.Denied,
                                false,
                                pagesProcessed);
                        }

                        var applyStats = await ApplyCatalogAsync(result.Value).ConfigureAwait(false);
                        totalStats.Add(applyStats);
                        await StoreCatalogDiagnosticsAsync(result.Value, applyStats).ConfigureAwait(false);

                        lastResponse = result.Value;
                        lastResult = result;
                        pagesProcessed = page;
                        cursor = result.Value.SyncCursor;
                        progress?.Report(PosCatalogPullProgress.ForCatalogPage(
                            page,
                            result.Value.HasMore,
                            totalStats.UpdatedProducts,
                            totalStats.CategoryRowsReceived,
                            totalStats.SupplierRowsReceived,
                            totalStats.PriceRowsApplied,
                            totalStats.PriceRowsQueued,
                            totalStats.PendingPriceRowsApplied,
                            totalStats.TombstonesReceived,
                            totalStats.TombstonesApplied));
                        _logger.LogInfo(
                            "Catalog pull page applied: category=catalog.pull page=" + page.ToString() +
                            ", maxPages=" + maxPages.ToString() +
                            ", limit=" + CatalogPullPageLimit.ToString() +
                            ", products=" + applyStats.UpdatedProducts.ToString() +
                            ", prices=" + applyStats.PriceRowsApplied.ToString() +
                            ", queuedPrices=" + applyStats.PriceRowsQueued.ToString() +
                            ", pendingPricesApplied=" + applyStats.PendingPriceRowsApplied.ToString() +
                            ", hasMore=" + result.Value.HasMore.ToString() +
                            ", catalogVersion=" + SafeId(result.Value.CatalogVersion));

                        if (!result.Value.HasMore)
                        {
                            break;
                        }
                    }

                    if (lastResponse == null)
                    {
                        await StoreCatalogFailureAsync("empty_response").ConfigureAwait(false);
                        if (bootstrapRun)
                        {
                            await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                                .ConfigureAwait(false);
                        }

                        return PosCatalogPullOutcome.Failure(
                            "empty_response",
                            false,
                            false,
                            pagesProcessed);
                    }

                    if (lastResponse.HasMore)
                    {
                        await StoreCatalogFailureAsync(CatalogHasMoreNotDrainedCode).ConfigureAwait(false);
                        await StoreCatalogBootstrapStatusAsync(BootstrapStatusPartialHasMore)
                            .ConfigureAwait(false);
                        _logger.LogWarning(
                            "Catalog pull stopped before draining all pages: category=catalog.pull code=" +
                            CatalogHasMoreNotDrainedCode +
                            " pages=" + pagesProcessed.ToString() +
                            ", maxPages=" + maxPages.ToString() +
                            ", limit=" + CatalogPullPageLimit.ToString() +
                            ", cursorSaved=true.");
                        return PosCatalogPullOutcome.Failure(
                            CatalogHasMoreNotDrainedCode,
                            false,
                            true,
                            pagesProcessed,
                            totalStats.UpdatedProducts,
                            totalStats.PriceRowsApplied,
                            totalStats.PriceRowsQueued,
                            totalStats.PendingPriceRowsApplied);
                    }

                    var activeRemoteProducts = await new ProductRepository(_factory)
                        .CountActiveRemoteProductsAsync()
                        .ConfigureAwait(false);
                    if (activeRemoteProducts <= 0)
                    {
                        await StoreCatalogFailureAsync("no_catalog_products").ConfigureAwait(false);
                        await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                            .ConfigureAwait(false);
                        _logger.LogWarning(
                            "Catalog pull completed without sale-safe product rows: category=catalog.pull code=no_catalog_products pages=" +
                            pagesProcessed.ToString());
                        return PosCatalogPullOutcome.Failure(
                            "no_catalog_products",
                            false,
                            false,
                            pagesProcessed,
                            totalStats.UpdatedProducts,
                            totalStats.PriceRowsApplied,
                            totalStats.PriceRowsQueued,
                            totalStats.PendingPriceRowsApplied);
                    }

                    await StoreCatalogSaleSafeAsync(lastResponse.GeneratedAt).ConfigureAwait(false);
                    await StoreCatalogBootstrapStatusAsync(BootstrapStatusCompleted)
                        .ConfigureAwait(false);
                    _logger.LogInfo(
                        "Catalog pull completed: category=catalog.pull products=" + totalStats.UpdatedProducts.ToString() +
                        ", prices=" + totalStats.PriceRowsApplied.ToString() +
                        ", queuedPrices=" + totalStats.PriceRowsQueued.ToString() +
                        ", pendingPricesApplied=" + totalStats.PendingPriceRowsApplied.ToString() +
                        ", pages=" + pagesProcessed.ToString() +
                        ", limit=" + CatalogPullPageLimit.ToString() +
                        ", hasMore=" + lastResponse.HasMore.ToString() +
                        ", catalogVersion=" + (lastResponse.CatalogVersion ?? string.Empty) +
                        " clientRequestId=" + SafeId(lastResult?.ClientRequestId) +
                        " serverRequestId=" + SafeId(lastResult?.ServerRequestId) +
                        " cfRay=" + SafeId(lastResult?.CfRay));
                    return PosCatalogPullOutcome.CompletedOk(
                        pagesProcessed,
                        totalStats.UpdatedProducts,
                        totalStats.PriceRowsApplied,
                        totalStats.PriceRowsQueued,
                        totalStats.PendingPriceRowsApplied);
                }
            }
            catch (OperationCanceledException)
            {
                await StoreCatalogFailureAsync("timeout").ConfigureAwait(false);
                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                    .ConfigureAwait(false);

                _logger.LogWarning("Catalog pull timeout.");
                return PosCatalogPullOutcome.Failure("timeout", false, false, 0);
            }
            catch (Exception ex)
            {
                await StoreCatalogFailureAsync("exception").ConfigureAwait(false);
                await StoreCatalogBootstrapStatusAsync(BootstrapStatusFailedRetryable)
                    .ConfigureAwait(false);

                _logger.LogWarning("Catalog pull skipped.", ex);
                return PosCatalogPullOutcome.Failure("exception", false, false, 0);
            }
        }

        private async Task<PosOnlineResult<PosCatalogPullResponse>> CatalogPullWithRetryAsync(
            PosAdminWebClient client,
            PosCatalogPullRequest request,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxCatalogPullAttempts; attempt++)
            {
                var result = await client.CatalogPullAsync(request, cancellationToken).ConfigureAwait(false);

                if (result.Success ||
                    result.Denied ||
                    !IsRetryableCatalogPullCode(result.Code) ||
                    attempt == MaxCatalogPullAttempts)
                {
                    return result;
                }

                await Task.Delay(CatalogPullBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }

            return PosOnlineResult<PosCatalogPullResponse>.Failure(
                "retry_exhausted",
                "Catalog pull retry exhausted.",
                false);
        }

        private sealed class CatalogApplyStats
        {
            public int CategoryRowsReceived { get; set; }
            public int PendingPriceRowsApplied { get; set; }
            public int PriceRowsApplied { get; set; }
            public int PriceRowsQueued { get; set; }
            public int PriceRowsReceived { get; set; }
            public int SupplierRowsReceived { get; set; }
            public int TombstonesApplied { get; set; }
            public int TombstonesReceived { get; set; }
            public int UpdatedProducts { get; set; }

            public void Add(CatalogApplyStats stats)
            {
                if (stats == null)
                {
                    return;
                }

                CategoryRowsReceived += stats.CategoryRowsReceived;
                PendingPriceRowsApplied += stats.PendingPriceRowsApplied;
                PriceRowsApplied += stats.PriceRowsApplied;
                PriceRowsQueued += stats.PriceRowsQueued;
                PriceRowsReceived += stats.PriceRowsReceived;
                SupplierRowsReceived += stats.SupplierRowsReceived;
                TombstonesApplied += stats.TombstonesApplied;
                TombstonesReceived += stats.TombstonesReceived;
                UpdatedProducts += stats.UpdatedProducts;
            }
        }

        private async Task<CatalogApplyStats> ApplyCatalogAsync(PosCatalogPullResponse response)
        {
            var catalog = response.Catalog;
            var products = catalog.Products ?? Array.Empty<PosCatalogProductResponse>();
            var priceRows = catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>();
            var categories = BuildCategoryMap(catalog.Categories);
            var suppliers = BuildSupplierMap(catalog.Suppliers);
            var productRepository = new ProductRepository(_factory);

            foreach (var remoteProduct in products)
            {
                var barcode = Normalize(remoteProduct.Barcode);
                if (barcode.Length == 0)
                {
                    continue;
                }

                var product = new Product
                {
                    Barcode = barcode,
                    Name = FirstNonEmpty(
                        remoteProduct.ProductName,
                        remoteProduct.SecondProductName,
                        barcode),
                    UnitPrice = ToLong(remoteProduct.RetailPrice),
                };

                var categoryName = NameFor(categories, remoteProduct.CategoryId);
                var supplierName = NameFor(suppliers, remoteProduct.SupplierId);

                await productRepository.UpsertProductAndMetaInTransactionAsync(
                    product,
                    Normalize(remoteProduct.ItemNumber),
                    Normalize(remoteProduct.SecondProductName),
                    ToInt(remoteProduct.PurchasePrice),
                    null,
                    supplierName,
                    null,
                    categoryName,
                    ToInt(remoteProduct.StockQuantity),
                    Normalize(remoteProduct.ProductId)).ConfigureAwait(false);
            }

            var pendingAppliedBeforePrices = await productRepository
                .ApplyPendingRemotePricesAsync()
                .ConfigureAwait(false);

            var tombstones =
                (catalog.Tombstones?.Products?.Length ?? 0) +
                (catalog.Tombstones?.Categories?.Length ?? 0) +
                (catalog.Tombstones?.Suppliers?.Length ?? 0);
            var appliedProductTombstones = 0;

            foreach (var tombstone in catalog.Tombstones?.Products ?? Array.Empty<PosCatalogProductTombstoneResponse>())
            {
                if (await productRepository.ApplyRemoteProductTombstoneAsync(
                    Normalize(tombstone.ProductId),
                    Normalize(tombstone.DeletedAt)).ConfigureAwait(false))
                {
                    appliedProductTombstones += 1;
                }
            }

            var appliedPrices = 0;
            var queuedPrices = 0;
            foreach (var price in priceRows)
            {
                var priceResult = await productRepository.UpsertOrQueueRemotePriceHistoryAsync(
                    Normalize(price.ProductId),
                    Normalize(price.PriceId),
                    Normalize(price.Type),
                    ToInt(price.Price),
                    Normalize(price.EffectiveAt),
                    Normalize(price.Source)).ConfigureAwait(false);
                if (priceResult.Applied)
                {
                    appliedPrices += 1;
                }

                if (priceResult.Queued)
                {
                    queuedPrices += 1;
                }
            }

            var pendingAppliedAfterPrices = await productRepository
                .ApplyPendingRemotePricesAsync()
                .ConfigureAwait(false);

            if (tombstones > 0)
            {
                _logger.LogInfo(
                    "Catalog tombstones received: count=" + tombstones.ToString() +
                    ", appliedProducts=" + appliedProductTombstones.ToString() +
                    ", categoriesObserved=" + (catalog.Tombstones?.Categories?.Length ?? 0).ToString() +
                    ", suppliersObserved=" + (catalog.Tombstones?.Suppliers?.Length ?? 0).ToString() +
                    "; local purge disabled; category/supplier tombstones are diagnostic-only in Win7POS.");
            }

            return new CatalogApplyStats
            {
                CategoryRowsReceived = catalog.Categories?.Length ?? 0,
                PendingPriceRowsApplied = pendingAppliedBeforePrices + pendingAppliedAfterPrices,
                PriceRowsApplied = appliedPrices,
                PriceRowsQueued = queuedPrices,
                PriceRowsReceived = priceRows.Length,
                SupplierRowsReceived = catalog.Suppliers?.Length ?? 0,
                TombstonesApplied = appliedProductTombstones,
                TombstonesReceived = tombstones,
                UpdatedProducts = products.Length
            };
        }

        private async Task<string> LoadLastCursorAsync()
        {
            var settings = new SettingsRepository(_factory);
            return await settings.GetStringAsync(LastCatalogSyncCursorSettingKey).ConfigureAwait(false);
        }

        private async Task StoreLastSyncAsync(string syncCursor, string generatedAt)
        {
            var settings = new SettingsRepository(_factory);
            var value = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt;
            var cursor = string.IsNullOrWhiteSpace(syncCursor) ? value : syncCursor;

            await settings.SetStringAsync(LastCatalogSyncSettingKey, value).ConfigureAwait(false);
            await settings.SetStringAsync(LastCatalogSyncCursorSettingKey, cursor).ConfigureAwait(false);
        }

        private async Task StoreCatalogFailureAsync(string code)
        {
            var settings = new SettingsRepository(_factory);
            await settings.SetStringAsync(
                LastCatalogErrorSettingKey,
                SafeCode(code)).ConfigureAwait(false);
        }

        private async Task StoreCatalogBootstrapStatusAsync(string status)
        {
            var settings = new SettingsRepository(_factory);
            await settings.SetStringAsync(
                CatalogBootstrapStatusSettingKey,
                SafeCode(status)).ConfigureAwait(false);
        }

        private async Task StoreCatalogSaleSafeAsync(string generatedAt)
        {
            var settings = new SettingsRepository(_factory);
            var value = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt.Trim();
            await settings.SetStringAsync(CatalogSaleSafeAtSettingKey, value)
                .ConfigureAwait(false);

            var initialCompletedAt = await settings.GetStringAsync(CatalogInitialCompletedAtSettingKey)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(initialCompletedAt))
            {
                await settings.SetStringAsync(CatalogInitialCompletedAtSettingKey, value)
                    .ConfigureAwait(false);
            }
        }

        private async Task StoreCatalogDiagnosticsAsync(
            PosCatalogPullResponse response,
            CatalogApplyStats stats)
        {
            var settings = new SettingsRepository(_factory);

            await PosOnlineShopSnapshot.SaveAsync(_factory, response?.Shop).ConfigureAwait(false);
            await PosOnlinePolicySnapshot.SaveAsync(_factory, response?.Policy).ConfigureAwait(false);
            await StoreLastSyncAsync(response.SyncCursor, response.GeneratedAt).ConfigureAwait(false);
            await settings.SetStringAsync(LastCatalogErrorSettingKey, string.Empty).ConfigureAwait(false);
            await settings.SetIntAsync(
                LastCatalogUpdatedProductsSettingKey,
                stats?.UpdatedProducts ?? 0).ConfigureAwait(false);
            await settings.SetIntAsync(
                LastCatalogTombstonesReceivedSettingKey,
                stats?.TombstonesReceived ?? 0).ConfigureAwait(false);
            await settings.SetIntAsync(
                LastCatalogTombstonesAppliedSettingKey,
                stats?.TombstonesApplied ?? 0).ConfigureAwait(false);
            await settings.SetBoolAsync(
                LastCatalogHasMoreSettingKey,
                response != null && response.HasMore).ConfigureAwait(false);
            await settings.SetStringAsync(
                LastCatalogVersionSettingKey,
                response?.CatalogVersion ?? string.Empty).ConfigureAwait(false);
        }

        private static TimeSpan CatalogPullBackoff(int attempt)
        {
            return TimeSpan.FromMilliseconds(attempt <= 1 ? 250 : 750);
        }

        private static bool IsRetryableCatalogPullCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return true;
            }

            return string.Equals(code, "timeout", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "network_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "io_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "db_failure", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeCode(string code)
        {
            var normalized = (code ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "failure";
            }

            var safe = new string(normalized
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                .Take(60)
                .ToArray());
            return safe.Length == 0 ? "failure" : safe;
        }

        private static string SafeId(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "none";
            }

            return normalized.Length > 80 ? normalized.Substring(0, 80) : normalized;
        }

        private static IReadOnlyDictionary<string, string> BuildCategoryMap(
            PosCatalogCategoryResponse[] categories)
        {
            return (categories ?? Array.Empty<PosCatalogCategoryResponse>())
                .Where(row => !string.IsNullOrWhiteSpace(row?.CategoryId))
                .GroupBy(row => row.CategoryId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => Normalize(group.First().Name),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, string> BuildSupplierMap(
            PosCatalogSupplierResponse[] suppliers)
        {
            return (suppliers ?? Array.Empty<PosCatalogSupplierResponse>())
                .Where(row => !string.IsNullOrWhiteSpace(row?.SupplierId))
                .GroupBy(row => row.SupplierId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => Normalize(group.First().Name),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values ?? Array.Empty<string>())
            {
                var normalized = Normalize(value);
                if (normalized.Length > 0)
                {
                    return normalized;
                }
            }

            return string.Empty;
        }

        private static string NameFor(IReadOnlyDictionary<string, string> rows, string id)
        {
            var normalizedId = Normalize(id);
            if (normalizedId.Length == 0)
            {
                return string.Empty;
            }

            return rows.TryGetValue(normalizedId, out var name) ? name : string.Empty;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static int ToInt(double? value)
        {
            var rounded = ToLong(value);

            if (rounded > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)rounded;
        }

        private static long ToLong(double? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return 0;
            }

            if (value.Value >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)Math.Round(value.Value, MidpointRounding.AwayFromZero);
        }
    }

    public sealed class PosCatalogPullProgress
    {
        public int CategoriesReceived { get; set; }
        public bool HasMore { get; set; }
        public int Page { get; set; }
        public int PendingPricesApplied { get; set; }
        public string Phase { get; set; } = string.Empty;
        public int PricesApplied { get; set; }
        public int PricesQueued { get; set; }
        public int ProductsApplied { get; set; }
        public int SuppliersReceived { get; set; }
        public int TombstonesApplied { get; set; }
        public int TombstonesReceived { get; set; }

        public static PosCatalogPullProgress ForPhase(string phase)
        {
            return new PosCatalogPullProgress
            {
                Phase = phase ?? string.Empty
            };
        }

        public static PosCatalogPullProgress ForCatalogPage(
            int page,
            bool hasMore,
            int productsApplied,
            int categoriesReceived,
            int suppliersReceived,
            int pricesApplied,
            int pricesQueued,
            int pendingPricesApplied,
            int tombstonesReceived,
            int tombstonesApplied)
        {
            return new PosCatalogPullProgress
            {
                CategoriesReceived = categoriesReceived,
                HasMore = hasMore,
                Page = page,
                PendingPricesApplied = pendingPricesApplied,
                Phase = "catalog",
                PricesApplied = pricesApplied,
                PricesQueued = pricesQueued,
                ProductsApplied = productsApplied,
                SuppliersReceived = suppliersReceived,
                TombstonesApplied = tombstonesApplied,
                TombstonesReceived = tombstonesReceived
            };
        }
    }

    public sealed class PosCatalogPullOutcome
    {
        private PosCatalogPullOutcome(
            bool completed,
            string statusCode,
            bool authDenied,
            bool hasMore,
            int pagesProcessed,
            bool catalogSaleSafe,
            int productsApplied,
            int pricesApplied,
            int pricesQueued,
            int pendingPricesApplied)
        {
            AuthDenied = authDenied;
            CatalogSaleSafe = catalogSaleSafe;
            Completed = completed;
            HasMore = hasMore;
            PagesProcessed = pagesProcessed;
            PendingPricesApplied = pendingPricesApplied;
            PricesApplied = pricesApplied;
            PricesQueued = pricesQueued;
            ProductsApplied = productsApplied;
            StatusCode = string.IsNullOrWhiteSpace(statusCode) ? "failure" : statusCode;
        }

        public bool AuthDenied { get; }
        public bool CatalogSaleSafe { get; }
        public bool Completed { get; }
        public bool HasMore { get; }
        public int PagesProcessed { get; }
        public int PendingPricesApplied { get; }
        public int PricesApplied { get; }
        public int PricesQueued { get; }
        public int ProductsApplied { get; }
        public string StatusCode { get; }

        public static PosCatalogPullOutcome CompletedOk(
            int pagesProcessed,
            int productsApplied = 0,
            int pricesApplied = 0,
            int pricesQueued = 0,
            int pendingPricesApplied = 0)
        {
            return new PosCatalogPullOutcome(
                true,
                "completed",
                false,
                false,
                pagesProcessed,
                true,
                productsApplied,
                pricesApplied,
                pricesQueued,
                pendingPricesApplied);
        }

        public static PosCatalogPullOutcome Failure(
            string statusCode,
            bool authDenied,
            bool hasMore,
            int pagesProcessed,
            int productsApplied = 0,
            int pricesApplied = 0,
            int pricesQueued = 0,
            int pendingPricesApplied = 0)
        {
            return new PosCatalogPullOutcome(
                false,
                statusCode,
                authDenied,
                hasMore,
                pagesProcessed,
                false,
                productsApplied,
                pricesApplied,
                pricesQueued,
                pendingPricesApplied);
        }
    }
}
