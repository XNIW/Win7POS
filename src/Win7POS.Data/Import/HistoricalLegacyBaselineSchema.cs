using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    // Inclusion evidence, distinct from an incremental grant/delivery receipt.
    // Original immutable operations and all existing financial tables stay intact.
    internal static class HistoricalLegacyBaselineSchema
    {
        internal const string Sql=@"
CREATE TABLE historical_legacy_baseline_operation (
 flow_id TEXT NOT NULL REFERENCES historical_baseline_flow(flow_id),
 operation_id TEXT NOT NULL REFERENCES historical_import_operation(operation_id),
 PRIMARY KEY(flow_id,operation_id)
);
CREATE TRIGGER historical_legacy_baseline_operation_insert BEFORE INSERT ON historical_legacy_baseline_operation
 WHEN NOT EXISTS(SELECT 1 FROM historical_baseline_flow f JOIN historical_import_operation op
 ON op.shop_id=f.shop_id AND op.dataset_id=f.local_dataset_id
 LEFT JOIN historical_publication_outbox o ON o.operation_id=op.operation_id
 WHERE f.flow_id=NEW.flow_id AND f.state='prepared' AND op.operation_id=NEW.operation_id
 AND (o.operation_id IS NULL OR (o.proof_json IS NULL AND o.grant_id IS NULL AND o.writer_epoch IS NULL
  AND o.receipt_json=op.receipt_json AND o.shop_id=op.shop_id AND o.local_dataset_id=op.dataset_id))
 AND NOT EXISTS(SELECT 1 FROM historical_preparation_intent i WHERE i.operation_id=op.operation_id))
 BEGIN SELECT RAISE(ABORT,'history_legacy_baseline_scope_invalid');END;
CREATE TRIGGER historical_legacy_baseline_operation_update BEFORE UPDATE ON historical_legacy_baseline_operation
 BEGIN SELECT RAISE(ABORT,'history_legacy_baseline_immutable');END;
CREATE TRIGGER historical_legacy_baseline_operation_delete BEFORE DELETE ON historical_legacy_baseline_operation
 BEGIN SELECT RAISE(ABORT,'history_legacy_baseline_immutable');END;
CREATE VIEW historical_legacy_baseline_confirmed AS
 SELECT b.operation_id,op.shop_id,CAST(MAX(f.server_revision) AS TEXT) server_revision
 FROM historical_legacy_baseline_operation b JOIN historical_baseline_flow f ON f.flow_id=b.flow_id AND f.state='applied'
 JOIN historical_import_operation op ON op.operation_id=b.operation_id AND op.shop_id=f.shop_id AND op.dataset_id=f.local_dataset_id
 JOIN historical_import_dataset d ON d.singleton_id=1 AND d.dataset_id=f.local_dataset_id
 JOIN historical_baseline_read_mode m ON m.flow_id=f.flow_id
 JOIN historical_cloud_state s ON s.shop_id=f.shop_id AND s.dataset_id=f.server_dataset_id
 WHERE replace(f.restore_lineage_id,'-','')=(SELECT replace(value,'-','') FROM app_settings WHERE key='pos.history.restore_lineage_id') COLLATE NOCASE
 GROUP BY b.operation_id,op.shop_id;
";
        internal static void Apply(SqliteConnection c,SqliteTransaction t)=>c.Execute(Sql,transaction:t);
        internal static bool IsSatisfied(LegacySchemaDetector d)
        {
            if(!d.HasCanonicalTableDefinitions(Sql,"historical_legacy_baseline_operation"))return false;
            const string query="SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE 'historical_legacy_baseline_%' OR name LIKE '%_historical_legacy_baseline_%' ORDER BY type,name";
            using var expected=new SqliteConnection("Data Source=:memory:");expected.Open();expected.Execute(Sql);
            return expected.Query<string>(query).Select(Normalize).SequenceEqual(d.Connection.Query<string>(query,transaction:d.Transaction).Select(Normalize));
        }
        private static string Normalize(string text)=>Regex.Replace(text??string.Empty,@"\s+"," ").Trim().ToLowerInvariant();
    }
}
