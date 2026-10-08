using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class SupplierImportCompletionSmoke
    {
        internal static async Task RunAsync()
        {
            SeedShop();
            var lines = EnvironmentDetails();
            lines.Add("sampling=one warmup then five post-change samples; pre-change evidence has one cold sample only");
            var dispatcher = Dispatcher.CurrentDispatcher;
            var authorizationCalls = 0;
            Func<bool> authorize = () =>
            {
                Require(dispatcher.CheckAccess(), "authorization left Dispatcher thread");
                authorizationCalls++;
                return true;
            };
            var service = new SupplierExcelImportWorkflowService(authorize);
            await CheckRetailHistoryAndAdminBoundsAsync(service, lines);
            await ProductPriceHistoryCompletionSmoke.RunAsync(lines);
            var rows = MakeRows(20000, "IMPORT-BASELINE-");
            await service.BuildSyncPreviewAsync(rows);
            await service.ApplyAsync(rows, true);
            var previewSamples = new List<double>();
            var applySamples = new List<double>();
            for (var sample = 1; sample <= 5; sample++)
            {
                previewSamples.Add(await MeasureAsync(lines, "preview_sample" + sample, () => service.BuildSyncPreviewAsync(rows), true));
                applySamples.Add(await MeasureAsync(lines, "apply_dryrun_sample" + sample, () => service.ApplyAsync(rows, true), true));
            }
            AddPercentiles(lines, "preview", previewSamples);
            AddPercentiles(lines, "apply_dryrun", applySamples);
            await CheckBackupPathFailureAsync(service, lines);
            await CheckVerifiedBackupWithWriterAsync(authorize, lines);
            await CheckInvalidSnapshotAsync(service, lines);
            await CheckBackupFaultsAsync(authorize, lines);
            await CheckCorrectionAndDoubleApplyAsync(service, lines);
            await CheckConcurrentChangeAsync(authorize, lines);
            await CheckCancellationAndCaptureAsync(service, lines);
            await CheckWizardCancellationAsync(authorize, lines);
            await CheckExactTextPersistenceAsync(service, lines);
            await CheckWorkflowRollbackAsync(authorize, lines);
            var commitPreview = await service.BuildSyncPreviewAsync(MakeRows(20000, "IMPORT-COMMIT-"));
            await MeasureAsync(lines, "apply_commit_20000_single_sample", () => service.ApplyAsync(commitPreview, false, "large.xlsx"), true);
            lines.Add("authorization_dispatcher_calls=" + authorizationCalls);
            File.WriteAllLines(Path.Combine(AppPaths.DataDirectory, "import-completion.txt"), lines);
        }

        private static void AddPercentiles(List<string> lines, string name, List<double> samples)
        {
            samples.Sort();
            lines.Add(name + " samples=5 p50_ms=" + samples[2].ToString("F1", CultureInfo.InvariantCulture) +
                " p95_nearest_rank_ms=" + samples[4].ToString("F1", CultureInfo.InvariantCulture) +
                " max_ms=" + samples[4].ToString("F1", CultureInfo.InvariantCulture));
        }

        private static async Task CheckRetailHistoryAndAdminBoundsAsync(SupplierExcelImportWorkflowService service, List<string> lines)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var products = new ProductRepository(factory);
            var applier = new SupplierExcelImportApplier(factory);
            var values = new[] { 0L, 999999999L, int.MaxValue, 2147483648L, 4294967296L, long.MaxValue };
            var cases = 0;
            foreach (var update in new[] { false, true })
            foreach (var value in values)
            {
                var barcode = "IMPORT-LONG-" + cases++;
                var oldPrice = value == 4294967296L ? 2147483648L : 4294967296L;
                if (update)
                    await products.UpsertAsync(new Product { Barcode = barcode, Name = "Before", UnitPrice = oldPrice }, ProductWriteOrigin.SupplierImportApply);
                var row = MakeRows(1, barcode)[0];
                row.Barcode = barcode;
                row.RetailPrice = value.ToString(CultureInfo.InvariantCulture);
                var localPreview = await applier.BuildPreviewAsync(new[] { row });
                var result = await applier.ApplyAsync(localPreview, new SupplierExcelImportApplyOptions { InsertNew = true });
                Require(result.Errors == 0, "local Int64 retail apply failed");
                var history = (await products.GetPriceHistoryByBarcodeAsync(barcode)).Single(item => item.PriceType == "retail");
                Require((await products.GetByBarcodeAsync(barcode)).UnitPrice == value && history.NewPrice == value &&
                    history.OldPrice == (update ? (long?)oldPrice : null), "local Int64 retail history lost precision");
            }
            var rejectedCases = 0;
            foreach (var update in new[] { false, true })
            foreach (var field in new[] { "retailPrice", "purchasePrice" })
            {
                var row = MakeRows(1, "IMPORT-ADMIN-REJECT-" + rejectedCases++)[0];
                if (update)
                    await products.UpsertAsync(new Product { Barcode = row.Barcode, Name = "Before", UnitPrice = 4294967296L }, ProductWriteOrigin.SupplierImportApply);
                if (field == "retailPrice") row.RetailPrice = "1000000000";
                else row.PurchasePrice = "1000000000";
                long productCount, historyCount, outboxCount;
                using (var connection = factory.Open())
                {
                    productCount = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM products");
                    historyCount = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history");
                    outboxCount = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox");
                }
                var backupCount = Directory.GetFiles(AppPaths.BackupsDirectory).Length;
                var preview = await service.BuildSyncPreviewAsync(new[] { row });
                Require(!preview.CanApply && preview.Errors.Any(error => error.RowIndex == row.RowNumber && error.Message.Contains(field)), "Admin preview did not reject unsupported price in its field");
                Require(preview.FinalRows.Single().RetailPrice == row.RetailPrice && preview.FinalRows.Single().PurchasePrice == row.PurchasePrice, "Admin rejection changed the editable draft");
                var rejected = false;
                try { await service.ApplyAsync(preview, false, "admin-rejected.xlsx"); }
                catch (SupplierExcelImportWorkflowException) { rejected = true; }
                using (var connection = factory.Open())
                {
                    Require(rejected && connection.ExecuteScalar<long>("SELECT COUNT(*) FROM products") == productCount &&
                        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history") == historyCount &&
                        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox") == outboxCount &&
                        Directory.GetFiles(AppPaths.BackupsDirectory).Length == backupCount, "Admin rejection wrote products/history/outbox or created backup");
                }
                if (update)
                {
                    var product = await products.GetByBarcodeAsync(row.Barcode);
                    Require(product.Name == "Before" && product.UnitPrice == 4294967296L, "Admin rejection changed existing product economics");
                }
            }
            var accepted = MakeRows(1, "IMPORT-ADMIN-MAX-")[0];
            accepted.PurchasePrice = accepted.RetailPrice = "999999999";
            var acceptedPreview = await service.BuildSyncPreviewAsync(new[] { accepted });
            var applied = await service.ApplyAsync(acceptedPreview, false, "admin-max.xlsx");
            var acceptedHistory = await products.GetPriceHistoryByBarcodeAsync(accepted.Barcode);
            using (var connection = factory.Open())
            {
                var payload = connection.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId });
                Require(applied.Success && (await products.GetByBarcodeAsync(accepted.Barcode)).UnitPrice == 999999999L &&
                    acceptedHistory.All(item => item.NewPrice == 999999999L) && payload.Contains("\"retailPrice\":\"999999999\"") &&
                    payload.Contains("\"purchasePrice\":\"999999999\""), "Admin maximum lost price parity");
            }
            lines.Add("retail_int64_history=PASS local_insert_update_cases=" + cases + " Admin_rejections=" + rejectedCases + " Admin_max=999999999 exact_product_history_outbox=True");
        }

        private static async Task CheckVerifiedBackupWithWriterAsync(Func<bool> authorize, List<string> lines)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            using (var connection = factory.Open())
            {
                connection.Execute("CREATE TABLE completion_import_writer(id INTEGER PRIMARY KEY, version INTEGER NOT NULL, payload TEXT NOT NULL);");
                using (var transaction = connection.BeginTransaction())
                {
                    connection.Execute("INSERT INTO completion_import_writer(id,version,payload) VALUES(@id,0,@payload)",
                        Enumerable.Range(1, 128).Select(id => new { id, payload = new string('a', 1024) }), transaction);
                    transaction.Commit();
                }
                connection.Execute("INSERT OR REPLACE INTO app_settings(key,value) VALUES('qa.import.snapshot','captured');");
            }
            var diagnosticPhases = new List<string>();
            var backupWorkerThread = 0;
            var writerCommits = 0;
            var hooks = new BackupRestoreTestHooks
            {
                NativeSnapshotRunner = snapshot =>
                {
                    using (var firstCommit = new ManualResetEventSlim())
                    {
                        var writer = Task.Run(() =>
                        {
                            using (var connection = factory.Open())
                            {
                                for (var version = 1; version <= 20; version++)
                                {
                                    using (var transaction = connection.BeginTransaction())
                                    {
                                        connection.Execute("UPDATE completion_import_writer SET version=@version,payload=@payload", new { version, payload = new string((char)('a' + version), 1024) }, transaction);
                                        transaction.Commit();
                                    }
                                    Interlocked.Increment(ref writerCommits);
                                    firstCommit.Set();
                                    Thread.Sleep(2);
                                }
                            }
                        });
                        Require(firstCommit.Wait(TimeSpan.FromSeconds(5)), "concurrent writer did not commit");
                        try { snapshot(); }
                        finally { writer.GetAwaiter().GetResult(); }
                    }
                },
                BackupFault = point =>
                {
                    if (point != BackupFailurePoint.AfterSnapshotBeforeValidation) return;
                    using (var connection = factory.Open()) connection.Execute("UPDATE app_settings SET value='after' WHERE key='qa.import.snapshot';");
                }
            };
            var backup = new SqliteOnlineBackup(factory, diagnostic =>
            {
                backupWorkerThread = Thread.CurrentThread.ManagedThreadId;
                diagnosticPhases.Add(diagnostic.Phase + ":" + diagnostic.ResultCode);
            }, hooks);
            var service = new SupplierExcelImportWorkflowService(authorize, PosDbOptions.Default(), backup);
            var row = MakeRows(1, "IMPORT-SNAPSHOT-")[0];
            var preview = await service.BuildSyncPreviewAsync(new[] { row });
            var result = await service.ApplyAsync(preview, false, "snapshot.xlsx");
            var validation = await new DbMaintenanceRepository(new SqliteConnectionFactory(PosDbOptions.ForPath(result.BackupPath))).ValidateAsync();
            Require(validation.IsValid && writerCommits == 20, "backup did not survive controlled writer");
            using (var connection = new SqliteConnectionFactory(PosDbOptions.ForPath(result.BackupPath)).Open())
            {
                Require(connection.ExecuteScalar<int>("SELECT COUNT(DISTINCT version) FROM completion_import_writer") == 1, "backup contains torn committed writer state");
                Require(connection.ExecuteScalar<string>("SELECT value FROM app_settings WHERE key='qa.import.snapshot'") == "captured", "published snapshot changed after capture");
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM products WHERE barcode=@barcode", new { barcode = row.Barcode }) == 0, "backup included import writes");
            }
            Require(result.Inserted == 1 && result.CatalogImportOutboxId > 0 &&
                diagnosticPhases.Contains("complete:backup_verified") && backupWorkerThread != Thread.CurrentThread.ManagedThreadId,
                "workflow did not use verified snapshot on worker");
            lines.Add("verified_preapply=PASS writer_commits=20 coherent_snapshot=True backup_worker_thread=" + backupWorkerThread + " phases=" + string.Join(",", diagnosticPhases));
        }

        private static async Task CheckInvalidSnapshotAsync(SupplierExcelImportWorkflowService service, List<string> lines)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var row = MakeRows(1, "IMPORT-BAD-FK-")[0];
            var preview = await service.BuildSyncPreviewAsync(new[] { row });
            var before = Directory.GetFiles(AppPaths.BackupsDirectory).Length;
            using (var connection = factory.Open())
            {
                connection.Execute("CREATE TABLE completion_import_bad_fk(product_id INTEGER REFERENCES products(id)); PRAGMA foreign_keys=OFF; INSERT INTO completion_import_bad_fk(product_id) VALUES(-999);");
            }
            var rejected = false;
            try { await service.ApplyAsync(preview, false); }
            catch (InvalidDataException) { rejected = true; }
            finally { using (var connection = factory.Open()) connection.Execute("DROP TABLE completion_import_bad_fk;"); }
            Require(rejected && Directory.GetFiles(AppPaths.BackupsDirectory).Length == before, "invalid snapshot was published");
            Require(!await ProductExistsAsync(row.Barcode), "invalid backup allowed apply");
            lines.Add("invalid_snapshot=PASS FK validation rejected before publication and import");
        }

        private static async Task CheckBackupFaultsAsync(Func<bool> authorize, List<string> lines)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            foreach (var faultPoint in new[] { BackupFailurePoint.AfterSnapshotBeforeValidation, BackupFailurePoint.BeforePublish })
            {
                var row = MakeRows(1, "IMPORT-FAULT-" + faultPoint + "-")[0];
                var hooks = new BackupRestoreTestHooks { BackupFault = point => { if (point == faultPoint) throw new IOException("injected " + point); } };
                var service = new SupplierExcelImportWorkflowService(authorize, PosDbOptions.Default(), new SqliteOnlineBackup(factory, null, hooks));
                var preview = await service.BuildSyncPreviewAsync(new[] { row });
                var before = Directory.GetFiles(AppPaths.BackupsDirectory).Length;
                var rejected = false;
                try { await service.ApplyAsync(preview, false); }
                catch (IOException) { rejected = true; }
                Require(rejected && !await ProductExistsAsync(row.Barcode) && Directory.GetFiles(AppPaths.BackupsDirectory).Length == before,
                    "backup fault leaked publication/partial file/import: " + faultPoint);
                lines.Add("backup_fault_" + faultPoint + "=PASS no import, no published/partial file");
            }
        }

        private static async Task CheckCorrectionAndDoubleApplyAsync(SupplierExcelImportWorkflowService service, List<string> lines)
        {
            var row = MakeRows(1, "IMPORT-CORRECT-")[0];
            foreach (var value in new[] { "2147483648", "NaN", "Infinity", "1e999", "abc", "0.00000000000000000000000000011" })
            {
                row.PurchasePrice = value;
                var invalid = await service.BuildSyncPreviewAsync(new[] { row });
                Require(!invalid.CanApply && invalid.Errors.Count > 0 && row.PurchasePrice == value, "invalid cell was lost or accepted");
                Require(!SupplierImportIssueDisplayRow.FromError(invalid.Errors[0]).Message.Contains("supplier_import_"), "cell error was not localized");
            }
            row.PurchasePrice = "100";
            var preview = await service.BuildSyncPreviewAsync(new[] { row });
            var attempts = await Task.WhenAll(TryApplyAsync(service, preview), TryApplyAsync(service, preview));
            Require(attempts.Count(value => value) == 1, "double command did not have exactly one successful apply");
            using (var connection = new SqliteConnectionFactory(PosDbOptions.Default()).Open())
            {
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM products WHERE barcode=@barcode", new { barcode = row.Barcode }) == 1, "double command duplicated product");
                Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM product_price_history WHERE barcode=@barcode", new { barcode = row.Barcode }) == 2, "double command duplicated history");
            }
            lines.Add("cell_correction_double_apply=PASS one product and two initial price-history entries");
        }

        private static async Task<bool> TryApplyAsync(SupplierExcelImportWorkflowService service, SupplierImportSyncPreview preview)
        {
            try { return (await service.ApplyAsync(preview, false)).Success; }
            catch (SupplierExcelImportWorkflowException) { return false; }
        }

        private static async Task CheckConcurrentChangeAsync(Func<bool> authorize, List<string> lines)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var row = MakeRows(1, "IMPORT-SNAPSHOT-")[0];
            row.ProductName = "Requested update";
            var hooks = new BackupRestoreTestHooks
            {
                BackupFault = point =>
                {
                    if (point != BackupFailurePoint.BeforePublish) return;
                    using (var connection = factory.Open()) connection.Execute("UPDATE products SET name='Concurrent update' WHERE barcode=@barcode", new { barcode = row.Barcode });
                }
            };
            var service = new SupplierExcelImportWorkflowService(authorize, PosDbOptions.Default(), new SqliteOnlineBackup(factory, null, hooks));
            var preview = await service.BuildSyncPreviewAsync(new[] { row });
            Require(!await TryApplyAsync(service, preview), "concurrent DB change after backup bypassed transactional revalidation");
            using (var connection = factory.Open())
                Require(connection.ExecuteScalar<string>("SELECT name FROM products WHERE barcode=@barcode", new { barcode = row.Barcode }) == "Concurrent update", "stale apply overwrote concurrent product");
            var stale = await service.BuildSyncPreviewAsync(new[] { row });
            stale.FinalRows[0].RetailPrice = "999";
            Require(!await TryApplyAsync(service, stale), "edited preview fingerprint accepted");
            lines.Add("stale_preview_and_concurrent_product=PASS rejected before writes");
        }

        private static async Task CheckCancellationAndCaptureAsync(SupplierExcelImportWorkflowService service, List<string> lines)
        {
            var row = MakeRows(1, "IMPORT-CAPTURE-")[0];
            var task = service.BuildSyncPreviewAsync(new[] { row });
            row.ProductName = "Changed during worker";
            var captured = await task;
            Require(captured.NewProducts[0].ProductName == "Import 0", "preview observed mutable UI cells after dispatch");
            using (var cancellation = new CancellationTokenSource())
            using (await new CatalogShopTransitionBarrier(new SqliteConnectionFactory(PosDbOptions.Default())).EnterAsync())
            {
                var pending = service.BuildSyncPreviewAsync(MakeRows(20000, "IMPORT-CANCEL-"), cancellation.Token);
                cancellation.Cancel();
                var cancelled = false;
                try { await pending; }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled, "preview did not propagate cancellation");
            }
            lines.Add("immutable_input_and_cancellation=PASS");
        }

        private static async Task<bool> ProductExistsAsync(string barcode)
        {
            using (var connection = new SqliteConnectionFactory(PosDbOptions.Default()).Open())
                return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products WHERE barcode=@barcode", new { barcode }) > 0;
        }

        private static void Require(bool condition, string message) => FunctionalCompletionSmoke.Require(condition, message);

        private static async Task CheckWizardCancellationAsync(Func<bool> authorize, List<string> lines)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var backupEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseBackup = new ManualResetEventSlim())
            {
                var pauseBackup = true;
                var hooks = new BackupRestoreTestHooks { BackupFault = point =>
                {
                    if (!pauseBackup || point != BackupFailurePoint.AfterSnapshotBeforeValidation) return;
                    backupEntered.TrySetResult(true);
                    Require(releaseBackup.Wait(TimeSpan.FromSeconds(10)), "wizard cancellation did not release snapshot worker");
                } };
                var workflow = new SupplierExcelImportWorkflowService(authorize, PosDbOptions.Default(), new SqliteOnlineBackup(factory, null, hooks));
                var completion = new FixtureCompletion();
                var vm = new SupplierExcelImportViewModel(workflow, completionDialogService: completion) { StepIndex = 2 };
                var row = MakeRows(1, "IMPORT-WIZARD-CANCEL-")[0];
                vm.EditableRows.Add(row);
                var closed = 0;
                vm.RequestClose += _ => closed++;
                using (await new CatalogShopTransitionBarrier(factory).EnterAsync())
                {
                    vm.SyncPreviewCommand.Execute(null);
                    Require(vm.IsBusy && vm.CancelCommand.CanExecute(null), "wizard preview cancel unavailable");
                    vm.CancelCommand.Execute(null);
                    await WaitUntilIdleAsync(vm);
                }
                Require(vm.SyncPreview == null && vm.StepIndex == 2 && row.ProductName == "Import 0", "preview cancellation lost draft");
                vm.SyncPreviewCommand.Execute(null);
                await WaitUntilIdleAsync(vm);
                Require(vm.CanApply, "wizard preview could not retry after cancellation");
                vm.ApplyCommand.Execute(null);
                Require(await Task.WhenAny(backupEntered.Task, Task.Delay(10000)) == backupEntered.Task, "wizard did not reach preapply snapshot");
                try
                {
                    Require(vm.IsBusy && vm.CancelCommand.CanExecute(null), "wizard apply cancel unavailable");
                    vm.CancelCommand.Execute(null);
                }
                finally { releaseBackup.Set(); }
                await WaitUntilIdleAsync(vm);
                Require(vm.LastApplyResult == null && completion.Calls == 0 && closed == 0 && !await ProductExistsAsync(row.Barcode), "cancelled wizard applied or reported success");
                Require(vm.Status == PosLocalization.T("supplierExcelImport.statusOperationCancelled") && row.PurchasePrice == "100", "cancelled wizard lost contextual draft");
                pauseBackup = false;
                vm.ApplyCommand.Execute(null);
                await WaitUntilIdleAsync(vm);
                Require(vm.LastApplyResult?.Success == true && completion.Calls == 1 && closed == 1, "wizard apply could not retry after cancellation");
            }
            lines.Add("wizard_commands_cancellation_retry=PASS preview and preapply cancellation preserve draft and never report false success");
        }

        private static async Task WaitUntilIdleAsync(SupplierExcelImportViewModel vm)
        {
            var watch = Stopwatch.StartNew();
            while (vm.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
            Require(!vm.IsBusy, "wizard operation did not finish");
        }

        private sealed class FixtureCompletion : ISupplierExcelCompletionDialogService
        {
            internal int Calls;
            public void ShowCompletion(string title, string message) { Calls++; }
        }

        private static async Task CheckExactTextPersistenceAsync(SupplierExcelImportWorkflowService service, List<string> lines)
        {
            using (var connection = new SqliteConnectionFactory(PosDbOptions.Default()).Open())
            {
                connection.Execute("INSERT INTO suppliers(id,name,is_active) VALUES(7654,'Supplier exact',1); INSERT INTO categories(id,name,is_active) VALUES(7654,'Category exact',1);");
            }
            var rows = MakeRows(2, "IMPORT-TEXT-");
            rows[0].Barcode = "IMPORT-ID  AB";
            rows[1].Barcode = "IMPORT-ID AB";
            foreach (var row in rows)
            {
                row.ProductName = "Name  exact"; row.ItemNumber = "Item  exact"; row.SecondProductName = "Second  exact";
                row.Supplier = "Supplier  exact"; row.Category = "Category  exact";
            }
            var preview = await service.BuildSyncPreviewAsync(rows);
            var result = await service.ApplyAsync(preview, false, "text.xlsx");
            Require(result.Inserted == 2 && result.CatalogImportOutboxId > 0, "distinct text identities collapsed");
            using (var connection = new SqliteConnectionFactory(PosDbOptions.Default()).Open())
            {
                foreach (var row in rows)
                {
                    var actual = connection.QuerySingle<ProductDetailsRow>(@"SELECT p.barcode AS Barcode,p.name AS Name,m.article_code AS ArticleCode,m.name2 AS Name2,m.supplier_name AS SupplierName,m.category_name AS CategoryName FROM products p JOIN product_meta m ON m.barcode=p.barcode WHERE p.barcode=@barcode", new { barcode = row.Barcode });
                    Require(actual.Name == row.ProductName && actual.ArticleCode == row.ItemNumber && actual.Name2 == row.SecondProductName && actual.SupplierName == "Supplier exact" && actual.CategoryName == "Category exact", "DB text differs from canonical import payload");
                    Require(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM product_meta m JOIN suppliers s ON s.id=m.supplier_id JOIN categories c ON c.id=m.category_id WHERE m.barcode=@barcode AND s.id=7654 AND c.id=7654 AND s.name=m.supplier_name AND c.name=m.category_name", new { barcode = row.Barcode }) == 1, "reference deduplication differs from canonical labels");
                }
                var payload = connection.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id = result.CatalogImportOutboxId });
                foreach (var row in rows) Require(payload.Contains(row.Barcode), "outbox lost exact barcode text");
                foreach (var value in new[] { rows[0].ProductName, rows[0].ItemNumber, rows[0].SecondProductName, "Supplier exact", "Category exact" }) Require(payload.Contains(value), "outbox lost canonical field text");
            }
            lines.Add("text_identity_DB_payload=PASS distinct internal whitespace barcodes and exact canonical fields persisted");
        }

        private static async Task CheckWorkflowRollbackAsync(Func<bool> authorize, List<string> lines)
        {
            var row = MakeRows(1, "IMPORT-ROLLBACK-")[0];
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var inject = true;
            var hooks = new BackupRestoreTestHooks { BackupFault = point =>
            {
                if (point != BackupFailurePoint.BeforePublish || !inject) return;
                using (var connection = factory.Open())
                    connection.Execute("CREATE TRIGGER completion_import_fail_history BEFORE INSERT ON product_price_history WHEN NEW.barcode='IMPORT-ROLLBACK-0' BEGIN SELECT RAISE(ABORT,'injected workflow history failure'); END;");
            } };
            var service = new SupplierExcelImportWorkflowService(authorize, PosDbOptions.Default(), new SqliteOnlineBackup(factory, null, hooks));
            var preview = await service.BuildSyncPreviewAsync(new[] { row });
            try { Require(!await TryApplyAsync(service, preview), "injected history fault did not reject workflow"); }
            finally { using (var connection = factory.Open()) connection.Execute("DROP TRIGGER completion_import_fail_history;"); }
            Require(!await ProductExistsAsync(row.Barcode), "history fault leaked product write");
            inject = false;
            var result = await service.ApplyAsync(preview, false, "rollback-retry.xlsx");
            Require(result.Inserted == 1 && result.CatalogImportOutboxId > 0, "workflow could not retry after rollback");
            lines.Add("workflow_history_failure_rollback_retry=PASS");
        }
        internal static async Task RunBaselineAsync()
        {
            var lines = EnvironmentDetails();
            var service = new SupplierExcelImportWorkflowService(() => true);
            var rows = MakeRows(20000, "IMPORT-BASELINE-");
            await MeasureAsync(lines, "preview", () => service.BuildSyncPreviewAsync(rows));
            await MeasureAsync(lines, "apply_dryrun", () => service.ApplyAsync(rows, true));
            var row = MakeRows(1, "IMPORT-BOUND-")[0];
            row.PurchasePrice = "2147483648";
            try
            {
                var preview = await service.BuildSyncPreviewAsync(new[] { row });
                lines.Add("purchase_overflow=errors:" + preview.Errors.Count);
            }
            catch (Exception exception) { lines.Add("purchase_overflow=" + exception.GetType().Name); }
            await CheckBackupPathFailureAsync(service, lines);
            File.WriteAllLines(Path.Combine(AppPaths.DataDirectory, "import-baseline.txt"), lines);
        }

        internal static async Task RunBackupVerificationBaselineAsync()
        {
            SeedShop();
            var service = new SupplierExcelImportWorkflowService(() => true);
            var row = MakeRows(1, "IMPORT-UNVERIFIED-")[0];
            var preview = await service.BuildSyncPreviewAsync(new[] { row });
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            using (var connection = factory.Open())
            {
                connection.Execute("CREATE TABLE completion_import_bad_fk(product_id INTEGER REFERENCES products(id));");
                connection.Execute("PRAGMA foreign_keys=OFF;");
                connection.Execute("INSERT INTO completion_import_bad_fk(product_id) VALUES(-999);");
            }
            var lines = new List<string>();
            try
            {
                var result = await service.ApplyAsync(preview, false, "unverified-backup.xlsx");
                var validation = await new DbMaintenanceRepository(new SqliteConnectionFactory(PosDbOptions.ForPath(result.BackupPath))).ValidateAsync();
                lines.Add("apply_success=" + result.Success + " backup_valid=" + validation.IsValid + " foreign_keys=" + validation.ForeignKeyCheck);
            }
            catch (Exception exception)
            {
                lines.Add("apply_rejected=" + exception.GetType().Name + ":" + exception.Message);
                var backup = Directory.GetFiles(AppPaths.BackupsDirectory, "supplier_import_preapply_*.db").SingleOrDefault();
                if (backup != null)
                {
                    var validation = await new DbMaintenanceRepository(new SqliteConnectionFactory(PosDbOptions.ForPath(backup))).ValidateAsync();
                    lines.Add("published_backup_valid=" + validation.IsValid + " foreign_keys=" + validation.ForeignKeyCheck);
                }
            }
            finally
            {
                using (var connection = factory.Open()) connection.Execute("DROP TABLE completion_import_bad_fk;");
            }
            File.WriteAllLines(Path.Combine(AppPaths.DataDirectory, "import-backup-verification-baseline.txt"), lines);
        }

        private static List<string> EnvironmentDetails() => new List<string>
        {
            "runtime=" + Environment.Version,
            "os=" + Environment.OSVersion,
            "processors=" + Environment.ProcessorCount,
            "process_bits=" + (IntPtr.Size * 8),
            "dataset=20000 all-new rows, fixture SQLite",
            "heartbeat_interval_ms=10; regression_budget=worker must yield before DB/CPU and Dispatcher must tick during operation"
        };

        private static void SeedShop()
        {
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            using (var connection = new SqliteConnectionFactory(options).Open())
            {
                connection.Execute("INSERT OR REPLACE INTO app_settings(key,value) VALUES(@key,@value)",
                    new[]
                    {
                        new { key = OutboxShopBinding.OfficialShopIdKey, value = "import-completion-shop" },
                        new { key = OutboxShopBinding.OfficialShopCodeKey, value = "IMPORT-COMPLETION" }
                    });
            }
        }

        private static async Task<double> MeasureAsync<T>(List<string> lines, string name, Func<Task<T>> operation, bool requireWorker = false)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var ticks = 0;
            var maximumGap = 0.0;
            var watch = Stopwatch.StartNew();
            var lastTick = 0.0;
            var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, __) =>
            {
                ticks++;
                maximumGap = Math.Max(maximumGap, watch.Elapsed.TotalMilliseconds - lastTick);
                lastTick = watch.Elapsed.TotalMilliseconds;
            };
            timer.Start();
            var process = Process.GetCurrentProcess();
            var privateBytesBefore = process.PrivateMemorySize64;
            var managedBytesBefore = GC.GetTotalMemory(false);
            using (var metrics = SqliteWorkMetrics.Begin())
            try
            {
                var task = operation();
                var synchronousMilliseconds = watch.Elapsed.TotalMilliseconds;
                var completedOnEntry = task.IsCompleted;
                var result = await task.ConfigureAwait(true);
                var totalMilliseconds = watch.Elapsed.TotalMilliseconds;
                maximumGap = Math.Max(maximumGap, totalMilliseconds - lastTick);
                lines.Add(name + " sync_ms=" + synchronousMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
                    " total_ms=" + totalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) +
                    " completed_on_entry=" + completedOnEntry + " dispatcher_ticks=" + ticks +
                    " max_heartbeat_gap_ms=" + maximumGap.ToString("F1", CultureInfo.InvariantCulture) +
                    " caller_thread=" + Thread.CurrentThread.ManagedThreadId +
                    " managed_bytes_delta=" + (GC.GetTotalMemory(false) - managedBytesBefore) +
                    " private_bytes_delta=" + (Process.GetCurrentProcess().PrivateMemorySize64 - privateBytesBefore) +
                    " connections=" + metrics.Connections + " observed_product_commands=" + metrics.ProductCommands +
                    " product_commands_on_dispatcher=" + metrics.ProductCommandsOnCallingThread);
                if (result is SupplierExcelApplyUiResult applied && applied.SqlMetrics != null)
                    lines.Add(name + " apply_sql_commands=" + applied.SqlMetrics.TotalCommands + " existing_product_batches=" + applied.SqlMetrics.ExistingProductSelectCommands + " product_writes=" + applied.SqlMetrics.ProductWriteCommands + " history_rows=" + applied.SqlMetrics.PriceHistoryRows + " outbox_commands=" + applied.SqlMetrics.OutboxCommands);
                if (requireWorker) Require(!completedOnEntry && ticks > 0, name + " blocked Dispatcher instead of yielding to worker");
                return totalMilliseconds;
            }
            finally { timer.Stop(); }
        }

        private static async Task CheckBackupPathFailureAsync(SupplierExcelImportWorkflowService service, List<string> lines)
        {
            var row = MakeRows(1, "IMPORT-BACKUP-FAULT-")[0];
            var preview = await service.BuildSyncPreviewAsync(new[] { row });
            var directory = AppPaths.BackupsDirectory;
            var held = directory + ".completion-held";
            Directory.Move(directory, held);
            File.WriteAllText(directory, "fixture prevents backup publication");
            var rejected = false;
            try { await service.ApplyAsync(preview, false, "backup-fault.xlsx"); }
            catch (IOException) { rejected = true; }
            catch (UnauthorizedAccessException) { rejected = true; }
            finally
            {
                File.Delete(directory);
                Directory.Move(held, directory);
            }
            using (var connection = new SqliteConnectionFactory(PosDbOptions.Default()).Open())
            {
                var count = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM products WHERE barcode = @barcode", new { barcode = row.Barcode });
                FunctionalCompletionSmoke.Require(rejected && count == 0, "backup failure allowed product write");
            }
            lines.Add("backup_path_fault=rejected, imported_product_absent");
        }

        private static List<SupplierImportEditableRow> MakeRows(int count, string prefix) => Enumerable.Range(0, count)
            .Select(index => new SupplierImportEditableRow
            {
                RowNumber = index + 2, Barcode = prefix + index, ProductName = "Import " + index,
                PurchasePrice = "100", RetailPrice = "200", Quantity = "1.234",
                HasProductNameSource = true, HasPurchasePriceSource = true, HasRetailPriceSource = true, HasQuantitySource = true
            }).ToList();
    }
}
