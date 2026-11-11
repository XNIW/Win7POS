using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalBaselineFlow
    {
        public string FlowId{get;set;} public string ShopId{get;set;} public string DeviceId{get;set;} public string StaffId{get;set;}
        public string LocalDatasetId{get;set;} public string RestoreLineageId{get;set;} public long RestoreEpoch{get;set;} public long LocalRevision{get;set;}
        public string ServerDatasetId{get;set;} public long WriterEpoch{get;set;} public string ArchivePath{get;set;} public string BackupPath{get;set;}
        public string ArchiveSha256{get;set;} public string ManifestSha256{get;set;} public string BackupSha256{get;set;} public string ManifestJson{get;set;}
        public string StartRequestId{get;set;} public string ApplyRequestId{get;set;} public string State{get;set;} public string UploadId{get;set;}
        public string RunId{get;set;} public string ReviewDigest{get;set;} public string ReceiptJson{get;set;} public string ReasonCode{get;set;}
        public string RunSessionId{get;set;} public int? RunCredentialVersion{get;set;}
    }

    /// <summary>Durable transfer identity and original acknowledgements. No HTTP or ledger mutation.</summary>
    public sealed partial class HistoricalBaselineStore
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly OnlineSyncGeneration _generation;
        private readonly Action _demand;
        public HistoricalBaselineStore(SqliteConnectionFactory factory,OnlineSyncGeneration generation,Action demand)
        {
            _factory=factory??throw new ArgumentNullException(nameof(factory));_generation=generation??throw new ArgumentNullException(nameof(generation));
            if(demand==null)throw new ArgumentNullException(nameof(demand));var expected=_factory.AuthorityStamp;
            _demand=()=>{demand();var current=_factory.AuthorityStamp;if(current.RestoreEpoch!=expected.RestoreEpoch||current.AuthorityInstanceEpoch!=expected.AuthorityInstanceEpoch)throw new OnlineSyncGenerationChangedException();};
        }
        private const string Select=@"SELECT flow_id FlowId,shop_id ShopId,device_id DeviceId,staff_id StaffId,local_dataset_id LocalDatasetId,
restore_lineage_id RestoreLineageId,restore_epoch RestoreEpoch,local_revision LocalRevision,server_dataset_id ServerDatasetId,writer_epoch WriterEpoch,
archive_path ArchivePath,backup_path BackupPath,archive_sha256 ArchiveSha256,manifest_sha256 ManifestSha256,backup_sha256 BackupSha256,manifest_json ManifestJson,
start_request_id StartRequestId,apply_request_id ApplyRequestId,state State,upload_id UploadId,run_id RunId,run_session_id RunSessionId,run_credential_version RunCredentialVersion,review_digest ReviewDigest,receipt_json ReceiptJson,reason_code ReasonCode
FROM historical_baseline_flow ";

        public async Task<HistoricalBaselineFlow> ReadAsync(string serverDataset,long writerEpoch,CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(serverDataset);if(writerEpoch<1)throw new ArgumentOutOfRangeException(nameof(writerEpoch));
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction(deferred:true);
            await DemandGeneration(c,t,ct).ConfigureAwait(false);
            var flow=c.QuerySingleOrDefault<HistoricalBaselineFlow>(Select+@"WHERE shop_id=@shop AND device_id=@device AND server_dataset_id=@serverDataset AND writer_epoch=@writerEpoch
AND local_dataset_id=(SELECT dataset_id FROM historical_import_dataset WHERE singleton_id=1)
AND replace(restore_lineage_id,'-','')=(SELECT replace(value,'-','') FROM app_settings WHERE key='pos.history.restore_lineage_id') COLLATE NOCASE",
                new{shop=_generation.ShopId,device=_generation.ShopDeviceId,serverDataset,writerEpoch},t);
            if(flow!=null)DemandFlow(c,t,flow,false);_demand();t.Commit();return flow;
        }

        public async Task<HistoricalBaselineFlow> SavePreparedAsync(HistoricalCloudExportReceipt receipt,string serverDataset,long writerEpoch,HistoricalCloudExportGuard guard,CancellationToken ct)
        {
            if(receipt==null||guard==null)throw new ArgumentNullException();HistoricalReplicaProtocol.DemandId(serverDataset);HistoricalReplicaProtocol.DemandId(_generation.StaffId);
            guard.DemandStillValid();var m=receipt.Manifest;
            if(m==null||m.SchemaVersion!="history-local-export-v2"||m.EvidenceLevel!="legacy_attested"||m.ShopId!=_generation.ShopId||m.DeviceId!=_generation.ShopDeviceId||
                m.ShopId!=guard.ShopId||m.DeviceId!=guard.DeviceId||m.LocalDatasetId!=guard.DatasetId||m.DataRevision!=S(guard.DataRevision)||m.RestoreEpoch!=S(guard.RestoreEpoch)||writerEpoch<1)
                throw new InvalidOperationException("history_baseline_scope_changed");
            HistoricalReplicaProtocol.DemandId(m.RestoreLineageId);
            var flow=new HistoricalBaselineFlow{FlowId=Guid.NewGuid().ToString("D"),ShopId=m.ShopId,DeviceId=m.DeviceId,StaffId=_generation.StaffId,
                LocalDatasetId=m.LocalDatasetId,RestoreLineageId=m.RestoreLineageId,RestoreEpoch=guard.RestoreEpoch,LocalRevision=guard.DataRevision,
                ServerDatasetId=serverDataset,WriterEpoch=writerEpoch,ArchivePath=receipt.ArchivePath,BackupPath=receipt.BackupPath,ArchiveSha256=receipt.ArchiveSha256,
                ManifestSha256=receipt.ManifestSha256,BackupSha256=m.BackupSha256,ManifestJson=Serialize(m),StartRequestId=Guid.NewGuid().ToString("D"),ApplyRequestId=Guid.NewGuid().ToString("D"),State="prepared"};
            await Task.Run(()=>VerifyFiles(flow,ct),ct).ConfigureAwait(false);
            guard.DemandStillValid();_demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);DemandFlow(c,t,flow,true);
            c.Execute(@"INSERT INTO historical_baseline_flow(flow_id,shop_id,device_id,staff_id,local_dataset_id,restore_lineage_id,restore_epoch,local_revision,server_dataset_id,writer_epoch,
archive_path,backup_path,archive_sha256,manifest_sha256,backup_sha256,manifest_json,start_request_id,apply_request_id,state)
VALUES(@FlowId,@ShopId,@DeviceId,@StaffId,@LocalDatasetId,@RestoreLineageId,@RestoreEpoch,@LocalRevision,@ServerDatasetId,@WriterEpoch,@ArchivePath,@BackupPath,
@ArchiveSha256,@ManifestSha256,@BackupSha256,@ManifestJson,@StartRequestId,@ApplyRequestId,'prepared')",flow,t);
            guard.DemandStillValid();_demand();t.Commit();return flow;
        }

        public Task SaveUploadAsync(string flow,string upload,CancellationToken ct)
        {HistoricalReplicaProtocol.DemandId(upload);return Change(flow,"prepared","state='upload_created',upload_id=@value",upload,ct);}
        public Task SaveReadyAsync(string flow,CancellationToken ct)=>Change(flow,"upload_created","state='upload_ready'",null,ct);
        public Task SaveRunAsync(string flow,string run,CancellationToken ct)
        {HistoricalReplicaProtocol.DemandId(run);return Change(flow,"upload_ready","state='run_started',run_id=@value,run_session_id=@session,run_credential_version=@version",run,ct);}
        public Task SealReviewAsync(string flow,string digest,CancellationToken ct)
        {HistoricalReplicaProtocol.DemandHash(digest);return Change(flow,"run_started","review_digest=@value",digest,ct);}

        public async Task MarkAttentionAsync(string id,string reason,CancellationToken ct)
        {
            if(reason!="baseline_upload_rejected"&&reason!="baseline_job_failed"&&reason!="baseline_review_denied"&&reason!="baseline_unknown_outcome"&&reason!="baseline_context_changed")throw new ArgumentException(nameof(reason));
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);var f=ReadFlow(c,t,id);DemandFlow(c,t,f,false);
            if(f.State=="needs_attention")return;if(f.State=="applied")throw new InvalidOperationException("history_baseline_phase_invalid");
            c.Execute("UPDATE historical_baseline_flow SET state='needs_attention',reason_code=@reason WHERE flow_id=@id",new{id,reason},t);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();
        }

        public async Task SaveReceiptAsync(string id,string json,CancellationToken ct)
        {
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);var f=ReadFlow(c,t,id);DemandFlow(c,t,f,false);
            ApplyReceipt(c,t,f,json);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();
        }
        private void ApplyReceipt(SqliteConnection c,SqliteTransaction t,HistoricalBaselineFlow f,string json)
        {
            var receipt=HistoricalBaselineReceipt.Read(json,f);var id=f.FlowId;
            if(f.State=="applied")
            {if(f.ReceiptJson!=json)throw new InvalidOperationException("history_baseline_receipt_changed");return;}
            if(f.State!="run_started"||f.ReviewDigest==null)throw new InvalidOperationException("history_baseline_phase_invalid");
            c.Execute(@"UPDATE historical_baseline_flow SET state='applied',receipt_json=@json,receipt_sha256=@hash,source_snapshot_id=@snapshot,server_revision=@revision WHERE flow_id=@id",
                new{id,json,hash=Hash(Encoding.UTF8.GetBytes(json)),snapshot=receipt.Snapshot,revision=receipt.Revision},t);
        }
        private async Task Change(string id,string state,string update,string value,CancellationToken ct)
        {
            _demand();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await DemandGeneration(c,t,ct).ConfigureAwait(false);var f=ReadFlow(c,t,id);DemandFlow(c,t,f,true);
            if(f.State!=state)throw new InvalidOperationException("history_baseline_phase_invalid");
            if(update=="review_digest=@value"&&(f.RunSessionId!=_generation.PosSessionId||f.RunCredentialVersion!=_generation.StaffCredentialVersion))
                throw new InvalidOperationException("history_baseline_session_changed");
            if(update=="review_digest=@value"&&f.ReviewDigest!=null)
            {if(f.ReviewDigest!=value)throw new InvalidOperationException("history_baseline_review_changed");_demand();ct.ThrowIfCancellationRequested();return;}
            c.Execute("UPDATE historical_baseline_flow SET "+update+" WHERE flow_id=@id",new{id,value,session=_generation.PosSessionId,version=_generation.StaffCredentialVersion},t);
            _demand();ct.ThrowIfCancellationRequested();t.Commit();
        }
        private HistoricalBaselineFlow ReadFlow(SqliteConnection c,SqliteTransaction t,string id)
        {HistoricalReplicaProtocol.DemandId(id);return c.QuerySingleOrDefault<HistoricalBaselineFlow>(Select+"WHERE flow_id=@id",new{id},t)??throw new InvalidOperationException("history_baseline_intent_missing");}
        private async Task DemandGeneration(SqliteConnection c,SqliteTransaction t,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();_demand();if(!await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(c,t,_generation).ConfigureAwait(false))throw new OnlineSyncGenerationChangedException();}
        private void DemandFlow(SqliteConnection c,SqliteTransaction t,HistoricalBaselineFlow f,bool exactRevision)
        {
            if(f.ShopId!=_generation.ShopId||f.DeviceId!=_generation.ShopDeviceId||f.StaffId!=_generation.StaffId)throw new InvalidOperationException("history_baseline_original_actor_required");
            var d=c.QuerySingle<HistoricalSalesDatasetState>("SELECT dataset_id DatasetId,data_revision DataRevision,(SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id') RestoreLineageId FROM historical_import_dataset WHERE singleton_id=1",transaction:t);
            if(d.DatasetId!=f.LocalDatasetId||HistoricalPublicationDraft.NormalizeLineage(d.RestoreLineageId)!=f.RestoreLineageId||_factory.AuthorityStamp.RestoreEpoch!=f.RestoreEpoch||
                (exactRevision?d.DataRevision!=f.LocalRevision:d.DataRevision<f.LocalRevision))throw new InvalidOperationException("history_baseline_scope_changed");
        }
        internal static void VerifyFiles(HistoricalBaselineFlow flow,CancellationToken ct)
        {
            foreach(var path in new[]{flow.ArchivePath,flow.BackupPath})
            {
                if(!Path.IsPathRooted(path)||Path.GetFullPath(path)!=path||path.StartsWith(@"\\",StringComparison.Ordinal))throw new InvalidOperationException("history_baseline_source_changed");
                PosDatabasePathIdentity.RejectReparseComponents(path);
            }
            ct.ThrowIfCancellationRequested();using var archive=new FileStream(flow.ArchivePath,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var backup=new FileStream(flow.BackupPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(archive.Length<1||archive.Length>HistoricalCloudExporter.MaximumArchiveBytes||Hash(archive)!=flow.ArchiveSha256||Hash(backup)!=flow.BackupSha256)throw new InvalidOperationException("history_baseline_source_changed");
            archive.Position=0;using var zip=new ZipArchive(archive,ZipArchiveMode.Read,true);var entry=zip.GetEntry("manifest.json");
            if(entry==null||entry.Length<1||entry.Length>65536)throw new InvalidOperationException("history_baseline_source_changed");
            using var input=entry.Open();using var bytes=new MemoryStream();input.CopyTo(bytes);
            if(Hash(bytes.ToArray())!=flow.ManifestSha256)throw new InvalidOperationException("history_baseline_source_changed");
            bytes.Position=0;var manifest=(HistoricalCloudExportManifest)new DataContractJsonSerializer(typeof(HistoricalCloudExportManifest)).ReadObject(bytes);
            if(Serialize(manifest)!=flow.ManifestJson||manifest.BackupByteLength!=S(backup.Length)||manifest.BackupSha256!=flow.BackupSha256)throw new InvalidOperationException("history_baseline_source_changed");
            ct.ThrowIfCancellationRequested();
        }
        private static string Serialize(HistoricalCloudExportManifest m){using var b=new MemoryStream();new DataContractJsonSerializer(typeof(HistoricalCloudExportManifest)).WriteObject(b,m);return Encoding.UTF8.GetString(b.ToArray());}
        private static string Hash(Stream value){using var sha=SHA256.Create();return HistoricalPublicationOutbox.Hex(sha.ComputeHash(value));}
        private static string Hash(byte[] value){using var sha=SHA256.Create();return HistoricalPublicationOutbox.Hex(sha.ComputeHash(value));}
        private static string S(long n)=>n.ToString(CultureInfo.InvariantCulture);
    }
}
