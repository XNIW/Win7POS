using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Infrastructure.Displays;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class CustomerDisplaySettingsViewModel : INotifyPropertyChanged
    {
        private readonly IReadOnlyList<DisplayMonitorInfo> _monitors;
        private readonly CustomerDisplaySettings _working;

        public CustomerDisplaySettingsViewModel(
            CustomerDisplaySettings settings,
            IReadOnlyList<DisplayMonitorInfo> monitors)
        {
            _working = settings?.Clone() ?? CustomerDisplaySettings.CreateDefault(monitors?.Count ?? 0);
            _monitors = monitors ?? new List<DisplayMonitorInfo>().AsReadOnly();
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
}
