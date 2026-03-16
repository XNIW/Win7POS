namespace Win7POS.Core.Import
{
    public sealed class ImportRow
    {
        public string Barcode { get; set; } = string.Empty;
        public string ArticleCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Name2 { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public int? Cost { get; set; }
        public int? Stock { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        /// <summary>Risolto da ImportWorkflowService prima di Apply. Se null, ProductUpserterAdapter usa il resolver.</summary>
        public int? SupplierId { get; set; }
        /// <summary>Risolto da ImportWorkflowService prima di Apply. Se null, ProductUpserterAdapter usa il resolver.</summary>
        public int? CategoryId { get; set; }
    }
}
