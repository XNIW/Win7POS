using System;
using System.Globalization;
using System.Runtime.Serialization;
using Win7POS.Core.Online;

namespace Win7POS.Core.Import
{
    [DataContract]
    public sealed class HistoricalPublicationLocalDecision
    {
        internal HistoricalPublicationLocalDecision(int inserted,int duplicates,int rejected,int corrections,int deactivated,long gross,long revisionDelta)
        {
            Inserted=inserted;Duplicates=duplicates;Rejected=rejected;Corrections=corrections;Deactivated=deactivated;
            SignedGrossClp=gross.ToString(CultureInfo.InvariantCulture);RevisionDelta=revisionDelta.ToString(CultureInfo.InvariantCulture);
            DemandValid();
        }
        [DataMember(Name="inserted",Order=0)] public int? Inserted{get;private set;}
        [DataMember(Name="duplicates",Order=1)] public int? Duplicates{get;private set;}
        [DataMember(Name="rejected",Order=2)] public int? Rejected{get;private set;}
        [DataMember(Name="corrections",Order=3)] public int? Corrections{get;private set;}
        [DataMember(Name="deactivated",Order=4)] public int? Deactivated{get;private set;}
        [DataMember(Name="signed_gross_clp",Order=5)] public string SignedGrossClp{get;private set;}
        [DataMember(Name="revision_delta",Order=6)] public string RevisionDelta{get;private set;}
        public void DemandValid()
        {
            if(!Count(Inserted)||!Count(Duplicates)||!Count(Deactivated)||!Count(Corrections)||!Rejected.HasValue||Rejected<0||
                Inserted+Duplicates>100000||Corrections>Inserted||HistoricalReplicaProtocol.Integer(RevisionDelta)!=Inserted+Deactivated)
                throw new InvalidOperationException("history_publication_decision_invalid");
            HistoricalReplicaProtocol.Integer(SignedGrossClp);
        }
        private static bool Count(int? value)=>value.HasValue&&value>=0&&value<=100000;
    }

    [DataContract]
    public sealed class HistoricalPublicationCanonicalDecision
    {
        internal HistoricalPublicationCanonicalDecision(string batchId,long revision,int inserted,int duplicates,int deactivated,long gross)
        {
            BatchId=batchId;Revision=revision.ToString(CultureInfo.InvariantCulture);Inserted=inserted;Duplicates=duplicates;Deactivated=deactivated;
            SignedGrossClp=gross.ToString(CultureInfo.InvariantCulture);DemandValid();
        }
        [DataMember(Name="batch_id",Order=0)] public string BatchId{get;private set;}
        [DataMember(Name="revision",Order=1)] public string Revision{get;private set;}
        [DataMember(Name="inserted",Order=2)] public int? Inserted{get;private set;}
        [DataMember(Name="duplicates",Order=3)] public int? Duplicates{get;private set;}
        [DataMember(Name="deactivated",Order=4)] public int? Deactivated{get;private set;}
        [DataMember(Name="signed_gross_clp",Order=5)] public string SignedGrossClp{get;private set;}
        public void DemandValid()
        {
            HistoricalReplicaProtocol.DemandId(BatchId);
            if(!Count(Inserted)||!Count(Duplicates)||!Count(Deactivated)||Inserted+Duplicates>100000||
                (Inserted+Duplicates>0&&Deactivated>0)||HistoricalReplicaProtocol.Integer(Revision)<1)
                throw new InvalidOperationException("history_publication_decision_invalid");
            HistoricalReplicaProtocol.Integer(SignedGrossClp);
        }
        private static bool Count(int? value)=>value.HasValue&&value>=0&&value<=100000;
    }

    /// <summary>
    /// Durable server authorization decision for exactly one content digest.
    /// It is not an offline mutation grant or a replacement for current login.
    /// </summary>
    [DataContract]
    public sealed class HistoricalPublicationProof
    {
        internal HistoricalPublicationProof(string grantId,string operationId,string shopId,string deviceId,string datasetId,string serverDatasetId,string restoreLineageId,
            long restoreEpoch,long writerEpoch,string contentDigest,string requestFingerprint,string decision,
            HistoricalPublicationLocalDecision localDecision,HistoricalPublicationCanonicalDecision canonicalDecision)
        {
            foreach(var id in new[]{grantId,operationId,shopId,deviceId,serverDatasetId,restoreLineageId})HistoricalReplicaProtocol.DemandId(id);
            HistoricalReplicaProtocol.DemandHash(contentDigest);HistoricalReplicaProtocol.DemandHash(requestFingerprint);
            if(string.IsNullOrEmpty(datasetId)||datasetId.Length>256||restoreEpoch<0||writerEpoch<1||decision!="content_authorized_v2")
                throw new InvalidOperationException("history_publication_proof_invalid");
            GrantId=grantId;OperationId=operationId;ShopId=shopId;DeviceId=deviceId;DatasetId=datasetId;ServerDatasetId=serverDatasetId;RestoreLineageId=restoreLineageId;
            RestoreEpoch=restoreEpoch;WriterEpoch=writerEpoch;ContentDigest=contentDigest;RequestFingerprint=requestFingerprint;Decision=decision;
            if(localDecision==null||canonicalDecision==null)throw new InvalidOperationException("history_publication_decision_missing");
            localDecision.DemandValid();canonicalDecision.DemandValid();LocalDecision=localDecision;CanonicalDecision=canonicalDecision;
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
        [DataMember(Name="local_decision",Order=12)] public HistoricalPublicationLocalDecision LocalDecision{get;private set;}
        [DataMember(Name="canonical_decision",Order=13)] public HistoricalPublicationCanonicalDecision CanonicalDecision{get;private set;}
        // DataContract readers bypass constructors. Validate every received or
        // persisted proof before it can authorize transport or a local commit.
        public void DemandValid()
            =>_ = new HistoricalPublicationProof(GrantId,OperationId,ShopId,DeviceId,DatasetId,ServerDatasetId,
                RestoreLineageId,RestoreEpoch,WriterEpoch,ContentDigest,RequestFingerprint,Decision,LocalDecision,CanonicalDecision);
    }
}
