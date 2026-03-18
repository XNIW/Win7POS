using System.Collections.Generic;

namespace Win7POS.Core.Import
{
    public sealed class ImportApplyResult
    {
        public int AppliedInserted { get; set; }
        public int AppliedUpdated { get; set; }
        public int NoChange { get; set; }
        public int Skipped { get; set; }
        public int ErrorsCount { get; set; }
        public int SuppliersFromSheet { get; set; }
        public int SuppliersFromDb { get; set; }
        public int SuppliersCreated { get; set; }
        public int CategoriesFromSheet { get; set; }
        public int CategoriesFromDb { get; set; }
        public int CategoriesCreated { get; set; }
        public int PriceHistoryInserted { get; set; }
        public int PriceHistorySkipped { get; set; }
        public int SupplierNameOverwrittenCount { get; set; }
        public int CategoryNameOverwrittenCount { get; set; }
        public List<string> ChangedBarcodes { get; } = new List<string>();
        /// <summary>Dettagli errori (barcode + messaggio) per diagnostica.</summary>
        public List<string> Errors { get; } = new List<string>();
    }
}
