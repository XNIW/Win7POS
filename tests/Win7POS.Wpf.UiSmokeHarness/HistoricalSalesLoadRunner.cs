using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Win7POS.Core.Import;
using Win7POS.Core.Reports;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Reports;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HistoricalSalesLoadRunner
    {
        internal static async Task<int> RunAsync(string[] args)
        {
            var receipt = new HistoricalSalesQaReceipt(Arg(args, "--context-file"), "Load", Arg(args, "--receipt"));
            await receipt.RunAsync("HIST_LOAD_50K", async test =>
            {
                var metrics = new Dictionary<string, double>(); receipt.Envelope.Metrics = metrics;
                var timings = new List<HistoricalSalesQaTiming>(); receipt.Envelope.Timings = timings;
                void Timing(string id, long start, long end) => timings.Add(new HistoricalSalesQaTiming { Id = id, StartTicks = start, EndTicks = end });
                // Fixture generation and native seeding precede the measured memory baseline.
                var setupStarted = Stopwatch.GetTimestamp();
                using var fixture = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(Arg(args, "--data-dir"), "load");
                var sourceStarted = Stopwatch.GetTimestamp();
                var sourceFiles = HistoricalSalesFixtureWriter.CreateLoad(Path.Combine(fixture.Root, "load-input"));
                var sourceEnded = Stopwatch.GetTimestamp(); Timing("source_generation", sourceStarted, sourceEnded); metrics["sourceGenerationMs"] = Milliseconds(sourceStarted, sourceEnded);
                var nativeStarted = Stopwatch.GetTimestamp();
                await HistoricalSalesMixedFixture.SeedAsync(fixture, 9000, 1000, CancellationToken.None);
                var nativeEnded = Stopwatch.GetTimestamp(); Timing("native_seed", nativeStarted, nativeEnded); metrics["nativeSeedMs"] = Milliseconds(nativeStarted, nativeEnded);
                var policy = HistoricalSalesMixedFixture.CreatePolicy();
                var from = new DateTime(2000, 1, 1); var to = new DateTime(2001, 1, 1);
                var coordinator = SalesReportSnapshotCoordinator.ForDatabase(fixture.Factory.DbPath);
                var query = new SalesReportQuery(fixture.Factory, () => fixture.Factory.AuthorityStamp, coordinator);
                var filter = new SalesReportFilter(fixture.ShopId, from, to, SalesReportOrigins.All, true, null, NativeSalesOwnershipRepository.PolicyHash, policy.Hash);
                var setupEnded = Stopwatch.GetTimestamp(); Timing("fixture_setup", setupStarted, setupEnded); metrics["fixtureSetupMs"] = Milliseconds(setupStarted, setupEnded);
                var baseline = Memory();
                using var sampler = new Sampler(Application.Current.Dispatcher, receipt.Context.MeasurementTargetProfile ?? "reference-windows", receipt.Context.MeasurementTargetIdentitySha256);
                sampler.Start();
                void Checkpoint() { sampler.CopyTo(receipt.Envelope); receipt.WriteCheckpoint(); }
                var started = Stopwatch.GetTimestamp();
                var analysis = await Task.Run(() => new HistoricalSalesImportAnalyzer().Analyze(sourceFiles, Options(), CancellationToken.None));
                var ended = Stopwatch.GetTimestamp(); Timing("analysis", started, ended); metrics["analysisMs"] = Milliseconds(started, ended);
                Checkpoint();
                test.Check("fixture_identity", receipt.Context.FixtureVersion == "historical-sales-getnet-v1" && sourceFiles.All(File.Exists) && analysis.Files.Count == 2);

                sampler.Phase = "plan"; started = Stopwatch.GetTimestamp();
                var plan = await Task.Run(() => fixture.PreparePlanAsync(analysis));
                ended = Stopwatch.GetTimestamp(); Timing("plan", started, ended); metrics["planMs"] = Milliseconds(started, ended);
                sampler.Phase = "hashing"; started = Stopwatch.GetTimestamp();
                using var verifiedSources = await Task.Run(() => fixture.Service.VerifySourcesAsync(plan, CancellationToken.None));
                ended = Stopwatch.GetTimestamp(); Timing("hashing", started, ended); metrics["hashingMs"] = Milliseconds(started, ended);
                sampler.Phase = "dedup"; started = Stopwatch.GetTimestamp();
                var preview = await Task.Run(() => fixture.Service.PreviewAsync(plan, CancellationToken.None));
                ended = Stopwatch.GetTimestamp(); Timing("dedup", started, ended); metrics["dedupMs"] = Milliseconds(started, ended);
                Checkpoint();

                long serviceStart = 0, beforeBackup = 0, afterBackup = 0, databaseStart = 0, mutationStart = 0, beforeCommit = 0, afterCommit = 0;
                fixture.Hooks.AtPhase = (phase, _, __) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (phase == "before_backup") { beforeBackup = now; sampler.Phase = "backup"; }
                    if (phase == "after_backup") afterBackup = now;
                    if (phase == "apply_database_started") { databaseStart = now; sampler.Phase = "apply"; }
                    if (phase == "mutation_started") mutationStart = now;
                    if (phase == "before_commit") { beforeCommit = now; sampler.Phase = "commit"; }
                    if (phase == "after_commit") { afterCommit = now; sampler.Phase = "apply_return"; }
                };
                sampler.Phase = "admission"; serviceStart = Stopwatch.GetTimestamp();
                var applied = await Task.Run(async () =>
                {
                    using var use = await fixture.Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply, plan.OperationId,
                        plan.RequestFingerprint, plan.DatasetId, plan.RestoreEpoch, () => fixture.Factory.AccessContext.DemandPath(fixture.Factory.DbPath), CancellationToken.None);
                    return await fixture.Service.ApplyAsync(plan, use.Guard, fixture.Backup(), CancellationToken.None, verifiedSources);
                });
                var serviceEnd = Stopwatch.GetTimestamp(); fixture.Hooks.AtPhase = null;
                if (!(serviceStart <= beforeBackup && beforeBackup <= afterBackup && afterBackup <= databaseStart && databaseStart <= mutationStart && mutationStart <= beforeCommit && beforeCommit <= afterCommit && afterCommit <= serviceEnd))
                    throw new InvalidOperationException("qa_history_phase_hook_missing");
                Timing("service", serviceStart, serviceEnd); Timing("admission", serviceStart, beforeBackup);
                Timing("backup", beforeBackup, afterBackup); Timing("backup_transition", afterBackup, databaseStart);
                Timing("apply", databaseStart, afterCommit); Timing("mutation", mutationStart, beforeCommit);
                Timing("commit", beforeCommit, afterCommit); Timing("apply_return", afterCommit, serviceEnd);
                metrics["serviceMs"] = Milliseconds(serviceStart, serviceEnd); metrics["admissionMs"] = Milliseconds(serviceStart, beforeBackup);
                metrics["backupMs"] = Milliseconds(beforeBackup, afterBackup); metrics["backupTransitionMs"] = Milliseconds(afterBackup, databaseStart);
                metrics["applyMs"] = Milliseconds(databaseStart, afterCommit); metrics["mutationMs"] = Milliseconds(mutationStart, beforeCommit);
                metrics["commitMs"] = Milliseconds(beforeCommit, afterCommit); metrics["applyReturnMs"] = Milliseconds(afterCommit, serviceEnd);
                // Independent literal oracle; these expectations are never derived from product output.
                var exact = !analysis.HasErrors && analysis.Summary.SourceRows == 50000 && analysis.Summary.SaleCount == 48900 &&
                    analysis.Summary.CancellationCount == 100 && analysis.Summary.RejectedCount == 1000 && analysis.Summary.AcceptedCount == 49000 &&
                    analysis.Summary.GrossClp == 48800000L && preview.NewCount == 49000 && applied.InsertedCount == 49000 && applied.DuplicateCount == 0 && applied.SignedAmountClp == 48800000L;
                test.Counter("rows", analysis.Summary.SourceRows); test.Counter("sales", analysis.Summary.SaleCount);
                test.Counter("cancellations", analysis.Summary.CancellationCount); test.Counter("rejected", analysis.Summary.RejectedCount);
                test.Counter("accepted", analysis.Summary.AcceptedCount); test.Counter("balanceClp", applied.SignedAmountClp);
                Checkpoint(); test.Check("exact_counters", exact);

                sampler.Phase = "ownership_cold"; started = Stopwatch.GetTimestamp();
                var ownership = new NativeSalesOwnershipRepository(fixture.Factory, coordinator, () => fixture.Factory.AuthorityStamp);
                foreach (var interval in policy.GetUtcIntervals(from, to))
                    while (!await Task.Run(() => ownership.RefreshRangeAsync(interval.FromUtcMs, interval.ToUtcMs, 100, CancellationToken.None))) { }
                ended = Stopwatch.GetTimestamp(); Timing("ownership_cold", started, ended); metrics["ownershipColdMs"] = Milliseconds(started, ended);
                sampler.Phase = "query_cold"; started = Stopwatch.GetTimestamp();
                var snapshot = await Task.Run(() => query.OpenAsync(filter, policy, 200, CancellationToken.None));
                ended = Stopwatch.GetTimestamp(); Timing("query_cold", started, ended); metrics["queryColdMs"] = Milliseconds(started, ended);
                var warm = new List<double>();
                for (var i = 0; i < 3; i++)
                {
                    sampler.Phase = "query_warm"; started = Stopwatch.GetTimestamp();
                    snapshot = await Task.Run(() => query.OpenAsync(filter, policy, 200, CancellationToken.None));
                    ended = Stopwatch.GetTimestamp(); Timing("query_warm", started, ended); warm.Add(Milliseconds(started, ended));
                }
                metrics["queryMs"] = warm.Max();
                var mixedExact = snapshot.Summary.BalanceClp == 56800000L && snapshot.Summary.HistoricalMovementCount == 49000 &&
                    snapshot.Summary.NativePositiveReceiptCount == 9000 && snapshot.Summary.NativeNegativeMovementCount == 1000 && snapshot.Summary.NativeAverageReceiptClp == 1000L;
                test.Counter("nativeRows", 10000); test.Counter("historicalRows", 49000); Checkpoint();

                // Cancel a running parse at an actual sampler observation, rather than a pre-cancelled call or a timed sleep.
                sampler.Phase = "analysis_cancel";
                using var analysisCancel = new CancellationTokenSource();
                long analysisCancelledAt = 0; var analysisRunning = 0; var analysisCancelled = false;
                sampler.OnObservation = phase =>
                {
                    if (phase == "analysis_cancel" && Volatile.Read(ref analysisRunning) == 1 && Interlocked.CompareExchange(ref analysisCancelledAt, Stopwatch.GetTimestamp(), 0) == 0) analysisCancel.Cancel();
                };
                try
                {
                    await Task.Run(() => { Volatile.Write(ref analysisRunning, 1); try { return new HistoricalSalesImportAnalyzer().Analyze(sourceFiles, Options(), analysisCancel.Token); } finally { Volatile.Write(ref analysisRunning, 0); } });
                }
                catch (OperationCanceledException) { analysisCancelled = true; }
                var analysisAcknowledgedAt = Stopwatch.GetTimestamp(); sampler.OnObservation = null;
                metrics["analysisCancelMs"] = analysisCancelledAt == 0 ? double.MaxValue : Milliseconds(analysisCancelledAt, analysisAcknowledgedAt);
                if (analysisCancelledAt != 0) Timing("analysis_cancel", analysisCancelledAt, analysisAcknowledgedAt);

                sampler.Phase = "rollback_prepare";
                var cancelledPlan = await Task.Run(() => fixture.PlanAsync(id: "cancel-load"));
                using var cancel = new CancellationTokenSource();
                long cancelledAt = 0, rollbackBackupStart = 0, rollbackBackupEnd = 0;
                fixture.Hooks.AtPhase = (phase, _, __) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (phase == "before_backup") { rollbackBackupStart = now; sampler.Phase = "rollback_backup"; }
                    if (phase == "after_backup") { rollbackBackupEnd = now; sampler.Phase = "rollback"; }
                    if (phase == "before_insert") { cancelledAt = now; cancel.Cancel(); }
                };
                var before = fixture.State(); var cancelled = false;
                await Task.Run(async () =>
                {
                    using var auth = await fixture.Operator.BeginHistoricalAuthorizationUseAsync(HistoricalImportOperationKind.Apply, cancelledPlan.OperationId,
                        cancelledPlan.RequestFingerprint, cancelledPlan.DatasetId, cancelledPlan.RestoreEpoch, () => fixture.Factory.AccessContext.DemandPath(fixture.Factory.DbPath), CancellationToken.None);
                    try { await fixture.Service.ApplyAsync(cancelledPlan, auth.Guard, fixture.Backup(), cancel.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                });
                var rollbackEnd = Stopwatch.GetTimestamp(); fixture.Hooks.AtPhase = null;
                metrics["cancelMs"] = cancelledAt == 0 ? double.MaxValue : Milliseconds(cancelledAt, rollbackEnd);
                metrics["rollbackMs"] = metrics["cancelMs"]; // Returned cancellation includes completion of the actual transaction rollback.
                if (cancelledAt != 0) Timing("rollback", cancelledAt, rollbackEnd);
                if (rollbackBackupStart != 0 && rollbackBackupEnd >= rollbackBackupStart) Timing("rollback_backup", rollbackBackupStart, rollbackBackupEnd);
                metrics["rollbackBackupMs"] = rollbackBackupStart == 0 ? double.MaxValue : Milliseconds(rollbackBackupStart, rollbackBackupEnd);
                var cancellationBudget = analysisCancelled && metrics["analysisCancelMs"] <= 1000 && cancelled && before == fixture.State() && metrics["cancelMs"] <= 1000 && metrics["rollbackMs"] <= 10000 && metrics["rollbackBackupMs"] <= 60000;
                Checkpoint(); test.Check("cancellation_budget", cancellationBudget);

                sampler.Phase = "maintenance_wait";
                var waiterCancelled = false; Task waiter = null;
                await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
                {
                    using var waiterCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                    using (ExecutionContext.SuppressFlow()) waiter = Task.Run(async () => { try { using var c = await fixture.Factory.OpenAsync(waiterCancellation.Token); } catch (OperationCanceledException) { waiterCancelled = true; } });
                    await waiter;
                }, CancellationToken.None);
                using (var connection = fixture.Factory.Open()) test.Check("maintenance_writer_exclusion", waiterCancelled && waiter.IsCompleted && connection.State == System.Data.ConnectionState.Open);
                sampler.Phase = "snapshot_mutation";
                await Task.Run(async () => await fixture.ApplyAsync(await fixture.PlanAsync(id: "new-after-snapshot")));
                var stale = false; try { await query.ValidateAsync(snapshot.Token, filter, policy, CancellationToken.None); } catch (ReportSnapshotStaleException) { stale = true; }
                Checkpoint(); test.Check("snapshot_invalidation", stale && !coordinator.TryUseSnapshot(snapshot.Token, () => { }));
                sampler.Phase = "drain"; await sampler.StopAsync(); sampler.CopyTo(receipt.Envelope);

                var samples = receipt.Envelope.Samples; var memorySamples = receipt.Envelope.MemorySamples;
                var probes = samples.GroupBy(s => s.ProbeId).Select(g => g.First()).ToArray();
                var latencies = probes.Where(s => s.CompletedTicks.HasValue).Select(s => Milliseconds(s.PostedTicks, s.CompletedTicks.Value)).OrderBy(x => x).ToArray();
                var p95 = latencies.Length == 0 ? double.MaxValue : latencies[(int)Math.Ceiling(latencies.Length * .95) - 1];
                var maximum = Math.Max(Math.Max(latencies.DefaultIfEmpty(0).Max(), samples.Select(s => s.PendingAgeMs).DefaultIfEmpty(0).Max()),
                    probes.Where(s => !s.CompletedTicks.HasValue).Select(s => Milliseconds(s.PostedTicks, sampler.EndTicks)).DefaultIfEmpty(0).Max());
                var peak = memorySamples.Select(s => s.PrivateBytes).DefaultIfEmpty(baseline).Max();
                metrics["privateBytesBefore"] = baseline; metrics["privateBytesAfter"] = Memory(); metrics["sampledPeakPrivateBytes"] = peak;
                metrics["dispatcherP95Ms"] = p95; metrics["dispatcherMaximumMs"] = maximum;
                metrics["expectedSamples"] = Sampler.Slots(sampler.StartTicks, sampler.EndTicks, 50); metrics["observedSamples"] = samples.Count;
                metrics["expectedMemorySamples"] = Sampler.Slots(sampler.StartTicks, sampler.EndTicks, 100); metrics["observedMemorySamples"] = memorySamples.Count;
                metrics["completedProbes"] = latencies.Length; metrics["pendingProbes"] = probes.Count(s => !s.CompletedTicks.HasValue);
                metrics["dropped"] = samples.Sum(s => s.Dropped); metrics["memoryDropped"] = memorySamples.Sum(s => s.Dropped);
                metrics["overflow"] = sampler.Overflow; metrics["suspended"] = samples.Sum(s => s.Suspended) + memorySamples.Sum(s => s.Suspended);
                metrics["sampleIntervalMs"] = 50; metrics["memoryIntervalMs"] = 100; metrics["stopwatchFrequency"] = Stopwatch.Frequency;
                var isWin7 = Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1;
                var win7 = isWin7 && receipt.Context.MeasurementTargetProfile == "win7-sp1" && receipt.Context.MeasurementTargetIdentitySha256 != null;
                var environment = !Environment.Is64BitProcess && Environment.OSVersion.Platform == PlatformID.Win32NT && sampler.EnvironmentValid &&
                    (!isWin7 || win7) && (receipt.Context.MeasurementTargetProfile != "win7-sp1" || isWin7);
                var memory = peak - baseline <= 150L * 1024 * 1024 && peak <= 700L * 1024 * 1024;
                var requiredPhasesObserved = receipt.Envelope.Phases.Where(p => p.Id == "analysis" || p.Id == "apply" || p.Id == "ownership_cold").All(p => p.ExpectedSlots > 0 && p.ExpectedMemorySlots > 0);
                var loadCompletedProbes = probes.Count(p => p.CompletedTicks.HasValue && p.Phase != "drain");
                var measurementComplete = sampler.Closed && requiredPhasesObserved && metrics["expectedSamples"] == samples.Count && metrics["expectedMemorySamples"] == memorySamples.Count && metrics["dropped"] == 0 && metrics["memoryDropped"] == 0 && sampler.Overflow == 0 && metrics["pendingProbes"] == 0 && loadCompletedProbes >= 20;
                var dispatcher = loadCompletedProbes >= 20 && p95 <= 100 && maximum <= 500;
                var phaseBudgets = metrics["analysisMs"] <= (win7 ? 60000 : 30000) && metrics["applyMs"] <= (win7 ? 30000 : 15000) && metrics["backupMs"] <= 60000;
                var warmBudget = metrics["queryMs"] <= (win7 ? 1000 : 500);
                var coldBudget = metrics["ownershipColdMs"] <= (win7 ? 60000 : 30000);
                test.Check("phase_measurements", phaseBudgets); test.Check("memory_budget", memory);
                test.Check("dispatcher_budget", dispatcher && measurementComplete); test.Check("mixed_query_budget", mixedExact && warmBudget);
                test.Check("ownership_cold_warm", coldBudget && snapshot.Coverage.CanPublish && warmBudget, win7 ? 60000 : 30000, metrics["ownershipColdMs"]);
                test.Check("environment_identity", environment);
                receipt.Envelope.MeasurementCompleted = measurementComplete; receipt.Envelope.EnvironmentValid = environment;
                receipt.Envelope.ThresholdsPassed = exact && phaseBudgets && memory && dispatcher && mixedExact && warmBudget && coldBudget && cancellationBudget;
            });
            return receipt.Complete();
        }

        private static HistoricalSalesImportOptions Options() => new HistoricalSalesImportOptions { LegacyClpConfirmed = true, ModernTimeInterpretation = HistoricalSalesModernTimeInterpretation.IsoUtc, TemporalPolicyVersion = "synthetic-fixed-offset-v1", TemporalEvidenceReference = "synthetic-independent-oracle-v1" };
        private static string Arg(IReadOnlyList<string> args, string name) { for (var i = 0; i + 1 < args.Count; i++) if (args[i] == name) return args[i + 1]; throw new ArgumentException("qa_history_argument_required"); }
        private static double Milliseconds(long start, long end) => (end - start) * 1000d / Stopwatch.Frequency;
        private static long Memory() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64; }

        private sealed class Sampler : IDisposable
        {
            private readonly Dispatcher _dispatcher;
            private readonly string _targetProfile, _targetIdentity;
            private readonly object _sync = new object();
            private readonly AutoResetEvent _wake = new AutoResetEvent(false);
            private readonly ManualResetEventSlim _started = new ManualResetEventSlim();
            private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly List<HistoricalSalesQaSample> _samples = new List<HistoricalSalesQaSample>();
            private readonly List<HistoricalSalesQaMemorySample> _memory = new List<HistoricalSalesQaMemorySample>();
            private readonly List<HistoricalSalesQaPhase> _phases = new List<HistoricalSalesQaPhase>();
            private readonly Dictionary<int, long?> _probes = new Dictionary<int, long?>();
            private Thread _thread;
            private int? _pending;
            private long _pendingPosted, _stopAt;
            private bool _stop, _closed, _environment = true;
            private int _overflow;
            private Action<string> _onObservation;
            internal Action<string> OnObservation { set { lock (_sync) _onObservation = value; } }
            internal long StartTicks { get; private set; }
            internal long EndTicks { get; private set; }
            internal bool Closed { get { lock (_sync) return _closed; } }
            internal bool EnvironmentValid { get { lock (_sync) return _environment; } }
            internal int Overflow { get { lock (_sync) return _overflow; } }
            internal string Phase
            {
                set
                {
                    lock (_sync)
                    {
                        if (_closed) throw new InvalidOperationException("qa_history_measurement_closed");
                        var now = Stopwatch.GetTimestamp();
                        _phases[_phases.Count - 1].EndTicks = now;
                        _phases.Add(new HistoricalSalesQaPhase { Sequence = _phases.Count, Id = value, StartTicks = now });
                    }
                }
            }
            internal Sampler(Dispatcher dispatcher, string targetProfile, string targetIdentity) { _dispatcher = dispatcher; _targetProfile = targetProfile; _targetIdentity = targetIdentity; }
            internal static int Slots(long from, long to, int intervalMs) => checked((int)Math.Ceiling((to - from) * 1000d / (Stopwatch.Frequency * (double)intervalMs)));
            private long Due(long index, int intervalMs) => StartTicks + index * Stopwatch.Frequency * intervalMs / 1000;
            internal void Start()
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "HISTORY-MEASUREMENT-v2" };
                _thread.Start(); _started.Wait();
                if (_done.Task.IsFaulted) _done.Task.GetAwaiter().GetResult();
            }
            private void Run()
            {
                try
                {
                    lock (_sync)
                    {
                        StartTicks = Stopwatch.GetTimestamp();
                        _phases.Add(new HistoricalSalesQaPhase { Sequence = 0, Id = "analysis", StartTicks = StartTicks });
                        _environment = QualifiedDesktop() && ReadAwake(out _);
                    }
                    var wallStart = DateTimeOffset.UtcNow; var awakeValid = ReadAwake(out var awakeStart);
                    _started.Set(); long last = -1, memoryLast = -1;
                    while (true)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var slot = (now - StartTicks) * 1000 / (Stopwatch.Frequency * 50);
                        var memorySlot = (now - StartTicks) * 1000 / (Stopwatch.Frequency * 100);
                        var suspended = !awakeValid || !ReadAwake(out var awakeNow) ||
                            Math.Abs((awakeNow - awakeStart) / 10000d - Milliseconds(StartTicks, now)) > 250 ||
                            Math.Abs((DateTimeOffset.UtcNow - wallStart).TotalMilliseconds - Milliseconds(StartTicks, now)) > 250;
                        Action<string> observed = null; string observedPhase = null; int? post = null;
                        lock (_sync)
                        {
                            // Decide from a fresh observation while completion callbacks share this lock.
                            // A callback may have completed after the earlier clock/awake checks.
                            now = Stopwatch.GetTimestamp();
                            slot = (now - StartTicks) * 1000 / (Stopwatch.Frequency * 50);
                            memorySlot = (now - StartTicks) * 1000 / (Stopwatch.Frequency * 100);
                            if (suspended) _environment = false;
                            if (slot > last)
                            {
                                var due = Due(slot, 50); var phase = PhaseAt(due);
                                if (!_pending.HasValue) { _pending = _probes.Count; _pendingPosted = now; _probes.Add(_pending.Value, null); post = _pending; }
                                var sample = new HistoricalSalesQaSample { Sequence = checked((int)slot), Phase = phase.Id, PhaseSequence = phase.Sequence, DueTicks = due, MonotonicTicks = now,
                                    ProbeId = _pending.Value, PostedTicks = _pendingPosted, PendingAgeMs = Milliseconds(_pendingPosted, now), Dropped = checked((int)(slot - last - 1)), Suspended = suspended ? 1 : 0 };
                                if (_samples.Count < 4096) _samples.Add(sample); else _overflow++;
                                last = slot; observed = _onObservation; observedPhase = phase.Id;
                            }
                        }
                        if (post.HasValue)
                        {
                            var id = post.Value;
                            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { lock (_sync) { if (_closed) return; _probes[id] = Stopwatch.GetTimestamp(); if (_pending == id) _pending = null; _wake.Set(); } }));
                        }
                        if (memorySlot > memoryLast)
                        {
                            // Process.Refresh is independent IO; it never holds the lock needed by a UI callback.
                            var bytes = Memory(); var qualified = QualifiedDesktop(); var observedAt = Stopwatch.GetTimestamp();
                            lock (_sync)
                            {
                                _environment &= qualified;
                                var observedSlot = (observedAt - StartTicks) * 1000 / (Stopwatch.Frequency * 100);
                                var due = Due(observedSlot, 100); var phase = PhaseAt(due);
                                var sample = new HistoricalSalesQaMemorySample { Sequence = checked((int)observedSlot), Phase = phase.Id, PhaseSequence = phase.Sequence, DueTicks = due,
                                    MonotonicTicks = observedAt, PrivateBytes = bytes, Dropped = checked((int)(observedSlot - memoryLast - 1)), Suspended = suspended ? 1 : 0 };
                                if (_memory.Count < 2048) _memory.Add(sample); else _overflow++;
                                memoryLast = observedSlot;
                            }
                        }
                        observed?.Invoke(observedPhase);
                        lock (_sync)
                        {
                            if (_stop && (!_pending.HasValue || Milliseconds(_stopAt, now) >= 500))
                            {
                                EndTicks = Stopwatch.GetTimestamp(); _phases[_phases.Count - 1].EndTicks = EndTicks;
                                _environment &= QualifiedDesktop(); _closed = true; break;
                            }
                        }
                        var next = Math.Min(Due(last + 1, 50), Due(memoryLast + 1, 100));
                        var remaining = Milliseconds(Stopwatch.GetTimestamp(), next);
                        if (remaining > 0) _wake.WaitOne((int)Math.Min(50, Math.Ceiling(remaining)));
                    }
                    _done.TrySetResult(true);
                }
                catch (Exception error) { lock (_sync) { _environment = false; _overflow++; } _started.Set(); _done.TrySetException(error); }
            }
            private HistoricalSalesQaPhase PhaseAt(long due) => _phases.Last(p => p.StartTicks <= due);
            internal async Task StopAsync()
            {
                lock (_sync) { if (!_stop) { _stop = true; _stopAt = Stopwatch.GetTimestamp(); } }
                _wake.Set(); await _done.Task;
            }
            internal void CopyTo(HistoricalSalesQaEnvelope envelope)
            {
                lock (_sync)
                {
                    var end = _closed ? EndTicks : Stopwatch.GetTimestamp();
                    envelope.Measurement = new HistoricalSalesQaMeasurement { StartTicks = StartTicks, EndTicks = _closed ? EndTicks : 0, TargetProfile = _targetProfile, TargetIdentitySha256 = _targetIdentity };
                    envelope.Samples = _samples.Select(s => new HistoricalSalesQaSample { Sequence = s.Sequence, Phase = s.Phase, PhaseSequence = s.PhaseSequence, MonotonicTicks = s.MonotonicTicks,
                        DueTicks = s.DueTicks, ProbeId = s.ProbeId, PostedTicks = s.PostedTicks, CompletedTicks = _probes[s.ProbeId], PendingAgeMs = s.PendingAgeMs, Dropped = s.Dropped, Suspended = s.Suspended }).ToList();
                    envelope.MemorySamples = _memory.Select(s => new HistoricalSalesQaMemorySample { Sequence = s.Sequence, Phase = s.Phase, PhaseSequence = s.PhaseSequence, MonotonicTicks = s.MonotonicTicks,
                        DueTicks = s.DueTicks, PrivateBytes = s.PrivateBytes, Dropped = s.Dropped, Suspended = s.Suspended }).ToList();
                    envelope.Phases = _phases.Select(p => new HistoricalSalesQaPhase { Sequence = p.Sequence, Id = p.Id, StartTicks = p.StartTicks, EndTicks = p.EndTicks == 0 ? end : p.EndTicks,
                        ExpectedSlots = Slots(StartTicks, p.EndTicks == 0 ? end : p.EndTicks, 50) - Slots(StartTicks, p.StartTicks, 50),
                        ExpectedMemorySlots = Slots(StartTicks, p.EndTicks == 0 ? end : p.EndTicks, 100) - Slots(StartTicks, p.StartTicks, 100) }).ToList();
                    envelope.MeasurementCompleted = false;
                }
            }
            public void Dispose()
            {
                lock (_sync) { if (!_stop) { _stop = true; _stopAt = Stopwatch.GetTimestamp(); } }
                _wake.Set();
                // An exceptional caller still leaves a bounded background observer, never an abandoned Dispatcher waiter.
                // Completion continuations own the events until the thread closes; late callbacks see _closed first.
                _done.Task.ContinueWith(t => { var ignored = t.Exception; lock (_sync) { _closed = true; _wake.Dispose(); _started.Dispose(); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            private static bool ReadAwake(out ulong ticks)
            {
                ticks = 0; return Environment.OSVersion.Platform == PlatformID.Win32NT && QueryUnbiasedInterruptTime(out ticks);
            }
            private static bool QualifiedDesktop()
            {
                if (!Environment.UserInteractive || Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
                using (var process = Process.GetCurrentProcess()) if (process.SessionId == 0) return false;
                var desktop = OpenInputDesktop(0, false, 1); if (desktop == IntPtr.Zero) return false;
                try { var name = new StringBuilder(256); return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase); }
                finally { CloseDesktop(desktop); }
            }
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryUnbiasedInterruptTime(out ulong ticks);
            [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
        }
    }
}
