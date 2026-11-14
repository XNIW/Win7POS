using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Win7POS.Data;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Separate bounded worker: never adds work to the historical 60s worker.
    // The Loaded observer only measures the first arranged surface; it never positions it.
    internal sealed class OperatorDialogGeometrySmoke
    {
        private static OperatorDialogGeometrySmoke _active;
        private static bool _registered;
        private readonly List<Observation> _observations = new List<Observation>();
        private readonly HashSet<Window> _observed = new HashSet<Window>();
        private readonly string _receipt;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Window _rootOwner;
        private bool _cancelSafe, _singleCommit, _traversal, _returnToOwner, _arithmetic, _resizeSafe, _userMovePreserved;

        private OperatorDialogGeometrySmoke(string data) { _receipt = Path.Combine(data, "operator-dialog-geometry.private.json"); }
        internal static async Task<string> RunAsync(string data)
        {
            TypedQaDiagnosticWriter.BeginScenario("OPERATOR_DIALOG_GEOMETRY","functional-completion");
            var probe = new OperatorDialogGeometrySmoke(data);
            if (!_registered)
            {
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler((sender, _) => { if (sender is DialogShellWindow) _active?.ObserveOpening((Window)sender, "first-layout"); }), true);
                _registered = true;
            }
            _active = probe;
            try
            {
                if (!await ArticleMutationLoopbackHarness.VerifyEmptyShopBootstrapAsync(data, geometry: probe))
                    throw new InvalidOperationException("geometry_authentic_chain_failed");
                probe._arithmetic = VerifyArithmetic();
                var ok = probe._observations.Count >= 16 && probe._observations.All(x => x.Centered && x.Contained && x.FooterVisible && x.FirstNativeCentered) &&
                    probe._cancelSafe && probe._singleCommit && probe._traversal && probe._returnToOwner && probe._arithmetic && probe._resizeSafe && probe._userMovePreserved && IsProcessDPIAware();
                TypedQaDiagnosticWriter.RecordAssertion(probe._observations.Count>=16,observationId:"geometry_matrix_complete");
                TypedQaDiagnosticWriter.RecordAssertion(probe._observations.All(x=>x.Centered&&x.Contained&&x.FooterVisible&&x.FirstNativeCentered),observationId:"geometry_all_surfaces_fit");
                TypedQaDiagnosticWriter.RecordAssertion(probe._cancelSafe,observationId:"geometry_cancel_zero_writes");
                TypedQaDiagnosticWriter.RecordAssertion(probe._singleCommit,observationId:"geometry_confirm_single_commit");
                TypedQaDiagnosticWriter.RecordAssertion(probe._traversal,observationId:"geometry_focus_traversal");
                TypedQaDiagnosticWriter.RecordAssertion(probe._returnToOwner,observationId:"geometry_return_to_owner");
                TypedQaDiagnosticWriter.RecordAssertion(probe._arithmetic,observationId:"geometry_independent_arithmetic");
                TypedQaDiagnosticWriter.RecordAssertion(probe._resizeSafe,observationId:"geometry_resize_contained");
                TypedQaDiagnosticWriter.RecordAssertion(probe._userMovePreserved,observationId:"geometry_user_move_preserved");
                TypedQaDiagnosticWriter.RecordAssertion(IsProcessDPIAware(),observationId:"geometry_process_dpi_aware");
                probe.Write(ok ? "PASS" : "FAIL");
                TypedQaDiagnosticWriter.CompleteScenario(ok);
                return (ok ? "PASS" : "FAIL") + ": operator_dialog_geometry; measurements=" + probe._observations.Count;
            }
            catch (Exception error)
            {
                probe.Write("FAIL", error.GetType().Name);
                TypedQaDiagnosticWriter.CompleteScenario(false,error);
                return "FAIL: operator_dialog_geometry; " + error.GetType().Name;
            }
            finally { _active = null; }
        }
        internal void ConfigureOwner(Window owner)
        {
            _rootOwner = owner;
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            var work = SystemParameters.WorkArea;
            owner.Width = Math.Min(1000, work.Width - 32); owner.Height = Math.Min(700, work.Height - 32);
            owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        }
        internal void ObserveOpening(Window window, string phase)
        {
            if (!_observed.Add(window)) return;
            if (phase == "first-layout") Measure(window, phase);
            window.ContentRendered += (_, __) => Measure(window, "first-render");
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) yield return typed;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        private static Rect ScreenBounds(FrameworkElement element)
        {
            var topLeft = element.PointToScreen(new Point());
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        private static bool IsFooter(Button button, FrameworkElement content)
        {
            if (button.IsCancel || button.IsDefault) return true;
            for (DependencyObject current = button; current != null && current != content; current = VisualTreeHelper.GetParent(current))
            {
                var parent = VisualTreeHelper.GetParent(current) as Grid;
                if (parent != null && parent.RowDefinitions.Count > 1 && Grid.GetRow((UIElement)current) == parent.RowDefinitions.Count - 1)
                    return ReferenceEquals(button.Style, button.TryFindResource("DialogActionButtonStyle")) ||
                        ReferenceEquals(button.Style, button.TryFindResource("DialogCancelButtonStyle"));
            }
            return false;
        }
        private void Measure(Window window, string phase)
        {
            if (_clock.Elapsed > TimeSpan.FromSeconds(55)) throw new TimeoutException("geometry_worker_deadline");
            if (!(window is DialogShellWindow shell) || shell.ActualWidth <= 0) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            var ownerHwnd = window.Owner == null ? hwnd : new WindowInteropHelper(window.Owner).Handle;
            var monitor = MonitorFromWindow(ownerHwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(hwnd, out var native)) throw new InvalidOperationException("geometry_native_query");
            var work = info.Work.ToRect();
            var surface = shell.UseModalOverlay ? Program.HarnessWindow.RequireOverlayCard(window) : window.Content as FrameworkElement;
            if (surface == null || surface.ActualWidth <= 0 || surface.ActualHeight <= 0) throw new InvalidOperationException("geometry_surface_unmeasured");
            var bounds = shell.UseModalOverlay ? ScreenBounds(surface) : native.ToRect();
            var footer = Descendants<Button>(surface).Where(x => x.Visibility == Visibility.Visible && IsFooter(x, shell.DialogContent as FrameworkElement)).ToArray();
            var footers = footer.Select(ScreenBounds).ToArray();
            var dx = bounds.Left + bounds.Width / 2 - work.Left - work.Width / 2;
            var dy = bounds.Top + bounds.Height / 2 - work.Top - work.Height / 2;
            var transform = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformToDevice;
            var firstNative = MonitorHelper.GetFirstVisibleBoundsPixels(window);
            if (!GetWindowRect(ownerHwnd, out var ownerNative)) throw new InvalidOperationException("geometry_native_owner_query");
            var firstCentered = firstNative.HasValue && Contains(work, firstNative.Value) &&
                Math.Abs(firstNative.Value.Left + firstNative.Value.Width / 2 - work.Left - work.Width / 2) <= 2 &&
                Math.Abs(firstNative.Value.Top + firstNative.Value.Height / 2 - work.Top - work.Height / 2) <= 2;
            _observations.Add(new Observation { Dialog = window.GetType().Name, Phase = phase, Work = work.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Hwnd = native.ToRect().ToString(System.Globalization.CultureInfo.InvariantCulture), Card = bounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Owner = window.Owner?.GetType().Name, OwnerPixels = ownerNative.ToRect().ToString(System.Globalization.CultureInfo.InvariantCulture),
                Monitor = monitor.ToInt64(), DpiX = 96 * transform.M11, DpiY = 96 * transform.M22,
                CenterOffsetX = dx, CenterOffsetY = dy, Centered = Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2,
                FirstHwnd = firstNative?.ToString(System.Globalization.CultureInfo.InvariantCulture), FirstNativeCentered = firstCentered,
                Contained = Contains(work, bounds), FooterVisible = footer.Length > 0 && footers.All(x => Contains(work, x) && Contains(bounds, x)) &&
                    footer.All(x => Program.HarnessWindow.IsRenderedWithin(x, surface)), FooterCount = footer.Length,
                FooterBounds = string.Join(";", footer.Select(x => Program.HarnessWindow.BoundsIn(x, surface))),
                FooterPixels = string.Join(";", footers.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) });
            Write("RUNNING");
        }
        private static bool Contains(Rect outer, Rect inner) => inner.Width > 0 && inner.Height > 0 &&
            inner.Left >= outer.Left - 1 && inner.Top >= outer.Top - 1 && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;
        internal static bool SurfaceFitsWorkArea(Window window)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            var hwnd = new WindowInteropHelper(window.Owner ?? window).Handle;
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return false;
            var surface = window is DialogShellWindow shell && shell.UseModalOverlay ? Program.HarnessWindow.RequireOverlayCard(window) : window.Content as FrameworkElement;
            if (surface == null || surface.ActualWidth <= 0 || surface.ActualHeight <= 0) return false;
            var bounds = ScreenBounds(surface); var work = info.Work.ToRect();
            return Contains(work, bounds) && Math.Abs(bounds.Left + bounds.Width / 2 - work.Left - work.Width / 2) <= 2 &&
                Math.Abs(bounds.Top + bounds.Height / 2 - work.Top - work.Height / 2) <= 2;
        }
        internal static Rect IndependentWorkAreaDip(Window window)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            var ownerHwnd = new WindowInteropHelper(window.Owner ?? window).Handle;
            if (!GetMonitorInfo(MonitorFromWindow(ownerHwnd, 2), ref info)) throw new InvalidOperationException("geometry_monitor_unavailable");
            var transform = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).CompositionTarget.TransformFromDevice;
            return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)), transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        }
        private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
        {
            while (!condition()) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); await Dispatcher.Yield(DispatcherPriority.Background); }
        }
        private static bool VerifyArithmetic()
        {
            // Literal independent expected geometry: negative monitor origin,
            // top/left taskbar, reduced work area, 1024x768 and 1024x600.
            return MonitorHelper.ConstrainToWorkArea(new Rect(1200, -500, 800, 600), new Rect(-1920, 40, 1920, 1040), true) == new Rect(-1360, 260, 800, 600) &&
                MonitorHelper.ConstrainToWorkArea(new Rect(0, 0, 1600, 1200), new Rect(0, 80, 1024, 688), true) == new Rect(0, 80, 1024, 688) &&
                MonitorHelper.ConstrainToWorkArea(new Rect(0, 0, 560, 400), new Rect(64, 0, 960, 768), true) == new Rect(264, 184, 560, 400) &&
                MonitorHelper.ConstrainToWorkArea(new Rect(900, 700, 720, 520), new Rect(0, 0, 1024, 768), false) == new Rect(304, 248, 720, 520) &&
                MonitorHelper.ConstrainToWorkArea(new Rect(0, 0, 1440, 1040), new Rect(0, 0, 1024, 600), false) == new Rect(0, 0, 1024, 600) &&
                MonitorHelper.ConstrainToWorkArea(new Rect(17, 19, 400, 300), new Rect(0, 0, 1024, 768), false) == new Rect(17, 19, 400, 300);
        }
        internal async Task VerifyWorkspaceAsync(HistoricalOnboardingDialog workspace, SqliteConnectionFactory factory, CancellationToken token)
        {
            Measure(workspace, "workspace-ready");
            for (var scenario = 0; scenario < 3; scenario++)
            {
                if (scenario == 1) _rootOwner.WindowState = WindowState.Maximized;
                if (scenario == 2)
                {
                    _rootOwner.WindowState = WindowState.Normal;
                    var work = SystemParameters.WorkArea; _rootOwner.Left = work.Right - _rootOwner.Width - 8; _rootOwner.Top = work.Bottom - _rootOwner.Height - 8;
                }
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = workspace.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    HistoricalSalesImportDialog import = null;
                    try
                    {
                        await UntilAsync(() => workspace.OwnedWindows.OfType<HistoricalSalesImportDialog>().Any(), token);
                        import = workspace.OwnedWindows.OfType<HistoricalSalesImportDialog>().Single();
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle); import.UpdateLayout(); Measure(import, "import-empty-" + scenario);
                        import.Activate(); import.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                        var first = Keyboard.FocusedElement;
                        (first as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        var next = Keyboard.FocusedElement;
                        (next as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                        _traversal |= first != null && first != next && Keyboard.FocusedElement == first;
                        if (scenario == 0)
                        {
                            await VerifyImportAsync(import, factory, token);
                            var previousLanguage = PosLocalization.Current.CurrentLanguage;
                            try
                            {
                                foreach (var language in new[] { "en", "it", "es", "zh" })
                                {
                                    PosLocalization.Current.SetLanguage(language);
                                    await ShowMessageAsync(import, new LanguageSettingsDialog(), token);
                                    await ShowMessageAsync(import, new ApplyConfirmDialog("QA", string.Join("\n", Enumerable.Repeat("Synthetic confirmation details.", 80))), token);
                                }
                            }
                            finally { PosLocalization.Current.SetLanguage(previousLanguage); }
                            var service = (PosWorkflowService)typeof(HistoricalOnboardingDialog).GetField("_service", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(workspace);
                            var session = (OperatorSession)OperatorSessionHolder.Current;
                            using (var report = new DailyReportViewModel(service, permissions: new PermissionService(session), session: session, allowPrinting: false))
                            {
                                report.DateText = "2000-01-02"; report.ReportOriginIndex = 2; await report.ReloadAnalyticalAsync();
                                await ShowMessageAsync(import, new DailyReportDialog(report), token);
                            }
                            await VerifyMoveAndResizeAsync(import, token);
                        }
                        import.Close(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        _returnToOwner |= workspace.IsEnabled && workspace.IsVisible;
                        done.TrySetResult(true);
                    }
                    catch (Exception error) { done.TrySetException(error); }
                    finally { if (import?.IsVisible == true) import.Close(); }
                }));
                ((Button)workspace.FindName("ImportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await done.Task;
                await UntilAsync(() => ((Button)workspace.FindName("ImportButton")).IsEnabled, token);
            }
        }
        private async Task VerifyImportAsync(HistoricalSalesImportDialog dialog, SqliteConnectionFactory factory, CancellationToken token)
        {
            var model = (HistoricalSalesImportViewModel)dialog.DataContext;
            var sources = new[] { "synthetic-legacy.xlsx", "synthetic-modern.xlsx" }.Select(x => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", "historical-sales-getnet-v1", x)).ToArray();
            model.SelectFilesForTesting=owner=>sources;
            await InvokeImportCommandAsync(dialog,model.SelectCommand,token);
            await InvokeImportCommandAsync(dialog,model.AnalyzeCommand,token);
            await InvokeImportCommandAsync(dialog,model.PreviewCommand,token);
            Require(model.ApplyCommand.CanExecute(null),"geometry_qualified_review_ready");
            Measure(dialog, "import-analysis-review");
            Program.HarnessWindow.SaveVisual(dialog.Content as FrameworkElement,
                Path.Combine(Path.GetDirectoryName(_receipt), "operator-import-loaded-render.png"));
            for (var confirm = 0; confirm < 2; confirm++)
            {
                var accept = confirm == 1; var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = dialog.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await UntilAsync(() => dialog.OwnedWindows.OfType<ApplyConfirmDialog>().Any(), token);
                        var popup = dialog.OwnedWindows.OfType<ApplyConfirmDialog>().Single();
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle); Measure(popup, accept ? "confirm-once" : "cancel");
                        if (accept)
                        {
                            if (model.ApplyCommand.CanExecute(null)) throw new InvalidOperationException("geometry_confirm_reentry_enabled");
                            model.ApplyCommand.Execute(null);
                        }
                        Descendants<Button>(popup).Single(x => accept ? x.IsDefault : x.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        done.TrySetResult(true);
                    }
                    catch (Exception error) { done.TrySetException(error); }
                }));
                await InvokeImportCommandAsync(dialog,model.ApplyCommand,token); await done.Task;
                // The modal frame can close before ApplyAsync starts its busy
                // operation. Wait for its terminal model state, not merely the
                // confirmation click or an initially false IsBusy.
                await UntilAsync(()=>!model.IsBusy&&(accept
                    ?model.ImportStepText==PosLocalization.T("history.stepResult")
                    :model.ApplyCommand.CanExecute(null)),token);
                using (var c = factory.Open())
                {
                    var operations = c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation");
                    if (!accept) _cancelSafe = operations == 0;
                    else
                    {
                        var active=c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1");
                        TypedQaDiagnosticWriter.RecordAssertion(operations==1,1L,operations,observationId:"geometry_confirm_operation_count");
                        TypedQaDiagnosticWriter.RecordAssertion(active==5,5L,active,observationId:"geometry_confirm_active_count");
                        _singleCommit = operations == 1 && active == 5;
                    }
                }
            }
            await ShowMessageAsync(dialog, new ModernMessageDialog("QA receipt", string.Join("\n", Enumerable.Repeat("Synthetic receipt detail for layout verification.", 100))), token);
            await ShowMessageAsync(dialog, new ModernMessageDialog("QA error", "Synthetic error and return to owner."), token);
            Measure(dialog, "return-to-import");
        }
        private static void Require(bool condition,string code,[CallerFilePath]string sourceFile="",[CallerLineNumber]int sourceLine=0)
        {
            TypedQaDiagnosticWriter.RecordAssertion(condition,sourceFile:sourceFile,sourceLine:sourceLine,observationId:code);
            if(!condition)throw new InvalidOperationException(code);
        }
        private static async Task InvokeImportCommandAsync(FrameworkElement view,ICommand command,CancellationToken token)
        {
            var button=Descendants<Button>(view).Single(x=>ReferenceEquals(x.Command,command));
            button.BringIntoView();await Dispatcher.Yield(DispatcherPriority.ContextIdle);view.UpdateLayout();
            Require(button.IsVisible&&button.IsEnabled&&command.CanExecute(null),"geometry_import_control_enabled");
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            await UntilAsync(()=>!(view.DataContext as HistoricalSalesImportViewModel).IsBusy,token);
        }
        private async Task ShowMessageAsync(Window owner, Window popup, CancellationToken token)
        {
            popup.Owner = owner;
            var done = new TaskCompletionSource<bool>();
            _ = owner.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await UntilAsync(() => popup.IsVisible, token); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Measure(popup, "message-ready"); popup.Close(); done.TrySetResult(true); }
                catch (Exception error) { popup.Close(); done.TrySetException(error); }
            }));
            popup.ShowDialog(); await done.Task;
        }
        private async Task VerifyMoveAndResizeAsync(Window dialog, CancellationToken token)
        {
            var left = dialog.Left; var top = dialog.Top; var width = dialog.Width; var height = dialog.Height;
            try
            {
                var work = SystemParameters.WorkArea;
                dialog.Left = work.Left + 30; dialog.Top = work.Top + 30;
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                _userMovePreserved = Math.Abs(dialog.Left - work.Left - 30) < 2 && Math.Abs(dialog.Top - work.Top - 30) < 2;
                await ShowMessageAsync(dialog, new ModernMessageDialog("QA nested", "Small owner at top/left edges."), token);
                dialog.Left = work.Right - dialog.ActualWidth - 30; dialog.Top = work.Bottom - dialog.ActualHeight - 30;
                await ShowMessageAsync(dialog, new ModernMessageDialog("QA nested", "Small owner at bottom/right edges."), token);
                var hwnd = new WindowInteropHelper(dialog).Handle;
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) throw new InvalidOperationException("geometry_resize_monitor");
                // Explicit NOMOVE regression uses the supported native
                // test interop, restricted to this owned synthetic HWND.
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, info.Work.Right - info.Work.Left + 300,
                    info.Work.Bottom - info.Work.Top + 300, 0x0016); // NOMOVE, NOZORDER, NOACTIVATE
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                _resizeSafe = GetWindowRect(hwnd, out var resized) && Contains(info.Work.ToRect(), resized.ToRect());
            }
            finally { dialog.Width = width; dialog.Height = height; dialog.Left = left; dialog.Top = top; }
        }
        private static string Hash(string path) { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static string ReadAwareness()
        {
            try { using (var process = Process.GetCurrentProcess()) { return GetProcessDpiAwareness(process.Handle, out var value) == 0 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "query_failed"; } }
            catch (DllNotFoundException) { return "unavailable_on_platform"; }
            catch (EntryPointNotFoundException) { return "unavailable_on_platform"; }
        }
        private void Write(string status, string failure = null) => HistoricalSalesQaReceipt.WriteJson(_receipt,
            new Receipt { Status = status, Failure = failure, DurationMs = _clock.Elapsed.TotalMilliseconds, ProcessDpiAware = IsProcessDPIAware(),
                CancelZeroWrites = _cancelSafe, ConfirmOnce = _singleCommit, FocusTraversal = _traversal, OwnerReturn = _returnToOwner,
                Arithmetic = _arithmetic, ResizeContained = _resizeSafe, UserMovePreserved = _userMovePreserved,
                WpfSha256 = Hash(typeof(DialogShellWindow).Assembly.Location), HarnessSha256 = Hash(typeof(Program).Assembly.Location),
                Os = Environment.OSVersion.VersionString, AwarenessKind = ReadAwareness(), Observations = _observations });
        [DataContract] private sealed class Receipt
        {
            [DataMember] public string Schema = "win7pos-operator-dialog-geometry-v1";
            [DataMember] public string Units = "Work/Hwnd/Card/FirstHwnd/FooterPixels: physical px; FooterBounds: surface-relative DIP";
            [DataMember] public string WpfSha256, HarnessSha256, Os, AwarenessKind;
            [DataMember] public string Status, Failure;
            [DataMember] public double DurationMs;
            [DataMember] public bool ProcessDpiAware, CancelZeroWrites, ConfirmOnce, FocusTraversal, OwnerReturn, Arithmetic, ResizeContained, UserMovePreserved;
            [DataMember] public List<Observation> Observations;
        }
        [DataContract] private sealed class Observation
        {
            [DataMember] public string Dialog, Phase, Owner, OwnerPixels, Work, Hwnd, Card, FirstHwnd, FooterBounds, FooterPixels;
            [DataMember] public long Monitor;
            [DataMember] public double DpiX, DpiY, CenterOffsetX, CenterOffsetY;
            [DataMember] public bool Centered, Contained, FooterVisible, FirstNativeCentered;
            [DataMember] public int FooterCount;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public Rect ToRect() => new Rect(Left, Top, Right - Left, Bottom - Top); }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool IsProcessDPIAware();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);
    }
}
