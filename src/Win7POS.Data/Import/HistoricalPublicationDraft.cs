using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    /// <summary>
    /// Immutable, bounded wire pages of one requested historical operation.
    /// The same pages are hashed before the server grant and persisted in the
    /// ledger transaction. No credential, workbook path or native sale is included.
    /// </summary>
    public sealed class HistoricalPublicationDraft : IDisposable
    {
        public const int ProtocolVersion=2,MaximumPageBytes=256*1024,MaximumRecordBytes=16384;
        public const long MaximumContentBytes=256L*1024*1024;
        private readonly object _spoolGate=new object();
        private readonly FileStream _spool;
        private readonly List<long> _pageOffsets=new List<long>();
        private bool _disposed;
        private HistoricalPublicationDraft(string operation,string kind,string shop,string device,string dataset,long restore,long revision,string fingerprint,string lineage,Func<IEnumerable<Record>> records,CancellationToken ct=default)
        {
            OperationId=operation;Kind=kind;ShopId=shop;DeviceId=device;DatasetId=dataset;
            RestoreEpoch=restore;BaseRevision=revision;RequestFingerprint=fingerprint;RestoreLineageId=NormalizeLineage(lineage);
            _spool=new FileStream(Path.Combine(Path.GetTempPath(),"W7H."+Guid.NewGuid().ToString("N")),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,32768,FileOptions.DeleteOnClose|FileOptions.SequentialScan);
            long bytes=0;var count=0;
            try
            {
                using var hash=SHA256.Create();
                using var writer=new BinaryWriter(_spool,new UTF8Encoding(false),true);
                foreach(var page in BuildPages(records(),ct))
                {
                    var encoded=Encoding.UTF8.GetBytes(page.Json);bytes=checked(bytes+encoded.Length);
                    if(bytes>MaximumContentBytes||++count>20000)throw new InvalidOperationException("history_publication_capacity");
                    _pageOffsets.Add(_spool.Position);writer.Write(encoded.Length);writer.Write(encoded);
                    var line=Encoding.ASCII.GetBytes(page.Digest+"\n");hash.TransformBlock(line,0,line.Length,line,0);
                }
                writer.Flush();_spool.Flush(true);
                hash.TransformFinalBlock(Array.Empty<byte>(),0,0);ContentDigest=Hex(hash.Hash);PageCount=count;
                if(count==0)throw new InvalidOperationException("history_publication_content_required");
            }
            catch{_spool.Dispose();throw;}
        }
        public string OperationId{get;} public string Kind{get;} public string ShopId{get;} public string DeviceId{get;}
        public string DatasetId{get;} public long RestoreEpoch{get;} public long BaseRevision{get;}
        public string RestoreLineageId{get;}
        public string RequestFingerprint{get;} public string ContentDigest{get;} public int PageCount{get;}
        internal HistoricalPublicationDraft(string operation,string kind,string shop,string device,string dataset,long restore,long revision,string fingerprint,string lineage,
            string expectedRoot,int expectedPages,IEnumerable<Page> persisted,CancellationToken ct)
        {
            OperationId=operation;Kind=kind;ShopId=shop;DeviceId=device;DatasetId=dataset;RestoreEpoch=restore;BaseRevision=revision;
            RequestFingerprint=fingerprint;RestoreLineageId=NormalizeLineage(lineage);
            _spool=new FileStream(Path.Combine(Path.GetTempPath(),"W7H."+Guid.NewGuid().ToString("N")),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,32768,FileOptions.DeleteOnClose|FileOptions.SequentialScan);
            try
            {
                using var hash=SHA256.Create();using var writer=new BinaryWriter(_spool,new UTF8Encoding(false),true);long bytes=0;var count=0;
                foreach(var page in persisted)
                {
                    ct.ThrowIfCancellationRequested();var data=Encoding.UTF8.GetBytes(page.Json);bytes=checked(bytes+data.Length);
                    if(++count!=page.Ordinal||count>20000||data.Length<2||data.Length>MaximumPageBytes||bytes>MaximumContentBytes)
                        throw new InvalidOperationException("history_preparation_content_changed");
                    _pageOffsets.Add(_spool.Position);writer.Write(data.Length);writer.Write(data);
                    var line=Encoding.ASCII.GetBytes(page.Digest+"\n");hash.TransformBlock(line,0,line.Length,line,0);
                }
                writer.Flush();_spool.Flush(true);hash.TransformFinalBlock(Array.Empty<byte>(),0,0);ContentDigest=Hex(hash.Hash);PageCount=count;
                if(count!=expectedPages||count<1||ContentDigest!=expectedRoot)throw new InvalidOperationException("history_preparation_content_changed");
            }
            catch{_spool.Dispose();throw;}
        }
        public sealed class Page
        {
            internal Page(int ordinal,string json){Ordinal=ordinal;Json=json;using var h=SHA256.Create();Digest=Hex(h.ComputeHash(Encoding.UTF8.GetBytes(json)));}
            public int Ordinal{get;} public string Json{get;} public string Digest{get;}
        }
        [DataContract]
        private sealed class Record
        {
            [DataMember(Name="kind",Order=0)] public string Kind;
            [DataMember(Name="fields",Order=1)] public Dictionary<string,string> Fields;
        }
        public IEnumerable<Page> Pages(CancellationToken ct)
        {
            for(var ordinal=1;ordinal<=PageCount;ordinal++)
                yield return ReadPage(ordinal,ct);
        }
        public Page ReadPage(int ordinal,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();byte[] bytes;
            lock(_spoolGate)
            {
                if(_disposed)throw new ObjectDisposedException(nameof(HistoricalPublicationDraft));
                if(ordinal<1||ordinal>PageCount)throw new ArgumentOutOfRangeException(nameof(ordinal));
                _spool.Position=_pageOffsets[ordinal-1];
                using var reader=new BinaryReader(_spool,new UTF8Encoding(false,true),true);
                var length=reader.ReadInt32();
                if(length<2||length>MaximumPageBytes)throw new InvalidOperationException("history_publication_content_changed");
                bytes=reader.ReadBytes(length);if(bytes.Length!=length)throw new InvalidOperationException("history_publication_content_changed");
            }
            // Bounded offset index permits resume without rereading prior pages.
            // No monitor/file lock spans decoding, a yield or the caller's HTTP.
            return new Page(ordinal,new UTF8Encoding(false,true).GetString(bytes));
        }
        public void Dispose(){lock(_spoolGate){if(_disposed)return;_disposed=true;_spool.Dispose();}}
        private static IEnumerable<Page> BuildPages(IEnumerable<Record> source,CancellationToken ct)
        {
            var buffer=new StringBuilder("[");var bytes=1;var ordinal=0;var records=0;
            foreach(var record in source)
            {
                ct.ThrowIfCancellationRequested();
                using var stream=new MemoryStream();
                new DataContractJsonSerializer(typeof(Record),new DataContractJsonSerializerSettings{UseSimpleDictionaryFormat=true}).WriteObject(stream,record);
                var json=Encoding.UTF8.GetString(stream.ToArray());var size=Encoding.UTF8.GetByteCount(json);
                if(size>MaximumRecordBytes)throw new InvalidOperationException("history_publication_record_capacity");
                if((records==200||bytes+size+2>MaximumPageBytes)&&bytes>1){yield return new Page(++ordinal,buffer.Append(']').ToString());buffer.Clear().Append('[');bytes=1;records=0;}
                if(bytes>1){buffer.Append(',');bytes++;}buffer.Append(json);bytes+=size;
                records++;
            }
            if(bytes>1)yield return new Page(++ordinal,buffer.Append(']').ToString());
        }
        internal void DemandMatches(HistoricalImportAuthorizationCommitGuard guard)
        {
            guard.DemandStillValid();
            if(OperationId!=guard.OperationId||Kind!=guard.Kind.ToString()||ShopId!=guard.ShopId||DeviceId!=guard.DeviceId||DatasetId!=guard.DatasetId||RestoreEpoch!=guard.RestoreEpoch||RequestFingerprint!=guard.RequestFingerprint)
                throw new InvalidOperationException("history_publication_binding_mismatch");
        }
        public static HistoricalPublicationDraft ForApply(HistoricalSalesImportPlan p,string device,string restoreLineageId,
            IReadOnlyDictionary<long,HistoricalPublicationCorrectionReference> correctionOwners=null,CancellationToken ct=default)
        {
            if(p==null)throw new ArgumentNullException(nameof(p));HistoricalMerchantAssignment.Required(device);
            var lineage=NormalizeLineage(restoreLineageId);
            var owners=new Dictionary<long,HistoricalPublicationCorrectionReference>();
            foreach(var approval in p.CorrectionApprovals)
            {
                if(correctionOwners==null||!correctionOwners.TryGetValue(approval.PreviousTransactionId,out var owner)||owner==null)
                    throw new InvalidOperationException("history_publication_correction_owner_required");
                owner.DemandMatches(p,lineage,approval.PreviousTransactionId);owners[approval.PreviousTransactionId]=owner;
            }
            if(correctionOwners!=null&&correctionOwners.Count!=owners.Count)throw new InvalidOperationException("history_publication_correction_owner_invalid");
            return new HistoricalPublicationDraft(p.OperationId,"Apply",p.ShopId,device,p.DatasetId,p.RestoreEpoch,p.DataRevision,p.RequestFingerprint,lineage,()=>ApplyRecords(p,device,lineage,owners),ct);
        }
        public static HistoricalPublicationDraft ForUndo(string operation,string shop,string device,string dataset,long restore,long revision,string batch,long batchRevision,string applyOperation,string restoreLineageId)
        {
            var fingerprint=HistoricalSalesImportPlan.UndoFingerprint(operation,shop,dataset,batch,batchRevision,restore);
            var lineage=NormalizeLineage(restoreLineageId);
            return new HistoricalPublicationDraft(operation,"Undo",shop,device,dataset,restore,revision,fingerprint,lineage,()=>new[]{Header(operation,"Undo",shop,device,dataset,restore,revision,fingerprint,lineage),Row("undo","batch_id",batch,"batch_revision",S(batchRevision),"apply_operation_id",applyOperation)});
        }
        private static Record Header(string operation,string kind,string shop,string device,string dataset,long restore,long revision,string fingerprint,string lineage)
            =>Row("operation","protocol_version","2","operation_id",operation,"operation_kind",kind,"shop_id",shop,"device_id",device,"local_dataset_id",dataset,"restore_epoch",S(restore),"restore_lineage_id",lineage,"base_revision",S(revision),"request_fingerprint",fingerprint);
        private static IEnumerable<Record> ApplyRecords(HistoricalSalesImportPlan p,string device,string lineage,IReadOnlyDictionary<long,HistoricalPublicationCorrectionReference> owners)
        {
            yield return Header(p.OperationId,"Apply",p.ShopId,device,p.DatasetId,p.RestoreEpoch,p.DataRevision,p.RequestFingerprint,lineage);
            yield return Row("overlap","native_start_ticks",S(p.NativeStartBusinessDateTime.Ticks),"evidence_reference",p.OverlapEvidenceReference);
            foreach(var f in p.Analysis.Files.OrderBy(f=>f.FileId,StringComparer.Ordinal))
                yield return Row("file","id",f.FileId,"sha256",f.Sha256,"profile",f.Profile.ToString(),"source_rows",S(f.SourceRows));
            foreach(var a in p.Assignments.OrderBy(a=>a.AliasKey,StringComparer.Ordinal))
                yield return Row("binding","alias_key",a.AliasKey,"local_scope_id",a.ScopeId,"local_binding_id",a.BindingId,"evidence_reference",a.EvidenceReference);
            foreach(var r in p.Analysis.AcceptedTransactions)
                yield return Row("movement","source_file_id",r.Source.FileId,"source_sheet",r.Source.SheetName,"source_row",S(r.Source.RowNumber),
                    "external_namespace",r.ExternalNamespace,"external_id",r.ExternalId,"business_ticks",S(r.BusinessDateTime.Value.Ticks),
                    "profile",r.Profile.ToString(),"alias_key",HistoricalMerchantAssignment.MerchantKey(r.Merchant),
                    "merchant_local",r.Merchant.Local,"merchant_seller",r.Merchant.Seller,"merchant_seller_id",r.Merchant.SellerId,"merchant_local_number",r.Merchant.LocalNumber,"merchant_rut",r.Merchant.Rut,
                    "movement_kind",r.MovementKind.ToString(),"transaction_state",r.TransactionStatus,"settlement_state",r.SettlementStatus,
                    "gross_clp",S(r.GrossClp),"fee_clp",S(r.CommissionClp),"fee_vat_clp",S(r.CommissionVatClp),"net_clp",S(r.NetClp),
                    "timestamp_raw",r.Timestamp.RawTimestamp,"offset_raw",r.Timestamp.RawOffset,"offset_minutes",S(r.Timestamp.OffsetMinutes),
                    "occurred_at_utc_ms",S(r.Timestamp.OccurredAtUtc?.ToUnixTimeMilliseconds()),"time_interpretation",r.Timestamp.Interpretation.ToString(),
                    "time_policy_version",r.Timestamp.PolicyVersion,"time_evidence_reference",r.Timestamp.EvidenceReference,
                    "card_subtype",r.CardSubtype.ToString(),"payment_id",r.PaymentId,"authorization_code",r.AuthorizationCode,
                    "original_card_type",r.OriginalCardType,"original_prepaid",r.OriginalPrepaid,"terminal",r.Terminal,"settlement_date_raw",r.RawSettlementDate);
            foreach(var r in p.Analysis.RejectedRows)
                yield return Row("rejected","source_file_id",r.Source.FileId,"source_sheet",r.Source.SheetName,"source_row",S(r.Source.RowNumber),"transaction_state",r.TransactionStatus,"requested_clp",S(r.RequestedClp));
            foreach(var a in p.CorrectionApprovals.OrderBy(a=>a.SourceFileId,StringComparer.Ordinal).ThenBy(a=>a.SourceSheet,StringComparer.Ordinal).ThenBy(a=>a.SourceRow))
                yield return Row("correction","source_file_id",a.SourceFileId,"source_sheet",a.SourceSheet,"source_row",S(a.SourceRow),
                    "previous_transaction_id",S(a.PreviousTransactionId),"previous_local_batch_id",owners[a.PreviousTransactionId].LocalBatchId,
                    "previous_apply_operation_id",owners[a.PreviousTransactionId].ApplyOperationId,"previous_economic_digest",a.Previous.Fingerprint,
                    "proposed_economic_digest",a.Proposed.Fingerprint,"context_fingerprint",a.ContextFingerprint,
                    "evidence_reference",a.EvidenceReference,"approval_fingerprint",a.Fingerprint);
        }
        private static Record Row(string kind,params string[] values)
        {
            var fields=new Dictionary<string,string>(StringComparer.Ordinal);
            for(var i=0;i<values.Length;i+=2)fields.Add(values[i],values[i+1]);
            return new Record{Kind=kind,Fields=fields};
        }
        private static string S(long? value)=>value?.ToString(CultureInfo.InvariantCulture);
        internal static string NormalizeLineage(string value)
        {
            if(!Guid.TryParseExact(value,"N",out var id)&&!Guid.TryParseExact(value,"D",out id))throw new InvalidOperationException("history_restore_lineage_invalid");
            return id.ToString("D");
        }
        private static string Hex(byte[] value)=>BitConverter.ToString(value).Replace("-","").ToLowerInvariant();
    }
}
