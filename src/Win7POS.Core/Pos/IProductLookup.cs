using System.Threading.Tasks;
using Win7POS.Core.Models;

namespace Win7POS.Core.Pos
{
    public interface IProductLookup
    {
        Task<Product> GetByBarcodeAsync(string barcode);
    }
}
