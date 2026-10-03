using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Infrastructure.Displays;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class CustomerDisplaySettingsViewModel : INotifyPropertyChanged
    {
        private readonly IReadOnlyList<DisplayMonitorInfo> _monitors;
        private readonly CustomerDisplaySettings _working;
        private readonly Func<string, Task<CustomerDisplayLogoReference>> _importLogo;
        private bool _logoBusy;
        private string _testDurationText;

        public CustomerDisplaySettingsViewModel(
            CustomerDisplaySettings settings,
            IReadOnlyList<DisplayMonitorInfo> monitors, Func<string, Task<CustomerDisplayLogoReference>> importLogo = null)
        {
            _working = settings?.Clone() ?? CustomerDisplaySettings.CreateDefault(monitors?.Count ?? 0);
            _monitors = monitors ?? new List<DisplayMonitorInfo>().AsReadOnly();
            _importLogo = importLogo;
            _testDurationText = _working.TestPatternDurationSeconds.ToString(CultureInfo.InvariantCulture);
            Monitors = new ObservableCollection<MonitorChoice>(_monitors.Select((monitor, index) => new MonitorChoice
            {
                DeviceName = monitor.DeviceName,
                DisplayName = PosLocalization.Current.Format(
                    "customerDisplay.settings.monitorSummary",
                    index + 1,
                    monitor.Width,
                    monitor.Height,
                    monitor.IsPrimary ? PosLocalization.Current.Text("customerDisplay.settings.primary") : string.Empty),
                IsPrimary = monitor.IsPrimary,
                IsAvailable = true
            }));
            if (string.IsNullOrWhiteSpace(_working.CashierMonitorDeviceName))
                _working.CashierMonitorDeviceName = _monitors.FirstOrDefault(x => x.IsPrimary)?.DeviceName ?? _monitors.FirstOrDefault()?.DeviceName ?? string.Empty;
        }

        public ObservableCollection<MonitorChoice> Monitors { get; }
        public Array FontScales => Enum.GetValues(typeof(CustomerDisplayFontScale));
        public Array Themes => Enum.GetValues(typeof(CustomerDisplayTheme));
        public Array Languages => Enum.GetValues(typeof(CustomerDisplayLanguage));
        public IEnumerable<CustomerDisplayOption> LogoPositions => Choices(typeof(CustomerDisplayLogoPosition), "logoPosition");
        public IEnumerable<CustomerDisplayOption> IdleModes => Choices(typeof(CustomerDisplayIdleMode), "idleMode");
        public IEnumerable<CustomerDisplayOption> BarcodeModes => Choices(typeof(CustomerDisplayBarcodeMode), "barcodeMode");
        private static IEnumerable<CustomerDisplayOption> Choices(Type type, string group) => Enum.GetValues(type).Cast<object>()
            .Select(value => new CustomerDisplayOption { Value = value, Label = PosLocalization.Current.Text("customerDisplay.polish." + group + "." + value) });
        public int MonitorCount => _monitors.Count;
        public string TopologyText
        {
            get
            {
                var selection = CustomerDisplayMonitorPolicy.Select(_monitors.Select(x => x.ToDescriptor()), _working);
                return PosLocalization.Current.Text("customerDisplay.settings.topology." + selection.TopologyMode.ToString().ToLowerInvariant());
            }
        }

        public bool Enabled { get => _working.Enabled; set { _working.Enabled = value; OnPropertyChanged(); } }
        public bool Automatic { get => _working.SelectionMode == CustomerDisplaySelectionMode.Auto; set { if (value) { _working.SelectionMode = CustomerDisplaySelectionMode.Auto; OnPropertyChanged(); OnPropertyChanged(nameof(Manual)); } } }
        public bool Manual { get => _working.SelectionMode == CustomerDisplaySelectionMode.Manual; set { if (value) { _working.SelectionMode = CustomerDisplaySelectionMode.Manual; OnPropertyChanged(); OnPropertyChanged(nameof(Automatic)); } } }
        public string CashierMonitor { get => _working.CashierMonitorDeviceName; set { _working.CashierMonitorDeviceName = value ?? string.Empty; OnPropertyChanged(); } }
        public string CustomerMonitor { get => _working.CustomerMonitorDeviceName; set { _working.CustomerMonitorDeviceName = value ?? string.Empty; OnPropertyChanged(); } }
        public bool AutoOpen { get => _working.AutoOpen; set { _working.AutoOpen = value; OnPropertyChanged(); } }
        public bool FullScreen { get => _working.FullScreen; set { _working.FullScreen = value; OnPropertyChanged(); } }
        public bool UseWorkingArea { get => _working.UseWorkingArea; set { _working.UseWorkingArea = value; OnPropertyChanged(); } }
        public bool AlwaysOnTop { get => _working.AlwaysOnTop; set { _working.AlwaysOnTop = value; OnPropertyChanged(); } }
        public bool FollowMinimize { get => _working.FollowCashierMinimize; set { _working.FollowCashierMinimize = value; OnPropertyChanged(); } }
        public bool ShowShopName { get => _working.ShowShopName; set { _working.ShowShopName = value; OnPropertyChanged(); } }
        public bool ShowBarcode { get => _working.ShowBarcode; set { _working.ShowBarcode = value; OnPropertyChanged(); } }
        public CustomerDisplayBarcodeMode BarcodeMode { get => _working.BarcodeMode; set { _working.BarcodeMode = value; OnPropertyChanged(); } }
        public CustomerDisplayLogoPosition LogoPosition { get => _working.LogoPosition; set { _working.LogoPosition = value; OnPropertyChanged(); } }
        public CustomerDisplayIdleMode IdleMode { get => _working.IdleMode; set { _working.IdleMode = value; OnPropertyChanged(); } }
        public string IdleMessage { get => _working.IdleMessage; set { _working.IdleMessage = value ?? string.Empty; OnPropertyChanged(); } }
        public bool ShowPaidAmount { get => _working.ShowPaidAmount; set { _working.ShowPaidAmount = value; OnPropertyChanged(); } }
        public bool ShowChangeAmount { get => _working.ShowChangeAmount; set { _working.ShowChangeAmount = value; OnPropertyChanged(); } }
        public int TestPatternDurationSeconds { get => _working.TestPatternDurationSeconds; set { _working.TestPatternDurationSeconds = value; _testDurationText = value.ToString(CultureInfo.InvariantCulture); OnPropertyChanged(); OnPropertyChanged(nameof(TestPatternDurationText)); } }
        public string TestPatternDurationText { get => _testDurationText; set { _testDurationText = value; OnPropertyChanged(); } }
        public string DiagnosticsText => TopologyText + "\n" + string.Join("\n", _monitors.Select((monitor, index) =>
            "#" + (index + 1) + " primary=" + monitor.IsPrimary + " bounds=" + monitor.BoundsLeft + "," + monitor.BoundsTop + "," + monitor.Width + "," + monitor.Height +
            " work=" + monitor.WorkAreaLeft + "," + monitor.WorkAreaTop + "," + monitor.WorkingWidth + "," + monitor.WorkingHeight +
            " bpp=" + monitor.BitsPerPixel + " orientation=" + monitor.Orientation + " device_hash=" + DeviceHash(monitor.DeviceName)));
        private static string DeviceHash(string name)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name ?? string.Empty))).Replace("-", "").ToLowerInvariant();
        }
        public string LogoFile => _working.LogoFile;
        public bool CanImportLogo => _importLogo != null && !_logoBusy;
        public bool CanSave => !_logoBusy;

        public async Task ImportLogoAsync(string path)
        {
            if (!CanImportLogo) throw new InvalidOperationException("logo_import_unavailable");
            _logoBusy = true; OnPropertyChanged(nameof(CanImportLogo)); OnPropertyChanged(nameof(CanSave));
            try
            {
                var reference = await _importLogo(path).ConfigureAwait(true);
                _working.LogoFile = reference.FileName;
                _working.LogoHash = reference.Hash;
                OnPropertyChanged(nameof(LogoFile));
            }
            finally { _logoBusy = false; OnPropertyChanged(nameof(CanImportLogo)); OnPropertyChanged(nameof(CanSave)); }
        }

        public void RemoveLogo()
        {
            if (_logoBusy) return;
            _working.LogoFile = _working.LogoHash = string.Empty;
            OnPropertyChanged(nameof(LogoFile));
        }
        public bool ShowUnitPrice { get => _working.ShowUnitPrice; set { _working.ShowUnitPrice = value; OnPropertyChanged(); } }
        public bool ShowLineTotal { get => _working.ShowLineTotal; set { _working.ShowLineTotal = value; OnPropertyChanged(); } }
        public bool ShowSubtotal { get => _working.ShowSubtotal; set { _working.ShowSubtotal = value; OnPropertyChanged(); } }
        public bool ShowDiscount { get => _working.ShowDiscount; set { _working.ShowDiscount = value; OnPropertyChanged(); } }
        public bool ShowItemCount { get => _working.ShowItemCount; set { _working.ShowItemCount = value; OnPropertyChanged(); } }
        public CustomerDisplayFontScale FontScale { get => _working.FontScale; set { _working.FontScale = value; OnPropertyChanged(); } }
        public CustomerDisplayTheme Theme { get => _working.Theme; set { _working.Theme = value; OnPropertyChanged(); } }
        public CustomerDisplayLanguage CustomerLanguage { get => _working.CustomerLanguage; set { _working.CustomerLanguage = value; OnPropertyChanged(); } }
        public int ThankYouSeconds { get => _working.ThankYouSeconds; set { _working.ThankYouSeconds = value; OnPropertyChanged(); } }
        public bool ReopenOnReturn { get => _working.ReopenWhenMonitorReturns; set { _working.ReopenWhenMonitorReturns = value; OnPropertyChanged(); } }

        public void InvertMonitors()
        {
            var current = CashierMonitor;
            CashierMonitor = CustomerMonitor;
            CustomerMonitor = current;
            Manual = true;
        }

        public void ResetAutomatic()
        {
            Automatic = true;
            CashierMonitor = _monitors.FirstOrDefault(x => x.IsPrimary)?.DeviceName ?? _monitors.FirstOrDefault()?.DeviceName ?? string.Empty;
            CustomerMonitor = _monitors.FirstOrDefault(x => !string.Equals(x.DeviceName, CashierMonitor, StringComparison.OrdinalIgnoreCase))?.DeviceName ?? string.Empty;
        }

        public bool TryBuild(out CustomerDisplaySettings settings, out string errorCode)
        {
            settings = _working.Clone();
            if (!int.TryParse(_testDurationText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 5 || seconds > 60)
            {
                errorCode = "test_pattern_duration";
                return false;
            }
            settings.TestPatternDurationSeconds = seconds;
            var errors = settings.Validate();
            if (errors.Count > 0)
            {
                errorCode = errors[0];
                return false;
            }

            var selection = CustomerDisplayMonitorPolicy.Select(_monitors.Select(x => x.ToDescriptor()), settings);
            if (settings.Enabled && selection.Customer == null)
            {
                errorCode = selection.ErrorCode;
                return false;
            }

            errorCode = string.Empty;
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class MonitorChoice
    {
        public string DeviceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public bool IsAvailable { get; set; }
    }

    public sealed class CustomerDisplayOption
    {
        public object Value { get; set; }
        public string Label { get; set; }
    }
}
