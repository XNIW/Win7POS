using System;

namespace Win7POS.Core.Online
{
    public sealed class CatalogPaginationLaneCounts
    {
        public CatalogPaginationLaneCounts(
            long products,
            long categories,
            long suppliers,
            long prices,
            long productTombstones = 0,
            long categoryTombstones = 0,
            long supplierTombstones = 0)
        {
            Products = NonNegative(products, nameof(products));
            Categories = NonNegative(categories, nameof(categories));
            Suppliers = NonNegative(suppliers, nameof(suppliers));
            Prices = NonNegative(prices, nameof(prices));
            ProductTombstones = NonNegative(productTombstones, nameof(productTombstones));
            CategoryTombstones = NonNegative(categoryTombstones, nameof(categoryTombstones));
            SupplierTombstones = NonNegative(supplierTombstones, nameof(supplierTombstones));
        }

        public long Categories { get; }
        public long CategoryTombstones { get; }
        public long Prices { get; }
        public long Products { get; }
        public long ProductTombstones { get; }
        public long Suppliers { get; }
        public long SupplierTombstones { get; }

        public bool HasAnyTombstones => ProductTombstones > 0 ||
            CategoryTombstones > 0 ||
            SupplierTombstones > 0;

        public CatalogPaginationLaneCounts Add(CatalogPaginationLaneCounts other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            return new CatalogPaginationLaneCounts(
                CheckedAdd(Products, other.Products),
                CheckedAdd(Categories, other.Categories),
                CheckedAdd(Suppliers, other.Suppliers),
                CheckedAdd(Prices, other.Prices),
                CheckedAdd(ProductTombstones, other.ProductTombstones),
                CheckedAdd(CategoryTombstones, other.CategoryTombstones),
                CheckedAdd(SupplierTombstones, other.SupplierTombstones));
        }

        public bool HasSaturatedLane(int requestLimit)
        {
            if (requestLimit <= 0) throw new ArgumentOutOfRangeException(nameof(requestLimit));
            return CheckedAdd(Products, ProductTombstones) >= requestLimit ||
                CheckedAdd(Categories, CategoryTombstones) >= requestLimit ||
                CheckedAdd(Suppliers, SupplierTombstones) >= requestLimit ||
                Prices >= requestLimit;
        }

        public static CatalogPaginationLaneCounts FromPayload(PosCatalogPayload payload)
        {
            if (payload == null)
            {
                return new CatalogPaginationLaneCounts(0, 0, 0, 0);
            }

            return new CatalogPaginationLaneCounts(
                Length(payload.Products),
                Length(payload.Categories),
                Length(payload.Suppliers),
                Length(payload.Prices),
                Length(payload.Tombstones?.Products),
                Length(payload.Tombstones?.Categories),
                Length(payload.Tombstones?.Suppliers));
        }

        private static long CheckedAdd(long left, long right)
        {
            if (left > long.MaxValue - right)
            {
                throw new OverflowException("Catalog lane count overflowed Int64.");
            }

            return left + right;
        }

        private static int Length<T>(T[] values)
        {
            return values == null ? 0 : values.Length;
        }

        private static long NonNegative(long value, string parameterName)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(parameterName);
            return value;
        }
    }

    public sealed class CatalogPaginationSafetyDecision
    {
        internal CatalogPaginationSafetyDecision(bool allowed, string code)
        {
            Allowed = allowed;
            Code = code ?? string.Empty;
        }

        public bool Allowed { get; }
        public string Code { get; }
    }

    public static class CatalogPaginationSafetyPolicy
    {
        public const string AmbiguousEndCode = "server_catalog_pagination_ambiguous";

        public static CatalogPaginationSafetyDecision EvaluateTerminalPage(
            PosCatalogPullResponse response,
            int requestLimit,
            bool fullSnapshotExpected,
            CatalogPaginationLaneCounts receivedBeforePage,
            CatalogPaginationLaneCounts cumulativeEvidence = null,
            bool pageAfterContinuation = false)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (requestLimit <= 0) throw new ArgumentOutOfRangeException(nameof(requestLimit));
            if (receivedBeforePage == null) throw new ArgumentNullException(nameof(receivedBeforePage));

            var page = CatalogPaginationLaneCounts.FromPayload(response.Catalog);
            var responseSaysFull = string.Equals(
                (response.SyncMode ?? string.Empty).Trim(),
                "full_refresh",
                StringComparison.OrdinalIgnoreCase);
            if (!fullSnapshotExpected && !responseSaysFull)
            {
                return new CatalogPaginationSafetyDecision(true, string.Empty);
            }

            var cumulative = cumulativeEvidence ?? receivedBeforePage.Add(page);
            var completeSummary = HasCompleteValidSummary(response.CatalogSummary);
            if (completeSummary &&
                (cumulative.Products > response.CatalogSummary.Products.Value ||
                 cumulative.Categories > response.CatalogSummary.Categories.Value ||
                 cumulative.Suppliers > response.CatalogSummary.Suppliers.Value ||
                 cumulative.Prices > response.CatalogSummary.Prices.Value))
            {
                return new CatalogPaginationSafetyDecision(false, AmbiguousEndCode);
            }

            if (response.HasMore)
            {
                // Summary totals cover active rows only. Even when every active total is
                // already satisfied, later pages may still contain ordered tombstones.
                return new CatalogPaginationSafetyDecision(true, string.Empty);
            }

            if (completeSummary &&
                pageAfterContinuation &&
                !cumulative.HasAnyTombstones &&
                ActiveSummarySatisfied(response.CatalogSummary, receivedBeforePage))
            {
                // A continuation after the active summary was already complete must be
                // explained by at least one deletion event before promotion.
                return new CatalogPaginationSafetyDecision(false, AmbiguousEndCode);
            }

            if (page.HasSaturatedLane(requestLimit) &&
                CatalogHeartbeatPolicy.NormalizeRevision(response.CatalogVersion).Length == 0)
            {
                return new CatalogPaginationSafetyDecision(false, AmbiguousEndCode);
            }

            if (completeSummary)
            {
                // The authoritative summary counts active snapshot rows, not deletion
                // events. Tombstones are therefore safe on an unsaturated terminal page;
                // a saturated terminal lane remains ambiguous because the server exposes
                // no authoritative tombstone total.
                if ((page.HasSaturatedLane(requestLimit) && cumulative.HasAnyTombstones) ||
                    response.CatalogSummary.Products.Value != cumulative.Products ||
                    response.CatalogSummary.Categories.Value != cumulative.Categories ||
                    response.CatalogSummary.Suppliers.Value != cumulative.Suppliers ||
                    response.CatalogSummary.Prices.Value != cumulative.Prices)
                {
                    return new CatalogPaginationSafetyDecision(false, AmbiguousEndCode);
                }

                return new CatalogPaginationSafetyDecision(true, string.Empty);
            }

            // A terminal authoritative full snapshot is promotable only with a
            // complete summary.  Without it there is no evidence that an
            // unsaturated final page is actually the end of the snapshot.
            return new CatalogPaginationSafetyDecision(false, AmbiguousEndCode);
        }

        public static bool HasCompleteValidSummary(PosCatalogSummaryResponse summary)
        {
            return summary != null &&
                summary.Products.HasValue && summary.Products.Value >= 0 &&
                summary.ActiveProducts.HasValue && summary.ActiveProducts.Value >= 0 &&
                summary.ActiveProducts.Value <= summary.Products.Value &&
                summary.Categories.HasValue && summary.Categories.Value >= 0 &&
                summary.Suppliers.HasValue && summary.Suppliers.Value >= 0 &&
                summary.Prices.HasValue && summary.Prices.Value >= 0;
        }

        internal static bool ActiveSummarySatisfied(
            PosCatalogSummaryResponse summary,
            CatalogPaginationLaneCounts counts)
        {
            return counts != null &&
                HasCompleteValidSummary(summary) &&
                summary.Products.Value == counts.Products &&
                summary.Categories.Value == counts.Categories &&
                summary.Suppliers.Value == counts.Suppliers &&
                summary.Prices.Value == counts.Prices;
        }
    }
}
