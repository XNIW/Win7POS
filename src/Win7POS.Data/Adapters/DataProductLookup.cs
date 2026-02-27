using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class DataProductLookup : IProductLookup
    {
        private readonly ProductRepository _products;

        public DataProductLookup(ProductRepository products) => _products = products;

        public Task<Product> GetByBarcodeAsync(string barcode) => _products.GetByBarcodeAsync(barcode);
    }
}
