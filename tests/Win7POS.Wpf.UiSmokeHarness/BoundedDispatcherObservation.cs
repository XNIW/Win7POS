using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Threading;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal sealed class BoundedDispatcherObservation : IDisposable
    {
        // Public command batches produced about 9k transient UI Automation
        // operations in the clean control. This is an observation capacity,
        // not a performance acceptance budget; exceeding it invalidates data.
        private const int Capacity = 16384;
        private sealed class Entry
        { public long Id, Posted, Eligible, PriorityChangedAt, ExecutionStarted; public DispatcherPriority Priority; public WeakReference Operation; public byte VisualCategory; public bool Started, VisualReleased, Inconsistent, PriorityChangePending; }
        private struct Execution
        { internal long Id, Posted, Start, End; internal DispatcherPriority Priority; internal MethodInfo Method; }
        private Execution[] _executions;
        private int _executionCount;
        internal void EnableExecutionCapture() { lock (_sync) _executions = new Execution[256]; }
        internal void WriteExecutions(TextWriter writer)
        {
            Execution[] records; int total;
            lock (_sync)
            {
                if (_executions == null) return;
                total = _executionCount;
                records = Enumerable.Range(Math.Max(0, total - _executions.Length), Math.Min(total, _executions.Length))
                    .Select(index => _executions[index % _executions.Length]).ToArray();
            }
            // A diagnostic window of completed callbacks >=2ms, not a complete
            // event trace. No timer reflection, stack capture, watchdog or I/O in
            // the measured callback; never retain an operation or delegate target.
            writer.WriteLine("EXECUTION_WINDOW,clock=Stopwatch_absolute_ticks,minimum_ms=2,capacity=256,frequency=" + Stopwatch.Frequency + ",total=" + total + ",overwritten=" + Math.Max(0, total - records.Length));
            foreach (var record in records)
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "EXECUTION,{0},posted_tick={1},start_tick={2},end_tick={3},duration_ms={4:F3},priority={5},method={6}.{7}",
                    record.Id, record.Posted, record.Start, record.End, (record.End - record.Start) * 1000d / Stopwatch.Frequency,
                    record.Priority, record.Method?.DeclaringType?.FullName, record.Method?.Name));
        }
        internal sealed class State
        {
            public int Pending, Inactive, FocusPending, ScrollPending, ClosedRootedWindows;
            public double OldestMs, OldestPostedMs, FocusOldestMs, ScrollOldestMs;
            public double FocusMaximumWaitMs, ScrollMaximumWaitMs;
            public int FocusPeak, ScrollPeak;
            public string Detail;
        }
        private readonly Dispatcher _dispatcher;
        private readonly bool _enabled;
        private readonly Func<long> _clock;
        private readonly object _sync = new object();
        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();
        private readonly ConditionalWeakTable<DispatcherOperation, Entry> _index = new ConditionalWeakTable<DispatcherOperation, Entry>();
        private static readonly FieldInfo Method = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Arguments = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
        private long _next;
        private int _focusPending, _scrollPending, _focusPeak, _scrollPeak;
        private double _focusMaximumWait, _scrollMaximumWait;
        internal int Dropped { get; private set; }
        internal int PeakObserved { get; private set; }
        internal string OverflowDetail { get; private set; }
        internal BoundedDispatcherObservation(Dispatcher dispatcher, bool enabled = true) : this(dispatcher, enabled, Stopwatch.GetTimestamp, true) { }
        private BoundedDispatcherObservation(Dispatcher dispatcher, bool enabled, Func<long> clock, bool subscribe)
        {
            _dispatcher = dispatcher;
            _enabled = enabled;
            _clock = clock;
            if (!enabled) return;
            if (Method == null || Arguments == null) throw new InvalidOperationException("dispatcher_observation_not_supported_on_this_runtime");
            if (!subscribe) return;
            dispatcher.Hooks.OperationPosted += Posted;
            dispatcher.Hooks.OperationCompleted += Finished;
            dispatcher.Hooks.OperationAborted += Finished;
            dispatcher.Hooks.OperationPriorityChanged += PriorityChanged;
            dispatcher.Hooks.OperationStarted += Started;
        }
        private void Posted(object sender, DispatcherHookEventArgs args)
            => RecordPosted(args.Operation);
        private void RecordPosted(DispatcherOperation operation)
        {
            lock (_sync)
            {
                // Synchronous Send can raise Completed before Posted on another
                // thread. Do not retain its already-finished bookkeeping entry.
                if (operation.Status == DispatcherOperationStatus.Completed || operation.Status == DispatcherOperationStatus.Aborted) return;
                if (_index.TryGetValue(operation, out _)) return;
                if (_entries.Count >= Capacity)
                {
                    foreach (var old in _entries.Values.ToArray())
                    {
                        var oldOperation = old.Operation.Target as DispatcherOperation;
                        if (oldOperation == null || oldOperation.Status == DispatcherOperationStatus.Completed || oldOperation.Status == DispatcherOperationStatus.Aborted)
                        {
                            RemoveVisualPending(old);
                            _entries.Remove(old.Id);
                            if (oldOperation != null) _index.Remove(oldOperation);
                        }
                    }
                    if (_entries.Count >= Capacity)
                    {
                        if (OverflowDetail == null)
                            OverflowDetail = string.Join("|", _entries.Values.Select(item => item.Operation.Target as DispatcherOperation).Where(item => item != null)
                                .GroupBy(item => item.Status + ":" + item.Priority + ":" + (Method.GetValue(item) as Delegate)?.Method)
                                .OrderByDescending(group => group.Count()).Take(20).Select(group => group.Count() + ":" + group.Key));
                        Dropped++; return;
                    }
                }
                var now = _clock();
                var entry = new Entry { Id = ++_next, Posted = now, Eligible = now, Priority = operation.Priority, Operation = new WeakReference(operation) };
                // Inspect only Input work for the two application-owned visual
                // categories; never retain the delegate or its target.
                if (operation.Priority == DispatcherPriority.Input && Method.GetValue(operation) is Delegate callback && callback.Target is Win7POS.Wpf.Pos.PosView)
                {
                    if (callback.Method.Name.Contains("FocusBarcode")) { entry.VisualCategory = 1; _focusPeak = Math.Max(_focusPeak, ++_focusPending); }
                    if (callback.Method.Name.Contains("QueueSelectionScroll")) { entry.VisualCategory = 2; _scrollPeak = Math.Max(_scrollPeak, ++_scrollPending); }
                }
                _index.Add(operation, entry); _entries.Add(entry.Id, entry);
                PeakObserved = Math.Max(PeakObserved, _entries.Count);
            }
        }
        private void PriorityChanged(object sender, DispatcherHookEventArgs args)
            => RecordPriorityChanged(args.Operation);
        private void RecordPriorityChanged(DispatcherOperation operation)
        {
            lock (_sync)
                if (_index.TryGetValue(operation, out var entry))
                {
                    if (entry.PriorityChangePending) ReconcilePriority(entry, operation.Priority);
                    if (entry.PriorityChangePending) Inconsistent(entry);
                    if (operation.Priority != entry.Priority)
                    {
                        // A runtime exposing the new priority at the hook.
                        if (entry.Priority == DispatcherPriority.Inactive) entry.Eligible = _clock();
                        entry.Priority = operation.Priority;
                    }
                    else
                    {
                        // Framework 4.8 raises the hook before publishing the new
                        // operation.Priority. Retain the event time, resolve the
                        // destination at the next observation/hook/start.
                        entry.PriorityChangedAt = _clock();
                        entry.PriorityChangePending = true;
                    }
                }
        }
        private void Inconsistent(Entry entry)
        {
            if (entry.Inconsistent) return;
            entry.Inconsistent = true; Dropped++;
            if (OverflowDetail == null) OverflowDetail = "priority_transition_timestamp_missing_or_concurrent";
        }
        private void ReconcilePriority(Entry entry, DispatcherPriority current)
        {
            if (current == entry.Priority) return; // Hook's new value may not yet be published.
            if (entry.PriorityChangePending)
            {
                if (entry.Priority == DispatcherPriority.Inactive && current != DispatcherPriority.Inactive)
                    entry.Eligible = entry.PriorityChangedAt;
                entry.PriorityChangePending = false;
                entry.Priority = current;
            }
            else Inconsistent(entry); // No timestamp can be reconstructed for a lost hook.
        }
        private void Finished(object sender, DispatcherHookEventArgs args)
            => RecordFinished(args.Operation);
        private void RecordFinished(DispatcherOperation operation)
        {
            lock (_sync)
                if (_index.TryGetValue(operation, out var entry))
                {
                    if (_executions != null && entry.ExecutionStarted != 0)
                    {
                        var end = _clock();
                        if ((end - entry.ExecutionStarted) * 1000d / Stopwatch.Frequency >= 2)
                            _executions[_executionCount++ % _executions.Length] = new Execution { Id = entry.Id, Posted = entry.Posted,
                                Start = entry.ExecutionStarted, End = end, Priority = operation.Priority, Method = (Method.GetValue(operation) as Delegate)?.Method };
                    }
                    if (!entry.Started) RemoveVisualPending(entry);
                    _entries.Remove(entry.Id); _index.Remove(operation);
                }
        }
        private void RemoveVisualPending(Entry entry)
        {
            if (entry.VisualReleased) return;
            entry.VisualReleased = true;
            if (entry.VisualCategory == 1) _focusPending--;
            if (entry.VisualCategory == 2) _scrollPending--;
        }
        private void Started(object sender, DispatcherHookEventArgs args)
            => RecordStarted(args.Operation);
        private void RecordStarted(DispatcherOperation operation)
        {
            lock (_sync)
                if (_index.TryGetValue(operation, out var entry))
                {
                    if (entry.Started) return;
                    ReconcilePriority(entry, operation.Priority);
                    entry.Started = true;
                    if (_executions != null) entry.ExecutionStarted = _clock();
                    RemoveVisualPending(entry);
                    if (entry.VisualCategory == 1) _focusMaximumWait = Math.Max(_focusMaximumWait, Age(entry.Eligible));
                    if (entry.VisualCategory == 2) _scrollMaximumWait = Math.Max(_scrollMaximumWait, Age(entry.Eligible));
                }
        }
        private double Age(long timestamp) => (_clock() - timestamp) * 1000d / Stopwatch.Frequency;
        internal State Snapshot()
        {
            if (!_enabled) return new State { Pending = -1, ClosedRootedWindows = -1, Detail = "OBSERVER_DISABLED_DIAGNOSTIC_ONLY" };
            var result = new State();
            var closed = new HashSet<Window>();
            var details = new List<string>();
            lock (_sync)
            {
                result.FocusPeak = _focusPeak; result.ScrollPeak = _scrollPeak;
                result.FocusMaximumWaitMs = _focusMaximumWait; result.ScrollMaximumWaitMs = _scrollMaximumWait;
                _focusPeak = _focusPending; _scrollPeak = _scrollPending;
                _focusMaximumWait = _scrollMaximumWait = 0;
                foreach (var entry in _entries.Values.ToArray())
                {
                    var operation = entry.Operation.Target as DispatcherOperation;
                    if (operation == null || operation.Status == DispatcherOperationStatus.Completed || operation.Status == DispatcherOperationStatus.Aborted)
                    {
                        RemoveVisualPending(entry);
                        _entries.Remove(entry.Id);
                        if (operation != null) _index.Remove(operation);
                        continue;
                    }
                    if (operation.Status != DispatcherOperationStatus.Pending) continue;
                    ReconcilePriority(entry, operation.Priority);
                    var callback = Method?.GetValue(operation) as Delegate;
                    var method = callback?.Method.Name ?? "unknown";
                    if (operation.Priority == DispatcherPriority.Inactive) { result.Inactive++; continue; }
                    result.Pending++;
                    var age = Age(entry.Eligible);
                    result.OldestMs = Math.Max(result.OldestMs, age);
                    result.OldestPostedMs = Math.Max(result.OldestPostedMs, Age(entry.Posted));
                    if (method.Contains("FocusBarcode")) { result.FocusPending++; result.FocusOldestMs = Math.Max(result.FocusOldestMs, age); }
                    if (method.Contains("QueueSelectionScroll")) { result.ScrollPending++; result.ScrollOldestMs = Math.Max(result.ScrollOldestMs, age); }
                    DependencyObject scope = null;
                    if (callback?.Target?.GetType().FullName == "System.Windows.Documents.TextEditor")
                    {
                        var field = callback.Target.GetType().GetField("_uiScope", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (field == null) throw new InvalidOperationException("text_editor_root_observation_unavailable");
                        scope = field.GetValue(callback.Target) as DependencyObject;
                    }
                    if (Arguments?.GetValue(operation) is UIElementAutomationPeer peer) scope = peer.Owner;
                    for (var depth = 0; scope != null && depth < 32; depth++)
                    {
                        if (scope is Window window)
                        {
                            if (!Application.Current.Windows.Cast<Window>().Contains(window)) closed.Add(window);
                            break;
                        }
                        scope = LogicalTreeHelper.GetParent(scope) ?? (scope is Visual ? VisualTreeHelper.GetParent(scope) : null);
                    }
                    details.Add(operation.Priority + ":" + method + ":" + age.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            result.ClosedRootedWindows = closed.Count;
            result.Detail = string.Join("|", details);
            return result;
        }

        internal static void VerifyControlledEvents(Dispatcher dispatcher)
        {
            var now = 1L;
            long At(int ms) => 1 + (long)(ms * Stopwatch.Frequency / 1000d);
            using var observer = new BoundedDispatcherObservation(dispatcher, true, () => System.Threading.Interlocked.Read(ref now), false);
            var operations = new List<DispatcherOperation>();
            DispatcherOperation Post(DispatcherPriority priority)
            {
                var operation = dispatcher.BeginInvoke(priority, new Action(() => { }));
                operations.Add(operation); observer.RecordPosted(operation); return operation;
            }
            void Require(bool condition, string message)
            { if (!condition) throw new InvalidOperationException("observer_controlled_events:" + message); }
            try
            {
                Require(DispatcherTimerTrace.NativeDueDelta(int.MinValue + 4, int.MaxValue - 5) == 10 &&
                    DispatcherTimerTrace.NativeDueDelta(int.MaxValue - 5, int.MinValue + 4) == -10, "native due wrap conversion");
                var timer = Post(DispatcherPriority.Inactive);
                now = At(90000);
                Require(observer.Snapshot().Pending == 0, "scheduled interval counted ready");
                observer.RecordPriorityChanged(timer); timer.Priority = DispatcherPriority.Background;
                now = At(90001);
                Require(observer.Snapshot().OldestMs < 1.01, "Inactive time counted after promotion");
                now = At(90100); observer.RecordPriorityChanged(timer); timer.Priority = DispatcherPriority.Input;
                now = At(90301);
                Require(observer.Snapshot().OldestMs >= 301, "ready reprioritization erased real >250ms delay");
                observer.RecordPriorityChanged(timer); timer.Priority = DispatcherPriority.Inactive;
                now = At(100000); observer.RecordPriorityChanged(timer); timer.Priority = DispatcherPriority.Render;
                now = At(100002);
                Require(observer.Snapshot().OldestMs < 2.01, "second Inactive interval counted ready");
                timer.Abort(); observer.RecordFinished(timer); observer.RecordFinished(timer); observer.RecordPosted(timer);
                Require(observer.Snapshot().Pending == 0, "aborted/completed-before-posted operation retained");
                var restarted = Post(DispatcherPriority.Background);
                now = At(100003); Require(observer.Snapshot().OldestMs < 1.01, "restart inherited old age");
                // Model the two owned categories without constructing a POS view.
                observer._index.TryGetValue(restarted, out var visual);
                visual.VisualCategory = 1; observer._focusPending = observer._focusPeak = 1;
                observer.RecordStarted(restarted); observer.RecordStarted(restarted); observer.RecordFinished(restarted);
                Require(observer._focusPending == 0, "duplicate start/finish made counter negative");
                restarted.Abort();
                var missingFinish = Post(DispatcherPriority.Input);
                observer._index.TryGetValue(missingFinish, out visual);
                visual.VisualCategory = 2; observer._scrollPending = observer._scrollPeak = 1;
                missingFinish.Abort(); observer.Snapshot(); observer.RecordFinished(missingFinish);
                Require(observer._scrollPending == 0, "missing finish left stale visual counter");
                var missingPromotion = Post(DispatcherPriority.Inactive);
                missingPromotion.Priority = DispatcherPriority.Background;
                observer.Snapshot(); observer.Snapshot();
                Require(observer.Dropped == 1, "missing/racing priority event concealed or double-counted");
                missingPromotion.Abort(); observer.RecordFinished(missingPromotion);
                var raced = Post(DispatcherPriority.Background);
                var worker = System.Threading.Tasks.Task.Run(() =>
                { for (var index = 0; index < 1000; index++) observer.RecordFinished(raced); });
                for (var index = 0; index < 1000; index++) observer.Snapshot();
                Require(worker.Wait(5000) && observer.Snapshot().Pending == 0 && observer._focusPending == 0 && observer._scrollPending == 0,
                    "concurrent completion/snapshot lost accounting");
            }
            finally { foreach (var operation in operations) operation.Abort(); }
        }
        public void Dispose()
        {
            _dispatcher.Hooks.OperationPosted -= Posted;
            _dispatcher.Hooks.OperationCompleted -= Finished;
            _dispatcher.Hooks.OperationAborted -= Finished;
            _dispatcher.Hooks.OperationPriorityChanged -= PriorityChanged;
            _dispatcher.Hooks.OperationStarted -= Started;
        }
    }
}
