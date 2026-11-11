using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Core.Reports;

namespace Win7POS.Data.Import
{
    /// <summary>Durable pre-authorization intent in the existing historical lane.
    /// No financial mutation, network request, lease renewal or independent scheduler.</summary>
    public sealed class HistoricalPublicationJournal
    {
        private readonly SqliteConnectionFactory _factory;
        public HistoricalPublicationJournal(SqliteConnectionFactory factory)=>_factory=factory??throw new ArgumentNullException(nameof(factory));

        public async Task<HistoricalPublicationDraft> ReadPendingDraftAsync(string operation,string shop,string device,string serverDataset,long writerEpoch,Action demand,CancellationToken ct)
        {
            if(demand==null)throw new ArgumentNullException(nameof(demand));demand();var authority=_factory.AuthorityStamp;
            Intent row;
            using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))
            {
                row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@operation",new{operation});
                if(row!=null)DemandCurrent(c,null,row.DatasetId,row.RestoreEpoch,row.LineageId,row.BaseRevision);
            }
            if(row==null)return null;
            if(row.ShopId!=shop||row.DeviceId!=device||row.ServerDataset!=serverDataset||row.WriterEpoch!=writerEpoch)
                throw new InvalidOperationException("history_preparation_identity_conflict");
            if(row.State=="aborted"||row.State=="committed"||row.State=="staging")return null;
            var draft=new HistoricalPublicationDraft(row.OperationId,row.Kind,row.ShopId,row.DeviceId,row.DatasetId,row.RestoreEpoch,row.BaseRevision,row.Fingerprint,row.LineageId,row.ContentDigest,row.PageCount,ReadPages(row,ct),ct);
            try
            {
                using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);
                DemandCurrent(c,null,row.DatasetId,row.RestoreEpoch,row.LineageId,row.BaseRevision);
                if(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_outcome WHERE operation_id=@operation AND state IN('prepared','authorized')",new{operation})!=1)
                    throw new InvalidOperationException("history_preparation_identity_conflict");
                demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();return draft;
            }
            catch{draft.Dispose();throw;}
        }

        public async Task<string> SaveIntentAsync(HistoricalPublicationDraft draft,string serverDataset,long writerEpoch,Action demand,CancellationToken ct)
        {
            if(draft==null||demand==null)throw new ArgumentNullException();
            HistoricalReplicaProtocol.DemandId(serverDataset);if(writerEpoch<1)throw new ArgumentOutOfRangeException(nameof(writerEpoch));
            demand();var authority=_factory.AuthorityStamp;
            string nonce;
            using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))
            using(var t=c.BeginTransaction())
            {
            DemandCurrent(c,t,draft.DatasetId,draft.RestoreEpoch,draft.RestoreLineageId,draft.BaseRevision,true);
            var row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@operation",new{operation=draft.OperationId},t);
            if(row!=null)
            {
                if(row.ShopId!=draft.ShopId||row.DeviceId!=draft.DeviceId||row.DatasetId!=draft.DatasetId||row.LineageId!=draft.RestoreLineageId||row.RestoreEpoch!=draft.RestoreEpoch
                    ||row.BaseRevision!=draft.BaseRevision||row.ServerDataset!=serverDataset||row.WriterEpoch!=writerEpoch||row.Kind!=draft.Kind
                    ||row.Fingerprint!=draft.RequestFingerprint||row.ContentDigest!=draft.ContentDigest||row.PageCount!=draft.PageCount||row.State=="aborted")
                    throw new InvalidOperationException("history_preparation_identity_conflict");
                if(row.State=="committed")throw new InvalidOperationException("history_preparation_commit_exists");
                nonce=row.Nonce;
            }
            else
            {
            if(c.ExecuteScalar<long>(@"SELECT COUNT(*) FROM historical_preparation_intent i JOIN historical_preparation_outcome s ON s.operation_id=i.operation_id
WHERE i.shop_id=@ShopId AND i.device_id=@DeviceId AND s.state IN('staging','prepared','authorized')",new{draft.ShopId,draft.DeviceId},t)>=4)
                throw new InvalidOperationException("history_preparation_capacity");
            nonce=Guid.NewGuid().ToString("D");
            c.Execute(@"INSERT INTO historical_preparation_intent(operation_id,local_nonce,shop_id,device_id,local_dataset_id,restore_lineage_id,restore_epoch,base_revision,server_dataset_id,writer_epoch,operation_kind,request_fingerprint,content_digest,page_count,created_at)
VALUES(@OperationId,@nonce,@ShopId,@DeviceId,@DatasetId,@RestoreLineageId,@RestoreEpoch,@BaseRevision,@serverDataset,@writerEpoch,@Kind,@RequestFingerprint,@ContentDigest,@PageCount,@now)",
                new{draft.OperationId,nonce,draft.ShopId,draft.DeviceId,draft.DatasetId,draft.RestoreLineageId,draft.RestoreEpoch,draft.BaseRevision,serverDataset,writerEpoch,draft.Kind,draft.RequestFingerprint,draft.ContentDigest,draft.PageCount,now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},t);
            c.Execute("INSERT INTO historical_preparation_outcome(operation_id,state) VALUES(@OperationId,'staging')",new{draft.OperationId},t);
            }
            demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();t.Commit();
            }
            using var hash=SHA256.Create();var count=0;long bytes=0;
            foreach(var page in draft.Pages(ct))
            {
                demand();DemandFactory(authority);var data=Encoding.UTF8.GetBytes(page.Json);bytes=checked(bytes+data.Length);
                if(++count!=page.Ordinal||bytes>HistoricalPublicationDraft.MaximumContentBytes)throw new InvalidOperationException("history_preparation_content_changed");
                using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))
                using(var t=c.BeginTransaction())
                {
                    DemandCurrent(c,t,draft.DatasetId,draft.RestoreEpoch,draft.RestoreLineageId,draft.BaseRevision,true);
                    var state=c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome WHERE operation_id=@OperationId",new{draft.OperationId},t);
                    if(state!="staging"&&state!="prepared"&&state!="authorized")throw new InvalidOperationException("history_preparation_identity_conflict");
                    var saved=c.QuerySingleOrDefault<PageRow>("SELECT ordinal Ordinal,page_json Json,page_sha256 Digest FROM historical_preparation_page WHERE operation_id=@OperationId AND ordinal=@Ordinal",new{draft.OperationId,page.Ordinal},t);
                    if(saved==null)
                    {
                        if(state!="staging")throw new InvalidOperationException("history_preparation_content_changed");
                        c.Execute("INSERT INTO historical_preparation_page VALUES(@OperationId,@Ordinal,@Json,@Digest)",new{draft.OperationId,page.Ordinal,page.Json,page.Digest},t);
                    }
                    else if(saved.Json!=page.Json||saved.Digest!=page.Digest)throw new InvalidOperationException("history_preparation_content_changed");
                    demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();t.Commit();
                }
                var line=Encoding.ASCII.GetBytes(page.Digest+"\n");hash.TransformBlock(line,0,line.Length,line,0);
                await Task.Yield();
            }
            hash.TransformFinalBlock(Array.Empty<byte>(),0,0);
            if(count!=draft.PageCount||HistoricalPublicationOutbox.Hex(hash.Hash)!=draft.ContentDigest)throw new InvalidOperationException("history_preparation_content_changed");
            using(var c=await _factory.OpenAsync(ct).ConfigureAwait(false))
            using(var t=c.BeginTransaction())
            {
                DemandCurrent(c,t,draft.DatasetId,draft.RestoreEpoch,draft.RestoreLineageId,draft.BaseRevision,true);
                if(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_page WHERE operation_id=@OperationId",new{draft.OperationId},t)!=draft.PageCount)
                    throw new InvalidOperationException("history_preparation_content_changed");
                var state=c.ExecuteScalar<string>("SELECT state FROM historical_preparation_outcome WHERE operation_id=@OperationId",new{draft.OperationId},t);
                if(state=="staging")c.Execute("UPDATE historical_preparation_outcome SET state='prepared' WHERE operation_id=@OperationId",new{draft.OperationId},t);
                else if(state!="prepared"&&state!="authorized")throw new InvalidOperationException("history_preparation_identity_conflict");
                demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();t.Commit();
            }
            return nonce;
        }

        public async Task SaveGrantAsync(string operation,string nonce,HistoricalPublicationProof proof,Action demand,CancellationToken ct)
        {
            if(proof==null||demand==null)throw new ArgumentNullException();proof.DemandValid();demand();var authority=_factory.AuthorityStamp;
            using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            var row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@operation",new{operation},t);
            if(row==null||row.Nonce!=nonce||(row.State!="prepared"&&row.State!="authorized"&&row.State!="committed")||proof.OperationId!=Guid.Parse(operation).ToString("D")||proof.ShopId!=row.ShopId||proof.DeviceId!=row.DeviceId
                ||proof.DatasetId!=row.DatasetId||proof.RestoreLineageId!=row.LineageId||proof.RestoreEpoch!=row.RestoreEpoch||proof.ServerDatasetId!=row.ServerDataset
                ||proof.WriterEpoch!=row.WriterEpoch||proof.RequestFingerprint!=row.Fingerprint||proof.ContentDigest!=row.ContentDigest)
                throw new InvalidOperationException("history_preparation_proof_mismatch");
            DemandCurrent(c,t,row.DatasetId,row.RestoreEpoch,row.LineageId,row.BaseRevision,row.State!="committed");var json=HistoricalPublicationOutbox.Serialize(proof);
            if(row.Proof!=null&&row.Proof!=json)throw new InvalidOperationException("history_preparation_proof_mismatch");
            if(row.State!="committed")c.Execute("UPDATE historical_preparation_outcome SET state='authorized',proof_json=@json WHERE operation_id=@operation",new{json,operation},t);
            demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();t.Commit();
        }

        // Local tombstone only: a server abort additionally needs its own
        // authenticated reconciliation protocol. No absent:true assertion is sent.
        public async Task AbortWithoutCommitAsync(string operation,string nonce,string shop,string device,Action demand,CancellationToken ct)
        {
            if(demand==null)throw new ArgumentNullException(nameof(demand));demand();var authority=_factory.AuthorityStamp;
            using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            var row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@operation",new{operation},t);
            if(row==null||row.Nonce!=nonce||row.ShopId!=shop||row.DeviceId!=device)throw new InvalidOperationException("history_preparation_identity_conflict");
            DemandCurrent(c,t,row.DatasetId,row.RestoreEpoch,row.LineageId,row.BaseRevision);
            if(row.State=="committed"||c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation WHERE operation_id=@operation",new{operation},t)!=0
                ||c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox WHERE operation_id=@operation",new{operation},t)!=0)
                throw new InvalidOperationException("history_preparation_commit_exists");
            if(row.State!="aborted")c.Execute("UPDATE historical_preparation_outcome SET state='aborted' WHERE operation_id=@operation",new{operation},t);
            demand();DemandFactory(authority);ct.ThrowIfCancellationRequested();t.Commit();
        }

        internal static void CommitIntent(SqliteConnection c,SqliteTransaction t,HistoricalPublicationDraft draft,HistoricalPublicationProof proof,CancellationToken ct)
        {
            var row=c.QuerySingleOrDefault<Intent>(Select+" WHERE i.operation_id=@OperationId",new{draft.OperationId},t);
            if(row==null){if(proof!=null)throw new InvalidOperationException("history_preparation_required");return;}
            if(proof==null||row.State!="authorized"||row.Proof!=HistoricalPublicationOutbox.Serialize(proof)||row.ContentDigest!=draft.ContentDigest)
                throw new InvalidOperationException("history_preparation_proof_mismatch");
            if(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_page WHERE operation_id=@OperationId",new{draft.OperationId},t)!=row.PageCount
                ||c.ExecuteScalar<long>(@"SELECT COUNT(*) FROM historical_preparation_page j LEFT JOIN historical_publication_page p ON p.operation_id=j.operation_id AND p.ordinal=j.ordinal
WHERE j.operation_id=@OperationId AND (p.page_json IS NOT j.page_json OR p.page_sha256 IS NOT j.page_sha256)",new{draft.OperationId},t)!=0)
                throw new InvalidOperationException("history_preparation_content_changed");
            c.Execute("UPDATE historical_preparation_outcome SET state='committed' WHERE operation_id=@OperationId",new{draft.OperationId},t);
            // Transfer exact bytes to the permanent outbox within the ledger TX.
            c.Execute("DELETE FROM historical_preparation_page WHERE operation_id=@OperationId",new{draft.OperationId},t);
        }
        private void DemandFactory(SalesReportAuthorityStamp captured)
        {if(_factory.AuthorityStamp.AuthorityInstanceEpoch!=captured.AuthorityInstanceEpoch||_factory.AuthorityStamp.RestoreEpoch!=captured.RestoreEpoch)throw new InvalidOperationException("history_preparation_authority_changed");}
        private void DemandCurrent(SqliteConnection c,SqliteTransaction t,string dataset,long restore,string lineage,long revision,bool exactRevision=false)
        {
            if(c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_dataset WHERE singleton_id=1 AND dataset_id=@dataset AND data_revision>=@revision",new{dataset,revision},t)!=1
                ||exactRevision&&c.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1",transaction:t)!=revision
                ||HistoricalPublicationDraft.NormalizeLineage(c.ExecuteScalar<string>("SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id'",transaction:t))!=lineage
                ||_factory.AuthorityStamp.RestoreEpoch!=restore)
                throw new InvalidOperationException("history_preparation_context_changed");
        }
        private System.Collections.Generic.IEnumerable<HistoricalPublicationDraft.Page> ReadPages(Intent row,CancellationToken ct)
        {
            for(var ordinal=1;ordinal<=row.PageCount;ordinal++)
            {
                ct.ThrowIfCancellationRequested();PageRow saved;
                using(var c=_factory.Open())saved=c.QuerySingleOrDefault<PageRow>("SELECT ordinal Ordinal,page_json Json,page_sha256 Digest FROM historical_preparation_page WHERE operation_id=@OperationId AND ordinal=@ordinal",new{row.OperationId,ordinal});
                if(saved==null)throw new InvalidOperationException("history_preparation_content_changed");
                var page=new HistoricalPublicationDraft.Page(saved.Ordinal,saved.Json);
                if(page.Digest!=saved.Digest)throw new InvalidOperationException("history_preparation_content_changed");
                // The SQLite connection is closed before yielding/spool I/O.
                yield return page;
            }
        }
        private const string Select=@"SELECT i.operation_id OperationId,i.local_nonce Nonce,i.shop_id ShopId,i.device_id DeviceId,i.local_dataset_id DatasetId,
i.restore_lineage_id LineageId,i.restore_epoch RestoreEpoch,i.base_revision BaseRevision,i.server_dataset_id ServerDataset,i.writer_epoch WriterEpoch,
i.operation_kind Kind,i.request_fingerprint Fingerprint,i.content_digest ContentDigest,i.page_count PageCount,s.state State,s.proof_json Proof
FROM historical_preparation_intent i JOIN historical_preparation_outcome s ON s.operation_id=i.operation_id";
        private sealed class Intent
        {
            public string OperationId{get;set;} public string Nonce{get;set;} public string ShopId{get;set;} public string DeviceId{get;set;} public string DatasetId{get;set;}
            public string LineageId{get;set;} public long RestoreEpoch{get;set;} public long BaseRevision{get;set;} public string ServerDataset{get;set;} public long WriterEpoch{get;set;}
            public string Kind{get;set;} public string Fingerprint{get;set;} public string ContentDigest{get;set;} public int PageCount{get;set;} public string State{get;set;} public string Proof{get;set;}
        }
        private sealed class PageRow{public int Ordinal{get;set;} public string Json{get;set;} public string Digest{get;set;}}
    }
}
