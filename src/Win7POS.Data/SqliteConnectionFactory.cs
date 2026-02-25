using Microsoft.Data.Sqlite;

namespace Win7POS.Data
{
    public sealed class SqliteConnectionFactory
    {
        private readonly PosDbOptions _opt;

        public SqliteConnectionFactory(PosDbOptions opt) => _opt = opt;

        public SqliteConnection Open()
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _opt.DbPath,
                ForeignKeys = true
            }.ToString();

            var conn = new SqliteConnection(cs);
            conn.Open();
            return conn;
        }
    }
}
