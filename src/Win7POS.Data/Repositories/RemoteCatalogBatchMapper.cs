using System;
using System.Collections.Generic;
using System.Linq;
using Win7POS.Core.Online;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Maps an accepted catalog transport page to the canonical local batch shape.
    /// Keeping this logic outside the WPF layer lets offline diagnostics exercise
    /// precisely the same conversion as the production catalog pull.
    /// </summary>
    public static class RemoteCatalogBatchMapper
    {
        public static RemoteCatalogBatch BuildRemoteCatalogBatch(
            PosCatalogPullResponse response,
            bool authoritativeFullRefresh,
            CatalogAuthoritativeStagePage stagePage)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            var catalog = response.Catalog ?? new PosCatalogPayload();
            var products = catalog.Products ?? Array.Empty<PosCatalogProductResponse>();
            var priceRows = catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>();
            var categories = BuildNameMap(catalog.Categories, row => row.CategoryId, row => row.Name);
            var suppliers = BuildNameMap(catalog.Suppliers, row => row.SupplierId, row => row.Name);
            return new RemoteCatalogBatch
            {
                AuthoritativeFullRefresh = authoritativeFullRefresh,
                AuthoritativeStagePage = stagePage,
                ReuseValidatedAuthoritativeStagePage =
                    authoritativeFullRefresh && stagePage != null,
                Categories = (catalog.Categories ?? Array.Empty<PosCatalogCategoryResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogCategoryWrite
                    {
                        RemoteCategoryId = Normalize(row.CategoryId),
                        Name = Normalize(row.Name),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray(),
                Suppliers = (catalog.Suppliers ?? Array.Empty<PosCatalogSupplierResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogSupplierWrite
                    {
                        RemoteSupplierId = Normalize(row.SupplierId),
                        Name = Normalize(row.Name),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray(),
                Products = products
                    .Select(row => row == null ? null : new RemoteCatalogProductWrite
                    {
                        ArticleCode = Normalize(row.ItemNumber),
                        Barcode = Normalize(row.Barcode),
                        CategoryName = NameFor(categories, row.CategoryId),
                        Name = FirstNonEmpty(row.ProductName, row.SecondProductName, row.Barcode),
                        PurchasePrice = ToInt(row.PurchasePrice),
                        RemoteCategoryId = Normalize(row.CategoryId),
                        RemoteProductId = Normalize(row.ProductId),
                        RemoteSupplierId = Normalize(row.SupplierId),
                        SecondName = Normalize(row.SecondProductName),
                        StockQuantity = ToInt(row.StockQuantity),
                        SupplierName = NameFor(suppliers, row.SupplierId),
                        UnitPrice = ToLong(row.RetailPrice)
                    })
                    .ToArray(),
                Prices = priceRows
                    .Select(row => row == null ? null : new RemoteCatalogPriceWrite
                    {
                        EffectiveAt = Normalize(row.EffectiveAt),
                        Price = row.Price < 0 || double.IsNaN(row.Price) || double.IsInfinity(row.Price)
                            ? -1
                            : ToInt(row.Price),
                        RemotePriceId = Normalize(row.PriceId),
                        RemoteProductId = Normalize(row.ProductId),
                        Source = Normalize(row.Source),
                        Type = Normalize(row.Type)
                    })
                    .ToArray(),
                ProductTombstones = (catalog.Tombstones?.Products ?? Array.Empty<PosCatalogProductTombstoneResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogProductTombstoneWrite
                    {
                        RemoteProductId = Normalize(row.ProductId),
                        RemoteDeletedAt = Normalize(row.DeletedAt),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray(),
                CategoryTombstones = (catalog.Tombstones?.Categories ?? Array.Empty<PosCatalogCategoryTombstoneResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogCategoryTombstoneWrite
                    {
                        RemoteCategoryId = Normalize(row.CategoryId),
                        RemoteDeletedAt = Normalize(row.DeletedAt),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray(),
                SupplierTombstones = (catalog.Tombstones?.Suppliers ?? Array.Empty<PosCatalogSupplierTombstoneResponse>())
                    .Select(row => row == null ? null : new RemoteCatalogSupplierTombstoneWrite
                    {
                        RemoteSupplierId = Normalize(row.SupplierId),
                        RemoteDeletedAt = Normalize(row.DeletedAt),
                        RemoteUpdatedAt = Normalize(row.UpdatedAt)
                    })
                    .ToArray()
            };
        }

        public static string ProductStageFingerprint(RemoteCatalogProductWrite row)
        {
            if (row == null ||
                string.IsNullOrWhiteSpace(row.RemoteProductId) ||
                string.IsNullOrWhiteSpace(row.Barcode) ||
                string.IsNullOrWhiteSpace(row.Name) ||
                row.UnitPrice <= 0 ||
                ProductIdentityPolicy.IsReservedBarcode(row.Barcode.Trim()))
            {
                return "invalid";
            }

            return "barcode:" + NormalizeBarcode(row.Barcode).ToUpperInvariant();
        }

        public static long ToLong(double? value)
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

        private static IReadOnlyDictionary<string, string> BuildNameMap<TRow>(
            TRow[] rows,
            Func<TRow, string> id,
            Func<TRow, string> name)
        {
            return (rows ?? Array.Empty<TRow>())
                .Where(row => row != null)
                .Select(row => new { Id = Normalize(id(row)), Name = Normalize(name(row)) })
                .Where(row => row.Id.Length > 0)
                .GroupBy(row => row.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
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
            return normalizedId.Length > 0 && rows.TryGetValue(normalizedId, out var name)
                ? name
                : string.Empty;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string NormalizeBarcode(string value)
        {
            return Normalize(value).Replace(" ", string.Empty);
        }

        private static int ToInt(double? value)
        {
            var rounded = ToLong(value);
            return rounded > int.MaxValue ? int.MaxValue : (int)rounded;
        }
    }
}
