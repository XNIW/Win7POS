using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Products.Images;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class CartQualificationSmoke
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        private static int Containers(ItemsControl control) => Enumerable.Range(0, control.Items.Count).Count(index => control.ItemContainerGenerator.ContainerFromIndex(index) != null);
        [System.Runtime.InteropServices.DllImport("UIAutomationCore.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool UiaClientsAreListening();

        internal static async Task RunAsync(string directory, int products, int minutes)
        {
            var previousOperator = OperatorSessionHolder.Current;
            OperatorSessionHolder.Current = await CartPerformanceRegressionSmoke.CreateQaOperatorAsync();
            using var process = Process.GetCurrentProcess();
            using var observer = new BoundedDispatcherObservation(Dispatcher.CurrentDispatcher,
                Environment.GetEnvironmentVariable("WIN7POS_QA_PERF_OBSERVER_OFF") != "1");
            var traceEnabled = Environment.GetEnvironmentVariable("WIN7POS_QA_PERF_TRACE") == "1";
            if (traceEnabled) AppDomain.MonitoringIsEnabled = true;
            using var trace = new CartPerformanceDiagnostics.OperationObserver(Dispatcher.CurrentDispatcher,
                traceEnabled ? "bounded" : "off", true, traceEnabled);
            trace.StartTimeline(Path.Combine(directory, "diagnostic-timeline.txt"));
            var scanLimitText = Environment.GetEnvironmentVariable("WIN7POS_QA_PERF_SCAN_LIMIT") ?? "0";
            if (!int.TryParse(scanLimitText, out var scanLimit) || scanLimit < 0 || scanLimit > 5)
                throw new ArgumentException("diagnostic_scan_limit_invalid");
            var inputDispatch = Environment.GetEnvironmentVariable("WIN7POS_QA_PERF_INPUT_DISPATCH") == "1";
            if (inputDispatch && scanLimit == 0) throw new ArgumentException("input_dispatch_requires_short_diagnostic");
            var completedScans = 0;
            using var environment = new PerformanceEnvironment(directory);
            using var scans = new StreamWriter(Path.Combine(directory, "qualification-scans.csv"));
            using var idle = new StreamWriter(Path.Combine(directory, "qualification-idle.csv"));
            using var operations = new StreamWriter(Path.Combine(directory, "qualification-operations.txt"));
            scans.WriteLine("cycle,mode,ordinal,service_ms,ui_return_ms,apply_ms,layout_ms,bitmap_ms,command_overhead_ms,gate_wait_ms,worker_queue_ms,product_lookup_update_ms,snapshot_query_map_ms,snapshot_projection_ms,realized_rows,realized_grid,collection_changes,row_notifications,product_commands,commands_on_dispatcher,focus_scroll_wait_ms");
            idle.WriteLine("cycle,elapsed_s,private_bytes,managed_bytes,pending,oldest_ms,focus_pending,focus_oldest_ms,scroll_pending,scroll_oldest_ms,closed_rooted_windows,cache_bytes,input_ms,render_ms,databind_ms,background_ms,observer_dropped,environment_valid,suspended,inactive_pending,oldest_posted_ms,handles,threads,gc0,gc1,gc2,metadata_entries,peak_observed_dispatcher,focus_peak,scroll_peak,focus_maximum_wait_ms,scroll_maximum_wait_ms");
            var view = new PosView();
            var vm = (PosViewModel)view.DataContext;
            var host = new Window { Width = 1024, Height = 768, Content = view, ShowInTaskbar = false };
            var loaded = false;
            view.Loaded += (_, __) => loaded = true;
            Application.Current.MainWindow = host;
            var service = (PosWorkflowService)typeof(PosViewModel).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vm);
            var session = (PosSession)typeof(PosWorkflowService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            var changes = 0;
            var notifications = 0;
            try
            {
                host.Show();
                await CartPerformanceRegressionSmoke.WaitAsync(() => loaded && !vm.IsBusy, "qualification view initialization", 10000);
                // Fixture population is setup, not scan work. Subsequent cycles
                // retain line identities; reset stress is a separate reproducer.
                session.ReplaceWithLines(Enumerable.Range(1, 500).Select(index => new RestoredLine
                { ProductId = index, Barcode = "P" + index.ToString("D8"), Name = "Product " + index, UnitPrice = 1000, Quantity = 1 }).ToList());
                vm.ApplyDiscountSnapshot(await service.GetSnapshotAsync());
                vm.CartItems.CollectionChanged += (_, __) => changes++;
                foreach (var row in vm.CartItems) row.PropertyChanged += (_, __) => notifications++;
                var rows = (ListBox)view.FindName("CartListBox");
                var grid = (ListBox)view.FindName("CartGridListBox");
                var cycle = 0;
                var usefulStart = environment.AwakeSeconds;
                if (traceEnabled) trace.Checkpoint("fixture_ready;cart=" + vm.CartItems.Count + ";uia_listening=" + UiaClientsAreListening() +
                    ";context=" + System.Threading.SynchronizationContext.Current?.GetType().FullName);
                do
                {
                    if (vm.CartItems.Count != 500) throw new InvalidOperationException("qualification_cart_size_mismatch");
                    var modes = cycle % 2 == 0 ? new[] { CartViewMode.Rows, CartViewMode.Grid } : new[] { CartViewMode.Grid, CartViewMode.Rows };
                    foreach (var mode in modes)
                    {
                        await vm.SetCartViewModeAsync(mode);
                        for (var ordinal = 1; ordinal <= 10; ordinal++)
                        {
                            changes = notifications = 0;
                            var expectedQuantity = vm.CartItems[0].Quantity + 1;
                            vm.BarcodeInput = "P00000001";
                            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            var sawBusy = false;
                            System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
                            {
                                if (args.PropertyName != nameof(vm.IsBusy)) return;
                                if (vm.IsBusy) sawBusy = true;
                                else if (sawBusy) finished.TrySetResult(true);
                            };
                            vm.PropertyChanged += handler;
                            using var sql = SqliteWorkMetrics.Begin();
                            using var detail = PosScanMeasurement.Begin();
                            var start = Stopwatch.GetTimestamp();
                            DispatcherOperation commandDispatch = null;
                            try
                            {
                                if (traceEnabled) trace.Checkpoint("command_start;cycle=" + cycle + ";mode=" + mode + ";scan=" + ordinal +
                                    ";expected_qty=" + expectedQuantity + ";focus=" + Keyboard.FocusedElement?.GetType().FullName);
                                if (inputDispatch)
                                {
                                    // One-factor Diagnostic comparison only. The enqueue,
                                    // execution and completion all stay inside the scan timer.
                                    commandDispatch = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
                                    {
                                        try { ExecutePublicScan(vm, traceEnabled ? trace : null); }
                                        catch (Exception error) { finished.TrySetException(error); }
                                    }, DispatcherPriority.Input);
                                }
                                else ExecutePublicScan(vm, traceEnabled ? trace : null);
                                if (await Task.WhenAny(finished.Task, Task.Delay(10000)) != finished.Task) throw new TimeoutException("qualification_public_scan_timeout");
                                await finished.Task;
                            }
                            finally
                            {
                                vm.PropertyChanged -= handler;
                                if (commandDispatch?.Status == DispatcherOperationStatus.Pending) commandDispatch.Abort(); // Only our unstarted command.
                            }
                            // Return control at Input between public scans, as
                            // actual input delivery does. A chain of higher-priority
                            // async continuations otherwise postpones the app's
                            // focus/scroll until after the entire synthetic batch.
                            // This wait remains INCLUDED in command_overhead_ms.
                            if (traceEnabled) trace.Checkpoint("busy_complete;qty=" + vm.CartItems[0].Quantity + ";service_complete=" + detail.ServiceCompletedTimestamp +
                                ";service_ms=" + detail.ServiceMilliseconds.ToString("F3", Invariant));
                            if (vm.CartItems[0].Quantity != expectedQuantity || detail.ServiceCompletedTimestamp == 0)
                                throw new InvalidOperationException("qualification_public_scan_not_applied");
                            if (traceEnabled) trace.Checkpoint("input_probe_start");
                            var visualWait = await ProbeAsync(DispatcherPriority.Input);
                            if (double.IsInfinity(visualWait))
                            {
                                // Failure-only evidence: preserve the pending producers
                                // and native queue without changing or draining WPF work.
                                var failure = observer.Snapshot();
                                operations.WriteLine("VISUAL_SCAN_TIMEOUT," + cycle + "," + mode + "," + ordinal + "," + failure.Detail);
                                var native = new StringBuilder();
                                trace.Snapshot(cycle, native);
                                if (traceEnabled) native.AppendLine("TRACE_DROPPED," + trace.Dropped);
                                CartPerformanceDiagnostics.RecordNativeQueue(cycle, "visual_timeout", native);
                                operations.Write(native);
                                scans.Flush(); operations.Flush(); environment.Sample(host); environment.Flush();
                                throw new TimeoutException("qualification_visual_scan_timeout");
                            }
                            if (traceEnabled) trace.Checkpoint("input_probe_complete;wait_ms=" + visualWait.ToString("F3", Invariant));
                            var returned = Stopwatch.GetTimestamp();
                            view.UpdateLayout();
                            var layout = Stopwatch.GetTimestamp();
                            var bitmap = new RenderTargetBitmap(1024, 768, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(view);
                            var rendered = Stopwatch.GetTimestamp();
                            var uiReturn = Math.Max(0, Milliseconds(detail.ApplyStartedTimestamp - detail.ServiceCompletedTimestamp));
                            var apply = detail["apply_snapshot"];
                            var overhead = Math.Max(0, Milliseconds(returned - start) - detail.ServiceMilliseconds - uiReturn - apply);
                            scans.WriteLine(string.Join(",", new object[] { cycle, mode, ordinal,
                                detail.ServiceMilliseconds.ToString("F3", Invariant), uiReturn.ToString("F3", Invariant), apply.ToString("F3", Invariant),
                                Milliseconds(layout - returned).ToString("F3", Invariant), Milliseconds(rendered - layout).ToString("F3", Invariant), overhead.ToString("F3", Invariant),
                                detail["gate_wait"].ToString("F3", Invariant), detail["worker_queue"].ToString("F3", Invariant), detail["product_lookup_update"].ToString("F3", Invariant),
                                detail["snapshot_query_map"].ToString("F3", Invariant), detail["snapshot_projection"].ToString("F3", Invariant),
                                Containers(rows), Containers(grid), changes, notifications, sql.ProductCommands, sql.ProductCommandsOnCallingThread, visualWait.ToString("F3", Invariant) }));
                            environment.Sample(host);
                            if (traceEnabled) trace.Checkpoint("scan_complete;cycle=" + cycle + ";scan=" + ordinal + ";rows=" + Containers(rows) + ";grid=" + Containers(grid));
                            completedScans++;
                            if (scanLimit > 0 && completedScans == scanLimit)
                            {
                                scans.Flush(); operations.Flush(); environment.Flush();
                                File.WriteAllText(Path.Combine(directory, "diagnostic-scans.json"),
                                    "{\"schemaVersion\":\"win7pos-short-public-scan-v1\",\"completedScans\":" + completedScans +
                                    ",\"environmentValid\":" + (environment.Valid ? "true" : "false") + ",\"qualified\":false}");
                                return;
                            }
                        }
                    }
                    var discount = new DiscountDialog(null, true, service, vm, 100, () => Task.FromResult(false)) { Owner = DialogOwnerHelper.GetSafeOwner() };
                    discount.Show(); discount.UpdateLayout(); discount.Close();
                    var imageResult = await ProductImageUiWpfSmoke.RunAsync(Path.Combine(directory, "images"));
                    if (!imageResult.StartsWith("PASS", StringComparison.Ordinal)) throw new InvalidOperationException(imageResult);
                    // Normal QA workflow returns from its auxiliary windows to
                    // its cart once. A lost foreground during idle is recorded,
                    // never repeatedly overridden to conceal user interaction.
                    host.Activate();
                    for (var second = 0; second < 20; second++)
                    {
                        if (traceEnabled) trace.Checkpoint("idle_sample_begin;cycle=" + cycle + ";second=" + second);
                        environment.Sample(host);
                        if (traceEnabled) trace.Checkpoint("idle_sample_end;cycle=" + cycle + ";second=" + second);
                        trace.SampleTimers("idle");
                        await Task.Delay(1000);
                    }
                    if (traceEnabled) trace.Checkpoint("final_sample_begin;cycle=" + cycle);
                    environment.Sample(host);
                    if (traceEnabled) trace.Checkpoint("final_sample_end;cycle=" + cycle);
                    trace.SampleTimers("before_snapshot");
                    var state = observer.Snapshot(); // Before probes can help work progress.
                    if (traceEnabled) trace.Checkpoint("snapshot_end;cycle=" + cycle + ";oldest_ms=" + state.OldestMs.ToString("F3", Invariant));
                    var input = await ProbeAsync(DispatcherPriority.Input);
                    var render = await ProbeAsync(DispatcherPriority.Render);
                    var binding = await ProbeAsync(DispatcherPriority.DataBind);
                    var background = await ProbeAsync(DispatcherPriority.Background);
                    process.Refresh();
                    var metadata = (IDictionary)typeof(PosViewModel).GetField("_cartProductImageCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vm);
                    idle.WriteLine(string.Join(",", new object[] { cycle, (environment.AwakeSeconds - usefulStart).ToString("F3", Invariant),
                        process.PrivateMemorySize64, GC.GetTotalMemory(false), state.Pending, state.OldestMs.ToString("F3", Invariant),
                        state.FocusPending, state.FocusOldestMs.ToString("F3", Invariant), state.ScrollPending, state.ScrollOldestMs.ToString("F3", Invariant),
                        state.ClosedRootedWindows, CachePixelBytes(), input.ToString("F3", Invariant), render.ToString("F3", Invariant), binding.ToString("F3", Invariant), background.ToString("F3", Invariant),
                        observer.Dropped, environment.Valid ? 1 : 0, environment.Suspended ? 1 : 0, state.Inactive, state.OldestPostedMs.ToString("F3", Invariant),
                        process.HandleCount, process.Threads.Count, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), metadata.Count, observer.PeakObserved, state.FocusPeak, state.ScrollPeak, state.FocusMaximumWaitMs.ToString("F3", Invariant), state.ScrollMaximumWaitMs.ToString("F3", Invariant) }));
                    operations.WriteLine(cycle + "," + state.Detail);
                    if (observer.Dropped != 0) operations.WriteLine("OBSERVER_OVERFLOW," + observer.OverflowDetail);
                    scans.Flush(); idle.Flush(); operations.Flush(); environment.Flush();
                    cycle++;
                } while (environment.AwakeSeconds - usefulStart < minutes * 60);
                File.WriteAllText(Path.Combine(directory, "qualification-measurement.json"), "{\"schemaVersion\":\"win7pos-performance-measurement-v1\",\"measurementCompleted\":true,\"environmentValid\":" +
                    (environment.Valid ? "true" : "false") + ",\"products\":" + products + ",\"cartSize\":" + vm.CartItems.Count + ",\"protocolVersion\":3,\"cycles\":" + cycle + ",\"stabilityEvaluatedByHarness\":false}");
            }
            finally { host.Close(); vm.Dispose(); Application.Current.MainWindow = null; OperatorSessionHolder.Current = previousOperator; }
        }

        private static void ExecutePublicScan(PosViewModel vm, CartPerformanceDiagnostics.OperationObserver trace)
        {
            if (trace != null)
            {
                var context = System.Threading.SynchronizationContext.Current;
                var priority = context?.GetType().GetField("_priority", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context);
                trace.Checkpoint("command_execute;context=" + context?.GetType().FullName + ";context_priority=" + (priority ?? "unavailable"));
            }
            if (!vm.AddBarcodeCommand.CanExecute(null)) throw new InvalidOperationException("qualification_scan_command_disabled");
            vm.AddBarcodeCommand.Execute(null);
        }

        private static async Task<double> ProbeAsync(DispatcherPriority priority)
        {
            var watch = Stopwatch.StartNew();
            var latency = 0d;
            var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => latency = watch.Elapsed.TotalMilliseconds, priority);
            await Task.WhenAny(operation.Task, Task.Delay(250));
            if (operation.Status == DispatcherOperationStatus.Completed) return latency;
            operation.Abort(); // Only this probe, never WPF work.
            return double.PositiveInfinity; // Explicit timeout: never an acceptable latency sample.
        }

        private static long CachePixelBytes()
        {
            var decoder = typeof(ProductImageRuntime).GetField("Decoder", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var type = decoder.GetType();
            var gate = type.GetField("_memoryGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(decoder);
            var bytes = 0L;
            lock (gate)
            {
                var memory = (IDictionary)type.GetField("_memory", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(decoder);
                foreach (var value in memory.Values)
                {
                    var weak = (WeakReference)value.GetType().GetField("Image", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
                    if (weak.Target is BitmapSource bitmap)
                        bytes += ((bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 31L) / 32L * 4L) * bitmap.PixelHeight;
                }
            }
            return bytes;
        }
    }
}
