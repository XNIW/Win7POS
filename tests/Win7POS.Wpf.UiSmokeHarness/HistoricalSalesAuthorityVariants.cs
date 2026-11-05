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

                var preparedAlias = new Variant { Id = "shortpath" };
                evidence.Variants.Add(preparedAlias);
                ObserveShortAlias(database, original, preparedAlias);
                // Hosted runner TEMP can live on a volume without on-disk8.3
                // names. Provision and exercise an independently owned real
                // SQLite copy in the user's local temp as well. Both attempts
                // are real; an unchanged long spelling never qualifies either.
                var ownedAlias = new Variant { Id = "shortpath_owned_temp" };
                evidence.Variants.Add(ownedAlias);
                ProbeOwnedTempAlias(database, context, ownedAlias);
                var alias = IsShortAliasProof(preparedAlias) ? preparedAlias : ownedAlias;
                evidence.EffectiveShortVariant = alias.Id;
                test.PrivateDiagnostic("shortpath_variants;preparedDistinct=" + preparedAlias.DistinctAlias +
                    ";preparedSameFile=" + preparedAlias.SamePhysicalFile + ";ownedDistinct=" + ownedAlias.DistinctAlias +
                    ";ownedSameFile=" + ownedAlias.SamePhysicalFile + ";ownedConsistent=" + ownedAlias.CopyConsistent +
                    ";ownedOwnerReleased=" + ownedAlias.OwnerReleased + ";selectedOwned=" + (alias == ownedAlias));
                test.Check("case_shortpath_alias_collide", casing.DistinctAlias && casing.SamePhysicalFile && casing.DenialCode == "database_in_use" &&
                    IsShortAliasProof(alias));

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

        private static bool IsShortAliasProof(Variant alias) => alias.NativeSucceeded && alias.DistinctAlias &&
            alias.SamePhysicalFile && alias.DenialCode == "database_in_use" &&
            (alias.FixturePath == null || alias.CopyConsistent && alias.OwnerReleased && alias.PreparationErrorType == null);

        private static void ObserveShortAlias(string database, Identity original, Variant alias)
        {
            var shortName = new StringBuilder(32768);
            var length = GetShortPathNameW(database, shortName, (uint)shortName.Capacity);
            alias.NativeError = length == 0 ? Marshal.GetLastWin32Error() : 0;
            alias.NativeSucceeded = length > 0 && length < shortName.Capacity;
            if (!alias.NativeSucceeded) return;
            var shortPath = shortName.ToString();
            // .NET Framework GetFullPath expands an existing8.3 name. Compare
            // the absolute Win32 spelling BEFORE expansion; require handle
            // identity and pass that same RAW alias to the real authority API.
            alias.DistinctAlias = Path.IsPathRooted(shortPath) && Path.IsPathRooted(database) &&
                !string.Equals(shortPath, database, StringComparison.OrdinalIgnoreCase);
            alias.SamePhysicalFile = ReadIdentity(shortPath).Hash == original.Hash;
            if (alias.DistinctAlias && alias.SamePhysicalFile) alias.DenialCode = ProbeAuthority(shortPath);
        }

        private static void ProbeOwnedTempAlias(string database, HistoricalSalesQaContext context, Variant alias)
        {
            PosDatabaseAuthorityLease owner = null;
            try
            {
                // Use the OS known folder, not TEMP/TMP overridden to the
                // runner's alternate volume. Never change global8dot3 policy.
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) throw new InvalidOperationException("qa_alias_owned_temp_unavailable");
                var parent = Path.GetFullPath(Path.Combine(local, "Temp"));
                PosDatabasePathIdentity.RejectReparseComponents(parent);
                var volume = new DriveInfo(Path.GetPathRoot(parent));
                if (!volume.IsReady || volume.DriveType != DriveType.Fixed || !string.Equals(volume.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("qa_alias_owned_ntfs_unavailable");
                Directory.CreateDirectory(parent);
                var root = Path.GetFullPath(Path.Combine(parent, "W7Q83." + context.RunNonce.Substring(0, 8) + "." + Guid.NewGuid().ToString("N").Substring(0, 12)));
                if (!root.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(root) || File.Exists(root) || root.StartsWith(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("qa_alias_owned_root_invalid");
                Directory.CreateDirectory(root);
                alias.FixturePath = Path.Combine(root, "authority-variant.db");
                alias.VolumeFileSystem = volume.DriveFormat;
                alias.FixtureRunId = context.RunId;
                alias.FixtureRunNonce = context.RunNonce;
                var options = new PosDbOptions(alias.FixturePath);
                owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(options);
                alias.FixtureToken = owner.FixtureToken;
                // Prepared A has no open SQLite consumers here. This read-only
                // guard prevents mutation while copying its exact closed image.
                using (var frozen = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (File.Exists(database + "-journal") || File.Exists(database + "-wal") || File.Exists(database + "-shm"))
                        throw new InvalidOperationException("qa_alias_source_sidecar_present");
                    var sourceHash = HashStream(frozen);
                    using (var target = new FileStream(alias.FixturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { frozen.Position = 0; frozen.CopyTo(target); target.Flush(true); }
                    owner.AccessContext.BindCreatedFile();
                    using (var target = new FileStream(alias.FixturePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        alias.CopyConsistent = target.Length == frozen.Length && HashStream(target) == sourceHash && HashStream(frozen) == sourceHash;
                    if (!alias.CopyConsistent) throw new InvalidOperationException("qa_alias_copy_not_consistent");
                    alias.SourceSha256 = sourceHash;
                    ObserveShortAlias(alias.FixturePath, ReadIdentity(alias.FixturePath), alias);
                }
            }
            catch (Exception error)
            {
                // This optional volume cannot make an absent alias PASS. Keep
                // preparation failures distinct from the actual denial code.
                alias.PreparationErrorType = error.GetType().FullName;
                alias.PreparationHResult = error.HResult;
                alias.PreparationError = error.ToString();
            }
            finally
            {
                if (owner != null) { owner.Dispose(); alias.OwnerReleased = true; }
                // Synthetic image/token stay retained at the unique owned path;
                // no recursive cleanup or traversal of user data is performed.
            }
        }

        private static string HashStream(Stream stream)
        {
            stream.Position = 0;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
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
            [DataMember(Name = "effectiveShortVariant")] public string EffectiveShortVariant { get; set; }
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
            [DataMember(Name = "fixturePath", EmitDefaultValue = false)] public string FixturePath { get; set; }
            [DataMember(Name = "fixtureToken", EmitDefaultValue = false)] public string FixtureToken { get; set; }
            [DataMember(Name = "fixtureRunId", EmitDefaultValue = false)] public string FixtureRunId { get; set; }
            [DataMember(Name = "fixtureRunNonce", EmitDefaultValue = false)] public string FixtureRunNonce { get; set; }
            [DataMember(Name = "volumeFileSystem", EmitDefaultValue = false)] public string VolumeFileSystem { get; set; }
            [DataMember(Name = "sourceSha256", EmitDefaultValue = false)] public string SourceSha256 { get; set; }
            [DataMember(Name = "copyConsistent")] public bool CopyConsistent { get; set; }
            [DataMember(Name = "ownerReleased")] public bool OwnerReleased { get; set; }
            [DataMember(Name = "preparationErrorType", EmitDefaultValue = false)] public string PreparationErrorType { get; set; }
            [DataMember(Name = "preparationHResult")] public int PreparationHResult { get; set; }
            [DataMember(Name = "preparationError", EmitDefaultValue = false)] public string PreparationError { get; set; }
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
