using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    internal static class HistoricalSalesSchema
    {
        internal const string Sql = @"
CREATE TABLE historical_import_dataset (
 singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
 dataset_id TEXT NOT NULL UNIQUE,
 data_revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(data_revision)='integer' AND data_revision>=0)
);
INSERT INTO historical_import_dataset(singleton_id,dataset_id,data_revision) VALUES(1,lower(hex(randomblob(16))),0);
CREATE TABLE historical_merchant_scope (
 id TEXT PRIMARY KEY NOT NULL, shop_id TEXT NOT NULL, provider TEXT NOT NULL,
 evidence_reference TEXT NOT NULL, created_at INTEGER NOT NULL
);
CREATE TABLE historical_merchant_binding (
 id TEXT PRIMARY KEY NOT NULL, scope_id TEXT NOT NULL REFERENCES historical_merchant_scope(id),
 shop_id TEXT NOT NULL, provider TEXT NOT NULL, alias_version TEXT NOT NULL, alias_key TEXT NOT NULL,
 evidence_reference TEXT NOT NULL, used_at INTEGER NULL, is_active INTEGER NOT NULL CHECK(is_active IN(0,1))
);
CREATE UNIQUE INDEX ux_historical_merchant_alias ON historical_merchant_binding(shop_id,provider,alias_version,alias_key) WHERE is_active=1;
CREATE TRIGGER historical_merchant_binding_immutable BEFORE UPDATE ON historical_merchant_binding
 WHEN OLD.used_at IS NOT NULL AND (NEW.scope_id<>OLD.scope_id OR NEW.shop_id<>OLD.shop_id OR NEW.provider<>OLD.provider OR NEW.alias_version<>OLD.alias_version OR NEW.alias_key<>OLD.alias_key)
 BEGIN SELECT RAISE(ABORT,'used_merchant_binding_immutable'); END;
CREATE TRIGGER historical_merchant_alias_used BEFORE INSERT ON historical_merchant_binding
 WHEN EXISTS(SELECT 1 FROM historical_merchant_binding b WHERE b.shop_id=NEW.shop_id AND b.provider=NEW.provider AND b.alias_version=NEW.alias_version AND b.alias_key=NEW.alias_key AND b.used_at IS NOT NULL AND b.scope_id<>NEW.scope_id)
 BEGIN SELECT RAISE(ABORT,'used_merchant_alias_immutable'); END;
CREATE TRIGGER historical_merchant_scope_immutable BEFORE UPDATE ON historical_merchant_scope BEGIN SELECT RAISE(ABORT,'merchant_scope_immutable'); END;
CREATE TABLE historical_import_operation (
 operation_id TEXT PRIMARY KEY NOT NULL, operation_kind TEXT NOT NULL CHECK(operation_kind IN('Apply','Undo')),
 shop_id TEXT NOT NULL, dataset_id TEXT NOT NULL, request_fingerprint TEXT NOT NULL,
 batch_id TEXT NOT NULL, operator_id INTEGER NOT NULL, created_at INTEGER NOT NULL,
 receipt_json TEXT NOT NULL, inserted_count INTEGER NOT NULL, duplicate_count INTEGER NOT NULL,
 signed_amount_clp INTEGER NOT NULL,
 FOREIGN KEY(batch_id) REFERENCES historical_import_batch(id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TRIGGER historical_operation_immutable_update BEFORE UPDATE ON historical_import_operation BEGIN SELECT RAISE(ABORT,'terminal_operation_immutable'); END;
CREATE TRIGGER historical_operation_immutable_delete BEFORE DELETE ON historical_import_operation BEGIN SELECT RAISE(ABORT,'terminal_operation_immutable'); END;
CREATE TABLE historical_import_batch (
 id TEXT PRIMARY KEY NOT NULL, shop_id TEXT NOT NULL, dataset_id TEXT NOT NULL,
 apply_operation_id TEXT NOT NULL UNIQUE REFERENCES historical_import_operation(operation_id) DEFERRABLE INITIALLY DEFERRED,
 state TEXT NOT NULL CHECK(state IN('Active','Undone')), revision INTEGER NOT NULL DEFAULT 1,
 created_at INTEGER NOT NULL, undone_at INTEGER NULL, undo_operation_id TEXT NULL REFERENCES historical_import_operation(operation_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE historical_import_file (
 id TEXT PRIMARY KEY NOT NULL, batch_id TEXT NOT NULL REFERENCES historical_import_batch(id),
 source_sha256 TEXT NOT NULL, profile TEXT NOT NULL, parser_version TEXT NOT NULL,
 temporal_policy TEXT NOT NULL, evidence_reference TEXT NULL, source_rows INTEGER NOT NULL
);
CREATE TABLE historical_transactions (
 id INTEGER PRIMARY KEY AUTOINCREMENT, batch_id TEXT NOT NULL REFERENCES historical_import_batch(id),
 file_id TEXT NOT NULL REFERENCES historical_import_file(id), shop_id TEXT NOT NULL, provider TEXT NOT NULL,
 merchant_scope_id TEXT NOT NULL REFERENCES historical_merchant_scope(id), binding_id TEXT NOT NULL REFERENCES historical_merchant_binding(id),
 external_namespace TEXT NOT NULL, external_id TEXT NOT NULL, business_ticks INTEGER NOT NULL,
 timestamp_raw TEXT NOT NULL, offset_minutes INTEGER NULL, occurred_at_utc_ms INTEGER NULL,
 movement_kind TEXT NOT NULL, state TEXT NOT NULL, settlement_state TEXT NULL, signed_amount_clp INTEGER NOT NULL,
 signed_fee_clp INTEGER NULL, signed_fee_vat_clp INTEGER NULL, signed_net_clp INTEGER NULL,
 original_cashier_id INTEGER NULL, card_subtype TEXT NOT NULL, source_sheet TEXT NOT NULL, source_row INTEGER NOT NULL,
 is_active INTEGER NOT NULL CHECK(is_active IN(0,1)), supersedes_id INTEGER NULL REFERENCES historical_transactions(id)
);
CREATE TRIGGER historical_movement_immutable BEFORE UPDATE ON historical_transactions
 WHEN OLD.is_active=0 OR NEW.is_active<>0 OR NEW.id IS NOT OLD.id OR NEW.batch_id IS NOT OLD.batch_id OR NEW.file_id IS NOT OLD.file_id OR NEW.shop_id IS NOT OLD.shop_id OR NEW.provider IS NOT OLD.provider OR NEW.merchant_scope_id IS NOT OLD.merchant_scope_id OR NEW.binding_id IS NOT OLD.binding_id OR NEW.external_namespace IS NOT OLD.external_namespace OR NEW.external_id IS NOT OLD.external_id OR NEW.business_ticks IS NOT OLD.business_ticks OR NEW.timestamp_raw IS NOT OLD.timestamp_raw OR NEW.offset_minutes IS NOT OLD.offset_minutes OR NEW.occurred_at_utc_ms IS NOT OLD.occurred_at_utc_ms OR NEW.movement_kind IS NOT OLD.movement_kind OR NEW.state IS NOT OLD.state OR NEW.settlement_state IS NOT OLD.settlement_state OR NEW.signed_amount_clp IS NOT OLD.signed_amount_clp OR NEW.signed_fee_clp IS NOT OLD.signed_fee_clp OR NEW.signed_fee_vat_clp IS NOT OLD.signed_fee_vat_clp OR NEW.signed_net_clp IS NOT OLD.signed_net_clp OR NEW.original_cashier_id IS NOT OLD.original_cashier_id OR NEW.card_subtype IS NOT OLD.card_subtype OR NEW.source_sheet IS NOT OLD.source_sheet OR NEW.source_row IS NOT OLD.source_row OR NEW.supersedes_id IS NOT OLD.supersedes_id
 BEGIN SELECT RAISE(ABORT,'historical_movement_immutable'); END;
CREATE UNIQUE INDEX ux_historical_active_identity ON historical_transactions(shop_id,provider,merchant_scope_id,external_namespace,external_id) WHERE is_active=1;
CREATE INDEX ix_historical_report ON historical_transactions(shop_id,is_active,business_ticks DESC,id DESC);
CREATE INDEX ix_historical_batch ON historical_transactions(batch_id,is_active,id);
CREATE TABLE historical_import_observation (
 id INTEGER PRIMARY KEY AUTOINCREMENT, batch_id TEXT NOT NULL REFERENCES historical_import_batch(id),
 file_id TEXT NOT NULL REFERENCES historical_import_file(id), source_row INTEGER NOT NULL,
 observation_kind TEXT NOT NULL, code TEXT NOT NULL, existing_transaction_id INTEGER NULL REFERENCES historical_transactions(id)
);
CREATE TABLE sales_report_native_ownership (
 sale_id INTEGER PRIMARY KEY REFERENCES sales(id), state TEXT NOT NULL CHECK(state IN('Verified','Unknown','CodeOnly','Conflict','Dirty')),
 shop_id TEXT NULL, source_fingerprint TEXT NOT NULL, policy_hash TEXT NOT NULL
);
CREATE TABLE sales_report_native_time_policy (
 policy_hash TEXT NOT NULL, shop_id TEXT NOT NULL, policy_version TEXT NOT NULL,
 evidence_reference TEXT NOT NULL, policy_json TEXT NOT NULL, is_active INTEGER NOT NULL CHECK(is_active IN(0,1)),
 PRIMARY KEY(shop_id,policy_hash)
);
CREATE UNIQUE INDEX ux_report_native_time_policy_active ON sales_report_native_time_policy(shop_id) WHERE is_active=1;
CREATE TRIGGER report_policy_immutable BEFORE UPDATE ON sales_report_native_time_policy
 WHEN NEW.policy_hash<>OLD.policy_hash OR NEW.shop_id<>OLD.shop_id OR NEW.policy_json<>OLD.policy_json OR NEW.policy_version<>OLD.policy_version
 BEGIN SELECT RAISE(ABORT,'report_policy_immutable'); END;
CREATE TRIGGER report_policy_insert_revision AFTER INSERT ON sales_report_native_time_policy BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER report_policy_update_revision AFTER UPDATE ON sales_report_native_time_policy BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER report_policy_delete_revision AFTER DELETE ON sales_report_native_time_policy BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER historical_tx_insert_revision AFTER INSERT ON historical_transactions WHEN NEW.is_active=1 BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER historical_tx_update_revision AFTER UPDATE ON historical_transactions BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER historical_tx_delete_revision AFTER DELETE ON historical_transactions BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER report_ownership_insert_revision AFTER INSERT ON sales_report_native_ownership BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER report_ownership_update_revision AFTER UPDATE ON sales_report_native_ownership BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
CREATE TRIGGER report_ownership_delete_revision AFTER DELETE ON sales_report_native_ownership BEGIN
 UPDATE historical_import_dataset SET data_revision=data_revision+1 WHERE singleton_id=1; END;
";
        internal static void Apply(SqliteConnection connection, SqliteTransaction transaction) => connection.Execute(Sql, transaction: transaction);
        internal static bool IsSatisfied(LegacySchemaDetector detector) => detector.HasCanonicalTableDefinitions(Sql,
            "historical_import_dataset", "historical_merchant_scope", "historical_merchant_binding", "historical_import_operation",
            "historical_import_batch", "historical_import_file", "historical_transactions", "historical_import_observation",
            "sales_report_native_ownership", "sales_report_native_time_policy") && detector.HasExactHistoricalDefinitions();
    }
}
