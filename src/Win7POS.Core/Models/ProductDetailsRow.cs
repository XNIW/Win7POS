namespace Win7POS.Core.Models
{
    /// <summary>
    /// DTO for product search with joined product_meta (article_code, purchase_price, supplier, category, stock, etc.)
    /// </summary>
    public sealed class ProductDetailsRow
    {
        public long Id { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int UnitPrice { get; set; }

        // from product_meta (nullable when no meta)
        public string ArticleCode { get; set; } = string.Empty;
        public string Name2 { get; set; } = string.Empty;
        public int PurchasePrice { get; set; }
        public int StockQty { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
    }
}
