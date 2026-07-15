using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Win7POS.Core.Models;

namespace Win7POS.Core.Pos
{
    public sealed class ReversalEconomicsSnapshot
    {
        public long OriginalGrossClp { get; set; }
        public long OriginalDiscountClp { get; set; }
        public long OriginalTaxClp { get; set; }
        public long OriginalNetClp { get; set; }
        public long PriorGrossClp { get; set; }
        public long ActualPriorDiscountClp { get; set; }
        public long ActualPriorTaxClp { get; set; }
    }

    public sealed class ReversalEconomicsResult
    {
        public long GrossClp { get; set; }
        public long DiscountClp { get; set; }
        public long TaxClp { get; set; }
        public long NetClp { get; set; }
    }

    public static class ReversalEconomicsPolicy
    {
        public const string InvalidOriginalCode = "reversal_original_economics_invalid";
        public const string InvalidHistoryCode = "historical_reversal_economics_invalid";
        public const string PriorSyncUnresolvedCode = "reversal_prior_sync_unresolved";
        public const string MismatchCode = "reversal_economics_mismatch";

        public static void ValidateSnapshot(ReversalEconomicsSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.OriginalGrossClp <= 0 ||
                snapshot.OriginalDiscountClp < 0 ||
                snapshot.OriginalTaxClp < 0 ||
                snapshot.OriginalDiscountClp > snapshot.OriginalGrossClp ||
                snapshot.OriginalNetClp != CheckedNet(
                    snapshot.OriginalGrossClp,
                    snapshot.OriginalDiscountClp,
                    snapshot.OriginalTaxClp))
            {
                throw new InvalidOperationException(InvalidOriginalCode);
            }

            if (snapshot.PriorGrossClp < 0 ||
                snapshot.PriorGrossClp > snapshot.OriginalGrossClp ||
                snapshot.ActualPriorDiscountClp < 0 ||
                snapshot.ActualPriorTaxClp < 0)
            {
                throw new InvalidOperationException(InvalidHistoryCode);
            }

            var expectedPriorDiscount = RoundPostgresNumericRatio(
                snapshot.OriginalDiscountClp,
                snapshot.PriorGrossClp,
                snapshot.OriginalGrossClp);
            var expectedPriorTax = RoundPostgresNumericRatio(
                snapshot.OriginalTaxClp,
                snapshot.PriorGrossClp,
                snapshot.OriginalGrossClp);
            if (snapshot.ActualPriorDiscountClp != expectedPriorDiscount ||
                snapshot.ActualPriorTaxClp != expectedPriorTax)
            {
                throw new InvalidOperationException(InvalidHistoryCode);
            }
        }

        public static ReversalEconomicsResult Calculate(
            ReversalEconomicsSnapshot snapshot,
            long currentGrossClp)
        {
            ValidateSnapshot(snapshot);
            if (currentGrossClp <= 0)
            {
                throw new InvalidOperationException(MismatchCode);
            }

            long cumulativeGross;
            try
            {
                cumulativeGross = checked(snapshot.PriorGrossClp + currentGrossClp);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(MismatchCode);
            }

            if (cumulativeGross > snapshot.OriginalGrossClp)
            {
                throw new InvalidOperationException(MismatchCode);
            }

            var targetDiscount = RoundPostgresNumericRatio(
                snapshot.OriginalDiscountClp,
                cumulativeGross,
                snapshot.OriginalGrossClp);
            var targetTax = RoundPostgresNumericRatio(
                snapshot.OriginalTaxClp,
                cumulativeGross,
                snapshot.OriginalGrossClp);
            var currentDiscount = targetDiscount - snapshot.ActualPriorDiscountClp;
            var currentTax = targetTax - snapshot.ActualPriorTaxClp;
            if (currentDiscount < 0 || currentTax < 0 || currentDiscount > currentGrossClp)
            {
                throw new InvalidOperationException(MismatchCode);
            }

            return new ReversalEconomicsResult
            {
                GrossClp = currentGrossClp,
                DiscountClp = currentDiscount,
                TaxClp = currentTax,
                NetClp = -CheckedNet(currentGrossClp, currentDiscount, currentTax)
            };
        }

        public static long CalculateItemGross(IEnumerable<SaleLine> lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            try
            {
                return lines.Aggregate(0L, (total, line) =>
                {
                    if (line == null ||
                        line.Quantity <= 0 ||
                        line.UnitPrice < 0 ||
                        DiscountKeys.IsEconomicAdjustment(line.Barcode))
                    {
                        throw new InvalidOperationException(MismatchCode);
                    }

                    return checked(total + checked((long)line.Quantity * line.UnitPrice));
                });
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(MismatchCode);
            }
        }

        public static long RoundPostgresNumericRatio(long value, long numerator, long denominator)
        {
            if (value < 0 || numerator < 0 || denominator <= 0)
            {
                throw new InvalidOperationException(MismatchCode);
            }

            var product = (BigInteger)value * numerator;
            var quotient = BigInteger.DivRem(product, denominator, out var remainder);
            if (remainder * 2 >= denominator)
            {
                quotient += BigInteger.One;
            }

            if (quotient > long.MaxValue)
            {
                throw new InvalidOperationException(MismatchCode);
            }

            return (long)quotient;
        }

        private static long CheckedNet(long grossClp, long discountClp, long taxClp)
        {
            try
            {
                return checked(checked(grossClp - discountClp) + taxClp);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(MismatchCode);
            }
        }
    }
}
