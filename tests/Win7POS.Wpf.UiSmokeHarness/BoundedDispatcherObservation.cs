using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
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
        { public long Id, Posted, Eligible; public DispatcherPriority Priority; public WeakReference Operation; public byte VisualCategory; public bool Started; }
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
        internal BoundedDispatcherObservation(Dispatcher dispatcher, bool enabled = true)
        {
            _dispatcher = dispatcher;
            _enabled = enabled;
            if (!enabled) return;
            if (Method == null || Arguments == null) throw new InvalidOperationException("dispatcher_observation_not_supported_on_this_runtime");
            dispatcher.Hooks.OperationPosted += Posted;
            dispatcher.Hooks.OperationCompleted += Finished;
            dispatcher.Hooks.OperationAborted += Finished;
            dispatcher.Hooks.OperationPriorityChanged += PriorityChanged;
            dispatcher.Hooks.OperationStarted += Started;
        }
        private void Posted(object sender, DispatcherHookEventArgs args)
        {
            lock (_sync)
            {
                // Synchronous Send can raise Completed before Posted on another
                // thread. Do not retain its already-finished bookkeeping entry.
                if (args.Operation.Status == DispatcherOperationStatus.Completed || args.Operation.Status == DispatcherOperationStatus.Aborted) return;
                if (_index.TryGetValue(args.Operation, out _)) return;
                if (_entries.Count >= Capacity)
                {
                    foreach (var old in _entries.Values.ToArray())
                    {
                        var operation = old.Operation.Target as DispatcherOperation;
                        if (operation == null || operation.Status == DispatcherOperationStatus.Completed || operation.Status == DispatcherOperationStatus.Aborted)
                            _entries.Remove(old.Id);
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
                var now = Stopwatch.GetTimestamp();
                var entry = new Entry { Id = ++_next, Posted = now, Eligible = now, Priority = args.Operation.Priority, Operation = new WeakReference(args.Operation) };
                // Inspect only Input work for the two application-owned visual
                // categories; never retain the delegate or its target.
                if (args.Operation.Priority == DispatcherPriority.Input && Method.GetValue(args.Operation) is Delegate callback && callback.Target is Win7POS.Wpf.Pos.PosView)
                {
                    if (callback.Method.Name.Contains("FocusBarcode")) { entry.VisualCategory = 1; _focusPeak = Math.Max(_focusPeak, ++_focusPending); }
                    if (callback.Method.Name.Contains("QueueSelectionScroll")) { entry.VisualCategory = 2; _scrollPeak = Math.Max(_scrollPeak, ++_scrollPending); }
                }
                _index.Add(args.Operation, entry); _entries.Add(entry.Id, entry);
                PeakObserved = Math.Max(PeakObserved, _entries.Count);
            }
        }
        private void PriorityChanged(object sender, DispatcherHookEventArgs args)
        {
            lock (_sync)
                if (_index.TryGetValue(args.Operation, out var entry))
                {
                    if (entry.Priority == DispatcherPriority.Inactive) entry.Eligible = Stopwatch.GetTimestamp();
                    entry.Priority = args.Operation.Priority;
                }
        }
        private void Finished(object sender, DispatcherHookEventArgs args)
        {
            lock (_sync)
                if (_index.TryGetValue(args.Operation, out var entry))
                {
                    if (!entry.Started) RemoveVisualPending(entry);
                    _entries.Remove(entry.Id); _index.Remove(args.Operation);
                }
        }
        private void RemoveVisualPending(Entry entry)
        {
            if (entry.VisualCategory == 1) _focusPending--;
            if (entry.VisualCategory == 2) _scrollPending--;
        }
        private void Started(object sender, DispatcherHookEventArgs args)
        {
            lock (_sync)
                if (_index.TryGetValue(args.Operation, out var entry))
                {
                    entry.Started = true;
                    RemoveVisualPending(entry);
                    if (entry.VisualCategory == 1) _focusMaximumWait = Math.Max(_focusMaximumWait, Age(entry.Eligible));
                    if (entry.VisualCategory == 2) _scrollMaximumWait = Math.Max(_scrollMaximumWait, Age(entry.Eligible));
                }
        }
        private static double Age(long timestamp) => (Stopwatch.GetTimestamp() - timestamp) * 1000d / Stopwatch.Frequency;
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
                    { _entries.Remove(entry.Id); continue; }
                    if (operation.Status != DispatcherOperationStatus.Pending) continue;
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
