-- include: legacy_pre_outbox.sql
-- Sanitized state with outboxes but before authoritative shop binding.
CREATE TABLE local_stock_movements (
  id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  movement_key TEXT NOT NULL UNIQUE,
  sale_id INTEGER NOT NULL,
  sale_line_id INTEGER NULL,
  barcode TEXT NOT NULL,
  quantity_delta INTEGER NOT NULL,
  movement_kind TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE TABLE sales_sync_outbox (
  id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  sale_id INTEGER NOT NULL UNIQUE,
  client_sale_id TEXT NOT NULL UNIQUE,
  client_batch_id TEXT NULL,
  idempotency_key TEXT NOT NULL UNIQUE,
  schema_version TEXT NOT NULL DEFAULT 'pos-sales-ledger-v2',
  operation_type TEXT NOT NULL DEFAULT 'sale',
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
CREATE TABLE catalog_import_outbox (
  id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  client_import_id TEXT NOT NULL,
  idempotency_key TEXT NOT NULL,
  schema_version TEXT NOT NULL DEFAULT 'pos-catalog-import-v1',
  operation_type TEXT NOT NULL DEFAULT 'catalog_import',
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
CREATE TABLE remote_catalog_pending_prices (
  id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  remote_price_id TEXT NULL,
  remote_product_id TEXT NOT NULL,
  type TEXT NOT NULL,
  price INTEGER NOT NULL,
  effective_at TEXT NOT NULL,
  source TEXT NULL,
  created_at TEXT NOT NULL
);

UPDATE sales
SET client_sale_id='fixture-sale-client', sync_status='pending'
WHERE id=1;
INSERT INTO local_stock_movements(
  movement_key, sale_id, sale_line_id, barcode, quantity_delta, movement_kind, created_at)
VALUES('fixture-movement', 1, 1, 'FIXTURE-0001', -1, 'sale', 1700000000000);
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, client_batch_id, idempotency_key, schema_version,
  operation_type, payload_json, payload_hash, status, created_at, updated_at)
VALUES(
  1, 'fixture-sale-client', 'fixture-batch', 'fixture-sale-idempotency',
  'pos-sales-ledger-v2', 'sale', '{}', 'synthetic-invalid-hash',
  'pending', 1700000000000, 1700000000000);
INSERT INTO catalog_import_outbox(
  client_import_id, idempotency_key, schema_version, operation_type, source,
  payload_json, payload_hash, status, created_at, updated_at)
VALUES(
  'fixture-import', 'fixture-import-idempotency', 'pos-catalog-import-v1',
  'catalog_import', 'supplier_excel', '{}', 'synthetic-invalid-hash',
  'pending', 1700000000000, 1700000000000);
INSERT INTO catalog_import_outbox(
  client_import_id, idempotency_key, schema_version, operation_type, source,
  payload_json, payload_hash, status, created_at, updated_at)
VALUES(
  'fixture-import-contract-mismatch', 'fixture-import-contract-mismatch-idempotency',
  'legacy-catalog-v0', 'catalog_import', 'supplier_excel', '{}',
  'synthetic-invalid-hash-contract-mismatch', 'pending',
  1700000000001, 1700000000001);
INSERT INTO fixture_probe(fixture_name, fixture_value)
VALUES('legacy_pre_shop_binding', 'preserve-pre-shop');
