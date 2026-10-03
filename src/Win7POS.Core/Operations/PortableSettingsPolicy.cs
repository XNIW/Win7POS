using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Backup;

namespace Win7POS.Core.Operations
{
    public enum SettingsDefaultsScope { Hardware, Backup, CustomerDisplay, Language, AllPortable }

    /// <summary>Only these local preferences may cross workstation boundaries.</summary>
    public static class PortableSettingsPolicy
    {
        public const int MaximumFileBytes = 65536;
        public const string LastSeenVersionKey = "pos.operations.application.last_seen_version";
        private sealed class Rule
        {
            internal string Default;
            internal Func<string, bool> Valid;
            internal SettingsDefaultsScope Scope;
        }
        private static readonly Dictionary<string, Rule> Rules = CreateRules();
        public static IReadOnlyList<string> Keys { get; } = Rules.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        public static IReadOnlyList<string> RedactedKeys { get; } = new[]
        {
            "pos.online.*", "pos.catalog.*", "pos.official_shop.*", "pos.restore.*", "session/login/trust/*",
            "shop/fiscal/*", "pos.printer.receipt.name", "pos.cashdrawer.printer_name", "pos.cashdrawer.command",
            "pos.operations.backup.destination.*", "pos.customer_display.cashier_device", "pos.customer_display.customer_device",
            "pos.customer_display.branding.logo_file", "pos.customer_display.branding.logo_hash", "pos.customer_display.idle.message", "unknown/*"
        };

        public static IReadOnlyDictionary<string, string> Defaults(SettingsDefaultsScope scope)
        {
            if (!Enum.IsDefined(typeof(SettingsDefaultsScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            return new ReadOnlyDictionary<string, string>(Rules.Where(x => scope == SettingsDefaultsScope.AllPortable || x.Value.Scope == scope)
                .ToDictionary(x => x.Key, x => x.Value.Default, StringComparer.Ordinal));
        }

        public static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string> stored)
        {
            var result = Rules.ToDictionary(x => x.Key, x => x.Value.Default, StringComparer.Ordinal);
            foreach (var key in Keys)
            {
                if (stored.TryGetValue(key, out var raw)) result[key] = Normalize(key, raw);
            }
            if (!stored.ContainsKey("pos.printer.receipt.profile") && stored.TryGetValue("pos.useReceipt42", out var legacyWidth))
                result["pos.printer.receipt.profile"] = legacyWidth == "1" || legacyWidth == "true" ? "thermal_80mm_42col" : "thermal_58mm_32col";
            if (!stored.ContainsKey("pos.printer.receipt.copies") && stored.TryGetValue("printer.copies", out var legacyCopies))
                result["pos.printer.receipt.copies"] = Normalize("pos.printer.receipt.copies", legacyCopies);
            if (!stored.ContainsKey("pos.customer_display.privacy.barcode_mode") && stored.TryGetValue("pos.customer_display.show_barcode", out var legacyBarcode))
                result["pos.customer_display.privacy.barcode_mode"] = legacyBarcode == "1" || legacyBarcode == "true" ? "full" : "hidden";
            // A custom pulse is deliberately not portable; retain its safe default preset.
            if (result["pos.cashdrawer.preset"] == "custom") result["pos.cashdrawer.preset"] = "escpos_pin2";
            Validate(result);
            return new ReadOnlyDictionary<string, string>(result);
        }

        public static void Validate(IReadOnlyDictionary<string, string> values)
        {
            if (values == null || values.Count > Rules.Count) throw new ArgumentException("Invalid portable settings.");
            foreach (var pair in values)
                if (!Rules.TryGetValue(pair.Key, out var rule) || pair.Value == null || !rule.Valid(pair.Value))
                    throw new ArgumentException("Invalid portable setting.");
            var merged = Rules.ToDictionary(x => x.Key, x => x.Value.Default, StringComparer.Ordinal);
            foreach (var pair in values) merged[pair.Key] = pair.Value;
            if (Number(merged["pos.scanner.keyboard_wedge.min_length"]) > Number(merged["pos.scanner.keyboard_wedge.max_length"]))
                throw new ArgumentException("Scanner length range is invalid.");
            if (merged["pos.printer.receipt.auto_print_after_sale"] == "true" &&
                merged["pos.printer.receipt.enabled"] != "true")
                throw new ArgumentException("Automatic receipt settings are invalid.");
        }

        public static string SafeHash(IReadOnlyDictionary<string, string> values)
        {
            // Do not hash secrets, free text, paths, queue/device names, or unknown keys.
            var safe = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in values)
            {
                if (Rules.TryGetValue(pair.Key, out var rule))
                {
                    var value = pair.Value;
                    if (rule.Default == "true" || rule.Default == "false")
                    {
                        if (value == "1") value = "true";
                        if (value == "0") value = "false";
                    }
                    if (rule.Valid(value)) safe[pair.Key] = value;
                }
                else if (pair.Key == LastSeenVersionKey && pair.Value != null && pair.Value.Length <= 64 && pair.Value.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '+'))
                    safe[pair.Key] = pair.Value;
            }
            return Hash(string.Join("\n", safe.Select(x => x.Key + ":" + x.Value.Length + ":" + x.Value)));
        }
        public static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static bool IsPortable(string key) => Rules.ContainsKey(key);
        private static int Number(string value) => int.Parse(value, CultureInfo.InvariantCulture);
        private static string Normalize(string key, string raw)
        {
            var rule = Rules[key];
            if (rule.Default == "true" || rule.Default == "false")
            {
                if (raw == "1") raw = "true";
                if (raw == "0") raw = "false";
            }
            // Invalid stored values fail closed to a valid portable default.
            return rule.Valid(raw) ? raw : rule.Default;
        }
        private static Dictionary<string, Rule> CreateRules()
        {
            var rules = new Dictionary<string, Rule>(StringComparer.Ordinal);
            Action<string, string, SettingsDefaultsScope, Func<string, bool>> add = (key, def, scope, valid) =>
                rules.Add(key, new Rule { Default = def, Scope = scope, Valid = valid });
            Action<string, bool, SettingsDefaultsScope> boolean = (key, def, scope) => add(key, def ? "true" : "false", scope, x => x == "true" || x == "false");
            Action<string, int, int, int, SettingsDefaultsScope> integer = (key, def, min, max, scope) =>
                add(key, def.ToString(CultureInfo.InvariantCulture), scope, x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max && n.ToString(CultureInfo.InvariantCulture) == x);
            Action<string, string, SettingsDefaultsScope, string[]> choice = (key, def, scope, choices) => add(key, def, scope, x => choices.Contains(x, StringComparer.Ordinal));
            var h = SettingsDefaultsScope.Hardware;
            choice("pos.scanner.keyboard_wedge.terminator", "enter", h, new[] { "enter", "tab", "enter_or_tab" });
            foreach (var name in new[] { "prefix", "suffix" })
                add("pos.scanner.keyboard_wedge." + name, "", h, x => x != null && x.Length <= 16 && x.All(c => c >= ' ' && c <= '~'));
            boolean("pos.scanner.keyboard_wedge.trim_whitespace", true, h);
            integer("pos.scanner.keyboard_wedge.min_length", 1, 1, 128, h);
            integer("pos.scanner.keyboard_wedge.max_length", 128, 1, 256, h);
            boolean("pos.printer.receipt.enabled", false, h);
            boolean("pos.printer.receipt.auto_print_after_sale", false, h);
            boolean("pos.printer.receipt.allow_windows_default", false, h);
            boolean("pos.printer.receipt.allow_virtual_printers", false, h);
            integer("pos.printer.receipt.copies", 1, 1, 3, h);
            choice("pos.printer.receipt.profile", "thermal_80mm_42col", h, new[] { "thermal_58mm_32col", "thermal_80mm_42col" });
            choice("pos.cashdrawer.mode", "disabled", h, new[] { "disabled", "printer_kick" });
            choice("pos.cashdrawer.preset", "escpos_pin2", h, new[] { "escpos_pin2", "escpos_pin5" });
            boolean("pos.cashdrawer.open_on_cash_sale", true, h);
            var b = SettingsDefaultsScope.Backup;
            choice("pos.operations.backup.schedule", "disabled", b, new[] { "disabled", "daily", "weekly" });
            add("pos.operations.backup.local_time", "02:00", b, x => BackupSchedulePolicy.TryParseLocalTime(x, out _));
            choice("pos.operations.backup.weekly_day", "Sunday", b, Enum.GetNames(typeof(DayOfWeek)));
            boolean("pos.operations.backup.catch_up_on_startup", true, b);
            integer("pos.operations.backup.retention.max_count", 14, 3, 365, b);
            integer("pos.operations.backup.retention.max_age_days", 30, 1, 3650, b);
            var d = SettingsDefaultsScope.CustomerDisplay;
            // Activation, monitor selection, branding files and idle free text stay local.
            foreach (var name in new[] { "full_screen", "always_on_top", "follow_minimize", "show_shop_name", "show_unit_price", "show_line_total", "show_subtotal", "show_discount", "show_item_count", "reopen_on_return" })
                boolean("pos.customer_display." + name, true, d);
            boolean("pos.customer_display.use_working_area", false, d);
            choice("pos.customer_display.font_scale", "Medium", d, new[] { "Small", "Medium", "Large" });
            choice("pos.customer_display.theme", "Dark", d, new[] { "Light", "Dark", "HighContrast" });
            choice("pos.customer_display.language", "FollowApplication", d, new[] { "FollowApplication", "IT", "EN", "ES", "ZH" });
            integer("pos.customer_display.thank_you_seconds", 5, 1, 30, d);
            choice("pos.customer_display.branding.logo_position", "left", d, new[] { "left", "center" });
            choice("pos.customer_display.idle.mode", "welcome", d, new[] { "welcome", "custom_message", "clock" });
            choice("pos.customer_display.privacy.barcode_mode", "hidden", d, new[] { "hidden", "last4", "full" });
            boolean("pos.customer_display.privacy.show_paid_amount", false, d);
            boolean("pos.customer_display.privacy.show_change_amount", true, d);
            integer("pos.customer_display.test_pattern.duration_seconds", 15, 5, 60, d);
            choice("ui.language", "en", SettingsDefaultsScope.Language, new[] { "en", "es", "it", "zh-CN" });
            return rules;
        }
    }
}
