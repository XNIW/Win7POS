using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Migrations;

namespace Win7POS.Data.Import
{
    internal static class HistoricalCloudSchema
    {
        internal const string Sql=@"
CREATE TABLE historical_cloud_state (
 shop_id TEXT PRIMARY KEY NOT NULL,dataset_id TEXT NOT NULL,writer_epoch INTEGER NOT NULL CHECK(writer_epoch>0),
 server_revision INTEGER NOT NULL CHECK(server_revision>=0),writer_authority TEXT NOT NULL CHECK(writer_authority IN('local','frozen','web')),
 generation_id TEXT NULL,published_revision INTEGER NULL,checksum TEXT NULL,last_success_utc_ms INTEGER NULL,stale_reason TEXT NULL,
 ever_web INTEGER NOT NULL DEFAULT 0 CHECK(ever_web IN(0,1))
);
CREATE TABLE historical_cloud_cache_revision (
 singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>=0)
);
INSERT INTO historical_cloud_cache_revision(singleton_id,revision) VALUES(1,0);
CREATE TABLE historical_cloud_prepared_export (
 shop_id TEXT NOT NULL,writer_epoch INTEGER NOT NULL,local_dataset_id TEXT NOT NULL,data_revision INTEGER NOT NULL,restore_epoch INTEGER NOT NULL,
 archive_path TEXT NOT NULL,backup_path TEXT NOT NULL,archive_sha256 TEXT NOT NULL,manifest_sha256 TEXT NOT NULL,backup_sha256 TEXT NOT NULL,manifest_json TEXT NOT NULL,
 PRIMARY KEY(shop_id,writer_epoch)
);
CREATE TRIGGER historical_cloud_export_immutable_update BEFORE UPDATE ON historical_cloud_prepared_export BEGIN SELECT RAISE(ABORT,'history_cloud_export_immutable');END;
CREATE TRIGGER historical_cloud_export_immutable_delete BEFORE DELETE ON historical_cloud_prepared_export BEGIN SELECT RAISE(ABORT,'history_cloud_export_immutable');END;
CREATE TABLE historical_cloud_generation (
 generation_id TEXT PRIMARY KEY NOT NULL,shop_id TEXT NOT NULL,dataset_id TEXT NOT NULL,writer_epoch INTEGER NOT NULL,revision INTEGER NOT NULL,
 mode TEXT NOT NULL CHECK(mode IN('snapshot','delta')),checksum TEXT NOT NULL,movement_count INTEGER NOT NULL,batch_count INTEGER NOT NULL,
 next_cursor TEXT NULL,terminal_cursor TEXT NULL,source_generation_id TEXT NULL,complete INTEGER NOT NULL DEFAULT 0 CHECK(complete IN(0,1)),
 created_utc_ms INTEGER NOT NULL,last_cursor TEXT NULL,last_page_digest TEXT NULL
);
CREATE UNIQUE INDEX ux_historical_cloud_generation_shop ON historical_cloud_generation(generation_id,shop_id);
CREATE TABLE historical_cloud_entity (
 id INTEGER PRIMARY KEY AUTOINCREMENT,generation_id TEXT NOT NULL REFERENCES historical_cloud_generation(generation_id),
 entity_kind TEXT NOT NULL CHECK(entity_kind IN('batch','movement')),entity_id TEXT NOT NULL,entity_digest TEXT NOT NULL,payload_json TEXT NOT NULL,
 shop_id TEXT NOT NULL,batch_id TEXT NULL,identity_key TEXT NULL,is_active INTEGER NULL,business_ticks INTEGER NULL,
 signed_amount_clp INTEGER NULL,signed_fee_clp INTEGER NULL,signed_fee_vat_clp INTEGER NULL,signed_net_clp INTEGER NULL,
 external_namespace TEXT NULL,external_id TEXT NULL,source_sheet TEXT NULL,source_row INTEGER NULL,timestamp_raw TEXT NULL,offset_minutes INTEGER NULL,card_subtype TEXT NULL,
 UNIQUE(generation_id,entity_kind,entity_id),FOREIGN KEY(generation_id,shop_id) REFERENCES historical_cloud_generation(generation_id,shop_id)
);
CREATE INDEX ix_historical_cloud_report ON historical_cloud_entity(generation_id,entity_kind,is_active,business_ticks DESC,id DESC);
CREATE INDEX ix_historical_cloud_active_identity ON historical_cloud_entity(generation_id,identity_key) WHERE entity_kind='movement' AND is_active=1;
CREATE VIEW historical_cloud_report_movements AS
 SELECT h.id,h.shop_id,h.provider,h.is_active,h.business_ticks,h.signed_amount_clp,h.original_cashier_id,h.external_namespace,h.external_id,h.batch_id,h.source_sheet,h.source_row,h.timestamp_raw,h.offset_minutes,h.card_subtype
 FROM historical_transactions h WHERE NOT EXISTS(SELECT 1 FROM historical_cloud_state s WHERE s.shop_id=h.shop_id COLLATE NOCASE AND s.ever_web=1)
 UNION ALL
 SELECT e.id,e.shop_id,'getnet',e.is_active,e.business_ticks,e.signed_amount_clp,NULL,e.external_namespace,e.external_id,e.batch_id,e.source_sheet,e.source_row,e.timestamp_raw,e.offset_minutes,e.card_subtype
 FROM historical_cloud_entity e JOIN historical_cloud_state s ON s.shop_id=e.shop_id AND s.generation_id=e.generation_id
 WHERE s.ever_web=1 AND e.entity_kind='movement';
";
        internal static void Apply(SqliteConnection connection,SqliteTransaction transaction)=>connection.Execute(Sql,transaction:transaction);
        internal static bool IsSatisfied(LegacySchemaDetector detector)
        {
            if(!detector.HasCanonicalTableDefinitions(Sql,"historical_cloud_state","historical_cloud_generation","historical_cloud_entity","historical_cloud_cache_revision","historical_cloud_prepared_export"))return false;
            using(var expected=new SqliteConnection("Data Source=:memory:"))
            {
                expected.Open();expected.Execute(Sql);
                const string query="SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE 'historical_cloud_%' OR name LIKE '%_historical_cloud_%' ORDER BY type,name";
                var want=expected.Query<string>(query).Select(Normalize).ToArray();
                var actual=detector.Connection.Query<string>(query,transaction:detector.Transaction).Select(Normalize).ToArray();
                return want.SequenceEqual(actual);
            }
        }
        private static string Normalize(string sql)=>Regex.Replace(sql??string.Empty,@"\s+"," ").Trim().ToLowerInvariant();
    }
}
