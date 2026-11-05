using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Win7POS.Data;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // These probes use real aliases of the retained synthetic database. The
    // private evidence distinguishes an unavailable native operation from an
    // authority denial; neither can silently stand in for the other.
    internal static class HistoricalSalesAuthorityVariants
    {
        internal static void Check(string data, string database, HistoricalSalesQaContext context, HistoricalSalesQaCase test)
        {
            var evidence = new VariantEvidence { RunId = context.RunId, RunNonce = context.RunNonce };
            var hard = Path.Combine(Path.GetDirectoryName(database), "hard.db");
            var junction = Path.Combine(data, "reparse-probe");
            try
            {
                var original = ReadIdentity(database);
                var casePath = database.ToUpperInvariant();
                if (string.Equals(casePath, database, StringComparison.Ordinal)) casePath = database.ToLowerInvariant();
                var casing = new Variant { Id = "case", DistinctAlias = !string.Equals(casePath, database, StringComparison.Ordinal), NativeSucceeded = true };
                evidence.Variants.Add(casing);
                casing.SamePhysicalFile = ReadIdentity(casePath).Hash == original.Hash;
                casing.DenialCode = ProbeAuthority(casePath);

                var shortName = new StringBuilder(32768);
                var length = GetShortPathNameW(database, shortName, (uint)shortName.Capacity);
                var shortError = length == 0 ? Marshal.GetLastWin32Error() : 0;
                var shortPath = shortName.ToString();
                var alias = new Variant { Id = "shortpath", NativeSucceeded = length > 0 && length < shortName.Capacity, NativeError = shortError };
                evidence.Variants.Add(alias);
                if (alias.NativeSucceeded)
                {
                    // .NET Framework GetFullPath expands an existing 8.3 name.
                    // Compare the absolute Win32 spelling before that expansion;
                    // the handle identity below still proves the same real file.
                    alias.DistinctAlias = Path.IsPathRooted(shortPath) && Path.IsPathRooted(database) &&
                        !string.Equals(shortPath, database, StringComparison.OrdinalIgnoreCase);
                    alias.SamePhysicalFile = ReadIdentity(shortPath).Hash == original.Hash;
                    if (alias.DistinctAlias && alias.SamePhysicalFile) alias.DenialCode = ProbeAuthority(shortPath);
                }
                test.Check("case_shortpath_alias_collide", casing.DistinctAlias && casing.SamePhysicalFile && casing.DenialCode == "database_in_use" &&
                    alias.NativeSucceeded && alias.DistinctAlias && alias.SamePhysicalFile && alias.DenialCode == "database_in_use");

                var hardCreated = CreateHardLinkW(hard, database, IntPtr.Zero);
                var hardError = hardCreated ? 0 : Marshal.GetLastWin32Error();
                var hardLink = new Variant { Id = "hardlink", NativeSucceeded = hardCreated, NativeError = hardError };
                evidence.Variants.Add(hardLink);
                if (hardCreated)
                {
                    var identity = ReadIdentity(hard);
                    hardLink.SamePhysicalFile = identity.Hash == original.Hash;
                    hardLink.LinkCount = identity.Links;
                    hardLink.DenialCode = ProbeAuthority(hard);
                }
                // Restore the original single-link invariant before probing a
                // different alias. No recursive deletion ever follows a reparse.
                if (hardCreated) File.Delete(hard);

                int junctionError;
                var junctionCreated = CreateJunction(junction, Path.GetDirectoryName(database), out junctionError);
                var reparse = new Variant { Id = "directory_junction", NativeSucceeded = junctionCreated, NativeError = junctionError };
                evidence.Variants.Add(reparse);
                if (junctionCreated)
                {
                    reparse.ReparsePoint = (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0;
                    var aliasDatabase = Path.Combine(junction, Path.GetFileName(database));
                    reparse.SamePhysicalFile = ReadIdentity(aliasDatabase).Hash == original.Hash;
                    if (reparse.ReparsePoint && reparse.SamePhysicalFile) reparse.DenialCode = ProbeAuthority(aliasDatabase);
                }
                test.Check("reparse_hardlink_rejected", hardLink.NativeSucceeded && hardLink.SamePhysicalFile && hardLink.LinkCount > 1 &&
                    hardLink.DenialCode == "database_hardlink_unsupported" && reparse.NativeSucceeded && reparse.ReparsePoint &&
                    reparse.SamePhysicalFile && reparse.DenialCode == "database_reparse_path_unsupported");
            }
            finally
            {
                if (File.Exists(hard)) File.Delete(hard);
                if (Directory.Exists(junction)) Directory.Delete(junction, false);
                HistoricalSalesQaReceipt.WriteJson(Path.Combine(data, "process-authority-variants.private.json"), evidence);
            }
        }

        private static string ProbeAuthority(string path)
        {
            // Constructing PosDbOptions directly avoids creating a missing alias
            // parent before the real entry point has verified its identity.
            try { using (PosDatabaseAuthorityLease.AcquireOperational(new PosDbOptions(path))) return "acquired"; }
            catch (PosDatabaseAuthorityException error) { return error.Code; }
        }

        private static Identity ReadIdentity(string path)
        {
            using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "qa_alias_identity_open_failed");
                FileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "qa_alias_identity_read_failed");
                var value = info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
                using (var sha = SHA256.Create()) return new Identity { Links = info.Links,
                    Hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant() };
            }
        }

        private static bool CreateJunction(string path, string target, out int error)
        {
            Directory.CreateDirectory(path);
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
            var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
            var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
            using (var memory = new MemoryStream(buffer))
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT, available on Windows 7.
                writer.Write((ushort)(buffer.Length - 8)); writer.Write((ushort)0);
                writer.Write((ushort)0); writer.Write((ushort)substitute.Length);
                writer.Write((ushort)(substitute.Length + 2)); writer.Write((ushort)print.Length);
                writer.Write(substitute); writer.Write((ushort)0); writer.Write(print); writer.Write((ushort)0);
            }
            using (var handle = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); return false; }
                uint returned;
                var created = DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero);
                error = created ? 0 : Marshal.GetLastWin32Error(); return created;
            }
        }

        private sealed class Identity { internal string Hash; internal uint Links; }
        [DataContract] private sealed class VariantEvidence
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } = "win7pos-authority-variants-private-v1";
            [DataMember(Name = "runId")] public string RunId { get; set; }
            [DataMember(Name = "runNonce")] public string RunNonce { get; set; }
            [DataMember(Name = "variants")] public List<Variant> Variants { get; set; } = new List<Variant>();
        }
        [DataContract] private sealed class Variant
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "nativeSucceeded")] public bool NativeSucceeded { get; set; }
            [DataMember(Name = "nativeError")] public int NativeError { get; set; }
            [DataMember(Name = "distinctAlias")] public bool DistinctAlias { get; set; }
            [DataMember(Name = "samePhysicalFile")] public bool SamePhysicalFile { get; set; }
            [DataMember(Name = "linkCount")] public uint LinkCount { get; set; }
            [DataMember(Name = "reparsePoint")] public bool ReparsePoint { get; set; }
            [DataMember(Name = "denialCode")] public string DenialCode { get; set; } = "not_run";
        }
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetShortPathNameW(string path, StringBuilder output, uint capacity);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);
    }
}
