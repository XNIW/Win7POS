using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed partial class PosOnlineSyncSupervisorHost
    {
        private readonly Func<Win7POS.Data.SqliteConnectionFactory,HistoricalBaselineSource> _baselineSourceFactory;
        private async Task<HistoricalBaselinePreparation> PrepareHistoricalBaselineAsync(OnlineSyncLaneExecutionContext context,
            HistoricalBaselineContext baseline,HistoricalSalesDatasetState state,CancellationToken ct)
        {
            if(baseline?.State!="missing"||baseline.CanPrepare!=true||baseline.WriterAuthority!="local")
                throw new HistoricalCloudRequestException("history_baseline_prepare_denied",true,false);
            if(!_store.TryReadGeneration(context.Generation,out _,out _))throw new OnlineSyncGenerationChangedException();
            var expected=_factory.AuthorityStamp;
            void Demand()
            {
                ct.ThrowIfCancellationRequested();var current=_factory.AuthorityStamp;
                if(CurrentGeneration?.Fingerprint!=context.Generation.Fingerprint||current.RestoreEpoch!=expected.RestoreEpoch||current.AuthorityInstanceEpoch!=expected.AuthorityInstanceEpoch)
                    throw new OnlineSyncGenerationChangedException();
            }
            // Only trusted WPF composition can issue this internal Core
            // capability. The manifest and Data layer cannot mint authority.
            Demand();var guard=new HistoricalCloudExportGuard(context.Generation.ShopId,context.Generation.ShopDeviceId,state.DatasetId,state.DataRevision,expected.RestoreEpoch,Demand);
            try
            {
                var receipt=await _baselineSourceFactory(_factory).PrepareAsync(guard,Guid.Parse(state.RestoreLineageId).ToString("D"),ct).ConfigureAwait(false);
                Demand();return new HistoricalBaselinePreparation(receipt,guard);
            }
            catch(InvalidOperationException e) when(e.Message=="history_cloud_native_sync_required"||e.Message=="history_cloud_history_sync_required")
            {throw new HistoricalCloudRequestException("history_baseline_waiting_for_sync",false,false);}
            catch(IOException){throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
            catch(UnauthorizedAccessException){throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
        }
    }
}
