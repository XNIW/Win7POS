using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class RemoteStaffMirrorLoginTests
{
    [TestMethod]
    public async Task RemoteStaffMirror_CanBeVerifiedOfflineByShopStaffAndCredential()
    {
        using var db = TestDb.Create();
        var users = new UserRepository(db.Factory);
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("SHOP-1", "STAFF-1", "1234"));

        var username = await users.FindRemoteStaffUsernameAsync("shop-1", "staff-1");
        Assert.IsFalse(string.IsNullOrWhiteSpace(username));

        var verified = await users.VerifyPinAsync(username!, "1234");
        Assert.IsNotNull(verified.User);
        Assert.AreEqual(username, verified.User!.Username);
    }

    [TestMethod]
    public async Task RemoteStaffMirror_StaffNeverMirroredDoesNotResolveOffline()
    {
        using var db = TestDb.Create();
        var users = new UserRepository(db.Factory);
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("SHOP-1", "STAFF-1", "1234"));

        var username = await users.FindRemoteStaffUsernameAsync("SHOP-1", "STAFF-404");

        Assert.IsNull(username);
    }

    [TestMethod]
    public async Task RemoteStaffMirror_WrongCredentialFailsAndUsesLockout()
    {
        using var db = TestDb.Create();
        var users = new UserRepository(db.Factory);
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("SHOP-1", "STAFF-1", "1234"));
        var username = await users.FindRemoteStaffUsernameAsync("SHOP-1", "STAFF-1");
        Assert.IsFalse(string.IsNullOrWhiteSpace(username));

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var failed = await users.VerifyPinAsync(username!, "9999");
            Assert.IsNull(failed.User);
            if (attempt < 5)
            {
                Assert.IsFalse(failed.WasLockedOut);
            }
        }

        var locked = await users.VerifyPinAsync(username!, "1234");

        Assert.IsNull(locked.User);
        Assert.IsTrue(locked.WasLockedOut);
    }

    [TestMethod]
    [DataRow("pos_admin", "admin")]
    [DataRow("staff_admin", "admin")]
    [DataRow("shop_owner_staff", "admin")]
    [DataRow("manager", "manager")]
    [DataRow("cashier", "cashier")]
    public async Task RemoteStaffMirror_MapsRemoteRoleKeyToLocalRole(string remoteRoleKey, string expectedRoleCode)
    {
        using var db = TestDb.Create();
        var users = new UserRepository(db.Factory);
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("SHOP-1", "STAFF-" + remoteRoleKey, "1234", remoteRoleKey));

        var username = await users.FindRemoteStaffUsernameAsync("SHOP-1", "STAFF-" + remoteRoleKey);
        Assert.IsFalse(string.IsNullOrWhiteSpace(username));

        var account = await users.GetByUsernameAsync(username!);

        Assert.IsNotNull(account);
        Assert.AreEqual(expectedRoleCode, account!.RoleCode);
    }

    [TestMethod]
    public async Task RemoteStaffMirror_PosAdminGetsSensitiveAdminPermissions()
    {
        using var db = TestDb.Create();
        var users = new UserRepository(db.Factory);
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("SHOP-1", "STAFF-ADMIN", "1234", "pos_admin"));

        var username = await users.FindRemoteStaffUsernameAsync("SHOP-1", "STAFF-ADMIN");
        Assert.IsFalse(string.IsNullOrWhiteSpace(username));

        var account = await users.GetByUsernameAsync(username!);

        Assert.IsNotNull(account);
        Assert.AreEqual("admin", account!.RoleCode);
        CollectionAssert.Contains(account.PermissionCodes.ToList(), PermissionCodes.UsersManage);
        CollectionAssert.Contains(account.PermissionCodes.ToList(), PermissionCodes.RolesManage);
        CollectionAssert.Contains(account.PermissionCodes.ToList(), PermissionCodes.DbMaintenance);
    }

    [TestMethod]
    public async Task RemoteStaffMirror_ManagerDoesNotGetSensitiveAdminPermissions()
    {
        using var db = TestDb.Create();
        var users = new UserRepository(db.Factory);
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("SHOP-1", "STAFF-MANAGER", "1234", "manager"));

        var username = await users.FindRemoteStaffUsernameAsync("SHOP-1", "STAFF-MANAGER");
        Assert.IsFalse(string.IsNullOrWhiteSpace(username));

        var account = await users.GetByUsernameAsync(username!);

        Assert.IsNotNull(account);
        Assert.AreEqual("manager", account!.RoleCode);
        CollectionAssert.DoesNotContain(account.PermissionCodes.ToList(), PermissionCodes.UsersManage);
        CollectionAssert.DoesNotContain(account.PermissionCodes.ToList(), PermissionCodes.RolesManage);
        CollectionAssert.DoesNotContain(account.PermissionCodes.ToList(), PermissionCodes.DbMaintenance);
    }

    [TestMethod]
    public async Task SettingsRepository_LastShopCodeRoundTripsNormalized()
    {
        using var db = TestDb.Create();
        var settings = new SettingsRepository(db.Factory);

        await settings.SetLastPosLoginShopCodeAsync(" shop-77 ");
        var saved = await settings.GetLastPosLoginShopCodeAsync();

        Assert.AreEqual("SHOP-77", saved);
    }

    private static RemoteStaffMirrorInput BuildMirror(
        string shopCode,
        string staffCode,
        string credential,
        string remoteRoleKey = "cashier")
    {
        return new RemoteStaffMirrorInput
        {
            Credential = credential,
            CredentialVersion = 1,
            DisplayName = "Remote Staff",
            RemoteRoleKey = remoteRoleKey,
            RemoteShopId = "remote-shop-" + shopCode,
            RemoteStaffId = "remote-staff-" + staffCode,
            ShopCode = shopCode,
            StaffCode = staffCode
        };
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var dbPath = Path.Combine(root, "pos.db");
            Factory = new SqliteConnectionFactory(PosDbOptions.ForPath(dbPath));
            DbInitializer.EnsureCreated(PosDbOptions.ForPath(dbPath));
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
