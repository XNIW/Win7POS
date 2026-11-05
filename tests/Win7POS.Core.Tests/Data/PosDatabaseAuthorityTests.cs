using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class PosDatabaseAuthorityTests
{
    [TestMethod]
    public void LockContention_AdmitsOnlyWin32SharingOrLockViolation()
    {
        Assert.IsTrue(PosDatabaseAuthorityLease.IsLockContention(new IOException("synthetic sharing", unchecked((int)0x80070020))));
        Assert.IsTrue(PosDatabaseAuthorityLease.IsLockContention(new IOException("synthetic lock", unchecked((int)0x80070021))));
        Assert.IsFalse(PosDatabaseAuthorityLease.IsLockContention(new PathTooLongException("synthetic path")));
        Assert.IsFalse(PosDatabaseAuthorityLease.IsLockContention(new IOException("synthetic disk full", unchecked((int)0x80070070))));
        Assert.IsFalse(PosDatabaseAuthorityLease.IsLockContention(new IOException("synthetic missing path", unchecked((int)0x80070003))));
        Assert.IsFalse(PosDatabaseAuthorityLease.IsLockContention(new IOException("synthetic access denied", unchecked((int)0x80070005))));
        Assert.IsFalse(PosDatabaseAuthorityLease.IsLockContention(new IOException("synthetic non-Win32 facility", unchecked((int)0x80130020))));
    }

    [TestMethod]
    public void FactoryWithoutOwner_DoesNotCreateOrBootstrapAnUnknownDatabase()
    {
        using var files = new Files();
        var options = PosDbOptions.ForPath(files.Path("unknown.db"));
        var factory = new SqliteConnectionFactory(options);
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => factory.Open());
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => DbInitializer.EnsureCreated(options));
        Assert.IsFalse(File.Exists(options.DbPath));
    }

    [TestMethod]
    public void ExactContext_CannotOpenAnotherFileOrPromoteValidationToMigration()
    {
        using var files = new Files();
        using var database = OwnedSqliteTestFile.Create(files.Path("live.db"));
        DbInitializer.EnsureCreated(database.Options);
        var wrong = new SqliteConnectionFactory(PosDbOptions.ForPath(files.Path("other.db")).WithAccessContext(database.Options.AccessContext));
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => wrong.Open());
        Assert.IsFalse(File.Exists(wrong.DbPath));
        var operation = database.Factory.AccessContext.ForOperation("synthetic-backup-operation");
        using var partial = PosDatabaseAccessContext.Allocate(files.Path("snapshot.partial"), PosDatabaseAccessRole.BackupPartial, operation);
        using var validation = partial.Narrow(PosDatabaseAccessRole.BackupValidation);
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => validation.Narrow(PosDatabaseAccessRole.CandidateMigration));
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => DbInitializer.EnsureCreated(PosDbOptions.ForPath(partial.DbPath).WithAccessContext(validation)));
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => partial.DemandOperation("another-operation", partial.DbPath, PosDatabaseAccessRole.BackupPartial));
    }

    [TestMethod]
    public void PublishTarget_CannotBeOpenedAsAWritableDatabase()
    {
        using var files = new Files();
        using var database = OwnedSqliteTestFile.Create(files.Path("live.db"));
        DbInitializer.EnsureCreated(database.Options);
        using var target = PosDatabaseAccessContext.ReservePublishTarget(files.Path("published.db"), database.Factory.AccessContext.ForOperation("publish-operation"));
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(target.DbPath).WithAccessContext(target));
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => factory.Open());
        Assert.IsFalse(File.Exists(target.DbPath));
    }

    [TestMethod]
    public void ExistingOnlyRecovery_DoesNotReplaceAMissingFileWithAnEmptyDatabase()
    {
        using var files = new Files();
        using var database = OwnedSqliteTestFile.Create(files.Path("not-created.db"));
        Assert.ThrowsExactly<FileNotFoundException>(() => database.Factory.OpenExistingForRecovery());
        Assert.IsFalse(File.Exists(database.Options.DbPath));
    }

    [TestMethod]
    public void DisposalWithAnOpenConnection_KeepsAuthorityUntilTheConnectionCloses()
    {
        using var files = new Files();
        var options = PosDbOptions.ForPath(files.Path("live.db"));
        using var owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(options);
        DbInitializer.EnsureCreated(options);
        var connection = new SqliteConnectionFactory(options).Open();
        try
        {
            Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => owner.Dispose());
            Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => PosDatabaseAuthorityLease.AcquireSyntheticExisting(options, owner.FixtureToken));
        }
        finally { connection.Dispose(); }
        owner.Dispose();
        using var replacement = PosDatabaseAuthorityLease.AcquireSyntheticExisting(options, owner.FixtureToken);
        Assert.IsNotNull(replacement.AuthorityStamp);
    }

    [TestMethod]
    public void OldFactory_DoesNotSilentlyAttachToANewAuthorityInstance()
    {
        using var files = new Files();
        var options = PosDbOptions.ForPath(files.Path("live.db"));
        var owner = PosDatabaseAuthorityLease.AcquireSyntheticNew(options);
        DbInitializer.EnsureCreated(options);
        var oldFactory = new SqliteConnectionFactory(options);
        var oldStamp = oldFactory.AuthorityStamp;
        var fixtureToken = owner.FixtureToken;
        owner.Dispose();
        using var replacement = PosDatabaseAuthorityLease.AcquireSyntheticExisting(options, fixtureToken);
        Assert.AreNotEqual(oldStamp.AuthorityInstanceEpoch, replacement.AuthorityStamp.AuthorityInstanceEpoch);
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => oldFactory.Open());
        Assert.ThrowsExactly<PosDatabaseAuthorityException>(() => { _ = oldFactory.AuthorityStamp; });
        using var connection = new SqliteConnectionFactory(options).OpenExistingForRecovery();
        Assert.IsNotNull(connection);
    }

    private sealed class Files : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Win7POS.AuthorityTests", Guid.NewGuid().ToString("N"));
        internal Files() => Directory.CreateDirectory(_root);
        internal string Path(string name) => System.IO.Path.Combine(_root, name);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
