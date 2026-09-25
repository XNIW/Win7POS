using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SettingsAtomicTests
{
    [TestMethod]
    public async Task GroupFailureRollsBackAndConcurrentReadersSeeOneGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "Win7POS-Settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            var repository = new SettingsRepository(factory);
            await repository.SetStringsAsync(new Dictionary<string, string> { ["test.a"] = "old", ["test.b"] = "old" });
            using (var conn = factory.Open()) conn.Execute("CREATE TRIGGER settings_fail BEFORE UPDATE ON app_settings WHEN NEW.key='test.b' BEGIN SELECT RAISE(ABORT,'test'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => repository.SetStringsAsync(new Dictionary<string, string> { ["test.a"] = "new", ["test.b"] = "new" }));
            Assert.AreEqual("old", await repository.GetStringAsync("test.a"));
            using (var conn = factory.Open()) conn.Execute("DROP TRIGGER settings_fail");
            var writer = Task.Run(async () =>
            {
                for (var i = 0; i < 30; i++) await repository.SetStringsAsync(new Dictionary<string, string> { ["test.a"] = i.ToString(), ["test.b"] = i.ToString() });
            });
            for (var i = 0; i < 30; i++)
            {
                var snapshot = await repository.GetStringsAsync(new[] { "test.a", "test.b" });
                Assert.AreEqual(snapshot["test.a"], snapshot["test.b"]);
            }
            await writer;
        }
        finally { SqliteConnectionFactory.ClearAllPools(); Directory.Delete(root, true); }
    }
}
