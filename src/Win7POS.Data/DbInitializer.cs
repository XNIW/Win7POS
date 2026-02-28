using System.IO;
using System.Linq;
using Dapper;

namespace Win7POS.Data
{
    public static class DbInitializer
    {
        public static void EnsureCreated(PosDbOptions opt)
        {
            SQLitePCL.Batteries_V2.Init();

            Directory.CreateDirectory(Path.GetDirectoryName(opt.DbPath));

            var factory = new SqliteConnectionFactory(opt);
            using var conn = factory.Open();

            conn.Execute(@"
CREATE TABLE IF NOT EXISTS products (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode   TEXT NOT NULL UNIQUE,
  name      TEXT NOT NULL,
  unitPrice INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sales (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  code      TEXT NOT NULL UNIQUE,
  createdAt INTEGER NOT NULL,
  kind      INTEGER NOT NULL DEFAULT 0,
  related_sale_id INTEGER NULL,
  voided_by_sale_id INTEGER NULL,
  voided_at INTEGER NULL,
  reason    TEXT NULL,
  total     INTEGER NOT NULL,
  paidCash  INTEGER NOT NULL,
  paidCard  INTEGER NOT NULL,
  change    INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sale_lines (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  saleId    INTEGER NOT NULL,
  productId INTEGER,
  barcode   TEXT NOT NULL,
  name      TEXT NOT NULL,
  quantity  INTEGER NOT NULL,
  unitPrice INTEGER NOT NULL,
  lineTotal INTEGER NOT NULL,
  related_original_line_id INTEGER NULL,
  FOREIGN KEY(saleId) REFERENCES sales(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS app_settings (
  key   TEXT PRIMARY KEY NOT NULL,
  value TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_sale_lines_saleId ON sale_lines(saleId);
CREATE INDEX IF NOT EXISTS idx_sale_lines_barcode ON sale_lines(barcode);
");

            EnsureMigrations(conn);
        }

        private static void EnsureMigrations(Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            EnsureColumn(conn, "sales", "kind", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "sales", "related_sale_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "voided_by_sale_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "voided_at", "INTEGER NULL");
            EnsureColumn(conn, "sales", "reason", "TEXT NULL");
            EnsureColumn(conn, "sale_lines", "related_original_line_id", "INTEGER NULL");
        }

        private static void EnsureColumn(Microsoft.Data.Sqlite.SqliteConnection conn, string table, string column, string ddl)
        {
            var info = conn.Query<TableInfoRow>("PRAGMA table_info(" + table + ");").ToList();
            if (info.Any(x => string.Equals(x.Name, column, System.StringComparison.OrdinalIgnoreCase)))
                return;
            conn.Execute("ALTER TABLE " + table + " ADD COLUMN " + column + " " + ddl + ";");
        }

        private sealed class TableInfoRow
        {
            public string Name { get; set; }
        }
    }
}
