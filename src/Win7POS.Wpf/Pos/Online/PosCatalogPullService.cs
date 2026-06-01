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
            if (options == null)
            {
                return false;
            }

            if (!_store.TryRead(out var trustedSession))
            {
                return false;
            }

            try
            {
                using (var client = new PosAdminWebClient(options))
                {
                    var result = await client.CatalogPullAsync(new PosCatalogPullRequest
                    {
                        AppVersion = typeof(PosCatalogPullService).Assembly.GetName().Version?.ToString(),
                        DeviceToken = trustedSession.DeviceToken,
                        PosSessionId = trustedSession.PosSessionId,
                        SessionToken = trustedSession.SessionToken,
                        ShopDeviceId = trustedSession.ShopDeviceId,
                    }, cancellationToken).ConfigureAwait(false);

                    if (!result.Success || result.Value == null || result.Value.Catalog == null)
                    {
                        if (result.Denied)
                        {
                            _store.Clear();
                        }

                        _logger.LogWarning("Catalog pull skipped: " + (result.Code ?? "failure"));
                        return false;
                    }

                    await ApplyCatalogAsync(result.Value).ConfigureAwait(false);
                    await StoreLastSyncAsync(result.Value.GeneratedAt).ConfigureAwait(false);

                    var products = result.Value.Catalog.Products ?? Array.Empty<PosCatalogProductResponse>();
                    _logger.LogInfo("Catalog pull completed: products=" + products.Length.ToString());
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Catalog pull skipped.", ex);
                return false;
            }
        }

        private async Task ApplyCatalogAsync(PosCatalogPullResponse response)
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
                    ToInt(remoteProduct.StockQuantity)).ConfigureAwait(false);
            }
        }

        private async Task StoreLastSyncAsync(string generatedAt)
        {
            var settings = new SettingsRepository(_factory);
            var value = string.IsNullOrWhiteSpace(generatedAt)
                ? DateTimeOffset.UtcNow.ToString("O")
                : generatedAt;

            await settings.SetStringAsync(LastCatalogSyncSettingKey, value).ConfigureAwait(false);
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
