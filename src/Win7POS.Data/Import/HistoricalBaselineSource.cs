using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    /// <summary>Product-owned persistent source; the complete verified backup stays local.</summary>
    public sealed class HistoricalBaselineSource
    {
        private readonly SqliteConnectionFactory _factory;private readonly string _root;
        public HistoricalBaselineSource(SqliteConnectionFactory factory):this(factory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Win7POS","HistoryTransfers")){}
        internal HistoricalBaselineSource(SqliteConnectionFactory factory,string root)
        {_factory=factory??throw new ArgumentNullException(nameof(factory));_root=Path.GetFullPath(root);}
        public async Task<HistoricalCloudExportReceipt> PrepareAsync(HistoricalCloudExportGuard guard,string lineage,CancellationToken ct)
        {
            if(guard==null)throw new ArgumentNullException(nameof(guard));guard.DemandStillValid();ct.ThrowIfCancellationRequested();
            PosDatabasePathIdentity.RejectReparseComponents(_root);
            Directory.CreateDirectory(_root);PosDatabasePathIdentity.RejectReparseComponents(_root);
            // Preserve every previous source, while bounding unattended backup
            // creation if a busy/changing database cannot complete preparation.
            if(Directory.EnumerateDirectories(_root).Take(20).Count()>=20)
                throw new HistoricalCloudRequestException("history_baseline_export_backpressure",false,false);
            var saved=new HistoricalPreparedExportStore(_factory);
            await DemandAdmittedOutcomesDrained(guard,ct).ConfigureAwait(false);
            await saved.DemandNativeSalesDrainedAsync(guard,ct).ConfigureAwait(false);
            var output=Path.Combine(_root,Guid.NewGuid().ToString("N"));
            if(Directory.Exists(output)||File.Exists(output))throw new IOException("history_baseline_source_exists");
            Directory.CreateDirectory(output);PosDatabasePathIdentity.RejectReparseComponents(output);
            var result=await new HistoricalCloudExporter(_factory).ExportBaselineAsync(Path.Combine(output,"baseline.w7history.zip"),
                Path.Combine(output,"baseline-verified.db"),guard,lineage,ct).ConfigureAwait(false);
            await DemandAdmittedOutcomesDrained(guard,ct).ConfigureAwait(false);
            await saved.DemandNativeSalesDrainedAsync(guard,ct).ConfigureAwait(false);
            guard.DemandStillValid();ct.ThrowIfCancellationRequested();return result;
        }
        private async Task DemandAdmittedOutcomesDrained(HistoricalCloudExportGuard guard,CancellationToken ct)
        {
            guard.DemandStillValid();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);
            // Pre-v2 immutable ledger operations belong in the baseline. Every
            // admitted/registered outcome still has to be reconciled first.
            var count=await c.ExecuteScalarAsync<long>(new CommandDefinition(@"SELECT
(SELECT COUNT(*) FROM historical_publication_outbox o JOIN historical_import_operation op ON op.operation_id=o.operation_id
 LEFT JOIN historical_publication_delivery d ON d.operation_id=o.operation_id WHERE o.shop_id=@shop AND
 (((o.proof_json IS NOT NULL OR o.grant_id IS NOT NULL OR EXISTS(SELECT 1 FROM historical_preparation_intent i WHERE i.operation_id=o.operation_id))
  AND (d.operation_id IS NULL OR d.status<>'synchronized')) OR o.receipt_json<>op.receipt_json OR o.local_dataset_id<>op.dataset_id OR o.shop_id<>op.shop_id))+
(SELECT COUNT(*) FROM historical_preparation_intent i JOIN historical_preparation_outcome s ON s.operation_id=i.operation_id
 WHERE i.shop_id=@shop AND s.state IN('staging','prepared','authorized'))+
(SELECT COUNT(*) FROM historical_commit_witness w JOIN historical_preparation_intent i ON i.operation_id=w.operation_id
 WHERE i.shop_id=@shop AND w.server_resolution IS NULL)",new{shop=guard.ShopId},cancellationToken:ct)).ConfigureAwait(false);
            if(count!=0)throw new InvalidOperationException("history_cloud_history_sync_required");guard.DemandStillValid();
        }
    }
}
