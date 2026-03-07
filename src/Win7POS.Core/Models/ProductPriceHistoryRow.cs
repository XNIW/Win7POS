namespace Win7POS.Core.Models
{
    /// <summary>Riga dello storico prezzi (price_history) per visualizzazione in UI.</summary>
    public sealed class ProductPriceHistoryRow
    {
        public string ProductBarcode { get; set; } = string.Empty;
        public string ChangedAt { get; set; } = string.Empty;
        public string PriceType { get; set; } = string.Empty; // "retail" | "purchase"
        public int? OldPrice { get; set; }
        public int NewPrice { get; set; }
        public string Source { get; set; } = string.Empty; // IMPORT, MANUAL_EDIT, BULK_IMPORT
        public string Note { get; set; } = string.Empty;
    }
}
