using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos
{
    public sealed partial class PosWorkflowService
    {
        public async Task<HistoricalQualificationContext> ReadHistoricalQualificationAsync(OperatorSession session,CancellationToken ct)
        {
            var permissions=new PermissionService(session);
            permissions.Demand(PermissionCodes.RegisterImportHistory,PermissionCodes.RegisterImportHistory);
            var version=session.CurrentOperatorAuthorityVersion;var stamp=_factory.AuthorityStamp;
            var shop=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if(string.IsNullOrWhiteSpace(shop?.ShopId))throw new InvalidOperationException("historical_shop_binding_required");
            var actor=session.CaptureHistoricalCloudActor(shop.ShopId);
            var result=await RequestHistoricalCloudAsync(actor,"qualified_config",new HistoricalCloudPayload(),ct).ConfigureAwait(false);
            var mapped=HistoricalQualificationResponseMapper.RequireContext(result,shop.ShopId);
            var current=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            actor.DemandCurrent();ct.ThrowIfCancellationRequested();
            permissions.Demand(PermissionCodes.RegisterImportHistory,PermissionCodes.RegisterImportHistory);
            var after=_factory.AuthorityStamp;
            if(session.CurrentOperatorAuthorityVersion!=version||current?.ShopId!=shop.ShopId||
                after.RestoreEpoch!=stamp.RestoreEpoch||after.AuthorityInstanceEpoch!=stamp.AuthorityInstanceEpoch)
                throw new InvalidOperationException("historical_read_scope_changed");
            return mapped;
        }
    }
}
