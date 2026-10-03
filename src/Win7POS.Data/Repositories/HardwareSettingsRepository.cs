using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core.Hardware;

namespace Win7POS.Data.Repositories
{
    /// <summary>One snapshot and one audited transaction for the complete local hardware configuration.</summary>
    public sealed class HardwareSettingsRepository
    {
        public const string ScannerPrefix = "pos.scanner.keyboard_wedge.";
        public const string ReceiptPrefix = "pos.printer.receipt.";
        public const string DrawerPrefix = "pos.cashdrawer.";
        public static readonly IReadOnlyList<string> Keys = new[]
        {
            ScannerPrefix + "terminator", ScannerPrefix + "prefix", ScannerPrefix + "suffix", ScannerPrefix + "trim_whitespace", ScannerPrefix + "min_length", ScannerPrefix + "max_length",
            ReceiptPrefix + "enabled", ReceiptPrefix + "name", ReceiptPrefix + "auto_print_after_sale", ReceiptPrefix + "copies", ReceiptPrefix + "allow_windows_default", ReceiptPrefix + "allow_virtual_printers", ReceiptPrefix + "profile",
            DrawerPrefix + "mode", DrawerPrefix + "enabled", DrawerPrefix + "printer_name", DrawerPrefix + "open_on_cash_sale", DrawerPrefix + "preset", DrawerPrefix + "command",
            "printer.name", "printer.copies", "pos.autoPrint", "printer.cashDrawerCommand", "pos.useReceipt42"
        };
        private readonly SettingsRepository _settings;
        public HardwareSettingsRepository(SettingsRepository settings) => _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        public HardwareSettingsRepository(SqliteConnectionFactory factory) : this(new SettingsRepository(factory)) { }

        public async Task<HardwareSettings> LoadAsync()
        {
            var values = await _settings.GetStringsAsync(Keys).ConfigureAwait(false);
            string Text(string key, string fallback = "") => values.TryGetValue(key, out var raw) ? raw ?? string.Empty : fallback;
            bool Bool(string key, bool fallback = false)
            {
                var raw = Text(key);
                if (raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)) return true;
                if (raw == "0" || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase)) return false;
                return fallback;
            }
            int Int(string key, int fallback) => int.TryParse(Text(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
            var model = new HardwareSettings();
            var terminator = Text(ScannerPrefix + "terminator", "enter");
            model.Scanner.Terminator = terminator == "tab" ? ScannerTerminator.Tab : terminator == "enter_or_tab" ? ScannerTerminator.EnterOrTab : ScannerTerminator.Enter;
            model.Scanner.Prefix = Text(ScannerPrefix + "prefix");
            model.Scanner.Suffix = Text(ScannerPrefix + "suffix");
            model.Scanner.TrimWhitespace = Bool(ScannerPrefix + "trim_whitespace", true);
            model.Scanner.MinimumLength = Int(ScannerPrefix + "min_length", 1);
            model.Scanner.MaximumLength = Int(ScannerPrefix + "max_length", 128);
            if (ScannerInputPolicy.Validate(model.Scanner).Count != 0 || (terminator != "enter" && terminator != "tab" && terminator != "enter_or_tab"))
                model.Scanner = new ScannerInputSettings();
            model.PrinterName = Text(ReceiptPrefix + "name", Text("printer.name"));
            model.ReceiptEnabled = Bool(ReceiptPrefix + "enabled");
            model.AutoPrint = Bool(ReceiptPrefix + "auto_print_after_sale", Bool("pos.autoPrint"));
            model.Copies = Int(ReceiptPrefix + "copies", Int("printer.copies", 1));
            model.AllowWindowsDefault = Bool(ReceiptPrefix + "allow_windows_default");
            model.AllowVirtualPrinters = Bool(ReceiptPrefix + "allow_virtual_printers");
            if (values.ContainsKey(ReceiptPrefix + "profile"))
            {
                if (ReceiptProfilePolicy.TryParse(Text(ReceiptPrefix + "profile"), out var profile)) model.ReceiptProfile = profile;
                else { model.ReceiptEnabled = false; model.AutoPrint = false; }
            }
            else model.ReceiptProfile = ReceiptProfilePolicy.FromLegacy(Bool("pos.useReceipt42", true));
            var command = Text(DrawerPrefix + "command", Text("printer.cashDrawerCommand", CashDrawerPresetPolicy.Pin2Command));
            model.CashDrawerCustomCommand = command;
            model.CashDrawerMode = Text(DrawerPrefix + "mode", Bool(DrawerPrefix + "enabled") ? "printer_kick" : "disabled");
            model.CashDrawerPrinterName = Text(DrawerPrefix + "printer_name");
            model.CashDrawerOpenOnCashSale = Bool(DrawerPrefix + "open_on_cash_sale", true);
            if (!values.ContainsKey(DrawerPrefix + "preset")) model.CashDrawerPreset = CashDrawerPresetPolicy.FromLegacyCommand(command);
            else if (CashDrawerPresetPolicy.TryParse(Text(DrawerPrefix + "preset"), out var preset)) model.CashDrawerPreset = preset;
            else { model.CashDrawerMode = "disabled"; model.CashDrawerPreset = CashDrawerPreset.EscposPin2; }
            var errors = model.Validate();
            if (errors.Any(e => e.StartsWith("receipt.", StringComparison.Ordinal) || e == "printer.name"))
            {
                model.ReceiptEnabled = false;
                model.AutoPrint = false;
                model.Copies = 1;
                if (model.PrinterName.Length > 255 || model.PrinterName.Any(char.IsControl)) model.PrinterName = string.Empty;
            }
            if (errors.Any(e => e.StartsWith("drawer.", StringComparison.Ordinal) || e == "printer.name"))
            {
                model.CashDrawerMode = "disabled";
                model.CashDrawerPreset = CashDrawerPreset.EscposPin2;
                model.CashDrawerCustomCommand = CashDrawerPresetPolicy.Pin2Command;
                if (model.CashDrawerPrinterName.Length > 255 || model.CashDrawerPrinterName.Any(char.IsControl)) model.CashDrawerPrinterName = string.Empty;
            }
            return model;
        }

        public Task<bool> SaveAsync(HardwareSettings settings, string actor, Action demandPermission, string operationId = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var copy = settings.Copy();
            var errors = copy.Validate();
            if (errors.Count != 0) throw new ArgumentException("Invalid hardware settings: " + string.Join(",", errors), nameof(settings));
            if (demandPermission == null) throw new ArgumentNullException(nameof(demandPermission));
            return _settings.SetStringsAuditedAsync(ToValues(copy), "HardwareSettingsUpdate", actor, "hardware_center", demandPermission, operationId);
        }

        public static IReadOnlyDictionary<string, string> ToValues(HardwareSettings model)
        {
            string Bit(bool value) => value ? "1" : "0";
            var copies = model.Copies.ToString(CultureInfo.InvariantCulture);
            var command = model.CashDrawerCommand;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ScannerPrefix + "terminator"] = model.Scanner.Terminator == ScannerTerminator.Tab ? "tab" : model.Scanner.Terminator == ScannerTerminator.EnterOrTab ? "enter_or_tab" : "enter",
                [ScannerPrefix + "prefix"] = model.Scanner.Prefix ?? string.Empty, [ScannerPrefix + "suffix"] = model.Scanner.Suffix ?? string.Empty,
                [ScannerPrefix + "trim_whitespace"] = Bit(model.Scanner.TrimWhitespace),
                [ScannerPrefix + "min_length"] = model.Scanner.MinimumLength.ToString(CultureInfo.InvariantCulture),
                [ScannerPrefix + "max_length"] = model.Scanner.MaximumLength.ToString(CultureInfo.InvariantCulture),
                [ReceiptPrefix + "enabled"] = Bit(model.ReceiptEnabled), [ReceiptPrefix + "name"] = model.PrinterName,
                [ReceiptPrefix + "auto_print_after_sale"] = Bit(model.AutoPrint), [ReceiptPrefix + "copies"] = copies,
                [ReceiptPrefix + "allow_windows_default"] = Bit(model.AllowWindowsDefault), [ReceiptPrefix + "allow_virtual_printers"] = Bit(model.AllowVirtualPrinters),
                [ReceiptPrefix + "profile"] = ReceiptProfilePolicy.Serialize(model.ReceiptProfile),
                [DrawerPrefix + "mode"] = model.CashDrawerMode, [DrawerPrefix + "enabled"] = Bit(model.CashDrawerMode == "printer_kick"),
                [DrawerPrefix + "printer_name"] = model.CashDrawerPrinterName, [DrawerPrefix + "open_on_cash_sale"] = Bit(model.CashDrawerOpenOnCashSale),
                [DrawerPrefix + "preset"] = CashDrawerPresetPolicy.Serialize(model.CashDrawerPreset), [DrawerPrefix + "command"] = command,
                ["printer.name"] = model.PrinterName, ["printer.copies"] = copies, ["pos.autoPrint"] = Bit(model.AutoPrint),
                ["printer.cashDrawerCommand"] = command, ["pos.useReceipt42"] = Bit(ReceiptProfilePolicy.Columns(model.ReceiptProfile) == 42)
            };
        }
    }
}
