using System;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Wpf.Pos.Online;
namespace Win7POS.Wpf.Infrastructure.Security
{
    internal sealed class HistoricalCloudActor
    {
        internal PosTrustedDeviceSession Trusted { get; set; }
        internal Action DemandCurrent { get; set; }
    }
    public sealed partial class OperatorSession
    {
        internal HistoricalCloudActor CaptureHistoricalCloudActor(string shopId)
        {
            var decision=_authorizationLeaseGuard.EvaluateAuthorizationUse(out var trusted,out _);
            if(!decision.Allowed||trusted==null||trusted.ShopId!=shopId||!PosOnlineSyncSupervisorHost.TryCreateGeneration(trusted,out var generation))throw new InvalidOperationException("history_cloud_authorization_denied");
            var read=CaptureHistoricalReadAuthorityCore(PermissionCodes.RegisterViewAll,shopId,generation.Fingerprint);
            read.DemandStillValid();return new HistoricalCloudActor{Trusted=trusted,DemandCurrent=read.DemandStillValid};
        }
    }
}
