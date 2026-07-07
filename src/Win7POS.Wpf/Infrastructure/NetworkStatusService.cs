using System;
using System.Linq;
using System.Net.NetworkInformation;

namespace Win7POS.Wpf.Infrastructure
{
    public sealed class NetworkStatusSnapshot
    {
        public bool IsNetworkAvailable { get; set; }
        public bool HasWifiAdapterUp { get; set; }
        public string DisplayText { get; set; }
    }

    public static class NetworkStatusService
    {
        public static NetworkStatusSnapshot Read()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var up = interfaces.Any(x =>
                    x.OperationalStatus == OperationalStatus.Up &&
                    x.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    x.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

                var wifiUp = interfaces.Any(x =>
                    x.OperationalStatus == OperationalStatus.Up &&
                    x.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

                return new NetworkStatusSnapshot
                {
                    IsNetworkAvailable = NetworkInterface.GetIsNetworkAvailable() || up,
                    HasWifiAdapterUp = wifiUp,
                    DisplayText = up ? "Online" : "Offline"
                };
            }
            catch
            {
                return new NetworkStatusSnapshot
                {
                    IsNetworkAvailable = false,
                    HasWifiAdapterUp = false,
                    DisplayText = "Offline"
                };
            }
        }
    }
}
