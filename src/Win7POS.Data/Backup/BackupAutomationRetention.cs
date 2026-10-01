using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Win7POS.Data.Backup
{
    internal static class BackupAutomationRetention
    {
        internal static string HashFile(string path, out long length)
        {
            BackupAutomationDestination.RejectReparseAncestors(Path.GetDirectoryName(path));
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Managed backup is a reparse point.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                length = stream.Length;
                return HashStream(stream);
            }
        }

        internal static bool IsVerifiedIdentity(BackupAutomationManagedFile file)
        {
            if (!Path.GetFileName(file.Path).StartsWith("pos_backup_", StringComparison.Ordinal) ||
                !file.Path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(file.Path), file.Destination, StringComparison.OrdinalIgnoreCase))
                return false;
            var hash = HashFile(file.Path, out var length);
            return length == file.Length && string.Equals(hash, file.Hash, StringComparison.Ordinal);
        }

        internal static bool DeleteVerifiedIdentity(BackupAutomationManagedFile file)
        {
            BackupAutomationDestination.RejectReparseAncestors(file.Destination);
            // Win7-compatible handle deletion keeps hash verification and removal bound to
            // the same file. Sharing permits reads only, so replacement cannot win a race.
            using (var handle = CreateFile(file.Path, 0x80000000U | 0x00010000U, 1U,
                IntPtr.Zero, 3U, 0x00200000U | 0x08000000U, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new IOException("Managed backup could not be opened for retention.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                if ((File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) != 0)
                    return false;
                using (var stream = new FileStream(handle, FileAccess.Read, 4096, false))
                {
                    if (stream.Length != file.Length || HashStream(stream) != file.Hash)
                        return false;
                    var disposition = new FileDisposition { DeleteFile = 1 };
                    if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
                        throw new IOException("Managed backup could not be removed by retention.",
                            new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            return true;
        }

        private static string HashStream(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDisposition
        {
            public byte DeleteFile;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint sharing,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
            ref FileDisposition information, uint bufferSize);
    }
}
