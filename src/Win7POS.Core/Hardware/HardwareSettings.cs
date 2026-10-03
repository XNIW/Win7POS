using System;
using System.Collections.Generic;

namespace Win7POS.Core.Hardware
{
    public sealed class HardwareSettings
    {
        public ScannerInputSettings Scanner { get; set; } = new ScannerInputSettings();
        public bool ReceiptEnabled { get; set; }
        public string PrinterName { get; set; } = string.Empty;
        public bool AutoPrint { get; set; }
        public int Copies { get; set; } = 1;
        public bool AllowWindowsDefault { get; set; }
        public bool AllowVirtualPrinters { get; set; }
        public ReceiptProfile ReceiptProfile { get; set; } = ReceiptProfile.Thermal80mm42col;
        public string CashDrawerMode { get; set; } = "disabled";
        public string CashDrawerPrinterName { get; set; } = string.Empty;
        public bool CashDrawerOpenOnCashSale { get; set; } = true;
        public CashDrawerPreset CashDrawerPreset { get; set; } = CashDrawerPreset.EscposPin2;
        public string CashDrawerCustomCommand { get; set; } = CashDrawerPresetPolicy.Pin2Command;
        public string CashDrawerCommand => CashDrawerPresetPolicy.Command(CashDrawerPreset, CashDrawerCustomCommand);

        public HardwareSettings Copy()
        {
            var copy = (HardwareSettings)MemberwiseClone();
            copy.Scanner = Scanner?.Copy();
            return copy;
        }
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>(ScannerInputPolicy.Validate(Scanner));
            if (Copies < 1 || Copies > 3) errors.Add("receipt.copies");
            if (!Enum.IsDefined(typeof(ReceiptProfile), ReceiptProfile)) errors.Add("receipt.profile");
            if (!QueueNameValid(PrinterName) || !QueueNameValid(CashDrawerPrinterName)) errors.Add("printer.name");
            if (ReceiptEnabled && string.IsNullOrWhiteSpace(PrinterName) && !AllowWindowsDefault) errors.Add("receipt.target");
            if (AutoPrint && !ReceiptEnabled) errors.Add("receipt.auto_print");
            if (CashDrawerMode != "disabled" && CashDrawerMode != "printer_kick") errors.Add("drawer.mode");
            if (!CashDrawerPresetPolicy.TryGetBytes(CashDrawerPreset, CashDrawerCustomCommand, out _)) errors.Add("drawer.command");
            if (CashDrawerMode == "printer_kick" && string.IsNullOrWhiteSpace(CashDrawerPrinterName) && string.IsNullOrWhiteSpace(PrinterName)) errors.Add("drawer.target");
            return errors;
        }
        private static bool QueueNameValid(string value)
        {
            if (value == null || value.Length > 255) return false;
            foreach (var c in value) if (char.IsControl(c)) return false;
            return true;
        }
    }
}
