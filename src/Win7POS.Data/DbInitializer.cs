using System;
using System.IO;
using System.Linq;
using Dapper;
using Win7POS.Core.Security;

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

CREATE TABLE IF NOT EXISTS audit_log (
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  ts      INTEGER NOT NULL,
  action  TEXT NOT NULL,
  details TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_sale_lines_saleId ON sale_lines(saleId);
CREATE INDEX IF NOT EXISTS idx_sale_lines_barcode ON sale_lines(barcode);
CREATE INDEX IF NOT EXISTS idx_sales_createdAt ON sales(createdAt);
CREATE INDEX IF NOT EXISTS idx_audit_log_ts ON audit_log(ts);

CREATE TABLE IF NOT EXISTS suppliers (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS categories (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS product_meta (
  barcode       TEXT PRIMARY KEY,
  article_code  TEXT NULL,
  name2         TEXT NULL,
  purchase_price INTEGER NOT NULL DEFAULT 0,
  purchase_old   INTEGER NOT NULL DEFAULT 0,
  retail_old     INTEGER NOT NULL DEFAULT 0,
  supplier_id    INTEGER NULL,
  supplier_name  TEXT NULL,
  category_id    INTEGER NULL,
  category_name  TEXT NULL,
  stock_qty      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS product_price_history (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode   TEXT NOT NULL,
  timestamp TEXT NOT NULL,
  type      TEXT NOT NULL,
  old_price INTEGER NULL,
  new_price INTEGER NOT NULL,
  source    TEXT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_price_history_unique
ON product_price_history(barcode, timestamp, type, new_price, coalesce(source,''));

CREATE TABLE IF NOT EXISTS held_carts (
  holdId     TEXT PRIMARY KEY NOT NULL,
  createdAtMs INTEGER NOT NULL,
  totalMinor INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS held_cart_lines (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  holdId    TEXT NOT NULL,
  barcode   TEXT NOT NULL,
  name      TEXT NOT NULL,
  unitPrice INTEGER NOT NULL,
  qty       INTEGER NOT NULL,
  FOREIGN KEY(holdId) REFERENCES held_carts(holdId) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_held_cart_lines_holdId ON held_cart_lines(holdId);

CREATE TABLE IF NOT EXISTS roles (
  id   INTEGER PRIMARY KEY AUTOINCREMENT,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  is_system INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS users (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  username          TEXT NOT NULL UNIQUE,
  display_name      TEXT NOT NULL,
  pin_hash          TEXT NOT NULL,
  pin_salt          TEXT NOT NULL,
  role_id           INTEGER NOT NULL,
  is_active         INTEGER NOT NULL DEFAULT 1,
  require_pin_change INTEGER NOT NULL DEFAULT 0,
  max_discount_percent INTEGER NOT NULL DEFAULT 0,
  created_at        INTEGER NOT NULL,
  updated_at        INTEGER NOT NULL,
  last_login_at     INTEGER NULL,
  failed_attempts   INTEGER NOT NULL DEFAULT 0,
  lockout_until     INTEGER NULL,
  FOREIGN KEY(role_id) REFERENCES roles(id)
);

CREATE TABLE IF NOT EXISTS role_permissions (
  role_id         INTEGER NOT NULL,
  permission_code TEXT NOT NULL,
  PRIMARY KEY(role_id, permission_code),
  FOREIGN KEY(role_id) REFERENCES roles(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS security_events (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  ts        INTEGER NOT NULL,
  user_id   INTEGER NULL,
  event_type TEXT NOT NULL,
  details   TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_security_events_ts ON security_events(ts);
");

            EnsureMigrations(conn);
            SeedSecurity(conn);
        }

        private static void EnsureMigrations(Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            EnsureColumn(conn, "users", "require_pin_change", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "users", "max_discount_percent", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "users", "failed_attempts", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "users", "lockout_until", "INTEGER NULL");
            EnsureColumn(conn, "sales", "kind", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "sales", "related_sale_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "voided_by_sale_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "voided_at", "INTEGER NULL");
            EnsureColumn(conn, "sales", "reason", "TEXT NULL");
            EnsureColumn(conn, "sale_lines", "related_original_line_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "operator_id", "INTEGER NULL");
            EnsureColumn(conn, "sales", "pdf_printed", "INTEGER NOT NULL DEFAULT 0");
        }

        private static void SeedSecurity(Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            conn.Execute(@"
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('admin','Admin',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('manager','Manager',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('supervisor','Supervisore',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('cashier','Cassiere',1);
");

            SeedRolePermissions(conn);
            // Nessun utente admin seedato: il primo admin viene creato dal wizard FirstRunSetupDialog.
        }

        private static void SeedRolePermissions(Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            var allPerms = new[] {
                PermissionCodes.PosSell, PermissionCodes.PosPay, PermissionCodes.PosSuspendCart, PermissionCodes.PosRecoverCart,
                PermissionCodes.PosDiscount, PermissionCodes.PosDiscountOverLimit, PermissionCodes.PosRefund, PermissionCodes.PosVoidSale,
                PermissionCodes.PosReprintReceipt,
                PermissionCodes.CatalogView, PermissionCodes.CatalogEdit, PermissionCodes.CatalogImport, PermissionCodes.CatalogPriceEdit,
                PermissionCodes.RegisterView, PermissionCodes.RegisterViewAll,
                PermissionCodes.DailyCloseView, PermissionCodes.DailyCloseRun, PermissionCodes.DailyClosePrint,
                PermissionCodes.SettingsShop, PermissionCodes.SettingsPrinter,
                PermissionCodes.DbBackup, PermissionCodes.DbRestore, PermissionCodes.DbMaintenance,
                PermissionCodes.UsersManage, PermissionCodes.RolesManage, PermissionCodes.SecurityOverride
            };
            var cashierPerms = new[] { PermissionCodes.PosSell, PermissionCodes.PosPay, PermissionCodes.PosSuspendCart, PermissionCodes.PosRecoverCart, PermissionCodes.PosReprintReceipt, PermissionCodes.CatalogView, PermissionCodes.RegisterView };
            var supervisorPerms = new[] { PermissionCodes.PosSell, PermissionCodes.PosPay, PermissionCodes.PosSuspendCart, PermissionCodes.PosRecoverCart, PermissionCodes.PosDiscount, PermissionCodes.PosRefund, PermissionCodes.PosVoidSale, PermissionCodes.PosReprintReceipt, PermissionCodes.CatalogView, PermissionCodes.RegisterView, PermissionCodes.RegisterViewAll, PermissionCodes.DailyCloseView, PermissionCodes.DailyCloseRun, PermissionCodes.DailyClosePrint, PermissionCodes.SettingsPrinter };
            var managerPerms = new[] { PermissionCodes.PosSell, PermissionCodes.PosPay, PermissionCodes.PosSuspendCart, PermissionCodes.PosRecoverCart, PermissionCodes.PosDiscount, PermissionCodes.PosDiscountOverLimit, PermissionCodes.PosRefund, PermissionCodes.PosVoidSale, PermissionCodes.PosReprintReceipt, PermissionCodes.CatalogView, PermissionCodes.CatalogEdit, PermissionCodes.CatalogPriceEdit, PermissionCodes.RegisterView, PermissionCodes.RegisterViewAll, PermissionCodes.DailyCloseView, PermissionCodes.DailyCloseRun, PermissionCodes.DailyClosePrint, PermissionCodes.SettingsShop, PermissionCodes.SettingsPrinter, PermissionCodes.DbBackup };
            var adminPerms = allPerms;

            var roleIds = conn.Query<RoleSeedRow>("SELECT code AS Code, id AS Id FROM roles").ToList();
            foreach (var r in roleIds)
            {
                var perms = r.Code == "admin" ? adminPerms : r.Code == "manager" ? managerPerms : r.Code == "supervisor" ? supervisorPerms : r.Code == "cashier" ? cashierPerms : Array.Empty<string>();
                foreach (var p in perms)
                {
                    conn.Execute("INSERT OR IGNORE INTO role_permissions(role_id, permission_code) VALUES(@rid, @code)", new { rid = r.Id, code = p });
                }
            }
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

        private sealed class RoleSeedRow
        {
            public string Code { get; set; }
            public int Id { get; set; }
        }
    }
}
