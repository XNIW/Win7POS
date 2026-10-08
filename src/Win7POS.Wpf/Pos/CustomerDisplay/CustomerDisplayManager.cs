using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Win7POS.Core.Pos;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Displays;

namespace Win7POS.Wpf.Pos.CustomerDisplay
{
    public sealed class CustomerDisplayManager : IDisposable
    {
        private static int _activeDisplaySettingsSubscriptions;
        private readonly IDisplayTopologyProvider _topologyProvider;
        private readonly CustomerDisplaySettingsRepository _settingsRepository;
        private readonly Dispatcher _dispatcher;
        private readonly FileLogger _logger = new FileLogger("CustomerDisplayManager");
        private readonly DispatcherTimer _topologyDebounce;
        private readonly DispatcherTimer _completedTimer;
        private readonly DispatcherTimer _contentTimer;
        private readonly CustomerDisplayLogoStore _logoStore;
        private readonly Action _demandPermission;
        private readonly Func<string> _actor;
        private readonly SemaphoreSlim _logoImportGate = new SemaphoreSlim(1, 1);
        private BitmapSource _logo;
        private CustomerDisplaySettings _previewSettings;
        private CustomerDisplaySnapshot _previewSnapshot;
        private readonly Stopwatch _previewWatch = new Stopwatch();
        private bool _previewPriorOpen;
        private bool _previewPriorManuallyClosed;
        private bool _previewPriorHiddenForMinimize;
        private bool _previewPriorMonitorMissing;
        private bool _previewPriorReopenBlocked;
        private bool _reloadRunning;
        private bool _reloadRequested;
        private CustomerDisplayWindow _window;
        private PosViewModel _posViewModel;
        private CustomerDisplaySnapshot _lastSnapshot = CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
        private CustomerDisplaySettings _settings = CustomerDisplaySettings.CreateDefault(1);
        private bool _subscribed;
        private bool _disposed;
        private bool _cashierMinimized;
        private bool _operatorLocked;
        private bool _monitorWasMissing;
        private bool _manuallyClosed;
        private bool _hiddenForCashierMinimize;
        private bool _reopenBlocked;
        private string _lastTopologySignature = string.Empty;

        public CustomerDisplayManager(
            IDisplayTopologyProvider topologyProvider,
            CustomerDisplaySettingsRepository settingsRepository,
            Dispatcher dispatcher, Action demandPermission = null, Func<string> actor = null)
        {
            _topologyProvider = topologyProvider ?? throw new ArgumentNullException(nameof(topologyProvider));
            _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _demandPermission = demandPermission ?? (() => throw new UnauthorizedAccessException("settings.printer permission required."));
            _actor = actor ?? (() => "operator");
            _logoStore = new CustomerDisplayLogoStore(Path.Combine(Path.GetDirectoryName(settingsRepository.DatabasePath), "customer-display-logos"));
            _topologyDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _topologyDebounce.Tick += OnTopologyDebounce;
            _completedTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _completedTimer.Tick += OnCompletedTimer;
            _contentTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _contentTimer.Tick += OnContentTimer;
        }

        public CustomerDisplaySettings Settings => _settings.Clone();
        public bool IsOpen => _window != null && _window.IsVisible;
        public bool IsPreviewActive => _previewSettings != null;
        public bool IsContentTimerRunning => _contentTimer.IsEnabled;
        public static int ActiveDisplaySettingsSubscriptions => Volatile.Read(ref _activeDisplaySettingsSubscriptions);
        public event Action<string> WarningRaised;

        public async Task InitializeAsync()
        {
            ThrowIfDisposed();
            var monitors = SafeMonitors();
            var independentCount = CustomerDisplayMonitorPolicy
                .Select(monitors.Select(x => x.ToDescriptor()), CustomerDisplaySettings.CreateDefault(monitors.Count))
                .IndependentMonitors.Count;
            _settings = await _settingsRepository.LoadAsync(independentCount).ConfigureAwait(true);
            _logo = await _logoStore.LoadSafeAsync(_settings).ConfigureAwait(true);
            if (_disposed) return;

            if (!_subscribed)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                SettingsRepository.SettingsChanged += OnSettingsCommitted;
                _subscribed = true;
                Interlocked.Increment(ref _activeDisplaySettingsSubscriptions);
            }

            if (_settings.Enabled && _settings.AutoOpen)
            {
                TryOpenOrUpdate(false);
            }
        }

        public void Attach(PosViewModel viewModel)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_posViewModel, viewModel)) return;
            if (_posViewModel != null)
                _posViewModel.CustomerDisplaySnapshotChanged -= OnSnapshotChanged;
            _posViewModel = viewModel;
            if (_posViewModel != null)
            {
                _posViewModel.CustomerDisplaySnapshotChanged += OnSnapshotChanged;
                OnSnapshotChanged(_posViewModel.CurrentCustomerDisplaySnapshot);
            }
        }

        public async Task SaveAndApplyAsync(CustomerDisplaySettings settings)
        {
            ThrowIfDisposed();
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var actor = _actor() ?? string.Empty;
            var demand = PermissionFence(actor);
            demand();
            settings = settings.Clone();
            ValidateAgainstTopology(settings);
            StopPreview();
            var logo = await _logoStore.LoadSafeAsync(settings).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(settings.LogoFile) && logo == null) throw new InvalidDataException("logo_unavailable");
            demand();
            await _settingsRepository.SaveAsync(settings, demand, actor).ConfigureAwait(true);
            _settings = settings.Clone();
            _logo = logo;
            if (_disposed) return;
            _manuallyClosed = false;
            _monitorWasMissing = false;
            _reopenBlocked = false;
            TryOpenOrUpdate(false);
        }

        public async Task<CustomerDisplayLogoReference> ImportLogoAsync(string path)
        {
            ThrowIfDisposed();
            var demand = PermissionFence(_actor() ?? string.Empty);
            demand();
            if (!await _logoImportGate.WaitAsync(0).ConfigureAwait(true)) throw new InvalidOperationException("logo_import_running");
            try
            {
                var reference = await _logoStore.ImportAsync(path).ConfigureAwait(true);
                ThrowIfDisposed();
                demand();
                return reference;
            }
            finally { _logoImportGate.Release(); }
        }

        private Action PermissionFence(string actor) => () =>
        {
            ThrowIfDisposed();
            _demandPermission();
            if (!string.Equals(actor, _actor() ?? string.Empty, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("settings actor changed during operation.");
        };

        public void OpenDisplay()
        {
            ThrowIfDisposed();
            _manuallyClosed = false;
            _monitorWasMissing = false;
            _reopenBlocked = false;
            TryOpenOrUpdate(true);
        }

        public void CloseDisplay()
        {
            StopPreview();
            _manuallyClosed = true;
            _hiddenForCashierMinimize = false;
            CloseWindow();
        }

        private void CloseWindow()
        {
            _completedTimer.Stop();
            if (_previewSettings == null || _disposed) _contentTimer.Stop();
            else _contentTimer.Start();
            var window = _window;
            _window = null;
            if (window != null)
            {
                try { window.Close(); }
                catch (Exception ex) { _logger.LogWarning("category=customer_display close=failed", ex); }
            }
        }

        public void Preview()
        {
            Preview(_settings);
        }

        public void Preview(CustomerDisplaySettings settings)
        {
            var snapshot = CustomerDisplayProjection.Cart(
                    new[]
                    {
                        new CustomerDisplayProjectionLine
                        {
                            StableKey = "preview:item",
                            Name = "[PREVIEW] Sample product",
                            Barcode = "0000000000000",
                            Quantity = 2,
                            UnitPrice = 1000,
                            LineTotal = 2000,
                            LineKind = CustomerDisplayLineKind.Item
                        }
                    },
                    2000,
                    2000,
                    "PREVIEW — Win7POS",
                    "preview:item",
                    true,
                    DateTimeOffset.UtcNow);
            StartPreview(settings, snapshot);
        }

        public void StartTestPattern(CustomerDisplaySettings settings)
        {
            StartPreview(settings, new CustomerDisplaySnapshot(CustomerDisplayState.TestPattern, string.Empty,
                Array.Empty<CustomerDisplayLine>(), 0, 0, 0, 0, 0, 0, string.Empty, "test_pattern", DateTimeOffset.UtcNow));
        }

        private void StartPreview(CustomerDisplaySettings settings, CustomerDisplaySnapshot snapshot)
        {
            ThrowIfDisposed();
            _demandPermission();
            StopPreview();
            var candidate = (settings ?? _settings).Clone();
            candidate.Enabled = true;
            ValidateAgainstTopology(candidate);
            _previewPriorOpen = IsOpen;
            _previewPriorManuallyClosed = _manuallyClosed;
            _previewPriorHiddenForMinimize = _hiddenForCashierMinimize;
            _previewPriorMonitorMissing = _monitorWasMissing;
            _previewPriorReopenBlocked = _reopenBlocked;
            _previewSettings = candidate;
            _previewSnapshot = snapshot;
            _manuallyClosed = false;
            _monitorWasMissing = false;
            _reopenBlocked = false;
            _previewWatch.Restart();
            _contentTimer.Start();
            try { TryOpenOrUpdate(true); }
            catch { StopPreview(); throw; }
        }

        public void StopPreview()
        {
            if (_previewSettings == null) return;
            _previewSettings = null;
            _previewSnapshot = null;
            _previewWatch.Stop();
            _contentTimer.Stop();
            _manuallyClosed = _previewPriorManuallyClosed;
            _hiddenForCashierMinimize = _previewPriorHiddenForMinimize;
            _monitorWasMissing = _previewPriorMonitorMissing;
            _reopenBlocked = _previewPriorReopenBlocked;
            if ((_previewPriorOpen || _hiddenForCashierMinimize) && _settings.Enabled) TryOpenOrUpdate(false);
            else CloseWindow();
        }

        private void OnContentTimer(object sender, EventArgs e)
        {
            if (_disposed) return;
            if (_previewSettings != null && _previewWatch.Elapsed.TotalSeconds >= _previewSettings.TestPatternDurationSeconds)
                StopPreview();
            else if (_window != null && _window.IsVisible)
                _window.RefreshClockAndPattern(DateTimeOffset.Now,
                    _previewSettings == null ? 0 : Math.Max(0, _previewSettings.TestPatternDurationSeconds - (int)_previewWatch.Elapsed.TotalSeconds));
        }

        public IReadOnlyList<MonitorIdentifyWindow> IdentifyMonitors()
        {
            ThrowIfDisposed();
            var windows = new List<MonitorIdentifyWindow>();
            var monitors = SafeMonitors();
            for (var index = 0; index < monitors.Count; index++)
            {
                var window = new MonitorIdentifyWindow(monitors[index], index + 1);
                windows.Add(window);
                window.Show();
            }
            return windows.AsReadOnly();
        }

        public void SetCashierMinimized(bool minimized)
        {
            _cashierMinimized = minimized;
            var settings = _previewSettings ?? _settings;
            if (settings.FollowCashierMinimize && minimized)
            {
                if (IsOpen) _hiddenForCashierMinimize = true;
                _window?.Hide();
                if (_previewSettings == null) _contentTimer.Stop();
                return;
            }

            if (settings.Enabled && (_hiddenForCashierMinimize || settings.AutoOpen))
            {
                TryOpenOrUpdate(false);
            }
        }

        public void SetOperatorLocked(bool locked)
        {
            _operatorLocked = locked;
            OnSnapshotChanged(_posViewModel?.CurrentCustomerDisplaySnapshot ?? _lastSnapshot);
        }

        private void OnSnapshotChanged(CustomerDisplaySnapshot snapshot)
        {
            if (snapshot == null || _disposed) return;
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(new Action(() => OnSnapshotChanged(snapshot)));
                return;
            }

            _lastSnapshot = snapshot;
            _completedTimer.Stop();
            if (snapshot.State == CustomerDisplayState.Completed)
            {
                _completedTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, Math.Min(30, _settings.ThankYouSeconds)));
                _completedTimer.Start();
            }
            if (_settings.Enabled || _previewSettings != null)
                TryOpenOrUpdate(false);
        }

        private void OnCompletedTimer(object sender, EventArgs e)
        {
            _completedTimer.Stop();
            _lastSnapshot = CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
            if (_settings.Enabled || _previewSettings != null) TryOpenOrUpdate(false);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                _topologyDebounce.Stop();
                _topologyDebounce.Start();
            }));
        }

        private void OnTopologyDebounce(object sender, EventArgs e)
        {
            _topologyDebounce.Stop();
            TryOpenOrUpdate(false);
        }

        private bool TryOpenOrUpdate(
            bool throwOnFailure,
            CustomerDisplaySnapshot snapshot = null,
            CustomerDisplaySettings settings = null)
        {
            settings = settings ?? _previewSettings ?? _settings;
            if (!settings.Enabled || _manuallyClosed)
            {
                _hiddenForCashierMinimize = false;
                CloseWindow();
                return false;
            }
            try
            {
                var monitors = SafeMonitors();
                var selection = CustomerDisplayMonitorPolicy.Select(monitors.Select(x => x.ToDescriptor()), settings);
                if (selection.Customer == null)
                {
                    _monitorWasMissing = true;
                    CloseWindow();
                    WarningRaised?.Invoke(selection.ErrorCode);
                    if (throwOnFailure) throw new InvalidOperationException(selection.ErrorCode);
                    return false;
                }

                var monitor = monitors.FirstOrDefault(x =>
                    string.Equals(x.DeviceName, selection.Customer.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (monitor == null)
                {
                    _monitorWasMissing = true;
                    CloseWindow();
                    WarningRaised?.Invoke("selected_monitor_missing");
                    if (throwOnFailure) throw new InvalidOperationException("selected_monitor_missing");
                    return false;
                }

                // Every entry point observes the same return policy, including a
                // snapshot that arrives before the display-settings debounce.
                if (_monitorWasMissing)
                {
                    _monitorWasMissing = false;
                    _reopenBlocked = !settings.ReopenWhenMonitorReturns;
                }
                if (_reopenBlocked)
                {
                    _hiddenForCashierMinimize = false;
                    CloseWindow();
                    return false;
                }
                if (_cashierMinimized && settings.FollowCashierMinimize)
                {
                    if (IsOpen) _hiddenForCashierMinimize = true;
                    _window?.Hide();
                    if (_previewSettings == null) _contentTimer.Stop();
                    return false;
                }

                var displaySnapshot = snapshot ?? _previewSnapshot ?? (_operatorLocked
                    ? CustomerDisplayProjection.WithState(_lastSnapshot, CustomerDisplayState.Locked, "locked")
                    : _lastSnapshot);
                var logo = _previewSettings == null || settings.LogoFile == _settings.LogoFile && settings.LogoHash == _settings.LogoHash ? _logo : _logoStore.Cached(settings);
                if (_window == null)
                {
                    _window = new CustomerDisplayWindow();
                    _window.Closed += OnWindowClosed;
                    _window.PrepareDisplay(displaySnapshot, settings, monitor, logo);
                    _window.Show();
                }
                else if (!_window.IsVisible)
                {
                    _window.PrepareDisplay(displaySnapshot, settings, monitor, logo);
                    _window.Show();
                }
                else
                {
                    _window.UpdateDisplay(displaySnapshot, settings, monitor, logo);
                }
                if (displaySnapshot.State == CustomerDisplayState.TestPattern)
                {
                    _window.SetPatternMonitor(monitors.ToList().IndexOf(monitor) + 1, monitor);
                    _window.RefreshClockAndPattern(DateTimeOffset.Now, Math.Max(0, settings.TestPatternDurationSeconds - (int)_previewWatch.Elapsed.TotalSeconds));
                }
                if (_previewSettings != null || displaySnapshot.State == CustomerDisplayState.Idle && settings.IdleMode == CustomerDisplayIdleMode.Clock)
                    _contentTimer.Start();
                else _contentTimer.Stop();
                _hiddenForCashierMinimize = false;
                return true;
            }
            catch (Exception ex)
            {
                CloseWindow();
                _logger.LogWarning("category=customer_display operation=open_or_update result=failed", ex);
                if (throwOnFailure) throw;
                return false;
            }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            if (sender is CustomerDisplayWindow window) window.Closed -= OnWindowClosed;
            if (ReferenceEquals(sender, _window))
            {
                _manuallyClosed = true;
                _hiddenForCashierMinimize = false;
                _window = null;
                _completedTimer.Stop();
                _contentTimer.Stop();
                _previewSettings = null;
                _previewSnapshot = null;
                _previewWatch.Stop();
            }
        }

        private async void OnSettingsCommitted(object sender, SettingsCommittedEventArgs e)
        {
            if (_disposed || !string.Equals(Path.GetFullPath(e.DatabasePath), Path.GetFullPath(_settingsRepository.DatabasePath), StringComparison.OrdinalIgnoreCase) ||
                !e.Keys.Any(key => key.StartsWith(CustomerDisplaySettingsRepository.Prefix, StringComparison.Ordinal) || key == "ui.language")) return;
            if (!_dispatcher.CheckAccess())
            {
                if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                    _ = _dispatcher.BeginInvoke(new Action(() => OnSettingsCommitted(sender, e)));
                return;
            }
            _reloadRequested = true;
            if (_reloadRunning) return;
            _reloadRunning = true;
            try
            {
                while (_reloadRequested && !_disposed)
                {
                    _reloadRequested = false;
                    var settings = await _settingsRepository.LoadAsync(SafeMonitors().Count).ConfigureAwait(true);
                    var logo = await _logoStore.LoadSafeAsync(settings).ConfigureAwait(true);
                    if (_disposed) return;
                    _settings = settings;
                    _logo = logo;
                    TryOpenOrUpdate(false);
                }
            }
            catch { WarningRaised?.Invoke("settingsReloadFailed"); }
            finally { _reloadRunning = false; }
        }

        private IReadOnlyList<DisplayMonitorInfo> SafeMonitors()
        {
            var monitors = _topologyProvider.GetMonitors() ?? new List<DisplayMonitorInfo>().AsReadOnly();
            var signature = string.Join("|", monitors.Select(monitor =>
                DeviceHash(monitor.DeviceName) + ":" + monitor.IsPrimary + ":" +
                monitor.BoundsLeft + "," + monitor.BoundsTop + "," + monitor.Width + "," + monitor.Height + ":" +
                monitor.WorkAreaLeft + "," + monitor.WorkAreaTop + "," + monitor.WorkingWidth + "," + monitor.WorkingHeight + ":" +
                monitor.BitsPerPixel + ":" + monitor.Orientation));
            if (!string.Equals(signature, _lastTopologySignature, StringComparison.Ordinal))
            {
                _lastTopologySignature = signature;
                foreach (var monitor in monitors)
                {
                    _logger.LogInfo(
                        "category=display_topology device_hash=" + DeviceHash(monitor.DeviceName) +
                        " primary=" + monitor.IsPrimary +
                        " bounds=" + monitor.BoundsLeft + "," + monitor.BoundsTop + "," + monitor.Width + "," + monitor.Height +
                        " work=" + monitor.WorkAreaLeft + "," + monitor.WorkAreaTop + "," + monitor.WorkingWidth + "," + monitor.WorkingHeight +
                        " bpp=" + monitor.BitsPerPixel +
                        " orientation=" + monitor.Orientation);
                }
            }
            return monitors;
        }

        private void ValidateAgainstTopology(CustomerDisplaySettings settings)
        {
            var errors = settings.Validate();
            if (errors.Count > 0) throw new ArgumentException(string.Join(",", errors));
            var selection = CustomerDisplayMonitorPolicy.Select(SafeMonitors().Select(x => x.ToDescriptor()), settings);
            if (settings.Enabled && selection.Customer == null)
                throw new InvalidOperationException(selection.ErrorCode);
        }

        private static string DeviceHash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", "").ToLowerInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CustomerDisplayManager));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _previewSettings = null;
            _previewSnapshot = null;
            _previewWatch.Stop();
            _topologyDebounce.Stop();
            _completedTimer.Stop();
            _contentTimer.Stop();
            _topologyDebounce.Tick -= OnTopologyDebounce;
            _completedTimer.Tick -= OnCompletedTimer;
            _contentTimer.Tick -= OnContentTimer;
            if (_subscribed)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SettingsRepository.SettingsChanged -= OnSettingsCommitted;
                _subscribed = false;
                Interlocked.Decrement(ref _activeDisplaySettingsSubscriptions);
            }
            if (_posViewModel != null)
            {
                _posViewModel.CustomerDisplaySnapshotChanged -= OnSnapshotChanged;
                _posViewModel = null;
            }
            CloseWindow();
        }
    }
}
