using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Pos;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Local-only workstation preferences. These keys are intentionally outside every
    /// catalog/sales sync payload and are committed as one SQLite transaction.
    /// </summary>
    public sealed class CustomerDisplaySettingsRepository
    {
        public const string Prefix = "pos.customer_display.";

        private readonly SqliteConnectionFactory _factory;

        public CustomerDisplaySettingsRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<CustomerDisplaySettings> LoadAsync(int independentMonitorCount)
        {
            var settings = CustomerDisplaySettings.CreateDefault(independentMonitorCount);
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SettingRow>(
                "SELECT key AS Key, value AS Value FROM app_settings WHERE key LIKE @prefix;",
                new { prefix = Prefix + "%" }).ConfigureAwait(false);
            var values = rows.ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            settings.Enabled = Bool(values, "enabled", settings.Enabled);
            settings.SelectionMode = EnumValue(values, "selection_mode", settings.SelectionMode);
            settings.CashierMonitorDeviceName = Text(values, "cashier_device");
            settings.CustomerMonitorDeviceName = Text(values, "customer_device");
            settings.AutoOpen = Bool(values, "auto_open", settings.AutoOpen);
            settings.FullScreen = Bool(values, "full_screen", settings.FullScreen);
            settings.UseWorkingArea = Bool(values, "use_working_area", settings.UseWorkingArea);
            settings.AlwaysOnTop = Bool(values, "always_on_top", settings.AlwaysOnTop);
            settings.FollowCashierMinimize = Bool(values, "follow_minimize", settings.FollowCashierMinimize);
            settings.ShowShopName = Bool(values, "show_shop_name", settings.ShowShopName);
            settings.ShowBarcode = Bool(values, "show_barcode", settings.ShowBarcode);
            settings.ShowUnitPrice = Bool(values, "show_unit_price", settings.ShowUnitPrice);
            settings.ShowLineTotal = Bool(values, "show_line_total", settings.ShowLineTotal);
            settings.ShowSubtotal = Bool(values, "show_subtotal", settings.ShowSubtotal);
            settings.ShowDiscount = Bool(values, "show_discount", settings.ShowDiscount);
            settings.ShowItemCount = Bool(values, "show_item_count", settings.ShowItemCount);
            settings.FontScale = EnumValue(values, "font_scale", settings.FontScale);
            settings.Theme = EnumValue(values, "theme", settings.Theme);
            settings.CustomerLanguage = EnumValue(values, "language", settings.CustomerLanguage);
            settings.ThankYouSeconds = Clamp(Int(values, "thank_you_seconds", settings.ThankYouSeconds), 1, 30);
            settings.ReopenWhenMonitorReturns = Bool(values, "reopen_on_return", settings.ReopenWhenMonitorReturns);
            return settings;
        }

        public async Task SaveAsync(CustomerDisplaySettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var validation = settings.Validate();
            if (validation.Count > 0)
                throw new ArgumentException("Invalid customer display settings: " + string.Join(",", validation));

            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["enabled"] = Bit(settings.Enabled),
                ["selection_mode"] = settings.SelectionMode.ToString(),
                ["cashier_device"] = settings.CashierMonitorDeviceName ?? string.Empty,
                ["customer_device"] = settings.CustomerMonitorDeviceName ?? string.Empty,
                ["auto_open"] = Bit(settings.AutoOpen),
                ["full_screen"] = Bit(settings.FullScreen),
                ["use_working_area"] = Bit(settings.UseWorkingArea),
                ["always_on_top"] = Bit(settings.AlwaysOnTop),
                ["follow_minimize"] = Bit(settings.FollowCashierMinimize),
                ["show_shop_name"] = Bit(settings.ShowShopName),
                ["show_barcode"] = Bit(settings.ShowBarcode),
                ["show_unit_price"] = Bit(settings.ShowUnitPrice),
                ["show_line_total"] = Bit(settings.ShowLineTotal),
                ["show_subtotal"] = Bit(settings.ShowSubtotal),
                ["show_discount"] = Bit(settings.ShowDiscount),
                ["show_item_count"] = Bit(settings.ShowItemCount),
                ["font_scale"] = settings.FontScale.ToString(),
                ["theme"] = settings.Theme.ToString(),
                ["language"] = settings.CustomerLanguage.ToString(),
                ["thank_you_seconds"] = Clamp(settings.ThankYouSeconds, 1, 30).ToString(CultureInfo.InvariantCulture),
                ["reopen_on_return"] = Bit(settings.ReopenWhenMonitorReturns)
            };

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            foreach (var pair in values)
            {
                await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                    new { key = Prefix + pair.Key, value = pair.Value }, tx).ConfigureAwait(false);
            }
            tx.Commit();
        }

        private static string Text(IReadOnlyDictionary<string, string> values, string suffix)
        {
            return values.TryGetValue(Prefix + suffix, out var value) ? (value ?? string.Empty).Trim() : string.Empty;
        }

        private static bool Bool(IReadOnlyDictionary<string, string> values, string suffix, bool fallback)
        {
            var value = Text(values, suffix);
            if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static int Int(IReadOnlyDictionary<string, string> values, string suffix, int fallback)
        {
            return int.TryParse(Text(values, suffix), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static T EnumValue<T>(IReadOnlyDictionary<string, string> values, string suffix, T fallback)
            where T : struct
        {
            return Enum.TryParse(Text(values, suffix), true, out T parsed) && Enum.IsDefined(typeof(T), parsed)
                ? parsed
                : fallback;
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
        private static string Bit(bool value) => value ? "1" : "0";

        private sealed class SettingRow
        {
            public string Key { get; set; }
            public string Value { get; set; }
        }
    }
}
