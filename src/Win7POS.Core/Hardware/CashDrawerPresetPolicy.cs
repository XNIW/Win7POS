using System;
using System.Globalization;

namespace Win7POS.Core.Hardware
{
    public enum CashDrawerPreset { EscposPin2, EscposPin5, Custom }
    public static class CashDrawerPresetPolicy
    {
        public const string Pin2Command = "27,112,0,25,250";
        public const string Pin5Command = "27,112,1,25,250";
        public static string Serialize(CashDrawerPreset preset) => preset == CashDrawerPreset.EscposPin2 ? "escpos_pin2" : preset == CashDrawerPreset.EscposPin5 ? "escpos_pin5" : "custom";
        public static bool TryParse(string value, out CashDrawerPreset preset)
        {
            preset = CashDrawerPreset.EscposPin2;
            if (value == "escpos_pin2") return true;
            if (value == "escpos_pin5") { preset = CashDrawerPreset.EscposPin5; return true; }
            if (value == "custom") { preset = CashDrawerPreset.Custom; return true; }
            return false;
        }
        public static CashDrawerPreset FromLegacyCommand(string command) => command == Pin2Command ? CashDrawerPreset.EscposPin2 : command == Pin5Command ? CashDrawerPreset.EscposPin5 : CashDrawerPreset.Custom;
        public static string Command(CashDrawerPreset preset, string custom) => preset == CashDrawerPreset.EscposPin2 ? Pin2Command : preset == CashDrawerPreset.EscposPin5 ? Pin5Command : custom ?? string.Empty;
        public static bool TryGetBytes(CashDrawerPreset preset, string custom, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (!Enum.IsDefined(typeof(CashDrawerPreset), preset)) return false;
            var command = Command(preset, custom);
            if (command.Length > 64) return false;
            var parts = command.Split(',');
            if (parts.Length != 5) return false;
            var parsed = new byte[5];
            for (var i = 0; i < 5; i++)
                if (!byte.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i])) return false;
            if (parsed[0] != 27 || parsed[1] != 112 ||
                (parsed[2] != 0 && parsed[2] != 1 && parsed[2] != 48 && parsed[2] != 49) || parsed[3] >= parsed[4]) return false;
            bytes = parsed;
            return true;
        }
    }
}
