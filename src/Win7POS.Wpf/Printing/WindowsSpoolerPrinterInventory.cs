using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Win7POS.Wpf.Printing
{
    /// <summary>
    /// Reads queue metadata with spooler APIs that are available on Windows 7.
    /// Level 4 enumeration is deliberately used for the global inventory; the
    /// heavier level 2 lookup is isolated to one already-known queue at a time.
    /// </summary>
    internal static class WindowsSpoolerPrinterInventory
    {
        internal const uint PrinterStatusPaused = 0x00000001;
        internal const uint PrinterStatusError = 0x00000002;
        internal const uint PrinterStatusPendingDeletion = 0x00000004;
        internal const uint PrinterStatusPaperJam = 0x00000008;
        internal const uint PrinterStatusPaperOut = 0x00000010;
        internal const uint PrinterStatusManualFeed = 0x00000020;
        internal const uint PrinterStatusPaperProblem = 0x00000040;
        internal const uint PrinterStatusOffline = 0x00000080;
        internal const uint PrinterStatusIoActive = 0x00000100;
        internal const uint PrinterStatusBusy = 0x00000200;
        internal const uint PrinterStatusPrinting = 0x00000400;
        internal const uint PrinterStatusOutputBinFull = 0x00000800;
        internal const uint PrinterStatusNotAvailable = 0x00001000;
        internal const uint PrinterStatusWaiting = 0x00002000;
        internal const uint PrinterStatusProcessing = 0x00004000;
        internal const uint PrinterStatusInitializing = 0x00008000;
        internal const uint PrinterStatusWarmingUp = 0x00010000;
        internal const uint PrinterStatusTonerLow = 0x00020000;
        internal const uint PrinterStatusNoToner = 0x00040000;
        internal const uint PrinterStatusPagePunt = 0x00080000;
        internal const uint PrinterStatusUserIntervention = 0x00100000;
        internal const uint PrinterStatusOutOfMemory = 0x00200000;
        internal const uint PrinterStatusDoorOpen = 0x00400000;
        internal const uint PrinterStatusServerUnknown = 0x00800000;
        internal const uint PrinterStatusPowerSave = 0x01000000;
        internal const uint PrinterStatusServerOffline = 0x02000000;

        internal const uint PrinterAttributeWorkOffline = 0x00000400;
        internal const uint PrinterAttributeFax = 0x00004000;

        private const uint PrinterEnumLocal = 0x00000002;
        private const uint PrinterEnumConnections = 0x00000004;
        private const int ErrorInsufficientBuffer = 122;

        internal static bool TryGetInstalledPrinters(out IReadOnlyList<WindowsSpoolerPrinterInfo> printers)
        {
            var result = new List<WindowsSpoolerPrinterInfo>();
            printers = result;

            try
            {
                uint bytesNeeded;
                uint printerCount;
                var flags = PrinterEnumLocal | PrinterEnumConnections;
                var firstCallSucceeded = EnumPrintersW(
                    flags,
                    null,
                    4,
                    IntPtr.Zero,
                    0,
                    out bytesNeeded,
                    out printerCount);

                if (!firstCallSucceeded && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return false;

                if (bytesNeeded == 0)
                    return true;

                if (bytesNeeded > int.MaxValue)
                    return false;

                var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    if (!EnumPrintersW(
                        flags,
                        null,
                        4,
                        buffer,
                        bytesNeeded,
                        out bytesNeeded,
                        out printerCount))
                    {
                        return false;
                    }

                    var itemSize = Marshal.SizeOf(typeof(PrinterInfo4));
                    var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    for (uint index = 0; index < printerCount; index++)
                    {
                        var itemPointer = IntPtr.Add(buffer, checked((int)index * itemSize));
                        var item = (PrinterInfo4)Marshal.PtrToStructure(itemPointer, typeof(PrinterInfo4));
                        var name = Normalize(item.PrinterName);
                        if (name.Length == 0 || !knownNames.Add(name))
                            continue;

                        PrinterInfo2 details;
                        var detailsAvailable = TryGetPrinterDetailsSafely(name, out details);
                        result.Add(new WindowsSpoolerPrinterInfo
                        {
                            Name = name,
                            DriverName = detailsAvailable ? Normalize(details.DriverName) : string.Empty,
                            PortName = detailsAvailable ? Normalize(details.PortName) : string.Empty,
                            Attributes = detailsAvailable ? details.Attributes : item.Attributes,
                            Status = detailsAvailable ? details.Status : 0,
                            HasDevMode = detailsAvailable && details.DevMode != IntPtr.Zero,
                            DetailsAvailable = detailsAvailable
                        });
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                return true;
            }
            catch (Exception ex) when (
                ex is DllNotFoundException ||
                ex is EntryPointNotFoundException ||
                ex is ExternalException ||
                ex is OverflowException ||
                ex is ArgumentException)
            {
                return false;
            }
        }

        internal static bool TryGetDefaultPrinterName(out string printerName)
        {
            printerName = string.Empty;

            try
            {
                uint characterCount = 0;
                var firstCallSucceeded = GetDefaultPrinterW(null, ref characterCount);
                if (!firstCallSucceeded && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return false;

                if (characterCount == 0 || characterCount > int.MaxValue)
                    return false;

                var buffer = new StringBuilder((int)characterCount);
                if (!GetDefaultPrinterW(buffer, ref characterCount))
                    return false;

                printerName = Normalize(buffer.ToString());
                return printerName.Length > 0;
            }
            catch (Exception ex) when (
                ex is DllNotFoundException ||
                ex is EntryPointNotFoundException ||
                ex is ExternalException ||
                ex is ArgumentException)
            {
                printerName = string.Empty;
                return false;
            }
        }

        private static bool TryGetPrinterDetails(string printerName, out PrinterInfo2 details)
        {
            details = default(PrinterInfo2);
            IntPtr printerHandle;
            if (!OpenPrinterW(printerName, out printerHandle, IntPtr.Zero) || printerHandle == IntPtr.Zero)
                return false;

            try
            {
                uint bytesNeeded;
                var firstCallSucceeded = GetPrinterW(printerHandle, 2, IntPtr.Zero, 0, out bytesNeeded);
                if (!firstCallSucceeded && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return false;

                if (bytesNeeded == 0 || bytesNeeded > int.MaxValue)
                    return false;

                // A queue can change between the size probe and the read. Retry
                // once if the spooler reports a larger buffer requirement.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var allocatedSize = bytesNeeded;
                    var buffer = Marshal.AllocHGlobal((int)allocatedSize);
                    try
                    {
                        if (GetPrinterW(printerHandle, 2, buffer, allocatedSize, out bytesNeeded))
                        {
                            details = (PrinterInfo2)Marshal.PtrToStructure(buffer, typeof(PrinterInfo2));
                            return true;
                        }

                        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer ||
                            bytesNeeded == 0 ||
                            bytesNeeded > int.MaxValue ||
                            bytesNeeded <= allocatedSize)
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }

                return false;
            }
            finally
            {
                ClosePrinter(printerHandle);
            }
        }

        private static bool TryGetPrinterDetailsSafely(string printerName, out PrinterInfo2 details)
        {
            try
            {
                return TryGetPrinterDetails(printerName, out details);
            }
            catch (Exception ex) when (
                ex is DllNotFoundException ||
                ex is EntryPointNotFoundException ||
                ex is ExternalException ||
                ex is OverflowException ||
                ex is ArgumentException)
            {
                details = default(PrinterInfo2);
                return false;
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PrinterInfo4
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string PrinterName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string ServerName;

            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PrinterInfo2
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string ServerName;
            [MarshalAs(UnmanagedType.LPWStr)] public string PrinterName;
            [MarshalAs(UnmanagedType.LPWStr)] public string ShareName;
            [MarshalAs(UnmanagedType.LPWStr)] public string PortName;
            [MarshalAs(UnmanagedType.LPWStr)] public string DriverName;
            [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
            [MarshalAs(UnmanagedType.LPWStr)] public string Location;
            public IntPtr DevMode;
            [MarshalAs(UnmanagedType.LPWStr)] public string SeparatorFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string PrintProcessor;
            [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
            [MarshalAs(UnmanagedType.LPWStr)] public string Parameters;
            public IntPtr SecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint JobCount;
            public uint AveragePagesPerMinute;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumPrintersW(
            uint flags,
            string name,
            uint level,
            IntPtr printerEnum,
            uint bufferSize,
            out uint bytesNeeded,
            out uint printerCount);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenPrinterW(
            string printerName,
            out IntPtr printerHandle,
            IntPtr defaults);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPrinterW(
            IntPtr printerHandle,
            uint level,
            IntPtr printer,
            uint bufferSize,
            out uint bytesNeeded);

        [DllImport("winspool.drv", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClosePrinter(IntPtr printerHandle);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDefaultPrinterW(StringBuilder printerName, ref uint characterCount);
    }

    internal sealed class WindowsSpoolerPrinterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DriverName { get; set; } = string.Empty;
        public string PortName { get; set; } = string.Empty;
        public uint Attributes { get; set; }
        public uint Status { get; set; }
        public bool HasDevMode { get; set; }
        public bool DetailsAvailable { get; set; }
    }
}
