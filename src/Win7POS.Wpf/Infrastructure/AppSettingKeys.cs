namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Chiavi per impostazioni applicazione (riservato per uso futuro).</summary>
    public static class AppSettingKeys
    {
        public const string UiLanguage = "ui.language";

        public const string PosPrinterReceiptEnabled = "pos.printer.receipt.enabled";
        public const string PosPrinterReceiptName = "pos.printer.receipt.name";
        public const string PosPrinterReceiptAutoPrintAfterSale = "pos.printer.receipt.auto_print_after_sale";
        public const string PosPrinterReceiptCopies = "pos.printer.receipt.copies";
        public const string PosPrinterReceiptAllowWindowsDefault = "pos.printer.receipt.allow_windows_default";
        public const string PosPrinterReceiptAllowVirtualPrinters = "pos.printer.receipt.allow_virtual_printers";
        public const string PosPrinterReceiptSaveCopy = "pos.printer.receipt.save_copy";
        public const string PosPrinterReceiptOutputDirectory = "pos.printer.receipt.output_directory";

        public const string PosCashDrawerEnabled = "pos.cashdrawer.enabled";
        public const string PosCashDrawerMode = "pos.cashdrawer.mode";
        public const string PosCashDrawerPrinterName = "pos.cashdrawer.printer_name";
        public const string PosCashDrawerOpenOnCashSale = "pos.cashdrawer.open_on_cash_sale";
        public const string PosCashDrawerCommand = "pos.cashdrawer.command";
    }
}
