using Dapper;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    internal static class HistoricalPublicationSchema
    {
        internal const string Sql = @"
CREATE TABLE historical_publication_outbox (
 sequence INTEGER PRIMARY KEY AUTOINCREMENT,
 operation_id TEXT NOT NULL UNIQUE REFERENCES historical_import_operation(operation_id),
 shop_id TEXT NOT NULL,device_id TEXT NOT NULL,local_dataset_id TEXT NOT NULL,
 restore_epoch INTEGER NOT NULL CHECK(restore_epoch>=0),local_revision INTEGER NOT NULL CHECK(local_revision>=0),
 operation_kind TEXT NOT NULL CHECK(operation_kind IN('Apply','Undo')),
 request_fingerprint TEXT NOT NULL,content_digest TEXT NOT NULL,protocol_version INTEGER NOT NULL CHECK(protocol_version=2),
 writer_epoch INTEGER NULL,grant_id TEXT NULL,proof_json TEXT NULL,
 depends_on_operation_id TEXT NULL,receipt_json TEXT NOT NULL,page_count INTEGER NOT NULL CHECK(page_count>0),
 created_at INTEGER NOT NULL,
 CHECK((grant_id IS NULL AND writer_epoch IS NULL AND proof_json IS NULL) OR
       (grant_id IS NOT NULL AND writer_epoch>0 AND proof_json IS NOT NULL))
);
CREATE TABLE historical_publication_page (
 operation_id TEXT NOT NULL REFERENCES historical_publication_outbox(operation_id),
 ordinal INTEGER NOT NULL CHECK(ordinal>0),page_json TEXT NOT NULL,page_sha256 TEXT NOT NULL,
 PRIMARY KEY(operation_id,ordinal)
);
CREATE TABLE historical_publication_delivery (
 operation_id TEXT PRIMARY KEY NOT NULL REFERENCES historical_publication_outbox(operation_id),
 status TEXT NOT NULL CHECK(status IN('pending','uncertain','synchronized','needs_attention')),
 reason_code TEXT NULL,next_page INTEGER NOT NULL DEFAULT 1 CHECK(next_page>0),
 attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),next_attempt_at INTEGER NULL,
 server_receipt_json TEXT NULL,server_revision TEXT NULL,confirmed_at INTEGER NULL,
 CHECK((status='synchronized' AND server_receipt_json IS NOT NULL AND server_revision IS NOT NULL AND confirmed_at IS NOT NULL)
    OR (status<>'synchronized' AND server_receipt_json IS NULL AND server_revision IS NULL AND confirmed_at IS NULL))
);
CREATE INDEX ix_historical_publication_due ON historical_publication_delivery(status,next_attempt_at,operation_id);
CREATE TRIGGER historical_publication_immutable_update BEFORE UPDATE ON historical_publication_outbox BEGIN SELECT RAISE(ABORT,'history_publication_immutable');END;
CREATE TRIGGER historical_publication_immutable_delete BEFORE DELETE ON historical_publication_outbox BEGIN SELECT RAISE(ABORT,'history_publication_immutable');END;
CREATE TRIGGER historical_publication_page_immutable_update BEFORE UPDATE ON historical_publication_page BEGIN SELECT RAISE(ABORT,'history_publication_immutable');END;
CREATE TRIGGER historical_publication_page_immutable_delete BEFORE DELETE ON historical_publication_page BEGIN SELECT RAISE(ABORT,'history_publication_immutable');END;
CREATE TRIGGER historical_publication_receipt_immutable BEFORE UPDATE ON historical_publication_delivery
 WHEN OLD.status='synchronized' BEGIN SELECT RAISE(ABORT,'history_publication_receipt_immutable');END;
";
        internal const string LineageSeedSql=@"INSERT OR IGNORE INTO app_settings(key,value) VALUES('pos.history.restore_lineage_id',lower(hex(randomblob(16))));
";
        internal static void Apply(SqliteConnection c,SqliteTransaction t)=>c.Execute(Sql+LineageSeedSql,transaction:t);
        internal static bool IsSatisfied(LegacySchemaDetector d)
        {
            if(!d.HasCanonicalTableDefinitions(Sql,"historical_publication_outbox","historical_publication_page","historical_publication_delivery"))return false;
            if(d.Connection.ExecuteScalar<long>("SELECT COUNT(*) FROM app_settings WHERE key='pos.history.restore_lineage_id' AND length(value)=32 AND value NOT GLOB '*[^0-9a-f]*'",transaction:d.Transaction)!=1)return false;
            // Compare triggers and indexes as well as table columns. A dropped
            // immutability trigger is not an admissible restored baseline.
            const string query="SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE 'historical_publication_%' OR name='ix_historical_publication_due' ORDER BY type,name";
            using(var expected=new SqliteConnection("Data Source=:memory:"))
            {
                expected.Open();expected.Execute(Sql);
                return expected.Query<string>(query).Select(Normalize).SequenceEqual(d.Connection.Query<string>(query,transaction:d.Transaction).Select(Normalize));
            }
        }
        private static string Normalize(string sql)=>Regex.Replace(sql??string.Empty,@"\s+"," ").Trim().ToLowerInvariant();
    }
}
