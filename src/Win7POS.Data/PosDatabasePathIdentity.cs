using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace Win7POS.Data
{
    internal static class PosDatabasePathIdentity
    {
        internal static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;
        internal static string CanonicalSlot(string path)
        {
            var full = Path.GetFullPath(path);
            if (!IsWindows)
            {
                // macOS exposes the system temp directory through /var -> /private/var.
                // Resolve that host alias; portable fixtures do not claim Win32 link qualification.
                var pointer = RealPath(Path.GetDirectoryName(full), IntPtr.Zero);
                if (pointer == IntPtr.Zero) throw new PosDatabaseAuthorityException("database_parent_identity_unavailable");
                try { full = Path.Combine(Marshal.PtrToStringAnsi(pointer), Path.GetFileName(full)); }
                finally { Free(pointer); }
                RejectReparseComponents(full);
                return full;
            }
            RejectReparseComponents(full);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return full;
            return File.Exists(full) ? FinalPath(full, false) : Path.Combine(FinalPath(Path.GetDirectoryName(full), true), Path.GetFileName(full));
        }
        internal static void RejectReparseComponents(string input)
        {
            var path = Path.GetFullPath(input);
            while (!string.IsNullOrEmpty(path))
            {
                try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new PosDatabaseAuthorityException("database_reparse_path_unsupported"); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Path.GetDirectoryName(path);
                if (parent == path) break;
                path = parent;
            }
        }
        internal static string FileIdentity(string path, bool directory)
        {
            using (var handle = Open(path, directory, 7))
            {
                var info = Info(handle);
                if (!directory && info.Links != 1) throw new PosDatabaseAuthorityException("database_hardlink_unsupported");
                return info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
            }
        }
        internal static IDisposable PinDirectory(string path) => Open(path, true, 3);
        internal static string FinalPath(string path, bool directory)
        {
            using (var handle = Open(path, directory, 7))
            {
                var builder = new StringBuilder(512);
                var length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
                if (length == 0 || length > 32767) throw new PosDatabaseAuthorityException("database_identity_unavailable");
                if (length >= builder.Capacity)
                { builder = new StringBuilder((int)length + 1); if (GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0) == 0) throw new PosDatabaseAuthorityException("database_identity_unavailable"); }
                var result = builder.ToString();
                if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new PosDatabaseAuthorityException("database_network_path_unsupported");
                return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
            }
        }
        internal static void VerifySingleLink(SafeFileHandle handle)
        { if (Info(handle).Links != 1) throw new PosDatabaseAuthorityException("database_hardlink_unsupported"); }
        private static SafeFileHandle Open(string path, bool directory, uint sharing)
        {
            var handle = CreateFileW(path, 0, sharing, IntPtr.Zero, 3, (directory ? 0x02000000u : 0u) | 0x00200000u, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new PosDatabaseAuthorityException("database_identity_unavailable"); }
            return handle;
        }
        private static FileInformation Info(SafeFileHandle handle)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) throw new PosDatabaseAuthorityException("database_identity_unavailable");
            if ((info.Attributes & 0x400) != 0) throw new PosDatabaseAuthorityException("database_reparse_path_unsupported");
            return info;
        }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr RealPath(string path, IntPtr destination);
        [DllImport("libc", EntryPoint = "free")]
        private static extern void Free(IntPtr pointer);
    }
}
