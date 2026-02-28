using System.IO;
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
  FOREIGN KEY(saleId) REFERENCES sales(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS app_settings (
  key   TEXT PRIMARY KEY NOT NULL,
  value TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_sale_lines_saleId ON sale_lines(saleId);
CREATE INDEX IF NOT EXISTS idx_sale_lines_barcode ON sale_lines(barcode);
");
        }
    }
}
