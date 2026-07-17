using System;
using System.Collections.Generic;

namespace Win7POS.Core.Pos
{
    public enum CustomerDisplaySelectionMode { Auto, Manual }
    public enum CustomerDisplayFontScale { Small, Medium, Large }
    public enum CustomerDisplayTheme { Light, Dark, HighContrast }
    public enum CustomerDisplayLanguage { FollowApplication, IT, EN, ES, ZH }

    public sealed class CustomerDisplaySettings
    {
        public bool Enabled { get; set; }
        public CustomerDisplaySelectionMode SelectionMode { get; set; }
        public string CashierMonitorDeviceName { get; set; } = string.Empty;
        public string CustomerMonitorDeviceName { get; set; } = string.Empty;
        public bool AutoOpen { get; set; }
        public bool FullScreen { get; set; }
        public bool UseWorkingArea { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool FollowCashierMinimize { get; set; }
        public bool ShowShopName { get; set; }
        public bool ShowBarcode { get; set; }
        public bool ShowUnitPrice { get; set; }
        public bool ShowLineTotal { get; set; }
        public bool ShowSubtotal { get; set; }
        public bool ShowDiscount { get; set; }
        public bool ShowItemCount { get; set; }
        public CustomerDisplayFontScale FontScale { get; set; }
        public CustomerDisplayTheme Theme { get; set; }
        public CustomerDisplayLanguage CustomerLanguage { get; set; }
        public int ThankYouSeconds { get; set; }
        public bool ReopenWhenMonitorReturns { get; set; }

        public static CustomerDisplaySettings CreateDefault(int independentMonitorCount)
        {
            return new CustomerDisplaySettings
            {
                Enabled = independentMonitorCount > 1,
                SelectionMode = CustomerDisplaySelectionMode.Auto,
                AutoOpen = true,
                FullScreen = true,
                UseWorkingArea = false,
                AlwaysOnTop = true,
                FollowCashierMinimize = true,
                ShowShopName = true,
                ShowBarcode = false,
                ShowUnitPrice = true,
                ShowLineTotal = true,
                ShowSubtotal = true,
                ShowDiscount = true,
                ShowItemCount = true,
                FontScale = CustomerDisplayFontScale.Medium,
                Theme = CustomerDisplayTheme.Dark,
                CustomerLanguage = CustomerDisplayLanguage.FollowApplication,
                ThankYouSeconds = 5,
                ReopenWhenMonitorReturns = true
            };
        }

        public CustomerDisplaySettings Clone()
        {
            return (CustomerDisplaySettings)MemberwiseClone();
        }

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (ThankYouSeconds < 1 || ThankYouSeconds > 30)
            {
                errors.Add("thank_you_seconds");
            }

            if (SelectionMode == CustomerDisplaySelectionMode.Manual &&
                string.IsNullOrWhiteSpace(CustomerMonitorDeviceName))
            {
                errors.Add("customer_monitor_required");
            }

            if (!string.IsNullOrWhiteSpace(CashierMonitorDeviceName) &&
                string.Equals(
                    CashierMonitorDeviceName.Trim(),
                    (CustomerMonitorDeviceName ?? string.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("same_monitor");
            }

            return errors.AsReadOnly();
        }
    }
}
