using System;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Import
{
    internal static class HistoricalBaselineReadMode
    {
        // All identifiers in this SQL are source constants, never user input.
        internal const string LocalScope=@"SELECT 1 FROM historical_baseline_read_mode m
JOIN historical_import_dataset d ON d.singleton_id=1 AND d.dataset_id=m.local_dataset_id
WHERE m.shop_id=s.shop_id AND replace(m.restore_lineage_id,'-','')=
(SELECT replace(value,'-','') FROM app_settings WHERE key='pos.history.restore_lineage_id') COLLATE NOCASE";
        internal const string CurrentServerScope=LocalScope+" AND m.server_dataset_id=s.dataset_id";

        internal static int TryActivate(SqliteConnection c,SqliteTransaction t,string shop,long restoreEpoch)
        {
            var rows=c.Query<Row>(@"SELECT f.flow_id Id,f.local_dataset_id Dataset,f.restore_lineage_id Lineage,
f.server_dataset_id ServerDataset,f.receipt_json Receipt,f.receipt_sha256 ReceiptHash,
CASE WHEN s.published_revision=g.revision AND s.checksum=g.checksum THEN 1 ELSE 0 END GenerationCurrent
FROM historical_baseline_flow f JOIN historical_import_dataset d ON d.singleton_id=1 AND d.dataset_id=f.local_dataset_id AND d.data_revision>=f.local_revision
JOIN historical_cloud_state s ON s.shop_id=f.shop_id AND s.dataset_id=f.server_dataset_id
JOIN historical_cloud_generation g ON g.generation_id=s.generation_id AND g.shop_id=s.shop_id AND g.dataset_id=s.dataset_id
WHERE f.shop_id=@shop AND f.state='applied' AND f.restore_epoch=@restoreEpoch AND g.complete=1
AND s.published_revision>=f.server_revision
AND replace(f.restore_lineage_id,'-','')=(SELECT replace(value,'-','') FROM app_settings WHERE key='pos.history.restore_lineage_id') COLLATE NOCASE
AND NOT EXISTS(SELECT 1 FROM historical_baseline_read_mode m WHERE m.shop_id=f.shop_id AND m.local_dataset_id=f.local_dataset_id
 AND m.restore_lineage_id=f.restore_lineage_id AND m.server_dataset_id=f.server_dataset_id)
ORDER BY f.writer_epoch DESC",new{shop,restoreEpoch},t).AsList();
            var changed=0;
            foreach(var row in rows)
            {
                if(row.Receipt==null||Encoding.UTF8.GetByteCount(row.Receipt)>65536)throw new InvalidOperationException("history_baseline_receipt_invalid");
                using var sha=SHA256.Create();var hash=HistoricalPublicationOutbox.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(row.Receipt)));
                if(hash!=row.ReceiptHash)throw new InvalidOperationException("history_baseline_receipt_invalid");
                // An empty delta can reuse older physical rows while advancing
                // the canonical revision. Its old generation cannot satisfy the
                // immutable read-mode trigger. Roll back publication and use the
                // synchronizer's existing bounded full-snapshot recovery.
                if(row.GenerationCurrent!=1)throw new HistoricalCloudResnapshotRequiredException();
                changed+=c.Execute(@"INSERT OR IGNORE INTO historical_baseline_read_mode(shop_id,local_dataset_id,restore_lineage_id,server_dataset_id,flow_id)
VALUES(@shop,@Dataset,@Lineage,@ServerDataset,@Id)",new{shop,row.Dataset,row.Lineage,row.ServerDataset,row.Id},t);
            }
            return changed;
        }
        private sealed class Row
        {
            public string Id{get;set;} public string Dataset{get;set;} public string Lineage{get;set;} public string ServerDataset{get;set;}
            public string Receipt{get;set;} public string ReceiptHash{get;set;}
            public int GenerationCurrent{get;set;}
        }
    }
}
