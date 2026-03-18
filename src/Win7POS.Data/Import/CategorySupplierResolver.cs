using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.ImportDb;

namespace Win7POS.Data.Import
{
    /// <summary>Risolve nome categoria/fornitore a ID; crea se non esiste. Supporta fogli dedicati come priorità 1, poi DB esistente, poi creazione.</summary>
    public sealed class CategorySupplierResolver
    {
        private readonly SqliteConnection _conn;
        private readonly SqliteTransaction _tx;
        private readonly Dictionary<string, int> _supplierCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _categoryCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyDictionary<string, int> _initialSuppliers;
        private readonly IReadOnlyDictionary<string, int> _initialCategories;

        public int SuppliersFromSheet { get; private set; }
        public int SuppliersFromDb { get; private set; }
        public int SuppliersCreated { get; private set; }
        public int CategoriesFromSheet { get; private set; }
        public int CategoriesFromDb { get; private set; }
        public int CategoriesCreated { get; private set; }

        public CategorySupplierResolver(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<SupplierRow> dedicatedSuppliers = null,
            IReadOnlyList<CategoryRow> dedicatedCategories = null)
        {
            _conn = conn ?? throw new ArgumentNullException(nameof(conn));
            _tx = tx ?? throw new ArgumentNullException(nameof(tx));
            _initialSuppliers = BuildInitialMap(dedicatedSuppliers);
            _initialCategories = BuildInitialMap(dedicatedCategories);
        }

        private static IReadOnlyDictionary<string, int> BuildInitialMap(IReadOnlyList<SupplierRow> rows)
        {
            if (rows == null || rows.Count == 0) return new Dictionary<string, int>();
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var key = Normalize(r?.Name);
                if (key.Length > 0 && !map.ContainsKey(key))
                    map[key] = r.Id;
            }
            return map;
        }

        private static IReadOnlyDictionary<string, int> BuildInitialMap(IReadOnlyList<CategoryRow> rows)
        {
            if (rows == null || rows.Count == 0) return new Dictionary<string, int>();
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var key = Normalize(r?.Name);
                if (key.Length > 0 && !map.ContainsKey(key))
                    map[key] = r.Id;
            }
            return map;
        }

        /// <summary>Normalizza: trim, case-insensitive match, collapse spazi doppi. Vuoto restituisce string.Empty.</summary>
        public static string Normalize(string s)
        {
            if (s == null) return string.Empty;
            var t = s.Trim();
            if (t.Length == 0) return string.Empty;
            return string.Join(" ", t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        public async Task<int?> GetOrCreateSupplierIdAsync(string name)
        {
            var key = Normalize(name);
            if (key.Length == 0) return null;

            if (_supplierCache.TryGetValue(key, out var cached)) return cached;
            if (_initialSuppliers != null && _initialSuppliers.TryGetValue(key, out var fromSheet))
            {
                SuppliersFromSheet++;
                _supplierCache[key] = fromSheet;
                return fromSheet;
            }

            var existing = await _conn.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM suppliers WHERE LOWER(TRIM(REPLACE(REPLACE(name, char(10), ' '), char(13), ' '))) = LOWER(@key)",
                new { key }, _tx).ConfigureAwait(false);
            if (existing.HasValue)
            {
                SuppliersFromDb++;
                _supplierCache[key] = existing.Value;
                return existing.Value;
            }

            var nextId = await _conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(id),0)+1 FROM suppliers", null, _tx).ConfigureAwait(false);
            await _conn.ExecuteAsync("INSERT INTO suppliers(id, name) VALUES(@id, @name)", new { id = nextId, name = key }, _tx).ConfigureAwait(false);
            SuppliersCreated++;
            _supplierCache[key] = nextId;
            return nextId;
        }

        public async Task<int?> GetOrCreateCategoryIdAsync(string name)
        {
            var key = Normalize(name);
            if (key.Length == 0) return null;

            if (_categoryCache.TryGetValue(key, out var cached)) return cached;
            if (_initialCategories != null && _initialCategories.TryGetValue(key, out var fromSheet))
            {
                CategoriesFromSheet++;
                _categoryCache[key] = fromSheet;
                return fromSheet;
            }

            var existing = await _conn.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM categories WHERE LOWER(TRIM(REPLACE(REPLACE(name, char(10), ' '), char(13), ' '))) = LOWER(@key)",
                new { key }, _tx).ConfigureAwait(false);
            if (existing.HasValue)
            {
                CategoriesFromDb++;
                _categoryCache[key] = existing.Value;
                return existing.Value;
            }

            var nextId = await _conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(id),0)+1 FROM categories", null, _tx).ConfigureAwait(false);
            await _conn.ExecuteAsync("INSERT INTO categories(id, name) VALUES(@id, @name)", new { id = nextId, name = key }, _tx).ConfigureAwait(false);
            CategoriesCreated++;
            _categoryCache[key] = nextId;
            return nextId;
        }

        public int SuppliersResolved => _supplierCache.Count;
        public int CategoriesResolved => _categoryCache.Count;
    }
}
