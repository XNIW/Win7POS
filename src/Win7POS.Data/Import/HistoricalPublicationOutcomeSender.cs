using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    /// <summary>One negative outcome in the existing historical supervisor turn.
    /// No independent queue, timer or local-write grant request.</summary>
    public sealed class HistoricalPublicationOutcomeSender
    {
        private readonly HistoricalPublicationJournal _journal;
        private readonly OnlineSyncGeneration _generation;
        private readonly Action _demand;
        private readonly Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> _request;
        private readonly Func<long> _now;
        public HistoricalPublicationOutcomeSender(HistoricalPublicationJournal journal,OnlineSyncGeneration generation,Action demand,
            Func<string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> request,Func<long> now=null)
        {_journal=journal??throw new ArgumentNullException(nameof(journal));_generation=generation??throw new ArgumentNullException(nameof(generation));
         _demand=demand??throw new ArgumentNullException(nameof(demand));_request=request??throw new ArgumentNullException(nameof(request));_now=now??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());}
        public async Task<bool> RunAsync(CancellationToken ct)
        {
            var witness=await _journal.ReadPendingNegativeOutcomeAsync(_generation,_demand,ct).ConfigureAwait(false);
            if(witness==null)return false;
            _demand();ct.ThrowIfCancellationRequested();
            var ack=await _request("local_abort",new HistoricalCloudPayload{OperationId=witness.OperationId,ContentDigest=witness.ContentDigest,
                WitnessJson=witness.Json,LocalProofJson=witness.ProofJson},ct).ConfigureAwait(false);
            _demand();ct.ThrowIfCancellationRequested();
            await _journal.ResolveNegativeOutcomeAsync(witness,ack,_generation,_demand,_now(),ct).ConfigureAwait(false);
            return true;
        }
    }
}
