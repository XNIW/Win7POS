using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SettingsRepositoryMonotonicIntTests
{
    private const string Key = "test.monotonic.sequence";

    [TestMethod]
    public async Task ReserveMonotonicInt_BackwardForwardAndReopen_NeverReuseValue()
    {
        using var db = new TestDb();
        var repository = new SettingsRepository(db.Factory);

        Assert.AreEqual(7, await repository.ReserveMonotonicIntAsync(Key, 7));
        Assert.AreEqual(8, await repository.ReserveMonotonicIntAsync(Key, 3));
        Assert.AreEqual(20, await repository.ReserveMonotonicIntAsync(Key, 20));

        var reopened = new SettingsRepository(new SqliteConnectionFactory(db.Options));
        Assert.AreEqual(21, await reopened.ReserveMonotonicIntAsync(Key, 20));
        Assert.AreEqual(21, await reopened.GetIntAsync(Key));
    }

    [TestMethod]
    public async Task ReserveMonotonicInt_ConcurrentRepositories_ReserveUniqueContiguousValues()
    {
        using var db = new TestDb();
        const int count = 24;

        var reservations = await Task.WhenAll(
            Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
            {
                var factory = new SqliteConnectionFactory(db.Options);
                return await new SettingsRepository(factory)
                    .ReserveMonotonicIntAsync(Key, 1);
            })));

        CollectionAssert.AreEqual(
            Enumerable.Range(1, count).ToArray(),
            reservations.OrderBy(value => value).ToArray());
    }

    [TestMethod]
    public async Task ReserveMonotonicInt_CorruptOrExhaustedState_FailsClosed()
    {
        using var db = new TestDb();
        var repository = new SettingsRepository(db.Factory);

        await repository.SetStringAsync(Key, "not-an-integer");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.ReserveMonotonicIntAsync(Key, 1));

        await repository.SetStringAsync(Key, "-1");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.ReserveMonotonicIntAsync(Key, 1));

        await repository.SetIntAsync(Key, int.MaxValue);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.ReserveMonotonicIntAsync(Key, int.MaxValue));
    }

    private sealed class TestDb : IDisposable
    {
        private readonly string _root;

        public TestDb()
        {
            _root = Path.Combine(Path.GetTempPath(), "Win7POS-Monotonic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Options = PosDbOptions.ForPath(Path.Combine(_root, "pos.db"));
            DbInitializer.EnsureCreated(Options);
            Factory = new SqliteConnectionFactory(Options);
        }

        public PosDbOptions Options { get; }
        public SqliteConnectionFactory Factory { get; }

        public void Dispose()
        {
            SqliteConnectionFactory.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }
}
