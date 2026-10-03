using System;
using System.Threading.Tasks;
using Win7POS.Core.Hardware;

namespace Win7POS.Wpf.Printing
{
    public sealed class PrinterQueueDiagnostic
    {
        public string QueueName { get; set; } = string.Empty;
        public string DriverName { get; set; } = string.Empty;
        public string PortName { get; set; } = string.Empty;
        public uint Status { get; set; }
        public uint JobCount { get; set; }
        public bool JobsAvailable { get; set; }
        public bool Available { get; set; }
    }

    public static class WindowsPrinterDiagnostics
    {
        private static readonly SingleFlightDiagnostic<PrinterQueueDiagnostic> Flight = new SingleFlightDiagnostic<PrinterQueueDiagnostic>();
        public static Task<DiagnosticResult<PrinterQueueDiagnostic>> ReadAsync(string queueName)
        {
            var name = (queueName ?? string.Empty).Trim();
            return Flight.RunAsync(name, () =>
            {
                if (App.IsSafeStart || name.Length == 0 || name.Length > 255)
                    return new PrinterQueueDiagnostic { QueueName = name };
                return WindowsSpoolerPrinterInventory.ReadDiagnostic(name);
            }, TimeSpan.FromSeconds(5));
        }
    }
}
