using System;
using Win7POS.Core.Models;

namespace Win7POS.Core.Pos
{
    /// <summary>
    /// Immutable, non-forgeable authorization capability consumed by the
    /// repository-native sale transaction, scoped to its economic operation. Only the trusted WPF
    /// authorization authority and the test assembly can construct one.
    /// </summary>
    public sealed class SaleAuthorizationCommitGuard
    {
        private readonly Action _demandStillValid;
        private readonly Action<TimeSpan, Action> _commitIfStillValid;

        internal SaleAuthorizationCommitGuard(
            long authorizationEpoch,
            string generationFingerprint,
            string generationId,
            int operatorId,
            string shopCode,
            string shopDeviceId,
            string shopId,
            int staffCredentialVersion,
            string staffId,
            Action demandStillValid,
            Action<TimeSpan, Action> commitIfStillValid,
            SaleKind authorizedSaleKind = SaleKind.Sale)
        {
            if (authorizedSaleKind != SaleKind.Sale &&
                authorizedSaleKind != SaleKind.Refund &&
                authorizedSaleKind != SaleKind.Void)
                throw new ArgumentOutOfRangeException(nameof(authorizedSaleKind));
            AuthorizedSaleKind = authorizedSaleKind;
            AuthorizationEpoch = authorizationEpoch;
            GenerationFingerprint = generationFingerprint;
            GenerationId = generationId;
            OperatorId = operatorId;
            ShopCode = shopCode;
            ShopDeviceId = shopDeviceId;
            ShopId = shopId;
            StaffCredentialVersion = staffCredentialVersion;
            StaffId = staffId;
            _demandStillValid = demandStillValid ??
                throw new ArgumentNullException(nameof(demandStillValid));
            _commitIfStillValid = commitIfStillValid ??
                throw new ArgumentNullException(nameof(commitIfStillValid));
        }

        public long AuthorizationEpoch { get; }
        public SaleKind AuthorizedSaleKind { get; }
        public string GenerationFingerprint { get; }
        public string GenerationId { get; }
        public int OperatorId { get; }
        public string ShopCode { get; }
        public string ShopDeviceId { get; }
        public string ShopId { get; }
        public int StaffCredentialVersion { get; }
        public string StaffId { get; }

        public void DemandStillValid()
        {
            _demandStillValid();
        }

        public void CommitIfStillValid(Action commit)
        {
            CommitIfStillValid(TimeSpan.Zero, commit);
        }

        public void CommitIfStillValid(
            TimeSpan minimumRemaining,
            Action commit)
        {
            if (minimumRemaining < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minimumRemaining));
            }
            if (commit == null)
                throw new ArgumentNullException(nameof(commit));
            _commitIfStillValid(minimumRemaining, commit);
        }
    }
}
