using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Component admission fixture only. HTTP callers still use their real
    // authenticated loopback boundary. This does not attest database economic
    // admission, the migration baseline or authenticated distributed E2E.
    internal sealed class HistoricalPublicationAdmissionFixture
    {
        // Synthetic component only: invoke the actual trusted constructor and
        // binding checks without widening product visibility to the harness.
        internal static void AttachLiveGrant(HistoricalImportAuthorizationCommitGuard guard,HistoricalPublicationProof proof,Action demand)
        {
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var constructor=typeof(HistoricalLocalWriteGrant).GetConstructors(flags).Single();
            var grant=(HistoricalLocalWriteGrant)constructor.Invoke(new object[]{proof.OperationId,proof.WriterEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),60000,0L,demand,null,0L,proof,null});
            typeof(HistoricalImportAuthorizationCommitGuard).GetMethod("AttachPublicationGrant",flags).Invoke(guard,new object[]{grant});
        }
        private readonly string _shop,_device,_dataset;
        private readonly Func<string,string,string,Decision> _decision;
        private readonly bool _allNewFixture;
        private readonly Dictionary<string,Prepared> _operations=new Dictionary<string,Prepared>(StringComparer.Ordinal);
        private sealed class Prepared
        {
            internal string Operation,Kind,LocalDataset,Restore,Lineage,Epoch,Request,Content;
            internal int Pages;internal readonly List<string> Digests=new List<string>();
            internal readonly Decision Totals=new Decision();internal string UndoBatch;
        }
        internal HistoricalPublicationAdmissionFixture(string shop,string device,string dataset,Func<string,string,string,Decision> decision=null,bool allNewFixture=false)
        {_shop=shop;_device=device;_dataset=dataset;_decision=decision;_allNewFixture=allNewFixture;}
        [DataContract] internal sealed class Decision
        {
            [DataMember(Name="inserted")] public int Inserted;
            [DataMember(Name="duplicates")] public int Duplicates;
            [DataMember(Name="rejected")] public int Rejected;
            [DataMember(Name="corrections")] public int Corrections;
            [DataMember(Name="deactivated")] public int Deactivated;
            internal long Gross;
            [DataMember(Name="signed_gross_clp")] private string GrossWire{get=>Gross.ToString(System.Globalization.CultureInfo.InvariantCulture);set=>Gross=HistoricalReplicaProtocol.Integer(value);}
            [DataMember(Name="revision_delta")] private string DeltaWire{get=>(Inserted+Deactivated).ToString(System.Globalization.CultureInfo.InvariantCulture);set{}}
        }
        [DataContract] private sealed class CanonicalDecision
        {
            [DataMember(Name="batch_id")] internal string Batch;
            [DataMember(Name="revision")] internal string Revision;
            [DataMember(Name="inserted")] internal int Inserted;
            [DataMember(Name="duplicates")] internal int Duplicates;
            [DataMember(Name="deactivated")] internal int Deactivated;
            [DataMember(Name="signed_gross_clp")] internal string Gross;
        }
        [DataContract] private sealed class Record
        {
            [DataMember(Name="kind")] internal string Kind{get;set;}
            [DataMember(Name="fields")] internal Dictionary<string,string> Fields{get;set;}
        }
        internal int Authorizations{get;private set;}
        internal HistoricalCloudResult Request(string operation,HistoricalCloudPayload payload,string rejection=null)
        {
            if(payload==null)throw new InvalidOperationException("qa_history_publication_payload_required");
            HistoricalReplicaProtocol.DemandId(payload.OperationId);HistoricalReplicaProtocol.DemandHash(payload.ContentDigest);
            if(operation=="local_prepare")
            {
                if(rejection=="v1_only")throw new InvalidOperationException("qa_history_v2_not_available");
                if(payload.ProtocolVersion!=2||payload.WriterEpoch!="1"||payload.PageCount<1||payload.PageCount>20000||
                    (payload.Kind!="Apply"&&payload.Kind!="Undo"))throw new InvalidOperationException("qa_history_publication_prepare_invalid");
                if(!_operations.TryGetValue(payload.OperationId,out var prepared))
                {
                    prepared=new Prepared{Operation=payload.OperationId,Kind=payload.Kind,LocalDataset=payload.LocalDatasetId,Restore=payload.RestoreEpoch,
                        Lineage=payload.RestoreLineageId,Epoch=payload.WriterEpoch,Request=payload.RequestFingerprint,Content=payload.ContentDigest,Pages=payload.PageCount.Value};
                    _operations.Add(payload.OperationId,prepared);
                }
                if(prepared.Content!=payload.ContentDigest||prepared.LocalDataset!=payload.LocalDatasetId||prepared.Lineage!=payload.RestoreLineageId)
                    throw new InvalidOperationException("qa_history_publication_identity_changed");
                return Progress(prepared,true);
            }
            if(!_operations.TryGetValue(payload.OperationId,out var p)||p.Content!=payload.ContentDigest)
                throw new InvalidOperationException("qa_history_publication_not_prepared");
            if(operation=="local_stage")
            {
                var raw=Encoding.UTF8.GetBytes(payload.PageJson??"");
                if(raw.Length<2||raw.Length>HistoricalPublicationDraft.MaximumPageBytes||Hash(raw)!=payload.PageSha256||payload.Ordinal<1||payload.Ordinal>p.Pages)
                    throw new InvalidOperationException("qa_history_publication_page_invalid");
                if(payload.Ordinal<=p.Digests.Count)
                {if(p.Digests[payload.Ordinal.Value-1]!=payload.PageSha256)throw new InvalidOperationException("qa_history_publication_page_changed");}
                else
                {
                    if(payload.Ordinal!=p.Digests.Count+1)throw new InvalidOperationException("qa_history_publication_page_skipped");
                    using var records=new MemoryStream(raw);
                    foreach(var record in (Record[])new DataContractJsonSerializer(typeof(Record[]),new DataContractJsonSerializerSettings{UseSimpleDictionaryFormat=true}).ReadObject(records))
                    {
                        if(record.Kind=="movement"){p.Totals.Inserted++;p.Totals.Gross=checked(p.Totals.Gross+HistoricalReplicaProtocol.Integer(record.Fields["gross_clp"]));}
                        else if(record.Kind=="rejected")p.Totals.Rejected++;
                        else if(record.Kind=="correction")p.Totals.Corrections++;
                        else if(record.Kind=="undo")p.UndoBatch=record.Fields["batch_id"];
                    }
                    p.Digests.Add(payload.PageSha256);
                }
                return Progress(p,false);
            }
            if(operation!="local_authorize"||p.Digests.Count!=p.Pages||Hash(Encoding.ASCII.GetBytes(string.Join("\n",p.Digests)+"\n"))!=p.Content)
                throw new InvalidOperationException("qa_history_publication_not_complete");
            Authorizations++;
            var id=Guid.NewGuid().ToString("D");
            var proofFields=new Dictionary<string,string>{{"grant_id",id},{"operation_id",p.Operation},{"shop_id",_shop},{"device_id",_device},
                {"local_dataset_id",p.LocalDataset},{"server_dataset_id",_dataset},{"restore_lineage_id",p.Lineage},{"restore_epoch",p.Restore},
                {"writer_epoch","1"},{"content_digest",p.Content},{"request_fingerprint",p.Request},{"decision","content_authorized_v2"}};
            var decision=_decision?.Invoke(p.Operation,p.Kind,p.UndoBatch);
            if(decision==null)
            {
                if(!_allNewFixture||p.Kind!="Apply")throw new InvalidOperationException("qa_history_decision_oracle_required");
                decision=p.Totals;
            }
            var proof=MakeProof(proofFields,decision);
            return new HistoricalCloudResult{State="authorized",OperationId=rejection=="wrong_operation"?Guid.NewGuid().ToString("D"):p.Operation,
                ContentDigest=p.Content,GrantId=id,WriterEpoch=p.Epoch,RemainingMilliseconds=rejection=="expired"?0:60000,PublicationProof=proof};
        }
        private static HistoricalCloudResult Progress(Prepared p,bool count)=>new HistoricalCloudResult{State="preparing",OperationId=p.Operation,
            ContentDigest=p.Content,NextOrdinal=p.Digests.Count+1,PublicationPageCount=count?p.Pages:0};
        private static string Hash(byte[] value){using var h=SHA256.Create();return BitConverter.ToString(h.ComputeHash(value)).Replace("-","").ToLowerInvariant();}
        // Typed simulated server DTOs only; no production constructor is opened
        // to the harness. This fixture is not the independent SQL economic oracle.
        internal static HistoricalPublicationProof MakeProof(Dictionary<string,string> fields,Decision local,string revision="1")
        {
            string Json<T>(T value){using var stream=new MemoryStream();new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings{UseSimpleDictionaryFormat=true}).WriteObject(stream,value);return Encoding.UTF8.GetString(stream.ToArray());}
            var root=Json(fields);var canonical=new CanonicalDecision{Batch=Guid.NewGuid().ToString("D"),Revision=revision,Inserted=local.Inserted,
                Duplicates=local.Duplicates,Deactivated=local.Deactivated,Gross=local.Gross.ToString(System.Globalization.CultureInfo.InvariantCulture)};
            var json=root.Substring(0,root.Length-1)+",\"local_decision\":"+Json(local)+",\"canonical_decision\":"+Json(canonical)+"}";
            using var read=new MemoryStream(Encoding.UTF8.GetBytes(json));
            var proof=(HistoricalPublicationProof)new DataContractJsonSerializer(typeof(HistoricalPublicationProof)).ReadObject(read);proof.DemandValid();return proof;
        }
        internal static string Wire(string operation,HistoricalCloudResult result)
        {
            if(operation=="local_authorize")
            {
                using var proofStream=new MemoryStream();new DataContractJsonSerializer(typeof(HistoricalPublicationProof)).WriteObject(proofStream,result.PublicationProof);
                return "{\"state\":\"authorized\",\"operation_id\":\""+result.OperationId+"\",\"content_digest\":\""+result.ContentDigest+"\",\"grant_id\":\""+
                    result.GrantId+"\",\"writer_epoch\":\""+result.WriterEpoch+"\",\"remaining_milliseconds\":"+result.RemainingMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+
                    ",\"proof\":"+Encoding.UTF8.GetString(proofStream.ToArray())+"}";
            }
            var fields=new Dictionary<string,object>{{"state",result.State},{"operation_id",result.OperationId},{"content_digest",result.ContentDigest}};
            fields.Add("next_ordinal",result.NextOrdinal);if(operation=="local_prepare")fields.Add("page_count",result.PublicationPageCount);
            using var stream=new MemoryStream();new DataContractJsonSerializer(typeof(Dictionary<string,object>),new DataContractJsonSerializerSettings{
                UseSimpleDictionaryFormat=true}).WriteObject(stream,fields);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
