using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Test-only factorial reproducer. It never certifies stability or modifies
    // internal WPF operations, IME, GC or host power/session policy.
    internal static class CartPerformanceDiagnostics
    {
        private static readonly FieldInfo SessionField = typeof(PosWorkflowService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
        private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        // Controlled equivalent: same 20-second idle sampling, foreground WPF
        // window and 125ms Background timer, no POS view, cart, service or fixture.
        internal static async Task<string> RunTimerControlAsync(string directory)
        {
            AppDomain.MonitoringIsEnabled = true;
            var minutes = int.Parse(Environment.GetEnvironmentVariable("WIN7POS_QA_SOAK_MINUTES"), CultureInfo.InvariantCulture);
            if (minutes < 1 || minutes > 5) throw new ArgumentException("timer control duration");
            var input = new TextBox { Text = "Synthetic timer control" };
            var host = new Window { Width = 1024, Height = 768, Content = input, ShowInTaskbar = false };
            using var trace = new OperationObserver(Dispatcher.CurrentDispatcher, "bounded", false, true);
            using var observation = new BoundedDispatcherObservation(Dispatcher.CurrentDispatcher);
            using var environment = new PerformanceEnvironment(directory);
            using var idle = new StreamWriter(Path.Combine(directory, "diagnostic-idle.csv"));
            idle.WriteLine("cycle,elapsed_s,pending,oldest_ms,inactive,observer_dropped,trace_dropped");
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(125) };
            timer.Tick += ControlledTick;
            trace.StartTimeline(Path.Combine(directory, "diagnostic-timeline.txt"));
            var cycle = 0;
            try
            {
                host.Show(); host.Activate(); input.Focus(); timer.Start();
                var start = environment.AwakeSeconds;
                do
                {
                    for (var second = 0; second < 20; second++)
                    {
                        trace.Checkpoint("idle_sample_begin;cycle=" + cycle + ";second=" + second);
                        environment.Sample(host);
                        trace.Checkpoint("idle_sample_end;cycle=" + cycle + ";second=" + second);
                        trace.SampleTimers("idle");
                        await Task.Delay(1000);
                    }
                    trace.Checkpoint("final_sample_begin;cycle=" + cycle);
                    environment.Sample(host);
                    trace.Checkpoint("final_sample_end;cycle=" + cycle);
                    trace.SampleTimers("before_snapshot");
                    var state = observation.Snapshot();
                    idle.WriteLine(string.Join(",", cycle++, (environment.AwakeSeconds - start).ToString("F3", CultureInfo.InvariantCulture), state.Pending,
                        state.OldestMs.ToString("F3", CultureInfo.InvariantCulture), state.Inactive, observation.Dropped, trace.Dropped));
                    idle.Flush(); environment.Flush();
                } while (environment.AwakeSeconds - start < minutes * 60);
                File.WriteAllText(Path.Combine(directory, "diagnostic-scans.json"), "{\"control\":\"minimal-wpf-timer\",\"measurementCompleted\":true,\"environmentValid\":" + (environment.Valid ? "true" : "false") + ",\"cycles\":" + cycle + ",\"qualifiesApplication\":false}");
                return "PASS DIAGNOSTIC_COMPLETED minimal WPF control; no application qualification";
            }
            finally { timer.Stop(); timer.Tick -= ControlledTick; host.Close(); }
        }
        private static void ControlledTick(object sender, EventArgs args) { }

        internal static async Task<string> RunAsync(string directory, int products, string specification)
        {
            var options = specification.Split(';').Select(value => value.Split('='))
                .ToDictionary(pair => pair[0], pair => pair.Length == 2 ? pair[1] : throw new ArgumentException("Invalid diagnostic option"));
            string Option(string key, string fallback) => options.TryGetValue(key, out var value) ? value : fallback;
            int Number(string key, int fallback, int minimum, int maximum)
            {
                var value = int.Parse(Option(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
                if (value < minimum || value > maximum) throw new ArgumentException(key);
                return value;
            }
            var allowed = new[] { "kind", "cycles", "cart", "idleMs", "bitmap", "observer", "reverse", "composition", "reset", "roots", "visibleSelectionOnly", "nativeQueue", "omitGrid", "trace", "inactiveProgress", "bareDialogs" };
            if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Unknown diagnostic option");
            var kind = Option("kind", "alternate");
            if (!new[] { "service", "rows", "grid", "alternate", "dialogs", "original", "text" }.Contains(kind)) throw new ArgumentException("kind");
            var cycles = Number("cycles", 4, 1, 120);
            var cart = Number("cart", 500, 1, 500);
            var idleMs = Number("idleMs", 1000, 0, 20000);
            var bitmapEnabled = Number("bitmap", 0, 0, 1) == 1;
            var reverse = Number("reverse", 0, 0, 1) == 1;
            var reset = Number("reset", 1, 0, 1) == 1;
            var roots = Number("roots", 0, 0, 1) == 1;
            var visibleSelectionOnly = Number("visibleSelectionOnly", 0, 0, 1) == 1;
            var nativeQueue = Number("nativeQueue", 0, 0, 1) == 1;
            var omitGrid = Number("omitGrid", 0, 0, 1) == 1;
            var trace = Number("trace", 0, 0, 1) == 1;
            var inactiveProgress = Number("inactiveProgress", 0, 0, 1) == 1;
            var bareDialogs = Number("bareDialogs", 0, 0, 1) == 1;
            if (bareDialogs && kind != "dialogs") throw new ArgumentException("bareDialogs requires dialogs kind");
            if (omitGrid && kind != "rows") throw new ArgumentException("omitGrid is a fixed Rows ablation only");
            if (trace) AppDomain.MonitoringIsEnabled = true;
            var composition = Option("composition", "replaced");
            if (composition != "replaced" && composition != "real") throw new ArgumentException("composition");
            var observerMode = Option("observer", "off");
            if (!new[] { "off", "legacy", "bounded" }.Contains(observerMode)) throw new ArgumentException("observer");
            File.WriteAllText(Path.Combine(directory, "diagnostic-options.txt"), specification);
            File.WriteAllText(Path.Combine(directory, "diagnostic-ready.txt"), Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            var output = new StringBuilder("cycle,phase,ordinal,service_complete_ms,ui_return_ms,apply_ms,layout_ms,bitmap_ms,rows_realized,grid_realized,collection_changes,row_notifications,private_bytes,managed_bytes,gc0,gc1,gc2,cpu_ms\n");
            var idle = new StringBuilder("cycle,elapsed_ms,private_bytes,managed_bytes,handles,threads,pending,oldest_ms,observer_dropped\n");
            var priorityOutput = new StringBuilder("cycle,priority,completed,wait_ms\n");
            var inspection = new StringBuilder();
            var nativeMessages = new Dictionary<int, long>();
            ThreadMessageEventHandler messageObserver = (ref MSG message, ref bool handled) =>
            {
                if (!nativeMessages.ContainsKey(message.message)) nativeMessages.Add(message.message, 0);
                nativeMessages[message.message]++;
            };
            if (nativeQueue) ComponentDispatcher.ThreadFilterMessage += messageObserver;
            var clock = Stopwatch.StartNew();
            using var process = Process.GetCurrentProcess();
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var observer = new OperationObserver(dispatcher, observerMode, roots, trace);
            PosView view = null;
            Window host = null;
            PosViewModel vm = null;
            var service = new PosWorkflowService();
            var changes = 0;
            var notifications = 0;
            try
            {
                if (kind == "text" || bareDialogs)
                {
                    if (bareDialogs) vm = new PosViewModel(service);
                    host = new Window { Width = 1024, Height = 768, Content = bareDialogs ? (object)new Grid() : new TextBox { Text = "QA plain WPF 中文 café" }, ShowInTaskbar = false };
                    host.Show();
                    RecordEnvironment(directory, host, null);
                }
                else if (kind != "service")
                {
                    view = new PosView();
                    if (composition == "real")
                    {
                        vm = (PosViewModel)view.DataContext;
                        service = (PosWorkflowService)typeof(PosViewModel).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vm);
                    }
                    else
                    {
                        (view.DataContext as IDisposable)?.Dispose();
                        vm = new PosViewModel(service);
                        view.DataContext = vm;
                    }
                    if (visibleSelectionOnly)
                    {
                        // Causal ablation of the existing handler, on the same
                        // immutable application payload. Never a qualification path.
                        var method = typeof(PosView).GetMethod("CartListBox_SelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                        var original = (SelectionChangedEventHandler)Delegate.CreateDelegate(typeof(SelectionChangedEventHandler), view, method);
                        foreach (var name in new[] { "CartListBox", "CartGridListBox" })
                        {
                            var list = (ListBox)view.FindName(name);
                            list.SelectionChanged -= original;
                            list.SelectionChanged += (sender, args) => { if (((ListBox)sender).IsVisible) original(sender, args); };
                        }
                    }
                    host = new Window { Width = 1024, Height = 768, Content = view, ShowInTaskbar = false };
                    if (omitGrid) ((ListBox)view.FindName("CartGridListBox")).ItemsSource = null;
                    host.Show();
                    var initialization = Stopwatch.StartNew();
                    while (vm.IsBusy && initialization.ElapsedMilliseconds < 10000) await Task.Delay(10);
                    if (vm.IsBusy) throw new TimeoutException("View initialization");
                    view.UpdateLayout();
                    vm.CartItems.CollectionChanged += (_, __) => changes++;
                    RecordEnvironment(directory, host, view);
                }
                var session = (PosSession)SessionField.GetValue(service);
                var rows = view?.FindName("CartListBox") as ListBox;
                var grid = view?.FindName("CartGridListBox") as ListBox;
                for (var cycle = 0; cycle < cycles; cycle++)
                {
                    if (cycle == 0 || reset)
                    {
                        session.ReplaceWithLines(Enumerable.Range(1, cart).Select(i => new RestoredLine
                        { ProductId = i, Barcode = "P" + i.ToString("D8"), Name = "Product " + i, UnitPrice = 1000, Quantity = 1 }).ToList());
                        vm?.ApplyDiscountSnapshot(await service.GetSnapshotAsync());
                        if (vm != null)
                            foreach (var row in vm.CartItems) row.PropertyChanged += (_, __) => notifications++;
                    }
                    var modes = kind == "grid" ? new[] { CartViewMode.Grid } : kind == "rows" || kind == "service" ?
                        new[] { CartViewMode.Rows } : reverse ? new[] { CartViewMode.Grid, CartViewMode.Rows } : new[] { CartViewMode.Rows, CartViewMode.Grid };
                    if (kind != "dialogs" && kind != "text")
                        foreach (var mode in modes)
                        {
                            if (vm != null) await vm.SetCartViewModeAsync(mode);
                            for (var scan = 0; scan < 10; scan++)
                            {
                                changes = notifications = 0;
                                var cpuStart = process.TotalProcessorTime.TotalMilliseconds;
                                var start = Stopwatch.GetTimestamp();
                                long completed = 0;
                                var task = service.AddByBarcodeAsync("P00000001");
                                var completion = task.ContinueWith(_ => Interlocked.Exchange(ref completed, Stopwatch.GetTimestamp()),
                                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                                var snapshot = await task;
                                var returned = Stopwatch.GetTimestamp();
                                vm?.ApplyDiscountSnapshot(snapshot);
                                var applied = Stopwatch.GetTimestamp();
                                view?.UpdateLayout();
                                var layout = Stopwatch.GetTimestamp();
                                if (bitmapEnabled && view != null)
                                {
                                    var bitmap = new RenderTargetBitmap(1024, 768, 96, 96, PixelFormats.Pbgra32);
                                    bitmap.Render(view);
                                }
                                var rendered = Stopwatch.GetTimestamp();
                                await completion;
                                var cpu = process.TotalProcessorTime.TotalMilliseconds - cpuStart;
                                process.Refresh();
                                output.AppendFormat(CultureInfo.InvariantCulture,
                                    "{0},{1},{2},{3:F3},{4:F3},{5:F3},{6:F3},{7:F3},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17:F3}\n",
                                    cycle, mode, scan + 1, Ms(completed - start), Ms(Math.Max(0, returned - completed)),
                                    Ms(applied - returned), Ms(layout - applied), Ms(rendered - layout), Containers(rows), Containers(grid),
                                    changes, notifications, process.PrivateMemorySize64, GC.GetTotalMemory(false),
                                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), cpu);
                            }
                        }
                    if (kind == "dialogs" || kind == "original")
                    {
                        var dialog = new Win7POS.Wpf.Pos.Dialogs.DiscountDialog(null, true, service, vm, 100, () => Task.FromResult(false))
                        { Owner = Win7POS.Wpf.Infrastructure.DialogOwnerHelper.GetSafeOwner() };
                        dialog.Show(); dialog.UpdateLayout(); dialog.Close();
                        var result = await ProductImageUiWpfSmoke.RunAsync(Path.Combine(directory, "images"));
                        if (!result.StartsWith("PASS", StringComparison.Ordinal)) throw new InvalidOperationException(result);
                    }
                    if (trace && view != null)
                    {
                        var progress = Descendants(view).OfType<ProgressBar>().ToArray();
                        inspection.AppendLine("PROGRESS," + cycle + ",total=" + progress.Length + ",visible=" + progress.Count(item => item.IsVisible) + ",indeterminate=" + progress.Count(item => item.IsIndeterminate));
                        if (inactiveProgress)
                            foreach (var item in progress.Where(item => !item.IsVisible)) item.IsIndeterminate = false;
                    }
                    if (nativeQueue) RecordNativeQueue(cycle, "before_idle", inspection);
                    await Task.Delay(idleMs);
                    if (nativeQueue) RecordNativeQueue(cycle, "after_idle", inspection);
                    process.Refresh();
                    var state = observer.Snapshot(cycle, inspection);
                    idle.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:F3},{2},{3},{4},{5},{6},{7:F3},{8}\n",
                        cycle, clock.Elapsed.TotalMilliseconds, process.PrivateMemorySize64, GC.GetTotalMemory(false),
                        process.HandleCount, process.Threads.Count, state.Item1, state.Item2, observer.Dropped);
                    // At most one own probe per priority per cycle. Timed out own
                    // probes are aborted; internal WPF work is never touched.
                    foreach (var priority in new[] { DispatcherPriority.Input, DispatcherPriority.Render, DispatcherPriority.DataBind, DispatcherPriority.Background })
                    {
                        var watch = Stopwatch.StartNew();
                        double latency = -1;
                        var probe = dispatcher.InvokeAsync(() => latency = watch.Elapsed.TotalMilliseconds, priority);
                        await Task.WhenAny(probe.Task, Task.Delay(250));
                        var done = probe.Status == DispatcherOperationStatus.Completed;
                        if (!done) probe.Abort();
                        priorityOutput.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F3}\n", cycle, priority, done ? 1 : 0, done ? latency : watch.Elapsed.TotalMilliseconds);
                    }
                    File.WriteAllText(Path.Combine(directory, "diagnostic-scans.csv"), output.ToString());
                    File.WriteAllText(Path.Combine(directory, "diagnostic-idle.csv"), idle.ToString());
                    File.WriteAllText(Path.Combine(directory, "diagnostic-priorities.csv"), priorityOutput.ToString());
                    File.WriteAllText(Path.Combine(directory, "diagnostic-operations.txt"), inspection.ToString());
                    if (nativeQueue) File.WriteAllLines(Path.Combine(directory, "diagnostic-native-messages.csv"),
                        new[] { "message,count" }.Concat(nativeMessages.OrderByDescending(pair => pair.Value).Select(pair => "0x" + pair.Key.ToString("X4") + "," + pair.Value)));
                }
                return "PASS DIAGNOSTIC_COMPLETED; stability and interactive environment NOT_QUALIFIED";
            }
            finally { if (nativeQueue) ComponentDispatcher.ThreadFilterMessage -= messageObserver; host?.Close(); vm?.Dispose(); }
        }

        private static int Containers(ItemsControl control)
        {
            if (control == null) return 0;
            var count = 0;
            for (var index = 0; index < control.Items.Count; index++)
                if (control.ItemContainerGenerator.ContainerFromIndex(index) != null) count++;
            return count;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG message, IntPtr window, uint minimum, uint maximum, uint remove);
        internal static void RecordNativeQueue(int cycle, string phase, StringBuilder text)
        {
            // Zero-time, non-consuming observation of this QA UI thread only.
            // QS_EVENT is recorded separately from keyboard/mouse/post messages.
            foreach (var mask in new uint[] { 0x0001, 0x0002, 0x0004, 0x0008, 0x0040, 0x0400, 0x0800, 0x1000, 0x2000 })
                text.AppendLine("NATIVE," + cycle + "," + phase + ",0x" + mask.ToString("X4") + "," + MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 0, mask, 4));
            // PM_NOREMOVE: inspect the first message without dispatching/removing it.
            if (PeekMessage(out var message, IntPtr.Zero, 0, 0, 0))
                text.AppendLine("PEEK," + cycle + "," + phase + ",0x" + message.message.ToString("X4") + ",window=" + message.hwnd + ",wParam=" + message.wParam + ",lParam=" + message.lParam);
        }
        private static void RecordEnvironment(string directory, Window window, PosView view)
        {
            var desktop = OpenInputDesktop(0, false, 0x0001);
            var error = Marshal.GetLastWin32Error();
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            var text = new StringBuilder();
            text.AppendLine("utc=" + DateTimeOffset.UtcNow.ToString("O"));
            text.AppendLine("clr=" + Environment.Version + ";wpf=" + typeof(Dispatcher).Assembly.FullName);
            text.AppendLine("session=" + Process.GetCurrentProcess().SessionId + ";inputDesktopReadable=" + (desktop != IntPtr.Zero) + ";error=" + error);
            text.AppendLine("visible=" + window.IsVisible + ";active=" + window.IsActive + ";ownForeground=" + (GetForegroundWindow() == new WindowInteropHelper(window).Handle));
            var source = PresentationSource.FromVisual(view ?? (Visual)window);
            text.AppendLine("dpiTransform=" + source?.CompositionTarget?.TransformToDevice);
            foreach (var type in new[] { typeof(Dispatcher), typeof(InputManager), typeof(HwndSource) })
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Where(field => field.Name.IndexOf("msg", StringComparison.OrdinalIgnoreCase) >= 0 || field.Name.IndexOf("message", StringComparison.OrdinalIgnoreCase) >= 0))
                    if (field.FieldType == typeof(int) || field.FieldType == typeof(uint) || field.FieldType.IsEnum) text.AppendLine("MESSAGE_ID," + type.FullName + "." + field.Name + "," + Convert.ToInt64(field.GetValue(null)));
            if (view == null) { File.WriteAllText(Path.Combine(directory, "diagnostic-environment.txt"), text.ToString()); return; }
            foreach (var name in new[] { "CartListBox", "CartGridListBox" })
            {
                var list = (ListBox)view.FindName(name);
                text.AppendLine(name + ";canContentScroll=" + ScrollViewer.GetCanContentScroll(list) + ";virtualizing=" + VirtualizingPanel.GetIsVirtualizing(list) +
                    ";mode=" + VirtualizingPanel.GetVirtualizationMode(list) + ";visibility=" + list.Visibility + ";isVisible=" + list.IsVisible + ";panels=" + string.Join("|", Descendants(list).OfType<Panel>().Select(panel => panel.GetType().Name).Distinct()));
            }
            File.WriteAllText(Path.Combine(directory, "diagnostic-environment.txt"), text.ToString());
        }

        internal sealed class OperationObserver : IDisposable
        {
            private sealed class Entry { public long Id; public long Posted, Eligible, PriorityChangedAt; public bool PriorityChangePending; public DispatcherPriority Priority; public WeakReference Operation; public long Started; public long Allocated; public double Cpu; public string Method; }
            private readonly Dispatcher _dispatcher;
            private readonly string _mode;
            private readonly bool _roots;
            private readonly bool _trace;
            private readonly List<object> _events = new List<object>();
            private readonly DispatcherTimerTrace _timers = new DispatcherTimerTrace();
            private readonly object _writeSync = new object();
            private readonly object _sync = new object();
            private readonly List<Entry> _legacy = new List<Entry>();
            private readonly Dictionary<long, Entry> _bounded = new Dictionary<long, Entry>();
            private readonly ConditionalWeakTable<DispatcherOperation, Entry> _index = new ConditionalWeakTable<DispatcherOperation, Entry>();
            private long _next;
            private readonly long _origin = Stopwatch.GetTimestamp();
            private readonly Dictionary<long, Entry> _running = new Dictionary<long, Entry>();
            private Timer _watchdog;
            private StreamWriter _timeline;
            private int _timelineLines;
            private bool _disposed;
            private bool _producerRecorded;
            public int Dropped { get; private set; }
            public void StartTimeline(string path)
            {
                if (!_trace || _mode == "off") return;
                _timeline = new StreamWriter(path);
                _timeline.WriteLine("TIMELINE,version=3,clock=Stopwatch,cpu=AppDomain_including_workers,allocations=AppDomain,watchdog_ms=100,entry_capacity=16384,event_capacity=512,active_capacity=32,line_capacity=131072,native_due_precision_ms=15.625_plus_bracket,creation=first_observed_not_constructor,priority_hook=may_precede_property_publication");
                _timeline.Flush();
                // Thread-pool timer: it keeps recording when the UI dispatcher
                // is occupied. Only scalar metadata crosses the thread boundary.
                _watchdog = new Timer(_ =>
                {
                    object[] batch;
                    string activeMethod;
                    long activeStarted;
                    var waiting = Stopwatch.GetTimestamp();
                    lock (_sync)
                    {
                        if (_disposed) return;
                        batch = _events.ToArray();
                        _events.Clear();
                        var active = _running.Values.OrderBy(item => item.Started).LastOrDefault();
                        activeMethod = active?.Method;
                        activeStarted = active?.Started ?? 0;
                    }
                    var acquired = Stopwatch.GetTimestamp();
                    // Never hold the UI-observer lock across formatting or file I/O.
                    lock (_writeSync)
                    {
                        if (_disposed) return;
                        if (_timelineLines + batch.Length + 1 > 131072) { lock (_sync) Dropped += batch.Length + 1; return; }
                        foreach (var item in batch) { _timeline.WriteLine(item); _timelineLines++; }
                        _timeline.WriteLine("WATCHDOG," + Ms(Stopwatch.GetTimestamp() - _origin).ToString("F3", CultureInfo.InvariantCulture) +
                            ",cpu_ms=" + AppDomain.CurrentDomain.MonitoringTotalProcessorTime.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) +
                            ",allocated=" + AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize + ",active=" + activeMethod +
                            ",active_ms=" + (activeStarted == 0 ? 0 : Ms(Stopwatch.GetTimestamp() - activeStarted)).ToString("F3", CultureInfo.InvariantCulture) +
                            ",observer_lock_ms=" + Ms(acquired - waiting).ToString("F3", CultureInfo.InvariantCulture));
                        _timelineLines++; _timeline.Flush();
                    }
                }, null, 100, 100);
            }
            public void Checkpoint(string value)
            {
                if (!_trace || _mode == "off") return;
                lock (_sync)
                {
                    if (_events.Count >= 512) { Dropped++; return; }
                    _events.Add("CHECKPOINT," + Ms(Stopwatch.GetTimestamp() - _origin).ToString("F3", CultureInfo.InvariantCulture) + "," + value);
                }
            }
            public OperationObserver(Dispatcher dispatcher, string mode, bool roots, bool trace)
            {
                _dispatcher = dispatcher; _mode = mode; _roots = roots; _trace = trace;
                if (mode == "off") return;
                dispatcher.Hooks.OperationPosted += Posted;
                if (trace) dispatcher.Hooks.OperationStarted += Started;
                if (trace) dispatcher.Hooks.OperationPriorityChanged += PriorityChanged;
                dispatcher.Hooks.OperationCompleted += Finished;
                dispatcher.Hooks.OperationAborted += Finished;
            }
            private void TimerEvent(DispatcherOperation operation, Entry entry, string phase)
            {
                if (!_trace) return;
                ReconcilePriority(entry, operation.Priority);
                var value = _timers.Capture(operation, phase, _origin, entry.Posted, entry.Eligible, entry.Started);
                if (value == null) return;
                if (_events.Count < 512) _events.Add(value); else Dropped++;
            }
            private static void ReconcilePriority(Entry entry, DispatcherPriority current)
            {
                if (!entry.PriorityChangePending || entry.Priority == current) return;
                if (entry.Priority == DispatcherPriority.Inactive && current != DispatcherPriority.Inactive)
                    entry.Eligible = entry.PriorityChangedAt;
                entry.Priority = current;
                entry.PriorityChangePending = false;
            }
            private void PriorityChanged(object sender, DispatcherHookEventArgs args)
            {
                lock (_sync)
                    if (_index.TryGetValue(args.Operation, out var entry))
                    {
                        ReconcilePriority(entry, args.Operation.Priority);
                        if (entry.Priority != args.Operation.Priority)
                        {
                            if (entry.Priority == DispatcherPriority.Inactive) entry.Eligible = Stopwatch.GetTimestamp();
                            entry.Priority = args.Operation.Priority;
                        }
                        else { entry.PriorityChangedAt = Stopwatch.GetTimestamp(); entry.PriorityChangePending = true; }
                        TimerEvent(args.Operation, entry, "priority");
                    }
            }
            internal void SampleTimers(string phase)
            {
                if (!_trace) return;
                lock (_sync)
                    foreach (var entry in _bounded.Values)
                        if (entry.Operation.Target is DispatcherOperation operation) TimerEvent(operation, entry, phase);
            }
            private void Started(object sender, DispatcherHookEventArgs args)
            {
                lock (_sync)
                    if (_index.TryGetValue(args.Operation, out var entry))
                    {
                        entry.Started = Stopwatch.GetTimestamp();
                        TimerEvent(args.Operation, entry, "start");
                        entry.Allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                        entry.Cpu = AppDomain.CurrentDomain.MonitoringTotalProcessorTime.TotalMilliseconds;
                        var method = (MethodField?.GetValue(args.Operation) as Delegate)?.Method;
                        entry.Method = args.Operation.Priority + ":" + method?.DeclaringType + ":" + method?.Name;
                        if (_running.Count < 32) _running[entry.Id] = entry; else Dropped++;
                    }
            }
            private void Posted(object sender, DispatcherHookEventArgs args)
            {
                lock (_sync)
                {
                    if (args.Operation.Status == DispatcherOperationStatus.Completed || args.Operation.Status == DispatcherOperationStatus.Aborted) return;
                    if (_mode == "bounded" && _bounded.Count >= 16384) { Dropped++; return; }
                    if (_index.TryGetValue(args.Operation, out _)) return;
                    var now = Stopwatch.GetTimestamp();
                    var entry = new Entry { Id = ++_next, Posted = now, Eligible = args.Operation.Priority == DispatcherPriority.Inactive ? 0 : now,
                        Priority = args.Operation.Priority, Operation = new WeakReference(args.Operation) };
                    _index.Add(args.Operation, entry);
                    TimerEvent(args.Operation, entry, "posted");
                    if (_trace && !_producerRecorded && _events.Count < 512 && (MethodField?.GetValue(args.Operation) as Delegate)?.Method.Name == "InitTextStore")
                    {
                        _producerRecorded = true;
                        _events.Add("PRODUCER," + new StackTrace(1, false));
                    }
                    if (_mode == "legacy") _legacy.Add(entry); else _bounded.Add(entry.Id, entry);
                }
            }
            private void Finished(object sender, DispatcherHookEventArgs args)
            {
                lock (_sync)
                {
                    if (_index.TryGetValue(args.Operation, out var timerEntry)) TimerEvent(args.Operation, timerEntry, "end");
                    if (_trace && _events.Count >= 512) Dropped++;
                    if (_trace && _index.TryGetValue(args.Operation, out var timed) && timed.Started != 0 && Ms(Stopwatch.GetTimestamp() - timed.Started) >= 10 && _events.Count < 512)
                        _events.Add(string.Format(CultureInfo.InvariantCulture, "OP_TRACE,{0:F3},{1:F3},{2},{3},{4},start_ms={5:F3},end_ms={6:F3}", Ms(Stopwatch.GetTimestamp() - timed.Started),
                            AppDomain.CurrentDomain.MonitoringTotalProcessorTime.TotalMilliseconds - timed.Cpu, AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - timed.Allocated,
                            args.Operation.Priority, timed.Method, Ms(timed.Started - _origin), Ms(Stopwatch.GetTimestamp() - _origin)));
                    if (_index.TryGetValue(args.Operation, out var running)) _running.Remove(running.Id);
                    if (_mode == "legacy") _legacy.RemoveAll(entry => !entry.Operation.IsAlive || ReferenceEquals(entry.Operation.Target, args.Operation));
                    else if (_index.TryGetValue(args.Operation, out var entry)) _bounded.Remove(entry.Id);
                    _index.Remove(args.Operation);
                }
            }
            public Tuple<int, double> Snapshot(int cycle, StringBuilder text)
            {
                if (_mode == "off") return Tuple.Create(-1, -1d);
                lock (_sync)
                {
                    foreach (var value in _events) text.AppendLine(value.ToString());
                    _events.Clear();
                    var entries = (_mode == "legacy" ? _legacy.ToArray() : _bounded.Values.ToArray());
                    var pending = new List<Tuple<DispatcherOperation, Entry>>();
                    foreach (var entry in entries)
                    {
                        var op = entry.Operation.Target as DispatcherOperation;
                        if (op != null && op.Status != DispatcherOperationStatus.Completed && op.Status != DispatcherOperationStatus.Aborted)
                            pending.Add(Tuple.Create(op, entry));
                        else { _legacy.Remove(entry); _bounded.Remove(entry.Id); }
                    }
                    foreach (var group in pending.GroupBy(item => item.Item1.Priority + ":" + ((MethodField?.GetValue(item.Item1) as Delegate)?.Method.ToString() ?? "unknown")))
                        text.AppendLine(cycle + "," + group.Count() + "," + Ms(Stopwatch.GetTimestamp() - group.Min(item => item.Item2.Posted)).ToString("F3", CultureInfo.InvariantCulture) + "," + group.Key);
                    if (_roots)
                        foreach (var item in pending.Take(12))
                        {
                            var callback = MethodField?.GetValue(item.Item1) as Delegate;
                            text.AppendLine("TARGET," + cycle + "," + callback?.Method + "," + callback?.Target?.GetType().FullName);
                            var target = callback?.Target;
                            if (target?.GetType().FullName == "System.Windows.Documents.TextEditor")
                            {
                                var scope = target.GetType().GetField("_uiScope", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target) as FrameworkElement;
                                text.AppendLine("ROOT," + cycle + ",DispatcherOperation._method.Target -> TextEditor._uiScope -> " + scope?.GetType().FullName + ",loaded=" + scope?.IsLoaded);
                                var parent = scope as DependencyObject;
                                for (var depth = 0; parent != null && depth < 24; depth++)
                                {
                                    var element = parent as FrameworkElement;
                                    text.AppendLine("PARENT," + cycle + "," + depth + "," + parent.GetType().FullName + ",vm=" + element?.DataContext?.GetType().FullName);
                                    parent = LogicalTreeHelper.GetParent(parent) ?? (parent is Visual ? VisualTreeHelper.GetParent(parent) : null);
                                }
                            }
                            foreach (var field in typeof(DispatcherOperation).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(field => field.Name.StartsWith("_arg", StringComparison.Ordinal)))
                            {
                                var argument = field.GetValue(item.Item1);
                                text.AppendLine("ARG," + field.Name + "," + argument?.GetType().FullName);
                            }
                        }
                    return Tuple.Create(pending.Count, pending.Count == 0 ? 0 : Ms(Stopwatch.GetTimestamp() - pending.Min(item => item.Item2.Posted)));
                }
            }
            public void Dispose()
            {
                _watchdog?.Dispose();
                lock (_sync)
                {
                    _disposed = true;
                }
                lock (_writeSync)
                {
                    if (_timeline != null)
                    {
                        foreach (var value in _events) _timeline.WriteLine(value);
                        _timeline.WriteLine("TRACE_DROPPED," + Dropped);
                        _timeline.Dispose();
                    }
                }
                _dispatcher.Hooks.OperationPosted -= Posted;
                _dispatcher.Hooks.OperationStarted -= Started;
                _dispatcher.Hooks.OperationPriorityChanged -= PriorityChanged;
                _dispatcher.Hooks.OperationCompleted -= Finished;
                _dispatcher.Hooks.OperationAborted -= Finished;
            }
        }
    }
}
