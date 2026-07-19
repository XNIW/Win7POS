-- include: legacy_initial_minimal.sql
-- Sanitized state immediately before refund/void support.
CREATE TABLE app_settings (
  key   TEXT PRIMARY KEY NOT NULL,
  value TEXT NOT NULL
);
INSERT INTO app_settings(key, value) VALUES('fixture.pre_refund', 'preserve-setting');
INSERT INTO fixture_probe(fixture_name, fixture_value)
VALUES('legacy_pre_refund_void', 'preserve-pre-refund');
