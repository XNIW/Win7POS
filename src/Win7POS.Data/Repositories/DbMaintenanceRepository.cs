using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Repositories
{
    public sealed class DbMaintenanceRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public DbMaintenanceRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<string> IntegrityCheckAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<string>("PRAGMA integrity_check;");
            var list = rows.ToList();
            if (list.Count == 0) return "OK";
            return string.Join(Environment.NewLine, list);
        }

        public async Task VacuumAsync()
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync("VACUUM;");
        }

        public async Task WalCheckpointAsync()
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(FULL);");
        }
    }
}
