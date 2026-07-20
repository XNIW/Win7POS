using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data.Migrations;
using Win7POS.Data.Online;

namespace Win7POS.Data
{
    public static class DbInitializer
    {
        private static readonly object MigrationLogLock = new object();

        public static void EnsureCreated(PosDbOptions opt)
        {
            SQLitePCL.Batteries_V2.Init();

            var directory = Path.GetDirectoryName(opt.DbPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(opt);
            LogMigrationInfo("migration start");
            try
            {
                SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() =>
                {
                    new SchemaMigrationRunner(
                        factory,
                        SchemaMigrationRegistry.All,
                        new SchemaMigrationRunnerOptions { Log = LogMigrationInfo })
                        .Run();

                    ValidateCurrentSchema(factory);

                    // Outbox rows and system-role grants remain mutable after a
                    // migration has been recorded. Reconcile after both no-op
                    // starts and upgrades, but only after the latest structural
                    // state is known safe; published migrations are never replayed.
                    ReconcileMutableInvariants(factory);

                    return System.Threading.Tasks.Task.CompletedTask;
                }).GetAwaiter().GetResult();
                LogMigrationInfo("migration done");
            }
            catch (Exception ex)
            {
                LogMigrationFailure(ex);
                throw;
            }
        }

        private static void ReconcileMutableInvariants(SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    BackfillLegacyOutboxBindings(connection, transaction);
                    SeedSecurity(connection, transaction);
                    transaction.Commit();
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
        }

        internal static string CoreSchemaSql => @"
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
  id                 INTEGER PRIMARY KEY,
  name               TEXT NOT NULL,
  remote_supplier_id TEXT NULL,
  remote_updated_at  TEXT NULL,
  remote_deleted_at  TEXT NULL,
  is_active          INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS categories (
  id                 INTEGER PRIMARY KEY,
  name               TEXT NOT NULL,
  remote_category_id TEXT NULL,
  remote_updated_at  TEXT NULL,
  remote_deleted_at  TEXT NULL,
  is_active          INTEGER NOT NULL DEFAULT 1
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
  remote_price_id TEXT NULL,
  catalog_import_client_item_id TEXT NULL,
  catalog_import_idempotency_key TEXT NULL
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
";

        // Structural fingerprint for the oldest supported ledgerless schema.
        // Later additive columns are intentionally excluded because migration 0002
        // owns them; types, nullability, defaults, PK ordinals and UNIQUE keys in
        // this prefix are still compared exactly before any migration is ledgered.
        internal static string CoreSchemaFingerprintSql => @"
CREATE TABLE products (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode   TEXT NOT NULL UNIQUE,
  name      TEXT NOT NULL,
  unitPrice INTEGER NOT NULL
);

CREATE TABLE sales (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  code      TEXT NOT NULL UNIQUE,
  createdAt INTEGER NOT NULL,
  total     INTEGER NOT NULL,
  paidCash  INTEGER NOT NULL,
  paidCard  INTEGER NOT NULL,
  change    INTEGER NOT NULL
);

CREATE TABLE sale_lines (
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

CREATE TABLE app_settings (
  key   TEXT PRIMARY KEY NOT NULL,
  value TEXT NOT NULL
);

CREATE TABLE audit_log (
  id      INTEGER PRIMARY KEY AUTOINCREMENT,
  ts      INTEGER NOT NULL,
  action  TEXT NOT NULL,
  details TEXT NOT NULL
);

CREATE TABLE suppliers (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL
);

CREATE TABLE categories (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL
);

CREATE TABLE product_meta (
  barcode        TEXT PRIMARY KEY,
  article_code   TEXT NULL,
  name2          TEXT NULL,
  purchase_price INTEGER NOT NULL DEFAULT 0,
  purchase_old   INTEGER NOT NULL DEFAULT 0,
  retail_old     INTEGER NOT NULL DEFAULT 0,
  supplier_id    INTEGER NULL,
  supplier_name  TEXT NULL,
  category_id    INTEGER NULL,
  category_name  TEXT NULL,
  stock_qty      INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE product_price_history (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode   TEXT NOT NULL,
  timestamp TEXT NOT NULL,
  type      TEXT NOT NULL,
  new_price INTEGER NOT NULL
);

CREATE TABLE held_carts (
  holdId      TEXT PRIMARY KEY NOT NULL,
  createdAtMs INTEGER NOT NULL,
  totalMinor  INTEGER NOT NULL
);

CREATE TABLE held_cart_lines (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  holdId    TEXT NOT NULL,
  barcode   TEXT NOT NULL,
  name      TEXT NOT NULL,
  unitPrice INTEGER NOT NULL,
  qty       INTEGER NOT NULL,
  FOREIGN KEY(holdId) REFERENCES held_carts(holdId) ON DELETE CASCADE
);

CREATE TABLE roles (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  code      TEXT NOT NULL UNIQUE,
  name      TEXT NOT NULL,
  is_system INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE users (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  username     TEXT NOT NULL UNIQUE,
  display_name TEXT NOT NULL,
  pin_hash     TEXT NOT NULL,
  pin_salt     TEXT NOT NULL,
  role_id      INTEGER NOT NULL,
  is_active    INTEGER NOT NULL DEFAULT 1,
  created_at   INTEGER NOT NULL,
  updated_at   INTEGER NOT NULL,
  FOREIGN KEY(role_id) REFERENCES roles(id)
);

CREATE TABLE role_permissions (
  role_id         INTEGER NOT NULL,
  permission_code TEXT NOT NULL,
  PRIMARY KEY(role_id, permission_code),
  FOREIGN KEY(role_id) REFERENCES roles(id) ON DELETE CASCADE
);

CREATE TABLE security_events (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  ts         INTEGER NOT NULL,
  user_id    INTEGER NULL,
  event_type TEXT NOT NULL,
  details    TEXT NOT NULL
);
";

        internal static void CreateBaseTables(SqliteConnection conn, SqliteTransaction tx)
        {
            conn.Execute(CoreSchemaSql, transaction: tx);
        }

        internal static SchemaColumnDefinition[] SupportedLegacyColumns { get; } = new[]
        {
            Column("users", "require_pin_change", "INTEGER", true, "0", "INTEGER NOT NULL DEFAULT 0"),
            Column("users", "max_discount_percent", "INTEGER", true, "0", "INTEGER NOT NULL DEFAULT 0"),
            Column("users", "last_login_at", "INTEGER", false, "", "INTEGER NULL"),
            Column("users", "failed_attempts", "INTEGER", true, "0", "INTEGER NOT NULL DEFAULT 0"),
            Column("users", "lockout_until", "INTEGER", false, "", "INTEGER NULL"),
            Column("users", "remote_staff_id", "TEXT", false, "", "TEXT NULL"),
            Column("users", "remote_staff_code", "TEXT", false, "", "TEXT NULL"),
            Column("users", "remote_shop_id", "TEXT", false, "", "TEXT NULL"),
            Column("users", "remote_shop_code", "TEXT", false, "", "TEXT NULL"),
            Column("users", "remote_role_key", "TEXT", false, "", "TEXT NULL"),
            Column("users", "remote_credential_version", "INTEGER", false, "", "INTEGER NULL"),
            Column("users", "remote_synced_at", "INTEGER", false, "", "INTEGER NULL"),
            Column("sales", "kind", "INTEGER", true, "0", "INTEGER NOT NULL DEFAULT 0"),
            Column("sales", "related_sale_id", "INTEGER", false, "", "INTEGER NULL"),
            Column("sales", "voided_by_sale_id", "INTEGER", false, "", "INTEGER NULL"),
            Column("sales", "voided_at", "INTEGER", false, "", "INTEGER NULL"),
            Column("sales", "reason", "TEXT", false, "", "TEXT NULL"),
            Column("sale_lines", "related_original_line_id", "INTEGER", false, "", "INTEGER NULL"),
            Column("sales", "operator_id", "INTEGER", false, "", "INTEGER NULL"),
            Column("sales", "pdf_printed", "INTEGER", true, "0", "INTEGER NOT NULL DEFAULT 0"),
            Column("sales", "client_sale_id", "TEXT", false, "", "TEXT NULL"),
            Column("sales", "sync_status", "TEXT", true, "'pending'", "TEXT NOT NULL DEFAULT 'pending'"),
            Column("products", "remote_product_id", "TEXT", false, "", "TEXT NULL"),
            Column("products", "remote_deleted_at", "TEXT", false, "", "TEXT NULL"),
            Column("products", "is_active", "INTEGER", true, "1", "INTEGER NOT NULL DEFAULT 1"),
            Column("categories", "remote_category_id", "TEXT", false, "", "TEXT NULL"),
            Column("categories", "remote_updated_at", "TEXT", false, "", "TEXT NULL"),
            Column("categories", "remote_deleted_at", "TEXT", false, "", "TEXT NULL"),
            Column("categories", "is_active", "INTEGER", true, "1", "INTEGER NOT NULL DEFAULT 1"),
            Column("suppliers", "remote_supplier_id", "TEXT", false, "", "TEXT NULL"),
            Column("suppliers", "remote_updated_at", "TEXT", false, "", "TEXT NULL"),
            Column("suppliers", "remote_deleted_at", "TEXT", false, "", "TEXT NULL"),
            Column("suppliers", "is_active", "INTEGER", true, "1", "INTEGER NOT NULL DEFAULT 1"),
            Column("product_price_history", "old_price", "INTEGER", false, "", "INTEGER NULL"),
            Column("product_price_history", "source", "TEXT", false, "", "TEXT NULL"),
            Column("product_price_history", "remote_price_id", "TEXT", false, "", "TEXT NULL"),
            Column("product_price_history", "catalog_import_client_item_id", "TEXT", false, "", "TEXT NULL"),
            Column("product_price_history", "catalog_import_idempotency_key", "TEXT", false, "", "TEXT NULL")
        };

        internal static string SupportedLegacyColumnMaterial => string.Join(
            "\n",
            SupportedLegacyColumns.Select(item => item.ToCanonicalMaterial()));

        internal static void EnsureMigrations(SqliteConnection conn, SqliteTransaction tx)
        {
            foreach (var column in SupportedLegacyColumns)
            {
                EnsureColumn(
                    conn,
                    tx,
                    column.TableName,
                    column.ColumnName,
                    column.AlterDefinition);
            }
        }

        private static void ValidateCurrentSchema(SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                var detector = new LegacySchemaDetector(connection);
                if (!SchemaMigrationRegistry.IsCurrentSchemaStructurallyValid(detector))
                {
                    throw new InvalidDataException(
                        "SQLite schema does not match the latest published migration state.");
                }
            }
        }

        internal static SchemaColumnDefinition ReceiptShopSnapshotColumn { get; } =
            Column("sales", "receipt_shop_snapshot", "TEXT", false, "", "TEXT NULL");

        internal static string ReceiptShopSnapshotAlterSql =>
            "ALTER TABLE sales ADD COLUMN receipt_shop_snapshot TEXT NULL;";

        internal static void EnsureReceiptShopSnapshot(SqliteConnection conn, SqliteTransaction tx)
        {
            EnsureColumn(
                conn,
                tx,
                ReceiptShopSnapshotColumn.TableName,
                ReceiptShopSnapshotColumn.ColumnName,
                ReceiptShopSnapshotColumn.AlterDefinition);
        }

        internal static SchemaColumnDefinition SalesSyncClaimGenerationColumn { get; } =
            Column("sales_sync_outbox", "claim_generation_id", "TEXT", false, "", "TEXT NULL");

        internal static SchemaColumnDefinition SalesSyncClaimTokenColumn { get; } =
            Column("sales_sync_outbox", "claim_token", "TEXT", false, "", "TEXT NULL");

        internal static SchemaColumnDefinition CatalogImportClaimGenerationColumn { get; } =
            Column("catalog_import_outbox", "claim_generation_id", "TEXT", false, "", "TEXT NULL");

        internal static SchemaColumnDefinition CatalogImportClaimTokenColumn { get; } =
            Column("catalog_import_outbox", "claim_token", "TEXT", false, "", "TEXT NULL");

        internal static SchemaColumnDefinition[] OnlineSyncGenerationColumns { get; } = new[]
        {
            SalesSyncClaimGenerationColumn,
            SalesSyncClaimTokenColumn,
            CatalogImportClaimGenerationColumn,
            CatalogImportClaimTokenColumn
        };

        internal static string OnlineSyncGenerationColumnMaterial => string.Join(
            "\n",
            OnlineSyncGenerationColumns.Select(item => item.ToCanonicalMaterial()));

        internal static string OnlineSyncGenerationSchemaSql => @"
CREATE TABLE IF NOT EXISTS pos_sync_session_generation (
  singleton_id INTEGER PRIMARY KEY NOT NULL CHECK(singleton_id = 1),
  generation_id TEXT NOT NULL,
  fingerprint TEXT NOT NULL,
  pos_session_id TEXT NOT NULL,
  shop_device_id TEXT NOT NULL,
  shop_id TEXT NOT NULL,
  shop_code TEXT NOT NULL,
  active INTEGER NOT NULL CHECK(active IN (0, 1)),
  auth_stop_reason TEXT NULL,
  activated_at INTEGER NOT NULL,
  stopped_at INTEGER NULL
);
";

        internal static string OnlineSyncGenerationAlterSql =>
            "ALTER TABLE sales_sync_outbox ADD COLUMN claim_generation_id TEXT NULL;\n" +
            "ALTER TABLE sales_sync_outbox ADD COLUMN claim_token TEXT NULL;\n" +
            "ALTER TABLE catalog_import_outbox ADD COLUMN claim_generation_id TEXT NULL;\n" +
            "ALTER TABLE catalog_import_outbox ADD COLUMN claim_token TEXT NULL;";

        internal static void EnsureOnlineSyncGenerationSchema(
            SqliteConnection conn,
            SqliteTransaction tx)
        {
            conn.Execute(OnlineSyncGenerationSchemaSql, transaction: tx);
            foreach (var column in OnlineSyncGenerationColumns)
            {
                EnsureColumn(
                    conn,
                    tx,
                    column.TableName,
                    column.ColumnName,
                    column.AlterDefinition);
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            conn.Execute(@"
UPDATE sales_sync_outbox
SET status = 'retry',
    attempt_count = CASE WHEN attempt_count > 0 THEN attempt_count - 1 ELSE 0 END,
    next_retry_at = 0,
    last_attempt_at = NULL,
    last_error_code = 'session_generation_upgrade',
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE status = 'in_progress';

UPDATE sales
SET sync_status = 'retry'
WHERE id IN (
  SELECT sale_id
  FROM sales_sync_outbox
  WHERE status = 'retry'
    AND last_error_code = 'session_generation_upgrade'
);

UPDATE catalog_import_outbox
SET status = 'retry',
    attempt_count = CASE WHEN attempt_count > 0 THEN attempt_count - 1 ELSE 0 END,
    next_retry_at = 0,
    last_attempt_at = NULL,
    last_error_code = 'session_generation_upgrade',
    last_error_at = @nowMs,
    claim_generation_id = NULL,
    claim_token = NULL,
    updated_at = @nowMs
WHERE status = 'in_progress';",
                new { nowMs },
                tx);
        }

        internal static string DependentSchemaSql => @"
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
  schema_version TEXT NOT NULL DEFAULT 'pos-sales-ledger-v2',
  operation_type TEXT NOT NULL DEFAULT 'sale',
  origin_shop_id TEXT NULL,
  origin_shop_code TEXT NULL,
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
  operation_type TEXT NOT NULL DEFAULT 'catalog_import',
  origin_shop_id TEXT NULL,
  origin_shop_code TEXT NULL,
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
  server_request_id TEXT NULL,
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

CREATE TABLE IF NOT EXISTS remote_catalog_product_references (
  remote_product_id  TEXT PRIMARY KEY NOT NULL,
  remote_category_id TEXT NULL,
  remote_supplier_id TEXT NULL
);

CREATE TABLE IF NOT EXISTS remote_catalog_price_ownership (
  remote_price_id   TEXT PRIMARY KEY NOT NULL,
  remote_product_id TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS remote_catalog_price_evidence_quarantine (
  id                              INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  evidence_kind                   TEXT NOT NULL,
  evidence_row_id                 INTEGER NOT NULL,
  remote_price_id                 TEXT NOT NULL,
  remote_product_id               TEXT NULL,
  barcode                         TEXT NULL,
  effective_at                    TEXT NOT NULL,
  type                            TEXT NOT NULL,
  old_price                       INTEGER NULL,
  price                           INTEGER NOT NULL,
  source                          TEXT NULL,
  catalog_import_client_item_id   TEXT NULL,
  catalog_import_idempotency_key  TEXT NULL,
  original_created_at             TEXT NULL,
  authoritative_remote_product_id TEXT NOT NULL,
  reason                          TEXT NOT NULL,
  quarantined_at                  TEXT NOT NULL,
  UNIQUE(evidence_kind, evidence_row_id, remote_price_id)
);
";

        internal static SchemaColumnDefinition[] DependentLegacyColumns { get; } = new[]
        {
            Column("local_stock_movements", "movement_key", "TEXT", true, "", "TEXT NULL"),
            Column("local_stock_movements", "sale_id", "INTEGER", true, "", "INTEGER NULL"),
            new SchemaColumnDefinition("local_stock_movements", "sale_line_id", "INTEGER NULL"),
            Column("local_stock_movements", "barcode", "TEXT", true, "", "TEXT NULL"),
            Column("local_stock_movements", "quantity_delta", "INTEGER", true, "", "INTEGER NOT NULL DEFAULT 0"),
            Column("local_stock_movements", "movement_kind", "TEXT", true, "", "TEXT NULL"),
            Column("local_stock_movements", "created_at", "INTEGER", true, "", "INTEGER NOT NULL DEFAULT 0"),
            Column("sales_sync_outbox", "sale_id", "INTEGER", true, "", "INTEGER NULL"),
            Column("sales_sync_outbox", "client_sale_id", "TEXT", true, "", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "client_batch_id", "TEXT NULL"),
            Column("sales_sync_outbox", "idempotency_key", "TEXT", true, "", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "schema_version", "TEXT NOT NULL DEFAULT 'pos-sales-ledger-v2'"),
            new SchemaColumnDefinition("sales_sync_outbox", "operation_type", "TEXT NOT NULL DEFAULT 'sale'"),
            new SchemaColumnDefinition("sales_sync_outbox", "origin_shop_id", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "origin_shop_code", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "payload_json", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "payload_hash", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "status", "TEXT NOT NULL DEFAULT 'pending'"),
            new SchemaColumnDefinition("sales_sync_outbox", "attempt_count", "INTEGER NOT NULL DEFAULT 0"),
            new SchemaColumnDefinition("sales_sync_outbox", "next_retry_at", "INTEGER NOT NULL DEFAULT 0"),
            new SchemaColumnDefinition("sales_sync_outbox", "last_attempt_at", "INTEGER NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "last_error_code", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "last_error_at", "INTEGER NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "server_batch_id", "TEXT NULL"),
            new SchemaColumnDefinition("sales_sync_outbox", "server_sale_id", "TEXT NULL"),
            Column("sales_sync_outbox", "created_at", "INTEGER", true, "", "INTEGER NOT NULL DEFAULT 0"),
            Column("sales_sync_outbox", "updated_at", "INTEGER", true, "", "INTEGER NOT NULL DEFAULT 0"),
            Column("catalog_import_outbox", "client_import_id", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''"),
            Column("catalog_import_outbox", "idempotency_key", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''"),
            new SchemaColumnDefinition("catalog_import_outbox", "schema_version", "TEXT NOT NULL DEFAULT 'pos-catalog-import-v1'"),
            new SchemaColumnDefinition("catalog_import_outbox", "operation_type", "TEXT NOT NULL DEFAULT 'catalog_import'"),
            new SchemaColumnDefinition("catalog_import_outbox", "origin_shop_id", "TEXT NULL"),
            new SchemaColumnDefinition("catalog_import_outbox", "origin_shop_code", "TEXT NULL"),
            new SchemaColumnDefinition("catalog_import_outbox", "source", "TEXT NOT NULL DEFAULT 'supplier_excel'"),
            Column("catalog_import_outbox", "payload_json", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''"),
            Column("catalog_import_outbox", "payload_hash", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''"),
            new SchemaColumnDefinition("catalog_import_outbox", "status", "TEXT NOT NULL DEFAULT 'pending'"),
            new SchemaColumnDefinition("catalog_import_outbox", "attempt_count", "INTEGER NOT NULL DEFAULT 0"),
            new SchemaColumnDefinition("catalog_import_outbox", "next_retry_at", "INTEGER NOT NULL DEFAULT 0"),
            new SchemaColumnDefinition("catalog_import_outbox", "last_attempt_at", "INTEGER NULL"),
            new SchemaColumnDefinition("catalog_import_outbox", "last_error_code", "TEXT NULL"),
            new SchemaColumnDefinition("catalog_import_outbox", "last_error_at", "INTEGER NULL"),
            new SchemaColumnDefinition("catalog_import_outbox", "server_import_id", "TEXT NULL"),
            new SchemaColumnDefinition("catalog_import_outbox", "server_request_id", "TEXT NULL"),
            Column("catalog_import_outbox", "created_at", "INTEGER", true, "", "INTEGER NOT NULL DEFAULT 0"),
            Column("catalog_import_outbox", "updated_at", "INTEGER", true, "", "INTEGER NOT NULL DEFAULT 0"),
            new SchemaColumnDefinition("remote_catalog_pending_prices", "remote_price_id", "TEXT NULL"),
            Column("remote_catalog_pending_prices", "remote_product_id", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''"),
            Column("remote_catalog_pending_prices", "type", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''"),
            Column("remote_catalog_pending_prices", "price", "INTEGER", true, "", "INTEGER NOT NULL DEFAULT 0"),
            Column("remote_catalog_pending_prices", "effective_at", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''"),
            new SchemaColumnDefinition("remote_catalog_pending_prices", "source", "TEXT NULL"),
            Column("remote_catalog_pending_prices", "created_at", "TEXT", true, "", "TEXT NOT NULL DEFAULT ''")
        };

        internal static string DependentLegacyColumnMaterial => string.Join(
            "\n",
            DependentLegacyColumns.Select(item => item.ToCanonicalMaterial()));

        internal static string RemoteOwnershipBackfillSql => @"
INSERT OR IGNORE INTO remote_catalog_price_ownership(
  remote_price_id,
  remote_product_id)
SELECT
  evidence.remote_price_id,
  MIN(evidence.remote_product_id)
FROM (
  SELECT
    TRIM(pending.remote_price_id) AS remote_price_id,
    TRIM(pending.remote_product_id) AS remote_product_id
  FROM remote_catalog_pending_prices pending
  WHERE TRIM(COALESCE(pending.remote_price_id, '')) <> ''
    AND TRIM(COALESCE(pending.remote_product_id, '')) <> ''
    AND NOT EXISTS (
      SELECT 1
      FROM product_price_history history
      WHERE TRIM(COALESCE(history.remote_price_id, '')) =
            TRIM(COALESCE(pending.remote_price_id, ''))
    )
) evidence
GROUP BY evidence.remote_price_id
HAVING COUNT(DISTINCT evidence.remote_product_id) = 1;";

        internal static void CreateDependentTables(SqliteConnection conn, SqliteTransaction tx)
        {
            conn.Execute(DependentSchemaSql, transaction: tx);
            foreach (var column in DependentLegacyColumns)
            {
                EnsureColumn(
                    conn,
                    tx,
                    column.TableName,
                    column.ColumnName,
                    column.AlterDefinition);
            }

            // Older pending rows already carry the remote product owner and can be
            // backfilled conservatively. History-only rows cannot: their barcode may
            // have been renamed/reused, so they remain unclaimed and fail closed.
            conn.Execute(RemoteOwnershipBackfillSql, transaction: tx);
        }

        internal const string MigrationSalesSchemaVersion = "pos-sales-ledger-v2";
        internal const string MigrationCatalogImportSchemaVersion = "pos-catalog-import-v1";

        internal static string OutboxNormalizationSql => @"
UPDATE sales_sync_outbox
SET schema_version = 'pos-sales-ledger-v2'
WHERE TRIM(COALESCE(schema_version, '')) = '';

UPDATE sales_sync_outbox
SET operation_type = CASE COALESCE((SELECT kind FROM sales WHERE sales.id = sales_sync_outbox.sale_id), 0)
      WHEN 1 THEN 'refund'
      WHEN 2 THEN 'void'
      ELSE 'sale'
    END
WHERE TRIM(COALESCE(operation_type, '')) = ''
   OR (
     operation_type = 'sale'
     AND COALESCE((SELECT kind FROM sales WHERE sales.id = sales_sync_outbox.sale_id), 0) IN (1, 2)
   );

UPDATE catalog_import_outbox
SET schema_version = 'pos-catalog-import-v1'
WHERE TRIM(COALESCE(schema_version, '')) = '';

UPDATE catalog_import_outbox
SET operation_type = 'catalog_import'
WHERE TRIM(COALESCE(operation_type, '')) = '';";

        internal static string OutboxContractMismatchSql => @"
UPDATE sales_sync_outbox
SET status = 'failed_blocked',
    last_error_code = 'legacy_contract_mismatch',
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked')
  AND NOT (
    status = 'failed_blocked'
    AND COALESCE(last_error_code, '') = 'legacy_contract_mismatch')
  AND (
    COALESCE(schema_version, '') <> 'pos-sales-ledger-v2'
    OR COALESCE(operation_type, '') <> CASE COALESCE(
         (SELECT kind FROM sales WHERE sales.id = sales_sync_outbox.sale_id), 0)
         WHEN 1 THEN 'refund'
         WHEN 2 THEN 'void'
         ELSE 'sale'
       END
  );

UPDATE sales
SET sync_status = 'blocked'
WHERE id IN (
  SELECT sale_id
  FROM sales_sync_outbox
  WHERE status = 'failed_blocked'
    AND last_error_code = 'legacy_contract_mismatch'
);

UPDATE catalog_import_outbox
SET status = 'failed_blocked',
    last_error_code = 'legacy_contract_mismatch',
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked')
  AND NOT (
    status = 'failed_blocked'
    AND COALESCE(last_error_code, '') = 'legacy_contract_mismatch')
  AND (
    COALESCE(schema_version, '') <> 'pos-catalog-import-v1'
    OR COALESCE(operation_type, '') <> 'catalog_import'
  );";

        internal static string CatalogAmbiguousOriginSql => @"
UPDATE catalog_import_outbox
SET status = 'failed_blocked',
    last_error_code = 'legacy_origin_ambiguous',
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked')
  AND TRIM(COALESCE(origin_shop_code, '')) = ''
  AND NOT (
    status = 'failed_blocked'
    AND COALESCE(last_error_code, '') IN (
      'legacy_origin_ambiguous',
      'legacy_contract_mismatch'));
";

        internal static string OutboxBackfillMaterial =>
            "sales-schema=" + MigrationSalesSchemaVersion + "\n" +
            "catalog-schema=" + MigrationCatalogImportSchemaVersion + "\n" +
            OutboxNormalizationSql + "\n" +
            OutboxContractMismatchSql + "\n" +
            CatalogAmbiguousOriginSql + "\n" +
            "origin-proof=sha256-utf8-exact;single-sale;batch-required;" +
            "schema-client-idempotency-kind-batch-match;normalized-shop-code;" +
            "payload-and-hash-immutable";

        internal static void BackfillLegacyOutboxBindings(SqliteConnection conn, SqliteTransaction tx)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            conn.Execute(OutboxNormalizationSql, transaction: tx);
            conn.Execute(OutboxContractMismatchSql, new { nowMs }, tx);
            var legacySales = conn.Query<LegacySalesOutboxRow>(@"
SELECT
  id AS Id,
  sale_id AS SaleId,
  client_sale_id AS ClientSaleId,
  client_batch_id AS ClientBatchId,
  idempotency_key AS IdempotencyKey,
  schema_version AS SchemaVersion,
  operation_type AS OperationType,
  payload_json AS PayloadJson,
  payload_hash AS PayloadHash
FROM sales_sync_outbox
WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked')
  AND TRIM(COALESCE(origin_shop_code, '')) = ''
  AND NOT (
    status = 'failed_blocked'
    AND COALESCE(last_error_code, '') IN (
      'legacy_origin_ambiguous',
      'legacy_contract_mismatch'));",
                    transaction: tx)
                .ToArray();

            foreach (var legacy in legacySales)
            {
                var provenShopCode = TryReadLegacySalesOriginShopCode(legacy);
                if (provenShopCode.Length > 0)
                {
                    conn.Execute(@"
UPDATE sales_sync_outbox
SET origin_shop_code = @provenShopCode,
    updated_at = CASE WHEN updated_at <= 0 THEN @nowMs ELSE updated_at END
WHERE id = @id
  AND TRIM(COALESCE(origin_shop_code, '')) = '';",
                        new { id = legacy.Id, nowMs, provenShopCode },
                        tx);
                    continue;
                }

                conn.Execute(@"
UPDATE sales_sync_outbox
SET status = 'failed_blocked',
    last_error_code = 'legacy_origin_ambiguous',
    last_error_at = @nowMs,
    updated_at = @nowMs
WHERE id = @id
  AND TRIM(COALESCE(origin_shop_code, '')) = '';

UPDATE sales
SET sync_status = 'blocked'
WHERE id = @saleId;",
                    new { id = legacy.Id, saleId = legacy.SaleId, nowMs },
                    tx);
            }

            conn.Execute(CatalogAmbiguousOriginSql, new { nowMs }, tx);
        }

        private static string TryReadLegacySalesOriginShopCode(LegacySalesOutboxRow row)
        {
            if (row == null ||
                string.IsNullOrWhiteSpace(row.PayloadJson) ||
                string.IsNullOrWhiteSpace(row.PayloadHash) ||
                !string.Equals(
                    PosSalesSyncRequestBuilder.Sha256Hex(row.PayloadJson),
                    row.PayloadHash.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PosSalesSyncRequest));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(row.PayloadJson)))
                {
                    var request = serializer.ReadObject(stream) as PosSalesSyncRequest;
                    var sale = request?.Sales != null && request.Sales.Length == 1
                        ? request.Sales[0]
                        : null;
                    var shopCode = OutboxShopBinding.NormalizeCode(request?.ShopCode);
                    if (request == null ||
                        sale == null ||
                        request.Batch == null ||
                        shopCode.Length == 0 ||
                        !string.Equals(request.SchemaVersion, row.SchemaVersion, StringComparison.Ordinal) ||
                        !string.Equals(sale.ClientSaleId, row.ClientSaleId, StringComparison.Ordinal) ||
                        !string.Equals(sale.IdempotencyKey, row.IdempotencyKey, StringComparison.Ordinal) ||
                        !string.Equals(sale.Kind, row.OperationType, StringComparison.Ordinal) ||
                        (!string.IsNullOrWhiteSpace(row.ClientBatchId) &&
                         !string.Equals(request.Batch.ClientBatchId, row.ClientBatchId, StringComparison.Ordinal)))
                    {
                        return string.Empty;
                    }

                    return shopCode;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private sealed class LegacySalesOutboxRow
        {
            public string ClientBatchId { get; set; }
            public string ClientSaleId { get; set; }
            public long Id { get; set; }
            public string IdempotencyKey { get; set; }
            public string OperationType { get; set; }
            public string PayloadHash { get; set; }
            public string PayloadJson { get; set; }
            public long SaleId { get; set; }
            public string SchemaVersion { get; set; }
        }

        internal static string CanonicalIndexSql => @"
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
CREATE INDEX IF NOT EXISTS idx_price_history_catalog_import_item
ON product_price_history(catalog_import_idempotency_key, catalog_import_client_item_id, type)
WHERE catalog_import_client_item_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_pending_remote_price_id
ON remote_catalog_pending_prices(remote_price_id) WHERE remote_price_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_pending_remote_price_fallback
ON remote_catalog_pending_prices(remote_product_id, type, effective_at, price, coalesce(source,'')) WHERE remote_price_id IS NULL;
CREATE INDEX IF NOT EXISTS idx_pending_remote_price_product ON remote_catalog_pending_prices(remote_product_id);
CREATE INDEX IF NOT EXISTS idx_remote_price_ownership_product ON remote_catalog_price_ownership(remote_product_id);
CREATE INDEX IF NOT EXISTS idx_remote_price_quarantine_remote_id ON remote_catalog_price_evidence_quarantine(remote_price_id);
CREATE INDEX IF NOT EXISTS idx_remote_product_refs_category ON remote_catalog_product_references(remote_category_id);
CREATE INDEX IF NOT EXISTS idx_remote_product_refs_supplier ON remote_catalog_product_references(remote_supplier_id);
CREATE INDEX IF NOT EXISTS idx_held_cart_lines_holdId ON held_cart_lines(holdId);
CREATE INDEX IF NOT EXISTS idx_security_events_ts ON security_events(ts);
CREATE INDEX IF NOT EXISTS idx_products_remote_product_id ON products(remote_product_id);
CREATE INDEX IF NOT EXISTS idx_products_active_barcode ON products(is_active, barcode);
CREATE INDEX IF NOT EXISTS idx_products_active_remote_product_id ON products(remote_product_id) WHERE COALESCE(is_active, 1) = 1;
CREATE UNIQUE INDEX IF NOT EXISTS idx_categories_remote_category_id ON categories(remote_category_id) WHERE remote_category_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_categories_active_name ON categories(is_active, name);
CREATE UNIQUE INDEX IF NOT EXISTS idx_suppliers_remote_supplier_id ON suppliers(remote_supplier_id) WHERE remote_supplier_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_suppliers_active_name ON suppliers(is_active, name);
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_remote_staff_id ON users(remote_staff_id) WHERE remote_staff_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_users_remote_shop_staff ON users(remote_shop_code, remote_staff_code);
";

        // Frozen whitelist used only while detecting PR-B ledgerless databases.
        // It is deliberately version-bound: future schema changes must append a
        // migration and must not broaden an already-published legacy fingerprint.
        internal static string PrBKnownSchemaSql =>
            CoreSchemaSql + @"
ALTER TABLE sales ADD COLUMN operator_id INTEGER NULL;
ALTER TABLE sales ADD COLUMN pdf_printed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sales ADD COLUMN sync_status TEXT NOT NULL DEFAULT 'pending';
" + DependentSchemaSql + "\n" + CanonicalIndexSql;

        // Frozen whitelist for the brief ledgerless schema published by PR #7.
        // Keep the PR-B whitelist immutable: migration 0007 owns this column.
        internal static string PostPr7LedgerlessKnownSchemaSql =>
            PrBKnownSchemaSql + "\n" + ReceiptShopSnapshotAlterSql;

        // Frozen whitelist for the schema first published by SYNC2. Earlier
        // whitelists remain unchanged so their migration fingerprints stay valid.
        internal static string PostSync2LedgerlessKnownSchemaSql =>
            PostPr7LedgerlessKnownSchemaSql + "\n" +
            OnlineSyncGenerationAlterSql + "\n" +
            OnlineSyncGenerationSchemaSql;

        internal static void EnsureIndexes(SqliteConnection conn, SqliteTransaction tx)
        {
            LogMigrationInfo("index creation phase");
            conn.Execute(CanonicalIndexSql, transaction: tx);
        }

        internal static string SecurityRoleSeedSql => @"
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('admin','Admin',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('manager','Manager',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('supervisor','Supervisore',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('cashier','Cassiere',1);
";

        internal static void SeedSecurity(SqliteConnection conn, SqliteTransaction tx)
        {
            conn.Execute(SecurityRoleSeedSql, transaction: tx);

            SeedRolePermissions(conn, tx);
            // Nessun utente admin seedato: il primo admin viene creato dal wizard FirstRunSetupDialog.
        }

        private static void SeedRolePermissions(SqliteConnection conn, SqliteTransaction tx)
        {
            var permissionSeeds = BuildRolePermissionSeeds();
            var roleIds = conn.Query<RoleSeedRow>(
                "SELECT code AS Code, id AS Id, is_system AS IsSystem FROM roles",
                transaction: tx).ToList();
            foreach (var role in roleIds)
            {
                if (!permissionSeeds.TryGetValue(role.Code, out var permissions))
                    permissions = Array.Empty<string>();
                foreach (var permission in permissions)
                {
                    conn.Execute(
                        "INSERT OR IGNORE INTO role_permissions(role_id, permission_code) VALUES(@rid, @code)",
                        new { rid = role.Id, code = permission },
                        tx);
                }
            }
        }

        internal static bool IsSecuritySeedSatisfied(
            SqliteConnection conn,
            SqliteTransaction tx)
        {
            var permissionSeeds = BuildRolePermissionSeeds();
            var roleIds = conn.Query<RoleSeedRow>(
                "SELECT code AS Code, id AS Id, is_system AS IsSystem FROM roles",
                transaction: tx).ToList();
            foreach (var seed in permissionSeeds)
            {
                var role = roleIds.FirstOrDefault(item =>
                    string.Equals(item.Code, seed.Key, StringComparison.Ordinal));
                if (role == null || role.IsSystem != 1)
                    return false;

                foreach (var permission in seed.Value)
                {
                    var count = conn.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM role_permissions
WHERE role_id = @roleId
  AND permission_code = @permission;",
                        new { roleId = role.Id, permission },
                        tx);
                    if (count != 1)
                        return false;
                }
            }

            return true;
        }

        internal static string SecuritySeedMaterial
        {
            get
            {
                var seeds = BuildRolePermissionSeeds();
                return SecurityRoleSeedSql + "\n" + string.Join(
                    "\n",
                    seeds
                        .OrderBy(item => item.Key, StringComparer.Ordinal)
                        .Select(item =>
                            item.Key + "=" + string.Join(
                                ",",
                                item.Value.OrderBy(value => value, StringComparer.Ordinal))));
            }
        }

        private static System.Collections.Generic.Dictionary<string, string[]> BuildRolePermissionSeeds()
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
            return new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "admin", adminPerms },
                { "manager", managerPerms },
                { "supervisor", supervisorPerms },
                { "cashier", cashierPerms }
            };
        }

        private static void EnsureColumn(SqliteConnection conn, SqliteTransaction tx, string table, string column, string ddl)
        {
            var info = conn.Query<TableInfoRow>("PRAGMA table_info(" + table + ");", transaction: tx).ToList();
            if (info.Any(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase)))
                return;
            conn.Execute("ALTER TABLE " + table + " ADD COLUMN " + column + " " + ddl + ";", transaction: tx);
            LogMigrationInfo("legacy column added: " + table + "." + column);
        }

        private static SchemaColumnDefinition Column(
            string table,
            string column,
            string declaredType,
            bool isNotNull,
            string defaultValue,
            string alterDefinition)
        {
            return new SchemaColumnDefinition(
                table,
                column,
                declaredType,
                isNotNull,
                defaultValue,
                alterDefinition);
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
                @"(?i)(session[_-]?token|device[_-]?token|trusted[_-]?device[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|apikey|token|pin|password|credential|pwd|db_password|database password)\s*[:=]\s*\S+",
                "$1=[redacted]");
            sanitized = Regex.Replace(
                sanitized,
                @"(?i)(""?(session[_-]?token|device[_-]?token|trusted[_-]?device[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|apikey|token|pin|password|credential|pwd|db_password|database password)""?\s*:\s*"")[^""]+("")",
                "$1[redacted]$3");
            sanitized = Regex.Replace(sanitized, @"(?i)(Authorization\s*:\s*Bearer\s+)[A-Za-z0-9._~+/-]+=*", "$1[redacted]");
            sanitized = Regex.Replace(sanitized, @"(?i)mcpos_(device|session)_[A-Za-z0-9_-]+", "mcpos_$1_[redacted]");
            sanitized = Regex.Replace(sanitized, @"(?i)\b(?:sk[-_]|sb_secret_)[A-Za-z0-9_-]{12,}\b", "[secret-redacted]");
            sanitized = Regex.Replace(sanitized, @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", "[jwt-redacted]");
            sanitized = Regex.Replace(sanitized, @"(?is)-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----.*?(?:-----END (?:RSA |OPENSSH |EC )?PRIVATE KEY-----|\z)", "[private-key-redacted]");
            sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\[^\r\n|]+", "[path]");
            sanitized = Regex.Replace(sanitized, @"/(?:Users|private|tmp|var)/[^\r\n|]+", "[path]");
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
            public int IsSystem { get; set; }
        }
    }
}
