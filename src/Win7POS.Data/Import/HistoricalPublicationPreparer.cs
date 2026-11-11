using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalPublicationAdmission
    {
        private readonly Func<long> _milliseconds;
        internal HistoricalPublicationAdmission(HistoricalPublicationProof proof,int remaining,long elapsed,long started,Func<long> milliseconds)
        {Proof=proof;RemainingMilliseconds=remaining;GrantRequestElapsedMilliseconds=elapsed;MonotonicRequestStartedMilliseconds=started;_milliseconds=milliseconds;}
        public HistoricalPublicationProof Proof{get;}
        public int RemainingMilliseconds{get;}
        public long GrantRequestElapsedMilliseconds{get;}
        public long MonotonicRequestStartedMilliseconds{get;}
        public long ReadMonotonicMilliseconds()=>_milliseconds();
    }

    /// <summary>
    /// Prepares immutable content before requesting a fresh content-bound grant.
    /// The caller owns the draft and preparation deadline. This writes no local
    /// ledger/outbox and cannot mint or renew a local mutation grant.
    /// </summary>
    public sealed class HistoricalPublicationPreparer
    {
        private readonly Action _demand;
        private readonly Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> _request;
        private readonly Func<long> _milliseconds;
        private readonly HistoricalPublicationJournal _journal;
        public HistoricalPublicationPreparer(SqliteConnectionFactory factory,Action demand,
            Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> request,Func<long> milliseconds=null)
        {_journal=new HistoricalPublicationJournal(factory);_demand=demand??throw new ArgumentNullException(nameof(demand));_request=request??throw new ArgumentNullException(nameof(request));
         _milliseconds=milliseconds??(()=>checked((long)(Stopwatch.GetTimestamp()*(1000m/Stopwatch.Frequency))));}

        public Task<HistoricalPublicationAdmission> PrepareAsync(HistoricalPublicationDraft draft,
            HistoricalCloudResult authority,CancellationToken ct)=>Task.Run(async()=>
        {
            if(draft==null||authority==null)throw new ArgumentNullException();
            Demand(ct);HistoricalReplicaProtocol.DemandId(authority.DatasetId);
            var writerEpoch=HistoricalReplicaProtocol.Integer(authority.WriterEpoch);
            if(authority.WriterAuthority!="local"||authority.ProtocolVersion!=1||writerEpoch<1)
                throw new InvalidOperationException("history_local_writer_closed");
            var operation=Guid.Parse(draft.OperationId).ToString("D");var serverDataset=authority.DatasetId;
            var nonce=await _journal.SaveIntentAsync(draft,serverDataset,writerEpoch,_demand,ct).ConfigureAwait(false);
            var prepared=await Request("local_prepare",new HistoricalCloudPayload{OperationId=operation,Kind=draft.Kind,
                LocalDatasetId=draft.DatasetId,RestoreEpoch=S(draft.RestoreEpoch),RestoreLineageId=draft.RestoreLineageId,
                BaseRevision=S(draft.BaseRevision),WriterEpoch=S(writerEpoch),RequestFingerprint=draft.RequestFingerprint,
                ContentDigest=draft.ContentDigest,PageCount=draft.PageCount,ProtocolVersion=2},ct).ConfigureAwait(false);
            DemandProgress(prepared,draft,operation,1);
            if(prepared.PublicationPageCount!=draft.PageCount)throw new InvalidOperationException("history_publication_preparation_mismatch");
            var next=prepared.NextOrdinal;
            while(next<=draft.PageCount)
            {
                Demand(ct);var page=draft.ReadPage(next,ct);
                var staged=await Request("local_stage",new HistoricalCloudPayload{OperationId=operation,ContentDigest=draft.ContentDigest,
                    Ordinal=page.Ordinal,PageJson=page.Json,PageSha256=page.Digest},ct).ConfigureAwait(false);
                DemandProgress(staged,draft,operation,page.Ordinal+1);next=staged.NextOrdinal;
            }
            if(next!=draft.PageCount+1)throw new InvalidOperationException("history_publication_preparation_mismatch");
            // Preparation can take many requests. The short local-write budget
            // starts at the final authorization request, never at staging.
            var started=_milliseconds();
            var granted=await Request("local_authorize",new HistoricalCloudPayload{OperationId=operation,ContentDigest=draft.ContentDigest},ct).ConfigureAwait(false);
            var proof=granted?.PublicationProof;proof?.DemandValid();
            if(granted?.State!="authorized"||granted.OperationId!=operation||granted.ContentDigest!=draft.ContentDigest||
                granted.WriterEpoch!=S(writerEpoch)||granted.RemainingMilliseconds<1||granted.RemainingMilliseconds>60000||
                proof==null||proof.GrantId!=granted.GrantId||proof.OperationId!=operation||proof.ShopId!=draft.ShopId||proof.DeviceId!=draft.DeviceId||
                proof.DatasetId!=draft.DatasetId||proof.ServerDatasetId!=serverDataset||proof.RestoreLineageId!=draft.RestoreLineageId||
                proof.RestoreEpoch!=draft.RestoreEpoch||proof.WriterEpoch!=writerEpoch||proof.ContentDigest!=draft.ContentDigest||proof.RequestFingerprint!=draft.RequestFingerprint)
                throw new InvalidOperationException("history_publication_proof_mismatch");
            await _journal.SaveGrantAsync(draft.OperationId,nonce,proof,_demand,ct).ConfigureAwait(false);
            Demand(ct);var elapsed=checked(_milliseconds()-started);
            if(elapsed<0)throw new InvalidOperationException("history_local_write_grant_invalid");
            if(elapsed>=granted.RemainingMilliseconds-10000L)throw new InvalidOperationException("history_local_write_grant_expired");
            return new HistoricalPublicationAdmission(proof,granted.RemainingMilliseconds,elapsed,started,_milliseconds);
        },ct);

        private void Demand(CancellationToken ct){ct.ThrowIfCancellationRequested();_demand();}
        private async Task<HistoricalCloudResult> Request(string operation,HistoricalCloudPayload payload,CancellationToken ct)
        {Demand(ct);var result=await _request(operation,payload,ct).ConfigureAwait(false);Demand(ct);return result;}
        private static void DemandProgress(HistoricalCloudResult result,HistoricalPublicationDraft draft,string operation,int minimum)
        {
            if(result?.State!="preparing"||result.OperationId!=operation||result.ContentDigest!=draft.ContentDigest||
                result.NextOrdinal<minimum||result.NextOrdinal>draft.PageCount+1)
                throw new InvalidOperationException("history_publication_preparation_mismatch");
        }
        private static string S(long value)=>value.ToString(CultureInfo.InvariantCulture);
    }
}
