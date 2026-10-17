using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Diagnostic only: the seven existing drains in the editor indicator lifecycle.
    internal static class PerfPhaseDrainObservation
    {
        [ThreadStatic] private static Capture _next;
        private static Receipt _pending;
        private static int _arms;
        private static int _claims;
        private static int _duplicates;
        private static int _receipts, _errorFlags;
        private static bool _lifecycleCompleted;
        private static readonly object ReceiptGate = new object();
        private static readonly bool Enabled =
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_WORKFLOW"), "wpf-build", StringComparison.Ordinal) &&
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME"), "workflow_dispatch", StringComparison.Ordinal) &&
            string.Equals(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), "1", StringComparison.Ordinal);
        private const int InvalidArm = 1, InvalidCapture = 2, InvalidClaim = 4, DuplicateReceipt = 8;
        internal const string FileName = "perf-phase-drain-observation-v2.json.txt";
        internal const string StatusFileName = "perf-phase-drain-observation-v2.status.txt";

        internal enum EditorDrain
        {
            Initial = 1, ProgressVisible, AncestorHidden, AncestorVisible,
            ProgressHidden, ProgressVisibleAgain, EditorClosed
        }

        internal static Capture Arm(Window editor, EditorDrain phase)
        {
            if (!Enabled) return null;
            var started = Stopwatch.GetTimestamp();
            var capture = new Capture(editor.Dispatcher, Application.Current?.Dispatcher);
            capture.Data.ArmStartedTicks = started;
            capture.Data.ObservationIndex = Interlocked.Increment(ref _arms);
            capture.Data.PhaseIndex = (int)phase;
            capture.Data.Phase = phase.ToString();
            if (capture.Data.ObservationIndex != (int)phase || (int)phase < 1 || (int)phase > 7)
                capture.Data.ErrorFlags |= InvalidArm;
            if (_next != null)
            {
                _next.Data.ErrorFlags |= InvalidArm;
                capture.Data.ErrorFlags |= InvalidArm;
            }
            else _next = capture;
            capture.Data.ArmCompletedTicks = Stopwatch.GetTimestamp();
            return capture;
        }

        internal static void CompleteEditorLifecycle()
        {
            if (!Enabled) return;
            lock (ReceiptGate) _lifecycleCompleted = true;
        }

        internal static Capture Claim()
        {
            var capture = _next;
            _next = null;
            if (capture == null) return null;
            if (capture.Data.ErrorFlags != 0)
            {
                capture.ReleaseReferences();
                return null;
            }
            capture.Claimed = true;
            Interlocked.Increment(ref _claims);
            return capture;
        }

        internal sealed class Capture : IDisposable
        {
            internal readonly Receipt Data = new Receipt();
            internal bool Claimed;
            private Dispatcher _selected, _editor, _application;
            private SynchronizationContext _entryContext;
            private int _disposed;

            internal Capture(Dispatcher editor, Dispatcher application)
            {
                _editor = editor;
                _application = application;
            }

            internal void Enter(Dispatcher selected)
            {
                Data.EntryStartedTicks = Stopwatch.GetTimestamp();
                try
                {
                    _selected = selected;
                    _entryContext = SynchronizationContext.Current;
                    Snapshot(Data.Entry, _entryContext);
                    Data.EntryCaptured = true;
                }
                catch { Data.ErrorFlags |= InvalidCapture; }
                Data.EntryCompletedTicks = Stopwatch.GetTimestamp();
            }

            internal void Resumed(Task winner, Task probe, Task delay, DispatcherOperationStatus decision,
                long beforeDelay, long afterDelay, long beforeDecision, long afterDecision)
            {
                try
                {
                    Data.BeforeDelayTicks = beforeDelay;
                    Data.AfterDelayTicks = afterDelay;
                    Data.BeforeDecisionTicks = beforeDecision;
                    Data.AfterDecisionTicks = afterDecision;
                    Data.Winner = ReferenceEquals(winner, probe) ? "probe" : ReferenceEquals(winner, delay) ? "delay" : "other_invalid";
                    Data.OperationStatusAtDecision = (int)decision;
                    Data.ShouldFail = decision != DispatcherOperationStatus.Completed;
                    Data.ProbeTaskStatus = (int)probe.Status;
                    Data.DelayTaskStatus = (int)delay.Status;
                    var context = SynchronizationContext.Current;
                    Data.SameContextAsEntry = ReferenceEquals(context, _entryContext);
                    Snapshot(Data.Continuation, context);
                    Data.ContinuationCaptured = true;
                }
                catch { Data.ErrorFlags |= InvalidCapture; }
                finally { ReleaseReferences(); }
                Data.CaptureCompletedTicks = Stopwatch.GetTimestamp();
            }

            internal void AbortReturned(bool result, long ticks)
            {
                Data.AbortAttempted = true;
                Data.AbortReturned = result;
                Data.AfterAbortTicks = ticks;
            }

            private void Snapshot(DispatcherSnapshot snapshot, SynchronizationContext context)
            {
                var thread = Thread.CurrentThread;
                // FromThread observes without creating a dispatcher on a foreign continuation.
                var current = Dispatcher.FromThread(thread);
                snapshot.ManagedThreadId = thread.ManagedThreadId;
                snapshot.CurrentDispatcherPresent = current != null;
                snapshot.CurrentEqualsSelected = ReferenceEquals(current, _selected);
                snapshot.CurrentEqualsEditor = ReferenceEquals(current, _editor);
                snapshot.CurrentEqualsApplication = ReferenceEquals(current, _application);
                snapshot.SelectedEqualsEditor = ReferenceEquals(_selected, _editor);
                snapshot.SelectedEqualsApplication = ReferenceEquals(_selected, _application);
                snapshot.EditorEqualsApplication = ReferenceEquals(_editor, _application);
                snapshot.SelectedManagedThreadId = _selected.Thread.ManagedThreadId;
                snapshot.EditorManagedThreadId = _editor.Thread.ManagedThreadId;
                snapshot.ApplicationManagedThreadId = _application?.Thread.ManagedThreadId;
                snapshot.SelectedCheckAccess = _selected.CheckAccess();
                snapshot.EditorCheckAccess = _editor.CheckAccess();
                snapshot.ApplicationCheckAccess = _application?.CheckAccess();
                snapshot.SelectedShutdownStarted = _selected.HasShutdownStarted;
                snapshot.SelectedShutdownFinished = _selected.HasShutdownFinished;
                snapshot.EditorShutdownStarted = _editor.HasShutdownStarted;
                snapshot.EditorShutdownFinished = _editor.HasShutdownFinished;
                snapshot.ApplicationShutdownStarted = _application?.HasShutdownStarted;
                snapshot.ApplicationShutdownFinished = _application?.HasShutdownFinished;
                snapshot.ContextType = context?.GetType();
                snapshot.ContextPresent = context != null;
            }

            internal void ReleaseReferences()
            {
                _entryContext = null;
                _selected = _editor = _application = null;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    Interlocked.Increment(ref _duplicates);
                    return;
                }
                if (ReferenceEquals(_next, this)) _next = null;
                if (!Claimed || !Data.EntryCaptured || !Data.ContinuationCaptured) Data.ErrorFlags |= InvalidClaim;
                ReleaseReferences();
                lock (ReceiptGate)
                {
                    _receipts++;
                    if (Data.ObservationIndex != _receipts || Data.PhaseIndex != _receipts)
                        Data.ErrorFlags |= InvalidClaim;
                    _errorFlags |= Data.ErrorFlags;
                    // Retain the first original failing predicate, even when an earlier
                    // capture was invalid. Otherwise retain the last reached drain.
                    var discarded = Data;
                    if (_pending == null || !_pending.ShouldFail)
                    {
                        discarded = _pending;
                        _pending = Data;
                    }
                    if (discarded != null)
                    {
                        discarded.Entry.ContextType = null;
                        discarded.Continuation.ContextType = null;
                    }
                }
            }
        }

        [DataContract]
        internal sealed class DispatcherSnapshot
        {
            [DataMember] public int ManagedThreadId;
            [DataMember] public bool CurrentDispatcherPresent, CurrentEqualsSelected;
            [DataMember] public bool CurrentEqualsEditor, CurrentEqualsApplication;
            [DataMember] public bool SelectedEqualsEditor, SelectedEqualsApplication, EditorEqualsApplication;
            [DataMember] public int SelectedManagedThreadId, EditorManagedThreadId;
            [DataMember] public int? ApplicationManagedThreadId;
            [DataMember] public bool SelectedCheckAccess, EditorCheckAccess;
            [DataMember] public bool? ApplicationCheckAccess;
            [DataMember] public bool SelectedShutdownStarted, SelectedShutdownFinished;
            [DataMember] public bool EditorShutdownStarted, EditorShutdownFinished;
            [DataMember] public bool? ApplicationShutdownStarted, ApplicationShutdownFinished;
            [DataMember] public bool ContextPresent;
            [DataMember] public string ContextTypeName;
            [DataMember] public string ContextPriority = "NOT_OBSERVED";
            internal Type ContextType;

            internal void DescribeAndReleaseType()
            {
                var type = ContextType;
                ContextType = null;
                ContextTypeName = type == null ? "null" :
                    type.Assembly == typeof(SynchronizationContext).Assembly || type.Assembly == typeof(DispatcherSynchronizationContext).Assembly
                    ? type.FullName : "NONFRAMEWORK_TYPE_OMITTED";
            }
        }

        [DataContract]
        internal sealed class Receipt
        {
            [DataMember] public int SchemaVersion = 2;
            [DataMember] public string Phase;
            [DataMember] public int ObservationIndex, PhaseIndex, ReceiptCount, AggregateErrorFlags;
            [DataMember] public string LifecycleStatus;
            [DataMember] public string Activation = "wpf-build/workflow_dispatch/attempt-1";
            [DataMember] public string ProposalBaseRevision = "32618249fc01f29e86c6774d2b2c9262a90f226f";
            [DataMember] public string ExecutedSourceRevision = "REQUIRES_EXTERNAL_BUILD_MANIFEST";
            [DataMember] public string Mode = "--functional-completion-smoke";
            [DataMember] public string Scenario = "PERF_visual_lifetime_and_public_commands";
            [DataMember] public string GitHubEventSha;
            [DataMember] public SourceDigest[] SourceFiles;
            [DataMember] public string AssemblyMvid, AssemblySha256, WrittenUtc;
            [DataMember] public string DiagnosticResult, FunctionalResult, Cause = "UNKNOWN";
            [DataMember] public int ErrorFlags, ArmCount, ClaimCount, DuplicateReceiptCount;
            [DataMember] public long Frequency = Stopwatch.Frequency;
            [DataMember] public long ArmStartedTicks, ArmCompletedTicks, EntryStartedTicks, EntryCompletedTicks;
            [DataMember] public long BeforeDelayTicks, AfterDelayTicks, BeforeDecisionTicks, AfterDecisionTicks;
            [DataMember] public long CaptureCompletedTicks, AfterAbortTicks;
            [DataMember] public bool EntryCaptured, ContinuationCaptured, SameContextAsEntry;
            [DataMember] public string Winner;
            [DataMember] public int OperationStatusAtDecision, ProbeTaskStatus, DelayTaskStatus;
            [DataMember] public bool ShouldFail, AbortAttempted, AbortReturned, DeadlineLowerBoundProven;
            [DataMember] public bool QueueHooksInstalled = false;
            [DataMember] public int DroppedEvents = 0;
            [DataMember] public string OperationStatusLegend = "0=Pending;1=Aborted;2=Completed;3=Executing";
            [DataMember] public string TaskStatusLegend = "0=Created;1=WaitingForActivation;2=WaitingToRun;3=Running;4=WaitingForChildrenToComplete;5=RanToCompletion;6=Canceled;7=Faulted";
            [DataMember] public DispatcherSnapshot Entry = new DispatcherSnapshot();
            [DataMember] public DispatcherSnapshot Continuation = new DispatcherSnapshot();
        }

        [DataContract]
        internal sealed class SourceDigest
        {
            [DataMember] public string RelativePath;
            [DataMember] public string HashKind = "SHA256_UTF8_LF_NO_BOM";
            [DataMember] public string Sha256;
        }

        // Called after functional-completion.txt is written; never changes its result.
        internal static void TryWriteArtifact(string dataDir, string result)
        {
            if (!Enabled) return;
            Receipt data;
            lock (ReceiptGate)
            {
                data = _pending;
                _pending = null;
                if (data != null)
                {
                    data.ReceiptCount = _receipts;
                    data.AggregateErrorFlags = _errorFlags;
                    data.LifecycleStatus = _lifecycleCompleted ? "COMPLETED" : "INTERRUPTED";
                }
            }
            if (data == null)
            {
                ReportStatus(dataDir, "NO_CAPTURE");
                return;
            }
            var status = "INVALID_WRITE_FAILED";
            try
            {
                data.ArmCount = Volatile.Read(ref _arms);
                data.ClaimCount = Volatile.Read(ref _claims);
                data.DuplicateReceiptCount = Volatile.Read(ref _duplicates);
                if (data.DuplicateReceiptCount != 0) data.AggregateErrorFlags |= DuplicateReceipt;
                if (data.ArmCount < 1 || data.ArmCount > 7 || data.ClaimCount != data.ArmCount ||
                    data.ReceiptCount != data.ArmCount ||
                    (data.LifecycleStatus == "COMPLETED" && (data.ReceiptCount != 7 || data.ShouldFail)))
                    data.AggregateErrorFlags |= InvalidClaim;
                data.Entry.DescribeAndReleaseType();
                data.Continuation.DescribeAndReleaseType();
                data.FunctionalResult = result.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : "FAIL";
                data.DiagnosticResult = data.AggregateErrorFlags != 0 ? "INVALID" : data.ShouldFail ? "REPRODUCED_PREDICATE" :
                    data.LifecycleStatus == "COMPLETED" ? "NOT_REPRODUCED" : "INCOMPLETE";
                data.DeadlineLowerBoundProven = data.Winner == "delay" && data.DelayTaskStatus == (int)TaskStatus.RanToCompletion &&
                    data.Frequency > 0 && data.Frequency <= long.MaxValue / 3 && data.AfterDelayTicks > 0 &&
                    data.BeforeDecisionTicks - data.AfterDelayTicks >= data.Frequency * 3;
                data.WrittenUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                var assembly = Assembly.GetExecutingAssembly();
                data.AssemblyMvid = assembly.ManifestModule.ModuleVersionId.ToString("D");
                data.AssemblySha256 = HashFileOrUnavailable(assembly.Location);
                data.GitHubEventSha = ReadEventSha();
                var paths = new[] { "tests/Win7POS.Wpf.UiSmokeHarness/CartPerformanceRegressionSmoke.cs",
                    "tests/Win7POS.Wpf.UiSmokeHarness/PerfPhaseDrainObservation.cs", "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs" };
                data.SourceFiles = new SourceDigest[paths.Length];
                for (var index = 0; index < paths.Length; index++)
                    data.SourceFiles[index] = new SourceDigest { RelativePath = paths[index],
                        Sha256 = HashSourceOrUnavailable(Path.Combine(Environment.CurrentDirectory, paths[index].Replace('/', Path.DirectorySeparatorChar))) };
                using (var buffer = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(Receipt)).WriteObject(buffer, data);
                    if (buffer.Length > 16384)
                    {
                        status = "INVALID_OVERSIZE";
                        return;
                    }
                    File.WriteAllBytes(Path.Combine(dataDir, FileName), buffer.ToArray());
                    status = "WRITTEN_" + data.DiagnosticResult;
                }
            }
            catch { status = "INVALID_WRITE_FAILED"; }
            finally
            {
                data.Entry.ContextType = null;
                data.Continuation.ContextType = null;
                ReportStatus(dataDir, status);
            }
        }

        private static void ReportStatus(string dataDir, string status)
        {
            try { File.WriteAllText(Path.Combine(dataDir, StatusFileName), "PERF_PHASE_DIAGNOSTIC_STATUS=" + status + Environment.NewLine,
                new System.Text.UTF8Encoding(false)); }
            catch { /* Missing status file is rejected externally; preserve the functional result. */ }
            try { Console.WriteLine("PERF_PHASE_DIAGNOSTIC_STATUS=" + status); }
            catch { /* Receipt/marker absence is rejected externally; preserve the functional result. */ }
        }

        private static string HashFileOrUnavailable(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch { return "UNAVAILABLE"; }
        }

        private static string ReadEventSha()
        {
            var value = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (value == null || value.Length != 40) return "UNAVAILABLE";
            foreach (var character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F'))) return "UNAVAILABLE";
            return value.ToLowerInvariant();
        }

        private static string HashSourceOrUnavailable(string path)
        {
            try
            {
                var encoding = new System.Text.UTF8Encoding(false, true);
                var source = File.ReadAllText(path, encoding);
                if (source.Length != 0 && source[0] == '\uFEFF') source = source.Substring(1);
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(encoding.GetBytes(source.Replace("\r\n", "\n")))).Replace("-", "").ToLowerInvariant();
            }
            catch { return "UNAVAILABLE"; }
        }
    }
}
