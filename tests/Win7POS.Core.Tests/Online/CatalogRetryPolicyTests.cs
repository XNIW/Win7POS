using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class CatalogRetryPolicyTests
{
    [TestMethod]
    public void DeterministicCatalogFailures_AreTerminalForTheCurrentRevision()
    {
        var codes = new[]
        {
            "catalog_product_row_invalid",
            "catalog_category_row_invalid",
            "catalog_supplier_row_invalid",
            "catalog_price_row_invalid",
            "catalog_product_tombstone_invalid",
            "catalog_product_conflict",
            "catalog_v2_page_contract_invalid",
            "catalog_rows_not_fully_applied",
            "response_shop_mismatch",
            "catalog_version_changed_mid_pull"
        };
        foreach (var code in codes)
        {
            Assert.IsTrue(CatalogRetryPolicy.IsDeterministicRevisionFailure(code), code);
            Assert.IsFalse(CatalogRetryPolicy.ShouldOfferManualRetry(code, authenticationDenied: false), code);

            var schedule = OnlineSyncLaneSchedulePolicy.Evaluate(
                OnlineSyncLane.CatalogDelta,
                new OnlineSyncLaneOutcome(false, code, terminal: true),
                currentFailureCount: 0,
                DateTimeOffset.UtcNow,
                jitterSample: 0.5d);

            Assert.IsFalse(schedule.ShouldSchedule, code);
        }
    }

    [TestMethod]
    public void TransientNetworkFailure_RemainsRetryable()
    {
        Assert.IsFalse(CatalogRetryPolicy.IsDeterministicRevisionFailure("timeout"));
        Assert.IsTrue(CatalogRetryPolicy.ShouldOfferManualRetry("timeout", authenticationDenied: false));

        var schedule = OnlineSyncLaneSchedulePolicy.Evaluate(
            OnlineSyncLane.CatalogDelta,
            new OnlineSyncLaneOutcome(false, "timeout", offline: true),
            currentFailureCount: 0,
            DateTimeOffset.UtcNow,
            jitterSample: 0.5d);

        Assert.IsTrue(schedule.ShouldSchedule);
        Assert.IsTrue(schedule.Delay > TimeSpan.Zero);
    }

    [TestMethod]
    public async Task RevisionChangedTrigger_AllowsOneNewCatalogExecutionAfterTerminalFailure()
    {
        var runs = 0;
        var salesOrHardwareSideEffects = 0;
        using var supervisor = new OnlineSyncSupervisor(
            new OnlineSyncGeneration(
                "catalog-retry-policy-generation",
                "session",
                "device",
                "shop",
                "SHOP",
                "staff",
                1),
            (context, trigger, cancellationToken) =>
            {
                Assert.AreEqual(OnlineSyncLane.CatalogDelta, context.Lane);
                Assert.IsTrue(trigger == OnlineSyncLaneTrigger.FirstBootstrap ||
                              trigger == OnlineSyncLaneTrigger.RevisionChanged);
                runs++;
                return Task.FromResult(new OnlineSyncLaneOutcome(
                    success: runs > 1,
                    code: runs == 1 ? "catalog_product_row_invalid" : "success",
                    terminal: runs == 1));
            },
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask);

        var first = await supervisor.TriggerAsync(
            OnlineSyncLane.CatalogDelta,
            OnlineSyncLaneTrigger.FirstBootstrap);
        await supervisor.WhenIdleAsync();

        Assert.IsFalse(first.Success);
        Assert.IsTrue(first.Terminal);
        Assert.AreEqual(1, runs);
        Assert.AreEqual(0, salesOrHardwareSideEffects);

        var afterRevision = await supervisor.TriggerAsync(
            OnlineSyncLane.CatalogDelta,
            OnlineSyncLaneTrigger.RevisionChanged);
        await supervisor.WhenIdleAsync();

        Assert.IsTrue(afterRevision.Success);
        Assert.AreEqual(2, runs);
        Assert.AreEqual(0, salesOrHardwareSideEffects);
    }
}
