using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;

namespace Win7POS.Wpf.Printing
{
    public static class WindowsPrinterDiscovery
    {
        private const uint BlockingStatusMask =
            WindowsSpoolerPrinterInventory.PrinterStatusPaused |
            WindowsSpoolerPrinterInventory.PrinterStatusError |
            WindowsSpoolerPrinterInventory.PrinterStatusPendingDeletion |
            WindowsSpoolerPrinterInventory.PrinterStatusPaperJam |
            WindowsSpoolerPrinterInventory.PrinterStatusPaperOut |
            WindowsSpoolerPrinterInventory.PrinterStatusPaperProblem |
            WindowsSpoolerPrinterInventory.PrinterStatusOffline |
            WindowsSpoolerPrinterInventory.PrinterStatusOutputBinFull |
            WindowsSpoolerPrinterInventory.PrinterStatusNotAvailable |
            WindowsSpoolerPrinterInventory.PrinterStatusNoToner |
            WindowsSpoolerPrinterInventory.PrinterStatusUserIntervention |
            WindowsSpoolerPrinterInventory.PrinterStatusOutOfMemory |
            WindowsSpoolerPrinterInventory.PrinterStatusDoorOpen |
            WindowsSpoolerPrinterInventory.PrinterStatusServerUnknown |
            WindowsSpoolerPrinterInventory.PrinterStatusServerOffline;

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

        private static readonly string[] VirtualPortHints =
        {
            "file:",
            "portprompt:",
            "xpsport:",
            "fax:"
        };

        private static readonly string[] PhysicalPortPrefixes =
        {
            "ESDPRT",
            "USB",
            "DOT4",
            "LPT",
            "COM",
            "WSD",
            "IP_",
            "TCP_"
        };

        public static IReadOnlyList<InstalledPrinterInfo> GetInstalledPrinters()
        {
            var printersByName = new Dictionary<string, InstalledPrinterInfo>(StringComparer.OrdinalIgnoreCase);
            var defaultName = GetDefaultPrinterName();

            IReadOnlyList<WindowsSpoolerPrinterInfo> spoolerPrinters;
            var spoolerInventoryAvailable = WindowsSpoolerPrinterInventory.TryGetInstalledPrinters(
                out spoolerPrinters);

            if (spoolerInventoryAvailable)
            {
                foreach (var printer in spoolerPrinters)
                {
                    if (printer == null || string.IsNullOrWhiteSpace(printer.Name))
                        continue;

                    var info = CreateFromSpooler(printer, defaultName);
                    printersByName[info.Name] = info;
                }
            }

            // Keep System.Drawing discovery as a compatibility fallback. It also
            // fills a queue that may have appeared between the two snapshots.
            foreach (var printerName in GetManagedPrinterNames())
            {
                if (printersByName.ContainsKey(printerName))
                    continue;

                var info = CreateFromManagedPrinter(printerName, defaultName, spoolerInventoryAvailable);
                printersByName[info.Name] = info;
            }

            return printersByName.Values
                .OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string GetDefaultPrinterName()
        {
            string printerName;
            if (WindowsSpoolerPrinterInventory.TryGetDefaultPrinterName(out printerName))
                return printerName;

            try
            {
                using (var document = new PrintDocument())
                {
                    return (document.PrinterSettings.PrinterName ?? string.Empty).Trim();
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
            return IsLikelyVirtualPrinter(printerName, string.Empty, string.Empty);
        }

        public static bool IsLikelyVirtualPrinter(string printerName, string driverName, string portName)
        {
            return ClassifyOutputKind(printerName, driverName, portName, 0) ==
                   PrinterOutputKind.Virtual;
        }

        public static PrinterOutputKind ClassifyOutputKind(
            string printerName,
            string driverName,
            string portName)
        {
            return ClassifyOutputKind(printerName, driverName, portName, 0);
        }

        private static PrinterOutputKind ClassifyOutputKind(
            string printerName,
            string driverName,
            string portName,
            uint printerAttributes)
        {
            if ((printerAttributes & WindowsSpoolerPrinterInventory.PrinterAttributeFax) != 0)
                return PrinterOutputKind.Virtual;

            var textValues = new[]
            {
                (printerName ?? string.Empty).Trim(),
                (driverName ?? string.Empty).Trim()
            };

            if (textValues.Any(value => VirtualPrinterHints.Any(
                hint => value.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                return PrinterOutputKind.Virtual;
            }

            var port = (portName ?? string.Empty).Trim();
            if (VirtualPortHints.Any(
                hint => port.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return PrinterOutputKind.Virtual;
            }

            if (PhysicalPortPrefixes.Any(prefix =>
                port.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return PrinterOutputKind.Physical;
            }

            return PrinterOutputKind.Unknown;
        }

        private static InstalledPrinterInfo CreateFromSpooler(
            WindowsSpoolerPrinterInfo printer,
            string defaultName)
        {
            var isPaused = printer.DetailsAvailable && HasStatus(
                printer.Status,
                WindowsSpoolerPrinterInventory.PrinterStatusPaused);
            var isOffline = printer.DetailsAvailable && HasStatus(
                printer.Status,
                WindowsSpoolerPrinterInventory.PrinterStatusOffline |
                WindowsSpoolerPrinterInventory.PrinterStatusNotAvailable |
                WindowsSpoolerPrinterInventory.PrinterStatusServerUnknown |
                WindowsSpoolerPrinterInventory.PrinterStatusServerOffline);
            isOffline = isOffline || (printer.Attributes &
                WindowsSpoolerPrinterInventory.PrinterAttributeWorkOffline) != 0;

            var hasUsableMetadata = printer.DetailsAvailable &&
                                    printer.HasDevMode &&
                                    !string.IsNullOrWhiteSpace(printer.DriverName) &&
                                    !string.IsNullOrWhiteSpace(printer.PortName);
            var isAvailable = hasUsableMetadata &&
                              (printer.Status & BlockingStatusMask) == 0 &&
                              !isOffline;

            return new InstalledPrinterInfo
            {
                Name = printer.Name,
                IsDefault = string.Equals(printer.Name, defaultName, StringComparison.OrdinalIgnoreCase),
                OutputKind = ClassifyOutputKind(
                    printer.Name,
                    printer.DriverName,
                    printer.PortName,
                    printer.Attributes),
                IsInventoryFresh = true,
                IsAvailable = isAvailable,
                IsOffline = isOffline,
                IsPaused = isPaused,
                DriverName = printer.DriverName,
                PortName = printer.PortName,
                StatusText = DescribeStatus(
                    printer.Status,
                    printer.Attributes,
                    printer.DetailsAvailable,
                    isAvailable),
                Notes = printer.DetailsAvailable
                    ? "Windows spooler queue metadata available."
                    : "Windows spooler queue details unavailable; queue left unavailable."
            };
        }

        private static InstalledPrinterInfo CreateFromManagedPrinter(
            string printerName,
            string defaultName,
            bool spoolerInventoryAvailable)
        {
            var managedQueueValid = IsManagedPrinterValid(printerName);
            var outputKind = ClassifyOutputKind(printerName, string.Empty, string.Empty);
            return new InstalledPrinterInfo
            {
                Name = printerName,
                IsDefault = string.Equals(printerName, defaultName, StringComparison.OrdinalIgnoreCase),
                OutputKind = outputKind,
                IsInventoryFresh = true,
                // Managed enumeration does not expose enough metadata to prove
                // that a queue is physical. A valid queue remains available for
                // an explicit, operator-approved virtual/unknown print only.
                IsAvailable = managedQueueValid,
                IsOffline = false,
                IsPaused = false,
                DriverName = string.Empty,
                PortName = string.Empty,
                StatusText = managedQueueValid ? "status unavailable" : "not available",
                Notes = spoolerInventoryAvailable
                    ? "Managed printer fallback; queue appeared outside the spooler snapshot."
                    : "Managed printer fallback; spooler metadata unavailable."
            };
        }

        private static IReadOnlyList<string> GetManagedPrinterNames()
        {
            var names = new List<string>();
            try
            {
                foreach (string printerName in PrinterSettings.InstalledPrinters)
                {
                    var name = (printerName ?? string.Empty).Trim();
                    if (name.Length > 0)
                        names.Add(name);
                }
            }
            catch
            {
                // Native spooler inventory can still be used when the managed
                // System.Drawing projection is temporarily unavailable.
            }

            return names;
        }

        private static bool IsManagedPrinterValid(string printerName)
        {
            try
            {
                using (var document = new PrintDocument())
                {
                    document.PrinterSettings.PrinterName = printerName;
                    return document.PrinterSettings.IsValid;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeStatus(
            uint status,
            uint attributes,
            bool detailsAvailable,
            bool isAvailable)
        {
            if (!detailsAvailable)
                return isAvailable ? "available (status unavailable)" : "not available";

            var states = new List<string>();
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPaused, "paused");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusOffline, "offline");
            if ((attributes & WindowsSpoolerPrinterInventory.PrinterAttributeWorkOffline) != 0 &&
                !HasStatus(status, WindowsSpoolerPrinterInventory.PrinterStatusOffline))
            {
                states.Add("offline");
            }
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusNotAvailable, "not available");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusServerUnknown, "server unavailable");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusServerOffline, "server offline");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusError, "error");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPaperJam, "paper jam");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPaperOut, "paper out");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPaperProblem, "paper problem");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusDoorOpen, "door open");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusUserIntervention, "user intervention required");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusNoToner, "no toner");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusOutputBinFull, "output bin full");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusOutOfMemory, "out of memory");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPendingDeletion, "pending deletion");

            if (states.Count > 0)
                return string.Join(", ", states);

            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPrinting, "printing");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusProcessing, "processing");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusBusy, "busy");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusInitializing, "initializing");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusWarmingUp, "warming up");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusWaiting, "waiting");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusIoActive, "I/O active");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusManualFeed, "manual feed");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusTonerLow, "toner low");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPagePunt, "page punt");
            AddState(states, status, WindowsSpoolerPrinterInventory.PrinterStatusPowerSave, "power save");

            return states.Count > 0 ? string.Join(", ", states) : (isAvailable ? "available" : "not available");
        }

        private static void AddState(List<string> states, uint status, uint flag, string text)
        {
            if (HasStatus(status, flag))
                states.Add(text);
        }

        private static bool HasStatus(uint status, uint flag)
        {
            return (status & flag) != 0;
        }
    }
}
