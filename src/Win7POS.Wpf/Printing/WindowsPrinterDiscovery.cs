using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;

namespace Win7POS.Wpf.Printing
{
    public static class WindowsPrinterDiscovery
    {
        private static readonly string[] VirtualPrinterHints =
        {
            "pdf",
            "xps",
            "onenote",
            "fax",
            "document writer",
            "print to file",
            "snagit",
            "send to"
        };

        public static IReadOnlyList<InstalledPrinterInfo> GetInstalledPrinters()
        {
            var result = new List<InstalledPrinterInfo>();
            var defaultName = GetDefaultPrinterName();

            foreach (string printerName in PrinterSettings.InstalledPrinters)
            {
                if (string.IsNullOrWhiteSpace(printerName))
                    continue;

                var info = new InstalledPrinterInfo
                {
                    Name = printerName.Trim(),
                    IsDefault = string.Equals(printerName.Trim(), defaultName, StringComparison.OrdinalIgnoreCase),
                    IsVirtual = IsLikelyVirtualPrinter(printerName),
                    DriverName = string.Empty,
                    PortName = string.Empty,
                    Notes = "status not fully detectable with Win7-safe API"
                };

                try
                {
                    using (var doc = new PrintDocument())
                    {
                        doc.PrinterSettings.PrinterName = info.Name;
                        info.IsAvailable = doc.PrinterSettings.IsValid;
                    }
                }
                catch
                {
                    info.IsAvailable = false;
                }

                info.StatusText = info.IsAvailable ? "available" : "not available";
                result.Add(info);
            }

            return result
                .OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string GetDefaultPrinterName()
        {
            try
            {
                using (var doc = new PrintDocument())
                {
                    return doc.PrinterSettings.PrinterName ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public static InstalledPrinterInfo FindPrinter(string printerName)
        {
            var name = (printerName ?? string.Empty).Trim();
            if (name.Length == 0)
                return null;

            return GetInstalledPrinters()
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsLikelyVirtualPrinter(string printerName)
        {
            var name = (printerName ?? string.Empty).Trim();
            if (name.Length == 0)
                return false;

            return VirtualPrinterHints.Any(h => name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
