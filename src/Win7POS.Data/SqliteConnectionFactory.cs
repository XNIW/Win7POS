using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data
{
    public sealed class SqliteConnectionFactory
    {
        private readonly PosDbOptions _opt;

        public SqliteConnectionFactory(PosDbOptions opt) => _opt = opt;

        private static string BuildConnectionString(string dbPath)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                ForeignKeys = true
            }.ToString();
            return cs + ";Default Timeout=5";
        }

        public SqliteConnection Open()
        {
            var conn = new SqliteConnection(BuildConnectionString(_opt.DbPath));
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
        {
            var conn = new SqliteConnection(BuildConnectionString(_opt.DbPath));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=5000;";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return conn;
        }
    }
}
