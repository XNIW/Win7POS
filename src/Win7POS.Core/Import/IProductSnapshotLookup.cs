using System.Threading.Tasks;

namespace Win7POS.Core.Import
{
    public interface IProductSnapshotLookup
    {
        Task<ProductSnapshot> GetByBarcodeAsync(string barcode);
    }
}
