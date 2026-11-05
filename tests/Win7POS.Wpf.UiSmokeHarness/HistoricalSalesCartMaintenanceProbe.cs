using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HistoricalSalesCartMaintenanceProbe
    {
        internal static async Task<bool> RunAsync(HistoricalSalesFoundationRunner.Fixture fixture, PosWorkflowService workflow)
        {
            var dispatcher = Application.Current.Dispatcher;
            dispatcher.VerifyAccess();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var pos = new PosViewModel(workflow, permissionService: new PermissionService(fixture.Operator), operatorSession: fixture.Operator);
            using var report = pos.CreateDailyReportViewModel();
            var focusRequests = 0;
            pos.FocusBarcodeRequested += () => focusRequests++;
            DispatcherTimer observer = null;
            HistoricalSalesImportDialog observedDialog = null;
            Task mutation = null;
            IDisposable gate = null;
            var entered = false;
            var blocked = false;
            var cancelled = false;
            var modalResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await workflow.AddManualPriceAsync(1234);
                await workflow.AddManualPriceAsync(1234);
                await workflow.AddManualPriceAsync(567);
                var original = await workflow.GetSnapshotAsync();
                if (original.Lines.Count != 2 || original.Total != 3035 || original.Lines.Single(x => x.UnitPrice == 1234).Quantity != 2) return false;
                var expected = Fingerprint(original);
                pos.StartInitialize();
                await UntilAsync(() => !pos.IsBusy && pos.CartItems.Count == 2, deadline.Token);
                if (!pos.AddBarcodeCommand.CanExecute(null) || !pos.PayCommand.CanExecute(null) || !pos.ClearCartCommand.CanExecute(null) ||
                    pos.CartItems.Sum(x => x.LineTotal) != 3035 || !report.ImportHistoryCommand.CanExecute(null)) return false;
                // Initialization only adds the normal synthetic demo catalog. Freeze the
                // operational baseline after it, before entering historical maintenance.
                var databaseBefore = fixture.State();
                var plan = await fixture.PlanAsync(id: "nonempty-cart-cancel");
                observer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Send, async (_, __) =>
                {
                    var dialog = Application.Current.Windows.OfType<HistoricalSalesImportDialog>().SingleOrDefault(x => x.IsVisible);
                    if (deadline.IsCancellationRequested)
                    {
                        observer.Stop();
                        modalResult.TrySetException(new TimeoutException("qa_cart_modal_readiness_timeout"));
                        dialog?.Close();
                        return;
                    }
                    if (dialog == null) return;
                    observer.Stop(); observedDialog = dialog;
                    try
                    {
                        entered = pos.IsBusy && !pos.AddBarcodeCommand.CanExecute(null) && !pos.PayCommand.CanExecute(null) && !pos.ClearCartCommand.CanExecute(null);
                        gate = await workflow.EnterHistoricalWorkflowGateForTestingAsync(deadline.Token);
                        mutation = workflow.ApplyHistoricalSalesAsync(plan, fixture.Operator, deadline.Token);
                        await UntilAsync(() => workflow.IsHistoricalMaintenanceActive || mutation.IsCompleted, deadline.Token);
                        if (!workflow.IsHistoricalMaintenanceActive || mutation.IsCompleted) throw new InvalidOperationException("qa_cart_maintenance_not_reached");
                        var attempt = workflow.AddManualPriceAsync(999);
                        blocked = attempt.IsCompleted;
                        try { await attempt; blocked = false; }
                        catch (InvalidOperationException error) { blocked &= error.Message == "historical_maintenance_active"; }
                        deadline.Cancel();
                        try { await mutation; }
                        catch (OperationCanceledException) { cancelled = true; }
                        modalResult.TrySetResult(true);
                    }
                    catch (Exception error) { modalResult.TrySetException(error); }
                    finally
                    {
                        gate?.Dispose(); gate = null;
                        dialog.Close();
                    }
                }, dispatcher);
                observer.Start();
                // Actual public command -> actual POS import action -> actual modal dialog.
                // The nested dispatcher observes the command states while this call is open.
                report.ImportHistoryCommand.Execute(null);
                await modalResult.Task;
                var after = await workflow.GetSnapshotAsync();
                return entered && blocked && cancelled && observedDialog != null && !observedDialog.IsVisible &&
                    !workflow.IsHistoricalMaintenanceActive && !pos.IsBusy && pos.AddBarcodeCommand.CanExecute(null) &&
                    pos.PayCommand.CanExecute(null) && pos.ClearCartCommand.CanExecute(null) && focusRequests > 0 &&
                    Fingerprint(after) == expected && pos.CartItems.Count == 2 && pos.CartItems.Sum(x => x.LineTotal) == 3035 &&
                    fixture.State() == databaseBefore;
            }
            finally
            {
                deadline.Cancel(); observer?.Stop(); gate?.Dispose();
                if (mutation != null) try { await mutation; } catch (OperationCanceledException) { }
                if (observedDialog?.IsVisible == true) observedDialog.Close();
                // StartInitialize started only this fixture's automation instance. Drain
                // it before disposing so the authority cannot outlive an owned reader.
                using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                using (await workflow.BackupAutomation.EnterMaintenanceAsync(cleanup.Token)) workflow.BackupAutomation.Dispose();
            }
        }

        private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
        {
            while (!condition()) { token.ThrowIfCancellationRequested(); await Dispatcher.Yield(DispatcherPriority.Background); }
        }

        private static string Fingerprint(PosWorkflowSnapshot snapshot)
            => snapshot.Total.ToString(CultureInfo.InvariantCulture) + ":" + snapshot.Subtotal.ToString(CultureInfo.InvariantCulture) + ":" +
                string.Join("|", snapshot.Lines.Select(x => string.Join(":", x.LineKey, x.Barcode, x.Name,
                    x.Quantity.ToString(CultureInfo.InvariantCulture), x.UnitPrice.ToString(CultureInfo.InvariantCulture),
                    x.LineTotal.ToString(CultureInfo.InvariantCulture), x.DiscountAmountMinor.ToString(CultureInfo.InvariantCulture))));
    }
}
