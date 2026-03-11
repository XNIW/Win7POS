using System;
using System.IO;

namespace Win7POS.Core
{
    public static class AppPaths
    {
        private static readonly string BaseDirectoryPath = ResolveBaseDirectory();

        public static string DataDirectory => BaseDirectoryPath;

        public static string DbPath => Path.Combine(DataDirectory, "pos.db");

        public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public static string BackupsDirectory => Path.Combine(DataDirectory, "backups");

        public static string ExportsDirectory => Path.Combine(DataDirectory, "exports");

        public static string LogPath => Path.Combine(LogsDirectory, "app.log");

        public static void EnsureCreated()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(BackupsDirectory);
            Directory.CreateDirectory(ExportsDirectory);
        }

        // Backward-compatible alias used by existing callers.
        public static void EnsureDataDirectories()
        {
            EnsureCreated();
        }

        private static string ResolveBaseDirectory()
        {
            return PosPaths.GetDataRoot();
        }
    }
}
