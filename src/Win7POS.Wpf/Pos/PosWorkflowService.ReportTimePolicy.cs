using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Online;
using Win7POS.Data.Reports;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos
{
    public sealed class SalesReportTimePolicyActivationResult
    {
        internal SalesReportTimePolicyActivationResult(string hash, string shopId, string backupPath) { PolicyHash = hash; ShopId = shopId; BackupPath = backupPath; }
        public string PolicyHash { get; }
        public string ShopId { get; }
        public string BackupPath { get; }
    }

    public sealed partial class PosWorkflowService
    {
        internal async Task<bool> HasVerifiedNativeReportPolicyAsync(OperatorSession session, CancellationToken token)
        {
            if (session == null || !session.IsLoggedIn || !session.EnsureAuthorizationValid()) return false;
            var version = session.CurrentOperatorAuthorityVersion;
            var official = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if (official == null || string.IsNullOrWhiteSpace(official.ShopId)) return false;
            var policy = await new SalesReportTimePolicyRepository(_factory).GetActiveAsync(official.ShopId, token).ConfigureAwait(false);
            return policy != null && ReferenceEquals(session, OperatorSessionHolder.Current) &&
                session.CurrentOperatorAuthorityVersion == version && session.IsLoggedIn && session.EnsureAuthorizationValid();
        }
        public Task<SalesReportTimePolicyActivationResult> ConfigureSalesReportTimePolicyAsync(ApprovedSalesReportTimePolicy approved,
            string evidenceReference, HistoricalImportContext reviewedContext, OperatorSession session, CancellationToken cancellationToken)
        {
            if (approved == null || session == null || reviewedContext == null) throw new ArgumentNullException();
            if (string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 500) throw new ArgumentException("report_time_policy_evidence_required");
            return Task.Run(async () =>
            {
                var context = await GetHistoricalImportContextAsync(session, cancellationToken).ConfigureAwait(false);
                if (context.ShopId != reviewedContext.ShopId || context.DatasetId != reviewedContext.DatasetId || context.RestoreEpoch != reviewedContext.RestoreEpoch)
                    throw new InvalidOperationException("report_time_policy_context_changed");
                if (Interlocked.CompareExchange(ref _historicalMaintenance, 1, 0) != 0) throw new InvalidOperationException("historical_maintenance_active");
                var ownsGate = false;
                IDisposable backupLease = null;
                try
                {
                    // Source IO and revalidation precede admission; the handle holds the reviewed source unchanged.
                    using var source = await SalesReportTimePolicyRepository.VerifySourceAndHoldAsync(approved, cancellationToken).ConfigureAwait(false);
                    using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    admission.CancelAfter(TimeSpan.FromSeconds(60));
                    backupLease = await _backupAutomation.EnterMaintenanceAsync(admission.Token).ConfigureAwait(false);
                    await _gate.WaitAsync(admission.Token).ConfigureAwait(false); ownsGate = true;
                    var expectedAuthority = _factory.AuthorityStamp;
                    using var authorization = await session.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply,
                        Guid.NewGuid().ToString("N"), approved.Policy.Hash, context.DatasetId, context.RestoreEpoch, () =>
                        {
                            var current = _factory.AuthorityStamp;
                            if (current.RestoreEpoch != context.RestoreEpoch || current.AuthorityInstanceEpoch != expectedAuthority.AuthorityInstanceEpoch)
                                throw new InvalidOperationException("report_time_policy_database_changed");
                        }, admission.Token).ConfigureAwait(false);
                    if (authorization.Guard.ShopId != context.ShopId) throw new InvalidOperationException("report_time_policy_shop_changed");
                    using var catalog = await new CatalogShopTransitionBarrier(_factory).EnterAsync(admission.Token).ConfigureAwait(false);
                    var backupPath = AllocateDbBackupPath("pos_pre_report_timepolicy_");
                    // The same 60-second admission token covers every preceding gate and the factory gate/drain.
                    // Disable its timer only after RunExclusiveMaintenanceAsync has actually admitted this body.
                    await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
                    {
                        admission.CancelAfter(Timeout.InfiniteTimeSpan);
                        authorization.Guard.DemandStillValid();
                        await new SqliteOnlineBackup(_factory).CreateVerifiedAsync(backupPath, cancellationToken).ConfigureAwait(false);
                        authorization.Guard.DemandStillValid();
                        await new SalesReportTimePolicyRepository(_factory).SaveApprovedAsync(context.ShopId, approved.Policy,
                            evidenceReference, authorization.Guard, cancellationToken, approved.SourceSha256).ConfigureAwait(false);
                    }, TimeSpan.FromSeconds(30), admission.Token).ConfigureAwait(false);
                    // A successful commit remains successful even if cancel/revocation arrives after its linearization point.
                    return new SalesReportTimePolicyActivationResult(approved.Policy.Hash, context.ShopId, backupPath);
                }
                finally
                {
                    try { if (ownsGate) _gate.Release(); }
                    finally { try { backupLease?.Dispose(); } finally { Interlocked.Exchange(ref _historicalMaintenance, 0); } }
                }
            }, cancellationToken);
        }
    }
}
