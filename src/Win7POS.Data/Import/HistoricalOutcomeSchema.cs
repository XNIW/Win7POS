using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    internal static class HistoricalOutcomeSchema
    {
        internal const string Sql=@"
CREATE TABLE historical_commit_witness (
 operation_id TEXT PRIMARY KEY NOT NULL REFERENCES historical_preparation_intent(operation_id),
 outcome TEXT NOT NULL CHECK(outcome IN('committed','aborted')),
 witness_json TEXT NOT NULL CHECK(length(CAST(witness_json AS BLOB)) BETWEEN 1 AND 16384),
 witness_sha256 TEXT NOT NULL CHECK(length(witness_sha256)=64 AND witness_sha256 NOT GLOB '*[^0-9a-f]*'),
 server_resolution TEXT NULL,resolved_at_utc_ms INTEGER NULL,
 CHECK((server_resolution IS NULL AND resolved_at_utc_ms IS NULL)
 OR (server_resolution IS NOT NULL AND resolved_at_utc_ms IS NOT NULL AND server_resolution=outcome
 AND typeof(resolved_at_utc_ms)='integer' AND resolved_at_utc_ms>0))
);
CREATE TRIGGER historical_commit_witness_delete BEFORE DELETE ON historical_commit_witness
 BEGIN SELECT RAISE(ABORT,'history_commit_witness_immutable');END;
CREATE TRIGGER historical_commit_witness_update BEFORE UPDATE ON historical_commit_witness
 WHEN NEW.operation_id IS NOT OLD.operation_id OR NEW.outcome IS NOT OLD.outcome
 OR NEW.witness_json IS NOT OLD.witness_json OR NEW.witness_sha256 IS NOT OLD.witness_sha256
 OR OLD.server_resolution IS NOT NULL
 BEGIN SELECT RAISE(ABORT,'history_commit_witness_immutable');END;
";
        internal static void Apply(SqliteConnection c,SqliteTransaction t)=>c.Execute(Sql,transaction:t);
        internal static bool IsSatisfied(LegacySchemaDetector d)
        {
            if(!d.HasCanonicalTableDefinitions(Sql,"historical_commit_witness"))return false;
            const string query="SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE 'historical_commit_%' ORDER BY type,name";
            using var expected=new SqliteConnection("Data Source=:memory:");expected.Open();expected.Execute(Sql);
            return expected.Query<string>(query).Select(Normalize).SequenceEqual(d.Connection.Query<string>(query,transaction:d.Transaction).Select(Normalize));
        }
        private static string Normalize(string sql)
        {
            var parts=Regex.Split(sql??string.Empty,@"('(?:''|[^'])*')");
            return string.Concat(parts.Select((part,index)=>index%2==1?part:Regex.Replace(part,@"\s+"," ").ToLowerInvariant())).Trim();
        }
    }
}
