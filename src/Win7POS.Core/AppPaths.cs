using System;
using System.IO;

namespace Win7POS.Core
{
    public static class AppPaths
    {
        private static readonly string BaseDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Win7POS");

        public static string DataDirectory => BaseDirectoryPath;

        public static string DbPath => Path.Combine(DataDirectory, "pos.db");

        public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

        public static string LogPath => Path.Combine(LogsDirectory, "app.log");

        public static void EnsureDataDirectories()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
        }
    }
}
