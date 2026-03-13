using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Repositories
{
    public sealed class CategoryRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public CategoryRepository(SqliteConnectionFactory factory) => _factory = factory;

        public async Task<IReadOnlyList<CategoryListItem>> ListAllAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<CategoryListItem>(
                "SELECT id AS Id, name AS Name FROM categories ORDER BY name ASC").ConfigureAwait(false);
            return rows.ToList();
        }
    }

    public sealed class CategoryListItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
