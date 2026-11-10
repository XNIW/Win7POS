using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Tests.Data;
using Win7POS.Data;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed class HistoricalCloudExportTests
{
    [TestMethod]
    public async Task VerifiedBackupExport_PreservesLiteralEconomicsOriginalReceiptAndPerEntryHashes()
    {
        using var f = await Fixture.CreateAsync();
        var before = f.ReadFacts();
        var receipt = await new HistoricalCloudExporter(f.Db.Factory).ExportAsync(f.Archive, f.Backup, await f.Guard(), CancellationToken.None);
        Assert.AreEqual("history-local-export-v1", receipt.Manifest.SchemaVersion);
        Assert.AreEqual("legacy_attested", receipt.Manifest.EvidenceLevel);
        Assert.AreEqual(before, f.ReadFacts(), "Export must not mutate native or historical data.");
        Assert.AreEqual(Hex(File.ReadAllBytes(f.Backup)), receipt.Manifest.BackupSha256);
        Assert.AreEqual(Hex(File.ReadAllBytes(f.Archive)), receipt.ArchiveSha256);
        using var zip = ZipFile.OpenRead(f.Archive);
        Assert.AreEqual(8, zip.Entries.Count);
        foreach (var entry in receipt.Manifest.Entries)
        {
            var bytes = Read(zip.GetEntry(entry.Name)!);
            Assert.AreEqual(entry.Sha256, Hex(bytes)); Assert.AreEqual(entry.ByteLength, bytes.Length.ToString());
            Assert.AreEqual(entry.Rows, Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ToString());
        }
        var rows = Rows(zip, "movements.ndjson");
        Assert.AreEqual(5, rows.Length);
        Assert.AreEqual(3380L, rows.Sum(r => long.Parse(r["signed_amount_clp"]!)));
        Assert.AreEqual(3320L, rows.Sum(r => long.Parse(r["signed_net_clp"]!)));
        Assert.IsTrue(rows.All(r => r["original_cashier_id"] == null));
        Assert.AreEqual(f.OriginalReceipt, Rows(zip, "operations.ndjson").Single()["receipt_json"]);
        var target = Environment.GetEnvironmentVariable("WIN7POS_QA_CLOUD_EXPORT_CORPUS");
        if (!string.IsNullOrEmpty(target))
        {
            Directory.CreateDirectory(target); File.Copy(f.Archive, Path.Combine(target, "synthetic-local-history.w7history.zip"), overwrite: false);
            File.WriteAllText(Path.Combine(target, "export-provenance.json"), JsonSerializer.Serialize(new { source = "HistoricalCloudExporter", syntheticOnly = true,
                archiveSha256 = receipt.ArchiveSha256, backupSha256 = receipt.Manifest.BackupSha256, manifest = receipt.Manifest }), new UTF8Encoding(false));
        }
    }
    [TestMethod]
    public async Task PreparedFrozenExport_RecoversTheExactArchiveAndRejectsLaterMutation()
    {
        using var f=await Fixture.CreateAsync();var guard=await f.Guard();var exporter=new HistoricalCloudExporter(f.Db.Factory);
        var receipt=await exporter.ExportAsync(f.Archive,f.Backup,guard,CancellationToken.None);var saved=new HistoricalPreparedExportStore(f.Db.Factory);
        await saved.SaveAsync(2,receipt,guard,CancellationToken.None);
        var recovered=await saved.ReadVerifiedAsync(2,await f.Guard(),CancellationToken.None);
        Assert.AreEqual(receipt.ArchiveSha256,recovered.ArchiveSha256);Assert.AreEqual(receipt.ManifestSha256,recovered.ManifestSha256);Assert.AreEqual(receipt.ArchivePath,recovered.ArchivePath);
        Assert.IsNull(await saved.ReadVerifiedAsync(3,await f.Guard(),CancellationToken.None));
        await File.AppendAllTextAsync(f.Archive,"changed");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>saved.ReadVerifiedAsync(2,guard,CancellationToken.None));
    }
    [TestMethod]
    public async Task InactiveExport_PreservesBothOriginalOperationsAndCurrentBatchState()
    {
        using var f = await Fixture.CreateAsync();
        var state = await f.Service.ReadDatasetAsync(CancellationToken.None);
        var batch = (await f.Service.GetBatchesAsync(Fixture.Shop, CancellationToken.None)).Single();
        var op = Guid.NewGuid().ToString("N");
        var fingerprint = HistoricalSalesImportPlan.UndoFingerprint(op, Fixture.Shop, state.DatasetId, batch.Id, batch.Revision, 0);
        await f.Service.UndoAsync(batch.Id, batch.Revision, f.ImportGuard(HistoricalImportOperationKind.Undo, op, fingerprint, state.DatasetId), Path.Combine(f.Output, "undo-backup.db"), CancellationToken.None);
        await new HistoricalCloudExporter(f.Db.Factory).ExportAsync(f.Archive, f.Backup, await f.Guard(), CancellationToken.None);
        using var zip = ZipFile.OpenRead(f.Archive);
        Assert.AreEqual(2, Rows(zip, "operations.ndjson").Length);
        Assert.IsTrue(Rows(zip, "movements.ndjson").All(r => r["is_active"] == "0"));
        Assert.AreEqual("Undone", Rows(zip, "batches.ndjson").Single()["state"]);
        Assert.AreEqual(f.OriginalReceipt, Rows(zip, "operations.ndjson").Single(r => r["operation_kind"] == "Apply")["receipt_json"]);
    }
    [TestMethod]
    public async Task LargeLocalIds_AreDecimalStringsWithoutJavascriptPrecisionLoss()
    {
        using var f = await Fixture.CreateAsync(largeIds: true);
        await new HistoricalCloudExporter(f.Db.Factory).ExportAsync(f.Archive, f.Backup, await f.Guard(), CancellationToken.None);
        using var zip = ZipFile.OpenRead(f.Archive);
        Assert.AreEqual("9007199254740993", Rows(zip, "movements.ndjson").First()["id"]);
    }
    [TestMethod]
    public async Task RevokedOrStaleGuard_DoesNotPublishAnArchive()
    {
        using var f = await Fixture.CreateAsync();
        var guard = await f.Guard(); var stale = new HistoricalCloudExportGuard(Fixture.Shop, Fixture.Device, guard.DatasetId, guard.DataRevision + 1, 0, () => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HistoricalCloudExporter(f.Db.Factory).ExportAsync(f.Archive, f.Backup, stale, CancellationToken.None));
        Assert.IsFalse(File.Exists(f.Archive)); Assert.IsFalse(File.Exists(f.Backup));
        var calls = 0; var revoked = new HistoricalCloudExportGuard(Fixture.Shop, Fixture.Device, guard.DatasetId, guard.DataRevision, 0, () => { if (++calls > 3) throw new InvalidOperationException("synthetic_revoked"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HistoricalCloudExporter(f.Db.Factory).ExportAsync(f.Archive, f.Backup, revoked, CancellationToken.None));
        Assert.IsFalse(File.Exists(f.Archive));
    }
    [TestMethod]
    public async Task ExistingOutputOrProfileDestination_IsNeverOverwritten()
    {
        using var f = await Fixture.CreateAsync(); File.WriteAllText(f.Archive, "synthetic-existing-output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HistoricalCloudExporter(f.Db.Factory).ExportAsync(f.Archive, f.Backup, f.Guard().GetAwaiter().GetResult(), CancellationToken.None));
        Assert.AreEqual("synthetic-existing-output", File.ReadAllText(f.Archive));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HistoricalCloudExporter(f.Db.Factory).ExportAsync(Path.Combine(Path.GetDirectoryName(f.Db.Factory.DbPath)!, "invalid.w7history.zip"), f.Backup, f.Guard().GetAwaiter().GetResult(), CancellationToken.None));
    }
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Read(ZipArchiveEntry entry) { using var s = entry.Open(); using var output = new MemoryStream(); s.CopyTo(output); return output.ToArray(); }
    private static Dictionary<string, string?>[] Rows(ZipArchive zip, string name) => Encoding.UTF8.GetString(Read(zip.GetEntry(name)!)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<Dictionary<string, string?>>(line)!).ToArray();
    internal sealed class Fixture : IDisposable
    {
        internal const string Shop = "10000000-0000-4000-8000-000000003019", Device = "20000000-0000-4000-8000-000000003019";
        internal string Root = Path.Combine(Path.GetTempPath(), "W7HC." + Guid.NewGuid().ToString("N"));
        internal string Output => Path.Combine(Root, "output");
        internal string Archive => Path.Combine(Output, "synthetic.w7history.zip");
        internal string Backup => Path.Combine(Output, "verified.db");
        internal OwnedSqliteTestFile Db = null!; internal HistoricalSalesImportService Service = null!; internal string OriginalReceipt = "";
        internal static async Task<Fixture> CreateAsync(bool largeIds = false)
        {
            var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Root, "profile")); Directory.CreateDirectory(f.Output);
            f.Db = OwnedSqliteTestFile.Create(Path.Combine(f.Root, "profile", "fixture.db")); DbInitializer.EnsureCreated(f.Db.Options);
            f.Service = new HistoricalSalesImportService(f.Db.Factory, () => f.Db.Factory.AuthorityStamp);
            if (largeIds) { using var c = f.Db.Factory.Open(); c.Execute("INSERT INTO sqlite_sequence(name,seq) VALUES('historical_transactions',9007199254740992)"); }
            var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "historical-sales-getnet-v1");
            var analysis = new HistoricalSalesImportAnalyzer().Analyze(new[] { Path.Combine(dir, "synthetic-legacy.xlsx"), Path.Combine(dir, "synthetic-modern.xlsx") }, new HistoricalSalesImportOptions {
                LegacyClpConfirmed = true, ModernTimeInterpretation = HistoricalSalesModernTimeInterpretation.IsoUtc,
                TemporalPolicyVersion = "synthetic-fixed-offset-v1", TemporalEvidenceReference = "synthetic ISO UTC evidence" });
            var state = await f.Service.ReadDatasetAsync(CancellationToken.None);
            var assignments = analysis.AcceptedTransactions.Select(r => r.Merchant).GroupBy(HistoricalMerchantAssignment.MerchantKey).Select((g, i) => new HistoricalMerchantAssignment(g.First(), "local-scope-" + i, "local-binding-" + i, "Synthetic mapping")).ToArray();
            var op = Guid.NewGuid().ToString("N"); var plan = new HistoricalSalesImportPlan(op, Shop, state.DatasetId, state.DataRevision, 0, analysis, assignments, new DateTime(2025, 1, 1), "Synthetic never used before");
            await f.Service.ApplyAsync(plan, f.ImportGuard(HistoricalImportOperationKind.Apply, op, plan.RequestFingerprint, state.DatasetId), Path.Combine(f.Output, "pre-apply.db"), CancellationToken.None);
            using var read = f.Db.Factory.Open(); f.OriginalReceipt = read.ExecuteScalar<string>("SELECT receipt_json FROM historical_import_operation WHERE operation_id=@op", new { op })!;
            return f;
        }
        internal HistoricalImportAuthorizationCommitGuard ImportGuard(HistoricalImportOperationKind kind, string op, string fingerprint, string dataset) => new(kind, op, fingerprint, Shop, "SYNTHETIC", Device, 1, dataset, 0, 1, "synthetic-generation", "synthetic-staff", 1, () => { }, (_, action) => action());
        internal async Task<HistoricalCloudExportGuard> Guard() { var state = await Service.ReadDatasetAsync(CancellationToken.None); return new(Shop, Device, state.DatasetId, state.DataRevision, 0, () => { }); }
        internal string ReadFacts() { using var c = Db.Factory.Open(); return string.Join("|", c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM local_stock_movements"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"), c.ExecuteScalar<long>("SELECT COALESCE(SUM(signed_amount_clp),0) FROM historical_transactions WHERE is_active=1")); }
        public void Dispose()
        {
            Db.Dispose();
            var target = Path.GetFullPath(Root);
            var allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("W7HC.", StringComparison.Ordinal))
                throw new InvalidOperationException("synthetic_cleanup_target_invalid");
            Directory.Delete(target, recursive: true);
        }
    }
}
