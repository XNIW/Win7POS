using System;
using System.IO;
using Win7POS.Core;

namespace Win7POS.Data
{
    public sealed class PosDbOptions
    {
        public string DbPath { get; }

        public PosDbOptions(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("dbPath is empty");
            DbPath = dbPath;
        }

        public static PosDbOptions Default()
        {
            AppPaths.EnsureDataDirectories();
            return new PosDbOptions(AppPaths.DbPath);
        }

        public static PosDbOptions ForPath(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("dbPath is empty");
            var fullPath = Path.GetFullPath(dbPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            return new PosDbOptions(fullPath);
        }
    }
}
