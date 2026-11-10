using System;
using System.Runtime.Serialization;
using Win7POS.Core.Online;

namespace Win7POS.Core.Import
{
    /// <summary>
    /// Durable server authorization decision for exactly one content digest.
    /// It is not an offline mutation grant or a replacement for current login.
    /// </summary>
    [DataContract]
    public sealed class HistoricalPublicationProof
    {
        internal HistoricalPublicationProof(string grantId,string operationId,string shopId,string deviceId,string datasetId,string serverDatasetId,string restoreLineageId,
            long restoreEpoch,long writerEpoch,string contentDigest,string requestFingerprint,string decision)
        {
            foreach(var id in new[]{grantId,operationId,shopId,deviceId,serverDatasetId,restoreLineageId})HistoricalReplicaProtocol.DemandId(id);
            HistoricalReplicaProtocol.DemandHash(contentDigest);HistoricalReplicaProtocol.DemandHash(requestFingerprint);
            if(string.IsNullOrEmpty(datasetId)||datasetId.Length>256||restoreEpoch<0||writerEpoch<1||decision!="content_authorized_v2")
                throw new InvalidOperationException("history_publication_proof_invalid");
            GrantId=grantId;OperationId=operationId;ShopId=shopId;DeviceId=deviceId;DatasetId=datasetId;ServerDatasetId=serverDatasetId;RestoreLineageId=restoreLineageId;
            RestoreEpoch=restoreEpoch;WriterEpoch=writerEpoch;ContentDigest=contentDigest;RequestFingerprint=requestFingerprint;Decision=decision;
        }
        [DataMember(Name="grant_id",Order=0)] public string GrantId{get;private set;}
        [DataMember(Name="operation_id",Order=1)] public string OperationId{get;private set;}
        [DataMember(Name="shop_id",Order=2)] public string ShopId{get;private set;}
        [DataMember(Name="device_id",Order=3)] public string DeviceId{get;private set;}
        [DataMember(Name="local_dataset_id",Order=4)] public string DatasetId{get;private set;}
        public long RestoreEpoch{get;private set;}
        public long WriterEpoch{get;private set;}
        [DataMember(Name="restore_epoch",Order=5)] private string RestoreEpochWire
        {get=>RestoreEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);set=>RestoreEpoch=HistoricalReplicaProtocol.Integer(value);}
        [DataMember(Name="writer_epoch",Order=6)] private string WriterEpochWire
        {get=>WriterEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);set=>WriterEpoch=HistoricalReplicaProtocol.Integer(value);}
        [DataMember(Name="content_digest",Order=7)] public string ContentDigest{get;private set;}
        [DataMember(Name="request_fingerprint",Order=8)] public string RequestFingerprint{get;private set;}
        [DataMember(Name="decision",Order=9)] public string Decision{get;private set;}
        [DataMember(Name="server_dataset_id",Order=10)] public string ServerDatasetId{get;private set;}
        [DataMember(Name="restore_lineage_id",Order=11)] public string RestoreLineageId{get;private set;}
        // DataContract readers bypass constructors. Validate every received or
        // persisted proof before it can authorize transport or a local commit.
        public void DemandValid()
            =>_ = new HistoricalPublicationProof(GrantId,OperationId,ShopId,DeviceId,DatasetId,ServerDatasetId,
                RestoreLineageId,RestoreEpoch,WriterEpoch,ContentDigest,RequestFingerprint,Decision);
    }
}
