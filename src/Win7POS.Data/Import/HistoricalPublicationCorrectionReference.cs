using System;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    /// <summary>Immutable owner identity read from the scoped local ledger.</summary>
    public sealed class HistoricalPublicationCorrectionReference
    {
        internal HistoricalPublicationCorrectionReference(HistoricalSalesImportPlan plan,string lineage,long transaction,string batch,string apply)
        {
            if(plan==null||transaction<1||string.IsNullOrWhiteSpace(batch)||batch.Length>256||
                !Guid.TryParse(apply,out var operation)||operation==Guid.Empty||!Guid.TryParse(lineage,out var restoreLineage)||restoreLineage==Guid.Empty)
                throw new InvalidOperationException("history_publication_correction_owner_invalid");
            ShopId=plan.ShopId;DatasetId=plan.DatasetId;RestoreEpoch=plan.RestoreEpoch;DataRevision=plan.DataRevision;
            RestoreLineageId=restoreLineage.ToString("D");TransactionId=transaction;LocalBatchId=batch;ApplyOperationId=operation.ToString("D");
        }
        public string ShopId{get;} public string DatasetId{get;} public long RestoreEpoch{get;} public long DataRevision{get;}
        public string RestoreLineageId{get;} public long TransactionId{get;} public string LocalBatchId{get;} public string ApplyOperationId{get;}
        internal void DemandMatches(HistoricalSalesImportPlan plan,string lineage,long transaction)
        {
            if(ShopId!=plan.ShopId||DatasetId!=plan.DatasetId||RestoreEpoch!=plan.RestoreEpoch||DataRevision!=plan.DataRevision||
                RestoreLineageId!=lineage||TransactionId!=transaction)
                throw new InvalidOperationException("history_publication_correction_owner_invalid");
        }
    }
}
