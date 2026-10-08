using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Win7POS.Core;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Displays;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.CustomerDisplay;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class CustomerDisplayCompletionSmoke
    {
        internal static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "Win7POS-display-completion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var outcomes = new List<string>();
            try
            {
                await CheckAsync(outcomes, "R2_full_startup_AutoOpen_false", () => CheckStartupAsync(root, false));
                await CheckAsync(outcomes, "R2_full_startup_AutoOpen_true", () => CheckStartupAsync(root, true));
                await CheckAsync(outcomes, "R2_preview_and_explicit_settings_open_intent", () => CheckPreviewAndSettingsAsync(root));
                await CheckAsync(outcomes, "W5_manual_minimize_restore_without_snapshot", () => CheckMinimizeAsync(root));
                await CheckAsync(outcomes, "W6_snapshot_before_and_after_reconnect_debounce", () => CheckReconnectAsync(root, false, true));
                await CheckAsync(outcomes, "W6_reopen_off_debounce_before_snapshot", () => CheckReconnectAsync(root, false, false));
                await CheckAsync(outcomes, "W6_reopen_on_debounce_before_snapshot", () => CheckReconnectAsync(root, true, false));
                await CheckAsync(outcomes, "W6_reopen_on_snapshot_before_debounce", () => CheckReconnectAsync(root, true, true));
                await CheckAsync(outcomes, "display_close_disable_lock_cart_focus", () => CheckLifecycleAsync(root));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
            File.WriteAllLines(Path.Combine(AppPaths.DataDirectory, "display-completion.txt"), outcomes);
            var failures = outcomes.Where(outcome => outcome.StartsWith("FAIL ", StringComparison.Ordinal)).ToArray();
            Require(failures.Length == 0, string.Join("; ", failures));
        }

        private static async Task CheckStartupAsync(string root, bool autoOpen)
        {
            var topology = new FixtureTopology();
            using (var manager = await CreateAsync(root, topology, false, autoOpen))
            using (var pos = new PosViewModel())
            {
                var scanner = new TextBox();
                var cashier = new Window { DataContext = pos, Content = scanner, Width = 280, Height = 140, ShowInTaskbar = false };
                var failures = new List<string>();
                Action<string> checkOpening = stage =>
                {
                    if (manager.IsOpen != autoOpen) failures.Add(stage + ": IsOpen=" + manager.IsOpen + " expected=" + autoOpen);
                };
                try
                {
                    cashier.Show();
                    cashier.Activate();
                    scanner.Focus();
                    Keyboard.Focus(scanner);
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                    Require(ReferenceEquals(Keyboard.FocusedElement, scanner), "startup scanner fixture did not receive focus");
                    checkOpening("InitializeAsync");
                    Publish(pos, "cart at attach");
                    manager.Attach(pos);
                    checkOpening("Attach(current snapshot)");
                    Publish(pos, "cart before first open");
                    checkOpening("subsequent PosViewModel snapshot");
                    await TopologyChangeAsync(manager, topology, true);
                    checkOpening("topology debounce before first open");
                    manager.SetOperatorLocked(true);
                    checkOpening("lock before first open");
                    manager.SetOperatorLocked(false);
                    checkOpening("unlock before first open");
                    manager.SetCashierMinimized(true);
                    manager.SetCashierMinimized(false);
                    checkOpening("minimize/restore before first open");

                    if (!autoOpen) manager.OpenDisplay();
                    Require(manager.IsOpen && ViewModel(manager).Lines.Single().Name == "cart before first open", "first open did not render the latest stored PosViewModel snapshot");
                    Publish(pos, "cart after first open");
                    Require(ViewModel(manager).Lines.Single().Name == "cart after first open", "opened display ignored PosViewModel snapshot");
                    var display = DisplayWindow(manager);
                    manager.SetCashierMinimized(true);
                    Publish(pos, "cart updated while minimized");
                    manager.SetCashierMinimized(false);
                    Require(manager.IsOpen && ReferenceEquals(display, DisplayWindow(manager)) && ViewModel(manager).Lines.Single().Name == "cart updated while minimized", "full startup path did not restore the same display with current content");
                    Require(ReferenceEquals(Keyboard.FocusedElement, scanner), "startup/open/snapshot/restore stole scanner focus");
                    Require(failures.Count == 0, string.Join(", ", failures));
                }
                finally { cashier.Close(); }
            }
        }

        private static async Task CheckPreviewAndSettingsAsync(string root)
        {
            var topology = new FixtureTopology();
            using (var manager = await CreateAsync(root, topology, false))
            using (var pos = new PosViewModel())
            {
                manager.Attach(pos);
                manager.Preview(manager.Settings);
                Require(manager.IsOpen && manager.IsPreviewActive, "explicit preview did not open before a runtime opening request");
                Publish(pos, "cart during preview");
                Require(ViewModel(manager).Lines.All(line => line.Name != "cart during preview"), "preview displayed runtime cart");
                manager.StopPreview();
                Require(!manager.IsOpen, "preview enabled a previously unopened runtime display");
                Publish(pos, "cart after preview");
                Require(!manager.IsOpen, "snapshot after preview created runtime opening intent");
                await manager.SaveAndApplyAsync(manager.Settings);
                Require(manager.IsOpen && ViewModel(manager).Lines.Single().Name == "cart after preview", "explicit settings apply no longer opens enabled display");
                manager.CloseDisplay();
                manager.Preview(manager.Settings);
                manager.StopPreview();
                Publish(pos, "cart after closed preview");
                Require(!manager.IsOpen, "preview forgot explicit runtime close");
            }
        }

        private static async Task CheckMinimizeAsync(string root)
        {
            var topology = new FixtureTopology();
            using (var manager = await CreateAsync(root, topology, false))
            {
                Require(!manager.IsOpen, "AutoOpen=false opened at initialization");
                manager.OpenDisplay();
                Publish(manager, Cart("before minimize"));
                var window = DisplayWindow(manager);
                manager.SetCashierMinimized(true);
                Require(!manager.IsOpen, "minimize did not hide the display");
                manager.SetCashierMinimized(true);
                manager.SetCashierMinimized(false);
                Require(manager.IsOpen && ReferenceEquals(window, DisplayWindow(manager)),
                    "manually opened display was not restored without a snapshot (AutoOpen=false)");
                Require(ViewModel(manager).Lines.Single().Name == "before minimize", "restore lost visible cart");

                manager.SetCashierMinimized(true);
                Publish(manager, Cart("updated while minimized"));
                manager.SetCashierMinimized(false);
                Require(ViewModel(manager).Lines.Single().Name == "updated while minimized", "restore missed hidden updates");
                manager.SetCashierMinimized(true);
                await TopologyChangeAsync(manager, topology, false);
                Require(!manager.IsOpen, "missing monitor opened while minimized");
                await TopologyChangeAsync(manager, topology, true);
                manager.SetCashierMinimized(false);
                Require(!manager.IsOpen, "minimize restore bypassed forbidden reconnect");
            }
        }

        private static async Task CheckReconnectAsync(string root, bool reopen, bool snapshotBeforeDebounce)
        {
            var topology = new FixtureTopology();
            using (var manager = await CreateAsync(root, topology, reopen))
            {
                manager.OpenDisplay();
                Publish(manager, Cart("before unplug"));
                await TopologyChangeAsync(manager, topology, false);
                Require(!manager.IsOpen && Flag(manager, "_monitorWasMissing"), "unplug did not close and record missing monitor");
                topology.Connected = true;
                NotifyTopology(manager);
                await Task.Delay(50);
                var failures = new List<string>();
                if (snapshotBeforeDebounce)
                {
                    Publish(manager, Cart("before reconnect debounce"));
                    if (manager.IsOpen != reopen) failures.Add("snapshot before 750ms reopened despite ReopenWhenMonitorReturns=false");
                }
                await Task.Delay(950);
                if (manager.IsOpen != reopen) failures.Add("debounce visibility disagrees with reopen policy");
                Publish(manager, Cart("after reconnect debounce"));
                if (manager.IsOpen != reopen) failures.Add("snapshot after debounce disagrees with reopen policy");
                if (manager.IsOpen && ViewModel(manager).Lines.Single().Name != "after reconnect debounce")
                    failures.Add("visible display ignores new cart after debounce");
                if (Flag(manager, "_manuallyClosed")) failures.Add("monitor policy was conflated with operator close");
                if (Flag(manager, "_monitorWasMissing")) failures.Add("present monitor still marked missing");
                Require(failures.Count == 0, string.Join(", ", failures));
                if (!reopen)
                {
                    Publish(manager, CustomerDisplayProjection.Completed(Cart("completed while blocked"), 1000, 1000, 0, DateTimeOffset.UtcNow));
                    await Task.Delay(1200);
                    Require(!manager.IsOpen, "completion timer bypassed forbidden reopen");
                    manager.SetOperatorLocked(true);
                    Require(!manager.IsOpen, "operator lock bypassed forbidden reopen");
                    manager.OpenDisplay();
                    Require(manager.IsOpen && !ViewModel(manager).HasLines, "manual reopen did not show lock state");
                    manager.SetOperatorLocked(false);
                    Publish(manager, Cart("manual reopen current cart"));
                    Require(ViewModel(manager).Lines.Single().Name == "manual reopen current cart", "manual reopen retained stale content");
                }
            }
        }

        private static async Task CheckLifecycleAsync(string root)
        {
            var topology = new FixtureTopology();
            using (var manager = await CreateAsync(root, topology, true))
            {
                var scanner = new TextBox();
                var cashier = new Window { Content = scanner, Width = 280, Height = 140, ShowInTaskbar = false };
                try
                {
                    cashier.Show();
                    cashier.Activate();
                    scanner.Focus();
                    Keyboard.Focus(scanner);
                    await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                    Require(ReferenceEquals(Keyboard.FocusedElement, scanner), "scanner fixture did not receive focus");
                    manager.OpenDisplay();
                    Publish(manager, Cart("scanner cart"));
                    manager.SetCashierMinimized(true);
                    manager.SetCashierMinimized(false);
                    Require(ReferenceEquals(Keyboard.FocusedElement, scanner), "display opening/restoration stole scanner focus");
                    manager.SetOperatorLocked(true);
                    Require(!ViewModel(manager).HasLines && !ViewModel(manager).ShowTotals, "locked display exposed cart");
                    manager.SetCashierMinimized(true);
                    Publish(manager, Cart("return to cart"));
                    manager.SetCashierMinimized(false);
                    Require(!ViewModel(manager).HasLines && !ViewModel(manager).ShowTotals, "snapshot received during operator lock exposed cart");
                    manager.SetOperatorLocked(false);
                    Require(ViewModel(manager).HasLines && ViewModel(manager).Lines.Single().Name == "return to cart", "return to cart remained locked");
                    var autoOpen = manager.Settings;
                    autoOpen.AutoOpen = true;
                    await manager.SaveAndApplyAsync(autoOpen);
                    manager.SetCashierMinimized(true);
                    manager.CloseDisplay();
                    manager.SetCashierMinimized(false);
                    Publish(manager, Cart("closed cart"));
                    await TopologyChangeAsync(manager, topology, false);
                    await TopologyChangeAsync(manager, topology, true);
                    Require(!manager.IsOpen, "manual close was overridden by restore, snapshot or topology");
                    manager.OpenDisplay();
                    DisplayWindow(manager).Close();
                    Publish(manager, Cart("native close cart"));
                    Require(!manager.IsOpen, "window close did not preserve operator intent");
                    manager.OpenDisplay();
                    manager.SetCashierMinimized(true);
                    var disabled = manager.Settings;
                    disabled.Enabled = false;
                    await manager.SaveAndApplyAsync(disabled);
                    manager.SetCashierMinimized(false);
                    Publish(manager, Cart("disabled cart"));
                    Require(!manager.IsOpen && !manager.IsContentTimerRunning, "disabled display was restored");
                }
                finally { cashier.Close(); }
            }
        }

        private static async Task<CustomerDisplayManager> CreateAsync(string root, FixtureTopology topology, bool reopen, bool autoOpen = false)
        {
            var options = PosDbOptions.ForPath(Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"));
            DbInitializer.EnsureCreated(options);
            var repository = new CustomerDisplaySettingsRepository(new SqliteConnectionFactory(options));
            var settings = CustomerDisplaySettings.CreateDefault(2);
            settings.Enabled = true;
            settings.AutoOpen = autoOpen;
            settings.AlwaysOnTop = false;
            settings.FollowCashierMinimize = true;
            settings.ThankYouSeconds = 1;
            settings.ReopenWhenMonitorReturns = reopen;
            await repository.SaveAsync(settings, () => { }, "qa_fixture");
            var manager = new CustomerDisplayManager(topology, repository, Dispatcher.CurrentDispatcher, () => { }, () => "qa_fixture");
            try { await manager.InitializeAsync(); return manager; }
            catch { manager.Dispose(); throw; }
        }

        private static async Task TopologyChangeAsync(CustomerDisplayManager manager, FixtureTopology topology, bool connected)
        {
            topology.Connected = connected;
            NotifyTopology(manager);
            await Task.Delay(950);
        }

        private static void NotifyTopology(CustomerDisplayManager manager) => Invoke(manager, "OnDisplaySettingsChanged", null, EventArgs.Empty);
        private static void Publish(CustomerDisplayManager manager, CustomerDisplaySnapshot snapshot) => Invoke(manager, "OnSnapshotChanged", snapshot);
        private static void Publish(PosViewModel pos, string name)
        {
            pos.CartItems.Clear();
            pos.CartItems.Add(new PosViewModel.PosCartLineRow { LineKey = "startup", Barcode = "STARTUP", Name = name, Quantity = 1, UnitPrice = 1000, LineTotal = 1000 });
            pos.Subtotal = 1000;
            pos.Total = 1000;
            // Exercise the real public snapshot publisher and its subscribed event.
            pos.SetCustomerDisplayShopName(name);
        }
        private static void Invoke(CustomerDisplayManager manager, string name, params object[] args) =>
            typeof(CustomerDisplayManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(manager, args);
        private static bool Flag(CustomerDisplayManager manager, string name) =>
            (bool)typeof(CustomerDisplayManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
        private static CustomerDisplayWindow DisplayWindow(CustomerDisplayManager manager) =>
            (CustomerDisplayWindow)typeof(CustomerDisplayManager).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
        private static CustomerDisplayViewModel ViewModel(CustomerDisplayManager manager) => (CustomerDisplayViewModel)DisplayWindow(manager).DataContext;

        private static CustomerDisplaySnapshot Cart(string name) => CustomerDisplayProjection.Cart(new[]
        {
            new CustomerDisplayProjectionLine { StableKey = "fixture", Name = name, Quantity = 1, UnitPrice = 1000, LineTotal = 1000 }
        }, 1000, 1000, "TEST", "fixture", false, DateTimeOffset.UtcNow);

        private static async Task CheckAsync(List<string> outcomes, string name, Func<Task> check)
        {
            try { await check(); outcomes.Add("PASS " + name); }
            catch (Exception error) { outcomes.Add("FAIL " + name + ": " + error.Message); }
        }

        private static void Require(bool condition, string message) => FunctionalCompletionSmoke.Require(condition, message);

        private sealed class FixtureTopology : IDisplayTopologyProvider
        {
            internal bool Connected { get; set; } = true;
            public IReadOnlyList<DisplayMonitorInfo> GetMonitors()
            {
                var cashier = new DisplayMonitorInfo { DeviceName = "FIXTURE-CASHIER", IsPrimary = true, Width = 1024, Height = 768, WorkingWidth = 1024, WorkingHeight = 728, BitsPerPixel = 32 };
                var customer = new DisplayMonitorInfo { DeviceName = "FIXTURE-CUSTOMER", BoundsLeft = 1024, Width = 800, Height = 600, WorkAreaLeft = 1024, WorkingWidth = 800, WorkingHeight = 560, BitsPerPixel = 32 };
                return Connected ? new[] { cashier, customer } : new[] { cashier };
            }
        }
    }
}
