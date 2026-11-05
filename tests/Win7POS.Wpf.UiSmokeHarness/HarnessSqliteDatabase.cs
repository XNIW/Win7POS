using System;
using Win7POS.Data;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>Exact-file test lifetime; creation and existing snapshot reads are distinct capabilities.</summary>
    internal sealed class HarnessSqliteDatabase : IDisposable
    {
        private readonly PosDatabaseAuthorityLease _owner;
        private readonly PosDatabaseAccessContext _snapshot;
        private HarnessSqliteDatabase(PosDbOptions options, PosDatabaseAuthorityLease owner, PosDatabaseAccessContext snapshot)
        { Options = options; _owner = owner; _snapshot = snapshot; Factory = new SqliteConnectionFactory(options); }
        internal PosDbOptions Options { get; }
        internal SqliteConnectionFactory Factory { get; }
        internal static HarnessSqliteDatabase CreateNew(string path)
        {
            var options = PosDbOptions.ForPath(path);
            var owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(options);
            try { return new HarnessSqliteDatabase(options.WithAccessContext(owner.AccessContext), owner, null); }
            catch { owner.Dispose(); throw; }
        }
        internal static HarnessSqliteDatabase ReadExistingSnapshot(string path)
        {
            var snapshot = PosDatabaseAccessContext.SelectedSource(path);
            try { return new HarnessSqliteDatabase(PosDbOptions.ForPath(path).WithAccessContext(snapshot), null, snapshot); }
            catch { snapshot.Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_owner != null) { SqliteConnectionFactory.ClearAllPools(); _owner.Dispose(); }
            _snapshot?.Dispose();
        }
    }
}
