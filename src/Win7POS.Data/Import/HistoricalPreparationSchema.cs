using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    internal static class HistoricalPreparationSchema
    {
        internal const string Sql=@"
CREATE TABLE historical_preparation_intent (
 operation_id TEXT PRIMARY KEY NOT NULL,local_nonce TEXT NOT NULL UNIQUE,
 shop_id TEXT NOT NULL,device_id TEXT NOT NULL,local_dataset_id TEXT NOT NULL,
 restore_lineage_id TEXT NOT NULL,restore_epoch INTEGER NOT NULL CHECK(restore_epoch>=0),
 base_revision INTEGER NOT NULL CHECK(base_revision>=0),server_dataset_id TEXT NOT NULL,
 writer_epoch INTEGER NOT NULL CHECK(writer_epoch>0),operation_kind TEXT NOT NULL CHECK(operation_kind IN('Apply','Undo')),
 request_fingerprint TEXT NOT NULL,content_digest TEXT NOT NULL,page_count INTEGER NOT NULL CHECK(page_count BETWEEN 1 AND 20000),created_at INTEGER NOT NULL
);
CREATE TABLE historical_preparation_page (
 operation_id TEXT NOT NULL REFERENCES historical_preparation_intent(operation_id),
 ordinal INTEGER NOT NULL CHECK(ordinal>0),page_json TEXT NOT NULL,page_sha256 TEXT NOT NULL,PRIMARY KEY(operation_id,ordinal)
);
CREATE TABLE historical_preparation_outcome (
 operation_id TEXT PRIMARY KEY NOT NULL REFERENCES historical_preparation_intent(operation_id),
 state TEXT NOT NULL CHECK(state IN('staging','prepared','authorized','committed','aborted')),proof_json TEXT NULL,
 CHECK(state NOT IN('authorized','committed') OR proof_json IS NOT NULL)
);
CREATE TRIGGER historical_preparation_intent_update BEFORE UPDATE ON historical_preparation_intent BEGIN SELECT RAISE(ABORT,'history_preparation_immutable');END;
CREATE TRIGGER historical_preparation_intent_delete BEFORE DELETE ON historical_preparation_intent BEGIN SELECT RAISE(ABORT,'history_preparation_immutable');END;
CREATE TRIGGER historical_preparation_page_update BEFORE UPDATE ON historical_preparation_page BEGIN SELECT RAISE(ABORT,'history_preparation_immutable');END;
CREATE TRIGGER historical_preparation_page_delete BEFORE DELETE ON historical_preparation_page
 WHEN NOT EXISTS(SELECT 1 FROM historical_preparation_outcome s JOIN historical_publication_page p ON p.operation_id=s.operation_id
 WHERE s.operation_id=OLD.operation_id AND s.state='committed' AND p.ordinal=OLD.ordinal AND p.page_json=OLD.page_json AND p.page_sha256=OLD.page_sha256)
 BEGIN SELECT RAISE(ABORT,'history_preparation_immutable');END;
CREATE TRIGGER historical_preparation_outcome_delete BEFORE DELETE ON historical_preparation_outcome BEGIN SELECT RAISE(ABORT,'history_preparation_immutable');END;
CREATE TRIGGER historical_preparation_outcome_update BEFORE UPDATE ON historical_preparation_outcome
 WHEN NEW.operation_id<>OLD.operation_id OR OLD.state IN('committed','aborted')
 OR (OLD.proof_json IS NOT NULL AND NEW.proof_json IS NOT OLD.proof_json)
 OR (OLD.state='authorized' AND NEW.state IN('staging','prepared')) OR (OLD.state='prepared' AND NEW.state='staging')
 BEGIN SELECT RAISE(ABORT,'history_preparation_immutable');END;
";
        internal static void Apply(SqliteConnection c,SqliteTransaction t)=>c.Execute(Sql,transaction:t);
        internal static bool IsSatisfied(LegacySchemaDetector d)
        {
            if(!d.HasCanonicalTableDefinitions(Sql,"historical_preparation_intent","historical_preparation_page","historical_preparation_outcome"))return false;
            const string query="SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE 'historical_preparation_%' ORDER BY type,name";
            using(var expected=new SqliteConnection("Data Source=:memory:"))
            {
                expected.Open();expected.Execute(Sql);
                return expected.Query<string>(query).Select(Normalize).SequenceEqual(d.Connection.Query<string>(query,transaction:d.Transaction).Select(Normalize));
            }
        }
        private static string Normalize(string sql)
        {
            var parts=Regex.Split(sql??string.Empty,@"('(?:''|[^'])*')");
            return string.Concat(parts.Select((part,index)=>index%2==1?part:Regex.Replace(part,@"\s+"," ").ToLowerInvariant())).Trim();
        }
    }
}
