-- include: legacy_post_pr7_unversioned.sql
-- Sanitized ledgerless schema immediately after SYNC2 and before PERF2-A.
ALTER TABLE sales_sync_outbox ADD COLUMN claim_generation_id TEXT NULL;
ALTER TABLE sales_sync_outbox ADD COLUMN claim_token TEXT NULL;
ALTER TABLE catalog_import_outbox ADD COLUMN claim_generation_id TEXT NULL;
ALTER TABLE catalog_import_outbox ADD COLUMN claim_token TEXT NULL;

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

UPDATE fixture_probe
SET fixture_name = 'legacy_post_sync2_unversioned',
    fixture_value = 'preserve-post-sync2-generation'
WHERE fixture_name = 'legacy_post_pr7_unversioned';
