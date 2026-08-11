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
        const int waveCount = 3;
        const int reservationsPerWave = 24;
        var reservations = new List<int>();

        for (var wave = 0; wave < waveCount; wave++)
        {
            reservations.AddRange(await ReserveConcurrentWaveAsync(
                db,
                reservationsPerWave));
        }

        var totalReservations = waveCount * reservationsPerWave;

        CollectionAssert.AreEqual(
            Enumerable.Range(1, totalReservations).ToArray(),
            reservations.OrderBy(value => value).ToArray());
        Assert.AreEqual(
            totalReservations,
            await new SettingsRepository(new SqliteConnectionFactory(db.Options))
                .GetIntAsync(Key));
    }

    [TestMethod]
    public async Task ReserveMonotonicInt_CorruptStateFailure_DoesNotStrandDatabaseReservationGate()
    {
        using var db = new TestDb();
        var first = new SettingsRepository(db.Factory);
        await first.SetStringAsync(Key, "not-an-integer");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => first.ReserveMonotonicIntAsync(Key, 1));

        await first.SetIntAsync(Key, 40);
        var freshRepository = new SettingsRepository(new SqliteConnectionFactory(db.Options));
        var reservation = freshRepository.ReserveMonotonicIntAsync(Key, 1);

        Assert.AreEqual(
            41,
            await AwaitWithTimeout(
                reservation,
                "A failed monotonic reservation stranded the per-database reservation gate."));
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

    private static async Task<int[]> ReserveConcurrentWaveAsync(TestDb db, int count)
    {
        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allWorkersReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var reservations = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref readyCount) == count)
            {
                allWorkersReady.TrySetResult(true);
            }

            await start.Task;
            return await new SettingsRepository(new SqliteConnectionFactory(db.Options))
                .ReserveMonotonicIntAsync(Key, 1);
        })).ToArray();

        var allReservations = Task.WhenAll(reservations);
        try
        {
            await AwaitWithTimeout(
                allWorkersReady.Task,
                "The concurrent monotonic-reservation workers did not reach their shared start barrier.");
            start.TrySetResult(true);
            return await AwaitWithTimeout(
                allReservations,
                "Concurrent monotonic reservations did not complete.");
        }
        finally
        {
            start.TrySetResult(true);
            if (!allReservations.IsCompleted)
            {
                try
                {
                    await AwaitWithTimeout(
                        allReservations,
                        "Concurrent monotonic-reservation workers did not drain after the start barrier was released.");
                }
                catch
                {
                    // Preserve the original test failure after attempting to drain workers.
                }
            }
        }
    }

    private static async Task AwaitWithTimeout(Task task, string timeoutMessage)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.AreSame(task, completed, timeoutMessage);
        await task;
    }

    private static async Task<T> AwaitWithTimeout<T>(Task<T> task, string timeoutMessage)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.AreSame(task, completed, timeoutMessage);
        return await task;
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
