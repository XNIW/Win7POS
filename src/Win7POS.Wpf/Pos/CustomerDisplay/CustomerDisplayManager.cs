using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
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
        private CustomerDisplayWindow _window;
        private PosViewModel _posViewModel;
        private CustomerDisplaySnapshot _lastSnapshot = CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
        private CustomerDisplaySettings _settings = CustomerDisplaySettings.CreateDefault(1);
        private bool _subscribed;
        private bool _disposed;
        private bool _cashierMinimized;
        private bool _monitorWasMissing;
        private bool _manuallyClosed;
        private string _lastTopologySignature = string.Empty;

        public CustomerDisplayManager(
            IDisplayTopologyProvider topologyProvider,
            CustomerDisplaySettingsRepository settingsRepository,
            Dispatcher dispatcher)
        {
            _topologyProvider = topologyProvider ?? throw new ArgumentNullException(nameof(topologyProvider));
            _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _topologyDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _topologyDebounce.Tick += OnTopologyDebounce;
            _completedTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _completedTimer.Tick += OnCompletedTimer;
        }

        public CustomerDisplaySettings Settings => _settings.Clone();
        public bool IsOpen => _window != null && _window.IsVisible;
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

            if (!_subscribed)
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
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
            ValidateAgainstTopology(settings);

            var previous = _settings.Clone();
            await _settingsRepository.SaveAsync(settings).ConfigureAwait(true);
            _settings = settings.Clone();
            try
            {
                if (!_settings.Enabled)
                {
                    _manuallyClosed = false;
                    CloseWindow();
                }
                else
                {
                    _manuallyClosed = false;
                    TryOpenOrUpdate(true);
                }
            }
            catch
            {
                _settings = previous;
                await _settingsRepository.SaveAsync(previous).ConfigureAwait(true);
                TryOpenOrUpdate(false);
                throw;
            }
        }

        public void OpenDisplay()
        {
            ThrowIfDisposed();
            _manuallyClosed = false;
            TryOpenOrUpdate(true);
        }

        public void CloseDisplay()
        {
            _manuallyClosed = true;
            CloseWindow();
        }

        private void CloseWindow()
        {
            _completedTimer.Stop();
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
            ThrowIfDisposed();
            _manuallyClosed = false;
            var previewSettings = _settings.Clone();
            previewSettings.Enabled = true;
            var snapshot = _lastSnapshot;
            if (snapshot == null || snapshot.Lines.Count == 0)
            {
                snapshot = CustomerDisplayProjection.Cart(
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
                    previewSettings.ShowBarcode,
                    DateTimeOffset.UtcNow);
            }
            TryOpenOrUpdate(true, snapshot, previewSettings);
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
            if (_settings.FollowCashierMinimize && minimized)
            {
                _window?.Hide();
                return;
            }

            if (!minimized && _settings.Enabled && _settings.AutoOpen)
            {
                TryOpenOrUpdate(false);
            }
        }

        public void SetOperatorLocked(bool locked)
        {
            var snapshot = locked
                ? CustomerDisplayProjection.WithState(_lastSnapshot, CustomerDisplayState.Locked, "locked")
                : _posViewModel?.CurrentCustomerDisplaySnapshot ?? _lastSnapshot;
            OnSnapshotChanged(snapshot);
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
            if (_settings.Enabled && (!_cashierMinimized || !_settings.FollowCashierMinimize))
                TryOpenOrUpdate(false);
        }

        private void OnCompletedTimer(object sender, EventArgs e)
        {
            _completedTimer.Stop();
            _lastSnapshot = CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
            if (_settings.Enabled) TryOpenOrUpdate(false);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                _topologyDebounce.Stop();
                _topologyDebounce.Start();
            }));
        }

        private void OnTopologyDebounce(object sender, EventArgs e)
        {
            _topologyDebounce.Stop();
            if (!_settings.Enabled || _manuallyClosed) return;
            if (_monitorWasMissing && !_settings.ReopenWhenMonitorReturns)
            {
                var monitors = SafeMonitors();
                var selection = CustomerDisplayMonitorPolicy.Select(
                    monitors.Select(x => x.ToDescriptor()),
                    _settings);
                if (selection.Customer != null)
                {
                    _monitorWasMissing = false;
                    _manuallyClosed = true;
                }
                return;
            }
            var opened = TryOpenOrUpdate(false);
            if (!opened)
            {
                _monitorWasMissing = true;
                CloseWindow();
            }
            else if (_monitorWasMissing && _settings.ReopenWhenMonitorReturns)
            {
                _monitorWasMissing = false;
                TryOpenOrUpdate(false);
            }
        }

        private bool TryOpenOrUpdate(
            bool throwOnFailure,
            CustomerDisplaySnapshot snapshot = null,
            CustomerDisplaySettings settings = null)
        {
            settings = settings ?? _settings;
            if (!settings.Enabled || _manuallyClosed || (_cashierMinimized && settings.FollowCashierMinimize)) return false;
            try
            {
                var monitors = SafeMonitors();
                var selection = CustomerDisplayMonitorPolicy.Select(monitors.Select(x => x.ToDescriptor()), settings);
                if (selection.Customer == null)
                {
                    CloseWindow();
                    WarningRaised?.Invoke(selection.ErrorCode);
                    if (throwOnFailure) throw new InvalidOperationException(selection.ErrorCode);
                    return false;
                }

                var monitor = monitors.FirstOrDefault(x =>
                    string.Equals(x.DeviceName, selection.Customer.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (monitor == null)
                {
                    CloseWindow();
                    WarningRaised?.Invoke("selected_monitor_missing");
                    if (throwOnFailure) throw new InvalidOperationException("selected_monitor_missing");
                    return false;
                }

                if (_window == null)
                {
                    _window = new CustomerDisplayWindow();
                    _window.Closed += (_, __) => _window = null;
                    _window.Show();
                }
                else if (!_window.IsVisible)
                {
                    _window.Show();
                }

                _window.UpdateDisplay(snapshot ?? _lastSnapshot, settings, monitor);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("category=customer_display operation=open_or_update result=failed", ex);
                if (throwOnFailure) throw;
                return false;
            }
        }

        private IReadOnlyList<DisplayMonitorInfo> SafeMonitors()
        {
            var monitors = _topologyProvider.GetMonitors() ?? new List<DisplayMonitorInfo>().AsReadOnly();
            var signature = string.Join("|", monitors.Select(monitor =>
                SafeToken(monitor.DeviceName) + ":" + monitor.IsPrimary + ":" +
                monitor.BoundsLeft + "," + monitor.BoundsTop + "," + monitor.Width + "," + monitor.Height + ":" +
                monitor.WorkAreaLeft + "," + monitor.WorkAreaTop + "," + monitor.WorkingWidth + "," + monitor.WorkingHeight + ":" +
                monitor.BitsPerPixel + ":" + monitor.Orientation));
            if (!string.Equals(signature, _lastTopologySignature, StringComparison.Ordinal))
            {
                _lastTopologySignature = signature;
                foreach (var monitor in monitors)
                {
                    _logger.LogInfo(
                        "category=display_topology device=" + SafeToken(monitor.DeviceName) +
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

        private static string SafeToken(string value)
        {
            return new string((value ?? string.Empty)
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '\\' || ch == '_' || ch == '-' || ch == '.')
                .Take(64)
                .ToArray());
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CustomerDisplayManager));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _topologyDebounce.Stop();
            _completedTimer.Stop();
            if (_subscribed)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
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
