using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalCloudRequestException:Exception
    {
        public HistoricalCloudRequestException(string code,bool denied,bool retryable)
        {Code=string.IsNullOrEmpty(code)?"history_request_failed":code;Denied=denied;Retryable=retryable;}
        public string Code{get;} public bool Denied{get;} public bool Retryable{get;}
    }

    /// <summary>Bounded delivery of previously admitted facts. Never requests a new mutation grant.</summary>
    public sealed class HistoricalPublicationSender
    {
        private readonly HistoricalPublicationOutbox _outbox;
        private readonly HistoricalPublicationJournal _journal;
        private readonly OnlineSyncGeneration _generation;
        private readonly Action _demand;
        private readonly Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> _request;
        private readonly Func<long> _now;
        public HistoricalPublicationSender(HistoricalPublicationOutbox outbox,OnlineSyncGeneration generation,Action demand,
            Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> request,Func<long> now=null)
        { _outbox=outbox??throw new ArgumentNullException(nameof(outbox));_generation=generation??throw new ArgumentNullException(nameof(generation));
          _demand=demand??throw new ArgumentNullException(nameof(demand));_request=request??throw new ArgumentNullException(nameof(request));
          _now=now??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());_journal=new HistoricalPublicationJournal(_outbox.Factory); }
        public Task<bool> RunAsync(CancellationToken ct)=>Task.Run(async()=>
        {
            HistoricalCloudRequestException latestFailure=null;
            for(var index=0;index<2;index++)
            {
                _demand();var attempt=await _outbox.ClaimAsync(_generation,_demand,_now(),ct).ConfigureAwait(false);
                if(attempt==null){if(latestFailure!=null)throw latestFailure;return false;}
                try
                {
                    // A lost delivery ACK is resolved by reading the original
                    // server receipt, including after writer authority changed.
                    var reply=await _request("local_receipt",new HistoricalCloudPayload{
                        OperationId=attempt.WireOperationId,ContentDigest=attempt.ContentDigest},ct).ConfigureAwait(false);
                    _demand();ct.ThrowIfCancellationRequested();
                    if(reply?.PublicationReceipt==null)
                    {
                        // Restore may recover an acquired receipt, but cannot
                        // first-deliver an old-lineage fact.
                        if(attempt.ReceiptOnly)throw new HistoricalCloudRequestException("history_publication_restore_reconciliation",false,false);
                        if(reply?.State!="authorized"&&reply?.State!="reconcile_required")throw new HistoricalCloudRequestException("history_publication_not_admitted",false,false);
                        var witness=await _journal.ReadOutcomeWitnessAsync(attempt.OperationId,_generation.ShopId,_generation.ShopDeviceId,_demand,ct).ConfigureAwait(false);
                        if(witness==null||witness.Outcome!="committed"||witness.ProofJson!=attempt.ProofJson)
                            throw new HistoricalCloudRequestException("history_publication_witness_missing",false,false);
                        reply=await _request("local_reconcile",new HistoricalCloudPayload{
                            OperationId=attempt.WireOperationId,GrantId=attempt.Proof.GrantId,ContentDigest=attempt.ContentDigest,
                            LocalRevision=attempt.LocalRevision.ToString(CultureInfo.InvariantCulture),LocalSequence=attempt.Sequence.ToString(CultureInfo.InvariantCulture),
                            LocalReceiptJson=attempt.ReceiptJson,WitnessJson=witness.Json,LocalProofJson=witness.ProofJson},ct).ConfigureAwait(false);
                    }
                    _demand();ct.ThrowIfCancellationRequested();
                    await _outbox.ConfirmAsync(attempt,reply?.PublicationReceipt,_generation,_demand,_now(),ct).ConfigureAwait(false);
                }
                catch(HistoricalCloudRequestException failure)
                {
                    await _outbox.FailAsync(attempt,_generation,_demand,failure.Retryable,failure.Code,_now(),ct).ConfigureAwait(false);
                    if(failure.Denied)throw;
                    if(failure.Retryable)latestFailure=failure;
                    // A content conflict is isolated to this operation. The
                    // next independent batch still receives its own turn.
                }
                catch(InvalidOperationException failure) when(failure.Message=="history_publication_receipt_mismatch")
                {
                    await _outbox.FailAsync(attempt,_generation,_demand,false,failure.Message,_now(),ct).ConfigureAwait(false);
                }
                catch(OperationCanceledException) when(!ct.IsCancellationRequested)
                {
                    await _outbox.FailAsync(attempt,_generation,_demand,true,"history_timeout",_now(),ct).ConfigureAwait(false);
                    latestFailure=new HistoricalCloudRequestException("history_timeout",false,true);
                }
            }
            if(latestFailure!=null)throw latestFailure;
            return true;
        },ct);
    }
}
