using System;
using System.Diagnostics;
using Win7POS.Core.Online;

namespace Win7POS.Core.Import
{
    /// <summary>A short in-memory server grant. Never persisted or minted by a receipt.</summary>
    public sealed class HistoricalLocalWriteGrant
    {
        private readonly Func<long> _clock; private readonly long _start; private readonly long _deadline; private readonly Action _demandActor;
        internal HistoricalLocalWriteGrant(string operationId,string epoch,int remainingMilliseconds,long requestElapsedMilliseconds,Action demandActor,
            Func<long> clock=null,long frequency=0)
        {
            HistoricalReplicaProtocol.DemandId(operationId);OperationId=operationId;WriterEpoch=HistoricalReplicaProtocol.Integer(epoch);
            if(WriterEpoch<1||remainingMilliseconds<1||remainingMilliseconds>60000||requestElapsedMilliseconds<0)throw new InvalidOperationException("history_local_write_grant_invalid");
            _demandActor=demandActor??throw new ArgumentNullException(nameof(demandActor));_clock=clock??Stopwatch.GetTimestamp;
            var hz=frequency==0?Stopwatch.Frequency:frequency;var budget=remainingMilliseconds-requestElapsedMilliseconds-10000;
            if(hz<1||hz>1000000000000L||budget<=0)throw new InvalidOperationException("history_local_write_grant_expired");
            _start=_clock();_deadline=checked(_start+(long)((decimal)budget*hz/1000));DemandStillValid();
        }
        public string OperationId { get; }
        public long WriterEpoch { get; }
        public void DemandStillValid()
        {
            _demandActor();var now=_clock();if(now<_start||now>=_deadline)throw new InvalidOperationException("history_local_write_grant_expired");
        }
    }
}
