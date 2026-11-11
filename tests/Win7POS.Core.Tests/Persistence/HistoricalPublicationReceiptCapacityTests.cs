using System.Text;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    [TestMethod]
    public async Task PublicationReceiptCapacity_RejectsOversizedMultibyteApprovalsBeforeIntentOrGrant()
    {
        using var f=new Fixture();var seed=await f.Plan();var previous=new HistoricalSalesEconomicValue(new DateTime(2020,1,2).Ticks,1000,20,null,980,"Sale","ABONADO");
        var proposed=new HistoricalSalesEconomicValue(new DateTime(2020,1,2).Ticks,1500,20,null,1480,"Sale","ABONADO");
        var approvals=Enumerable.Range(1,HistoricalSalesApplyPreview.MaxDisplayedCorrections).Select(i=>
            new HistoricalSalesCorrectionCandidate(i,previous,proposed,"f1","synthetic",i+1,new string('a',64)).Approve(new string('\u8a3c',2048))).ToArray();
        var plan=new HistoricalSalesImportPlan(seed.OperationId,seed.ShopId,seed.DatasetId,seed.DataRevision,seed.RestoreEpoch,
            seed.Analysis,seed.Assignments,seed.NativeStartBusinessDateTime,seed.OverlapEvidenceReference,approvals);
        var state=await f.Service.ReadDatasetAsync(default);
        var error=Assert.Throws<InvalidOperationException>(()=>HistoricalPublicationDraft.ForApply(plan,PublicationDevice,state.RestoreLineageId));
        Assert.AreEqual("history_publication_receipt_capacity",error.Message);
        using var c=f.Factory.Open();Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_preparation_intent"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(0L,c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_publication_outbox"));
    }

    [TestMethod]
    public async Task PublicationReceiptCapacity_OrdinaryOriginalReceiptRemainsByteExactAndDeliverable()
    {
        using var f=new Fixture();var applied=await CreatePublication(f,PublicationGeneration(),"receipt-byte-bound");
        using var c=f.Factory.Open();var raw=c.ExecuteScalar<string>("SELECT receipt_json FROM historical_publication_outbox")??throw new AssertFailedException("Missing original receipt.");
        var original=c.ExecuteScalar<string>("SELECT receipt_json FROM historical_import_operation");
        Assert.AreEqual(original,raw);Assert.IsTrue(Encoding.UTF8.GetByteCount(raw)<=HistoricalPublicationDraft.MaximumReceiptBytes);
        Assert.AreEqual(applied.Local.OperationId,c.ExecuteScalar<string>("SELECT operation_id FROM historical_publication_outbox"));
    }
}
