using System;
using System.Globalization;

namespace Win7POS.Core.Pos
{
    // Issued only by the validated manual-price domain operation. A null product
    // id, reserved barcode or UI boolean alone is never local sale provenance.
    public sealed class ManualSaleLineEvidence
    {
        private readonly long _price;
        internal ManualSaleLineEvidence(long price)
        {
            if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
            _price = price;
        }

        public bool Matches(long? productId, string barcode, long unitPrice) =>
            !productId.HasValue && unitPrice == _price &&
            string.Equals(barcode, DiscountKeys.ManualPrefix + _price.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
