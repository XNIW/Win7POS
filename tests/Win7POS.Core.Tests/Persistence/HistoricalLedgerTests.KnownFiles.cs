using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    [TestMethod]
    public async Task KnownFilesRequireExactActiveCoverageAndNeverWrite()
    {
        using var f=new Fixture();var plan=await f.Plan();
        Assert.IsFalse(await f.Service.AreFilesAlreadyActiveAsync(plan.ShopId,plan.Analysis,default));
        var receipt=await f.Service.ApplyAsync(plan,f.Guard(plan),f.Backup(),default);
        var before=await f.Service.ReadDatasetAsync(default);
        Assert.IsTrue(await f.Service.AreFilesAlreadyActiveAsync(plan.ShopId,plan.Analysis,default));
        Assert.IsFalse(await f.Service.AreFilesAlreadyActiveAsync("another-shop",plan.Analysis,default));
        Assert.IsFalse(await f.Service.AreFilesAlreadyActiveAsync(plan.ShopId,(await f.Plan(amount:9999)).Analysis,default));
        Assert.IsFalse(await f.Service.AreFilesAlreadyActiveAsync(plan.ShopId,(await f.Plan(id:"different")).Analysis,default));
        using(var db=f.Factory.Open())Assert.AreEqual(1L,db.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"));
        Assert.AreEqual(before.DataRevision,(await f.Service.ReadDatasetAsync(default)).DataRevision);
        await f.Service.UndoAsync(receipt.BatchId,1,f.UndoGuard(receipt.BatchId,1),f.Backup(),default);
        Assert.IsFalse(await f.Service.AreFilesAlreadyActiveAsync(plan.ShopId,plan.Analysis,default));
    }

    [TestMethod]
    public async Task KnownDuplicateObserverStopsRecognizingAfterOwnerUndo()
    {
        using var f=new Fixture();var plan=await f.Plan();var receipt=await f.Service.ApplyAsync(plan,f.Guard(plan),f.Backup(),default);
        var duplicate=await f.Plan();await f.Service.ApplyAsync(duplicate,f.Guard(duplicate),f.Backup(),default);
        Assert.IsTrue(await f.Service.AreFilesAlreadyActiveAsync(plan.ShopId,duplicate.Analysis,default));
        await f.Service.UndoAsync(receipt.BatchId,1,f.UndoGuard(receipt.BatchId,1),f.Backup(),default);
        Assert.IsFalse(await f.Service.AreFilesAlreadyActiveAsync(plan.ShopId,duplicate.Analysis,default));
    }
}
