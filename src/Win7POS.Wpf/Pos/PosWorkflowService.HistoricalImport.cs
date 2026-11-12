using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos
{
    public sealed class HistoricalImportContext
    {
        public string ShopId { get; internal set; }
        public string ShopCode { get; internal set; }
        public string DatasetId { get; internal set; }
        public long DataRevision { get; internal set; }
        public long RestoreEpoch { get; internal set; }
    }

    public sealed partial class PosWorkflowService
    {
        internal async Task<IDisposable> EnterHistoricalWorkflowGateForTestingAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false); return new HistoricalWorkflowGateLease(_gate);
        }
        private sealed class HistoricalWorkflowGateLease : IDisposable
        {
            private SemaphoreSlim _held;
            internal HistoricalWorkflowGateLease(SemaphoreSlim held) { _held = held; }
            public void Dispose() => Interlocked.Exchange(ref _held, null)?.Release();
        }
        private int _historicalMaintenance;
        private TimeSpan _historicalAdmissionBudget = TimeSpan.FromSeconds(60);
        internal void SetHistoricalAdmissionBudgetForTesting(TimeSpan budget)
        {
            // A synthetic regression may shorten the deadline; it cannot relax it.
            if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(budget));
            _historicalAdmissionBudget = budget;
        }
        public bool IsHistoricalMaintenanceActive => Volatile.Read(ref _historicalMaintenance) != 0;
        private void DemandHistoricalCartAvailable() { if (IsHistoricalMaintenanceActive) throw new InvalidOperationException("historical_maintenance_active"); }
        private HistoricalSalesImportService HistoricalLedger => new HistoricalSalesImportService(_factory, () => _factory.AuthorityStamp);
        private Func<HistoricalSalesImportAnalyzer> _historicalAnalyzerFactoryForTesting;
        internal void SetHistoricalAnalyzerFactoryForTesting(Func<HistoricalSalesImportAnalyzer> factory) => _historicalAnalyzerFactoryForTesting = factory;
        public Task<HistoricalSalesImportAnalysis> AnalyzeHistoricalSalesAsync(IReadOnlyList<string> paths, HistoricalSalesImportOptions options, CancellationToken ct)
            => Task.Run(() => (_historicalAnalyzerFactoryForTesting?.Invoke() ?? new HistoricalSalesImportAnalyzer()).Analyze(paths, options, ct), ct);
        public async Task<HistoricalImportContext> GetHistoricalImportContextAsync(OperatorSession session, CancellationToken ct)
        {
            new PermissionService(session).Demand(PermissionCodes.RegisterImportHistory, PermissionCodes.RegisterImportHistory);
            var shop = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(shop?.ShopId) || string.IsNullOrWhiteSpace(shop.ShopCode)) throw new InvalidOperationException("historical_shop_binding_required");
            var state = await HistoricalLedger.ReadDatasetAsync(ct).ConfigureAwait(false);
            return new HistoricalImportContext { ShopId = shop.ShopId, ShopCode = shop.ShopCode, DatasetId = state.DatasetId, DataRevision = state.DataRevision, RestoreEpoch = _factory.AuthorityStamp.RestoreEpoch };
        }
        public async Task<IReadOnlyList<HistoricalMerchantBindingInfo>> GetHistoricalMerchantBindingsAsync(OperatorSession session, CancellationToken ct)
        {
            var context = await GetHistoricalImportContextAsync(session, ct).ConfigureAwait(false);
            return await HistoricalLedger.GetMerchantBindingsAsync(context.ShopId, ct).ConfigureAwait(false);
        }
        public async Task<IReadOnlyList<HistoricalSalesBatch>> ReadHistoricalBatchesAsync(OperatorSession session, CancellationToken ct)
        {
            var version = session.CurrentOperatorAuthorityVersion;
            new PermissionService(session).Demand(PermissionCodes.RegisterViewAll, PermissionCodes.RegisterViewAll);
            var shop = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(shop?.ShopId)) throw new InvalidOperationException("historical_shop_binding_required");
            var rows = await HistoricalLedger.GetBatchesAsync(shop.ShopId, ct).ConfigureAwait(false);
            var current = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            new PermissionService(session).Demand(PermissionCodes.RegisterViewAll, PermissionCodes.RegisterViewAll);
            if (session.CurrentOperatorAuthorityVersion != version || current?.ShopId != shop.ShopId) throw new InvalidOperationException("historical_read_scope_changed");
            return rows.ToArray();
        }
        public async Task<HistoricalSalesOperationReceipt> ReadHistoricalReceiptAsync(string operationId, OperatorSession session, CancellationToken ct)
        {
            var version = session.CurrentOperatorAuthorityVersion;
            var permission = new PermissionService(session); permission.Demand(PermissionCodes.RegisterViewAll, PermissionCodes.RegisterViewAll);
            var shop = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(shop?.ShopId)) throw new InvalidOperationException("historical_shop_binding_required");
            var receipt = await HistoricalLedger.GetReceiptAsync(operationId, shop.ShopId, ct).ConfigureAwait(false);
            var current = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); permission.Demand(PermissionCodes.RegisterViewAll, PermissionCodes.RegisterViewAll);
            if (session.CurrentOperatorAuthorityVersion != version || current?.ShopId != shop.ShopId) throw new InvalidOperationException("historical_read_scope_changed");
            return receipt;
        }
        public async Task<HistoricalPublicationSummary> ReadHistoricalPublicationSummaryAsync(OperatorSession session,CancellationToken ct)
        {
            var version=session.CurrentOperatorAuthorityVersion;var stamp=_factory.AuthorityStamp;
            var permissions=new PermissionService(session);permissions.Demand(PermissionCodes.RegisterViewAll,PermissionCodes.RegisterViewAll);
            var shop=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if(string.IsNullOrWhiteSpace(shop?.ShopId))throw new InvalidOperationException("historical_shop_binding_required");
            var summary=await Task.Run(()=>new HistoricalPublicationOutbox(_factory).ReadSummaryAsync(shop.ShopId,ct),ct).ConfigureAwait(false);
            var current=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);ct.ThrowIfCancellationRequested();
            permissions.Demand(PermissionCodes.RegisterViewAll,PermissionCodes.RegisterViewAll);var after=_factory.AuthorityStamp;
            if(session.CurrentOperatorAuthorityVersion!=version||current?.ShopId!=shop.ShopId||stamp.RestoreEpoch!=after.RestoreEpoch||stamp.AuthorityInstanceEpoch!=after.AuthorityInstanceEpoch)
                throw new InvalidOperationException("historical_read_scope_changed");
            return summary;
        }
        public Task<HistoricalSalesApplyPreview> PreviewHistoricalSalesAsync(HistoricalSalesImportPlan plan, CancellationToken ct)
            => Task.Run(() => HistoricalLedger.PreviewAsync(plan, ct), ct);
        public async Task<HistoricalSalesOperationReceipt> ApplyHistoricalSalesAsync(HistoricalSalesImportPlan plan, OperatorSession session, CancellationToken ct)
        {
            return await RunHistoricalMutationAsync(HistoricalImportOperationKind.Apply, plan.OperationId, plan.RequestFingerprint, plan.ShopId,
                plan.DatasetId, plan.RestoreEpoch, session, plan,
                (device,token)=>HistoricalLedger.PreparePublicationDraftAsync(plan,device,token),
                (guard, sources, admission, admitted, draft, proof) => HistoricalLedger.ApplyAsync(plan, guard, AllocateDbBackupPath("pos_pre_history_"), ct, sources, admission, admitted,draft,proof), ct).ConfigureAwait(false);
        }
        public async Task<HistoricalSalesOperationReceipt> UndoHistoricalSalesAsync(HistoricalSalesBatch batch, OperatorSession session, CancellationToken ct)
        {
            var shop = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(shop?.ShopId)) throw new InvalidOperationException("historical_shop_binding_required");
            var state = await HistoricalLedger.ReadDatasetAsync(ct).ConfigureAwait(false);
            var epoch = _factory.AuthorityStamp.RestoreEpoch;
            var op = Guid.NewGuid().ToString("N");
            var fingerprint = HistoricalSalesImportPlan.UndoFingerprint(op, shop.ShopId, state.DatasetId, batch.Id, batch.Revision, epoch);
            return await RunHistoricalMutationAsync(HistoricalImportOperationKind.Undo, op, fingerprint, shop.ShopId, state.DatasetId, epoch, session, null,
                (device,token)=>HistoricalLedger.PrepareUndoPublicationDraftAsync(op,shop.ShopId,device,state.DatasetId,epoch,batch.Id,batch.Revision,token),
                (guard, sources, admission, admitted, draft, proof) => HistoricalLedger.UndoAsync(batch.Id, batch.Revision, guard, AllocateDbBackupPath("pos_pre_history_undo_"), ct, admission, admitted,draft,proof), ct).ConfigureAwait(false);
        }
        private async Task<HistoricalSalesOperationReceipt> RunHistoricalMutationAsync(HistoricalImportOperationKind kind, string op, string fingerprint, string shopId,
            string dataset, long epoch, OperatorSession session, HistoricalSalesImportPlan sourcePlan,
            Func<string,CancellationToken,Task<HistoricalPublicationDraft>> preparePublication,
            Func<HistoricalImportAuthorizationCommitGuard, HistoricalSalesVerifiedSources, CancellationToken, Action, HistoricalPublicationDraft,HistoricalPublicationProof,Task<HistoricalSalesOperationReceipt>> mutation, CancellationToken ct)
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var readAuthority = session.CaptureHistoricalReadAuthority(kind, shopId);
            var replay = await HistoricalLedger.TryReplayAsync(new HistoricalSalesOperationIdentity(kind, op, fingerprint, shopId, dataset), admission.Token).ConfigureAwait(false);
            if (replay != null)
            {
                HistoricalSalesOperationReceipt authorized = null;
                readAuthority.Publish(() => authorized = replay);
                SignalHistoricalPublication();
                return authorized;
            }
            // Hash and hold inputs before acquiring workflow/auth/catalog/maintenance locks.
            using var publicationUse=HistoricalPublicationOperationUse.Begin(_factory,op);
            using var sources = sourcePlan == null ? null : await HistoricalLedger.VerifySourcesAsync(sourcePlan, admission.Token).ConfigureAwait(false);
            readAuthority.DemandStillValid();
            var expectedAuthority = _factory.AuthorityStamp;
            // All HTTP admission finishes before backup maintenance, workflow
            // serialization or the catalog barrier. Commit rechecks this stamp.
            var actor=session.CaptureHistoricalCloudActor(shopId);
            using var publication=await preparePublication(actor.Trusted.ShopDeviceId,admission.Token).ConfigureAwait(false);
            actor.DemandCurrent();
            var writerGrant = await AcquireHistoricalLocalWriteAsync(session,shopId,op,kind,publication,admission.Token).ConfigureAwait(false);
            // Content staging precedes the short admission/maintenance window.
            // No fallback can create a new commit with a v1 identity-only grant.
            admission.CancelAfter(_historicalAdmissionBudget);
            readAuthority.DemandStillValid();
            if (Interlocked.CompareExchange(ref _historicalMaintenance, 1, 0) != 0) throw new InvalidOperationException("historical_maintenance_active");
            var ownsGate = false;
            try
            {
                using var backupLease = await _backupAutomation.EnterMaintenanceAsync(admission.Token).ConfigureAwait(false);
                await _gate.WaitAsync(admission.Token).ConfigureAwait(false); ownsGate = true;
                using var authorization = await session.BeginHistoricalAuthorizationUseAsync(kind, op, fingerprint, dataset, epoch, () =>
                {
                    var current = _factory.AuthorityStamp;
                    writerGrant.DemandStillValid();
                    if (current.RestoreEpoch != epoch || current.AuthorityInstanceEpoch != expectedAuthority.AuthorityInstanceEpoch) throw new InvalidOperationException("historical_database_authority_changed");
                }, admission.Token).ConfigureAwait(false);
                authorization.Guard.AttachPublicationGrant(writerGrant);
                using var catalog = await new CatalogShopTransitionBarrier(_factory).EnterAsync(admission.Token).ConfigureAwait(false);
                var receipt = await Task.Run(() => mutation(authorization.Guard, sources, admission.Token,
                    () => admission.CancelAfter(Timeout.InfiniteTimeSpan),publication,writerGrant.PublicationProof), ct).ConfigureAwait(false);
                SignalHistoricalPublication();
                return receipt;
            }
            finally { if (ownsGate) _gate.Release(); Interlocked.Exchange(ref _historicalMaintenance, 0); }
        }
        private static void SignalHistoricalPublication()
        {
            try { PosOnlineSyncSignalBus.Signal(Win7POS.Core.Online.OnlineSyncLane.HistoricalPayments,
                Win7POS.Core.Online.OnlineSyncLaneTrigger.LocalCommit); } catch { }
        }
    }
}
