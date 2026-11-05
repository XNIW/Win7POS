using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Win7POS.Core.Reports;
using Win7POS.Data.Reports;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data
{
    public sealed class PosDatabaseAuthorityException : InvalidOperationException
    {
        public PosDatabaseAuthorityException(string code) : base(code) { Code = code; }
        public PosDatabaseAuthorityException(string code, Exception innerException) : base(code, innerException) { Code = code; }
        public string Code { get; }
    }

    /// <summary>One OS-backed writing authority for one exact database slot. File existence is never the lock.</summary>
    public sealed class PosDatabaseAuthorityLease : IDisposable
    {
        private static readonly object RegistrySync = new object();
        private static readonly Dictionary<string, PosDatabaseAuthorityLease> Registry = new Dictionary<string, PosDatabaseAuthorityLease>(StringComparer.Ordinal);
        private readonly object _sync = new object();
        private readonly FileStream _lock;
        private readonly IDisposable _parentPin;
        private readonly bool _windowsIdentity;
        private readonly string _parentIdentity;
        private readonly string _inputPath;
        private string _fileIdentity;
        private bool _disposed;
        private int _connections;
        private long _restoreEpoch;
        private string _instanceEpoch = Guid.NewGuid().ToString("N");
        private SalesReportAuthorityStamp _stamp;
        private readonly SalesReportSnapshotCoordinator _reportCoordinator;
        private bool _authorityTransitionPending;

        private PosDatabaseAuthorityLease(string path, string key, FileStream processLock, IDisposable parentPin,
            bool windowsIdentity, string parentIdentity, bool allowCreate, string fixtureToken, PosDatabaseAccessRole role, string inputPath)
        {
            DbPath = path; Key = key; _lock = processLock; _parentPin = parentPin; _windowsIdentity = windowsIdentity;
            _parentIdentity = parentIdentity; AllowCreate = allowCreate; FixtureToken = fixtureToken;
            _inputPath = inputPath;
            _fileIdentity = File.Exists(path) && windowsIdentity ? PosDatabasePathIdentity.FileIdentity(path, false) : null;
            _stamp = new SalesReportAuthorityStamp(0, _instanceEpoch);
            if (role == PosDatabaseAccessRole.Live || role == PosDatabaseAccessRole.SyntheticFixture)
                _reportCoordinator = SalesReportSnapshotCoordinator.ForDatabase(path);
            AccessContext = new PosDatabaseAccessContext(this, path, role, false);
        }

        public string DbPath { get; }
        internal string Key { get; }
        internal bool AllowCreate { get; private set; }
        public string FixtureToken { get; }
        internal bool IsOwnedBootstrapMetadata(string path)
        {
            DemandSlot();
            if (FixtureToken == null || File.Exists(DbPath)) return false;
            var full = Path.GetFullPath(path);
            var comparison = PosDatabasePathIdentity.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(full, DbPath + ".synthetic-owner", comparison)) return true;
            var lockDirectory = Path.GetDirectoryName(_lock.Name);
            if (!string.Equals(full, lockDirectory, comparison) || !Directory.Exists(full)) return false;
            var entries = Directory.GetFileSystemEntries(full);
            return entries.Length == 1 && string.Equals(entries[0], _lock.Name, comparison);
        }
        public PosDatabaseAccessContext AccessContext { get; }
        public SalesReportAuthorityStamp AuthorityStamp
        {
            get
            {
                var stamp = Volatile.Read(ref _stamp);
                if (Volatile.Read(ref _disposed)) throw new PosDatabaseAuthorityException("database_authority_disposed");
                return stamp ?? throw new PosDatabaseAuthorityException("database_authority_unavailable");
            }
        }

        public static PosDatabaseAuthorityLease AcquireOperational(PosDbOptions options)
        {
            if (!PosDatabasePathIdentity.IsWindows) throw new PosDatabaseAuthorityException("database_operational_identity_unsupported");
            return AcquireCore(options.DbPath, true, null, PosDatabaseAccessRole.Live);
        }

        /// <summary>Only a new synthetic DB can establish a fixture token. Existing operational files cannot opt in.</summary>
        internal static PosDatabaseAuthorityLease AcquireSyntheticNew(PosDbOptions options)
        {
            var path = Path.GetFullPath(options.DbPath);
            DemandPortableFixturePath(path);
            if (File.Exists(path)) throw new PosDatabaseAuthorityException("database_fixture_must_be_new");
            var token = Guid.NewGuid().ToString("N");
            var owner = AcquireCore(path, PosDatabasePathIdentity.IsWindows, token, PosDatabaseAccessRole.SyntheticFixture);
            try
            {
                using (var marker = new FileStream(path + ".synthetic-owner", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(marker, new UTF8Encoding(false))) writer.Write(token);
                return owner;
            }
            catch { owner.Dispose(); throw; }
        }

        internal static PosDatabaseAuthorityLease AcquireSyntheticExisting(PosDbOptions options, string fixtureToken)
        {
            var path = Path.GetFullPath(options.DbPath);
            DemandPortableFixturePath(path);
            if (string.IsNullOrWhiteSpace(fixtureToken) || fixtureToken.Length != 32 ||
                !string.Equals(File.ReadAllText(path + ".synthetic-owner"), fixtureToken, StringComparison.Ordinal))
                throw new PosDatabaseAuthorityException("database_fixture_provenance_invalid");
            return AcquireCore(path, PosDatabasePathIdentity.IsWindows, fixtureToken, PosDatabaseAccessRole.SyntheticFixture);
        }
        private static void DemandPortableFixturePath(string path)
        {
            if (PosDatabasePathIdentity.IsWindows) return;
            var temp = Path.GetDirectoryName(PosDatabasePathIdentity.CanonicalSlot(Path.Combine(Path.GetTempPath(), "authority-root-check")));
            var candidate = PosDatabasePathIdentity.CanonicalSlot(path);
            if (!candidate.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new PosDatabaseAuthorityException("database_fixture_owned_temp_root_required");
        }

        internal static PosDatabaseAuthorityLease AcquireAuxiliary(string path, PosDatabaseAccessRole role)
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new PosDatabaseAuthorityException("database_device_path_unsupported");
            return AcquireCore(path, PosDatabasePathIdentity.IsWindows && !full.StartsWith(@"\\", StringComparison.Ordinal), null, role);
        }

        private static PosDatabaseAuthorityLease AcquireCore(string input, bool windowsIdentity, string fixtureToken, PosDatabaseAccessRole role)
        {
            var path = Path.GetFullPath(input);
            var originalPath = path;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) throw new PosDatabaseAuthorityException("database_path_invalid");
            Directory.CreateDirectory(directory);
            if (!PosDatabasePathIdentity.IsWindows)
            { path = PosDatabasePathIdentity.CanonicalSlot(path); directory = Path.GetDirectoryName(path); }
            PosDatabasePathIdentity.RejectReparseComponents(path);
            if (windowsIdentity)
            {
                path = File.Exists(path) ? PosDatabasePathIdentity.FinalPath(path, false) :
                    Path.Combine(PosDatabasePathIdentity.FinalPath(directory, true), Path.GetFileName(path));
                directory = Path.GetDirectoryName(path);
            }
            IDisposable pin = null; FileStream processLock = null;
            try
            {
                var parentIdentity = windowsIdentity ? PosDatabasePathIdentity.FileIdentity(directory, true) : directory;
                if (windowsIdentity)
                {
                    if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new PosDatabaseAuthorityException("database_network_path_unsupported");
                    pin = PosDatabasePathIdentity.PinDirectory(directory);
                }
                var key = parentIdentity + "|" + (PosDatabasePathIdentity.IsWindows ? Path.GetFileName(path).ToUpperInvariant() : Path.GetFileName(path));
                lock (RegistrySync) if (Registry.ContainsKey(key)) throw new PosDatabaseAuthorityException("database_in_use");
                var lockDirectory = Path.Combine(directory, ".win7pos-authority");
                Directory.CreateDirectory(lockDirectory);
                PosDatabasePathIdentity.RejectReparseComponents(lockDirectory);
                string lockName;
                using (var hash = SHA256.Create()) lockName = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
                try { processLock = new FileStream(Path.Combine(lockDirectory, lockName + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when (IsLockContention(error)) { throw new PosDatabaseAuthorityException("database_in_use", error); }
                catch (IOException error) { throw new PosDatabaseAuthorityException(error is PathTooLongException ? "database_lock_path_too_long" : "database_lock_io_failure", error); }
                catch (UnauthorizedAccessException error) { throw new PosDatabaseAuthorityException("database_lock_access_denied", error); }
                if (windowsIdentity) PosDatabasePathIdentity.VerifySingleLink(processLock.SafeFileHandle);
                var owner = new PosDatabaseAuthorityLease(path, key, processLock, pin, windowsIdentity, parentIdentity, !File.Exists(path), fixtureToken, role, originalPath);
                owner.DemandCurrent();
                lock (RegistrySync)
                {
                    if (Registry.ContainsKey(key)) throw new PosDatabaseAuthorityException("database_in_use");
                    Registry.Add(key, owner);
                }
                try { owner._reportCoordinator?.BindAuthority(owner, () => Volatile.Read(ref owner._stamp)); }
                catch
                {
                    lock (RegistrySync) Registry.Remove(key);
                    Volatile.Write(ref owner._stamp, null); Volatile.Write(ref owner._disposed, true);
                    throw;
                }
                processLock = null; pin = null; return owner;
            }
            finally { processLock?.Dispose(); pin?.Dispose(); }
        }

        internal static bool IsLockContention(IOException error)
        {
            // Only Win32 sharing/lock violations prove another holder. Path,
            // media and filesystem failures remain distinct and fail closed.
            var hresult = unchecked((uint)error.HResult);
            var win32Code = hresult & 0xffff;
            return !(error is PathTooLongException) && (hresult & 0xffff0000u) == 0x80070000u && (win32Code == 32 || win32Code == 33);
        }

        internal static PosDatabaseAccessContext Resolve(string path)
        {
            var full = PosDatabasePathIdentity.CanonicalSlot(path);
            lock (RegistrySync)
                foreach (var owner in Registry.Values)
                    if ((owner.AccessContext.Role == PosDatabaseAccessRole.Live || owner.AccessContext.Role == PosDatabaseAccessRole.SyntheticFixture) &&
                        string.Equals(owner.DbPath, full, PosDatabasePathIdentity.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return owner.AccessContext;
            throw new PosDatabaseAuthorityException("database_authority_required");
        }
        internal static PosDatabaseAccessContext FindOwned(string path)
        {
            var full = PosDatabasePathIdentity.CanonicalSlot(path);
            lock (RegistrySync)
                foreach (var owner in Registry.Values)
                    if (string.Equals(owner.DbPath, full, PosDatabasePathIdentity.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return owner.AccessContext;
            return null;
        }
        internal static PosDatabaseAccessContext FindLiveMemory(string path)
        {
            var full = Path.GetFullPath(path);
            lock (RegistrySync)
                foreach (var owner in Registry.Values)
                    if ((owner.AccessContext.Role == PosDatabaseAccessRole.Live || owner.AccessContext.Role == PosDatabaseAccessRole.SyntheticFixture) &&
                        (string.Equals(owner.DbPath, full, PosDatabasePathIdentity.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                         string.Equals(owner._inputPath, full, PosDatabasePathIdentity.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return owner.AccessContext;
            return null;
        }

        internal void DemandCurrent() { lock (_sync) DemandCurrentLocked(); }
        internal void DemandSlot()
        {
            lock (_sync)
            {
                if (_disposed) throw new PosDatabaseAuthorityException("database_authority_disposed");
                if (_authorityTransitionPending) throw new PosDatabaseAuthorityException("database_authority_transition_pending");
                PosDatabasePathIdentity.RejectReparseComponents(DbPath);
                if (_windowsIdentity && _parentIdentity != PosDatabasePathIdentity.FileIdentity(Path.GetDirectoryName(DbPath), true))
                    throw new PosDatabaseAuthorityException("database_parent_identity_changed");
            }
        }
        private void DemandCurrentLocked()
        {
            if (_disposed) throw new PosDatabaseAuthorityException("database_authority_disposed");
            if (_authorityTransitionPending) throw new PosDatabaseAuthorityException("database_authority_transition_pending");
            PosDatabasePathIdentity.RejectReparseComponents(DbPath);
            if (_windowsIdentity && _parentIdentity != PosDatabasePathIdentity.FileIdentity(Path.GetDirectoryName(DbPath), true))
                throw new PosDatabaseAuthorityException("database_parent_identity_changed");
            if (!File.Exists(DbPath))
            { if (!AllowCreate) throw new FileNotFoundException("database_existing_file_required"); return; }
            AllowCreate = false;
            if (!_windowsIdentity) return;
            if (File.Exists(DbPath))
            {
                var identity = PosDatabasePathIdentity.FileIdentity(DbPath, false);
                if (_fileIdentity != null && identity != _fileIdentity) throw new PosDatabaseAuthorityException("database_identity_changed");
                _fileIdentity = identity;
            }
        }

        internal IDisposable EnterConnection()
        { lock (_sync) { DemandCurrentLocked(); _connections++; return new ConnectionLease(this); } }
        internal void BindCreatedFile()
        { lock (_sync) { DemandCurrentLocked(); if (!File.Exists(DbPath)) throw new FileNotFoundException("database_existing_file_required"); AllowCreate = false; } }
        internal void SetRestoreEpoch(long epoch)
        {
            if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
            SalesReportAuthorityStamp next;
            lock (_sync)
            {
                DemandCurrentLocked();
                if (_restoreEpoch == epoch && _stamp != null) return;
                _authorityTransitionPending = true; next = new SalesReportAuthorityStamp(epoch, _stamp == null ? Guid.NewGuid().ToString("N") : _instanceEpoch);
            }
            PublishAuthorityChange(() => { _restoreEpoch = epoch; _instanceEpoch = next.AuthorityInstanceEpoch; Volatile.Write(ref _stamp, next); });
        }
        internal void RebindAfterRestore()
        {
            SalesReportAuthorityStamp next;
            lock (_sync)
            {
                if (_disposed || _authorityTransitionPending || _connections != 0) throw new PosDatabaseAuthorityException("database_rebind_connections_active");
                if (!File.Exists(DbPath)) throw new FileNotFoundException("database_existing_file_required");
                _fileIdentity = _windowsIdentity ? PosDatabasePathIdentity.FileIdentity(DbPath, false) : null;
                AllowCreate = false; _authorityTransitionPending = true;
                next = new SalesReportAuthorityStamp(_restoreEpoch, Guid.NewGuid().ToString("N"));
            }
            PublishAuthorityChange(() => { _instanceEpoch = next.AuthorityInstanceEpoch; Volatile.Write(ref _stamp, next); });
        }
        private void PublishAuthorityChange(Action publishMemoryOnly)
        {
            // All filesystem checks and owner-lock work happen before this call.
            // Gate order is publication -> memory stamp only; never owner _sync/DB.
            try
            {
                if (_reportCoordinator == null) publishMemoryOnly();
                else _reportCoordinator.ChangeAuthority(this, publishMemoryOnly);
            }
            finally { lock (_sync) { _authorityTransitionPending = false; } }
        }
        internal void PrepareOwnedRecreation()
        { lock (_sync) { if (_disposed || _connections != 0) throw new PosDatabaseAuthorityException("database_recreation_connections_active"); _fileIdentity = null; AllowCreate = true; } }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (_authorityTransitionPending || _connections != 0) throw new PosDatabaseAuthorityException("database_authority_connections_active");
                _authorityTransitionPending = true;
            }
            PublishAuthorityChange(() => { Volatile.Write(ref _stamp, null); Volatile.Write(ref _disposed, true); });
            lock (RegistrySync) Registry.Remove(Key);
            try
            { if (AccessContext.Role == PosDatabaseAccessRole.Live || AccessContext.Role == PosDatabaseAccessRole.SyntheticFixture) SqliteConnection.ClearAllPools(); }
            finally { try { _lock.Dispose(); } finally { _parentPin?.Dispose(); } }
        }
        private sealed class ConnectionLease : IDisposable
        {
            private PosDatabaseAuthorityLease _owner;
            internal ConnectionLease(PosDatabaseAuthorityLease owner) { _owner = owner; }
            public void Dispose() { var owner = Interlocked.Exchange(ref _owner, null); if (owner != null) lock (owner._sync) owner._connections--; }
        }
    }
}
