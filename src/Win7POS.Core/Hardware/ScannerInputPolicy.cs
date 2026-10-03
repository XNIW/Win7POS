using System;
using System.Collections.Generic;

namespace Win7POS.Core.Hardware
{
    public enum ScannerTerminator { Enter, Tab, EnterOrTab }

    public sealed class ScannerInputSettings
    {
        public ScannerTerminator Terminator { get; set; } = ScannerTerminator.Enter;
        public string Prefix { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;
        public bool TrimWhitespace { get; set; } = true;
        public int MinimumLength { get; set; } = 1;
        public int MaximumLength { get; set; } = 128;
        public ScannerInputSettings Copy() => (ScannerInputSettings)MemberwiseClone();
    }

    /// <summary>Keyboard-wedge normalization only. No device discovery, lookup or persistence.</summary>
    public static class ScannerInputPolicy
    {
        public const int MaximumRawInputLength = 1024;

        public static IReadOnlyList<string> Validate(ScannerInputSettings settings)
        {
            var errors = new List<string>();
            if (settings == null) { errors.Add("scanner.settings"); return errors; }
            if (!Enum.IsDefined(typeof(ScannerTerminator), settings.Terminator)) errors.Add("scanner.terminator");
            if (!IsAffixValid(settings.Prefix)) errors.Add("scanner.prefix");
            if (!IsAffixValid(settings.Suffix)) errors.Add("scanner.suffix");
            if (settings.MinimumLength < 1 || settings.MinimumLength > 128) errors.Add("scanner.min_length");
            if (settings.MaximumLength < 1 || settings.MaximumLength > 256 || settings.MaximumLength < settings.MinimumLength)
                errors.Add("scanner.max_length");
            return errors;
        }

        public static bool Accepts(ScannerInputSettings settings, ScannerTerminator key) =>
            settings != null && (key == ScannerTerminator.Enter || key == ScannerTerminator.Tab) &&
            (settings.Terminator == ScannerTerminator.EnterOrTab || settings.Terminator == key);

        public static bool TryNormalize(string raw, ScannerInputSettings settings, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = string.Empty;
            if (Validate(settings).Count != 0) { error = "scanner.invalidSettings"; return false; }
            if (raw == null || raw.Length > MaximumRawInputLength) { error = "scanner.invalidLength"; return false; }
            var value = settings.TrimWhitespace ? raw.Trim() : raw;
            var prefix = settings.Prefix ?? string.Empty;
            var suffix = settings.Suffix ?? string.Empty;
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(suffix, StringComparison.Ordinal) ||
                value.Length < prefix.Length + suffix.Length)
            { error = "scanner.affixMismatch"; return false; }
            value = value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length);
            if (value.Length < settings.MinimumLength || value.Length > settings.MaximumLength)
            { error = "scanner.invalidLength"; return false; }
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i]) || (char.IsSurrogate(value[i]) &&
                    (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length || !char.IsLowSurrogate(value[++i]))))
                { error = "scanner.invalidCharacters"; return false; }
            }
            normalized = value;
            return true;
        }

        private static bool IsAffixValid(string value)
        {
            value = value ?? string.Empty;
            if (value.Length > 16) return false;
            foreach (var character in value)
                if (character < ' ' || character > '~') return false;
            return true;
        }
    }
}
