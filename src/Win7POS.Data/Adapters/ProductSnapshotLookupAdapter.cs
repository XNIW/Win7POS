using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class ProductSnapshotLookupAdapter : IProductSnapshotLookup
    {
        private readonly ProductRepository _products;
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;

        public ProductSnapshotLookupAdapter(ProductRepository products)
        {
            _products = products;
        }

        public ProductSnapshotLookupAdapter(SqliteConnection conn, SqliteTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public async Task<ProductSnapshot> GetByBarcodeAsync(string barcode)
        {
            if (_conn != null)
            {
                return await _conn.QuerySingleOrDefaultAsync<ProductSnapshot>(
                    "SELECT barcode, name, unitPrice FROM products WHERE barcode = @barcode",
                    new { barcode },
                    _tx);
            }

            var p = await _products.GetByBarcodeAsync(barcode);
            if (p == null) return null;
            return new ProductSnapshot
            {
                Barcode = p.Barcode,
                Name = p.Name,
                UnitPrice = p.UnitPrice
            };
        }
    }
}
