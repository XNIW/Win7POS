using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HistoricalSalesFoundationRunner
    {
        internal static async Task<int> RunAsync(string[] args)
        {
            var data = Path.GetFullPath(Arg(args, "--data-dir"));
            var contextPath = Arg(args, "--context-file");
            var receiptPath = Arg(args, "--receipt");
            var context = HistoricalSalesQaReceipt.LoadContext(contextPath);
            if (args.Contains("--historical-sales-import-crash-probe", StringComparer.Ordinal)) return await CrashAsync(data, context, receiptPath);
            if (args.Contains("--historical-sales-import-crash-verify", StringComparer.Ordinal)) return await VerifyCrashAsync(args, data, contextPath, context, receiptPath);
            var id = Arg(args, "--foundation-case");
            if (args.Contains("--prepare-process-probes", StringComparer.Ordinal))
            {
                if (id != "HIST_B_PROCESS_ADMISSION") throw new InvalidOperationException("qa_history_prepare_case_invalid");
                PrepareProcessProbes(data, context, receiptPath); return 0;
            }
            var receipt = new HistoricalSalesQaReceipt(contextPath, "CrashProbe", receiptPath);
            await receipt.RunAsync(id, async test =>
            {
                switch (id)
                {
                    case "HIST_B_COMMIT_AUTHORITY": await CommitAuthorityAsync(data, test); break;
                    case "HIST_B_PROCESS_ADMISSION": await AdmissionAsync(data, context, test); break;
                    case "HIST_B_ENTRYPOINT_AUTHORITY": EntryPoints(data, context, test); break;
                    case "HIST_B_BACKUP_RESTORE": await BackupRestoreAsync(data, test); break;
                    default: throw new InvalidOperationException("qa_history_foundation_case_invalid");
                }
            });
            return receipt.Complete(partialCase: true);
        }

        private static async Task<int> CrashAsync(string data, HistoricalSalesQaContext context, string checkpointPath)
        {
            using (var fixture = await Fixture.CreateAsync(data))
            {
                using (var c = fixture.Factory.Open())
                    c.Execute("CREATE TABLE qa_history_crash_sentinel(nonce TEXT NOT NULL,baseline TEXT NOT NULL);INSERT INTO qa_history_crash_sentinel VALUES(@nonce,'prepared-v1')", new { nonce = context.RunNonce });
                var baselineRevision = (await fixture.Service.ReadDatasetAsync(CancellationToken.None)).DataRevision;
                SqliteConnectionFactory.ClearAllPools();
                var baselineHash = HashFile(fixture.Factory.DbPath);
                var proof = new CrashProof
                {
                    RunId = context.RunId, RunNonce = context.RunNonce, SourceFingerprintSha256 = context.SourceFingerprintSha256,
                    HarnessSha256 = context.HarnessSha256, FixtureSha256 = context.FixtureSha256, Phase = "prepared", Prepared = true,
                    DatabasePath = fixture.Factory.DbPath, FixtureToken = fixture.Owner.FixtureToken, DatabaseIdentityHash = PosDatabaseAuthorityQaProbe.DatabaseIdentityHash(fixture.Factory.DbPath),
                    BaselineHash = baselineHash, BaselineRevision = baselineRevision, WorkerPid = Process.GetCurrentProcess().Id, WorkerCreationTimeUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O")
                };
                HistoricalSalesQaReceipt.WriteJson(checkpointPath, proof);
                var plan = await fixture.PlanAsync(24000);
                fixture.Hooks.AtPhase = (phase, connection, transaction) =>
                {
                    if (phase != "before_commit") return;
                    proof.MainDuringHash = HashFile(fixture.Factory.DbPath);
                    proof.JournalReady = HotJournal(fixture.Factory.DbPath + "-journal");
                    proof.SpillObserved = proof.MainDuringHash != baselineHash;
                    if (!proof.JournalReady || !proof.SpillObserved) throw new InvalidOperationException("qa_history_spill_not_reached");
                    proof.Phase = "precommit_after_spill";
                    HistoricalSalesQaReceipt.WriteJson(checkpointPath, proof);
                    // Exact current process only; OS closes handles without SQLite unwind.
                    if (!TerminateProcess(GetCurrentProcess(), 79)) throw new InvalidOperationException("qa_history_self_termination_failed");
                    throw new InvalidOperationException("qa_history_termination_returned");
                };
                await fixture.ApplyAsync(plan);
            }
            return 1; // Reaching this return means the required crash did not occur.
        }

        private static async Task<int> VerifyCrashAsync(string[] args, string data, string contextPath, HistoricalSalesQaContext context, string receiptPath)
        {
            var checkpoint = Path.Combine(Arg(args, "--diagnostics-dir"), "historical-sales-crash-checkpoint.json");
            var proof = HistoricalSalesQaReceipt.ReadJson<CrashProof>(checkpoint);
            var receipt = new HistoricalSalesQaReceipt(contextPath, "CrashProbe", receiptPath);
            await receipt.RunAsync("HIST_CRASH_HOT_JOURNAL", test =>
            {
                var expectedRoot = data.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var exists = proof.DatabasePath != null && Path.GetFullPath(proof.DatabasePath).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(proof.DatabasePath);
                var identity = exists && PosDatabaseAuthorityQaProbe.DatabaseIdentityHash(proof.DatabasePath) == proof.DatabaseIdentityHash;
                test.Check("same_existing_database", exists && identity);
                if (!exists || !identity || proof.RunNonce != context.RunNonce || proof.RunId != context.RunId) throw new InvalidOperationException("qa_history_existing_database_mismatch");
                test.Check("reached_after_spill", proof.Phase == "precommit_after_spill" && proof.SpillObserved && proof.JournalReady && HotJournal(proof.DatabasePath + "-journal") && HashFile(proof.DatabasePath) == proof.MainDuringHash);
                test.Check("expected_abrupt_exit", Arg(args, "--worker-exit-code") == "79" && WorkerGone(proof));
                var options = PosDbOptions.ForPath(proof.DatabasePath);
                using (var owner = PosDatabaseAuthorityLease.AcquireSyntheticExisting(options, proof.FixtureToken))
                {
                    var factory = new SqliteConnectionFactory(options.WithAccessContext(owner.AccessContext));
                    long revision; long rows; string sentinel; bool integrity;
                    // Existing-only read/write connection permits technical hot-journal recovery.
                    // No initializer, migration, source recreation or seed is called here.
                    using (var c = factory.OpenExistingForRecovery())
                    {
                        sentinel = c.ExecuteScalar<string>("SELECT nonce FROM qa_history_crash_sentinel WHERE baseline='prepared-v1'");
                        revision = c.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1");
                        rows = c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions") + c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_batch") + c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation");
                        integrity = c.ExecuteScalar<string>("PRAGMA integrity_check") == "ok" && c.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0;
                    }
                    SqliteConnectionFactory.ClearAllPools();
                    test.Check("prepared_sentinel", proof.Prepared && sentinel == context.RunNonce);
                    test.Check("journal_recovery_completed", integrity && !File.Exists(proof.DatabasePath + "-journal"));
                    test.Check("atomic_data_and_revision", rows == 0 && revision == proof.BaselineRevision, 0L, rows);
                    test.Check("operational_invariants", HashFile(proof.DatabasePath) == proof.BaselineHash);
                }
                test.Check("owned_process_cleanup", WorkerGone(proof));
                return Task.CompletedTask;
            });
            return receipt.Complete(partialCase: true);
        }

        private static async Task CommitAuthorityAsync(string data, HistoricalSalesQaCase test)
        {
            foreach (var item in new[] { new[] { "apply_revoke_before_insert", "before_insert", "revoke" }, new[] { "apply_revoke_precommit", "before_commit", "revoke" }, new[] { "expiry_before_commit", "before_commit", "expiry" }, new[] { "operator_permission_changed", "before_commit", "permission" } })
            {
                using (var fixture = await Fixture.CreateAsync(data))
                {
                    var plan = await fixture.PlanAsync(); var before = fixture.State(); var reached = false;
                    fixture.Hooks.AtPhase = (phase, _, __) => { if (phase != item[1]) return; reached = true; if (item[2] == "expiry") fixture.Expire(); else if (item[2] == "permission") fixture.Operator.CurrentUser.PermissionCodes = Array.Empty<string>(); else PosOnlineSyncRevocationLatch.Revoke(fixture.Generation); };
                    var denied = false; try { await fixture.ApplyAsync(plan); } catch (InvalidOperationException) { denied = true; }
                    test.Check(item[0], reached && denied && before == fixture.State());
                }
            }
            using (var fixture = await Fixture.CreateAsync(data))
            {
                var applied = await fixture.ApplyAsync(await fixture.PlanAsync()); var before = fixture.State(); var reached = false;
                fixture.Hooks.AtPhase = (phase, _, __) => { if (phase == "before_undo_commit") { reached = true; PosOnlineSyncRevocationLatch.Revoke(fixture.Generation); } };
                var denied = false; try { await fixture.UndoAsync(applied); } catch (InvalidOperationException) { denied = true; }
                test.Check("undo_revoke_precommit", reached && denied && before == fixture.State());
            }
            using (var fixture = await Fixture.CreateAsync(data))
            {
                var plan = await fixture.PlanAsync(); var first = await fixture.ApplyAsync(plan); var replay = await fixture.ApplyAsync(plan);
                test.Check("linearized_commit_once", first.InsertedCount == 1 && replay.IsReplay && first.BatchId == replay.BatchId && fixture.Count("historical_transactions") == 1);
            }
        }

        private static async Task AdmissionAsync(string data, HistoricalSalesQaContext context, HistoricalSalesQaCase test)
        {
            var proof = ReadProcessProof(data, context);
            test.Check("real_holder_contender", proof.HolderReady && proof.HolderExitCode == 0 && proof.CleanupSucceeded && proof.Cases.Any(x => x.Overlapped && x.ExitCode == 78 && x.Stage == "denied_before_init"));
            using (var fixture = await Fixture.CreateAsync(data))
            {
                var busy = false; var clock = Stopwatch.StartNew();
                using (var held = fixture.Factory.Open())
                using (var tx = held.BeginTransaction())
                {
                    held.Execute("UPDATE historical_import_dataset SET data_revision=data_revision", transaction: tx);
                    await Task.Run(() => { try { using (var contender = fixture.Factory.Open()) contender.Execute("UPDATE historical_import_dataset SET data_revision=data_revision"); } catch (SqliteException error) when (error.SqliteErrorCode == 5 || error.SqliteErrorCode == 6) { busy = true; } });
                    tx.Rollback();
                }
                test.Check("sqlite_busy_bounded", busy && clock.Elapsed < TimeSpan.FromSeconds(7));
                using (var held = await PosOnlineSyncRevocationLatch.EnterAuthorizationUseAsync())
                using (var cancel = new CancellationTokenSource(100))
                {
                    var cancelled = false; try { using (await PosOnlineSyncRevocationLatch.EnterAuthorizationUseAsync(cancel.Token)) { } } catch (OperationCanceledException) { cancelled = true; }
                    test.Check("auth_cancel_no_orphan", cancelled);
                }
                var entered = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<bool>();
                var maintenance = SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () => { entered.SetResult(true); await release.Task; });
                await entered.Task;
                var maintenanceCancelled = false;
                using (var cancel = new CancellationTokenSource(100))
                { try { await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() => Task.CompletedTask, cancel.Token); } catch (OperationCanceledException) { maintenanceCancelled = true; } }
                release.SetResult(true); await maintenance;
                var reentered = false; await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() => { reentered = true; return Task.CompletedTask; });
                test.Check("maintenance_cancel_no_orphan", maintenanceCancelled && reentered);
                var workflow = new PosWorkflowService(fixture.Options, () => DateTimeOffset.UtcNow);
                var plan = await fixture.PlanAsync();
                bool workflowCancelled;
                using (await workflow.EnterHistoricalWorkflowGateForTestingAsync(CancellationToken.None))
                using (var cancel = new CancellationTokenSource(100))
                { workflowCancelled = false; try { await workflow.ApplyHistoricalSalesAsync(plan, fixture.Operator, cancel.Token); } catch (OperationCanceledException) { workflowCancelled = true; } }
                var sharedDeadline = await HistoricalSalesAdmissionDeadlineProbe.RunAsync(fixture);
                test.Check("workflow_cancel_no_orphan", workflowCancelled && !workflow.IsHistoricalMaintenanceActive && sharedDeadline);
                test.Check("commands_cart_restored", await HistoricalSalesCartMaintenanceProbe.RunAsync(fixture, workflow));
                var live = await fixture.ApplyAsync(await fixture.PlanAsync());
                test.Check("lock_order_no_deadlock", live.InsertedCount == 1 && fixture.Count("historical_transactions") == 1);
            }
        }

        private static void EntryPoints(string data, HistoricalSalesQaContext context, HistoricalSalesQaCase test)
        {
            var proof = ReadProcessProof(data, context);
            foreach (var pair in new[] { new[] { "wpf-normal", "wpf_normal_rejected_before_init" }, new[] { "wpf-safe", "wpf_safe_rejected_before_init" }, new[] { "cli", "cli_rejected_before_seed" }, new[] { "harness", "harness_rejected_before_init" } })
            {
                var row = proof.Cases.SingleOrDefault(x => x.Id == pair[0]);
                test.Check(pair[1], row != null && row.ExitCode == 78 && row.Stage == "denied_before_init" && !row.WriteStarted && row.Overlapped);
            }
            var distinct = proof.Cases.SingleOrDefault(x => x.Id == "distinct");
            test.Check("distinct_databases_admitted", distinct != null && distinct.ExitCode == 0 && distinct.Stage == "acquired_before_init" && !distinct.WriteStarted);
            var prepared = HistoricalSalesQaReceipt.ReadJson<ProcessPreparation>(Path.Combine(data, "process-probes-prepared.json"));
            var options = PosDbOptions.ForPath(prepared.DatabaseA);
            using (var owner = PosDatabaseAuthorityLease.AcquireOperational(options))
            {
                HistoricalSalesAuthorityVariants.Check(data, prepared.DatabaseA, context, test);
            }
            using (var restarted = PosDatabaseAuthorityLease.AcquireOperational(options))
                test.Check("restart_reacquires", restarted.AuthorityStamp != null && proof.CleanupSucceeded && HashFile(prepared.DatabaseA) == prepared.DatabaseAHash);
        }

        private static async Task BackupRestoreAsync(string data, HistoricalSalesQaCase test)
        {
            using (var fixture = await Fixture.CreateAsync(data))
            {
                var first = await fixture.ApplyAsync(await fixture.PlanAsync()); var snapshot = fixture.Backup();
                var valid = await new SqliteOnlineBackup(fixture.Factory).CreateVerifiedAsync(snapshot);
                test.Check("backup_validate_publish", valid.IsValid && File.Exists(snapshot));
                var auxDenied = false; try { using (var wrong = new SqliteConnectionFactory(PosDbOptions.ForPath(snapshot).WithAccessContext(fixture.Owner.AccessContext)).Open()) { } } catch (PosDatabaseAuthorityException) { auxDenied = true; }
                test.Check("aux_scope_reject", auxDenied);
                var stale = await fixture.PlanAsync(id: "stale"); var second = await fixture.ApplyAsync(await fixture.PlanAsync(id: "second"));
                var preRestore = fixture.Backup(); await new SqliteOnlineBackup(fixture.Factory).CreateVerifiedAsync(preRestore);
                var beforeStamp = fixture.Factory.AuthorityStamp;
                await new AtomicRestoreInstaller().InstallAsync(snapshot, fixture.Factory.DbPath, preRestore, async () =>
                {
                    await new CatalogShopStateRepository(fixture.Factory).ResetForRestoreReviewAsync(fixture.ShopId, fixture.ShopCode);
                    fixture.Factory.RefreshAuthorityStamp();
                });
                test.Check("candidate_swap_rebind", fixture.Count("historical_transactions") == 1 && beforeStamp.AuthorityInstanceEpoch != fixture.Factory.AuthorityStamp.AuthorityInstanceEpoch);
                var staleDenied = false; try { await fixture.ApplyAsync(stale); } catch (InvalidOperationException) { staleDenied = true; }
                test.Check("epoch_stales_plan", staleDenied && fixture.Factory.AuthorityStamp.RestoreEpoch > beforeStamp.RestoreEpoch);
                using (var c = fixture.Factory.Open()) test.Check("mapping_fk_preserved", c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_merchant_binding") == 1 && c.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_foreign_key_check") == 0);
                var duplicate = await fixture.ApplyAsync(await fixture.PlanAsync());
                test.Check("reimport_zero", duplicate.InsertedCount == 0 && duplicate.DuplicateCount == 1);
                var beforeRollback = fixture.State(); var rollbackSnapshot = fixture.Backup(); await new SqliteOnlineBackup(fixture.Factory).CreateVerifiedAsync(rollbackSnapshot);
                SqliteConnectionFactory.ClearAllPools();
                var beforeRollbackHash = HashFile(fixture.Factory.DbPath);
                var beforeRollbackIdentity = PosDatabaseAuthorityQaProbe.DatabaseIdentityHash(fixture.Factory.DbPath);
                var injectedFailure = new InvalidOperationException("qa_expected_postswap_failure");
                var failed = false; var reachedPostSwap = false; var candidateObserved = false;
                try
                {
                    await new AtomicRestoreInstaller().InstallAsync(snapshot, fixture.Factory.DbPath, rollbackSnapshot, () =>
                    {
                        reachedPostSwap = true;
                        candidateObserved = PosDatabaseAuthorityQaProbe.DatabaseIdentityHash(fixture.Factory.DbPath) != beforeRollbackIdentity &&
                            fixture.Count("historical_transactions") == 1 && fixture.Count("historical_import_batch") == 1;
                        throw injectedFailure;
                    });
                }
                catch (InvalidOperationException error) when (ReferenceEquals(error, injectedFailure)) { failed = true; }
                SqliteConnectionFactory.ClearAllPools();
                test.Check("rollback_restore", failed && reachedPostSwap && candidateObserved && beforeRollback == fixture.State() &&
                    PosDatabaseAuthorityQaProbe.DatabaseIdentityHash(fixture.Factory.DbPath) == beforeRollbackIdentity && HashFile(fixture.Factory.DbPath) == beforeRollbackHash);
                SqliteConnectionFactory.ClearAllPools();
                using (var exclusive = new FileStream(fixture.Factory.DbPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    test.Check("all_handles_released", exclusive.Length > 0);
            }
        }

        private static void PrepareProcessProbes(string data, HistoricalSalesQaContext context, string receiptPath)
        {
            var preparation = new ProcessPreparation { RunId = context.RunId, RunNonce = context.RunNonce, RootDirectory = data };
            foreach (var name in new[] { "a", "b" })
            {
                var options = PosDbOptions.ForPath(Path.Combine(data, "probe-" + name, "pos.db"));
                using (var owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(options))
                {
                    DbInitializer.EnsureCreated(options.WithAccessContext(owner.AccessContext));
                    SqliteConnectionFactory.ClearAllPools();
                    if (name == "a") { preparation.DatabaseA = options.DbPath; preparation.FixtureTokenA = owner.FixtureToken; preparation.DatabaseAHash = HashFile(options.DbPath); }
                    else { preparation.DatabaseB = options.DbPath; preparation.FixtureTokenB = owner.FixtureToken; }
                }
            }
            HistoricalSalesQaReceipt.WriteJson(Path.Combine(data, "process-probes-prepared.json"), preparation);
            HistoricalSalesQaReceipt.WriteJson(receiptPath, preparation); // Preparation is not a scenario receipt or PASS.
        }

        private static ProcessProof ReadProcessProof(string data, HistoricalSalesQaContext context)
        {
            var proof = HistoricalSalesQaReceipt.ReadJson<ProcessProof>(Path.Combine(data, "process-probes-proof.json"));
            if (proof.SchemaVersion != "win7pos-process-probes-proof-v1" || proof.RunId != context.RunId || proof.RunNonce != context.RunNonce) throw new InvalidOperationException("qa_history_process_proof_invalid");
            return proof;
        }
        private static bool Denied(Func<IDisposable> action) { try { using (action()) return false; } catch (PosDatabaseAuthorityException) { return true; } }
        private static bool WorkerGone(CrashProof proof)
        {
            try { using (var process = Process.GetProcessById(proof.WorkerPid)) return process.HasExited || process.StartTime.ToUniversalTime().ToString("O") != proof.WorkerCreationTimeUtc; }
            catch (ArgumentException) { return true; }
        }
        private static bool HotJournal(string path)
        {
            if (!File.Exists(path)) return false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length <= 512) return false; var header = new byte[28]; if (stream.Read(header, 0, header.Length) != header.Length) return false;
                return header.Take(8).SequenceEqual(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 });
            }
        }
        private static string HashFile(string path) { using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static string Arg(string[] args, string key) { var i = Array.IndexOf(args, key); if (i < 0 || i + 1 == args.Length) throw new ArgumentException("qa_history_argument_missing"); return args[i + 1]; }

        internal sealed class Fixture : IDisposable
        {
            internal string Root; internal PosDbOptions Options; internal PosDatabaseAuthorityLease Owner; internal SqliteConnectionFactory Factory;
            internal HistoricalSalesImportTestHooks Hooks = new HistoricalSalesImportTestHooks(); internal HistoricalSalesImportService Service; internal OperatorSession Operator;
            internal OnlineSyncGeneration Generation; internal string ShopId; internal string ShopCode; private PosTrustedDeviceStore _store; private long _expiryTicks;
            // All serial fixtures in this worker share ONE monotonic domain.
            // Clear intentionally retains the product's trusted high-water map;
            // generating a domain per DB would exhaust its bounded 16 slots.
            private const string FixtureClockDomain = "qa-history-shared-clock-v1";
            private static readonly DateTimeOffset FixtureWallAnchor = DateTimeOffset.UtcNow;
            private static long _fixtureClockTicks;
            private static long FixtureTicks() => Interlocked.Read(ref _fixtureClockTicks);
            private static DateTimeOffset FixtureNow() => FixtureWallAnchor.AddTicks(FixtureTicks());
            private static PosFirstLoginResponse FreshFixtureResponse(out PosAuthoritativeReceiptClock receiptClock)
            {
                var response = AuthorizationLeaseWpfSmoke.BuildResponse(true);
                var previousAnchor = DateTimeOffset.Parse(response.ServerTime, CultureInfo.InvariantCulture);
                var offlineLifetime = DateTimeOffset.Parse(response.EffectiveOfflineAuthorizationExpiresAt, CultureInfo.InvariantCulture) - previousAnchor;
                var sessionLifetime = DateTimeOffset.Parse(response.Session.ExpiresAt, CultureInfo.InvariantCulture) - previousAnchor;
                var ticks = FixtureTicks(); var now = FixtureWallAnchor.AddTicks(ticks);
                response.ServerTime = now.ToString("O", CultureInfo.InvariantCulture);
                response.EffectiveOfflineAuthorizationExpiresAt = now.Add(offlineLifetime).ToString("O", CultureInfo.InvariantCulture);
                response.Session.ExpiresAt = now.Add(sessionLifetime).ToString("O", CultureInfo.InvariantCulture);
                receiptClock = new PosAuthoritativeReceiptClock(now, ticks, TimeSpan.TicksPerSecond, FixtureClockDomain);
                return response;
            }
            internal OperatorSession Session => Operator;
            internal Win7POS.Core.Reports.SalesReportAuthorityStamp Authority => Factory.AuthorityStamp;
            internal static Task<Fixture> CreateAsync(string data, string name) => CreateAsync(data);
            internal static async Task<Fixture> CreateAsync(string data)
            {
                var f = new Fixture { Root = Path.Combine(data, "B." + Guid.NewGuid().ToString("N").Substring(0, 10)) }; Directory.CreateDirectory(f.Root);
                f.Options = PosDbOptions.ForPath(Path.Combine(f.Root, "pos.db")); f.Owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(f.Options); f.Options = f.Options.WithAccessContext(f.Owner.AccessContext); DbInitializer.EnsureCreated(f.Options);
                f.Factory = new SqliteConnectionFactory(f.Options); f.Service = new HistoricalSalesImportService(f.Factory, () => f.Factory.AuthorityStamp, f.Hooks);
                var response = FreshFixtureResponse(out var receiptClock); f.ShopId = response.Shop.ShopId; f.ShopCode = response.Shop.ShopCode;
                await new ShopOfficialSnapshotRepository(f.Factory).SaveAsync(new OfficialShopSnapshot { ShopId = f.ShopId, ShopCode = f.ShopCode, ShopName = "Synthetic QA", ShopStatus = "active", Source = "qa_harness" });
                var users = new UserRepository(f.Factory);
                await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput { Credential = "2468", CredentialVersion = response.Staff.CredentialVersion, DisplayName = response.Staff.DisplayName, RemoteRoleKey = response.Staff.RoleKey, RemoteShopId = f.ShopId, RemoteStaffId = response.Staff.StaffId, ShopCode = f.ShopCode, StaffCode = response.Staff.StaffCode });
                f._store = new PosTrustedDeviceStore(FixtureTicks, TimeSpan.TicksPerSecond, FixtureClockDomain); f._store.Clear(); f._store.SaveFirstLogin(response, "qa-history-session-" + Guid.NewGuid().ToString("N"), receiptClock);
                if (!f._store.TryRead(out var trusted) || !PosOnlineSyncSupervisorHost.TryCreateGeneration(trusted, out f.Generation)) throw new InvalidOperationException("qa_history_trust_setup_failed");
                f._expiryTicks = checked(receiptClock.MonotonicTimestamp + (DateTimeOffset.Parse(trusted.EffectiveOfflineAuthorizationExpiresAt, CultureInfo.InvariantCulture) - DateTimeOffset.Parse(trusted.LastOkServerAt, CultureInfo.InvariantCulture)).Ticks);
                f.Operator = new OperatorSession(users, new SecurityRepository(f.Factory), new PosOfflineAuthorizationLeaseGuard(f._store, FixtureNow, FixtureTicks, TimeSpan.TicksPerSecond));
                var username = await users.FindTrustedRemoteStaffUsernameAsync(f.ShopId, f.ShopCode, response.Staff.StaffId, response.Staff.StaffCode, response.Staff.CredentialVersion);
                if (await f.Operator.LoginAsync(username, "2468") != LoginResult.Success) throw new InvalidOperationException("qa_history_login_failed");
                f.Operator.CurrentUser.PermissionCodes = new[] { PermissionCodes.RegisterImportHistory, PermissionCodes.RegisterCancelHistory, PermissionCodes.RegisterViewAll, PermissionCodes.RegisterView, PermissionCodes.DailyCloseView, PermissionCodes.PosPay };
                File.WriteAllText(Path.Combine(f.Root, "source.bin"), "synthetic-history-source-v1", new UTF8Encoding(false)); return f;
            }
            internal async Task<HistoricalSalesImportPlan> PlanAsync(int count = 1, string id = "000123")
            {
                var source = Path.Combine(Root, "source.bin"); var merchant = new HistoricalSalesMerchant("SYNTHETIC", "QA", "42", "1", "test");
                var rows = new List<HistoricalSalesTransaction>(count); var start = new DateTime(2000, 1, 2, 11, 12, 13);
                for (var i = 0; i < count; i++) { var time = start.AddSeconds(i); var timestamp = new HistoricalSalesTimestamp(time.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture), "", null, time, null, null, null, HistoricalSalesModernTimeInterpretation.Unresolved, "synthetic-fixed-offset-v1", "synthetic-evidence", false); rows.Add(new HistoricalSalesTransaction(new HistoricalSalesSource("f1", "synthetic", i + 2), HistoricalSalesProfile.GetnetLegacy, "legacy-id-v1", count == 1 ? id : "synthetic-" + i.ToString("D12", CultureInfo.InvariantCulture), "", "", merchant, timestamp, HistoricalSalesMovementKind.Sale, "Abonado", "Abonado", 1000, 0, 0, 1000, HistoricalSalesCardSubtype.Debit, "Debito", "", "", "")); }
                var file = new HistoricalSalesImportFile("f1", source, HashFile(source), new FileInfo(source).Length, "synthetic", HistoricalSalesProfile.GetnetLegacy, count);
                var summary = new HistoricalSalesImportSummary(count, count, count, 0, 0, count * 1000L, count * 1000L, 0, count * 1000L, count, 0, 0, start, start.AddSeconds(count - 1));
                var analysis = new HistoricalSalesImportAnalysis(new[] { file }, rows, Array.Empty<HistoricalSalesRejectedRow>(), Array.Empty<HistoricalSalesImportDiagnostic>(), summary, false); var state = await Service.ReadDatasetAsync(CancellationToken.None);
                var key = HistoricalMerchantAssignment.MerchantKey(merchant).Substring(0, 16);
                return new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"), ShopId, state.DatasetId, state.DataRevision, Factory.AuthorityStamp.RestoreEpoch, analysis, new[] { new HistoricalMerchantAssignment(merchant, "synthetic-scope-" + key, "synthetic-binding-" + key, "synthetic-evidence") }, new DateTime(2001, 1, 1), "synthetic-start-proof");
            }
            internal async Task<HistoricalSalesImportPlan> PreparePlanAsync(HistoricalSalesImportAnalysis analysis)
            {
                var state = await Service.ReadDatasetAsync(CancellationToken.None);
                var assignments = analysis.AcceptedTransactions.Select(x => x.Merchant).GroupBy(HistoricalMerchantAssignment.MerchantKey).Select(g => new HistoricalMerchantAssignment(g.First(), "synthetic-scope-" + g.Key.Substring(0, 16), "synthetic-binding-" + g.Key.Substring(0, 16), "synthetic-evidence")).ToArray();
                return new HistoricalSalesImportPlan(Guid.NewGuid().ToString("N"), ShopId, state.DatasetId, state.DataRevision, Factory.AuthorityStamp.RestoreEpoch, analysis, assignments, new DateTime(2040, 1, 1), "synthetic-start-proof");
            }
            internal async Task<HistoricalSalesOperationReceipt> ApplyAsync(HistoricalSalesImportPlan plan)
            { using (var use = await Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply, plan.OperationId, plan.RequestFingerprint, plan.DatasetId, plan.RestoreEpoch, () => Factory.AccessContext.DemandPath(Factory.DbPath), CancellationToken.None)) return await Service.ApplyAsync(plan, use.Guard, Backup(), CancellationToken.None); }
            internal async Task UndoAsync(HistoricalSalesOperationReceipt applied)
            { var state = await Service.ReadDatasetAsync(CancellationToken.None); var op = Guid.NewGuid().ToString("N"); var epoch = Factory.AuthorityStamp.RestoreEpoch; var fingerprint = HistoricalSalesImportPlan.UndoFingerprint(op, ShopId, state.DatasetId, applied.BatchId, 1, epoch); using (var use = await Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Undo, op, fingerprint, state.DatasetId, epoch, () => Factory.AccessContext.DemandPath(Factory.DbPath), CancellationToken.None)) await Service.UndoAsync(applied.BatchId, 1, use.Guard, Backup(), CancellationToken.None); }
            internal void Expire()
            {
                // Advance the shared clock to this lease's exact boundary. Never
                // rewind another fixture's progress or erase any trusted state.
                var current = FixtureTicks();
                while (current < _expiryTicks)
                {
                    var observed = Interlocked.CompareExchange(ref _fixtureClockTicks, _expiryTicks, current);
                    if (observed == current) return;
                    current = observed;
                }
            }
            internal void ChangeShopForTesting()
            {
                var response = FreshFixtureResponse(out var receiptClock);
                response.Shop.ShopId = "synthetic-other-shop"; response.Shop.ShopCode = "SYNTHETIC-OTHER";
                _store.SaveFirstLogin(response, "qa-history-other-" + Guid.NewGuid().ToString("N"), receiptClock);
            }
            internal string Backup() => Path.Combine(Root, "backup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".db");
            internal long Count(string table) { if (!new[] { "historical_transactions", "historical_import_batch", "historical_import_operation" }.Contains(table)) throw new ArgumentException(); using (var c = Factory.Open()) return c.ExecuteScalar<long>("SELECT COUNT(*) FROM " + table); }
            internal string State() { using (var c = Factory.Open()) return string.Join("|", c.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_batch"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"), c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox")); }
            public void Dispose() { _store?.Clear(); SqliteConnectionFactory.ClearAllPools(); Owner?.Dispose(); } // Evidence remains owned and retained.
        }

        [DataContract] private sealed class CrashProof
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion = "win7pos-historical-sales-crash-checkpoint-v1";
            [DataMember(Name = "runId")] public string RunId; [DataMember(Name = "runNonce")] public string RunNonce;
            [DataMember(Name = "sourceFingerprintSha256")] public string SourceFingerprintSha256; [DataMember(Name = "harnessSha256")] public string HarnessSha256; [DataMember(Name = "fixtureSha256")] public string FixtureSha256;
            [DataMember(Name = "phase")] public string Phase; [DataMember(Name = "prepared")] public bool Prepared; [DataMember(Name = "journalReady")] public bool JournalReady; [DataMember(Name = "spillObserved")] public bool SpillObserved;
            [DataMember(Name = "databasePath")] public string DatabasePath; [DataMember(Name = "fixtureToken")] public string FixtureToken; [DataMember(Name = "databaseIdentityHash")] public string DatabaseIdentityHash;
            [DataMember(Name = "baselineHash")] public string BaselineHash; [DataMember(Name = "mainDuringHash")] public string MainDuringHash; [DataMember(Name = "baselineRevision")] public long BaselineRevision;
            [DataMember(Name = "workerPid")] public int WorkerPid; [DataMember(Name = "workerCreationTimeUtc")] public string WorkerCreationTimeUtc;
        }
        [DataContract] private sealed class ProcessPreparation
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion = "win7pos-process-probes-prepared-v1";
            [DataMember(Name = "runId")] public string RunId; [DataMember(Name = "runNonce")] public string RunNonce; [DataMember(Name = "rootDirectory")] public string RootDirectory;
            [DataMember(Name = "databaseA")] public string DatabaseA; [DataMember(Name = "fixtureTokenA")] public string FixtureTokenA; [DataMember(Name = "databaseAHash")] public string DatabaseAHash;
            [DataMember(Name = "databaseB")] public string DatabaseB; [DataMember(Name = "fixtureTokenB")] public string FixtureTokenB;
        }
        [DataContract] private sealed class ProcessProof
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } [DataMember(Name = "runId")] public string RunId { get; set; } [DataMember(Name = "runNonce")] public string RunNonce { get; set; }
            [DataMember(Name = "holderReady")] public bool HolderReady { get; set; } [DataMember(Name = "holderExitCode")] public int HolderExitCode { get; set; } [DataMember(Name = "cleanupSucceeded")] public bool CleanupSucceeded { get; set; }
            [DataMember(Name = "cases")] public List<ProcessCase> Cases { get; set; }
        }
        [DataContract] private sealed class ProcessCase
        {
            [DataMember(Name = "id")] public string Id { get; set; } [DataMember(Name = "exitCode")] public int ExitCode { get; set; } [DataMember(Name = "stage")] public string Stage { get; set; } [DataMember(Name = "writeStarted")] public bool WriteStarted { get; set; } [DataMember(Name = "overlapped")] public bool Overlapped { get; set; }
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    }
}
