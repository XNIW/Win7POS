using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos
{
    public sealed partial class PosWorkflowService
    {
        private Func<HistoricalCloudActor,string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> _historyCloudRequestForTesting;
        internal void SetHistoricalCloudRequestForTesting(Func<HistoricalCloudActor,string,HistoricalCloudPayload,CancellationToken,Task<HistoricalCloudResult>> request)
            =>_historyCloudRequestForTesting=request;
        private async Task<HistoricalCloudResult> RequestHistoricalCloudAsync(HistoricalCloudActor actor,string operation,HistoricalCloudPayload payload,CancellationToken ct)
        {
            actor.DemandCurrent();HistoricalCloudResult value;
            if(_historyCloudRequestForTesting!=null)value=await _historyCloudRequestForTesting(actor,operation,payload,ct).ConfigureAwait(false);
            else
            {
                if(!PosAdminWebOptions.TryLoad(out var options,out _))throw new InvalidOperationException("history_cloud_connection_required");
                using var client=new PosAdminWebClient(options);var trusted=actor.Trusted;
                if(!Online.PosOnlineSyncSupervisorHost.TryCreateGeneration(trusted,out var generation))throw new OnlineSyncGenerationChangedException();
                var response=await Online.PosOnlineSyncSignalBus.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,
                    (credentials,token)=>client.HistoricalCloudAsync(new HistoricalCloudRequest{DeviceId=credentials.ShopDeviceId,SessionId=credentials.PosSessionId,
                        DeviceToken=credentials.DeviceToken,SessionToken=credentials.SessionToken,Operation=operation,Payload=payload??new HistoricalCloudPayload()},token),
                    result=>result!=null&&SharedAuthStopPolicy.IsAuthenticationDenied(result.Code)?result.Code:string.Empty,ct).ConfigureAwait(false);
                if(!response.Success&&response.Code=="history_resnapshot_required")throw new HistoricalCloudResnapshotRequiredException();
                if(!response.Success||response.Value?.Ok!=true||response.Value.ShopId!=trusted.ShopId||response.Value.DeviceId!=trusted.ShopDeviceId||response.Value.SessionId!=trusted.PosSessionId||response.Value.Result==null)throw new InvalidOperationException("history_cloud_request_rejected");
                value=response.Value.Result;
            }
            actor.DemandCurrent();ct.ThrowIfCancellationRequested();return value??throw new InvalidOperationException("history_cloud_response_invalid");
        }
        private async Task<HistoricalLocalWriteGrant> AcquireHistoricalLocalWriteAsync(OperatorSession session,string shop,string operation,HistoricalImportOperationKind kind,HistoricalPublicationDraft draft,CancellationToken ct)
        {
            var actor=session.CaptureHistoricalCloudActor(shop);
            if(draft==null||draft.ShopId!=shop||draft.DeviceId!=actor.Trusted.ShopDeviceId||draft.OperationId!=operation||draft.Kind!=kind.ToString())
                throw new InvalidOperationException("history_publication_content_required");
            var authority=await RequestHistoricalCloudAsync(actor,"authority",null,ct).ConfigureAwait(false);
            await new HistoricalReplicaStore(_factory,actor.DemandCurrent).ObserveAuthorityAsync(shop,authority,ct).ConfigureAwait(false);
            if(authority.WriterAuthority!="local"||authority.ProtocolVersion!=1)throw new InvalidOperationException("history_local_writer_closed");
            var granted=await new HistoricalPublicationPreparer(actor.DemandCurrent,
                (op,payload,token)=>RequestHistoricalCloudAsync(actor,op,payload,token)).PrepareAsync(draft,authority,ct).ConfigureAwait(false);
            return new HistoricalLocalWriteGrant(Guid.Parse(operation).ToString("D"),authority.WriterEpoch,granted.RemainingMilliseconds,
                granted.GrantRequestElapsedMilliseconds,actor.DemandCurrent,publicationProof:granted.Proof);
        }
        public async Task<HistoricalReplicaState> ReadHistoricalCloudStateAsync(OperatorSession session,CancellationToken ct)
        {
            var shop=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);var actor=session.CaptureHistoricalCloudActor(shop?.ShopId);
            return await new HistoricalReplicaStore(_factory,actor.DemandCurrent).ReadAsync(actor.Trusted.ShopId,ct).ConfigureAwait(false);
        }
        private readonly SemaphoreSlim _historicalReplicaTurn = new SemaphoreSlim(1,1);
        /// <summary>Diagnostic entry to the same bounded replica turn; no commercial maintenance.</summary>
        public async Task SynchronizeHistoricalCloudAsync(OperatorSession session,string clientBuildSha,CancellationToken ct)
        {
            var shop=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            var actor=session.CaptureHistoricalCloudActor(shop?.ShopId);
            if(_historyCloudRequestForTesting==null)
            {
                var outcome=await Online.PosOnlineSyncSignalBus.TriggerAsync(OnlineSyncLane.HistoricalPayments,
                    OnlineSyncLaneTrigger.Manual,ct).ConfigureAwait(false);
                actor.DemandCurrent();
                if(outcome?.Success!=true)throw new InvalidOperationException("history_cloud_sync_incomplete");
                return;
            }
            var authorityStamp=_factory.AuthorityStamp;
            Action demand=()=>
            {
                actor.DemandCurrent();var current=_factory.AuthorityStamp;
                if(current.RestoreEpoch!=authorityStamp.RestoreEpoch||current.AuthorityInstanceEpoch!=authorityStamp.AuthorityInstanceEpoch)
                    throw new InvalidOperationException("history_cloud_database_changed");
            };
            if(!Online.PosOnlineSyncSupervisorHost.TryCreateGeneration(actor.Trusted,out var generation))
                throw new InvalidOperationException("history_cloud_authorization_denied");
            await _historicalReplicaTurn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var store=new HistoricalReplicaStore(_factory,demand,generation);
                await new HistoricalReplicaSynchronizer(store,demand,(operation,payload,token)=>RequestHistoricalCloudAsync(actor,operation,payload,token))
                    .RunAsync(actor.Trusted.ShopId,clientBuildSha,ct).ConfigureAwait(false);
            }
            finally{_historicalReplicaTurn.Release();}
        }
    }
}
