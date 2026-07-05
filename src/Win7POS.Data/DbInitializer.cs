using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core;
using Win7POS.Core.Security;

namespace Win7POS.Data
{
    public static class DbInitializer
    {
        private static readonly object MigrationLogLock = new object();

        public static void EnsureCreated(PosDbOptions opt)
        {
            SQLitePCL.Batteries_V2.Init();

            Directory.CreateDirectory(Path.GetDirectoryName(opt.DbPath));

            var factory = new SqliteConnectionFactory(opt);
            using var conn = factory.Open();
            using var tx = conn.BeginTransaction();

            LogMigrationInfo("migration start");
            try
            {
                CreateBaseTables(conn, tx);
                EnsureMigrations(conn, tx);
                CreateDependentTables(conn, tx);
                EnsureIndexes(conn, tx);
                SeedSecurity(conn, tx);
                tx.Commit();
                LogMigrationInfo("migration done");
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                LogMigrationFailure(ex);
                throw;
            }
        }

        private static void CreateBaseTables(SqliteConnection conn, SqliteTransaction tx)
        {
            conn.Execute(@"
CREATE TABLE IF NOT EXISTS products (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode   TEXT NOT NULL UNIQUE,
  name      TEXT NOT NULL,
  unitPrice INTEGER NOT NULL,
  remote_product_id TEXT NULL,
  remote_deleted_at TEXT NULL,
  is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS sales (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  client_sale_id TEXT NULL UNIQUE,
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
  source    TEXT NULL,
  remote_price_id TEXT NULL
);

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
  remote_staff_id   TEXT NULL,
  remote_staff_code TEXT NULL,
  remote_shop_id    TEXT NULL,
  remote_shop_code  TEXT NULL,
  remote_role_key   TEXT NULL,
  remote_credential_version INTEGER NULL,
  remote_synced_at  INTEGER NULL,
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
", transaction: tx);
        }

        private static void EnsureMigrations(SqliteConnection conn, SqliteTransaction tx)
        {
            EnsureColumn(conn, tx, "users", "require_pin_change", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "users", "max_discount_percent", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "users", "failed_attempts", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "users", "lockout_until", "INTEGER NULL");
            EnsureColumn(conn, tx, "users", "remote_staff_id", "TEXT NULL");
            EnsureColumn(conn, tx, "users", "remote_staff_code", "TEXT NULL");
            EnsureColumn(conn, tx, "users", "remote_shop_id", "TEXT NULL");
            EnsureColumn(conn, tx, "users", "remote_shop_code", "TEXT NULL");
            EnsureColumn(conn, tx, "users", "remote_role_key", "TEXT NULL");
            EnsureColumn(conn, tx, "users", "remote_credential_version", "INTEGER NULL");
            EnsureColumn(conn, tx, "users", "remote_synced_at", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales", "kind", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "sales", "related_sale_id", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales", "voided_by_sale_id", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales", "voided_at", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales", "reason", "TEXT NULL");
            EnsureColumn(conn, tx, "sale_lines", "related_original_line_id", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales", "operator_id", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales", "pdf_printed", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "sales", "client_sale_id", "TEXT NULL");
            EnsureColumn(conn, tx, "sales", "sync_status", "TEXT NOT NULL DEFAULT 'pending'");
            EnsureColumn(conn, tx, "products", "remote_product_id", "TEXT NULL");
            EnsureColumn(conn, tx, "products", "remote_deleted_at", "TEXT NULL");
            EnsureColumn(conn, tx, "products", "is_active", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(conn, tx, "product_price_history", "old_price", "INTEGER NULL");
            EnsureColumn(conn, tx, "product_price_history", "source", "TEXT NULL");
            EnsureColumn(conn, tx, "product_price_history", "remote_price_id", "TEXT NULL");
        }

        private static void CreateDependentTables(SqliteConnection conn, SqliteTransaction tx)
        {
            conn.Execute(@"
CREATE TABLE IF NOT EXISTS local_stock_movements (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  movement_key TEXT NOT NULL UNIQUE,
  sale_id   INTEGER NOT NULL,
  sale_line_id INTEGER NULL,
  barcode   TEXT NOT NULL,
  quantity_delta INTEGER NOT NULL,
  movement_kind TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sales_sync_outbox (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  sale_id   INTEGER NOT NULL UNIQUE,
  client_sale_id TEXT NOT NULL UNIQUE,
  client_batch_id TEXT NULL,
  idempotency_key TEXT NOT NULL UNIQUE,
  payload_json TEXT NULL,
  payload_hash TEXT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_retry_at INTEGER NOT NULL DEFAULT 0,
  last_attempt_at INTEGER NULL,
  last_error_code TEXT NULL,
  last_error_at INTEGER NULL,
  server_batch_id TEXT NULL,
  server_sale_id TEXT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY(sale_id) REFERENCES sales(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS catalog_import_outbox (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  client_import_id TEXT NOT NULL,
  idempotency_key TEXT NOT NULL,
  schema_version TEXT NOT NULL DEFAULT 'pos-catalog-import-v1',
  source TEXT NOT NULL DEFAULT 'supplier_excel',
  payload_json TEXT NOT NULL,
  payload_hash TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_retry_at INTEGER NOT NULL DEFAULT 0,
  last_attempt_at INTEGER NULL,
  last_error_code TEXT NULL,
  last_error_at INTEGER NULL,
  server_import_id TEXT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS remote_catalog_pending_prices (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  remote_price_id TEXT NULL,
  remote_product_id TEXT NOT NULL,
  type      TEXT NOT NULL,
  price     INTEGER NOT NULL,
  effective_at TEXT NOT NULL,
  source    TEXT NULL,
  created_at TEXT NOT NULL
);
", transaction: tx);

            EnsureColumn(conn, tx, "local_stock_movements", "movement_key", "TEXT NULL");
            EnsureColumn(conn, tx, "local_stock_movements", "sale_id", "INTEGER NULL");
            EnsureColumn(conn, tx, "local_stock_movements", "sale_line_id", "INTEGER NULL");
            EnsureColumn(conn, tx, "local_stock_movements", "barcode", "TEXT NULL");
            EnsureColumn(conn, tx, "local_stock_movements", "quantity_delta", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "local_stock_movements", "movement_kind", "TEXT NULL");
            EnsureColumn(conn, tx, "local_stock_movements", "created_at", "INTEGER NOT NULL DEFAULT 0");

            EnsureColumn(conn, tx, "sales_sync_outbox", "sale_id", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "client_sale_id", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "client_batch_id", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "idempotency_key", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "payload_json", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "payload_hash", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "status", "TEXT NOT NULL DEFAULT 'pending'");
            EnsureColumn(conn, tx, "sales_sync_outbox", "attempt_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "sales_sync_outbox", "next_retry_at", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "sales_sync_outbox", "last_attempt_at", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "last_error_code", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "last_error_at", "INTEGER NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "server_batch_id", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "server_sale_id", "TEXT NULL");
            EnsureColumn(conn, tx, "sales_sync_outbox", "created_at", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "sales_sync_outbox", "updated_at", "INTEGER NOT NULL DEFAULT 0");

            EnsureColumn(conn, tx, "catalog_import_outbox", "client_import_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, tx, "catalog_import_outbox", "idempotency_key", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, tx, "catalog_import_outbox", "schema_version", "TEXT NOT NULL DEFAULT 'pos-catalog-import-v1'");
            EnsureColumn(conn, tx, "catalog_import_outbox", "source", "TEXT NOT NULL DEFAULT 'supplier_excel'");
            EnsureColumn(conn, tx, "catalog_import_outbox", "payload_json", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, tx, "catalog_import_outbox", "payload_hash", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, tx, "catalog_import_outbox", "status", "TEXT NOT NULL DEFAULT 'pending'");
            EnsureColumn(conn, tx, "catalog_import_outbox", "attempt_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "catalog_import_outbox", "next_retry_at", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "catalog_import_outbox", "last_attempt_at", "INTEGER NULL");
            EnsureColumn(conn, tx, "catalog_import_outbox", "last_error_code", "TEXT NULL");
            EnsureColumn(conn, tx, "catalog_import_outbox", "last_error_at", "INTEGER NULL");
            EnsureColumn(conn, tx, "catalog_import_outbox", "server_import_id", "TEXT NULL");
            EnsureColumn(conn, tx, "catalog_import_outbox", "created_at", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "catalog_import_outbox", "updated_at", "INTEGER NOT NULL DEFAULT 0");

            EnsureColumn(conn, tx, "remote_catalog_pending_prices", "remote_price_id", "TEXT NULL");
            EnsureColumn(conn, tx, "remote_catalog_pending_prices", "remote_product_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, tx, "remote_catalog_pending_prices", "type", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, tx, "remote_catalog_pending_prices", "price", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, tx, "remote_catalog_pending_prices", "effective_at", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, tx, "remote_catalog_pending_prices", "source", "TEXT NULL");
            EnsureColumn(conn, tx, "remote_catalog_pending_prices", "created_at", "TEXT NOT NULL DEFAULT ''");
        }

        private static void EnsureIndexes(SqliteConnection conn, SqliteTransaction tx)
        {
            LogMigrationInfo("index creation phase");
            conn.Execute(@"
CREATE INDEX IF NOT EXISTS idx_sale_lines_saleId ON sale_lines(saleId);
CREATE INDEX IF NOT EXISTS idx_sale_lines_barcode ON sale_lines(barcode);
CREATE INDEX IF NOT EXISTS idx_sales_createdAt ON sales(createdAt);
CREATE INDEX IF NOT EXISTS idx_sales_client_sale_id ON sales(client_sale_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_sales_client_sale_id_unique ON sales(client_sale_id) WHERE client_sale_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_sales_sync_status ON sales(sync_status, createdAt);
CREATE INDEX IF NOT EXISTS idx_local_stock_movements_sale ON local_stock_movements(sale_id);
CREATE INDEX IF NOT EXISTS idx_local_stock_movements_barcode ON local_stock_movements(barcode);
CREATE INDEX IF NOT EXISTS idx_sales_sync_outbox_status_next ON sales_sync_outbox(status, next_retry_at, id);
CREATE INDEX IF NOT EXISTS idx_sales_sync_outbox_sale ON sales_sync_outbox(sale_id);
CREATE INDEX IF NOT EXISTS idx_sales_sync_outbox_last_attempt ON sales_sync_outbox(last_attempt_at);
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalog_import_outbox_client_import ON catalog_import_outbox(client_import_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_catalog_import_outbox_idempotency ON catalog_import_outbox(idempotency_key);
CREATE INDEX IF NOT EXISTS idx_catalog_import_outbox_status_next ON catalog_import_outbox(status, next_retry_at, id);
CREATE INDEX IF NOT EXISTS idx_catalog_import_outbox_last_attempt ON catalog_import_outbox(last_attempt_at);
CREATE INDEX IF NOT EXISTS idx_audit_log_ts ON audit_log(ts);
CREATE UNIQUE INDEX IF NOT EXISTS idx_price_history_unique
ON product_price_history(barcode, timestamp, type, new_price, coalesce(source,''));
CREATE UNIQUE INDEX IF NOT EXISTS idx_price_history_remote_price_id
ON product_price_history(remote_price_id) WHERE remote_price_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_pending_remote_price_id
ON remote_catalog_pending_prices(remote_price_id) WHERE remote_price_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_pending_remote_price_fallback
ON remote_catalog_pending_prices(remote_product_id, type, effective_at, price, coalesce(source,'')) WHERE remote_price_id IS NULL;
CREATE INDEX IF NOT EXISTS idx_pending_remote_price_product ON remote_catalog_pending_prices(remote_product_id);
CREATE INDEX IF NOT EXISTS idx_held_cart_lines_holdId ON held_cart_lines(holdId);
CREATE INDEX IF NOT EXISTS idx_security_events_ts ON security_events(ts);
CREATE INDEX IF NOT EXISTS idx_products_remote_product_id ON products(remote_product_id);
CREATE INDEX IF NOT EXISTS idx_products_active_barcode ON products(is_active, barcode);
CREATE INDEX IF NOT EXISTS idx_products_active_remote_product_id ON products(remote_product_id) WHERE COALESCE(is_active, 1) = 1;
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_remote_staff_id ON users(remote_staff_id) WHERE remote_staff_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_users_remote_shop_staff ON users(remote_shop_code, remote_staff_code);
", transaction: tx);
        }

        private static void SeedSecurity(SqliteConnection conn, SqliteTransaction tx)
        {
            conn.Execute(@"
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('admin','Admin',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('manager','Manager',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('supervisor','Supervisore',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('cashier','Cassiere',1);
", transaction: tx);

            SeedRolePermissions(conn, tx);
            // Nessun utente admin seedato: il primo admin viene creato dal wizard FirstRunSetupDialog.
        }

        private static void SeedRolePermissions(SqliteConnection conn, SqliteTransaction tx)
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

            var roleIds = conn.Query<RoleSeedRow>("SELECT code AS Code, id AS Id FROM roles", transaction: tx).ToList();
            foreach (var r in roleIds)
            {
                var perms = r.Code == "admin" ? adminPerms : r.Code == "manager" ? managerPerms : r.Code == "supervisor" ? supervisorPerms : r.Code == "cashier" ? cashierPerms : Array.Empty<string>();
                foreach (var p in perms)
                {
                    conn.Execute("INSERT OR IGNORE INTO role_permissions(role_id, permission_code) VALUES(@rid, @code)", new { rid = r.Id, code = p }, tx);
                }
            }
        }

        private static void EnsureColumn(SqliteConnection conn, SqliteTransaction tx, string table, string column, string ddl)
        {
            var info = conn.Query<TableInfoRow>("PRAGMA table_info(" + table + ");", transaction: tx).ToList();
            if (info.Any(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase)))
                return;
            conn.Execute("ALTER TABLE " + table + " ADD COLUMN " + column + " " + ddl + ";", transaction: tx);
            LogMigrationInfo("legacy column added: " + table + "." + column);
        }

        private static void LogMigrationInfo(string message)
        {
            WriteMigrationLog("INFO", message);
        }

        private static void LogMigrationFailure(Exception ex)
        {
            var detail = ex == null
                ? "migration failed"
                : "migration failed: " + ex.GetType().FullName + ": " + ex.Message;
            WriteMigrationLog("ERROR", detail);
        }

        private static void WriteMigrationLog(string level, string message)
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " [" + level + "][DbInitializer] " +
                    SanitizeLogMessage(message ?? string.Empty) +
                    Environment.NewLine;
                lock (MigrationLogLock)
                {
                    File.AppendAllText(AppPaths.LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Migration logging must never break local DB startup.
            }
        }

        private static string SanitizeLogMessage(string value)
        {
            var sanitized = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(sessionToken|deviceToken|trustedDeviceToken|pin|password|credential)\s*[:=]\s*\S+",
                "$1=[redacted]");
            sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\[^\s|]+", "[path]");
            sanitized = Regex.Replace(sanitized, @"/(?:Users|private|tmp|var)/[^\s|]+", "[path]");
            return sanitized;
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
