using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    // Separate transfer metadata and read selection. Frozen migration16/view
    // and the financial ledger stay unchanged; ever_web is never fabricated.
    internal static class HistoricalBaselineSchema
    {
        internal const string Sql=@"
CREATE TABLE historical_baseline_flow (
 flow_id TEXT PRIMARY KEY NOT NULL,shop_id TEXT NOT NULL,device_id TEXT NOT NULL,staff_id TEXT NOT NULL,
 local_dataset_id TEXT NOT NULL,restore_lineage_id TEXT NOT NULL,restore_epoch INTEGER NOT NULL CHECK(typeof(restore_epoch)='integer' AND restore_epoch>=0),
 local_revision INTEGER NOT NULL CHECK(typeof(local_revision)='integer' AND local_revision>=0),server_dataset_id TEXT NOT NULL,
 writer_epoch INTEGER NOT NULL CHECK(typeof(writer_epoch)='integer' AND writer_epoch>0),
 archive_path TEXT NOT NULL,backup_path TEXT NOT NULL,archive_sha256 TEXT NOT NULL,manifest_sha256 TEXT NOT NULL,backup_sha256 TEXT NOT NULL,
 manifest_json TEXT NOT NULL CHECK(length(CAST(manifest_json AS BLOB)) BETWEEN 1 AND 65536),
 start_request_id TEXT NOT NULL UNIQUE,apply_request_id TEXT NOT NULL UNIQUE,
 state TEXT NOT NULL CHECK(state IN('prepared','upload_created','upload_ready','run_started','applied','needs_attention')),
 upload_id TEXT NULL,run_id TEXT NULL,run_session_id TEXT NULL,run_credential_version INTEGER NULL,
 review_digest TEXT NULL CHECK(review_digest IS NULL OR (length(review_digest)=64 AND review_digest NOT GLOB '*[^0-9a-f]*')),
 receipt_json TEXT NULL,receipt_sha256 TEXT NULL,source_snapshot_id TEXT NULL,server_revision INTEGER NULL,
 reason_code TEXT NULL CHECK(reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 64),
 CHECK(state='needs_attention' OR
  (state='prepared' AND upload_id IS NULL AND run_id IS NULL AND receipt_json IS NULL AND receipt_sha256 IS NULL AND source_snapshot_id IS NULL AND server_revision IS NULL) OR
  (state IN('upload_created','upload_ready') AND upload_id IS NOT NULL AND run_id IS NULL AND receipt_json IS NULL AND receipt_sha256 IS NULL AND source_snapshot_id IS NULL AND server_revision IS NULL) OR
  (state='run_started' AND upload_id IS NOT NULL AND run_id IS NOT NULL AND receipt_json IS NULL AND receipt_sha256 IS NULL AND source_snapshot_id IS NULL AND server_revision IS NULL) OR
  (state='applied' AND upload_id IS NOT NULL AND run_id IS NOT NULL AND review_digest IS NOT NULL AND receipt_json IS NOT NULL AND receipt_sha256 IS NOT NULL AND source_snapshot_id IS NOT NULL
   AND typeof(server_revision)='integer' AND server_revision>0)),
 CHECK(receipt_json IS NULL OR length(CAST(receipt_json AS BLOB)) BETWEEN 1 AND 65536),
 CHECK((run_id IS NULL AND run_session_id IS NULL AND run_credential_version IS NULL) OR
  (run_id IS NOT NULL AND run_session_id IS NOT NULL AND typeof(run_credential_version)='integer' AND run_credential_version>0)),
 CHECK(length(archive_sha256)=64 AND archive_sha256 NOT GLOB '*[^0-9a-f]*'),
 CHECK(length(manifest_sha256)=64 AND manifest_sha256 NOT GLOB '*[^0-9a-f]*'),
 CHECK(length(backup_sha256)=64 AND backup_sha256 NOT GLOB '*[^0-9a-f]*'),
 CHECK(receipt_sha256 IS NULL OR (length(receipt_sha256)=64 AND receipt_sha256 NOT GLOB '*[^0-9a-f]*')),
 UNIQUE(shop_id,device_id,local_dataset_id,restore_lineage_id,server_dataset_id,writer_epoch)
);
CREATE TABLE historical_baseline_recovery (
 request_id TEXT PRIMARY KEY NOT NULL,flow_id TEXT NOT NULL REFERENCES historical_baseline_flow(flow_id),
 session_id TEXT NOT NULL,credential_version INTEGER NOT NULL CHECK(typeof(credential_version)='integer' AND credential_version>0),
 previous_review_digest TEXT NULL CHECK(previous_review_digest IS NULL OR (length(previous_review_digest)=64 AND previous_review_digest NOT GLOB '*[^0-9a-f]*')),
 state TEXT NOT NULL CHECK(state IN('prepared','acknowledged','consumed','superseded')),ack_json TEXT NULL,ack_sha256 TEXT NULL,
 CHECK((state IN('prepared','superseded') AND ack_json IS NULL AND ack_sha256 IS NULL) OR
  (state IN('acknowledged','consumed') AND ack_json IS NOT NULL AND ack_sha256 IS NOT NULL AND length(CAST(ack_json AS BLOB)) BETWEEN 1 AND 16384 AND length(ack_sha256)=64 AND ack_sha256 NOT GLOB '*[^0-9a-f]*'))
);
CREATE UNIQUE INDEX ux_historical_baseline_recovery_context ON historical_baseline_recovery(flow_id,session_id,credential_version,COALESCE(previous_review_digest,''));
CREATE TRIGGER historical_baseline_recovery_delete BEFORE DELETE ON historical_baseline_recovery
 BEGIN SELECT RAISE(ABORT,'history_baseline_recovery_immutable');END;
CREATE TRIGGER historical_baseline_recovery_update BEFORE UPDATE ON historical_baseline_recovery
 WHEN NEW.request_id IS NOT OLD.request_id OR NEW.flow_id IS NOT OLD.flow_id OR NEW.session_id IS NOT OLD.session_id
 OR NEW.credential_version IS NOT OLD.credential_version OR NEW.previous_review_digest IS NOT OLD.previous_review_digest
 OR (OLD.ack_json IS NOT NULL AND (NEW.ack_json IS NOT OLD.ack_json OR NEW.ack_sha256 IS NOT OLD.ack_sha256))
 OR NOT ((OLD.state='prepared' AND NEW.state='acknowledged') OR (OLD.state='acknowledged' AND NEW.state='consumed') OR
  (OLD.state='prepared' AND NEW.state='superseded' AND EXISTS(SELECT 1 FROM historical_baseline_recovery r WHERE r.flow_id=OLD.flow_id AND r.state='consumed')))
 BEGIN SELECT RAISE(ABORT,'history_baseline_recovery_immutable');END;
CREATE TRIGGER historical_baseline_flow_delete BEFORE DELETE ON historical_baseline_flow
 BEGIN SELECT RAISE(ABORT,'history_baseline_flow_immutable');END;
CREATE TRIGGER historical_baseline_flow_update BEFORE UPDATE ON historical_baseline_flow
 WHEN NEW.flow_id IS NOT OLD.flow_id OR NEW.shop_id IS NOT OLD.shop_id OR NEW.device_id IS NOT OLD.device_id OR NEW.staff_id IS NOT OLD.staff_id
 OR NEW.local_dataset_id IS NOT OLD.local_dataset_id OR NEW.restore_lineage_id IS NOT OLD.restore_lineage_id OR NEW.restore_epoch IS NOT OLD.restore_epoch
 OR NEW.local_revision IS NOT OLD.local_revision OR NEW.server_dataset_id IS NOT OLD.server_dataset_id OR NEW.writer_epoch IS NOT OLD.writer_epoch
 OR NEW.archive_path IS NOT OLD.archive_path OR NEW.backup_path IS NOT OLD.backup_path OR NEW.archive_sha256 IS NOT OLD.archive_sha256
 OR NEW.manifest_sha256 IS NOT OLD.manifest_sha256 OR NEW.backup_sha256 IS NOT OLD.backup_sha256 OR NEW.manifest_json IS NOT OLD.manifest_json
 OR NEW.start_request_id IS NOT OLD.start_request_id OR NEW.apply_request_id IS NOT OLD.apply_request_id
 OR (OLD.upload_id IS NOT NULL AND NEW.upload_id IS NOT OLD.upload_id) OR (OLD.run_id IS NOT NULL AND NEW.run_id IS NOT OLD.run_id)
 OR ((OLD.review_digest IS NOT NULL AND NEW.review_digest IS NOT OLD.review_digest) OR
     (OLD.run_session_id IS NOT NULL AND (NEW.run_session_id IS NOT OLD.run_session_id OR NEW.run_credential_version IS NOT OLD.run_credential_version)))
 AND NOT (NEW.review_digest IS NULL AND EXISTS(SELECT 1 FROM historical_baseline_recovery r WHERE r.flow_id=OLD.flow_id AND r.state='acknowledged'
   AND r.previous_review_digest IS OLD.review_digest AND r.session_id=NEW.run_session_id AND r.credential_version=NEW.run_credential_version))
 OR OLD.state IN('applied','needs_attention') OR NOT (NEW.state='needs_attention'
 OR (OLD.state='run_started' AND NEW.state='run_started' AND OLD.review_digest IS NULL AND NEW.review_digest IS NOT NULL)
 OR (OLD.state='run_started' AND NEW.state='run_started' AND NEW.review_digest IS NULL AND EXISTS(
  SELECT 1 FROM historical_baseline_recovery r WHERE r.flow_id=OLD.flow_id AND r.state='acknowledged' AND r.previous_review_digest IS OLD.review_digest
   AND r.session_id=NEW.run_session_id AND r.credential_version=NEW.run_credential_version))
 OR (OLD.state='prepared' AND NEW.state='upload_created') OR (OLD.state='upload_created' AND NEW.state='upload_ready')
 OR (OLD.state='upload_ready' AND NEW.state='run_started') OR (OLD.state='run_started' AND NEW.state='applied'))
 BEGIN SELECT RAISE(ABORT,'history_baseline_flow_immutable');END;
CREATE TABLE historical_baseline_read_mode (
 shop_id TEXT NOT NULL,local_dataset_id TEXT NOT NULL,restore_lineage_id TEXT NOT NULL,server_dataset_id TEXT NOT NULL,
 flow_id TEXT NOT NULL UNIQUE REFERENCES historical_baseline_flow(flow_id),
 PRIMARY KEY(shop_id,local_dataset_id,restore_lineage_id,server_dataset_id)
);
CREATE TRIGGER historical_baseline_read_insert BEFORE INSERT ON historical_baseline_read_mode
 WHEN NOT EXISTS(SELECT 1 FROM historical_baseline_flow f JOIN historical_cloud_state s ON s.shop_id=f.shop_id AND s.dataset_id=f.server_dataset_id
 JOIN historical_cloud_generation g ON g.generation_id=s.generation_id AND g.shop_id=s.shop_id AND g.dataset_id=s.dataset_id
 WHERE f.flow_id=NEW.flow_id AND f.state='applied' AND f.shop_id=NEW.shop_id AND f.local_dataset_id=NEW.local_dataset_id
 AND f.restore_lineage_id=NEW.restore_lineage_id AND f.server_dataset_id=NEW.server_dataset_id AND g.complete=1
 AND s.published_revision=g.revision AND s.checksum=g.checksum AND s.published_revision>=f.server_revision)
 BEGIN SELECT RAISE(ABORT,'history_baseline_replica_unverified');END;
CREATE TRIGGER historical_baseline_read_update BEFORE UPDATE ON historical_baseline_read_mode
 BEGIN SELECT RAISE(ABORT,'history_baseline_read_immutable');END;
CREATE TRIGGER historical_baseline_read_delete BEFORE DELETE ON historical_baseline_read_mode
 BEGIN SELECT RAISE(ABORT,'history_baseline_read_immutable');END;
CREATE VIEW historical_baseline_report_movements AS
 SELECT h.id,h.shop_id,h.provider,h.is_active,h.business_ticks,h.signed_amount_clp,h.original_cashier_id,h.external_namespace,h.external_id,h.batch_id,h.source_sheet,h.source_row,h.timestamp_raw,h.offset_minutes,h.card_subtype
 FROM historical_transactions h WHERE NOT EXISTS(SELECT 1 FROM historical_cloud_state s WHERE s.shop_id=h.shop_id COLLATE NOCASE
 AND (s.ever_web=1 OR EXISTS(SELECT 1 FROM historical_baseline_read_mode m JOIN historical_import_dataset d ON d.singleton_id=1 AND d.dataset_id=m.local_dataset_id
 WHERE m.shop_id=s.shop_id AND replace(m.restore_lineage_id,'-','')=(SELECT replace(value,'-','') FROM app_settings WHERE key='pos.history.restore_lineage_id') COLLATE NOCASE)))
 UNION ALL
 SELECT e.id,e.shop_id,'getnet',e.is_active,e.business_ticks,e.signed_amount_clp,NULL,e.external_namespace,e.external_id,e.batch_id,e.source_sheet,e.source_row,e.timestamp_raw,e.offset_minutes,e.card_subtype
 FROM historical_cloud_entity e JOIN historical_cloud_state s ON s.shop_id=e.shop_id AND s.generation_id=e.generation_id
 WHERE e.entity_kind='movement' AND (s.ever_web=1 OR EXISTS(SELECT 1 FROM historical_baseline_read_mode m
 JOIN historical_import_dataset d ON d.singleton_id=1 AND d.dataset_id=m.local_dataset_id WHERE m.shop_id=s.shop_id AND m.server_dataset_id=s.dataset_id
 AND replace(m.restore_lineage_id,'-','')=(SELECT replace(value,'-','') FROM app_settings WHERE key='pos.history.restore_lineage_id') COLLATE NOCASE));
";
        internal static void Apply(SqliteConnection c,SqliteTransaction t)=>c.Execute(Sql,transaction:t);
        internal static bool IsSatisfied(LegacySchemaDetector d)
        {
            if(d.TableExists("historical_baseline_retry"))return HistoricalBaselineRetrySchema.MatchesCompleteBaseline(d);
            if(!d.HasCanonicalTableDefinitions(Sql,"historical_baseline_flow","historical_baseline_read_mode","historical_baseline_recovery"))return false;
            const string query="SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE 'historical_baseline_%' OR name LIKE '%_historical_baseline_%' ORDER BY type,name";
            using var expected=new SqliteConnection("Data Source=:memory:");expected.Open();expected.Execute(Sql);
            return expected.Query<string>(query).Select(Normalize).SequenceEqual(d.Connection.Query<string>(query,transaction:d.Transaction).Select(Normalize));
        }
        private static string Normalize(string sql)=>Regex.Replace(sql??string.Empty,@"\s+"," ").Trim().ToLowerInvariant();
    }
}
