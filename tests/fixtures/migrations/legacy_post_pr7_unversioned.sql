-- include: legacy_pre_catalog_exactness.sql
-- Sanitized schema immediately preceding PR-B (current main without a ledger).
ALTER TABLE users ADD COLUMN last_login_at INTEGER NULL;

CREATE TABLE remote_catalog_price_ownership (
  remote_price_id TEXT PRIMARY KEY NOT NULL,
  remote_product_id TEXT NOT NULL
);
CREATE TABLE remote_catalog_price_evidence_quarantine (
  id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  evidence_kind TEXT NOT NULL,
  evidence_row_id INTEGER NOT NULL,
  remote_price_id TEXT NOT NULL,
  remote_product_id TEXT NULL,
  barcode TEXT NULL,
  effective_at TEXT NOT NULL,
  type TEXT NOT NULL,
  old_price INTEGER NULL,
  price INTEGER NOT NULL,
  source TEXT NULL,
  catalog_import_client_item_id TEXT NULL,
  catalog_import_idempotency_key TEXT NULL,
  original_created_at TEXT NULL,
  authoritative_remote_product_id TEXT NOT NULL,
  reason TEXT NOT NULL,
  quarantined_at TEXT NOT NULL,
  UNIQUE(evidence_kind, evidence_row_id, remote_price_id)
);
INSERT INTO remote_catalog_price_ownership(remote_price_id, remote_product_id)
VALUES('fixture-price-id', 'fixture-product-id');

CREATE INDEX idx_sale_lines_saleId ON sale_lines(saleId);
CREATE INDEX idx_sale_lines_barcode ON sale_lines(barcode);
CREATE INDEX idx_sales_createdAt ON sales(createdAt);
CREATE INDEX idx_sales_client_sale_id ON sales(client_sale_id);
CREATE UNIQUE INDEX idx_sales_client_sale_id_unique ON sales(client_sale_id) WHERE client_sale_id IS NOT NULL;
CREATE INDEX idx_sales_sync_status ON sales(sync_status, createdAt);
CREATE INDEX idx_local_stock_movements_sale ON local_stock_movements(sale_id);
CREATE INDEX idx_local_stock_movements_barcode ON local_stock_movements(barcode);
CREATE INDEX idx_sales_sync_outbox_status_next ON sales_sync_outbox(status, next_retry_at, id);
CREATE INDEX idx_sales_sync_outbox_sale ON sales_sync_outbox(sale_id);
CREATE INDEX idx_sales_sync_outbox_last_attempt ON sales_sync_outbox(last_attempt_at);
CREATE UNIQUE INDEX idx_catalog_import_outbox_client_import ON catalog_import_outbox(client_import_id);
CREATE UNIQUE INDEX idx_catalog_import_outbox_idempotency ON catalog_import_outbox(idempotency_key);
CREATE INDEX idx_catalog_import_outbox_status_next ON catalog_import_outbox(status, next_retry_at, id);
CREATE INDEX idx_catalog_import_outbox_last_attempt ON catalog_import_outbox(last_attempt_at);
CREATE INDEX idx_audit_log_ts ON audit_log(ts);
CREATE UNIQUE INDEX idx_price_history_unique
ON product_price_history(barcode, timestamp, type, new_price, coalesce(source,''));
CREATE UNIQUE INDEX idx_price_history_remote_price_id
ON product_price_history(remote_price_id) WHERE remote_price_id IS NOT NULL;
CREATE INDEX idx_price_history_catalog_import_item
ON product_price_history(catalog_import_idempotency_key, catalog_import_client_item_id, type)
WHERE catalog_import_client_item_id IS NOT NULL;
CREATE UNIQUE INDEX idx_pending_remote_price_id
ON remote_catalog_pending_prices(remote_price_id) WHERE remote_price_id IS NOT NULL;
CREATE UNIQUE INDEX idx_pending_remote_price_fallback
ON remote_catalog_pending_prices(remote_product_id, type, effective_at, price, coalesce(source,''))
WHERE remote_price_id IS NULL;
CREATE INDEX idx_pending_remote_price_product ON remote_catalog_pending_prices(remote_product_id);
CREATE INDEX idx_remote_price_ownership_product ON remote_catalog_price_ownership(remote_product_id);
CREATE INDEX idx_remote_price_quarantine_remote_id ON remote_catalog_price_evidence_quarantine(remote_price_id);
CREATE INDEX idx_remote_product_refs_category ON remote_catalog_product_references(remote_category_id);
CREATE INDEX idx_remote_product_refs_supplier ON remote_catalog_product_references(remote_supplier_id);
CREATE INDEX idx_held_cart_lines_holdId ON held_cart_lines(holdId);
CREATE INDEX idx_security_events_ts ON security_events(ts);
CREATE INDEX idx_products_remote_product_id ON products(remote_product_id);
CREATE INDEX idx_products_active_barcode ON products(is_active, barcode);
CREATE INDEX idx_products_active_remote_product_id ON products(remote_product_id) WHERE COALESCE(is_active, 1) = 1;
CREATE UNIQUE INDEX idx_categories_remote_category_id ON categories(remote_category_id) WHERE remote_category_id IS NOT NULL;
CREATE INDEX idx_categories_active_name ON categories(is_active, name);
CREATE UNIQUE INDEX idx_suppliers_remote_supplier_id ON suppliers(remote_supplier_id) WHERE remote_supplier_id IS NOT NULL;
CREATE INDEX idx_suppliers_active_name ON suppliers(is_active, name);
CREATE UNIQUE INDEX idx_users_remote_staff_id ON users(remote_staff_id) WHERE remote_staff_id IS NOT NULL;
CREATE INDEX idx_users_remote_shop_staff ON users(remote_shop_code, remote_staff_code);

INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('admin','Admin',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('manager','Manager',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('supervisor','Supervisor',1);
INSERT OR IGNORE INTO roles(code, name, is_system) VALUES('cashier','Cashier',1);

WITH required(permission_code) AS (VALUES
 ('pos.sell'),('pos.pay'),('pos.suspend_cart'),('pos.recover_cart'),
 ('pos.discount'),('pos.discount_over_limit'),('pos.refund'),('pos.void_sale'),('pos.reprint_receipt'),
 ('catalog.view'),('catalog.edit'),('catalog.import'),('catalog.price_edit'),
 ('register.view'),('register.view_all'),('daily_close.view'),('daily_close.run'),('daily_close.print'),
 ('settings.shop'),('settings.printer'),('db.backup'),('db.restore'),('db.maintenance'),
 ('users.manage'),('roles.manage'),('security.override'))
INSERT INTO role_permissions(role_id, permission_code)
SELECT roles.id, required.permission_code FROM roles, required WHERE roles.code='admin';

WITH required(permission_code) AS (VALUES
 ('pos.sell'),('pos.pay'),('pos.suspend_cart'),('pos.recover_cart'),
 ('pos.discount'),('pos.discount_over_limit'),('pos.refund'),('pos.void_sale'),('pos.reprint_receipt'),
 ('catalog.view'),('catalog.edit'),('catalog.price_edit'),
 ('register.view'),('register.view_all'),('daily_close.view'),('daily_close.run'),('daily_close.print'),
 ('settings.shop'),('settings.printer'),('db.backup'))
INSERT INTO role_permissions(role_id, permission_code)
SELECT roles.id, required.permission_code FROM roles, required WHERE roles.code='manager';

WITH required(permission_code) AS (VALUES
 ('pos.sell'),('pos.pay'),('pos.suspend_cart'),('pos.recover_cart'),
 ('pos.discount'),('pos.refund'),('pos.void_sale'),('pos.reprint_receipt'),
 ('catalog.view'),('register.view'),('register.view_all'),
 ('daily_close.view'),('daily_close.run'),('daily_close.print'),('settings.printer'))
INSERT INTO role_permissions(role_id, permission_code)
SELECT roles.id, required.permission_code FROM roles, required WHERE roles.code='supervisor';

WITH required(permission_code) AS (VALUES
 ('pos.sell'),('pos.pay'),('pos.suspend_cart'),('pos.recover_cart'),
 ('pos.reprint_receipt'),('catalog.view'),('register.view'))
INSERT INTO role_permissions(role_id, permission_code)
SELECT roles.id, required.permission_code FROM roles, required WHERE roles.code='cashier';

INSERT INTO fixture_probe(fixture_name, fixture_value)
VALUES('legacy_current_main_unversioned', 'preserve-current-main');

ALTER TABLE sales ADD COLUMN receipt_shop_snapshot TEXT NULL;

INSERT INTO sales(
  code, createdAt, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES(
  'POST-PR7-SNAPSHOT', 1784419200, 1990, 1990, 0, 0,
  '{"shopName":"Negozio QA Ñ","address":"Via Unicode 7"}');

UPDATE fixture_probe
SET fixture_name = 'legacy_post_pr7_unversioned',
    fixture_value = 'preserve-post-pr7-snapshot'
WHERE fixture_name = 'legacy_current_main_unversioned';
