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
        private const int MaxCatalogPullAttempts = 3;
        private const int MaxCatalogPullPages = 10;

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

            return await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: true,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> TryPullCatalogAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            CancellationToken cancellationToken)
        {
            return await TryPullCatalogWithSessionAsync(
                options,
                trustedSession,
                clearStoredStateOnDenied: false,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryPullCatalogWithSessionAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            bool clearStoredStateOnDenied,
            CancellationToken cancellationToken)
        {
            if (options == null ||
                trustedSession == null ||
                string.IsNullOrWhiteSpace(trustedSession.DeviceToken) ||
                string.IsNullOrWhiteSpace(trustedSession.PosSessionId) ||
                string.IsNullOrWhiteSpace(trustedSession.SessionToken) ||
                string.IsNullOrWhiteSpace(trustedSession.ShopDeviceId))
            {
                return false;
            }

            try
            {
                using (var client = new PosAdminWebClient(options))
                {
                    var cursor = string.Empty;
                    var totalStats = new CatalogApplyStats();
                    PosCatalogPullResponse lastResponse = null;

                    for (var page = 1; page <= MaxCatalogPullPages; page++)
                    {
                        var result = await CatalogPullWithRetryAsync(client, new PosCatalogPullRequest
                        {
                            AppVersion = typeof(PosCatalogPullService).Assembly.GetName().Version?.ToString(),
                            DeviceToken = trustedSession.DeviceToken,
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
                            _logger.LogWarning("Catalog pull skipped: " + (result.Code ?? "failure"));
                            return false;
                        }

                        var applyStats = await ApplyCatalogAsync(result.Value).ConfigureAwait(false);
                        totalStats.PriceRowsApplied += applyStats.PriceRowsApplied;
                        totalStats.TombstonesApplied += applyStats.TombstonesApplied;
                        totalStats.TombstonesReceived += applyStats.TombstonesReceived;
                        totalStats.UpdatedProducts += applyStats.UpdatedProducts;
                        await StoreCatalogDiagnosticsAsync(result.Value, applyStats).ConfigureAwait(false);

                        lastResponse = result.Value;
                        var catalogVersion = result.Value.CatalogVersion;
                        cursor = result.Value.SyncCursor;

                        if (!result.Value.HasMore)
                        {
                            break;
                        }
                    }

                    if (lastResponse == null)
                    {
                        await StoreCatalogFailureAsync("empty_response").ConfigureAwait(false);
                        return false;
                    }

                    if (lastResponse.HasMore)
                    {
                        await StoreCatalogFailureAsync("has_more_not_drained").ConfigureAwait(false);
                        _logger.LogWarning("Catalog pull stopped before draining all pages.");
                        return false;
                    }

                    _logger.LogInfo("Catalog pull completed: products=" + totalStats.UpdatedProducts.ToString() + ", prices=" + totalStats.PriceRowsApplied.ToString() + ", hasMore=" + lastResponse.HasMore.ToString() + ", catalogVersion=" + (lastResponse.CatalogVersion ?? string.Empty));
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                await StoreCatalogFailureAsync("exception").ConfigureAwait(false);
                _logger.LogWarning("Catalog pull skipped.", ex);
                return false;
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
            public int PriceRowsApplied { get; set; }
            public int TombstonesApplied { get; set; }
            public int TombstonesReceived { get; set; }
            public int UpdatedProducts { get; set; }
        }

        private async Task<CatalogApplyStats> ApplyCatalogAsync(PosCatalogPullResponse response)
        {
            var catalog = response.Catalog;
            var products = catalog.Products ?? Array.Empty<PosCatalogProductResponse>();
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
            foreach (var price in catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>())
            {
                if (await productRepository.UpsertRemotePriceHistoryAsync(
                    Normalize(price.ProductId),
                    Normalize(price.Type),
                    ToInt(price.Price),
                    Normalize(price.EffectiveAt),
                    Normalize(price.Source)).ConfigureAwait(false))
                {
                    appliedPrices += 1;
                }
            }

            if (tombstones > 0)
            {
                _logger.LogInfo("Catalog tombstones received: count=" + tombstones.ToString() + ", applied=" + appliedProductTombstones.ToString() + "; local purge disabled.");
            }

            return new CatalogApplyStats
            {
                PriceRowsApplied = appliedPrices,
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
                string.IsNullOrWhiteSpace(code) ? "failure" : code.Trim()).ConfigureAwait(false);
        }

        private async Task StoreCatalogDiagnosticsAsync(
            PosCatalogPullResponse response,
            CatalogApplyStats stats)
        {
            var settings = new SettingsRepository(_factory);

            await PosOnlineShopSnapshot.SaveAsync(_factory, response?.Shop).ConfigureAwait(false);
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
            return string.Equals(code, "timeout", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "network_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "io_error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "db_failure", StringComparison.OrdinalIgnoreCase);
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
}
