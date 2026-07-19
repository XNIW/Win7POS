-- include: legacy_pre_refund_void.sql
-- Sanitized state immediately before durable sync outboxes.
ALTER TABLE sales ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sales ADD COLUMN related_sale_id INTEGER NULL;
ALTER TABLE sales ADD COLUMN voided_by_sale_id INTEGER NULL;
ALTER TABLE sales ADD COLUMN voided_at INTEGER NULL;
ALTER TABLE sales ADD COLUMN reason TEXT NULL;
ALTER TABLE sales ADD COLUMN operator_id INTEGER NULL;
ALTER TABLE sales ADD COLUMN pdf_printed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sales ADD COLUMN client_sale_id TEXT NULL;
ALTER TABLE sales ADD COLUMN sync_status TEXT NOT NULL DEFAULT 'pending';
ALTER TABLE sale_lines ADD COLUMN related_original_line_id INTEGER NULL;
ALTER TABLE products ADD COLUMN remote_product_id TEXT NULL;
ALTER TABLE products ADD COLUMN remote_deleted_at TEXT NULL;
ALTER TABLE products ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1;

CREATE TABLE audit_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts INTEGER NOT NULL,
  action TEXT NOT NULL,
  details TEXT NOT NULL
);
CREATE TABLE suppliers (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  remote_supplier_id TEXT NULL,
  remote_updated_at TEXT NULL,
  remote_deleted_at TEXT NULL,
  is_active INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE categories (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  remote_category_id TEXT NULL,
  remote_updated_at TEXT NULL,
  remote_deleted_at TEXT NULL,
  is_active INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE product_meta (
  barcode TEXT PRIMARY KEY,
  article_code TEXT NULL,
  name2 TEXT NULL,
  purchase_price INTEGER NOT NULL DEFAULT 0,
  purchase_old INTEGER NOT NULL DEFAULT 0,
  retail_old INTEGER NOT NULL DEFAULT 0,
  supplier_id INTEGER NULL,
  supplier_name TEXT NULL,
  category_id INTEGER NULL,
  category_name TEXT NULL,
  stock_qty INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE product_price_history (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode TEXT NOT NULL,
  timestamp TEXT NOT NULL,
  type TEXT NOT NULL,
  old_price INTEGER NULL,
  new_price INTEGER NOT NULL,
  source TEXT NULL,
  remote_price_id TEXT NULL,
  catalog_import_client_item_id TEXT NULL,
  catalog_import_idempotency_key TEXT NULL
);
CREATE TABLE held_carts (
  holdId TEXT PRIMARY KEY NOT NULL,
  createdAtMs INTEGER NOT NULL,
  totalMinor INTEGER NOT NULL
);
CREATE TABLE held_cart_lines (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  holdId TEXT NOT NULL,
  barcode TEXT NOT NULL,
  name TEXT NOT NULL,
  unitPrice INTEGER NOT NULL,
  qty INTEGER NOT NULL,
  FOREIGN KEY(holdId) REFERENCES held_carts(holdId) ON DELETE CASCADE
);
CREATE TABLE roles (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  is_system INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE users (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  username TEXT NOT NULL UNIQUE,
  display_name TEXT NOT NULL,
  pin_hash TEXT NOT NULL,
  pin_salt TEXT NOT NULL,
  role_id INTEGER NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1,
  require_pin_change INTEGER NOT NULL DEFAULT 0,
  max_discount_percent INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  failed_attempts INTEGER NOT NULL DEFAULT 0,
  lockout_until INTEGER NULL,
  remote_staff_id TEXT NULL,
  remote_staff_code TEXT NULL,
  remote_shop_id TEXT NULL,
  remote_shop_code TEXT NULL,
  remote_role_key TEXT NULL,
  remote_credential_version INTEGER NULL,
  remote_synced_at INTEGER NULL,
  FOREIGN KEY(role_id) REFERENCES roles(id)
);
CREATE TABLE role_permissions (
  role_id INTEGER NOT NULL,
  permission_code TEXT NOT NULL,
  PRIMARY KEY(role_id, permission_code),
  FOREIGN KEY(role_id) REFERENCES roles(id) ON DELETE CASCADE
);
CREATE TABLE security_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts INTEGER NOT NULL,
  user_id INTEGER NULL,
  event_type TEXT NOT NULL,
  details TEXT NOT NULL
);

INSERT INTO suppliers(id, name, remote_supplier_id, is_active)
VALUES(1, 'Sanitized supplier', 'fixture-supplier', 1);
INSERT INTO categories(id, name, remote_category_id, is_active)
VALUES(1, 'Sanitized category', 'fixture-category', 1);
INSERT INTO product_meta(barcode, article_code, supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES('FIXTURE-0001', 'ARTICLE-1', 1, 'Sanitized supplier', 1, 'Sanitized category', 7);
INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES('FIXTURE-0001', '2026-01-01T00:00:00Z', 'retail', 1200, 1234, 'fixture');
INSERT INTO held_carts(holdId, createdAtMs, totalMinor)
VALUES('fixture-hold', 1700000000000, 1234);
INSERT INTO held_cart_lines(holdId, barcode, name, unitPrice, qty)
VALUES('fixture-hold', 'FIXTURE-0001', 'Sanitized legacy product', 1234, 1);
INSERT INTO roles(id, code, name, is_system)
VALUES(99, 'fixture_role', 'Fixture role', 0);
INSERT INTO users(username, display_name, pin_hash, pin_salt, role_id, created_at, updated_at)
VALUES('fixture_user', 'Fixture User', 'synthetic-hash', 'synthetic-salt', 99, 1700000000000, 1700000000000);
INSERT INTO fixture_probe(fixture_name, fixture_value)
VALUES('legacy_pre_outbox', 'preserve-pre-outbox');
