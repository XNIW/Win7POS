using System;
using System.Diagnostics;
using Win7POS.Core.Online;

namespace Win7POS.Core.Import
{
    /// <summary>A short in-memory server grant. Never persisted or minted by a receipt.</summary>
    public sealed class HistoricalLocalWriteGrant
    {
        private readonly Func<long> _clock; private readonly long _start; private readonly long _deadline; private readonly Action _demandActor;
        private readonly long _frequency;private readonly int _remainingMilliseconds;private readonly long _requestElapsedMilliseconds;
        internal HistoricalLocalWriteGrant(string operationId,string epoch,int remainingMilliseconds,long requestElapsedMilliseconds,Action demandActor,
            Func<long> clock=null,long frequency=0,HistoricalPublicationProof publicationProof=null)
        {
            HistoricalReplicaProtocol.DemandId(operationId);OperationId=operationId;WriterEpoch=HistoricalReplicaProtocol.Integer(epoch);
            if(WriterEpoch<1||remainingMilliseconds<1||remainingMilliseconds>60000||requestElapsedMilliseconds<0)throw new InvalidOperationException("history_local_write_grant_invalid");
            _demandActor=demandActor??throw new ArgumentNullException(nameof(demandActor));_clock=clock??Stopwatch.GetTimestamp;
            var hz=frequency==0?Stopwatch.Frequency:frequency;var budget=remainingMilliseconds-requestElapsedMilliseconds-10000;
            if(hz<1||hz>1000000000000L||budget<=0)throw new InvalidOperationException("history_local_write_grant_expired");
            _frequency=hz;_remainingMilliseconds=remainingMilliseconds;_requestElapsedMilliseconds=requestElapsedMilliseconds;
            _start=_clock();_deadline=checked(_start+(long)((decimal)budget*hz/1000));DemandStillValid();
            if(publicationProof!=null&&(publicationProof.OperationId!=operationId||publicationProof.WriterEpoch!=WriterEpoch))
                throw new InvalidOperationException("history_publication_proof_invalid");
            PublicationProof=publicationProof;
            AdmissionId=Guid.NewGuid().ToString("D");
        }
        public string OperationId { get; }
        public long WriterEpoch { get; }
        public HistoricalPublicationProof PublicationProof { get; }
        public string AdmissionId { get; }
        /// <summary>Measures the original process-local monotonic window. This
        /// does not persist or renew a grant and cannot authorize a later commit.</summary>
        public HistoricalLocalCommitWindow CaptureCommitWindow(TimeSpan minimumRemaining)
        {
            if(minimumRemaining<TimeSpan.Zero||minimumRemaining>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(minimumRemaining));
            if(PublicationProof==null)throw new InvalidOperationException("history_publication_content_required");
            PublicationProof.DemandValid();_demandActor();
            var now=_clock();var minimumMilliseconds=(long)decimal.Ceiling((decimal)minimumRemaining.TotalMilliseconds);
            var minimumTicks=(long)decimal.Ceiling((decimal)minimumMilliseconds*_frequency/1000);
            if(now<_start||now>=_deadline||_deadline-now<minimumTicks)throw new InvalidOperationException("history_local_write_grant_expired");
            var elapsed=(long)decimal.Ceiling((decimal)(now-_start)*1000/_frequency);
            if(_remainingMilliseconds-_requestElapsedMilliseconds-elapsed-10000<minimumMilliseconds)
                throw new InvalidOperationException("history_local_write_grant_expired");
            return new HistoricalLocalCommitWindow(AdmissionId,PublicationProof,_remainingMilliseconds,_requestElapsedMilliseconds,elapsed,minimumMilliseconds);
        }
        public void DemandStillValid()
        {
            _demandActor();var now=_clock();if(now<_start||now>=_deadline)throw new InvalidOperationException("history_local_write_grant_expired");
        }
    }

    /// <summary>Immutable measurement from a live trusted grant, not a replayable
    /// mutation capability or proof of physical commit. Data seals it atomically.</summary>
    public sealed class HistoricalLocalCommitWindow
    {
        internal HistoricalLocalCommitWindow(string admission,HistoricalPublicationProof proof,int remaining,long requestElapsed,long elapsed,long minimumRemaining)
        {AdmissionId=admission;Proof=proof;RemainingMilliseconds=remaining;RequestElapsedMilliseconds=requestElapsed;ElapsedMilliseconds=elapsed;MinimumRemainingMilliseconds=minimumRemaining;}
        public string AdmissionId{get;}
        public HistoricalPublicationProof Proof{get;}
        public int RemainingMilliseconds{get;}
        public long RequestElapsedMilliseconds{get;}
        public long ElapsedMilliseconds{get;}
        public long MinimumRemainingMilliseconds{get;}
    }
}
