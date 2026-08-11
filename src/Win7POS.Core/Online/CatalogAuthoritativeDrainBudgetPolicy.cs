using System;

namespace Win7POS.Core.Online
{
    public sealed class CatalogLanePageCapacity
    {
        public CatalogLanePageCapacity(
            long categories,
            long suppliers,
            long products,
            long prices)
        {
            Categories = Positive(categories, nameof(categories));
            Suppliers = Positive(suppliers, nameof(suppliers));
            Products = Positive(products, nameof(products));
            Prices = Positive(prices, nameof(prices));
        }

        public long Categories { get; }
        public long Prices { get; }
        public long Products { get; }
        public long Suppliers { get; }

        private static long Positive(long value, string parameterName)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
            return value;
        }
    }

    public sealed class CatalogAuthoritativeDrainDecision
    {
        internal CatalogAuthoritativeDrainDecision(
            bool allowed,
            long activePageBudget,
            string code)
        {
            Allowed = allowed;
            ActivePageBudget = activePageBudget;
            Code = code ?? string.Empty;
        }

        /// <summary>
        /// Expected pages for the active manifest lanes. This is sizing and timeout
        /// input only; it is never a termination ceiling.
        /// </summary>
        public long ActivePageBudget { get; }

        public bool Allowed { get; }
        public string Code { get; }
    }

    public sealed class CatalogAuthoritativeProgressBudget
    {
        internal CatalogAuthoritativeProgressBudget(
            bool allowed,
            long activePageBudget,
            long noProgressTimeoutMilliseconds,
            long overallTimeoutMilliseconds,
            string code)
        {
            ActivePageBudget = activePageBudget;
            Allowed = allowed;
            Code = code ?? string.Empty;
            NoProgressTimeoutMilliseconds = noProgressTimeoutMilliseconds;
            OverallTimeoutMilliseconds = overallTimeoutMilliseconds;
        }

        public long ActivePageBudget { get; }
        public bool Allowed { get; }
        public string Code { get; }
        public long NoProgressTimeoutMilliseconds { get; }
        public long OverallTimeoutMilliseconds { get; }
    }

    /// <summary>
    /// Sizes a sequential catalog-v2 full refresh from the manifest. The result is
    /// not a page cap: a valid run continues until hasMore=false, subject only to
    /// cursor/protocol, cancellation, progress-time and local-resource protections.
    /// </summary>
    public static class CatalogAuthoritativeDrainBudgetPolicy
    {
        public const string CursorRepeatedCode =
            "catalog_authoritative_cursor_repeated";
        public const string InsufficientDiskCode =
            "catalog_authoritative_insufficient_disk";
        public const string NumericOverflowCode =
            "catalog_authoritative_numeric_overflow";
        public const string ProgressTimeoutCode =
            "catalog_authoritative_progress_timeout";
        public const string ResourceCeilingExceededCode =
            "catalog_authoritative_resource_ceiling_exceeded";
        public const string SqliteFailureCode =
            "catalog_authoritative_sqlite_failure";
        public const string StageByteBudgetExceededCode =
            "catalog_authoritative_stage_byte_budget_exceeded";
        public const string SummaryInvalidCode =
            "catalog_authoritative_summary_invalid";

        public const long NoProgressTimeoutMilliseconds = 2L * 60L * 1000L;
        public const long OverallSetupAllowanceMilliseconds = 5L * 60L * 1000L;
        public const long PerActivePageSlaMilliseconds = 60L * 1000L;

        public static CatalogLanePageCapacity ProtocolLaneCapacity =>
            new CatalogLanePageCapacity(
                categories: 240,
                suppliers: 240,
                products: 60,
                prices: 120);

        public static CatalogAuthoritativeDrainDecision Calculate(
            PosCatalogSummaryResponse summary)
        {
            return Calculate(summary, ProtocolLaneCapacity);
        }

        public static CatalogAuthoritativeProgressBudget CalculateProgressBudget(
            CatalogAuthoritativeDrainDecision drain)
        {
            if (drain == null) throw new ArgumentNullException(nameof(drain));
            if (!drain.Allowed)
            {
                return new CatalogAuthoritativeProgressBudget(
                    false,
                    drain.ActivePageBudget,
                    0,
                    0,
                    drain.Code);
            }

            try
            {
                var overall = checked(
                    OverallSetupAllowanceMilliseconds +
                    checked(drain.ActivePageBudget * PerActivePageSlaMilliseconds));
                return new CatalogAuthoritativeProgressBudget(
                    true,
                    drain.ActivePageBudget,
                    NoProgressTimeoutMilliseconds,
                    overall,
                    string.Empty);
            }
            catch (OverflowException)
            {
                return new CatalogAuthoritativeProgressBudget(
                    false,
                    drain.ActivePageBudget,
                    0,
                    0,
                    NumericOverflowCode);
            }
        }

        internal static CatalogAuthoritativeDrainDecision Calculate(
            PosCatalogSummaryResponse summary,
            CatalogLanePageCapacity capacity)
        {
            if (capacity == null) throw new ArgumentNullException(nameof(capacity));
            if (!CatalogPaginationSafetyPolicy.HasCompleteValidSummary(summary))
            {
                return Failure(SummaryInvalidCode);
            }

            try
            {
                var activeBudget = checked(
                    checked(
                        Pages(summary.Categories.Value, capacity.Categories) +
                        Pages(summary.Suppliers.Value, capacity.Suppliers)) +
                    checked(
                        Pages(summary.Products.Value, capacity.Products) +
                        Pages(summary.Prices.Value, capacity.Prices)));
                activeBudget = Math.Max(1L, activeBudget);
                return new CatalogAuthoritativeDrainDecision(
                    true,
                    activeBudget,
                    string.Empty);
            }
            catch (OverflowException)
            {
                return Failure(NumericOverflowCode);
            }
        }

        private static CatalogAuthoritativeDrainDecision Failure(string code)
        {
            return new CatalogAuthoritativeDrainDecision(false, 0, code);
        }

        private static long Pages(long rows, long capacity)
        {
            if (rows < 0) throw new ArgumentOutOfRangeException(nameof(rows));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            return rows == 0 ? 0 : checked(1L + ((rows - 1L) / capacity));
        }
    }
}
