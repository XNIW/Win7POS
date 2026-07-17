using System;
using System.Collections.Generic;
using System.Linq;

namespace Win7POS.Core.Pos
{
    public sealed class CustomerDisplayProjectionLine
    {
        public string StableKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal { get; set; }
        public CustomerDisplayLineKind LineKind { get; set; }
    }

    public static class CustomerDisplayProjection
    {
        public static CustomerDisplaySnapshot Cart(
            IEnumerable<CustomerDisplayProjectionLine> sourceLines,
            long authoritativeSubtotal,
            long authoritativeTotal,
            string shopName,
            string lastChangedLineKey,
            bool showBarcode,
            DateTimeOffset timestamp)
        {
            var lines = (sourceLines ?? Enumerable.Empty<CustomerDisplayProjectionLine>())
                .Where(x => x != null && x.Quantity > 0)
                .Select(x => new CustomerDisplayLine(
                    StableKey(x),
                    PublicName(x),
                    PublicBarcode(x, showBarcode),
                    x.Quantity,
                    x.UnitPrice,
                    x.LineTotal,
                    x.LineKind))
                .ToList();

            var discount = Math.Max(0, authoritativeSubtotal - authoritativeTotal);
            return new CustomerDisplaySnapshot(
                lines.Count == 0 ? CustomerDisplayState.Idle : CustomerDisplayState.CartActive,
                shopName,
                lines,
                lines.Where(x => x.LineKind == CustomerDisplayLineKind.Item).Sum(x => x.Quantity),
                authoritativeSubtotal,
                discount,
                authoritativeTotal,
                0,
                0,
                lastChangedLineKey,
                lines.Count == 0 ? "welcome" : "cart",
                timestamp);
        }

        public static CustomerDisplaySnapshot WithState(
            CustomerDisplaySnapshot cart,
            CustomerDisplayState state,
            string messageCode,
            long paid = 0,
            long change = 0,
            DateTimeOffset? timestamp = null)
        {
            cart = cart ?? Empty(timestamp ?? DateTimeOffset.UtcNow);
            return new CustomerDisplaySnapshot(
                state,
                cart.ShopName,
                state == CustomerDisplayState.Locked || state == CustomerDisplayState.Unavailable
                    ? Array.Empty<CustomerDisplayLine>()
                    : cart.Lines,
                state == CustomerDisplayState.Locked || state == CustomerDisplayState.Unavailable ? 0 : cart.ItemCount,
                cart.Subtotal,
                cart.Discount,
                cart.Total,
                paid,
                change,
                cart.LastChangedLineKey,
                messageCode,
                timestamp ?? DateTimeOffset.UtcNow);
        }

        public static CustomerDisplaySnapshot Completed(
            CustomerDisplaySnapshot previousCart,
            long total,
            long paid,
            long change,
            DateTimeOffset timestamp)
        {
            var previous = previousCart ?? Empty(timestamp);
            return new CustomerDisplaySnapshot(
                CustomerDisplayState.Completed,
                previous.ShopName,
                previous.Lines,
                previous.ItemCount,
                previous.Subtotal,
                previous.Discount,
                total,
                paid,
                change,
                previous.LastChangedLineKey,
                "thank_you",
                timestamp);
        }

        public static CustomerDisplaySnapshot Empty(DateTimeOffset timestamp)
        {
            return new CustomerDisplaySnapshot(
                CustomerDisplayState.Idle,
                string.Empty,
                Array.Empty<CustomerDisplayLine>(),
                0, 0, 0, 0, 0, 0,
                string.Empty,
                "welcome",
                timestamp);
        }

        private static string StableKey(CustomerDisplayProjectionLine line)
        {
            if (!string.IsNullOrWhiteSpace(line.StableKey)) return line.StableKey.Trim();
            if (!string.IsNullOrWhiteSpace(line.Barcode)) return "line:" + line.Barcode.Trim();
            return "line:" + (line.Name ?? string.Empty).Trim();
        }

        private static string PublicName(CustomerDisplayProjectionLine line)
        {
            if (line.LineKind == CustomerDisplayLineKind.Discount) return "Discount";
            if (line.LineKind == CustomerDisplayLineKind.Adjustment) return "Adjustment";
            return string.IsNullOrWhiteSpace(line.Name) ? "Item" : line.Name.Trim();
        }

        private static string PublicBarcode(CustomerDisplayProjectionLine line, bool showBarcode)
        {
            if (!showBarcode || line.LineKind != CustomerDisplayLineKind.Item) return string.Empty;
            var barcode = (line.Barcode ?? string.Empty).Trim();
            if (barcode.StartsWith("DISC:", StringComparison.OrdinalIgnoreCase) ||
                barcode.StartsWith("TAX:", StringComparison.OrdinalIgnoreCase) ||
                barcode.StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return barcode;
        }
    }
}
