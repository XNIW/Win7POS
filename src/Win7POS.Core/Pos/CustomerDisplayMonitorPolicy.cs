using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Win7POS.Core.Pos
{
    public sealed class CustomerDisplayMonitorDescriptor
    {
        public CustomerDisplayMonitorDescriptor() { }

        public CustomerDisplayMonitorDescriptor(
            string deviceName,
            bool isPrimary,
            int left,
            int top,
            int width,
            int height)
        {
            DeviceName = deviceName ?? string.Empty;
            IsPrimary = isPrimary;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public string DeviceName { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public enum CustomerDisplayTopologyMode
    {
        Single,
        Extended,
        Duplicate
    }

    public sealed class CustomerDisplayMonitorSelection
    {
        public CustomerDisplayTopologyMode TopologyMode { get; set; }
        public CustomerDisplayMonitorDescriptor Cashier { get; set; }
        public CustomerDisplayMonitorDescriptor Customer { get; set; }
        public string ErrorCode { get; set; } = string.Empty;
        public IReadOnlyList<CustomerDisplayMonitorDescriptor> IndependentMonitors { get; set; } =
            new ReadOnlyCollection<CustomerDisplayMonitorDescriptor>(new List<CustomerDisplayMonitorDescriptor>());
    }

    public static class CustomerDisplayMonitorPolicy
    {
        public static CustomerDisplayMonitorSelection Select(
            IEnumerable<CustomerDisplayMonitorDescriptor> available,
            CustomerDisplaySettings settings)
        {
            var raw = (available ?? Enumerable.Empty<CustomerDisplayMonitorDescriptor>())
                .Where(IsValid)
                .ToList();
            var independent = raw
                .GroupBy(BoundsKey, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase).First())
                .OrderBy(x => x.Left)
                .ThenBy(x => x.Top)
                .ThenBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new CustomerDisplayMonitorSelection
            {
                TopologyMode = raw.Count > independent.Count
                    ? CustomerDisplayTopologyMode.Duplicate
                    : independent.Count > 1 ? CustomerDisplayTopologyMode.Extended : CustomerDisplayTopologyMode.Single,
                IndependentMonitors = new ReadOnlyCollection<CustomerDisplayMonitorDescriptor>(independent)
            };

            if (independent.Count == 0)
            {
                result.ErrorCode = "no_monitors";
                return result;
            }

            settings = settings ?? CustomerDisplaySettings.CreateDefault(independent.Count);
            result.Cashier = Find(independent, settings.CashierMonitorDeviceName) ??
                             independent.FirstOrDefault(x => x.IsPrimary) ?? independent[0];

            if (result.TopologyMode == CustomerDisplayTopologyMode.Duplicate)
            {
                result.ErrorCode = "extend_required";
                return result;
            }

            if (settings.SelectionMode == CustomerDisplaySelectionMode.Manual)
            {
                result.Customer = Find(independent, settings.CustomerMonitorDeviceName);
                if (result.Customer == null)
                {
                    result.ErrorCode = "selected_monitor_missing";
                    return result;
                }
            }
            else
            {
                var lastValid = Find(independent, settings.CustomerMonitorDeviceName);
                result.Customer = lastValid != null && !Same(result.Cashier, lastValid)
                    ? lastValid
                    : independent.FirstOrDefault(x => !Same(result.Cashier, x));
            }

            if (result.Customer == null)
            {
                result.ErrorCode = "customer_monitor_unavailable";
            }
            else if (Same(result.Cashier, result.Customer))
            {
                result.Customer = null;
                result.ErrorCode = "same_monitor";
            }

            return result;
        }

        public static bool Same(CustomerDisplayMonitorDescriptor left, CustomerDisplayMonitorDescriptor right)
        {
            if (left == null || right == null) return false;
            return string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                   BoundsKey(left) == BoundsKey(right);
        }

        private static CustomerDisplayMonitorDescriptor Find(
            IEnumerable<CustomerDisplayMonitorDescriptor> monitors,
            string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return null;
            return monitors.FirstOrDefault(x =>
                string.Equals(x.DeviceName, deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsValid(CustomerDisplayMonitorDescriptor monitor)
        {
            return monitor != null && monitor.Width > 0 && monitor.Height > 0;
        }

        private static string BoundsKey(CustomerDisplayMonitorDescriptor monitor)
        {
            return monitor.Left + ":" + monitor.Top + ":" + monitor.Width + ":" + monitor.Height;
        }
    }
}
