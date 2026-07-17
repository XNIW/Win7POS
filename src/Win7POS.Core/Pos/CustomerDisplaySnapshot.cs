using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Win7POS.Core.Pos
{
    public enum CustomerDisplayState
    {
        Idle,
        CartActive,
        Payment,
        Completed,
        Locked,
        Unavailable
    }

    public enum CustomerDisplayLineKind
    {
        Item,
        Discount,
        Adjustment
    }

    public sealed class CustomerDisplayLine
    {
        public CustomerDisplayLine(
            string stableKey,
            string name,
            string barcode,
            int quantity,
            long unitPrice,
            long lineTotal,
            CustomerDisplayLineKind lineKind)
        {
            StableKey = stableKey ?? string.Empty;
            Name = name ?? string.Empty;
            Barcode = barcode ?? string.Empty;
            Quantity = quantity;
            UnitPrice = unitPrice;
            LineTotal = lineTotal;
            LineKind = lineKind;
        }

        public string StableKey { get; }
        public string Name { get; }
        public string Barcode { get; }
        public int Quantity { get; }
        public long UnitPrice { get; }
        public long LineTotal { get; }
        public CustomerDisplayLineKind LineKind { get; }
    }

    public sealed class CustomerDisplaySnapshot
    {
        public CustomerDisplaySnapshot(
            CustomerDisplayState state,
            string shopName,
            IEnumerable<CustomerDisplayLine> lines,
            int itemCount,
            long subtotal,
            long discount,
            long total,
            long paid,
            long change,
            string lastChangedLineKey,
            string messageCode,
            DateTimeOffset timestamp)
        {
            State = state;
            ShopName = shopName ?? string.Empty;
            Lines = new ReadOnlyCollection<CustomerDisplayLine>((lines ?? Enumerable.Empty<CustomerDisplayLine>()).ToList());
            ItemCount = itemCount;
            Subtotal = subtotal;
            Discount = discount;
            Total = total;
            Paid = paid;
            Change = change;
            LastChangedLineKey = lastChangedLineKey ?? string.Empty;
            MessageCode = messageCode ?? string.Empty;
            Timestamp = timestamp;
        }

        public CustomerDisplayState State { get; }
        public string ShopName { get; }
        public IReadOnlyList<CustomerDisplayLine> Lines { get; }
        public int ItemCount { get; }
        public long Subtotal { get; }
        public long Discount { get; }
        public long Total { get; }
        public long Paid { get; }
        public long Change { get; }
        public string LastChangedLineKey { get; }
        public string MessageCode { get; }
        public DateTimeOffset Timestamp { get; }
    }
}
