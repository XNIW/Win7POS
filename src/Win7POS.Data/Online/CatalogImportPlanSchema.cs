using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Online
{
    internal static class CatalogImportPlanSchema
    {
        internal const string Sql = @"
CREATE TABLE IF NOT EXISTS catalog_import_correction_proof (
 proof_hash TEXT PRIMARY KEY NOT NULL,
 original_id INTEGER NOT NULL REFERENCES catalog_import_outbox(id) ON DELETE RESTRICT,
 proof_json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS catalog_import_prepared_plan (
 original_id INTEGER PRIMARY KEY REFERENCES catalog_import_outbox(id) ON DELETE RESTRICT,
 target_id INTEGER NOT NULL REFERENCES catalog_import_outbox(id) ON DELETE RESTRICT,
 original_hash TEXT NOT NULL,
 target_hash TEXT NOT NULL,
 rows_hash TEXT NOT NULL,
 rows_json TEXT NOT NULL,
 operation_created_at TEXT NOT NULL,
 dispatch_started_at INTEGER NULL,
 plan_json TEXT NOT NULL,
 plan_hash TEXT NOT NULL,
 plan_document_json TEXT NULL,
 plan_document_hash TEXT NULL,
 remote_plan_json TEXT NULL,
 remote_plan_hash TEXT NULL,
 created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS catalog_import_recovery_supersession (
 predecessor_plan_id TEXT PRIMARY KEY NOT NULL,
 original_id INTEGER NOT NULL REFERENCES catalog_import_outbox(id) ON DELETE RESTRICT,
 original_hash TEXT NOT NULL,
 archive_json TEXT NOT NULL,
 archive_hash TEXT NOT NULL,
 settlement_json TEXT NOT NULL,
 settlement_hash TEXT NOT NULL,
 finalized_at INTEGER NULL,
 successor_plan_id TEXT NULL,
 resolved_at INTEGER NULL,
 created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS catalog_import_plan (
 plan_id TEXT PRIMARY KEY NOT NULL,
 original_id INTEGER NULL REFERENCES catalog_import_outbox(id) ON DELETE RESTRICT,
 total_rows INTEGER NOT NULL CHECK(total_rows>0),
 total_parts INTEGER NOT NULL CHECK(total_parts>0),
 created_at INTEGER NOT NULL,
 completed_at INTEGER NULL,
 remote_plan_json TEXT NULL,
 remote_plan_hash TEXT NULL,
 recovery_rows_json TEXT NULL,
 recovery_rows_hash TEXT NULL
);
CREATE TABLE IF NOT EXISTS catalog_import_plan_part (
 plan_id TEXT NOT NULL REFERENCES catalog_import_plan(plan_id) ON DELETE RESTRICT,
 ordinal INTEGER NOT NULL CHECK(ordinal>=0),
 outbox_id INTEGER NOT NULL UNIQUE REFERENCES catalog_import_outbox(id) ON DELETE RESTRICT,
 payload_hash TEXT NOT NULL,
 row_count INTEGER NOT NULL CHECK(row_count BETWEEN 1 AND 1000),
 ack_json TEXT NULL,
 PRIMARY KEY(plan_id,ordinal)
);
CREATE TABLE IF NOT EXISTS catalog_import_recovery_draft (
 original_id INTEGER PRIMARY KEY REFERENCES catalog_import_outbox(id) ON DELETE RESTRICT,
 payload_hash TEXT NOT NULL,
 rows_json TEXT NOT NULL,
 rows_hash TEXT NOT NULL,
 origin_shop_id TEXT NOT NULL,
 origin_shop_code TEXT NOT NULL,
 generation_fingerprint TEXT NULL,
 transition_epoch INTEGER NOT NULL,
 revision_fingerprint TEXT NOT NULL,
 updated_at INTEGER NOT NULL
);
";
        internal static void Apply(SqliteConnection connection, SqliteTransaction transaction) => connection.Execute(Sql, transaction: transaction);
        internal static bool IsSatisfied(LegacySchemaDetector detector) => detector.HasCanonicalTableDefinitions(Sql,
            "catalog_import_plan", "catalog_import_plan_part", "catalog_import_recovery_draft", "catalog_import_correction_proof", "catalog_import_prepared_plan", "catalog_import_recovery_supersession") && detector.HasExactCatalogImportPlanDefinitions();
    }
}
