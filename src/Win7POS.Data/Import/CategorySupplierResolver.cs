using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
        private readonly SupplierExcelImportSqlMetrics _metrics;
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
            IReadOnlyList<CategoryRow> dedicatedCategories = null,
            SupplierExcelImportSqlMetrics metrics = null)
        {
            _conn = conn ?? throw new ArgumentNullException(nameof(conn));
            _tx = tx ?? throw new ArgumentNullException(nameof(tx));
            _metrics = metrics;
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

        /// <summary>
        /// Resolves every relevant name before product writes. Reads are bounded
        /// by the shared supplier-import parameter batch and inserts reuse one
        /// prepared command per reference type.
        /// </summary>
        public async Task PreloadAsync(
            IEnumerable<string> supplierNames,
            IEnumerable<string> categoryNames,
            CancellationToken cancellationToken)
        {
            await PreloadReferencesAsync(
                supplierNames,
                true,
                _supplierCache,
                _initialSuppliers,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await PreloadReferencesAsync(
                categoryNames,
                false,
                _categoryCache,
                _initialCategories,
                cancellationToken).ConfigureAwait(false);
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

            _metrics?.RecordSupplierSelect();
            var existing = await _conn.QuerySingleOrDefaultAsync<int?>(
                @"SELECT id
FROM suppliers
WHERE COALESCE(is_active, 1) = 1
  AND LOWER(TRIM(REPLACE(REPLACE(name, char(10), ' '), char(13), ' '))) = LOWER(@key)",
                new { key }, _tx).ConfigureAwait(false);
            if (existing.HasValue)
            {
                SuppliersFromDb++;
                _supplierCache[key] = existing.Value;
                return existing.Value;
            }

            _metrics?.RecordSupplierMaxId();
            var nextId = await _conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(id),0)+1 FROM suppliers", null, _tx).ConfigureAwait(false);
            _metrics?.RecordSupplierInsert();
            await _conn.ExecuteAsync("INSERT INTO suppliers(id, name, is_active) VALUES(@id, @name, 1)", new { id = nextId, name = key }, _tx).ConfigureAwait(false);
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

            _metrics?.RecordCategorySelect();
            var existing = await _conn.QuerySingleOrDefaultAsync<int?>(
                @"SELECT id
FROM categories
WHERE COALESCE(is_active, 1) = 1
  AND LOWER(TRIM(REPLACE(REPLACE(name, char(10), ' '), char(13), ' '))) = LOWER(@key)",
                new { key }, _tx).ConfigureAwait(false);
            if (existing.HasValue)
            {
                CategoriesFromDb++;
                _categoryCache[key] = existing.Value;
                return existing.Value;
            }

            _metrics?.RecordCategoryMaxId();
            var nextId = await _conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(id),0)+1 FROM categories", null, _tx).ConfigureAwait(false);
            _metrics?.RecordCategoryInsert();
            await _conn.ExecuteAsync("INSERT INTO categories(id, name, is_active) VALUES(@id, @name, 1)", new { id = nextId, name = key }, _tx).ConfigureAwait(false);
            CategoriesCreated++;
            _categoryCache[key] = nextId;
            return nextId;
        }

        public int SuppliersResolved => _supplierCache.Count;
        public int CategoriesResolved => _categoryCache.Count;

        private async Task PreloadReferencesAsync(
            IEnumerable<string> names,
            bool suppliers,
            IDictionary<string, int> cache,
            IReadOnlyDictionary<string, int> initial,
            CancellationToken cancellationToken)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in names ?? Enumerable.Empty<string>())
            {
                var key = Normalize(value);
                if (key.Length == 0 || !seen.Add(key))
                    continue;
                int fromSheet;
                if (initial != null && initial.TryGetValue(key, out fromSheet))
                {
                    cache[key] = fromSheet;
                    if (suppliers) SuppliersFromSheet++;
                    else CategoriesFromSheet++;
                    continue;
                }
                ordered.Add(key);
            }

            for (var offset = 0; offset < ordered.Count; offset += SupplierExcelImportBatching.ParameterBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = ordered
                    .Skip(offset)
                    .Take(SupplierExcelImportBatching.ParameterBatchSize)
                    .ToArray();
                using (var command = _conn.CreateCommand())
                {
                    command.Transaction = _tx;
                    command.CommandText = BuildReferenceLookupSql(suppliers, batch.Length);
                    for (var index = 0; index < batch.Length; index++)
                        command.Parameters.AddWithValue("@key" + index, batch[index]);
                    command.Prepare();
                    if (suppliers) _metrics?.RecordSupplierSelect();
                    else _metrics?.RecordCategorySelect();
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var id = reader.GetInt32(0);
                            var key = reader.GetString(1);
                            if (!matched.Add(key))
                                throw new InvalidOperationException((suppliers ? "Supplier" : "Category") + " name is not unique: " + key);
                            cache[key] = id;
                            if (suppliers) SuppliersFromDb++;
                            else CategoriesFromDb++;
                        }
                    }
                }
            }

            var missing = ordered.Where(key => !cache.ContainsKey(key)).ToArray();
            if (missing.Length == 0)
                return;

            var table = suppliers ? "suppliers" : "categories";
            using (var maxCommand = _conn.CreateCommand())
            using (var insertCommand = _conn.CreateCommand())
            {
                maxCommand.Transaction = _tx;
                maxCommand.CommandText = "SELECT COALESCE(MAX(id),0) FROM " + table;
                maxCommand.Prepare();
                if (suppliers) _metrics?.RecordSupplierMaxId();
                else _metrics?.RecordCategoryMaxId();
                var nextId = Convert.ToInt32(
                    await maxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) + 1;

                insertCommand.Transaction = _tx;
                insertCommand.CommandText = "INSERT INTO " + table + "(id, name, is_active) VALUES(@id, @name, 1)";
                var idParameter = insertCommand.Parameters.Add("@id", SqliteType.Integer);
                var nameParameter = insertCommand.Parameters.Add("@name", SqliteType.Text);
                insertCommand.Prepare();
                foreach (var key in missing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    idParameter.Value = nextId;
                    nameParameter.Value = key;
                    if (suppliers) _metrics?.RecordSupplierInsert();
                    else _metrics?.RecordCategoryInsert();
                    await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    cache[key] = nextId;
                    if (suppliers) SuppliersCreated++;
                    else CategoriesCreated++;
                    nextId++;
                }
            }
        }

        private static string BuildReferenceLookupSql(bool suppliers, int count)
        {
            var values = new StringBuilder();
            for (var index = 0; index < count; index++)
            {
                if (index > 0) values.Append(',');
                values.Append('(').Append(index).Append(", @key").Append(index).Append(')');
            }

            var table = suppliers ? "suppliers" : "categories";
            return @"WITH requested(ord, key) AS (VALUES " + values + @")
SELECT item.id, requested.key
FROM requested
JOIN " + table + @" item
  ON COALESCE(item.is_active, 1) = 1
 AND LOWER(TRIM(REPLACE(REPLACE(item.name, char(10), ' '), char(13), ' '))) = LOWER(requested.key)
ORDER BY requested.ord ASC, item.id ASC;";
        }
    }
}
