using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class ProductImageCacheScopeStoreTests
{
    [TestMethod]
    public async Task ServerScopeRotationChangesPartitionAndPersistsOnlyOpaqueHash()
    {
        using var db = TestDb.Create();
        const string staffId = "60000000-0000-4000-8000-000000000149";
        const string shopId = "10000000-0000-4000-8000-000000000149";
        var store = new ProductImageCacheScopeStore(db.Factory);

        var unbound = await store.ResolveAsync(staffId, shopId);
        var identityPurge = await store.ObserveTrustedIdentityAsync(staffId, shopId);
        var identityPurgeReplay = await store.ObserveTrustedIdentityAsync(staffId, shopId);
        Assert.AreEqual(identityPurge, identityPurgeReplay);
        Assert.IsTrue(await store.AcknowledgePurgeAsync(
            staffId,
            shopId,
            null,
            identityPurge));
        Assert.IsNull(await store.ObserveTrustedIdentityAsync(staffId, shopId));

        var firstBinding = await store.BindWithTransitionAsync(
            staffId,
            shopId,
            "server-scope-one");
        var firstBindingReplay = await store.BindWithTransitionAsync(
            staffId,
            shopId,
            "server-scope-one");
        Assert.AreEqual(firstBinding.PurgeToken, firstBindingReplay.PurgeToken);
        Assert.IsNull(await store.ResolveActiveAsync(staffId, shopId));
        Assert.IsTrue(await store.AcknowledgePurgeAsync(
            staffId,
            shopId,
            firstBinding.AccountScope,
            firstBinding.PurgeToken));
        Assert.AreEqual(
            firstBinding.AccountScope,
            await store.ResolveActiveAsync(staffId, shopId));
        var firstStable = await store.BindWithTransitionAsync(
            staffId,
            shopId,
            "server-scope-one");
        var secondBinding = await store.BindWithTransitionAsync(
            staffId,
            shopId,
            "server-scope-two");
        var secondBindingReplay = await store.BindWithTransitionAsync(
            staffId,
            shopId,
            "server-scope-two");
        Assert.AreEqual(secondBinding.PurgeToken, secondBindingReplay.PurgeToken);
        Assert.IsTrue(await store.AcknowledgePurgeAsync(
            staffId,
            shopId,
            secondBinding.AccountScope,
            secondBinding.PurgeToken));
        var stableBinding = await store.BindWithTransitionAsync(
            staffId,
            shopId,
            "server-scope-two");
        var shopIdentityPurge = await store.ObserveTrustedIdentityAsync(
            staffId,
            "20000000-0000-4000-8000-000000000149");
        Assert.IsNull(await store.ResolveActiveAsync(staffId, shopId));
        var restarted = await new ProductImageCacheScopeStore(db.Factory)
            .ResolveAsync(staffId, shopId);

        Assert.IsNull(unbound);
        Assert.IsNotNull(firstBinding.PurgeToken);
        Assert.IsNull(firstStable.PurgeToken);
        Assert.IsNotNull(secondBinding.PurgeToken);
        Assert.IsNull(stableBinding.PurgeToken);
        Assert.IsNotNull(shopIdentityPurge);
        Assert.AreNotEqual(firstBinding.AccountScope, secondBinding.AccountScope);
        Assert.AreEqual(secondBinding.AccountScope, restarted);
        using var connection = db.Factory.Open();
        var durable = string.Join("|", await connection.QueryAsync<string>(@"
SELECT key || '=' || value
FROM app_settings
WHERE key LIKE 'pos.product_image.cache_scope.v1.%';"));
        Assert.IsFalse(durable.Contains("server-scope-one", StringComparison.Ordinal));
        Assert.IsFalse(durable.Contains("server-scope-two", StringComparison.Ordinal));

        await connection.ExecuteAsync(@"
UPDATE app_settings
SET value = 'corrupt'
WHERE key LIKE 'pos.product_image.cache_scope.v1.%';");
        Assert.IsNull(await store.ResolveAsync(staffId, shopId));
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        internal SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        internal static TestDb Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "win7pos-image-cache-scope-" + Guid.NewGuid().ToString("N"));
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
