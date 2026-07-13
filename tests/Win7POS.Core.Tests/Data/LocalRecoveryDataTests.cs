using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class LocalRecoveryDataTests
{
    [TestMethod]
    public async Task BootstrapState_UsesSingleTargetedSnapshotAndDistinguishesDisabledUsers()
    {
        using var db = TestDb.Create();
        await db.ExecuteAsync(@"
INSERT INTO users(username, display_name, pin_hash, pin_salt, role_id, is_active, created_at, updated_at)
SELECT 'disabled', 'Disabled', 'hash', 'salt', id, 0, 1, 1 FROM roles WHERE code = 'admin';");

        var state = await new UserRepository(db.Factory).GetBootstrapStateAsync();

        Assert.AreEqual(1, state.TotalUserRows);
        Assert.AreEqual(0, state.ActiveLoginableUsers);
        Assert.AreEqual(0, state.ActiveRemoteMirrors);
        Assert.IsTrue(state.HasOnlyDisabledUsers);
    }

    [TestMethod]
    public async Task ConcurrentFirstRunCreation_CreatesExactlyOneAdministrator()
    {
        using var db = TestDb.Create();
        var first = CreateAdminAsync(db.Factory, "admin_one");
        var second = CreateAdminAsync(db.Factory, "admin_two");

        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, results.Count(x => x.CreatedSuccessfully));
        Assert.AreEqual(1L, await db.ScalarAsync("SELECT COUNT(*) FROM users"));
        Assert.AreEqual(2L, await db.ScalarAsync("SELECT COUNT(*) FROM security_events"));
    }

    [TestMethod]
    public async Task FirstRunAuditFailure_RollsBackUserAndAllAuditRows()
    {
        using var db = TestDb.Create();
        await db.ExecuteAsync(@"
CREATE TRIGGER fail_first_run_audit
BEFORE INSERT ON security_events
WHEN NEW.event_type = 'first_run_admin_created'
BEGIN
    SELECT RAISE(ABORT, 'simulated audit failure');
END;");

        await Assert.ThrowsExactlyAsync<SqliteException>(
            () => CreateAdminAsync(db.Factory, "admin_rollback"));

        Assert.AreEqual(0L, await db.ScalarAsync("SELECT COUNT(*) FROM users"));
        Assert.AreEqual(0L, await db.ScalarAsync("SELECT COUNT(*) FROM security_events"));
    }

    [TestMethod]
    public async Task LocalCatalogApproval_RequiresSellableProductAndWritesAtomicAudit()
    {
        using var db = TestDb.Create();
        var created = await CreateAdminAsync(db.Factory, "admin_catalog");
        Assert.IsTrue(created.CreatedSuccessfully);
        var recovery = new CatalogRecoveryRepository(db.Factory);

        Assert.IsFalse(await recovery.TryApproveLocalCatalogAsync(created.UserId!.Value));

        await db.ExecuteAsync(
            "INSERT INTO products(barcode, name, unitPrice, is_active) VALUES('LOCAL-1', 'Local product', 1000, 1)");

        Assert.IsTrue(await recovery.TryApproveLocalCatalogAsync(created.UserId.Value));
        Assert.AreEqual(1L, await db.ScalarAsync(
            "SELECT COUNT(*) FROM app_settings WHERE key = 'pos.catalog.sale_safe_at' AND value LIKE 'local-recovery:%'"));
        Assert.AreEqual(1L, await db.ScalarAsync(
            "SELECT COUNT(*) FROM security_events WHERE event_type = 'local_recovery_catalog_approved'"));
    }

    private static Task<FirstRunAdminCreateResult> CreateAdminAsync(
        SqliteConnectionFactory factory,
        string username)
    {
        var salt = PinHelper.GenerateSalt();
        var hash = PinHelper.HashPin("12345", salt);
        return new UserRepository(factory).TryCreateFirstRunAdminAsync(
            username,
            "Recovery Admin",
            hash,
            salt);
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var dbPath = Path.Combine(root, "pos.db");
            var options = PosDbOptions.ForPath(dbPath);
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-recovery-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public async Task ExecuteAsync(string sql)
        {
            using var conn = Factory.Open();
            using var command = conn.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql)
        {
            using var conn = Factory.Open();
            using var command = conn.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
