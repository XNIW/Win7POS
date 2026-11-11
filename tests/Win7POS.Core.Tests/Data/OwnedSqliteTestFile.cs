using Win7POS.Data;

namespace Win7POS.Core.Tests.Data;

/// <summary>Exact-file fixture ownership. A restart requires the original capability; no unowned file or directory is adopted.</summary>
internal sealed class OwnedSqliteTestFile : IDisposable
{
    private readonly PosDatabaseAuthorityLease _owner;
    private OwnedSqliteTestFile(string path,string? fixtureToken=null)
    {
        Options = PosDbOptions.ForPath(path);
        _owner = fixtureToken==null?PosDatabaseAuthorityLease.AcquireSyntheticNew(Options):PosDatabaseAuthorityLease.AcquireSyntheticExisting(Options,fixtureToken);
        Options = Options.WithAccessContext(_owner.AccessContext);
        Factory = new SqliteConnectionFactory(Options);
    }
    internal PosDbOptions Options { get; }
    internal SqliteConnectionFactory Factory { get; }
    internal string FixtureToken => _owner.FixtureToken;
    internal static OwnedSqliteTestFile Create(string path) => new(path);
    internal static OwnedSqliteTestFile Reopen(string path,string fixtureToken)
    {
        if(string.IsNullOrWhiteSpace(fixtureToken))throw new ArgumentException("Fixture capability required.",nameof(fixtureToken));
        if(!Path.IsPathRooted(path)||Path.GetFullPath(path)!=path||!File.Exists(path))throw new InvalidOperationException("history_test_owned_database_missing");
        return new(path,fixtureToken);
    }
    public void Dispose() { SqliteConnectionFactory.ClearAllPools(); _owner.Dispose(); }
}
