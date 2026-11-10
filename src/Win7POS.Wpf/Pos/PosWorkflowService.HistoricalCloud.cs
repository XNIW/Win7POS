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
        private sealed class HistoricalCloudResnapshotRequired:InvalidOperationException{internal HistoricalCloudResnapshotRequired():base("history_cloud_resnapshot_required"){} }
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
                var response=await client.HistoricalCloudAsync(new HistoricalCloudRequest{DeviceId=trusted.ShopDeviceId,SessionId=trusted.PosSessionId,DeviceToken=trusted.DeviceToken,SessionToken=trusted.SessionToken,Operation=operation,Payload=payload??new HistoricalCloudPayload()},ct).ConfigureAwait(false);
                if(!response.Success&&response.Code=="history_resnapshot_required")throw new HistoricalCloudResnapshotRequired();
                if(!response.Success||response.Value?.Ok!=true||response.Value.ShopId!=trusted.ShopId||response.Value.DeviceId!=trusted.ShopDeviceId||response.Value.SessionId!=trusted.PosSessionId||response.Value.Result==null)throw new InvalidOperationException("history_cloud_request_rejected");
                value=response.Value.Result;
            }
            actor.DemandCurrent();ct.ThrowIfCancellationRequested();return value??throw new InvalidOperationException("history_cloud_response_invalid");
        }
        private async Task<HistoricalLocalWriteGrant> AcquireHistoricalLocalWriteAsync(OperatorSession session,string shop,string operation,HistoricalImportOperationKind kind,CancellationToken ct)
        {
            var actor=session.CaptureHistoricalCloudActor(shop);var elapsed=Stopwatch.StartNew();
            var authority=await RequestHistoricalCloudAsync(actor,"authority",null,ct).ConfigureAwait(false);
            await new HistoricalReplicaStore(_factory,actor.DemandCurrent).ObserveAuthorityAsync(shop,authority,ct).ConfigureAwait(false);
            if(authority.WriterAuthority!="local"||authority.ProtocolVersion!=1)throw new InvalidOperationException("history_local_writer_closed");
            var id=Guid.Parse(operation).ToString("D");
            var granted=await RequestHistoricalCloudAsync(actor,"local_write_grant",new HistoricalCloudPayload{OperationId=id,Kind=kind.ToString(),WriterEpoch=authority.WriterEpoch},ct).ConfigureAwait(false);
            HistoricalReplicaProtocol.DemandId(granted.GrantId);
            if(granted.OperationId!=id||granted.WriterEpoch!=authority.WriterEpoch)throw new InvalidOperationException("history_local_write_grant_invalid");
            return new HistoricalLocalWriteGrant(id,granted.WriterEpoch,granted.RemainingMilliseconds,elapsed.ElapsedMilliseconds,actor.DemandCurrent);
        }
        public async Task<HistoricalReplicaState> ReadHistoricalCloudStateAsync(OperatorSession session,CancellationToken ct)
        {
            var shop=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);var actor=session.CaptureHistoricalCloudActor(shop?.ShopId);
            return await new HistoricalReplicaStore(_factory,actor.DemandCurrent).ReadAsync(actor.Trusted.ShopId,ct).ConfigureAwait(false);
        }
        /// <summary>Dedicated read replica, never the native sales outbox.</summary>
        public async Task SynchronizeHistoricalCloudAsync(OperatorSession session,string clientBuildSha,CancellationToken ct)
        {
            if(string.IsNullOrEmpty(clientBuildSha)||!System.Text.RegularExpressions.Regex.IsMatch(clientBuildSha,"^[0-9a-f]{40}$"))throw new InvalidOperationException("history_cloud_build_identity_required");
            var shop=await _officialShopSnapshots.GetAsync().ConfigureAwait(false);var actor=session.CaptureHistoricalCloudActor(shop?.ShopId);
            var authorityStamp=_factory.AuthorityStamp;Action demand=()=>{actor.DemandCurrent();var current=_factory.AuthorityStamp;if(current.RestoreEpoch!=authorityStamp.RestoreEpoch||current.AuthorityInstanceEpoch!=authorityStamp.AuthorityInstanceEpoch)throw new InvalidOperationException("history_cloud_database_changed");};
            var store=new HistoricalReplicaStore(_factory,demand);
            if(Interlocked.CompareExchange(ref _historicalMaintenance,1,0)!=0)throw new InvalidOperationException("historical_maintenance_active");
            var gate=false;
            try
            {
                using var backup=await _backupAutomation.EnterMaintenanceAsync(ct).ConfigureAwait(false);await _gate.WaitAsync(ct).ConfigureAwait(false);gate=true;
                var prior=await store.ReadAsync(actor.Trusted.ShopId,ct).ConfigureAwait(false);
                var authority=await RequestHistoricalCloudAsync(actor,"authority",null,ct).ConfigureAwait(false);await store.ObserveAuthorityAsync(actor.Trusted.ShopId,authority,ct).ConfigureAwait(false);
                // Epoch/dataset changes resnapshot. One explicit server request
                // for a fresh snapshot may restart this read transfer only.
                var delta=prior?.GenerationId!=null&&prior.DatasetId==authority.DatasetId&&prior.WriterEpoch==HistoricalReplicaProtocol.Integer(authority.WriterEpoch)&&prior.PublishedRevision.HasValue;
                var pending=await store.ReadPendingAsync(actor.Trusted.ShopId,ct).ConfigureAwait(false);
                for(var attempt=0;attempt<2;attempt++)
                {
                try
                {
                var manifest=attempt==0&&pending.GenerationId!=null?pending.Manifest:await RequestHistoricalCloudAsync(actor,attempt==0&&delta?"delta_start":"snapshot_start",attempt==0&&delta?
                    new HistoricalCloudPayload{DatasetId=prior.DatasetId,Revision=prior.PublishedRevision.Value.ToString(CultureInfo.InvariantCulture),WriterEpoch=authority.WriterEpoch,PageSize=200}:
                    new HistoricalCloudPayload{RequestKey=Guid.NewGuid().ToString("D"),PageSize=200},ct).ConfigureAwait(false);
                var generation=attempt==0&&pending.GenerationId!=null?pending.GenerationId:await store.BeginAsync(actor.Trusted.ShopId,manifest,ct).ConfigureAwait(false);var cursor=attempt==0&&pending.Complete?null:manifest.Cursor;var pages=0;
                while(cursor!=null)
                {
                    if(++pages>20000)throw new InvalidOperationException("history_replica_page_limit");
                    var page=await RequestHistoricalCloudAsync(actor,"page",new HistoricalCloudPayload{Cursor=cursor},ct).ConfigureAwait(false);
                    await store.StageAsync(generation,page,ct).ConfigureAwait(false);cursor=page.NextCursor;
                }
                var finalCursor=await store.PublishAsync(generation,ct).ConfigureAwait(false);
                var ack=await RequestHistoricalCloudAsync(actor,"replica_ack",new HistoricalCloudPayload{Cursor=finalCursor,Checksum=manifest.Checksum,ProtocolVersion=1,ClientBuildSha=clientBuildSha},ct).ConfigureAwait(false);
                if(!ack.Acknowledged||ack.Revision!=manifest.Revision||ack.WriterEpoch!=manifest.WriterEpoch)throw new InvalidOperationException("history_replica_ack_invalid");
                break;
                }
                catch(HistoricalCloudResnapshotRequired) when(attempt==0){actor.DemandCurrent();}
                }
            }
            catch{try{await store.MarkStaleAsync(actor.Trusted.ShopId,CancellationToken.None).ConfigureAwait(false);}catch{}throw;}
            finally{if(gate)_gate.Release();Interlocked.Exchange(ref _historicalMaintenance,0);}
        }
    }
}
