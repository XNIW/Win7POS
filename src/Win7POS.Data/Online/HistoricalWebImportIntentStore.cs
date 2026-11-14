using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Online
{
    /// <summary>Only durable control metadata. Never tokens, URLs, workbook rows or automatic commits.</summary>
    public sealed class HistoricalWebImportIntent
    {
        internal HistoricalWebImportIntent(string key,string actor,string dataset,long epoch,string analysisKey,string job,string[] uploads,string[] sources,
            string config,string fingerprint,string[] evidence,string review,string commitKey,bool attempted,string receipt)
        {Key=key;ActorFingerprint=actor;DatasetId=dataset;WriterEpoch=epoch;AnalysisRequestKey=analysisKey;JobId=job;UploadIds=new ReadOnlyCollection<string>(uploads.ToArray());
            ReviewDigest=review;CommitRequestKey=commitKey;CommitAttempted=attempted;ReceiptOperationId=receipt;
            FileSha256s=new ReadOnlyCollection<string>(sources.ToArray());ConfigDigest=config;ResolverFingerprint=fingerprint;EvidenceIds=new ReadOnlyCollection<string>(evidence.ToArray());}
        public string Key { get; } public string ActorFingerprint { get; } public string DatasetId { get; } public long WriterEpoch { get; }
        public string AnalysisRequestKey { get; } public string JobId { get; } public IReadOnlyList<string> UploadIds { get; }
        public string ReviewDigest { get; } public string CommitRequestKey { get; } public bool CommitAttempted { get; } public string ReceiptOperationId { get; }
        public IReadOnlyList<string> FileSha256s { get; } public string ConfigDigest { get; } public string ResolverFingerprint { get; } public IReadOnlyList<string> EvidenceIds { get; }
    }
    public sealed class HistoricalWebImportIntentStore
    {
        private readonly SqliteConnectionFactory _factory;
        public HistoricalWebImportIntentStore(SqliteConnectionFactory factory)=>_factory=factory??throw new ArgumentNullException(nameof(factory));
        public Task<HistoricalWebImportIntent> GetOrCreateAsync(string key,string actor,string dataset,long epoch,IReadOnlyList<string> files,string config,string fingerprint,CancellationToken ct)
        {
            if(!HistoricalWebImportResponseMapper.Hash(key)||!HistoricalWebImportResponseMapper.Hash(actor)||!HistoricalWebImportResponseMapper.Id(dataset)||epoch<1||
                files==null||files.Count<1||files.Count>10||files.Any(x=>!HistoricalWebImportResponseMapper.Hash(x))||files.Distinct(StringComparer.Ordinal).Count()!=files.Count||
                !HistoricalWebImportResponseMapper.Hash(config)||!HistoricalWebImportResponseMapper.Hash(fingerprint))throw Invalid();
            return ChangeAsync(key,r=>
            {
                if(r==null)return new Record{Actor=actor,Dataset=dataset,Epoch=epoch,AnalysisKey=Guid.NewGuid().ToString("D"),Uploads=new string[files.Count],Sources=files.ToArray(),Config=config,Fingerprint=fingerprint,Evidence=Array.Empty<string>()};
                if(r.Actor!=actor||r.Dataset!=dataset||r.Epoch!=epoch||!r.Sources.SequenceEqual(files))throw Invalid();
                // A pending financial request is never replaced by another key.
                // After an observed receipt, an explicit new preparation may
                // review the same files again (including a later batch undo).
                if(r.Receipt!=null||!r.Attempted&&(r.Config!=config||r.Fingerprint!=fingerprint))
                {r.AnalysisKey=Guid.NewGuid().ToString("D");r.Job=null;r.Review=null;r.CommitKey=null;r.Attempted=false;r.Receipt=null;r.Evidence=Array.Empty<string>();r.Config=config;r.Fingerprint=fingerprint;}
                return r;
            },ct);
        }
        public Task<HistoricalWebImportIntent> ReadAsync(string key,CancellationToken ct)=>ChangeAsync(key,r=>r??throw Invalid(),ct,readOnly:true);
        public Task<HistoricalWebImportIntent> SetUploadAsync(string key,int index,string upload,CancellationToken ct)
            =>ChangeAsync(key,r=>{if(r==null||index<0||index>=r.Uploads.Length||!HistoricalWebImportResponseMapper.Id(upload)||r.Uploads[index]!=null&&r.Uploads[index]!=upload)throw Invalid();r.Uploads[index]=upload;return r;},ct);
        public Task<HistoricalWebImportIntent> SetJobAsync(string key,string job,CancellationToken ct)
            =>ChangeAsync(key,r=>{if(r==null||!HistoricalWebImportResponseMapper.Id(job)||r.Job!=null&&r.Job!=job)throw Invalid();r.Job=job;return r;},ct);
        public Task<HistoricalWebImportIntent> SetReviewAsync(string key,string digest,IReadOnlyList<string> evidence,CancellationToken ct)
            =>ChangeAsync(key,r=>
            {
                if(r==null||r.Job==null||!HistoricalWebImportResponseMapper.Hash(digest)||evidence==null||evidence.Count>200||evidence.Any(x=>!HistoricalWebImportResponseMapper.Id(x))||evidence.Distinct(StringComparer.Ordinal).Count()!=evidence.Count)throw Invalid();
                if(r.Review!=digest)
                {
                    if(r.Attempted)throw new InvalidOperationException("history_web_commit_recovery_required");
                    r.Review=digest;r.CommitKey=Guid.NewGuid().ToString("D");r.Evidence=evidence.ToArray();
                }
                else if(!r.Evidence.SequenceEqual(evidence))throw Invalid();
                return r;
            },ct);
        public Task<HistoricalWebImportIntent> BeginCommitAsync(string key,string job,string digest,string request,CancellationToken ct)
            =>ChangeAsync(key,r=>{if(r==null||r.Job!=job||r.Review!=digest||r.CommitKey!=request)throw Invalid();r.Attempted=true;return r;},ct);
        public Task<HistoricalWebImportIntent> CompleteAsync(string key,string request,string operation,CancellationToken ct)
            =>ChangeAsync(key,r=>{if(r==null||r.CommitKey!=request||!r.Attempted||!HistoricalWebImportResponseMapper.Id(operation)||r.Receipt!=null&&r.Receipt!=operation)throw Invalid();r.Receipt=operation;return r;},ct);
        private async Task<HistoricalWebImportIntent> ChangeAsync(string key,Func<Record,Record> change,CancellationToken ct,bool readOnly=false)
        {
            if(!HistoricalWebImportResponseMapper.Hash(key))throw Invalid();
            using var connection=await _factory.OpenAsync(ct).ConfigureAwait(false);using var transaction=connection.BeginTransaction();
            var setting="pos.history.web-import.v1:"+key;
            var raw=await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT value FROM app_settings WHERE key=@setting",new{setting},transaction,cancellationToken:ct)).ConfigureAwait(false);
            Record old=null;
            if(raw!=null)
            {
                if(raw.Length>16384)throw Invalid();
                try{using var bytes=new MemoryStream(Encoding.UTF8.GetBytes(raw));old=(Record)new DataContractJsonSerializer(typeof(Record)).ReadObject(bytes);}
                catch(SerializationException){throw Invalid();}
                Validate(old);
            }
            var record=change(old);Validate(record);
            if(!readOnly)
            {
                using var memory=new MemoryStream();new DataContractJsonSerializer(typeof(Record)).WriteObject(memory,record);
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO app_settings(key,value) VALUES(@setting,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    new{setting,value=Encoding.UTF8.GetString(memory.ToArray())},transaction,cancellationToken:ct)).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();transaction.Commit();
            return new HistoricalWebImportIntent(key,record.Actor,record.Dataset,record.Epoch,record.AnalysisKey,record.Job,record.Uploads,record.Sources,record.Config,record.Fingerprint,record.Evidence,record.Review,record.CommitKey,record.Attempted,record.Receipt);
        }
        private static void Validate(Record r)
        {
            if(r==null||r.Version!=1||!HistoricalWebImportResponseMapper.Hash(r.Actor)||!HistoricalWebImportResponseMapper.Id(r.Dataset)||r.Epoch<1||
                !HistoricalWebImportResponseMapper.Id(r.AnalysisKey)||r.Uploads==null||r.Uploads.Length<1||r.Uploads.Length>10||r.Uploads.Any(x=>x!=null&&!HistoricalWebImportResponseMapper.Id(x))||
                r.Sources==null||r.Sources.Length!=r.Uploads.Length||r.Sources.Any(x=>!HistoricalWebImportResponseMapper.Hash(x))||r.Sources.Distinct(StringComparer.Ordinal).Count()!=r.Sources.Length||
                !HistoricalWebImportResponseMapper.Hash(r.Config)||!HistoricalWebImportResponseMapper.Hash(r.Fingerprint)||r.Evidence==null||r.Evidence.Length>200||r.Evidence.Any(x=>!HistoricalWebImportResponseMapper.Id(x))||r.Evidence.Distinct(StringComparer.Ordinal).Count()!=r.Evidence.Length||
                r.Job!=null&&!HistoricalWebImportResponseMapper.Id(r.Job)||r.Review!=null&&!HistoricalWebImportResponseMapper.Hash(r.Review)||r.CommitKey!=null&&!HistoricalWebImportResponseMapper.Id(r.CommitKey)||
                (r.Review==null)!=(r.CommitKey==null)||r.Attempted&&r.CommitKey==null||r.Receipt!=null&&(!r.Attempted||!HistoricalWebImportResponseMapper.Id(r.Receipt)))throw Invalid();
        }
        private static InvalidOperationException Invalid()=>new InvalidOperationException("history_web_intent_invalid");
        [DataContract]private sealed class Record
        {
            [DataMember]public int Version=1;[DataMember]public string Actor;[DataMember]public string Dataset;[DataMember]public long Epoch;
            [DataMember]public string AnalysisKey;[DataMember]public string[] Uploads;[DataMember]public string Job;[DataMember]public string Review;
            [DataMember]public string[] Sources;[DataMember]public string Config;[DataMember]public string Fingerprint;[DataMember]public string[] Evidence;
            [DataMember]public string CommitKey;[DataMember]public bool Attempted;[DataMember]public string Receipt;
        }
    }
}
