using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Data.Backup;

namespace Win7POS.Data.Import
{
    [DataContract]
    public sealed class HistoricalCloudExportEntry
    {
        [DataMember(Name = "name")] public string Name { get; internal set; }
        [DataMember(Name = "rows")] public string Rows { get; internal set; }
        [DataMember(Name = "byteLength")] public string ByteLength { get; internal set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; internal set; }
    }
    [DataContract]
    public sealed class HistoricalCloudExportManifest
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; internal set; } = "history-local-export-v1";
        [DataMember(Name = "shopId")] public string ShopId { get; internal set; }
        [DataMember(Name = "deviceId")] public string DeviceId { get; internal set; }
        [DataMember(Name = "localDatasetId")] public string LocalDatasetId { get; internal set; }
        [DataMember(Name = "dataRevision")] public string DataRevision { get; internal set; }
        [DataMember(Name = "restoreEpoch")] public string RestoreEpoch { get; internal set; }
        [DataMember(Name = "restoreLineageId", EmitDefaultValue = false)] public string RestoreLineageId { get; internal set; }
        [DataMember(Name = "createdAtUtc")] public string CreatedAtUtc { get; internal set; }
        [DataMember(Name = "backupSha256")] public string BackupSha256 { get; internal set; }
        [DataMember(Name = "backupByteLength")] public string BackupByteLength { get; internal set; }
        [DataMember(Name = "evidenceLevel")] public string EvidenceLevel { get; internal set; } = "legacy_attested";
        [DataMember(Name = "entries")] public HistoricalCloudExportEntry[] Entries { get; internal set; }
    }
    public sealed class HistoricalCloudExportReceipt
    {
        public string ArchivePath { get; internal set; }
        public string BackupPath { get; internal set; }
        public string ArchiveSha256 { get; internal set; }
        public string ManifestSha256 { get; internal set; }
        public HistoricalCloudExportManifest Manifest { get; internal set; }
    }
    /// <summary>Streams only historical rows from a verified read-only product backup.</summary>
    public sealed class HistoricalCloudExporter
    {
        public const long MaximumExpandedBytes = 256L * 1024 * 1024;
        public const long MaximumArchiveBytes = 20L * 1024 * 1024;
        private readonly SqliteConnectionFactory _live;
        private static readonly (string Name, string Sql)[] Tables = {
            ("scopes.ndjson", "SELECT id,shop_id,provider,evidence_reference,created_at FROM historical_merchant_scope WHERE shop_id=@shop ORDER BY id"),
            ("bindings.ndjson", "SELECT id,scope_id,shop_id,provider,alias_version,alias_key,evidence_reference,used_at,is_active FROM historical_merchant_binding WHERE shop_id=@shop ORDER BY id"),
            ("operations.ndjson", "SELECT operation_id,operation_kind,shop_id,dataset_id,request_fingerprint,batch_id,operator_id,created_at,receipt_json,inserted_count,duplicate_count,signed_amount_clp FROM historical_import_operation WHERE shop_id=@shop ORDER BY operation_id"),
            ("batches.ndjson", "SELECT id,shop_id,dataset_id,apply_operation_id,state,revision,created_at,undone_at,undo_operation_id FROM historical_import_batch WHERE shop_id=@shop ORDER BY id"),
            ("files.ndjson", "SELECT f.id,f.batch_id,f.source_sha256,f.profile,f.parser_version,f.temporal_policy,f.evidence_reference,f.source_rows FROM historical_import_file f JOIN historical_import_batch b ON b.id=f.batch_id WHERE b.shop_id=@shop ORDER BY f.id"),
            ("movements.ndjson", "SELECT id,batch_id,file_id,shop_id,provider,merchant_scope_id,binding_id,external_namespace,external_id,business_ticks,timestamp_raw,offset_minutes,occurred_at_utc_ms,movement_kind,state,settlement_state,signed_amount_clp,signed_fee_clp,signed_fee_vat_clp,signed_net_clp,original_cashier_id,card_subtype,source_sheet,source_row,is_active,supersedes_id FROM historical_transactions WHERE shop_id=@shop ORDER BY id"),
            ("observations.ndjson", "SELECT o.id,o.batch_id,o.file_id,o.source_row,o.observation_kind,o.code,o.existing_transaction_id FROM historical_import_observation o JOIN historical_import_batch b ON b.id=o.batch_id WHERE b.shop_id=@shop ORDER BY o.id")
        };
        public HistoricalCloudExporter(SqliteConnectionFactory live) { _live = live ?? throw new ArgumentNullException(nameof(live)); }
        public Task<HistoricalCloudExportReceipt> ExportAsync(string archivePath, string backupPath, HistoricalCloudExportGuard guard, CancellationToken ct)
            => ExportCoreAsync(archivePath, backupPath, guard, null, ct);

        // The local v2 baseline attests the existing live lineage. Never derive
        // it by initializing/migrating a copy, which would create a new lineage.
        public Task<HistoricalCloudExportReceipt> ExportBaselineAsync(string archivePath, string backupPath, HistoricalCloudExportGuard guard, string restoreLineageId, CancellationToken ct)
            => ExportCoreAsync(archivePath, backupPath, guard, HistoricalPublicationDraft.NormalizeLineage(restoreLineageId), ct);

        private async Task<HistoricalCloudExportReceipt> ExportCoreAsync(string archivePath, string backupPath, HistoricalCloudExportGuard guard, string lineage, CancellationToken ct)
        {
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            guard.DemandStillValid(); ct.ThrowIfCancellationRequested();
            archivePath = ValidateOutput(archivePath); backupPath = ValidateOutput(backupPath);
            if (!archivePath.EndsWith(".w7history.zip", StringComparison.OrdinalIgnoreCase) || string.Equals(archivePath, backupPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("history_cloud_export_path_invalid");
            await DemandCurrentAsync(guard, lineage, ct).ConfigureAwait(false);
            // Uses the same backup protocol as Apply, including integrity/FK validation.
            var validation = await new SqliteOnlineBackup(_live).CreateVerifiedAsync(backupPath, ct).ConfigureAwait(false);
            if (!validation.IsValid) throw new InvalidOperationException("history_cloud_export_backup_invalid");
            guard.DemandStillValid();
            return await Task.Run(() => ExportSnapshot(archivePath, backupPath, guard, lineage, ct), ct).ConfigureAwait(false);
        }
        private string ValidateOutput(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("history_cloud_export_path_invalid");
            var full = Path.GetFullPath(path); var profile = Path.GetDirectoryName(Path.GetFullPath(_live.DbPath)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(profile, StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\", StringComparison.Ordinal) || File.Exists(full))
                throw new InvalidOperationException("history_cloud_export_path_invalid");
            PosDatabasePathIdentity.RejectReparseComponents(full);
            return full;
        }
        private async Task DemandCurrentAsync(HistoricalCloudExportGuard guard, string lineage, CancellationToken ct)
        {
            guard.DemandStillValid();
            using var c = await _live.OpenExistingForRecoveryAsync(ct).ConfigureAwait(false);
            var state = await c.QuerySingleAsync<HistoricalSalesDatasetState>(new CommandDefinition("SELECT dataset_id DatasetId,data_revision DataRevision,(SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id') RestoreLineageId FROM historical_import_dataset WHERE singleton_id=1", cancellationToken: ct)).ConfigureAwait(false);
            if (state.DatasetId != guard.DatasetId || state.DataRevision != guard.DataRevision || _live.AuthorityStamp.RestoreEpoch != guard.RestoreEpoch)
                throw new InvalidOperationException("history_cloud_export_snapshot_stale");
            DemandLineage(state, lineage);
            guard.DemandStillValid();
        }
        private static void DemandLineage(HistoricalSalesDatasetState state, string expected)
        {
            if (expected != null && HistoricalPublicationDraft.NormalizeLineage(state.RestoreLineageId) != expected)
                throw new InvalidOperationException("history_cloud_export_lineage_stale");
        }
        private HistoricalCloudExportReceipt ExportSnapshot(string destination, string backupPath, HistoricalCloudExportGuard guard, string lineage, CancellationToken ct)
        {
            var partial = destination + ".partial." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using var heldBackup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var backupHash = Hash(heldBackup); heldBackup.Position = 0;
            using var context = PosDatabaseAccessContext.SelectedSource(backupPath);
            var snapshot = new SqliteConnectionFactory(new PosDbOptions(backupPath, accessContext: context));
            using var db = snapshot.Open(); using var tx = db.BeginTransaction(deferred: true);
            var state = db.QuerySingle<HistoricalSalesDatasetState>("SELECT dataset_id DatasetId,data_revision DataRevision,(SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id') RestoreLineageId FROM historical_import_dataset WHERE singleton_id=1", transaction: tx);
            if (state.DatasetId != guard.DatasetId || state.DataRevision != guard.DataRevision) throw new InvalidOperationException("history_cloud_export_snapshot_stale");
            DemandLineage(state, lineage);
            if (db.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE shop_id=@shop", new { shop = guard.ShopId }, tx) > 100000)
                throw new InvalidOperationException("history_cloud_export_row_limit");
            var entries = new List<HistoricalCloudExportEntry>(); long expanded = 0;
            var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var table in Tables)
                    {
                        guard.DemandStillValid(); ct.ThrowIfCancellationRequested();
                        using var output = zip.CreateEntry(table.Name, CompressionLevel.Optimal).Open();
                        using var hashed = new HashingExportStream(output, count => { expanded = checked(expanded + count); if (expanded > MaximumExpandedBytes) throw new InvalidOperationException("history_cloud_export_size_limit"); });
                        using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = table.Sql; command.Parameters.AddWithValue("@shop", guard.ShopId);
                        using var reader = command.ExecuteReader(); long count = 0;
                        while (reader.Read())
                        {
                            if (++count > (table.Name == "movements.ndjson" ? 100000 : table.Name == "observations.ndjson" ? 200000 : 10000)) throw new InvalidOperationException("history_cloud_export_row_limit");
                            guard.DemandStillValid(); ct.ThrowIfCancellationRequested();
                            var row = new Dictionary<string, string>(StringComparer.Ordinal);
                            for (var column = 0; column < reader.FieldCount; column++)
                            {
                                var value = reader.GetValue(column);
                                if (!(value is DBNull) && !(value is long) && !(value is string)) throw new InvalidDataException("history_cloud_export_value_invalid");
                                row.Add(reader.GetName(column), value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
                            }
                            serializer.WriteObject(hashed, row); hashed.WriteByte(10);
                        }
                        entries.Add(new HistoricalCloudExportEntry { Name = table.Name, Rows = count.ToString(CultureInfo.InvariantCulture), ByteLength = hashed.Bytes.ToString(CultureInfo.InvariantCulture), Sha256 = hashed.FinishHash() });
                    }
                    var manifest = new HistoricalCloudExportManifest { SchemaVersion = lineage == null ? "history-local-export-v1" : "history-local-export-v2",
                        RestoreLineageId = lineage, ShopId = guard.ShopId, DeviceId = guard.DeviceId, LocalDatasetId = guard.DatasetId,
                        DataRevision = guard.DataRevision.ToString(CultureInfo.InvariantCulture), RestoreEpoch = guard.RestoreEpoch.ToString(CultureInfo.InvariantCulture),
                        CreatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), BackupSha256 = backupHash,
                        BackupByteLength = heldBackup.Length.ToString(CultureInfo.InvariantCulture), Entries = entries.ToArray() };
                    using var manifestStream = zip.CreateEntry("manifest.json").Open(); new DataContractJsonSerializer(typeof(HistoricalCloudExportManifest)).WriteObject(manifestStream, manifest);
                }
                target.Flush(true);
            }
            tx.Commit(); heldBackup.Position = 0;
            if (Hash(heldBackup) != backupHash) throw new InvalidOperationException("history_cloud_export_backup_changed");
            DemandCurrentAsync(guard, lineage, ct).GetAwaiter().GetResult(); guard.DemandStillValid(); ct.ThrowIfCancellationRequested();
            if (new FileInfo(partial).Length > MaximumArchiveBytes) throw new InvalidOperationException("history_cloud_export_size_limit");
            ValidateOutput(destination); File.Move(partial, destination);
            using var archive = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            archive.Position = 0; using var readZip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            using var readManifest = readZip.GetEntry("manifest.json").Open(); using var manifestBytes = new MemoryStream();
            readManifest.CopyTo(manifestBytes); manifestBytes.Position = 0;
            var finalManifest = (HistoricalCloudExportManifest)new DataContractJsonSerializer(typeof(HistoricalCloudExportManifest)).ReadObject(manifestBytes);
            manifestBytes.Position = 0; var manifestSha = Hash(manifestBytes);
            readManifest.Dispose(); readZip.Dispose(); archive.Position = 0;
            return new HistoricalCloudExportReceipt { ArchivePath = destination, BackupPath = backupPath, ArchiveSha256 = Hash(archive), ManifestSha256 = manifestSha, Manifest = finalManifest };
        }
        private static string Hash(Stream stream) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private sealed class HashingExportStream : Stream
        {
            private readonly Stream _inner; private readonly Action<int> _countBytes; private readonly SHA256 _hash = SHA256.Create(); private bool _finished;
            internal HashingExportStream(Stream inner, Action<int> countBytes) { _inner = inner; _countBytes = countBytes; }
            internal long Bytes { get; private set; }
            public override void Write(byte[] buffer, int offset, int count) { _countBytes(count); _hash.TransformBlock(buffer, offset, count, null, 0); _inner.Write(buffer, offset, count); Bytes += count; }
            internal string FinishHash() { if (_finished) throw new InvalidOperationException(); _finished = true; _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return BitConverter.ToString(_hash.Hash).Replace("-", "").ToLowerInvariant(); }
            protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
            public override void Flush() => _inner.Flush(); public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
