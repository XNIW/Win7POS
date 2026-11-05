using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Reports;

namespace Win7POS.Data
{
    public enum PosDatabaseAccessRole { Live, SyntheticFixture, SelectedSnapshotSource, BackupPartial, BackupValidation, PublishTarget, RestoreCandidate, CandidateMigration, CandidateValidation, RestoreRollback }

    /// <summary>An exact-path capability. A live capability never authorizes a sibling path.</summary>
    public sealed class PosDatabaseAccessContext : IDisposable
    {
        private readonly PosDatabaseAuthorityLease _owner;
        private readonly bool _ownsLease;
        private bool _disposed;
        private readonly string _selectedIdentity;
        internal PosDatabaseAccessContext(PosDatabaseAuthorityLease owner, string path, PosDatabaseAccessRole role, bool ownsLease, string operationId = null)
        {
            _owner = owner; DbPath = Path.GetFullPath(path); Role = role; _ownsLease = ownsLease; OperationId = operationId ?? Guid.NewGuid().ToString("N");
            if (owner == null && PosDatabasePathIdentity.IsWindows && !DbPath.StartsWith(@"\\", StringComparison.Ordinal))
                _selectedIdentity = PosDatabasePathIdentity.FileIdentity(DbPath, false);
        }
        public string DbPath { get; }
        public PosDatabaseAccessRole Role { get; }
        public string OperationId { get; }
        public SalesReportAuthorityStamp AuthorityStamp => _owner == null ? throw new PosDatabaseAuthorityException("database_live_authority_required") : _owner.AuthorityStamp;
        internal bool AppliesRuntimePolicy => Role == PosDatabaseAccessRole.Live || Role == PosDatabaseAccessRole.SyntheticFixture ||
            Role == PosDatabaseAccessRole.CandidateMigration || Role == PosDatabaseAccessRole.BackupPartial;
        internal bool AllowsCreate => _owner != null && _owner.AllowCreate && (Role == PosDatabaseAccessRole.Live || Role == PosDatabaseAccessRole.SyntheticFixture);
        internal bool Pooling => Role == PosDatabaseAccessRole.Live || Role == PosDatabaseAccessRole.SyntheticFixture;
        public static PosDatabaseAccessContext Resolve(PosDbOptions options)
        {
            var context = options.AccessContext ?? PosDatabaseAuthorityLease.Resolve(options.DbPath);
            context.DemandPath(options.DbPath); return context;
        }
        public static bool TryResolve(PosDbOptions options, out PosDatabaseAccessContext context)
        {
            try { context = Resolve(options); return true; }
            catch (PosDatabaseAuthorityException error) when (error.Code == "database_authority_required") { context = null; return false; }
        }
        public void DemandPath(string path)
        {
            if (_disposed || !string.Equals(PosDatabasePathIdentity.CanonicalSlot(DbPath), PosDatabasePathIdentity.CanonicalSlot(path), PosDatabasePathIdentity.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new PosDatabaseAuthorityException("database_context_path_mismatch");
            _owner?.DemandCurrent();
            if (_owner == null && !File.Exists(DbPath)) throw new FileNotFoundException("database_existing_file_required");
            if (_selectedIdentity != null && _selectedIdentity != PosDatabasePathIdentity.FileIdentity(DbPath, false))
                throw new PosDatabaseAuthorityException("database_selected_source_identity_changed");
        }
        public void DemandInitialization()
        {
            DemandPath(DbPath);
            if (Role != PosDatabaseAccessRole.Live && Role != PosDatabaseAccessRole.SyntheticFixture && Role != PosDatabaseAccessRole.CandidateMigration)
                throw new PosDatabaseAuthorityException("database_context_initialization_denied");
        }
        internal IDisposable EnterConnection() { DemandPath(DbPath); return _owner?.EnterConnection() ?? new EmptyLease(); }
        internal void BindCreatedFile() { _owner?.BindCreatedFile(); }
        internal SqliteOpenMode OpenMode(bool existingOnly)
        {
            DemandPath(DbPath);
            if (Role == PosDatabaseAccessRole.PublishTarget) throw new PosDatabaseAuthorityException("database_context_open_denied");
            if (Role == PosDatabaseAccessRole.SelectedSnapshotSource || Role == PosDatabaseAccessRole.BackupValidation || Role == PosDatabaseAccessRole.CandidateValidation)
                return SqliteOpenMode.ReadOnly;
            if (!existingOnly && AllowsCreate) return SqliteOpenMode.ReadWriteCreate;
            if (!File.Exists(DbPath)) throw new FileNotFoundException("database_existing_file_required");
            return SqliteOpenMode.ReadWrite;
        }
        internal void SetRestoreEpoch(long epoch) { DemandPath(DbPath); _owner?.SetRestoreEpoch(epoch); }
        internal void RebindAfterRestore()
        {
            if (Role != PosDatabaseAccessRole.Live && Role != PosDatabaseAccessRole.SyntheticFixture) throw new PosDatabaseAuthorityException("database_live_rebind_denied");
            _owner.RebindAfterRestore();
        }
        internal void DemandRestoreSlot()
        {
            if (_disposed || _owner == null || (Role != PosDatabaseAccessRole.Live && Role != PosDatabaseAccessRole.SyntheticFixture))
                throw new PosDatabaseAuthorityException("database_live_restore_authority_required");
            _owner.DemandSlot();
        }
        internal static PosDatabaseAccessContext ResolveRestoreSlot(string path)
        {
            var context = PosDatabaseAuthorityLease.Resolve(path);
            context.DemandRestoreSlot(); return context;
        }
        internal PosDatabaseAccessContext Narrow(PosDatabaseAccessRole role)
        {
            DemandPath(DbPath);
            var allowed = role == Role ||
                ((Role == PosDatabaseAccessRole.Live || Role == PosDatabaseAccessRole.SyntheticFixture) && role == PosDatabaseAccessRole.SelectedSnapshotSource) ||
                (Role == PosDatabaseAccessRole.BackupPartial && role == PosDatabaseAccessRole.BackupValidation) ||
                (Role == PosDatabaseAccessRole.RestoreCandidate && (role == PosDatabaseAccessRole.CandidateMigration || role == PosDatabaseAccessRole.CandidateValidation));
            if (!allowed) throw new PosDatabaseAuthorityException("database_context_role_escalation_denied");
            return new PosDatabaseAccessContext(_owner, DbPath, role, false, OperationId);
        }
        internal PosDatabaseAccessContext ForOperation(string operationId)
        {
            DemandPath(DbPath); ValidateOperationId(operationId);
            return new PosDatabaseAccessContext(_owner, DbPath, Role, false, operationId);
        }
        internal PosDatabaseAccessContext ForRestoreOperation(string operationId)
        {
            DemandRestoreSlot(); ValidateOperationId(operationId);
            return new PosDatabaseAccessContext(_owner, DbPath, Role, false, operationId);
        }
        private static void ValidateOperationId(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
                throw new PosDatabaseAuthorityException("database_operation_invalid");
            foreach (var character in operationId) if (char.IsControl(character)) throw new PosDatabaseAuthorityException("database_operation_invalid");
        }
        internal void DemandOperation(string operationId, string path, params PosDatabaseAccessRole[] roles)
        {
            DemandPath(path);
            if (!string.Equals(OperationId, operationId, StringComparison.Ordinal) || Array.IndexOf(roles, Role) < 0)
                throw new PosDatabaseAuthorityException("database_context_operation_or_role_mismatch");
        }
        private void DemandAuxiliaryCreation()
        {
            DemandPath(DbPath);
            if (Role != PosDatabaseAccessRole.Live && Role != PosDatabaseAccessRole.SyntheticFixture && Role != PosDatabaseAccessRole.CandidateMigration)
                throw new PosDatabaseAuthorityException("database_auxiliary_creation_denied");
        }
        internal static PosDatabaseAccessContext ReservePublishTarget(string path, PosDatabaseAccessContext provenance)
        {
            provenance.DemandAuxiliaryCreation();
            if (File.Exists(path)) throw new IOException("database_auxiliary_target_exists");
            var owner = PosDatabaseAuthorityLease.AcquireAuxiliary(path, PosDatabaseAccessRole.PublishTarget);
            return new PosDatabaseAccessContext(owner, path, PosDatabaseAccessRole.PublishTarget, true, provenance.OperationId);
        }
        internal static PosDatabaseAccessContext SelectedSource(string path, string operationId = null)
        {
            path = PosDatabasePathIdentity.CanonicalSlot(path);
            PosDatabasePathIdentity.RejectReparseComponents(path);
            if (!File.Exists(path)) throw new FileNotFoundException("database_existing_file_required");
            return new PosDatabaseAccessContext(null, path, PosDatabaseAccessRole.SelectedSnapshotSource, false, operationId);
        }
        internal static PosDatabaseAccessContext Allocate(string path, PosDatabaseAccessRole role, PosDatabaseAccessContext provenance)
        {
            provenance.DemandAuxiliaryCreation();
            if (role != PosDatabaseAccessRole.BackupPartial && role != PosDatabaseAccessRole.RestoreCandidate)
                throw new PosDatabaseAuthorityException("database_auxiliary_role_invalid");
            if (File.Exists(path)) throw new IOException("database_auxiliary_target_exists");
            var owner = PosDatabaseAuthorityLease.AcquireAuxiliary(path, role);
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                owner.BindCreatedFile();
                return new PosDatabaseAccessContext(owner, path, role, true, provenance.OperationId);
            }
            catch { owner.Dispose(); throw; }
        }
        internal static PosDatabaseAccessContext AdoptRollback(string path, PosDatabaseAccessContext live)
        {
            live.DemandRestoreSlot();
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetDirectoryName(live.DbPath), PosDatabasePathIdentity.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new PosDatabaseAuthorityException("database_rollback_path_mismatch");
            var existing = PosDatabaseAuthorityLease.FindOwned(path);
            if (existing != null)
            {
                if (existing.Role != PosDatabaseAccessRole.SyntheticFixture && existing.Role != PosDatabaseAccessRole.RestoreRollback)
                    throw new PosDatabaseAuthorityException("database_rollback_already_owned_for_other_purpose");
                existing.DemandPath(path);
                return new PosDatabaseAccessContext(existing._owner, path, PosDatabaseAccessRole.RestoreRollback, false, live.OperationId);
            }
            var owner = PosDatabaseAuthorityLease.AcquireAuxiliary(path, PosDatabaseAccessRole.RestoreRollback);
            return new PosDatabaseAccessContext(owner, path, PosDatabaseAccessRole.RestoreRollback, true);
        }
        internal void RecreateOwnedPartial()
        {
            if (Role != PosDatabaseAccessRole.BackupPartial && Role != PosDatabaseAccessRole.RestoreCandidate)
                throw new PosDatabaseAuthorityException("database_auxiliary_recreate_denied");
            _owner.PrepareOwnedRecreation();
            using (new FileStream(DbPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            _owner.BindCreatedFile();
        }
        public void Dispose() { if (_disposed) return; if (_ownsLease) _owner.Dispose(); _disposed = true; }
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }
}
