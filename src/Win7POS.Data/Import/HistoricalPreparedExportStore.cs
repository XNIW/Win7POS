using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
namespace Win7POS.Data.Import
{
    /// <summary>Local recovery of the exact product-produced frozen export, not an upload authority.</summary>
    public sealed class HistoricalPreparedExportStore
    {
        private readonly SqliteConnectionFactory _factory;
        public HistoricalPreparedExportStore(SqliteConnectionFactory factory){_factory=factory??throw new ArgumentNullException(nameof(factory));}
        public async Task DemandNativeSalesDrainedAsync(HistoricalCloudExportGuard guard,CancellationToken ct)
        {
            guard.DemandStillValid();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);
            if(await c.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM sales s LEFT JOIN sales_sync_outbox o ON o.sale_id=s.id WHERE o.status IS NULL OR o.status<>'acked'",cancellationToken:ct)).ConfigureAwait(false)!=0)
                throw new InvalidOperationException("history_cloud_native_sync_required");guard.DemandStillValid();
        }
        public async Task SaveAsync(long writerEpoch,HistoricalCloudExportReceipt receipt,HistoricalCloudExportGuard guard,CancellationToken ct)
        {
            if(writerEpoch<1||receipt==null)throw new InvalidOperationException("history_cloud_export_invalid");
            DemandScope(receipt.Manifest,guard);using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);using var t=c.BeginTransaction();
            await c.ExecuteAsync(new CommandDefinition(@"INSERT INTO historical_cloud_prepared_export(shop_id,writer_epoch,local_dataset_id,data_revision,restore_epoch,archive_path,backup_path,archive_sha256,manifest_sha256,backup_sha256,manifest_json)
VALUES(@shop,@epoch,@dataset,@revision,@restore,@archive,@backup,@archiveHash,@manifestHash,@backupHash,@manifest)",new{shop=guard.ShopId,epoch=writerEpoch,dataset=guard.DatasetId,revision=guard.DataRevision,restore=guard.RestoreEpoch,archive=receipt.ArchivePath,backup=receipt.BackupPath,archiveHash=receipt.ArchiveSha256,manifestHash=receipt.ManifestSha256,backupHash=receipt.Manifest.BackupSha256,manifest=Serialize(receipt.Manifest)},t,cancellationToken:ct)).ConfigureAwait(false);
            guard.DemandStillValid();t.Commit();
        }
        public async Task<HistoricalCloudExportReceipt> ReadVerifiedAsync(long epoch,HistoricalCloudExportGuard guard,CancellationToken ct)
        {
            guard.DemandStillValid();using var c=await _factory.OpenAsync(ct).ConfigureAwait(false);
            var row=await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("SELECT local_dataset_id Dataset,data_revision Revision,restore_epoch Restore,archive_path Archive,backup_path Backup,archive_sha256 ArchiveHash,manifest_sha256 ManifestHash,backup_sha256 BackupHash,manifest_json Manifest FROM historical_cloud_prepared_export WHERE shop_id=@shop AND writer_epoch=@epoch",new{shop=guard.ShopId,epoch},cancellationToken:ct)).ConfigureAwait(false);
            if(row==null)return null;
            if(row.Dataset!=guard.DatasetId||row.Revision!=guard.DataRevision||row.Restore!=guard.RestoreEpoch)throw new InvalidOperationException("history_cloud_export_snapshot_stale");
            DemandPlainFile(row.Archive);DemandPlainFile(row.Backup);using var archive=new FileStream(row.Archive,FileMode.Open,FileAccess.Read,FileShare.Read);using var backup=new FileStream(row.Backup,FileMode.Open,FileAccess.Read,FileShare.Read);
            ct.ThrowIfCancellationRequested();if(archive.Length>HistoricalCloudExporter.MaximumArchiveBytes||Hash(archive)!=row.ArchiveHash||Hash(backup)!=row.BackupHash)throw new InvalidOperationException("history_cloud_export_changed");
            archive.Position=0;using var zip=new ZipArchive(archive,ZipArchiveMode.Read,true);var entry=zip.GetEntry("manifest.json");if(entry==null||entry.Length>65536)throw new InvalidOperationException("history_cloud_export_changed");
            using var input=entry.Open();using var bytes=new MemoryStream();input.CopyTo(bytes);bytes.Position=0;if(Hash(bytes)!=row.ManifestHash)throw new InvalidOperationException("history_cloud_export_changed");bytes.Position=0;
            var manifest=(HistoricalCloudExportManifest)new DataContractJsonSerializer(typeof(HistoricalCloudExportManifest)).ReadObject(bytes);DemandScope(manifest,guard);
            if(manifest.BackupSha256!=row.BackupHash||manifest.BackupByteLength!=backup.Length.ToString(CultureInfo.InvariantCulture)||Serialize(manifest)!=row.Manifest)throw new InvalidOperationException("history_cloud_export_changed");
            guard.DemandStillValid();ct.ThrowIfCancellationRequested();return new HistoricalCloudExportReceipt{ArchivePath=row.Archive,BackupPath=row.Backup,ArchiveSha256=row.ArchiveHash,ManifestSha256=row.ManifestHash,Manifest=manifest};
        }
        private static void DemandScope(HistoricalCloudExportManifest m,HistoricalCloudExportGuard g)
        {g.DemandStillValid();if(m==null||m.ShopId!=g.ShopId||m.DeviceId!=g.DeviceId||m.LocalDatasetId!=g.DatasetId||m.DataRevision!=g.DataRevision.ToString(CultureInfo.InvariantCulture)||m.RestoreEpoch!=g.RestoreEpoch.ToString(CultureInfo.InvariantCulture))throw new InvalidOperationException("history_cloud_export_scope_changed");}
        private static void DemandPlainFile(string path)
        {
            if(!Path.IsPathRooted(path)||Path.GetFullPath(path)!=path||path.StartsWith(@"\\",StringComparison.Ordinal))throw new InvalidOperationException("history_cloud_export_path_invalid");
            for(var at=path;!string.IsNullOrEmpty(at);at=Path.GetDirectoryName(at))if((File.GetAttributes(at)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("history_cloud_export_path_invalid");
        }
        private static string Serialize(HistoricalCloudExportManifest manifest){using var s=new MemoryStream();new DataContractJsonSerializer(typeof(HistoricalCloudExportManifest)).WriteObject(s,manifest);return Encoding.UTF8.GetString(s.ToArray());}
        private static string Hash(Stream stream){stream.Position=0;using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        private sealed class Row{public string Dataset{get;set;}public long Revision{get;set;}public long Restore{get;set;}public string Archive{get;set;}public string Backup{get;set;}public string ArchiveHash{get;set;}public string ManifestHash{get;set;}public string BackupHash{get;set;}public string Manifest{get;set;}}
    }
}
