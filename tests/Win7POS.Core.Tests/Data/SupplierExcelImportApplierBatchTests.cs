using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SupplierExcelImportApplierBatchTests
{
    [TestMethod]
    [DataRow("name")]
    [DataRow("purchase")]
    [DataRow("retail")]
    [DataRow("quantity")]
    [DataRow("supplier")]
    [DataRow("category")]
    public async Task ApplyAsync_TargetFieldChangedAfterStep4_RejectsBeforeWrites(string field)
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "STALE-001").ConfigureAwait(false);
        var preview = await BuildUpdatePreviewAsync(db.Factory, "STALE-001", 41).ConfigureAwait(false);
        await MutateTargetFieldAsync(db.Factory, "STALE-001", field).ConfigureAwait(false);
        var concurrentState = await ReadProductStateAsync(db.Factory, "STALE-001").ConfigureAwait(false);

        var result = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        AssertStale(result, "expected_fields_changed");
        Assert.AreEqual(concurrentState, await ReadProductStateAsync(db.Factory, "STALE-001").ConfigureAwait(false));
        await AssertNoHistoryOrOutboxAsync(db.Factory).ConfigureAwait(false);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_ProductInsertedAfterNewPreview_RejectsBeforeWrites()
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "RACE-NEW", 52).ConfigureAwait(false);
        await SeedProductAsync(db.Factory, "RACE-NEW").ConfigureAwait(false);
        var concurrentState = await ReadProductStateAsync(db.Factory, "RACE-NEW").ConfigureAwait(false);

        var result = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        AssertStale(result, "expected_missing_now_exists");
        Assert.AreEqual(concurrentState, await ReadProductStateAsync(db.Factory, "RACE-NEW").ConfigureAwait(false));
        await AssertNoHistoryOrOutboxAsync(db.Factory).ConfigureAwait(false);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_ShopChangedAfterStep4_RejectsAllNewPreviewBeforeWrites()
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "SHOP-RACE", 57).ConfigureAwait(false);
        await SetShopContextAsync(db.Factory, "replacement-shop-id", "REPLACEMENT-SHOP", 1)
            .ConfigureAwait(false);

        var result = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        AssertStale(result, "expected_shop_changed");
        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_TransitionBarrierSerializesApplyAndEpochChangeIsRejected()
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "SHOP-BARRIER", 58).ConfigureAwait(false);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
            preview,
            "shop-barrier.xlsx",
            "test");
        var barrier = new CatalogShopTransitionBarrier(db.Factory);
        var lease = await barrier.EnterAsync().ConfigureAwait(false);
        Task<SupplierExcelImportApplyResult> applyTask;
        try
        {
            applyTask = new SupplierExcelImportApplier(db.Factory).ApplyAsync(
                preview,
                new SupplierExcelImportApplyOptions
                {
                    CatalogImportOutboxEntry = entry,
                    InsertNew = true
                });
            Assert.IsFalse(applyTask.IsCompleted, "Apply must wait while the shop transition lease is held.");
            await SetShopContextAsync(db.Factory, "test-shop-id", "TEST-SHOP", 1)
                .ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }

        var result = await applyTask.ConfigureAwait(false);
        AssertStale(result, "expected_transition_epoch_changed");
        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_UncapturedShopContext_IsRejectedDeterministically()
    {
        using var db = TestDb.Create();
        var preview = SupplierImportAnalyzer.BuildSyncPreview(
            new[] { NewRow("SHOP-CONTEXT-MISSING", 59, "Missing context") },
            Array.Empty<Win7POS.Core.Models.ProductDetailsRow>());

        var result = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);

        AssertStale(result, "apply_context_missing");
        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplyAsync_ProductDeletedAfterStep4_RejectsBeforeWrites(bool softDelete)
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "RACE-DELETE").ConfigureAwait(false);
        var preview = await BuildUpdatePreviewAsync(db.Factory, "RACE-DELETE", 63).ConfigureAwait(false);
        using (var conn = db.Factory.Open())
        {
            if (softDelete)
            {
                await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = '2026-08-03T12:00:00Z'
WHERE barcode = 'RACE-DELETE';").ConfigureAwait(false);
            }
            else
            {
                await conn.ExecuteAsync(@"
DELETE FROM product_meta WHERE barcode = 'RACE-DELETE';
DELETE FROM products WHERE barcode = 'RACE-DELETE';").ConfigureAwait(false);
            }
        }

        var result = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        AssertStale(result, softDelete ? "expected_active_changed" : "expected_exists_now_missing");
        Assert.AreEqual(softDelete ? 1L : 0L, await ScalarLongAsync(
            db.Factory,
            "SELECT COUNT(1) FROM products WHERE barcode = 'RACE-DELETE';").ConfigureAwait(false));
        await AssertNoHistoryOrOutboxAsync(db.Factory).ConfigureAwait(false);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_UnrelatedCatalogChange_DoesNotInvalidateTargetScopedPreview()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "TARGET-001").ConfigureAwait(false);
        await SeedProductAsync(db.Factory, "UNRELATED-001").ConfigureAwait(false);
        var preview = await BuildUpdatePreviewAsync(db.Factory, "TARGET-001", 74).ConfigureAwait(false);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE products SET name = 'Concurrent unrelated' WHERE barcode = 'UNRELATED-001';")
                .ConfigureAwait(false);
        }

        var result = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        Assert.AreEqual(0, result.Errors, string.Join(" | ", result.ErrorMessages));
        Assert.AreEqual(1, result.Updated);
        Assert.AreEqual(1L, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM catalog_import_outbox;").ConfigureAwait(false));
        Assert.AreEqual(
            "Concurrent unrelated",
            await ScalarStringAsync(db.Factory, "SELECT name FROM products WHERE barcode = 'UNRELATED-001';").ConfigureAwait(false));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_TargetChangedAndRestoredToEquivalentState_RemainsValid()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "RESTORED-001").ConfigureAwait(false);
        var preview = await BuildUpdatePreviewAsync(db.Factory, "RESTORED-001", 85).ConfigureAwait(false);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync("UPDATE products SET name = 'Transient value' WHERE barcode = 'RESTORED-001';").ConfigureAwait(false);
            await conn.ExecuteAsync("UPDATE products SET name = 'Existing RESTORED-001' WHERE barcode = 'RESTORED-001';").ConfigureAwait(false);
        }

        var result = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        Assert.AreEqual(0, result.Errors, string.Join(" | ", result.ErrorMessages));
        Assert.AreEqual(1, result.Updated);
        Assert.AreEqual("Imported RESTORED-001", await ScalarStringAsync(
            db.Factory,
            "SELECT name FROM products WHERE barcode = 'RESTORED-001';").ConfigureAwait(false));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_DuplicateBarcode_LastWinsWithOriginalSourceRowAndOneOutboxItem()
    {
        using var db = TestDb.Create();
        var rows = new[]
        {
            NewRow("DUP-LAST-WINS", 101, "First"),
            NewRow("DUP-LAST-WINS", 207, "Last")
        };
        var preview = await new SupplierExcelImportApplier(db.Factory)
            .BuildPreviewAsync(rows)
            .ConfigureAwait(false);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "duplicate.xlsx", "test");

        var result = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry, InsertNew = true })
            .ConfigureAwait(false);

        Assert.AreEqual(0, result.Errors, string.Join(" | ", result.ErrorMessages));
        Assert.AreEqual(1, result.Inserted);
        Assert.AreEqual(1, result.SqlMetrics.PriceHistoryCommands);
        Assert.AreEqual(2, result.SqlMetrics.PriceHistoryRows);
        Assert.AreEqual(3, result.SqlMetrics.OutboxCommands);
        Assert.AreEqual("Last", await ScalarStringAsync(
            db.Factory,
            "SELECT name FROM products WHERE barcode = 'DUP-LAST-WINS';").ConfigureAwait(false));
        Assert.AreEqual(2L, await ScalarLongAsync(
            db.Factory,
            "SELECT COUNT(1) FROM product_price_history WHERE barcode = 'DUP-LAST-WINS';").ConfigureAwait(false));
        var payload = await ScalarStringAsync(db.Factory, "SELECT payload_json FROM catalog_import_outbox;").ConfigureAwait(false);
        var request = Deserialize<PosCatalogImportRequest>(payload);
        Assert.AreEqual(1, request.Items.Length);
        Assert.AreEqual(207, request.Items[0].RowNumber);
        Assert.AreEqual("Last", request.Items[0].ProductName);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_BlankExistingFieldsArePreservedAndNoChangeDoesNotWriteHistoryOrOutbox()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "BLANK-PRESERVE").ConfigureAwait(false);
        var original = await ReadProductStateAsync(db.Factory, "BLANK-PRESERVE").ConfigureAwait(false);
        var preview = await new SupplierExcelImportApplier(db.Factory).BuildPreviewAsync(
            new[]
            {
                new SupplierImportEditableRow
                {
                    RowNumber = 211,
                    Barcode = "BLANK-PRESERVE"
                }
            }).ConfigureAwait(false);

        var result = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions
            {
                CatalogImportOutboxEntry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
                    preview,
                    "blank.xlsx",
                    "test"),
                InsertNew = true
            }).ConfigureAwait(false);

        Assert.AreEqual(0, result.Errors, string.Join(" | ", result.ErrorMessages));
        Assert.AreEqual(1, result.NoChange);
        Assert.AreEqual(0, result.SqlMetrics.ProductWriteCommands);
        Assert.AreEqual(0, result.SqlMetrics.PriceHistoryCommands);
        Assert.AreEqual(0, result.SqlMetrics.OutboxCommands);
        Assert.AreEqual(original, await ReadProductStateAsync(db.Factory, "BLANK-PRESERVE").ConfigureAwait(false));
        await AssertNoHistoryOrOutboxAsync(db.Factory).ConfigureAwait(false);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_PriceChangesWriteExactOldNewClpValuesWithImportSource()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "PRICE-HISTORY").ConfigureAwait(false);
        var preview = await BuildUpdatePreviewAsync(db.Factory, "PRICE-HISTORY", 222).ConfigureAwait(false);

        var result = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        Assert.AreEqual(0, result.Errors, string.Join(" | ", result.ErrorMessages));
        Assert.AreEqual(2, result.PriceHistoryInserted);
        Assert.AreEqual(1, result.SqlMetrics.PriceHistoryCommands);
        using (var conn = db.Factory.Open())
        {
            var history = (await conn.QueryAsync<PriceHistoryProof>(@"
SELECT type AS Type, old_price AS OldPrice, new_price AS NewPrice, source AS Source
FROM product_price_history
WHERE barcode = 'PRICE-HISTORY'
ORDER BY type ASC;").ConfigureAwait(false)).ToArray();
            Assert.AreEqual(2, history.Length);
            Assert.AreEqual("purchase", history[0].Type);
            Assert.AreEqual(100, history[0].OldPrice);
            Assert.AreEqual(200, history[0].NewPrice);
            Assert.AreEqual("IMPORT", history[0].Source);
            Assert.AreEqual("retail", history[1].Type);
            Assert.AreEqual(180, history[1].OldPrice);
            Assert.AreEqual(300, history[1].NewPrice);
            Assert.AreEqual("IMPORT", history[1].Source);
        }
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_ReplayingSuccessfulNewPreview_IsSafelyRejectedWithoutDuplicateHistoryOrOutbox()
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "REPLAY-001", 233).ConfigureAwait(false);
        var first = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);
        Assert.AreEqual(0, first.Errors, string.Join(" | ", first.ErrorMessages));

        var replay = await ApplyWithOutboxAsync(db.Factory, preview).ConfigureAwait(false);

        AssertStale(replay, "expected_missing_now_exists");
        Assert.AreEqual(1L, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM products;").ConfigureAwait(false));
        Assert.AreEqual(2L, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM product_price_history;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM catalog_import_outbox;").ConfigureAwait(false));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_DryRunWithConcurrentTargetChange_RejectsAndLeavesNoResidue()
    {
        using var db = TestDb.Create();
        await SeedProductAsync(db.Factory, "DRY-STALE").ConfigureAwait(false);
        var preview = await BuildUpdatePreviewAsync(db.Factory, "DRY-STALE", 96).ConfigureAwait(false);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync("UPDATE product_meta SET stock_qty = 99 WHERE barcode = 'DRY-STALE';").ConfigureAwait(false);
        }

        var result = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { DryRun = true, InsertNew = true })
            .ConfigureAwait(false);

        AssertStale(result, "expected_fields_changed");
        Assert.AreEqual(99L, await ScalarLongAsync(
            db.Factory,
            "SELECT stock_qty FROM product_meta WHERE barcode = 'DRY-STALE';").ConfigureAwait(false));
        await AssertNoHistoryOrOutboxAsync(db.Factory).ConfigureAwait(false);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_BatchSnapshotUsesBoundedObservedSelectCount()
    {
        using var db = TestDb.Create();
        const int rowCount = 1200;
        await SeedProductsAsync(db.Factory, rowCount).ConfigureAwait(false);
        var rows = Enumerable.Range(1, rowCount)
            .Select(index => ExistingNoChangeRow(index))
            .ToArray();
        var preview = await new SupplierExcelImportApplier(db.Factory)
            .BuildPreviewAsync(rows)
            .ConfigureAwait(false);

        var result = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { DryRun = true, InsertNew = true })
            .ConfigureAwait(false);

        Assert.AreEqual(0, result.Errors, string.Join(" | ", result.ErrorMessages));
        Assert.AreEqual(rowCount, result.NoChange);
        Assert.AreEqual(3, result.SqlMetrics.ExistingProductSelectCommands);
        Assert.AreEqual(1, result.SqlMetrics.ApplyContextSelectCommands);
        Assert.AreEqual(4, result.SqlMetrics.TotalCommands);
        Assert.AreEqual(rowCount, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM products;").ConfigureAwait(false));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("AfterBatchPreload", 0)]
    [DataRow("AfterRevalidation", 0)]
    [DataRow("AfterReferenceResolution", 0)]
    [DataRow("AfterProductWrites", 1)]
    [DataRow("AfterHistoryWrites", 1)]
    [DataRow("BeforeOutboxEnqueue", 0)]
    [DataRow("AfterOutboxEnqueueBeforeCommit", 0)]
    public async Task ApplyAsync_DeterministicFaultPoint_RollsBackEverythingAndRetrySucceeds(
        string pointName,
        int failAfter)
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "FAULT-001", 111).ConfigureAwait(false);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "fault.xlsx", "test");
        var hooks = new SupplierExcelImportTestHooks
        {
            FaultPoint = Enum.Parse<SupplierExcelImportFaultPoint>(pointName),
            FailAfterProductWrites = failAfter,
            FailAfterHistoryWrites = failAfter
        };

        var failed = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions
            {
                CatalogImportOutboxEntry = entry,
                InsertNew = true,
                TestHooks = hooks
            }).ConfigureAwait(false);

        Assert.AreEqual(1, failed.Errors);
        Assert.IsTrue(failed.ErrorMessages.Single().Contains("SUPPLIER_IMPORT_TEST_FAULT", StringComparison.Ordinal));
        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);

        var retry = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry, InsertNew = true })
            .ConfigureAwait(false);
        Assert.AreEqual(0, retry.Errors, string.Join(" | ", retry.ErrorMessages));
        Assert.AreEqual(1, retry.Inserted);
        Assert.AreEqual(1L, await ScalarLongAsync(db.Factory, "SELECT COUNT(1) FROM catalog_import_outbox;").ConfigureAwait(false));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_OutboxInsertFailure_RollsBackProductsMetaHistoryAndOutbox()
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "OUTBOX-FAIL", 122).ConfigureAwait(false);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "outbox.xlsx", "test");
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
CREATE TRIGGER supplier_import_outbox_failure
BEFORE INSERT ON catalog_import_outbox
BEGIN
  SELECT RAISE(ABORT, 'forced outbox enqueue failure');
END;").ConfigureAwait(false);
        }

        var failed = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry, InsertNew = true })
            .ConfigureAwait(false);

        Assert.AreEqual(1, failed.Errors);
        Assert.IsTrue(failed.ErrorMessages.Single().Contains("forced outbox enqueue failure", StringComparison.Ordinal));
        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync("DROP TRIGGER supplier_import_outbox_failure;").ConfigureAwait(false);
        }
        var retry = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry, InsertNew = true })
            .ConfigureAwait(false);
        Assert.AreEqual(0, retry.Errors, string.Join(" | ", retry.ErrorMessages));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_PriceHistoryInsertFailure_RollsBackProductsMetaReferencesAndOutbox()
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "HISTORY-FAIL", 244).ConfigureAwait(false);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "history.xlsx", "test");
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
CREATE TRIGGER supplier_import_history_failure
BEFORE INSERT ON product_price_history
BEGIN
  SELECT RAISE(ABORT, 'forced history insert failure');
END;").ConfigureAwait(false);
        }

        var failed = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry, InsertNew = true })
            .ConfigureAwait(false);

        Assert.AreEqual(1, failed.Errors);
        Assert.IsTrue(failed.ErrorMessages.Single().Contains("forced history insert failure", StringComparison.Ordinal));
        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync("DROP TRIGGER supplier_import_history_failure;").ConfigureAwait(false);
        }
        var retry = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry, InsertNew = true })
            .ConfigureAwait(false);
        Assert.AreEqual(0, retry.Errors, string.Join(" | ", retry.ErrorMessages));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_CancelledBeforeTransaction_WritesNothingAndCanRetry()
    {
        using var db = TestDb.Create();
        var preview = await BuildNewPreviewAsync(db.Factory, "CANCEL-BEFORE", 133).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SupplierExcelImportApplier(db.Factory).ApplyAsync(
                preview,
                new SupplierExcelImportApplyOptions { InsertNew = true },
                cancellation.Token));

        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);
        var retry = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
        Assert.AreEqual(0, retry.Errors, string.Join(" | ", retry.ErrorMessages));
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_CancelledBetweenPreloadBatches_RollsBackAndCanRetry()
    {
        using var db = TestDb.Create();
        var rows = Enumerable.Range(1, SupplierExcelImportBatching.ParameterBatchSize + 1)
            .Select(index => NewRow("CANCEL-BATCH-" + index.ToString("0000", CultureInfo.InvariantCulture), index + 200, "Batch " + index))
            .ToArray();
        var preview = await new SupplierExcelImportApplier(db.Factory)
            .BuildPreviewAsync(rows)
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var hooks = new SupplierExcelImportTestHooks
        {
            AfterExistingProductBatch = batchNumber =>
            {
                if (batchNumber == 1)
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SupplierExcelImportApplier(db.Factory).ApplyAsync(
                preview,
                new SupplierExcelImportApplyOptions { InsertNew = true, TestHooks = hooks },
                cancellation.Token));

        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);
        var retry = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
        Assert.AreEqual(0, retry.Errors, string.Join(" | ", retry.ErrorMessages));
        Assert.AreEqual(rows.Length, retry.Inserted);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ApplyAsync_CancelledAfterFirstProductWrite_RollsBackAndCanRetry()
    {
        using var db = TestDb.Create();
        var rows = new[]
        {
            NewRow("CANCEL-WRITE-1", 301, "First"),
            NewRow("CANCEL-WRITE-2", 302, "Second")
        };
        var preview = await new SupplierExcelImportApplier(db.Factory)
            .BuildPreviewAsync(rows)
            .ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var hooks = new SupplierExcelImportTestHooks
        {
            AfterProductWrite = count =>
            {
                if (count == 1)
                    cancellation.Cancel();
            }
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new SupplierExcelImportApplier(db.Factory).ApplyAsync(
                preview,
                new SupplierExcelImportApplyOptions { InsertNew = true, TestHooks = hooks },
                cancellation.Token));

        await AssertEmptyApplyStateAsync(db.Factory).ConfigureAwait(false);
        var retry = await new SupplierExcelImportApplier(db.Factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
        Assert.AreEqual(0, retry.Errors, string.Join(" | ", retry.ErrorMessages));
        Assert.AreEqual(2, retry.Inserted);
        await AssertIntegrityAsync(db.Factory).ConfigureAwait(false);
    }

    private static Task<SupplierImportSyncPreview> BuildNewPreviewAsync(
        SqliteConnectionFactory factory,
        string barcode,
        int rowNumber)
    {
        return new SupplierExcelImportApplier(factory).BuildPreviewAsync(
            new[] { NewRow(barcode, rowNumber, "Imported " + barcode) });
    }

    private static async Task<SupplierImportSyncPreview> BuildUpdatePreviewAsync(
        SqliteConnectionFactory factory,
        string barcode,
        int rowNumber)
    {
        return await new SupplierExcelImportApplier(factory).BuildPreviewAsync(
            new[]
            {
                new SupplierImportEditableRow
                {
                    RowNumber = rowNumber,
                    Barcode = barcode,
                    ItemNumber = "IMPORTED-ITEM",
                    ProductName = "Imported " + barcode,
                    SecondProductName = "Imported second",
                    PurchasePrice = "200",
                    RetailPrice = "300",
                    Quantity = "4",
                    Supplier = "Imported Supplier",
                    Category = "Imported Category"
                }
            }).ConfigureAwait(false);
    }

    private static async Task<SupplierExcelImportApplyResult> ApplyWithOutboxAsync(
        SqliteConnectionFactory factory,
        SupplierImportSyncPreview preview)
    {
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "stale.xlsx", "test");
        return await new SupplierExcelImportApplier(factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry, InsertNew = true })
            .ConfigureAwait(false);
    }

    private static SupplierImportEditableRow NewRow(string barcode, int rowNumber, string name)
    {
        return new SupplierImportEditableRow
        {
            RowNumber = rowNumber,
            Barcode = barcode,
            ItemNumber = "ITEM-" + rowNumber.ToString(CultureInfo.InvariantCulture),
            ProductName = name,
            SecondProductName = "Second " + rowNumber.ToString(CultureInfo.InvariantCulture),
            PurchasePrice = "100",
            RetailPrice = "180",
            Quantity = "2",
            Supplier = "New Supplier",
            Category = "New Category"
        };
    }

    private static SupplierImportEditableRow ExistingNoChangeRow(int index)
    {
        return new SupplierImportEditableRow
        {
            RowNumber = index + 1,
            Barcode = PerfBarcode(index),
            ItemNumber = "ITEM-" + index.ToString("0000", CultureInfo.InvariantCulture),
            ProductName = "Existing " + index.ToString(CultureInfo.InvariantCulture),
            SecondProductName = "Second " + index.ToString(CultureInfo.InvariantCulture),
            PurchasePrice = "100",
            RetailPrice = "180",
            Quantity = "2",
            Supplier = "Shared Supplier",
            Category = "Shared Category"
        };
    }

    private static async Task SeedProductsAsync(SqliteConnectionFactory factory, int count)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        using var product = conn.CreateCommand();
        using var meta = conn.CreateCommand();
        product.Transaction = tx;
        product.CommandText = @"
INSERT INTO products(barcode, name, unitPrice, is_active, remote_deleted_at)
VALUES(@barcode, @name, 180, 1, NULL);";
        var productBarcode = product.Parameters.Add("@barcode", SqliteType.Text);
        var productName = product.Parameters.Add("@name", SqliteType.Text);
        product.Prepare();
        meta.Transaction = tx;
        meta.CommandText = @"
INSERT INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @barcode, @articleCode, @name2, 100, 0, 0,
  1, 'Shared Supplier', 1, 'Shared Category', 2);";
        var metaBarcode = meta.Parameters.Add("@barcode", SqliteType.Text);
        var articleCode = meta.Parameters.Add("@articleCode", SqliteType.Text);
        var name2 = meta.Parameters.Add("@name2", SqliteType.Text);
        meta.Prepare();
        await conn.ExecuteAsync(@"
INSERT INTO suppliers(id, name, is_active) VALUES(1, 'Shared Supplier', 1);
INSERT INTO categories(id, name, is_active) VALUES(1, 'Shared Category', 1);", transaction: tx).ConfigureAwait(false);
        for (var index = 1; index <= count; index++)
        {
            productBarcode.Value = PerfBarcode(index);
            productName.Value = "Existing " + index.ToString(CultureInfo.InvariantCulture);
            await product.ExecuteNonQueryAsync().ConfigureAwait(false);
            metaBarcode.Value = PerfBarcode(index);
            articleCode.Value = "ITEM-" + index.ToString("0000", CultureInfo.InvariantCulture);
            name2.Value = "Second " + index.ToString(CultureInfo.InvariantCulture);
            await meta.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        tx.Commit();
    }

    private static async Task SeedProductAsync(SqliteConnectionFactory factory, string barcode)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO suppliers(id, name, is_active) VALUES(1, 'Existing Supplier', 1);
INSERT OR IGNORE INTO categories(id, name, is_active) VALUES(1, 'Existing Category', 1);
INSERT INTO products(barcode, name, unitPrice, is_active, remote_deleted_at)
VALUES(@barcode, @name, 180, 1, NULL);
INSERT INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @barcode, 'EXISTING-ITEM', 'Existing second', 100, 0, 0,
  1, 'Existing Supplier', 1, 'Existing Category', 2);",
            new { barcode, name = "Existing " + barcode }).ConfigureAwait(false);
    }

    private static async Task MutateTargetFieldAsync(
        SqliteConnectionFactory factory,
        string barcode,
        string field)
    {
        var sql = field switch
        {
            "name" => "UPDATE products SET name = 'Concurrent name' WHERE barcode = @barcode;",
            "purchase" => "UPDATE product_meta SET purchase_price = 777 WHERE barcode = @barcode;",
            "retail" => "UPDATE products SET unitPrice = 888 WHERE barcode = @barcode;",
            "quantity" => "UPDATE product_meta SET stock_qty = 99 WHERE barcode = @barcode;",
            "supplier" => "UPDATE product_meta SET supplier_id = 77, supplier_name = 'Concurrent supplier' WHERE barcode = @barcode;",
            "category" => "UPDATE product_meta SET category_id = 88, category_name = 'Concurrent category' WHERE barcode = @barcode;",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        using var conn = factory.Open();
        await conn.ExecuteAsync(sql, new { barcode }).ConfigureAwait(false);
    }

    private static async Task SetShopContextAsync(
        SqliteConnectionFactory factory,
        string shopId,
        string shopCode,
        long transitionEpoch)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@shopIdKey, @shopId)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
INSERT INTO app_settings(key, value) VALUES(@shopCodeKey, @shopCode)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
INSERT INTO app_settings(key, value) VALUES(@epochKey, @epoch)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            new
            {
                shopIdKey = OutboxShopBinding.OfficialShopIdKey,
                shopId,
                shopCodeKey = OutboxShopBinding.OfficialShopCodeKey,
                shopCode,
                epochKey = CatalogShopStateRepository.TransitionEpochKey,
                epoch = transitionEpoch.ToString(CultureInfo.InvariantCulture)
            }).ConfigureAwait(false);
    }

    private static void AssertStale(SupplierExcelImportApplyResult result, string reason)
    {
        Assert.AreEqual(1, result.Errors, string.Join(" | ", result.ErrorMessages));
        Assert.IsTrue(result.ErrorMessages.Single().Contains(SupplierExcelImportApplier.StalePreviewErrorCode, StringComparison.Ordinal));
        Assert.IsTrue(result.ErrorMessages.Single().Contains(reason, StringComparison.Ordinal));
        Assert.AreEqual(0, result.Inserted);
        Assert.AreEqual(0, result.Updated);
    }

    private static async Task AssertEmptyApplyStateAsync(SqliteConnectionFactory factory)
    {
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM products;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM product_meta;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM suppliers;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM categories;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM product_price_history;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM catalog_import_outbox;").ConfigureAwait(false));
        await AssertIntegrityAsync(factory).ConfigureAwait(false);
    }

    private static async Task AssertNoHistoryOrOutboxAsync(SqliteConnectionFactory factory)
    {
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM product_price_history;").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarLongAsync(factory, "SELECT COUNT(1) FROM catalog_import_outbox;").ConfigureAwait(false));
    }

    private static async Task<string> ReadProductStateAsync(SqliteConnectionFactory factory, string barcode)
    {
        using var conn = factory.Open();
        var row = await conn.QuerySingleAsync(@"
SELECT
  p.id || '|' || p.barcode || '|' || p.name || '|' || p.unitPrice || '|' || COALESCE(p.is_active, 1) || '|' ||
  COALESCE(m.article_code, '') || '|' || COALESCE(m.name2, '') || '|' || COALESCE(m.purchase_price, 0) || '|' ||
  COALESCE(m.stock_qty, 0) || '|' || COALESCE(m.supplier_id, -1) || '|' || COALESCE(m.supplier_name, '') || '|' ||
  COALESCE(m.category_id, -1) || '|' || COALESCE(m.category_name, '') AS Value
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.barcode = @barcode;", new { barcode }).ConfigureAwait(false);
        return Convert.ToString(row.Value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task AssertIntegrityAsync(SqliteConnectionFactory factory)
    {
        Assert.AreEqual("ok", await ScalarStringAsync(factory, "PRAGMA integrity_check;").ConfigureAwait(false), true);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnectionFactory factory, string sql)
    {
        using var conn = factory.Open();
        return Convert.ToInt64(await conn.ExecuteScalarAsync(sql).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnectionFactory factory, string sql)
    {
        using var conn = factory.Open();
        return Convert.ToString(await conn.ExecuteScalarAsync(sql).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static T Deserialize<T>(string json) where T : class
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(stream)!;
    }

    private static string PerfBarcode(int index)
    {
        return "BATCH-" + index.ToString("000000", CultureInfo.InvariantCulture);
    }

    private sealed class PriceHistoryProof
    {
        public string Type { get; set; } = string.Empty;
        public int? OldPrice { get; set; }
        public int NewPrice { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
            using var conn = Factory.Open();
            conn.Execute(@"
INSERT INTO app_settings(key, value) VALUES(@codeKey, 'TEST-SHOP')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
INSERT INTO app_settings(key, value) VALUES(@idKey, 'test-shop-id')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new
                {
                    codeKey = OutboxShopBinding.OfficialShopCodeKey,
                    idKey = OutboxShopBinding.OfficialShopIdKey
                });
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-supplier-batch-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
