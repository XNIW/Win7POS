-- include: legacy_pre_shop_binding.sql
-- Sanitized shop-bound state before catalog exactness evidence tables.
ALTER TABLE sales_sync_outbox ADD COLUMN origin_shop_id TEXT NULL;
ALTER TABLE sales_sync_outbox ADD COLUMN origin_shop_code TEXT NULL;
ALTER TABLE catalog_import_outbox ADD COLUMN origin_shop_id TEXT NULL;
ALTER TABLE catalog_import_outbox ADD COLUMN origin_shop_code TEXT NULL;
UPDATE sales_sync_outbox
SET origin_shop_id='fixture-shop-id', origin_shop_code='FIXTURE-SHOP';
UPDATE catalog_import_outbox
SET origin_shop_id='fixture-shop-id', origin_shop_code='FIXTURE-SHOP';
UPDATE catalog_import_outbox
SET status='failed_blocked',
    last_error_code='legacy_contract_mismatch',
    last_error_at=1700000000002,
    updated_at=1700000000002
WHERE client_import_id='fixture-import-contract-mismatch';

CREATE TABLE remote_catalog_product_references (
  remote_product_id TEXT PRIMARY KEY NOT NULL,
  remote_category_id TEXT NULL,
  remote_supplier_id TEXT NULL
);
INSERT INTO remote_catalog_product_references(
  remote_product_id, remote_category_id, remote_supplier_id)
VALUES('fixture-product-id', 'fixture-category', 'fixture-supplier');
INSERT INTO remote_catalog_pending_prices(
  remote_price_id, remote_product_id, type, price, effective_at, source, created_at)
VALUES(
  'fixture-price-id', 'fixture-product-id', 'retail', 1234,
  '2026-01-01T00:00:00Z', 'fixture', '2026-01-01T00:00:00Z');
INSERT INTO fixture_probe(fixture_name, fixture_value)
VALUES('legacy_pre_catalog_exactness', 'preserve-pre-exactness');
