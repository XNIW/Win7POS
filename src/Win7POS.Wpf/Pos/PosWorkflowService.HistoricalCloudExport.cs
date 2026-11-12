using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Data.Import;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Win7POS.Core.Online;
using Win7POS.Core.Import;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos
{
    public sealed partial class PosWorkflowService
    {
        public static string HistoricalCloudBuildIdentity
        {
            get
            {
                var value=typeof(PosWorkflowService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a=>a.Key=="HistoricalCloudBuildSha")?.Value;
                if(value==null||!System.Text.RegularExpressions.Regex.IsMatch(value,"^[0-9a-f]{40}$"))throw new InvalidOperationException("history_cloud_build_identity_required");return value;
            }
        }
        public async Task<HistoricalCloudExportReceipt> PrepareHistoricalCloudTransferAsync(OperatorSession session,string archivePath,string verifiedBackupPath,CancellationToken ct)
        {
            var build=HistoricalCloudBuildIdentity;var context=await GetHistoricalImportContextAsync(session,ct).ConfigureAwait(false);
            var actor=session.CaptureHistoricalCloudActor(context.ShopId);var authority=await RequestHistoricalCloudAsync(actor,"authority",null,ct).ConfigureAwait(false);
            if(authority.WriterAuthority!="frozen"||authority.ProtocolVersion!=1)throw new InvalidOperationException("history_cloud_freeze_required");
            await new HistoricalReplicaStore(_factory,actor.DemandCurrent).ObserveAuthorityAsync(context.ShopId,authority,ct).ConfigureAwait(false);
            var writerEpoch=HistoricalReplicaProtocol.Integer(authority.WriterEpoch);var expected=_factory.AuthorityStamp;
            var guard=session.CaptureHistoricalCloudExportGuard(context.ShopId,context.DatasetId,context.DataRevision,context.RestoreEpoch,()=>
            {var current=_factory.AuthorityStamp;actor.DemandCurrent();if(current.RestoreEpoch!=expected.RestoreEpoch||current.AuthorityInstanceEpoch!=expected.AuthorityInstanceEpoch)throw new InvalidOperationException("history_cloud_database_changed");});
            SignalHistoricalPublication();
            var saved=new HistoricalPreparedExportStore(_factory);
            await saved.DemandHistoricalPublicationsDrainedAsync(guard,ct).ConfigureAwait(false);
            if(Interlocked.CompareExchange(ref _historicalMaintenance,1,0)!=0)throw new InvalidOperationException("historical_maintenance_active");var gate=false;
            HistoricalCloudExportReceipt receipt;
            try
            {
                using var backup=await _backupAutomation.EnterMaintenanceAsync(ct).ConfigureAwait(false);await _gate.WaitAsync(ct).ConfigureAwait(false);gate=true;
                await saved.DemandNativeSalesDrainedAsync(guard,ct).ConfigureAwait(false);
                await saved.DemandHistoricalPublicationsDrainedAsync(guard,ct).ConfigureAwait(false);
                receipt=await saved.ReadVerifiedAsync(writerEpoch,guard,ct).ConfigureAwait(false);
                if(receipt==null)
                {
                    if(string.IsNullOrEmpty(archivePath)||string.IsNullOrEmpty(verifiedBackupPath))throw new InvalidOperationException("history_cloud_export_path_required");
                    receipt=await new HistoricalCloudExporter(_factory).ExportAsync(archivePath,verifiedBackupPath,guard,ct).ConfigureAwait(false);
                    await saved.SaveAsync(writerEpoch,receipt,guard,ct).ConfigureAwait(false);
                }
            }
            finally{if(gate)_gate.Release();Interlocked.Exchange(ref _historicalMaintenance,0);}
            // The server checks every outstanding admitted operation again.
            // No network request holds backup maintenance or the cash-register gate.
            await saved.DemandHistoricalPublicationsDrainedAsync(guard,ct).ConfigureAwait(false);
            await saved.DemandNativeSalesDrainedAsync(guard,ct).ConfigureAwait(false);
            var state=await HistoricalLedger.ReadDatasetAsync(ct).ConfigureAwait(false);
            if(state.DatasetId!=guard.DatasetId||state.DataRevision!=guard.DataRevision)throw new InvalidOperationException("history_cloud_export_snapshot_stale");
            var response=await RequestHistoricalCloudAsync(actor,"freeze_ack",new HistoricalCloudPayload{WriterEpoch=writerEpoch.ToString(CultureInfo.InvariantCulture),LocalDatasetId=guard.DatasetId,DataRevision=guard.DataRevision.ToString(CultureInfo.InvariantCulture),RestoreEpoch=guard.RestoreEpoch.ToString(CultureInfo.InvariantCulture),ArchiveSha256=receipt.ArchiveSha256,ManifestSha256=receipt.ManifestSha256,BackupSha256=receipt.Manifest.BackupSha256,LocalWritesDrained=true,NativeOutboxDrained=true,ProtocolVersion=1,ClientBuildSha=build},ct).ConfigureAwait(false);
            if(!response.Acknowledged||response.WriterEpoch!=authority.WriterEpoch)throw new InvalidOperationException("history_cloud_freeze_ack_invalid");
            guard.DemandStillValid();return receipt;
        }
        /// <summary>Explicit operator export; no upload, cutover, sale or history mutation.</summary>
        public async Task<HistoricalCloudExportReceipt> ExportHistoricalCloudAsync(OperatorSession session,
            string archivePath, string verifiedBackupPath, CancellationToken cancellationToken)
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            admission.CancelAfter(TimeSpan.FromSeconds(60));
            var context = await GetHistoricalImportContextAsync(session, admission.Token).ConfigureAwait(false);
            var authority = _factory.AuthorityStamp;
            var guard = session.CaptureHistoricalCloudExportGuard(context.ShopId, context.DatasetId, context.DataRevision, context.RestoreEpoch, () =>
            {
                var current = _factory.AuthorityStamp;
                if (current.RestoreEpoch != authority.RestoreEpoch || current.AuthorityInstanceEpoch != authority.AuthorityInstanceEpoch)
                    throw new InvalidOperationException("history_cloud_export_database_changed");
            });
            if (Interlocked.CompareExchange(ref _historicalMaintenance, 1, 0) != 0) throw new InvalidOperationException("historical_maintenance_active");
            var gate = false;
            try
            {
                using var backup = await _backupAutomation.EnterMaintenanceAsync(admission.Token).ConfigureAwait(false);
                await _gate.WaitAsync(admission.Token).ConfigureAwait(false); gate = true;
                guard.DemandStillValid();
                return await new HistoricalCloudExporter(_factory).ExportAsync(archivePath, verifiedBackupPath, guard, admission.Token).ConfigureAwait(false);
            }
            finally { if (gate) _gate.Release(); Interlocked.Exchange(ref _historicalMaintenance, 0); }
        }
    }
}
