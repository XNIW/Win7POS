using System;
using System.IO;
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
    }
}
