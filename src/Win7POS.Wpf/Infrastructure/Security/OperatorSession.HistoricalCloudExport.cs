using System;
using Win7POS.Core.Import;
using Win7POS.Core.Security;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public sealed partial class OperatorSession
    {
        internal HistoricalCloudExportGuard CaptureHistoricalCloudExportGuard(string shopId, string datasetId,
            long dataRevision, long restoreEpoch, Action demandDatabaseAuthority)
        {
            if (demandDatabaseAuthority == null) throw new ArgumentNullException(nameof(demandDatabaseAuthority));
            var decision = _authorizationLeaseGuard.EvaluateAuthorizationUse(out var session, out _);
            if (!decision.Allowed || session == null || !PosOnlineSyncSupervisorHost.TryCreateGeneration(session, out var generation) || generation.ShopId != shopId)
                throw new InvalidOperationException("history_cloud_export_authorization_denied");
            var read = CaptureHistoricalReadAuthorityCore(PermissionCodes.RegisterViewAll, shopId, generation.Fingerprint);
            Action demand = () =>
            {
                read.DemandStillValid();
                new PermissionService(this).Demand(PermissionCodes.RegisterImportHistory, PermissionCodes.RegisterImportHistory);
                demandDatabaseAuthority();
            };
            demand();
            return new HistoricalCloudExportGuard(shopId, generation.ShopDeviceId, datasetId, dataRevision, restoreEpoch, demand);
        }
    }
}
