using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class ProductUpserterAdapter : IProductUpserter
    {
        private readonly ProductRepository _products;

        public ProductUpserterAdapter(ProductRepository products)
        {
            _products = products;
        }

        public async Task<UpsertOutcome> UpsertAsync(Product product)
        {
            var existing = await _products.GetByBarcodeAsync(product.Barcode);
            await _products.UpsertAsync(product);
            return existing == null ? UpsertOutcome.Inserted : UpsertOutcome.Updated;
        }
    }
}
