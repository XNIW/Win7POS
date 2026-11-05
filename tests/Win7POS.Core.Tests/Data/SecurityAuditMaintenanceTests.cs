using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SecurityAuditMaintenanceTests
{
    [TestMethod]
    public async Task AuditReturnsAnAwaitableWhileMaintenanceIsHeld_ThenPersistsAfterRelease()
    {
        using var database = OwnedSqliteTestFile.Create(Path.Combine(Path.GetTempPath(), "SA." + Guid.NewGuid().ToString("N"), "pos.db"));
        DbInitializer.EnsureCreated(database.Options);
        var repository = new SecurityRepository(database.Factory);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? audit = null;
        Task? invocation = null;
        Task holder;
        using (ExecutionContext.SuppressFlow())
            holder = Task.Run(() => SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
            {
                entered.TrySetResult(true);
                await release.Task;
            }));
        var returnedBeforeRelease = false;
        var auditWasPending = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            invocation = Task.Run(() =>
            {
                audit = repository.LogEventAsync("qa.audit.maintenance", "synthetic-audit-only");
                returned.TrySetResult(true);
            });
            returnedBeforeRelease = ReferenceEquals(await Task.WhenAny(returned.Task, Task.Delay(1000)), returned.Task);
            auditWasPending = returnedBeforeRelease && audit != null && !audit.IsCompleted;
        }
        finally
        {
            release.TrySetResult(true);
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            if (invocation != null) await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            if (audit != null) await audit.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.IsTrue(returnedBeforeRelease, "Audit invocation must return without synchronously waiting for database maintenance.");
        Assert.IsTrue(auditWasPending, "Returning promptly must preserve the queued audit write.");
        using var connection = database.Factory.Open();
        Assert.AreEqual(1L, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM security_events WHERE event_type='qa.audit.maintenance' AND details='synthetic-audit-only'"));
    }
}
