using Win7POS.Data;

namespace Win7POS.Core.Tests.Data;

/// <summary>Exact-file fixture ownership; this never adopts an existing database or grants a whole temp directory.</summary>
internal sealed class OwnedSqliteTestFile : IDisposable
{
    private readonly PosDatabaseAuthorityLease _owner;
    private OwnedSqliteTestFile(string path)
    {
        Options = PosDbOptions.ForPath(path);
        _owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(Options);
        Options = Options.WithAccessContext(_owner.AccessContext);
        Factory = new SqliteConnectionFactory(Options);
    }
    internal PosDbOptions Options { get; }
    internal SqliteConnectionFactory Factory { get; }
    internal static OwnedSqliteTestFile Create(string path) => new(path);
    public void Dispose() { SqliteConnectionFactory.ClearAllPools(); _owner.Dispose(); }
}
