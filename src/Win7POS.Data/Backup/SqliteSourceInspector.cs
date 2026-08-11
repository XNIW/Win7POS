using System;
using System.IO;
using System.Text;

namespace Win7POS.Data.Backup
{
    internal sealed class SqliteSourceInspection
    {
        public long DatabaseBytes { get; set; }
        public BackupRestoreSourceKind Kind { get; set; }
        public bool ShmPresent { get; set; }
        public long ShmBytes { get; set; }
        public bool WalPresent { get; set; }
        public long WalBytes { get; set; }
    }

    internal static class SqliteSourceInspector
    {
        private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

        public static SqliteSourceInspection Inspect(string databasePath, bool requireWalSidecars)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("SQLite source path is required.", nameof(databasePath));
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("SQLite source database was not found.");

            var header = new byte[100];
            int read;
            using (var stream = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan))
            {
                read = stream.Read(header, 0, header.Length);
            }

            var walPath = databasePath + "-wal";
            var shmPath = databasePath + "-shm";
            var walPresent = File.Exists(walPath);
            var shmPresent = File.Exists(shmPath);
            var kind = HeaderMatches(header, read) && header[18] == 2 && header[19] == 2
                ? BackupRestoreSourceKind.Wal
                : HeaderMatches(header, read) && header[18] == 1 && header[19] == 1
                    ? (File.Exists(databasePath + "-journal")
                        ? BackupRestoreSourceKind.Delete
                        : BackupRestoreSourceKind.Standalone)
                    : BackupRestoreSourceKind.Unknown;

            if ((walPresent || shmPresent) && kind != BackupRestoreSourceKind.Wal)
            {
                throw new InvalidDataException(
                    "SQLite source has a stale WAL/SHM sidecar that does not match its journal header.");
            }

            if (kind == BackupRestoreSourceKind.Wal && requireWalSidecars)
            {
                if (!walPresent)
                {
                    throw new InvalidDataException(
                        "SQLite WAL source is missing its required WAL sidecar; snapshot aborted fail-closed.");
                }

                if (!shmPresent)
                {
                    throw new InvalidDataException(
                        "SQLite WAL source is missing its shared-memory sidecar; snapshot aborted fail-closed.");
                }

                var rawDatabasePageSize = ((uint)header[16] << 8) | header[17];
                var databasePageSize = rawDatabasePageSize == 1 ? 65536L : rawDatabasePageSize;
                ValidateWalShape(walPath, databasePageSize);
            }

            return new SqliteSourceInspection
            {
                DatabaseBytes = new FileInfo(databasePath).Length,
                Kind = kind,
                ShmBytes = shmPresent ? new FileInfo(shmPath).Length : 0,
                ShmPresent = shmPresent,
                WalBytes = walPresent ? new FileInfo(walPath).Length : 0,
                WalPresent = walPresent
            };
        }

        private static bool HeaderMatches(byte[] header, int read)
        {
            if (read < SqliteHeader.Length)
                return false;
            for (var index = 0; index < SqliteHeader.Length; index++)
            {
                if (header[index] != SqliteHeader[index])
                    return false;
            }
            return true;
        }

        private static void ValidateWalShape(string walPath, long databasePageSize)
        {
            var header = new byte[32];
            int read;
            long length;
            using (var stream = new FileStream(
                walPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan))
            {
                length = stream.Length;
                read = stream.Read(header, 0, header.Length);
            }

            if (read != header.Length || length < header.Length)
                throw new InvalidDataException("SQLite WAL sidecar is truncated.");

            var magic = ReadBigEndianUInt32(header, 0);
            if (magic != 0x377f0682U && magic != 0x377f0683U)
                throw new InvalidDataException("SQLite WAL sidecar header is invalid.");

            var rawPageSize = ReadBigEndianUInt32(header, 8);
            var pageSize = rawPageSize == 1 ? 65536L : rawPageSize;
            if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
                throw new InvalidDataException("SQLite WAL sidecar page size is invalid.");
            if (pageSize != databasePageSize)
                throw new InvalidDataException("SQLite WAL sidecar page size does not match the database.");

            var frameSize = 24L + pageSize;
            if ((length - 32L) % frameSize != 0)
                throw new InvalidDataException("SQLite WAL sidecar contains a partial frame.");
        }

        private static uint ReadBigEndianUInt32(byte[] value, int offset)
        {
            return ((uint)value[offset] << 24) |
                ((uint)value[offset + 1] << 16) |
                ((uint)value[offset + 2] << 8) |
                value[offset + 3];
        }
    }
}
