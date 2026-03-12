using System;
using System.Runtime.InteropServices;

namespace Win7POS.Wpf.Printing
{
    /// <summary>Invio raw bytes alla stampante Windows (es. ESC/POS per aprire cassetto).</summary>
    internal static class RawPrinterHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct DocInfo1
        {
            [MarshalAs(UnmanagedType.LPTStr)]
            public string pDocName;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string pOutputFile;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string pDataType;
        }

        [DllImport("winspool.Drv", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DocInfo1 pDocInfo);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        public static void SendBytesToPrinter(string printerName, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(printerName) || bytes == null || bytes.Length == 0) return;
            IntPtr hPrinter = IntPtr.Zero;
            try
            {
                if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                    throw new InvalidOperationException("Impossibile aprire la stampante: " + printerName);
                var docInfo = new DocInfo1
                {
                    pDocName = "CashDrawer",
                    pOutputFile = null,
                    pDataType = "RAW"
                };
                if (!StartDocPrinter(hPrinter, 1, ref docInfo))
                    throw new InvalidOperationException("StartDocPrinter failed.");
                try
                {
                    if (!StartPagePrinter(hPrinter))
                        throw new InvalidOperationException("StartPagePrinter failed.");
                    try
                    {
                        IntPtr pBytes = Marshal.AllocCoTaskMem(bytes.Length);
                        try
                        {
                            Marshal.Copy(bytes, 0, pBytes, bytes.Length);
                            if (!WritePrinter(hPrinter, pBytes, bytes.Length, out int written) || written != bytes.Length)
                                throw new InvalidOperationException("WritePrinter failed.");
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(pBytes);
                        }
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }
            }
            finally
            {
                if (hPrinter != IntPtr.Zero)
                    ClosePrinter(hPrinter);
            }
        }
    }
}
