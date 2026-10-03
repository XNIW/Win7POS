using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Displays;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.CustomerDisplay;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class CustomerDisplayPolishSmoke
    {
        internal static async Task RunAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "Win7POS-display-polish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var storeDirectory = Path.Combine(root, "customer-display-logos");
                var store = new CustomerDisplayLogoStore(storeDirectory);
                CustomerDisplayLogoReference png = null;
                foreach (var kind in new[] { "png", "jpg", "bmp" })
                {
                    var source = Path.Combine(root, "fixture." + kind);
                    WriteImage(source, kind, 40);
                    var sourceBytes = File.ReadAllBytes(source);
                    var truncated = Path.Combine(root, "malformed." + kind);
                    File.WriteAllBytes(truncated, sourceBytes.Take(24).ToArray());
                    await MustFailAsync(() => store.ImportAsync(truncated), "malformed image accepted: " + kind).ConfigureAwait(true);
                    var dimensionBomb = Path.Combine(root, "dimension-bomb." + kind);
                    File.WriteAllBytes(dimensionBomb, OversizeDimensions(sourceBytes, kind));
                    await MustFailAsync(() => store.ImportAsync(dimensionBomb), "unbounded image dimensions accepted: " + kind).ConfigureAwait(true);
                    foreach (var invalidBytes in new[] { sourceBytes.Take(24).ToArray(), OversizeDimensions(sourceBytes, kind) })
                    {
                        string invalidHash;
                        using (var sha = SHA256.Create()) invalidHash = BitConverter.ToString(sha.ComputeHash(invalidBytes)).Replace("-", "").ToLowerInvariant();
                        var invalidReference = new CustomerDisplayLogoReference { FileName = "logo_" + invalidHash + "." + kind, Hash = invalidHash };
                        Directory.CreateDirectory(storeDirectory);
                        var invalidManaged = Path.Combine(storeDirectory, invalidReference.FileName);
                        File.WriteAllBytes(invalidManaged, invalidBytes);
                        Require(await store.LoadSafeAsync(WithLogo(invalidReference)).ConfigureAwait(true) == null, "malformed managed image did not fall back: " + kind);
                        File.Delete(invalidManaged);
                    }
                    var reference = await store.ImportAsync(source).ConfigureAwait(true);
                    Require(CustomerDisplayContentPolicy.IsManagedLogoReference(reference.FileName, reference.Hash), "managed image reference invalid: " + kind);
                    var settings = WithLogo(reference);
                    var bitmap = await store.LoadSafeAsync(settings).ConfigureAwait(true);
                    Require(bitmap != null && bitmap.IsFrozen && bitmap.PixelWidth == 2 && bitmap.PixelHeight == 2, "bounded detached image decode failed: " + kind);
                    var managed = Path.Combine(storeDirectory, reference.FileName);
                    using (var exclusive = new FileStream(managed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        Require(await store.LoadSafeAsync(settings).ConfigureAwait(true) == null, "locked managed image did not fall back: " + kind);
                    var replacement = Path.Combine(root, "replacement." + kind);
                    WriteImage(replacement, kind, 180);
                    File.Copy(replacement, managed, true);
                    Require(await store.LoadSafeAsync(settings).ConfigureAwait(true) == null, "replaced managed image accepted old content identity: " + kind);
                    File.Delete(managed);
                    Require(await store.LoadSafeAsync(settings).ConfigureAwait(true) == null, "missing managed image did not fall back: " + kind);
                    reference = await store.ImportAsync(source).ConfigureAwait(true);
                    using (var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        await MustFailAsync(() => store.ImportAsync(source), "locked source accepted: " + kind).ConfigureAwait(true);
                    if (kind == "png") png = reference;
                }
                Require(png != null, "PNG fixture missing");
                var oversizePath = Path.Combine(root, "oversize.png");
                File.WriteAllBytes(oversizePath, new byte[CustomerDisplayContentPolicy.MaximumLogoBytes + 1]);
                await MustFailAsync(() => store.ImportAsync(oversizePath), "oversize image bytes accepted").ConfigureAwait(true);
                await MustFailAsync(() => store.ImportAsync(@"\\fixture\share\logo.png"), "UNC source accepted").ConfigureAwait(true);
                await MustFailAsync(() => store.ImportAsync("https://fixture.invalid/logo.png"), "network image source accepted").ConfigureAwait(true);
                Require(Directory.GetFiles(storeDirectory, "*.partial-*").Length == 0, "partial logo residue");

                CheckPrivacyAndLocalization();
                await CheckManagerLifecycleAsync(root).ConfigureAwait(true);
            }
            finally
            {
                SqliteConnectionFactory.ClearAllPools();
                Directory.Delete(root, true);
            }
        }

        private static void CheckPrivacyAndLocalization()
        {
            var cart = Cart("PRIVATE-SALE-CANARY");
            var completed = CustomerDisplayProjection.Completed(cart, 1000, 2000, 1000, DateTimeOffset.UtcNow);
            var settings = CustomerDisplaySettings.CreateDefault(2);
            settings.BarcodeMode = CustomerDisplayBarcodeMode.Last4;
            settings.ShowPaidAmount = false; settings.ShowChangeAmount = false;
            var vm = new CustomerDisplayViewModel();
            vm.Apply(completed, settings, CustomerDisplayLayoutPolicy.Determine(600, 800));
            Require(!vm.ShowPaid && !vm.ShowChange && vm.PaidText == "" && vm.ChangeText == "", "completed amount privacy leaked");
            Require(vm.Lines.Single().Barcode == "••••1234", "last-four barcode privacy failed");
            foreach (var reserved in new[] { "DISC:fixture", "TAX:fixture", "MANUAL:fixture" })
            {
                var row = new CustomerDisplayLineRow(new CustomerDisplayLine("reserved", "fixture", reserved, 1, 1, 1, CustomerDisplayLineKind.Item), CustomerDisplayBarcodeMode.Full, true, true, false);
                Require(row.Barcode == "" && !row.ShowBarcode, "reserved barcode leaked");
            }
            foreach (var language in new[] { "it", "en", "es", "zh-CN" })
            {
                var translated = PosLocalization.Current.TextForLanguage(language, "customerDisplay.polish.testPattern");
                Require(!string.IsNullOrEmpty(translated) && translated != "customerDisplay.polish.testPattern", "display translation missing: " + language);
            }
            settings.IdleMode = CustomerDisplayIdleMode.CustomMessage; settings.IdleMessage = "Caffè 欢迎";
            vm.Apply(CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow), settings, CustomerDisplayLayoutPolicy.Determine(800, 600));
            Require(vm.StateMessage == settings.IdleMessage, "custom idle message missing");
            settings.IdleMode = CustomerDisplayIdleMode.Clock;
            vm.Apply(CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow), settings, CustomerDisplayLayoutPolicy.Determine(1920, 1080));
            var first = new DateTimeOffset(2026, 10, 3, 12, 1, 2, TimeSpan.Zero);
            vm.RefreshClockAndPattern(first, 0); var prior = vm.StateMessage;
            vm.RefreshClockAndPattern(first.AddSeconds(1), 0);
            Require(vm.StateMessage != prior, "idle clock does not tick");
        }

        private static async Task CheckManagerLifecycleAsync(string root)
        {
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            var repository = new CustomerDisplaySettingsRepository(new SqliteConnectionFactory(options));
            var saved = CustomerDisplaySettings.CreateDefault(2);
            saved.Enabled = true; saved.AutoOpen = false; saved.AlwaysOnTop = false;
            await repository.SaveAsync(saved, () => { }, "qa_fixture").ConfigureAwait(true);
            var topology = new FixtureTopology();
            var beforeSubscriptions = CustomerDisplayManager.ActiveDisplaySettingsSubscriptions;
            var manager = new CustomerDisplayManager(topology, repository, Dispatcher.CurrentDispatcher, () => { }, () => "qa_fixture");
            try
            {
                await manager.InitializeAsync().ConfigureAwait(true);
                Require(!manager.IsOpen && !manager.IsContentTimerRunning, "closed display retained idle timer");
                manager.OpenDisplay();
                var changed = typeof(CustomerDisplayManager).GetMethod("OnSnapshotChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                changed.Invoke(manager, new object[] { Cart("PRIVATE-SALE-CANARY") });
                var priorWindow = DisplayWindow(manager);
                var priorVm = (CustomerDisplayViewModel)priorWindow.DataContext;
                Require(priorVm.Lines.Single().Name == "PRIVATE-SALE-CANARY", "prior test projection missing");
                var focused = Keyboard.FocusedElement;
                var unsaved = saved.Clone(); unsaved.Theme = CustomerDisplayTheme.HighContrast; unsaved.TestPatternDurationSeconds = 5;
                manager.Preview(unsaved);
                var previewVm = (CustomerDisplayViewModel)DisplayWindow(manager).DataContext;
                Require(previewVm.Lines.All(line => line.Name != "PRIVATE-SALE-CANARY"), "preview copied a real sale projection");
                Require(manager.Settings.Theme == saved.Theme, "unsaved preview persisted runtime settings");
                manager.StopPreview();
                Require(((CustomerDisplayViewModel)DisplayWindow(manager).DataContext).Lines.Single().Name == "PRIVATE-SALE-CANARY", "manual preview did not restore prior projection");
                var elapsed = Stopwatch.StartNew();
                manager.StartTestPattern(unsaved);
                var patternVm = (CustomerDisplayViewModel)DisplayWindow(manager).DataContext;
                Require(patternVm.IsTestPattern && patternVm.Lines.Count == 0 && !patternVm.ShowTotals && !patternVm.ShowPaid && !patternVm.ShowChange, "test pattern retained sale data");
                Require(focused == null || ReferenceEquals(focused, Keyboard.FocusedElement), "nonactivating pattern stole keyboard focus");
                await WaitForConditionAsync(() => !manager.IsPreviewActive, TimeSpan.FromSeconds(8), "finite test pattern did not expire").ConfigureAwait(true);
                Require(elapsed.Elapsed.TotalSeconds >= 5 && elapsed.Elapsed.TotalSeconds < 8, "test pattern did not honor finite 5-second duration");
                Require(((CustomerDisplayViewModel)DisplayWindow(manager).DataContext).Lines.Single().Name == "PRIVATE-SALE-CANARY", "automatic pattern expiry did not restore projection");
                Require(!manager.IsContentTimerRunning, "pattern timer remained after restoration");
                var clock = saved.Clone(); clock.IdleMode = CustomerDisplayIdleMode.Clock;
                await manager.SaveAndApplyAsync(clock).ConfigureAwait(true);
                changed.Invoke(manager, new object[] { CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow) });
                Require(manager.IsContentTimerRunning, "visible idle clock timer did not start");
                manager.CloseDisplay();
                Require(!manager.IsOpen && !manager.IsContentTimerRunning, "clock timer survived display close");
                manager.SetCashierMinimized(true);
                elapsed.Restart();
                manager.StartTestPattern(unsaved);
                Require(manager.IsPreviewActive && !manager.IsOpen && manager.IsContentTimerRunning, "minimized preview did not keep its expiry timer");
                await WaitForConditionAsync(() => !manager.IsPreviewActive, TimeSpan.FromSeconds(8), "minimized test pattern did not expire").ConfigureAwait(true);
                Require(elapsed.Elapsed.TotalSeconds >= 5 && elapsed.Elapsed.TotalSeconds < 8, "minimized pattern did not honor finite 5-second duration");
                Require(!manager.IsOpen && !manager.IsContentTimerRunning, "minimized pattern expiry retained a window or timer");
                manager.SetCashierMinimized(false);
                var vm = new CustomerDisplaySettingsViewModel(saved, topology.GetMonitors());
                Require(vm.DiagnosticsText.IndexOf("PRIVATE-DEVICE", StringComparison.Ordinal) < 0, "copied diagnostics exposed a device identifier");
                vm.TestPatternDurationText = "bad";
                Require(!vm.TryBuild(out _, out _), "invalid duration text was silently ignored");
            }
            finally { manager.Dispose(); }
            Require(!manager.IsOpen && !manager.IsContentTimerRunning, "disposed display retained a window or timer");
            Require(CustomerDisplayManager.ActiveDisplaySettingsSubscriptions == beforeSubscriptions, "display/settings subscription leaked");
            var actor = "fixture_A";
            var permissionChecks = 0;
            using (var fenced = new CustomerDisplayManager(topology, repository, Dispatcher.CurrentDispatcher,
                () => { if (++permissionChecks == 6) actor = "fixture_B"; }, () => actor))
            {
                await fenced.InitializeAsync().ConfigureAwait(true);
                var rejected = false;
                var changedSettings = saved.Clone(); changedSettings.Theme = CustomerDisplayTheme.HighContrast;
                try { await fenced.SaveAndApplyAsync(changedSettings).ConfigureAwait(true); }
                catch (UnauthorizedAccessException) { rejected = true; }
                Require(rejected && actor == "fixture_B", "allowed operator switch was not fenced before settings commit");
                Require((await repository.LoadAsync(2)).Theme == saved.Theme, "actor switch partially saved settings");
            }
        }

        private static Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, string error)
        {
            var signal = new TaskCompletionSource<bool>();
            var deadline = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            EventHandler tick = null;
            tick = (sender, args) =>
            {
                if (predicate()) { timer.Stop(); timer.Tick -= tick; signal.TrySetResult(true); }
                else if (deadline.Elapsed >= timeout) { timer.Stop(); timer.Tick -= tick; signal.TrySetException(new InvalidOperationException(error)); }
            };
            timer.Tick += tick; timer.Start();
            return signal.Task;
        }

        private static CustomerDisplayWindow DisplayWindow(CustomerDisplayManager manager) =>
            (CustomerDisplayWindow)typeof(CustomerDisplayManager).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);

        private static CustomerDisplaySnapshot Cart(string name) => CustomerDisplayProjection.Cart(new[]
        {
            new CustomerDisplayProjectionLine { StableKey = "fixture", Name = name, Barcode = "00001234", Quantity = 1, UnitPrice = 1000, LineTotal = 1000 }
        }, 1000, 1000, "TEST", "fixture", true, DateTimeOffset.UtcNow);

        private static CustomerDisplaySettings WithLogo(CustomerDisplayLogoReference reference)
        {
            var settings = CustomerDisplaySettings.CreateDefault(2);
            settings.LogoFile = reference.FileName; settings.LogoHash = reference.Hash; return settings;
        }

        private static void WriteImage(string path, string kind, byte shade)
        {
            var pixels = Enumerable.Repeat(shade, 16).ToArray();
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, pixels, 8);
            BitmapEncoder encoder = kind == "png" ? (BitmapEncoder)new PngBitmapEncoder() : kind == "jpg" ? new JpegBitmapEncoder() : (BitmapEncoder)new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(path)) encoder.Save(output);
        }

        private static byte[] OversizeDimensions(byte[] source, string kind)
        {
            var bytes = source.ToArray();
            if (kind == "png") { bytes[16] = 0; bytes[17] = 76; bytes[18] = 75; bytes[19] = 64; }
            else if (kind == "bmp") { bytes[18] = 64; bytes[19] = 75; bytes[20] = 76; bytes[21] = 0; }
            else
            {
                var found = false;
                for (var index = 0; index + 8 < bytes.Length; index++)
                {
                    if (bytes[index] != 255 || bytes[index + 1] != 192 && bytes[index + 1] != 194) continue;
                    bytes[index + 5] = bytes[index + 6] = bytes[index + 7] = bytes[index + 8] = 255;
                    found = true; break;
                }
                Require(found, "JPEG fixture frame header missing");
            }
            return bytes;
        }

        private static async Task MustFailAsync(Func<Task> action, string error)
        {
            var failed = false;
            try { await action().ConfigureAwait(true); }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is ArgumentException || exception is FormatException || exception is NotSupportedException || exception is System.Runtime.InteropServices.COMException)
            { failed = true; }
            Require(failed, error);
        }

        private static void Require(bool condition, string message) => FunctionalCompletionSmoke.Require(condition, message);

        private sealed class FixtureTopology : IDisplayTopologyProvider
        {
            public IReadOnlyList<DisplayMonitorInfo> GetMonitors() => new[]
            {
                new DisplayMonitorInfo { DeviceName = "PRIVATE-DEVICE-CASHIER", IsPrimary = true, Width = 1024, Height = 768, WorkingWidth = 1024, WorkingHeight = 728, BitsPerPixel = 32 },
                new DisplayMonitorInfo { DeviceName = "PRIVATE-DEVICE-CUSTOMER", BoundsLeft = 1024, Width = 800, Height = 600, WorkAreaLeft = 1024, WorkingWidth = 800, WorkingHeight = 560, BitsPerPixel = 32 }
            };
        }
    }
}
