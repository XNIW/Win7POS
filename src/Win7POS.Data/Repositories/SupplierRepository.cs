using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Repositories
{
    public sealed class SupplierRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public SupplierRepository(SqliteConnectionFactory factory) => _factory = factory;

        public async Task<IReadOnlyList<SupplierListItem>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SupplierListItem>(
                "SELECT id AS Id, name AS Name FROM suppliers ORDER BY name ASC");
            return rows.ToList();
        }
    }

    public sealed class SupplierListItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
