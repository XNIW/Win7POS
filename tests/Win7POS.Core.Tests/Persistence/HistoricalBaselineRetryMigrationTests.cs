using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data.Migrations;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;
public sealed partial class HistoricalBaselineStoreTests
{
    static void RestorePublished0021(HistoricalCloudExportTests.Fixture f)
    {
        using var c=f.Db.Factory.Open();using var t=c.BeginTransaction();
        c.Execute("DROP TABLE historical_baseline_retry;DROP TRIGGER historical_baseline_flow_update;DELETE FROM schema_migrations WHERE migration_id='0022-historical-baseline-retry'",transaction:t);
        var sql=HistoricalBaselineSchema.Sql;var start=sql.IndexOf("CREATE TRIGGER historical_baseline_flow_update",StringComparison.Ordinal);var end=sql.IndexOf("CREATE TABLE historical_baseline_read_mode",start,StringComparison.Ordinal);
        c.Execute(sql.Substring(start,end-start),transaction:t);t.Commit();
    }
    static string[] MigrationEvidence(HistoricalCloudExportTests.Fixture f)
    {
        using var c=f.Db.Factory.Open();return c.Query<string>("SELECT migration_id||'|'||checksum||'|'||applied_at FROM schema_migrations ORDER BY migration_id").ToArray();
    }
    static string[] BaselineSchemaEvidence(HistoricalCloudExportTests.Fixture f)
    {
        using var c=f.Db.Factory.Open();return c.Query<string>("SELECT type||'|'||name||'|'||sql FROM sqlite_master WHERE name LIKE '%historical_baseline%' ORDER BY type,name").ToArray();
    }
    [TestMethod]
    public async Task BaselineRetryMigration_Populated0021AddsOnly0022AfterVerifiedBackup()
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await FailedPreparation(f,g);RestorePublished0021(f);
        var facts=f.ReadFacts();var ledger=MigrationEvidence(f);var flow=(await Store(f,g).ReadAsync(Server,1,default))!;
        var result=new SchemaMigrationRunner(f.Db.Factory,SchemaMigrationRegistry.All).Run();
        Assert.IsFalse(string.IsNullOrEmpty(result.BackupFileName));CollectionAssert.AreEqual(new[]{"0022-historical-baseline-retry"},result.AppliedMigrationIds.ToArray());
        CollectionAssert.AreEqual(ledger,MigrationEvidence(f).Take(21).ToArray());Assert.AreEqual(facts,f.ReadFacts());
        var after=(await Store(f,g).ReadAsync(Server,1,default))!;Assert.AreEqual(flow.FlowId,after.FlowId);Assert.AreEqual(flow.RunId,after.RunId);Assert.AreEqual(flow.ArchiveSha256,after.ArchiveSha256);Assert.AreEqual("needs_attention",after.State);
        Assert.IsTrue((await Store(f,g).ReadRetryStatusAsync(default)).CanRequestRetry);
        Assert.IsTrue(new SchemaMigrationRunner(f.Db.Factory,SchemaMigrationRegistry.All).Run().WasNoOp);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BaselineRetryMigration_DdlOrPostconditionFailureRestoresOldTriggerLedgerAndFacts(bool failPostcondition)
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await FailedPreparation(f,g);RestorePublished0021(f);
        var facts=f.ReadFacts();var ledger=MigrationEvidence(f);var shape=BaselineSchemaEvidence(f);
        var replacement=new SchemaMigration("0022-historical-baseline-retry","Synthetic injected migration fault","synthetic-0022-fault","1.0.0","Synthetic rollback probe",true,
            (c,t)=>{HistoricalBaselineRetrySchema.Apply(c,t);if(!failPostcondition)throw new InvalidOperationException("injected_0022_failure");},_=>false);
        Assert.Throws<Exception>(()=>new SchemaMigrationRunner(f.Db.Factory,SchemaMigrationRegistry.All.Take(21).Append(replacement)).Run());
        CollectionAssert.AreEqual(ledger,MigrationEvidence(f));CollectionAssert.AreEqual(shape,BaselineSchemaEvidence(f));Assert.AreEqual(facts,f.ReadFacts());
        Assert.AreEqual("needs_attention",(await Store(f,g).ReadAsync(Server,1,default))!.State);
    }
    [TestMethod]
    [DataRow("valid")]
    [DataRow("partial")]
    [DataRow("acknowledged")]
    public async Task BaselineRetryMigration_LedgerlessAdoptionRequiresCanonicalShapeAndNoUnfinishedAck(string kind)
    {
        using var f=await HistoricalCloudExportTests.Fixture.CreateAsync();var g=Generation();await FailedPreparation(f,g);
        var intent=await Store(f,g).PrepareFailedRetryAsync(default);var facts=f.ReadFacts();
        using(var c=f.Db.Factory.Open())
        {
            if(kind=="partial")c.Execute("DROP INDEX ux_historical_baseline_retry_pending");
            if(kind=="acknowledged")c.Execute("UPDATE historical_baseline_retry SET state='acknowledged',ack_json='synthetic',ack_sha256=@sha",new{sha=new string('a',64)});
            c.Execute("DROP TABLE schema_migrations");
        }
        if(kind=="valid")
        {
            var result=new SchemaMigrationRunner(f.Db.Factory,SchemaMigrationRegistry.All).Run();Assert.AreEqual(22,result.BootstrappedMigrationIds.Count);Assert.AreEqual(0,result.AppliedMigrationIds.Count);
        }
        else
        {
            Assert.Throws<InvalidDataException>(()=>new SchemaMigrationRunner(f.Db.Factory,SchemaMigrationRegistry.All).Run());
            using var c=f.Db.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name='schema_migrations'"));
        }
        Assert.AreEqual(facts,f.ReadFacts());
    }
}
