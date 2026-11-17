using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    // Additive operator intent. The published 0020 material is unchanged.
    internal static class HistoricalBaselineRetrySchema
    {
        internal const string TableSql=@"
CREATE TABLE historical_baseline_retry (
 request_id TEXT PRIMARY KEY NOT NULL,flow_id TEXT NOT NULL REFERENCES historical_baseline_flow(flow_id),
 session_id TEXT NOT NULL,credential_version INTEGER NOT NULL CHECK(typeof(credential_version)='integer' AND credential_version>0),
 previous_review_digest TEXT NULL CHECK(previous_review_digest IS NULL),
 supersedes_request_id TEXT NULL REFERENCES historical_baseline_retry(request_id),
 state TEXT NOT NULL CHECK(state IN('prepared','acknowledged','consumed','superseded','rejected')),
 ack_json TEXT NULL,ack_sha256 TEXT NULL,rejection_code TEXT NULL,
 CHECK((state IN('prepared','superseded','rejected') AND ack_json IS NULL AND ack_sha256 IS NULL)
 OR (state IN('acknowledged','consumed') AND ack_json IS NOT NULL AND ack_sha256 IS NOT NULL
 AND length(CAST(ack_json AS BLOB)) BETWEEN 1 AND 16384 AND length(ack_sha256)=64 AND ack_sha256 NOT GLOB '*[^0-9a-f]*')),
 CHECK((state='rejected' AND length(rejection_code) BETWEEN 1 AND 80) OR (state<>'rejected' AND rejection_code IS NULL))
);
CREATE UNIQUE INDEX ux_historical_baseline_retry_pending ON historical_baseline_retry(flow_id,session_id,credential_version) WHERE state IN('prepared','acknowledged');
CREATE TRIGGER historical_baseline_retry_insert BEFORE INSERT ON historical_baseline_retry
 WHEN NEW.state<>'prepared' OR NOT EXISTS(SELECT 1 FROM historical_baseline_flow f WHERE f.flow_id=NEW.flow_id
 AND f.state='needs_attention' AND f.reason_code='baseline_job_failed' AND f.review_digest IS NULL AND f.receipt_json IS NULL
 AND f.run_id IS NOT NULL AND f.upload_id IS NOT NULL)
 OR (NEW.supersedes_request_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM historical_baseline_retry r
 WHERE r.request_id=NEW.supersedes_request_id AND r.flow_id=NEW.flow_id AND r.state='prepared'))
 BEGIN SELECT RAISE(ABORT,'history_baseline_retry_invalid');END;
CREATE TRIGGER historical_baseline_retry_delete BEFORE DELETE ON historical_baseline_retry
 BEGIN SELECT RAISE(ABORT,'history_baseline_retry_immutable');END;
CREATE TRIGGER historical_baseline_retry_update BEFORE UPDATE ON historical_baseline_retry
 WHEN NEW.request_id IS NOT OLD.request_id OR NEW.flow_id IS NOT OLD.flow_id OR NEW.session_id IS NOT OLD.session_id
 OR NEW.credential_version IS NOT OLD.credential_version OR NEW.previous_review_digest IS NOT OLD.previous_review_digest
 OR NEW.supersedes_request_id IS NOT OLD.supersedes_request_id
 OR (OLD.ack_json IS NOT NULL AND (NEW.ack_json IS NOT OLD.ack_json OR NEW.ack_sha256 IS NOT OLD.ack_sha256))
 OR NOT ((OLD.state='prepared' AND NEW.state IN('acknowledged','rejected')) OR (OLD.state='acknowledged' AND NEW.state='consumed')
 OR (OLD.state='prepared' AND NEW.state='superseded' AND EXISTS(SELECT 1 FROM historical_baseline_retry r WHERE r.flow_id=OLD.flow_id AND r.state='consumed')))
 BEGIN SELECT RAISE(ABORT,'history_baseline_retry_immutable');END;
";
        private const string RetryTransition=@"OLD.state='needs_attention' AND OLD.reason_code='baseline_job_failed' AND NEW.state='run_started'
 AND NEW.reason_code IS OLD.reason_code AND OLD.review_digest IS NULL AND NEW.review_digest IS NULL
 AND NEW.flow_id IS OLD.flow_id AND NEW.shop_id IS OLD.shop_id AND NEW.device_id IS OLD.device_id AND NEW.staff_id IS OLD.staff_id
 AND NEW.local_dataset_id IS OLD.local_dataset_id AND NEW.restore_lineage_id IS OLD.restore_lineage_id AND NEW.restore_epoch IS OLD.restore_epoch
 AND NEW.local_revision IS OLD.local_revision AND NEW.server_dataset_id IS OLD.server_dataset_id AND NEW.writer_epoch IS OLD.writer_epoch
 AND NEW.archive_path IS OLD.archive_path AND NEW.backup_path IS OLD.backup_path AND NEW.archive_sha256 IS OLD.archive_sha256
 AND NEW.manifest_sha256 IS OLD.manifest_sha256 AND NEW.backup_sha256 IS OLD.backup_sha256 AND NEW.manifest_json IS OLD.manifest_json
 AND NEW.start_request_id IS OLD.start_request_id AND NEW.apply_request_id IS OLD.apply_request_id
 AND NEW.upload_id IS OLD.upload_id AND NEW.run_id IS OLD.run_id
 AND OLD.receipt_json IS NULL AND NEW.receipt_json IS OLD.receipt_json AND NEW.receipt_sha256 IS OLD.receipt_sha256
 AND NEW.source_snapshot_id IS OLD.source_snapshot_id AND NEW.server_revision IS OLD.server_revision
 AND EXISTS(SELECT 1 FROM historical_baseline_retry r WHERE r.flow_id=OLD.flow_id AND r.state='acknowledged'
 AND r.previous_review_digest IS NULL AND r.session_id=NEW.run_session_id AND r.credential_version=NEW.run_credential_version)";
        // Retain every original guard and admit only this complete, ACK-backed transition.
        internal static readonly string Sql=TableSql+BuildTriggerSql();
        private static string BuildTriggerSql()
        {
            const string start="CREATE TRIGGER historical_baseline_flow_update BEFORE UPDATE ON historical_baseline_flow\n";
            var original=HistoricalBaselineSchema.Sql.Replace("\r\n","\n");var index=original.IndexOf(start,StringComparison.Ordinal);
            if(index<0)throw new InvalidOperationException("history_baseline_retry_schema_invalid");
            var end=original.IndexOf("CREATE TABLE historical_baseline_read_mode",index,StringComparison.Ordinal);
            var trigger=original.Substring(index,end-index);
            return "DROP TRIGGER historical_baseline_flow_update;\n"+trigger.Replace(" WHEN NEW.flow_id"," WHEN NOT ("+RetryTransition+") AND (NEW.flow_id")
                .Replace(" BEGIN SELECT RAISE(ABORT,'history_baseline_flow_immutable');END;", ")\n BEGIN SELECT RAISE(ABORT,'history_baseline_flow_immutable');END;");
        }
        internal static void Apply(SqliteConnection c,SqliteTransaction t)=>c.Execute(Sql,transaction:t);
        internal static bool IsSatisfied(LegacySchemaDetector d)
        {
            if(!d.HasCanonicalTableDefinitions(TableSql,"historical_baseline_retry"))return false;
            return MatchesCompleteBaseline(d);
        }
        internal static bool MatchesCompleteBaseline(LegacySchemaDetector d)
        {
            const string query="SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE 'historical_baseline_%' OR name LIKE '%_historical_baseline_%' ORDER BY type,name";
            using var expected=new SqliteConnection("Data Source=:memory:");expected.Open();expected.Execute(HistoricalBaselineSchema.Sql);expected.Execute(Sql);
            return expected.Query<string>(query).Select(Normalize).SequenceEqual(d.Connection.Query<string>(query,transaction:d.Transaction).Select(Normalize));
        }
        private static string Normalize(string sql)=>Regex.Replace(sql??string.Empty,@"\s+"," ").Trim().ToLowerInvariant();
    }
}
