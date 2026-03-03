using System.Collections.Generic;
using System.Threading.Tasks;

namespace Win7POS.Core.Import
{
    public interface IProductSnapshotLookup
    {
        Task<ProductSnapshot> GetByBarcodeAsync(string barcode);
        /// <summary>Batch lookup per ridurre query N+1. Restituisce dizionario barcode->snapshot (solo barcode presenti).</summary>
        Task<IReadOnlyDictionary<string, ProductSnapshot>> GetByBarcodesAsync(IEnumerable<string> barcodes);
    }
}
