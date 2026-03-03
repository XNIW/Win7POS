using System;
using System.Collections.Generic;

namespace Win7POS.Core.ImportDb
{
    public sealed class ProductDbWorkbook
    {
        public IReadOnlyList<ProductRow> Products { get; set; } = Array.Empty<ProductRow>();
        public IReadOnlyList<SupplierRow> Suppliers { get; set; } = Array.Empty<SupplierRow>();
        public IReadOnlyList<CategoryRow> Categories { get; set; } = Array.Empty<CategoryRow>();
        public IReadOnlyList<PriceHistoryRow> PriceHistory { get; set; } = Array.Empty<PriceHistoryRow>();
    }

    public sealed class ProductRow
    {
        public string Barcode { get; set; } = string.Empty;
        public string ArticleCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Name2 { get; set; } = string.Empty;
        public int PurchasePrice { get; set; }
        public int RetailPrice { get; set; }
        public int PurchaseOld { get; set; }
        public int RetailOld { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public int? SupplierId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public int? CategoryId { get; set; }
        public int StockQty { get; set; }
    }

    public sealed class SupplierRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CategoryRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class PriceHistoryRow
    {
        public string ProductBarcode { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int? OldPrice { get; set; }
        public int NewPrice { get; set; }
        public string Source { get; set; } = string.Empty;
    }
}
