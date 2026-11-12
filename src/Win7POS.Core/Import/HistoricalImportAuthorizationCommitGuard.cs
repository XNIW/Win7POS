using System;

namespace Win7POS.Core.Import
{
    public enum HistoricalImportOperationKind { Apply, Undo }

    /// <summary>A dedicated authority capability. Only trusted product composition can issue it.</summary>
    public sealed class HistoricalImportAuthorizationCommitGuard
    {
        private readonly Action _demand;
        private readonly Action<TimeSpan, Action> _commit;
        internal HistoricalImportAuthorizationCommitGuard(HistoricalImportOperationKind kind,
            string operationId, string requestFingerprint, string shopId, string shopCode,
            string deviceId, int operatorId, string datasetId, long restoreEpoch,
            long authorizationEpoch, string generationFingerprint, string staffId, int credentialVersion,
            Action demand, Action<TimeSpan, Action> commit)
        {
            foreach (var value in new[] { operationId, requestFingerprint, shopId, shopCode, deviceId, datasetId, generationFingerprint })
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Incomplete historical authorization binding.");
            Kind = kind; OperationId = operationId; RequestFingerprint = requestFingerprint;
            ShopId = shopId; ShopCode = shopCode; DeviceId = deviceId; OperatorId = operatorId;
            DatasetId = datasetId; RestoreEpoch = restoreEpoch; AuthorizationEpoch = authorizationEpoch;
            GenerationFingerprint = generationFingerprint; StaffId = staffId; CredentialVersion = credentialVersion;
            _demand = demand ?? throw new ArgumentNullException(nameof(demand));
            _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        }
        public HistoricalImportOperationKind Kind { get; }
        public string OperationId { get; }
        public string RequestFingerprint { get; }
        public string ShopId { get; }
        public string ShopCode { get; }
        public string DeviceId { get; }
        public int OperatorId { get; }
        public string DatasetId { get; }
        public long RestoreEpoch { get; }
        public long AuthorizationEpoch { get; }
        public string GenerationFingerprint { get; }
        public string StaffId { get; }
        public int CredentialVersion { get; }
        private HistoricalLocalWriteGrant _publicationGrant;
        internal void AttachPublicationGrant(HistoricalLocalWriteGrant grant)
        {
            var proof=grant?.PublicationProof;
            if(_publicationGrant!=null||proof==null||grant.OperationId!=Guid.Parse(OperationId).ToString("D")||proof.ShopId!=ShopId||proof.DeviceId!=DeviceId
                ||proof.DatasetId!=DatasetId||proof.RestoreEpoch!=RestoreEpoch||proof.RequestFingerprint!=RequestFingerprint)
                throw new InvalidOperationException("history_publication_proof_mismatch");
            grant.DemandStillValid();_publicationGrant=grant;
        }
        public HistoricalLocalCommitWindow CapturePublicationCommitWindow(TimeSpan minimumRemaining)
        {
            _demand();
            return (_publicationGrant??throw new InvalidOperationException("history_publication_live_admission_required")).CaptureCommitWindow(minimumRemaining);
        }
        public void DemandStillValid() => _demand();
        public void CommitIfStillValid(TimeSpan minimumRemaining, Action commit)
        {
            if (minimumRemaining < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumRemaining));
            _commit(minimumRemaining, commit ?? throw new ArgumentNullException(nameof(commit)));
        }
    }
}
