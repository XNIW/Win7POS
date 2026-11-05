using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Data;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HistoricalSalesAdmissionDeadlineProbe
    {
        internal static async Task<bool> RunAsync(HistoricalSalesFoundationRunner.Fixture fixture)
        {
            var workflow = new PosWorkflowService(fixture.Options, () => DateTimeOffset.UtcNow);
            workflow.SetHistoricalAdmissionBudgetForTesting(TimeSpan.FromMilliseconds(500));
            var plan = await fixture.PlanAsync(id: "shared-admission-deadline");
            var baseline = fixture.State();
            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var workflowHolder = await workflow.EnterHistoricalWorkflowGateForTestingAsync(cleanupCancellation.Token);
            var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseMaintenance = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task maintenance = null;
            Task mutation = null;
            var clock = Stopwatch.StartNew();
            var cancelled = false; var finishedWithinResidualBudget = false;
            try
            {
                mutation = workflow.ApplyHistoricalSalesAsync(plan, fixture.Operator, cleanupCancellation.Token);
                while (!workflow.IsHistoricalMaintenanceActive && !mutation.IsCompleted)
                    await Task.Delay(1, cleanupCancellation.Token); // Explicit phase readiness, bounded by the cleanup token.
                if (mutation.IsCompleted) return false;
                using (ExecutionContext.SuppressFlow())
                    maintenance = Task.Run(() => SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
                    {
                        maintenanceEntered.SetResult(true);
                        await releaseMaintenance.Task;
                    }, cleanupCancellation.Token));
                if (!ReferenceEquals(await Task.WhenAny(maintenanceEntered.Task, maintenance), maintenanceEntered.Task)) await maintenance;
                await maintenanceEntered.Task;
                // Spend part of the SAME admission deadline at the actual workflow gate.
                await Task.Delay(TimeSpan.FromMilliseconds(300), cleanupCancellation.Token);
                workflowHolder.Dispose(); workflowHolder = null;
                // A reset at the next gate would survive this observation window.
                // The waiter is always cancelled, released and awaited below; it is never abandoned.
                finishedWithinResidualBudget = ReferenceEquals(await Task.WhenAny(mutation, Task.Delay(450)), mutation);
                if (finishedWithinResidualBudget)
                    try { await mutation; } catch (OperationCanceledException) { cancelled = true; }
            }
            finally
            {
                cleanupCancellation.Cancel();
                workflowHolder?.Dispose();
                releaseMaintenance.TrySetResult(true);
                if (maintenance != null) try { await maintenance; } catch (OperationCanceledException) { }
                if (mutation != null) try { await mutation; } catch (OperationCanceledException) { cancelled = true; }
            }
            // This is a shortened deadline test, never a relaxed production budget.
            return finishedWithinResidualBudget && cancelled && clock.Elapsed < TimeSpan.FromSeconds(2) &&
                !workflow.IsHistoricalMaintenanceActive && baseline == fixture.State();
        }
    }
}
