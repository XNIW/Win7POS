using System;
using System.IO;
using System.Text;
using Win7POS.Core;

namespace Win7POS.Wpf.Pos.Online
{
    public static class PosDeviceIdentity
    {
        private const string DeviceIdFileName = "pos-device-id.txt";

        public static string DeviceIdFilePath => Path.Combine(AppPaths.DataDirectory, DeviceIdFileName);

        public static string GetOrCreateDeviceIdentifier()
        {
            AppPaths.EnsureDataDirectories();

            var existing = TryReadExistingDeviceId();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return "win7pos:" + existing.Trim();
            }

            var created = Guid.NewGuid().ToString("D");
            File.WriteAllText(DeviceIdFilePath, created);
            return "win7pos:" + created;
        }

        public static string GetStableDisplayName()
        {
            var machineName = SanitizeDisplayName(Environment.MachineName);

            if (string.IsNullOrWhiteSpace(machineName))
            {
                machineName = "WIN7POS";
            }

            var displayName = machineName.StartsWith("CASSA-", StringComparison.OrdinalIgnoreCase)
                ? machineName
                : "CASSA-" + machineName;

            return displayName.Length > 32 ? displayName.Substring(0, 32) : displayName;
        }

        private static string TryReadExistingDeviceId()
        {
            try
            {
                if (!File.Exists(DeviceIdFilePath))
                {
                    return null;
                }

                var raw = File.ReadAllText(DeviceIdFilePath).Trim();
                return Guid.TryParse(raw, out _) ? raw : null;
            }
            catch
            {
                return null;
            }
        }

        private static string SanitizeDisplayName(string value)
        {
            var raw = (value ?? string.Empty).Trim().ToUpperInvariant();
            var builder = new StringBuilder(raw.Length);
            var previousDash = false;

            foreach (var ch in raw)
            {
                var allowed = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
                if (allowed)
                {
                    builder.Append(ch);
                    previousDash = false;
                    continue;
                }

                if (!previousDash && builder.Length > 0)
                {
                    builder.Append('-');
                    previousDash = true;
                }
            }

            return builder.ToString().Trim('-');
        }
    }
}
