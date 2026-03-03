using System.Collections.Generic;
using System.Linq;
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
                    _tx).ConfigureAwait(false);
            }

            var p = await _products.GetByBarcodeAsync(barcode).ConfigureAwait(false);
            if (p == null) return null;
            return new ProductSnapshot
            {
                Barcode = p.Barcode,
                Name = p.Name,
                UnitPrice = p.UnitPrice
            };
        }

        public async Task<IReadOnlyDictionary<string, ProductSnapshot>> GetByBarcodesAsync(IEnumerable<string> barcodes)
        {
            if (_conn != null)
            {
                var list = (barcodes ?? Enumerable.Empty<string>()).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Distinct().ToList();
                if (list.Count == 0) return new Dictionary<string, ProductSnapshot>();

                var rows = await _conn.QueryAsync<ProductSnapshot>(
                    "SELECT barcode, name, unitPrice FROM products WHERE barcode IN @barcodes",
                    new { barcodes = list },
                    _tx).ConfigureAwait(false);
                var dict = new Dictionary<string, ProductSnapshot>();
                foreach (var p in rows ?? Enumerable.Empty<ProductSnapshot>())
                    if (p?.Barcode != null) dict[p.Barcode] = p;
                return dict;
            }

            if (_products == null) return new Dictionary<string, ProductSnapshot>();

            var productsDict = await _products.GetByBarcodesAsync(barcodes).ConfigureAwait(false);
            var result = new Dictionary<string, ProductSnapshot>(productsDict.Count);
            foreach (var kv in productsDict)
            {
                var p = kv.Value;
                if (p != null) result[kv.Key] = new ProductSnapshot { Barcode = p.Barcode, Name = p.Name, UnitPrice = p.UnitPrice };
            }
            return result;
        }
    }
}
