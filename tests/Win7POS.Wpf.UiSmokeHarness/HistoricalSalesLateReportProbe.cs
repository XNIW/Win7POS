using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Win7POS.Data;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HistoricalSalesLateReportProbe
    {
        internal static async Task<bool> RunAsync(string data, HistoricalSalesQaCase test)
            => await RunCaseAsync(data, test, false) && await RunCaseAsync(data, test, true);

        private static async Task<bool> RunCaseAsync(string data, HistoricalSalesQaCase test, bool logout)
        {
            // A preceding epoch test may legitimately log its operator out.
            // Establish a new real login instead of reusing that stale session.
            using (var fixture = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
            {
                var receipt = await fixture.ApplyAsync(await fixture.PlanAsync());
                var workflow = new PosWorkflowService(fixture.Options, () => DateTimeOffset.UtcNow);
                using (var report = new DailyReportViewModel(workflow, permissions: new PermissionService(fixture.Operator), session: fixture.Operator))
                using (var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    report.DateText = "2000-01-02";
                    report.ReportOriginIndex = 2;
                    await report.ReloadAnalyticalAsync();
                    var loaded = receipt.InsertedCount == 1 && report.ReportNumbersVisible && report.NetAmount == 1000 &&
                        !report.IsBusy && !string.IsNullOrEmpty(report.StatusBadgeText) &&
                        !string.IsNullOrEmpty(report.SummaryReceiptPreview) && report.PrintSummaryCommand.CanExecute(null);
                    test.PrivateDiagnostic("late_report_initial;logout=" + logout + ";loaded=" + loaded + ";visible=" + report.ReportNumbersVisible +
                        ";net=" + report.NetAmount + ";busy=" + report.IsBusy);
                    if (!loaded) return false;

                    var beforeState = fixture.State();
                    var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task holder = null;
                    Task pending = null;
                    var sessionEvents = 0;
                    Action countEvents = () => Interlocked.Increment(ref sessionEvents);
                    fixture.Operator.SessionChanged += countEvents;
                    var genuinelyPending = false;
                    var immediate = false;
                    var failures = new List<Exception>();
                    try
                    {
                        // Suppress only this task's ambient execution context:
                        // the UI reader must not inherit the maintenance owner.
                        using (ExecutionContext.SuppressFlow())
                            holder = Task.Run(() => SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
                            {
                                entered.TrySetResult(true);
                                await release.Task.ConfigureAwait(false);
                            }, readiness.Token));
                        var readyTimeout = Task.Delay(Timeout.Infinite, readiness.Token);
                        if (!ReferenceEquals(await Task.WhenAny(entered.Task, holder, readyTimeout), entered.Task))
                            throw new TimeoutException("qa_history_late_report_maintenance_not_ready");
                        await entered.Task;

                        pending = report.ReloadAnalyticalAsync();
                        // Let the actual Task.Run report worker queue its reader
                        // while the global connection admission fence is held.
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        genuinelyPending = report.IsBusy && !pending.IsCompleted && !holder.IsCompleted;
                        var epochBefore = PosOnlineSyncRevocationLatch.ReadAuthorizationStamp().Epoch;
                        if (logout) fixture.Operator.Logout();
                        else PosOnlineSyncRevocationLatch.InvalidateAuthorizationState();
                        var epochAfter = PosOnlineSyncRevocationLatch.ReadAuthorizationStamp().Epoch;
                        var eventsBeforeRead = Volatile.Read(ref sessionEvents);
                        immediate = genuinelyPending && epochAfter > epochBefore && (logout ? eventsBeforeRead > 0 : eventsBeforeRead == 0) &&
                            !report.ReportNumbersVisible && CommandsDisabled(report) && (logout || Volatile.Read(ref sessionEvents) == 0);
                        test.PrivateDiagnostic("late_report_boundary;logout=" + logout + ";pending=" + genuinelyPending + ";epochBefore=" + epochBefore +
                            ";epochAfter=" + epochAfter + ";sessionEventsBeforeRead=" + eventsBeforeRead +
                            ";sessionEventsAfterRead=" + Volatile.Read(ref sessionEvents) + ";immediate=" + immediate);
                    }
                    catch (Exception error) { failures.Add(error); }
                    finally
                    {
                        // Release and await BOTH real tasks on every path. A
                        // legitimate later SessionChanged/logout is observed,
                        // not suppressed or required to stay absent forever.
                        release.TrySetResult(true);
                        readiness.Cancel();
                        if (holder != null) try { await holder; } catch (Exception error) { failures.Add(error); }
                        if (pending != null)
                        {
                            // The successful probe relies on epoch invalidation,
                            // never an independent cancellation that could mask
                            // a stale publication bug. Cancel only failed setup.
                            if (failures.Count != 0 && report.CancelReportCommand.CanExecute(null)) report.CancelReportCommand.Execute(null);
                            try { await pending; } catch (Exception error) { failures.Add(error); }
                        }
                        await Dispatcher.Yield(DispatcherPriority.Send);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        fixture.Operator.SessionChanged -= countEvents;
                        // Logout may have queued its ordinary audit write behind
                        // the same maintenance fence. Drain actual connections
                        // before this synthetic lifetime owner can be disposed.
                        using (var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                            await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(3), drain.Token);
                    }
                    var noOrphan = holder != null && holder.IsCompleted && pending != null && pending.IsCompleted && !report.IsBusy;
                    var cleared = FinancialValuesCleared(report);
                    var unchanged = beforeState == fixture.State();
                    test.PrivateDiagnostic("late_report_complete;logout=" + logout + ";pending=" + genuinelyPending + ";immediate=" + immediate +
                        ";noOrphan=" + noOrphan + ";cleared=" + cleared + ";unchanged=" + unchanged +
                        ";sessionEvents=" + Volatile.Read(ref sessionEvents) + ";failures=" + failures.Count);
                    if (failures.Count != 0) throw new AggregateException("qa_history_late_report_probe_failed", failures);
                    return loaded && genuinelyPending && immediate && noOrphan && cleared && unchanged;
                }
            }
        }

        private static bool CommandsDisabled(DailyReportViewModel report) =>
            !report.PrintSummaryCommand.CanExecute(null) && !report.PrintSelectedHistoryCommand.CanExecute(null) && !report.ExportCommand.CanExecute(null);

        private static bool FinancialValuesCleared(DailyReportViewModel report) =>
            !report.ReportNumbersVisible && CommandsDisabled(report) &&
            report.NetAmount == 0 && report.TotalAmount == 0 && report.GrossSalesAmount == 0 && report.RefundsAmount == 0 &&
            report.CashAmount == 0 && report.CardAmount == 0 && report.SalesCount == 0 &&
            report.PeriodNetAmount == 0 && report.PeriodGrossAmount == 0 && report.PeriodRefundsAmount == 0 &&
            report.PeriodCashAmount == 0 && report.PeriodCardAmount == 0 && report.PeriodSalesCount == 0 &&
            report.MarkedNetAmount == 0 && report.MarkedGrossAmount == 0 && report.MarkedRefundsAmount == 0 &&
            report.MarkedCashAmount == 0 && report.MarkedCardAmount == 0 && report.MarkedSalesCount == 0 && report.MarkedCount == 0 &&
            report.HistoryRows.Count == 0 && report.HistoryRowCount == 0 && report.SelectedHistoryRow == null &&
            report.HourlySalesPoints.Count == 0 && string.IsNullOrEmpty(report.SummaryReceiptPreview) && string.IsNullOrEmpty(report.SingleMarkedReceiptPreview);
    }
}
