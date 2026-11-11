using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Tests.Data;
using Win7POS.Core.Tests.Import;
using Win7POS.Data;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;

[TestClass]
public sealed class HistoricalPublicationCorpusTests
{
    const string Shop="10000000-0000-4000-8000-000000403019";
    [TestMethod]
    public async Task ClrPublicationCorpus_PreservesDuplicateUndoCorrectionAndSecondDeviceProvenance()
    {
        var root=Path.Combine(Path.GetTempPath(),"W7PC."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var original=GetnetTestWorkbook.Legacy(new[]{GetnetTestWorkbook.LegacyRow("SYN-PUB-001")});
            using var another=GetnetTestWorkbook.Legacy(new[]{GetnetTestWorkbook.LegacyRow("SYN-PUB-NEW","2200","20","2180")});
            using var corrected=GetnetTestWorkbook.Legacy(new[]{GetnetTestWorkbook.LegacyRow("SYN-PUB-001","1500","20","1480")});
            using var a=new Actor(root,"a","20000000-0000-4000-8000-000000403019");
            using var b=new Actor(root,"b","20000000-0000-4000-8000-000000403020");
            var cases=new List<object>();
            var seed=await a.Plan(original.Path);var first=await a.Service.ApplyAsync(seed,a.Guard(seed),a.Backup(),default);
            var baselineA=await a.Export();var baselineB=await b.Export();
            async Task<HistoricalSalesOperationReceipt> Apply(Actor actor,string name,string path,int inserted,int duplicates,int corrections,long gross)
            {
                var plan=await actor.Plan(path);var before=await actor.Service.ReadDatasetAsync(default);
                var preview=await actor.Service.PreviewAsync(plan,default);
                if(corrections>0)
                {
                    Assert.AreEqual(corrections,preview.CorrectionCount);
                    plan=new HistoricalSalesImportPlan(plan.OperationId,Shop,plan.DatasetId,plan.DataRevision,0,plan.Analysis,plan.Assignments,
                        plan.NativeStartBusinessDateTime,plan.OverlapEvidenceReference,preview.Corrections.Select(c=>c.Approve("synthetic-owned-correction")));
                }
                using var draft=await actor.Service.PreparePublicationDraftAsync(plan,actor.Device,default);
                var result=await actor.Service.ApplyAsync(plan,actor.Guard(plan),actor.Backup(),default,publicationDraft:draft);
                Assert.AreEqual(inserted,result.InsertedCount,name);Assert.AreEqual(duplicates,result.DuplicateCount,name);
                Assert.AreEqual(corrections,result.CorrectionCount,name);Assert.AreEqual(gross,result.SignedAmountClp,name);
                Assert.AreEqual(before.DataRevision+inserted,(await actor.Service.ReadDatasetAsync(default)).DataRevision,name);
                cases.Add(actor.Case(name,draft,result));return result;
            }
            async Task Undo(Actor actor,string name,HistoricalSalesOperationReceipt source,int deactivated,long gross)
            {
                var state=await actor.Service.ReadDatasetAsync(default);var op=Guid.NewGuid().ToString("N");
                var fp=HistoricalSalesImportPlan.UndoFingerprint(op,Shop,state.DatasetId,source.BatchId,1,0);
                using var draft=await actor.Service.PrepareUndoPublicationDraftAsync(op,Shop,actor.Device,state.DatasetId,0,source.BatchId,1,default);
                var receipt=await actor.Service.UndoAsync(source.BatchId,1,actor.Guard(HistoricalImportOperationKind.Undo,op,fp,state.DatasetId),actor.Backup(),default,publicationDraft:draft);
                Assert.AreEqual(deactivated,receipt.InsertedCount,name);Assert.AreEqual(gross,receipt.SignedAmountClp,name);
                Assert.AreEqual(state.DataRevision+deactivated,(await actor.Service.ReadDatasetAsync(default)).DataRevision,name);
                cases.Add(actor.Case(name,draft,receipt));
            }
            await Apply(a,"a-duplicate",original.Path,0,1,0,0);
            await Apply(a,"a-new",another.Path,1,0,0,2200);
            await Undo(a,"a-undo-baseline",first,1,1200);
            var reimport=await Apply(a,"a-identical-reimport",original.Path,1,0,0,1200);
            await Undo(a,"a-undo-incremental",reimport,1,1200);
            await Apply(a,"a-corrected-reimport",corrected.Path,1,0,1,1500);
            await Apply(a,"a-corrected-duplicate",corrected.Path,0,1,0,0);
            var observed=await Apply(b,"b-locally-new-canonical-existing",corrected.Path,1,0,0,1500);
            await Undo(b,"b-undo-local-canonical-duplicate",observed,1,1500);
            using(var c=a.Db.Factory.Open())
            {
                Assert.AreEqual(3700L,c.ExecuteScalar<long>("SELECT SUM(signed_amount_clp) FROM historical_transactions WHERE is_active=1"));
                Assert.AreEqual(3660L,c.ExecuteScalar<long>("SELECT SUM(signed_net_clp) FROM historical_transactions WHERE is_active=1"));
            }
            foreach(var actor in new[]{a,b})using(var c=actor.Db.Factory.Open())
                foreach(var table in new[]{"sales","sales_sync_outbox","local_stock_movements"})Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM "+table),table);
            var corpus=Environment.GetEnvironmentVariable("WIN7POS_QA_PUBLICATION_CORPUS");
            if(!string.IsNullOrEmpty(corpus))
            {
                if(Directory.Exists(corpus)&&Directory.EnumerateFileSystemEntries(corpus).Any())throw new InvalidOperationException("synthetic_corpus_target_not_empty");
                Directory.CreateDirectory(corpus);var baselines=new[]{baselineA,baselineB}.Select((receipt,i)=>
                {
                    var file=i==0?"a-baseline.w7history.zip":"b-baseline.w7history.zip";File.Copy(receipt.ArchivePath,Path.Combine(corpus,file),false);
                    return new{file,archive_sha256=receipt.ArchiveSha256,manifest_sha256=receipt.ManifestSha256,receipt.Manifest};
                }).ToArray();
                File.WriteAllText(Path.Combine(corpus,"publication-corpus.json"),JsonSerializer.Serialize(new{source="CLR analyzer/exporter/draft/ledger",syntheticOnly=true,
                    baselineSource="verified selective archive only",baselines,cases,nativeEffects=0},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
            }
        }
        finally
        {
            var full=Path.GetFullPath(root);var allowed=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("W7PC.",StringComparison.Ordinal))throw new InvalidOperationException("synthetic_cleanup_target_invalid");
            Directory.Delete(full,true);
        }
    }
    sealed class Actor:IDisposable
    {
        internal readonly OwnedSqliteTestFile Db;internal readonly HistoricalSalesImportService Service;internal readonly string Device;
        readonly string _root,_scope;
        internal Actor(string root,string name,string device)
        {
            _root=Path.Combine(root,name);_scope="corpus-scope-"+name;Directory.CreateDirectory(Path.Combine(_root,"profile"));Device=device;
            Db=OwnedSqliteTestFile.Create(Path.Combine(_root,"profile","fixture.db"));DbInitializer.EnsureCreated(Db.Options);
            Service=new HistoricalSalesImportService(Db.Factory,()=>Db.Factory.AuthorityStamp);
        }
        internal string Backup()=>Path.Combine(_root,Guid.NewGuid().ToString("N")+".db");
        internal async Task<HistoricalSalesImportPlan> Plan(string path)
        {
            var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{path},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
            var state=await Service.ReadDatasetAsync(default);
            return new(Guid.NewGuid().ToString("N"),Shop,state.DatasetId,state.DataRevision,0,analysis,
                analysis.AcceptedTransactions.Select(r=>r.Merchant).GroupBy(HistoricalMerchantAssignment.MerchantKey).Select(g=>new HistoricalMerchantAssignment(g.First(),_scope,"corpus-binding-"+_scope,"synthetic mapping")),
                new DateTime(2025,1,1),"synthetic never used before");
        }
        internal HistoricalImportAuthorizationCommitGuard Guard(HistoricalSalesImportPlan plan)=>Guard(HistoricalImportOperationKind.Apply,plan.OperationId,plan.RequestFingerprint,plan.DatasetId);
        internal HistoricalImportAuthorizationCommitGuard Guard(HistoricalImportOperationKind kind,string op,string fp,string dataset)
            =>new(kind,op,fp,Shop,"SYNTHETIC",Device,1,dataset,0,1,"synthetic-generation","synthetic-staff",1,()=>{},(_,commit)=>commit());
        internal async Task<HistoricalCloudExportReceipt> Export()
        {
            var state=await Service.ReadDatasetAsync(default);return await new HistoricalCloudExporter(Db.Factory).ExportBaselineAsync(Path.Combine(_root,"baseline.w7history.zip"),Backup(),
                new HistoricalCloudExportGuard(Shop,Device,state.DatasetId,state.DataRevision,0,()=>{}),Guid.Parse(state.RestoreLineageId).ToString("D"),default);
        }
        internal object Case(string name,HistoricalPublicationDraft draft,HistoricalSalesOperationReceipt receipt)
        {
            using var c=Db.Factory.Open();var raw=c.ExecuteScalar<string>("SELECT receipt_json FROM historical_publication_outbox WHERE operation_id=@id",new{id=receipt.OperationId})!;
            return new{name,shop_id=Shop,device_id=Device,operation_id=Guid.Parse(draft.OperationId).ToString("D"),kind=draft.Kind,local_dataset_id=draft.DatasetId,
                restore_lineage_id=draft.RestoreLineageId,restore_epoch="0",base_revision=draft.BaseRevision.ToString(CultureInfo.InvariantCulture),request_fingerprint=draft.RequestFingerprint,
                content_digest=draft.ContentDigest,page_count=draft.PageCount,pages=draft.Pages(default).Select(p=>new{ordinal=p.Ordinal,page_json=p.Json,page_sha256=p.Digest}).ToArray(),
                local_receipt_json=raw,local_receipt_sha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
                local_decision=new{inserted=draft.Kind=="Apply"?receipt.InsertedCount:0,duplicates=receipt.DuplicateCount,rejected=receipt.RejectedCount,corrections=receipt.CorrectionCount,
                    deactivated=draft.Kind=="Undo"?receipt.InsertedCount:0,signed_gross_clp=receipt.SignedAmountClp.ToString(CultureInfo.InvariantCulture),revision_delta=receipt.InsertedCount.ToString(CultureInfo.InvariantCulture)}};
        }
        public void Dispose()=>Db.Dispose();
    }
}
