using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core.Security;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static partial class ArticleMutationLoopbackHarness
    {
        internal static async Task<string> VerifyAuthenticatedShellUxAsync(string data)
        {
            var options = PosDbOptions.Default();
            using (var authority = PosDatabaseAuthorityLease.AcquireSyntheticNew(options))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(52)))
            using (var server = new ArticleLoopbackServer(authoritativelyEmptyCatalog: true))
            {
                var priorUrl = Environment.GetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable);
                var priorSession = OperatorSessionHolder.Current;
                var proofs = new CatalogRetryUiProof { Case = "ordinary_main_window_navigation" };
                Win7POS.Data.Backup.BackupAutomationService ownedBackup = null;
                var receipt = Path.Combine(data, "authenticated-shell-ux.private.json");
                Action save = () => WriteJson(receipt, proofs);
                Action<string, bool> check = (id, ok) => CheckCatalogRetryUi(proofs, save, id, ok);
                try
                {
                    Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, server.BaseUrl);
                    PosAdminWebOptions.SaveBaseUrl(new Uri(server.BaseUrl));
                    var factory = new SqliteConnectionFactory(options.WithAccessContext(authority.AccessContext));
                    DbInitializer.EnsureCreated(options.WithAccessContext(authority.AccessContext));
                    for (var reopen = 0; reopen < 3; reopen++)
                    {
                        server.UseCatalogSnapshot(empty: reopen != 2);
                        server.StaffRoleKey = reopen == 1 ? "pos_cashier" : "pos_admin";
                        OperatorSessionHolder.Current = null;
                        var shell = new MainWindow();
                        var mainHandle = IntPtr.Zero;
                        try
                        {
                            shell.Show();
                            await WaitCatalogRetryUiAsync(() => shell.OwnedWindows.OfType<PosOnlineFirstLoginDialog>().Any(), deadline.Token);
                            var login = shell.OwnedWindows.OfType<PosOnlineFirstLoginDialog>().Single();
                            await WaitCatalogRetryUiAsync(() => AccessDialogInitialized(login) && !AccessDialogBusy(login), deadline.Token);
                            AccessControl<TextBox>(login, "ShopCodeBox").Text = ShopCode;
                            AccessControl<TextBox>(login, "StaffCodeBox").Text = "ARTICLEPOS";
                            AccessControl<PasswordBox>(login, "CredentialBox").Password = "2468";
                            AccessClick(login, "ConnectButton");
                            await WaitCatalogRetryUiAsync(() =>
                            {
                                var preflight = shell.OwnedWindows.OfType<PosStartOfDaySyncDialog>().FirstOrDefault();
                                if (preflight?.Result?.CanEnterAuthenticatedShell == true && preflight.FindName("ContinueButton") is Button button && button.IsEnabled)
                                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                return ShellField<bool>(shell, "_shellAuthenticated") &&
                                    (reopen == 2 ? ShellField<bool>(shell, "_shellCanSell") :
                                     ((FrameworkElement)shell.FindName("EmptyCatalogPlaceholder")).IsVisible);
                            }, deadline.Token);
                            var session = (OperatorSession)OperatorSessionHolder.Current;
                            var version = session.CurrentOperatorAuthorityVersion;
                            mainHandle = new System.Windows.Interop.WindowInteropHelper(shell).Handle;
                            var process = Process.GetCurrentProcess().Id;
                            if (reopen == 2)
                            {
                                check("ready_login_no_historical_popup", shell.IsVisible && session.IsLoggedIn && !login.IsVisible &&
                                    !shell.OwnedWindows.OfType<HistoricalOnboardingDialog>().Any() && ShellField<bool>(shell, "_shellCanSell"));
                                await VerifyShellScannerAndReturnAsync(shell, check, deadline.Token, "ready_login");
                                await VerifyShellPaymentAsync(shell, factory, check, deadline.Token);
                                continue;
                            }
                            check("login_empty_shell_" + reopen, shell.IsVisible && session.IsLoggedIn && session.EnsureAuthorizationValid() &&
                                !login.IsVisible && login.DialogResult == true && !shell.OwnedWindows.OfType<HistoricalOnboardingDialog>().Any() &&
                                ShellField<PosView>(shell, "PosViewControl") == null && !ShellField<bool>(shell, "_shellCanSell"));
                            using (var c = factory.Open())
                                check("empty_navigation_no_commercial_writes_" + reopen,
                                    c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == 0 &&
                                    c.ExecuteScalar<long>("SELECT COUNT(*) FROM sale_lines") == 0 &&
                                    c.ExecuteScalar<long>("SELECT COUNT(*) FROM local_stock_movements") == 0 &&
                                    c.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox") == 0 &&
                                    c.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_import_operation") == 0);
                            if (reopen == 0) Program.HarnessWindow.SaveVisual(shell.Content as FrameworkElement,
                                Path.Combine(data, "authenticated-shell-empty-render.png"));
                            if (reopen == 0)
                                await VerifyShellCatalogTransitionAsync(shell, factory, check, deadline.Token);
                            var permissions = new PermissionService(session);
                            if (reopen == 0)
                            {
                                var settings = new Win7POS.Data.Repositories.SettingsRepository(factory);
                                await settings.SetStringAsync("pos.sales_sync.last_error", "synthetic_warning");
                                await (Task)ShellInvoke(shell, "RefreshSyncStatusStripAsync", factory);
                                check("empty_state_preserves_operational_warning", ((TextBlock)shell.FindName("SyncStatusText")).Text.Contains(PosLocalization.T("sync.lastSalesError")));
                                await settings.SetStringAsync("pos.sales_sync.last_error", string.Empty);
                                await (Task)ShellInvoke(shell, "RefreshSyncStatusStripAsync", factory);
                            }
                            var service = new PosWorkflowService(options.WithAccessContext(authority.AccessContext), () => DateTimeOffset.UtcNow);
                            using (var unloaded = DailyReportComposition.Create(service, null, permissions, session, historicalOnly: true))
                                check("unloaded_payment_totals_unavailable_" + reopen, !unloaded.ReportNumbersVisible &&
                                    new[] { unloaded.CashAmountDisplay, unloaded.CardAmountDisplay, unloaded.GrossSalesAmountDisplay,
                                        unloaded.RefundsAmountDisplay, unloaded.NetAmountDisplay }.All(x => x == PosLocalization.T("historyReport.notAvailable")));
                            if (reopen == 1)
                            {
                                using (var denied = DailyReportComposition.Create(service, null, permissions, session))
                                    check("no_history_permission_still_enters_shell", !permissions.Has(PermissionCodes.RegisterImportHistory) &&
                                        !denied.ImportHistoryCommand.CanExecute(null) && session.IsLoggedIn && shell.IsVisible);
                                continue;
                            }
                            ShellInvoke(shell, "OnMenuDailyReportClick", null, new RoutedEventArgs());
                            var reportView = (DailyReportView)shell.FindName("DailyReportViewControl");
                            await WaitCatalogRetryUiAsync(() => reportView.DataContext is DailyReportViewModel &&
                                ((TabControl)shell.FindName("MainTabControl")).SelectedIndex == 2, deadline.Token);
                            var report = (DailyReportViewModel)reportView.DataContext;
                            report.SelectedTabIndex = 1; report.HistoryFromText = "2000-01-01"; report.HistoryToText = "2000-01-02";
                            await WaitCatalogRetryUiAsync(() => !report.IsBusy, deadline.Token);
                            check("historical_origin_without_native_time_policy", report.ReportOriginIndex == 2);
                            for (var close = 0; close < 3; close++)
                            {
                                var childDone = new TaskCompletionSource<bool>();
                                _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
                                {
                                    HistoricalSalesImportDialog import = null;
                                    try
                                    {
                                        await WaitCatalogRetryUiAsync(() => shell.OwnedWindows.OfType<HistoricalSalesImportDialog>().Any(), deadline.Token);
                                        import = shell.OwnedWindows.OfType<HistoricalSalesImportDialog>().Single();
                                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                                        check("import_geometry_" + close, OperatorDialogGeometrySmoke.SurfaceFitsWorkArea(import));
                                        if (close == 0) Program.HarnessWindow.FindVisualDescendants<Button>(import).Single(b => b.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                        else if (close == 1) SystemCommands.CloseWindow(import);
                                        else
                                        {
                                            import.Activate(); import.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                                            await Dispatcher.Yield(DispatcherPriority.Input);
                                            var focused = Keyboard.FocusedElement as UIElement;
                                            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,
                                                PresentationSource.FromVisual(focused), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                                        }
                                        await WaitCatalogRetryUiAsync(() => !import.IsVisible, deadline.Token);
                                        childDone.TrySetResult(true);
                                    }
                                    catch (Exception e) { childDone.TrySetException(e); }
                                    finally { if (import?.IsVisible == true) import.Close(); }
                                }));
                                report.ImportHistoryCommand.Execute(null);
                                await childDone.Task;
                                check("child_close_preserves_main_session_filters_" + close,
                                    shell.IsVisible && shell.IsEnabled && Process.GetCurrentProcess().Id == process &&
                                    new System.Windows.Interop.WindowInteropHelper(shell).Handle == mainHandle && session.IsLoggedIn &&
                                    session.CurrentOperatorAuthorityVersion == version && ReferenceEquals(reportView.DataContext, report) &&
                                    report.HistoryFromText == "2000-01-01" && report.HistoryToText == "2000-01-02" && report.ReportOriginIndex == 2 &&
                                    !shell.OwnedWindows.OfType<HistoricalSalesImportDialog>().Any());
                            }
                            ShellInvoke(shell, "OnMenuPosClick", null, new RoutedEventArgs());
                            ShellInvoke(shell, "OnMenuDailyReportClick", null, new RoutedEventArgs());
                            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                            check("report_reopen_reuses_filters_and_model", ReferenceEquals(reportView.DataContext, report) && report.HistoryFromText == "2000-01-01");
                            ShellInvoke(shell, "OnMenuPosClick", null, new RoutedEventArgs());
                            server.UseCatalogSnapshot(empty: false);
                            var coordinator = ShellField<PosStartupCoordinator>(shell, "_startupCoordinator");
                            ownedBackup = ShellField<PosWorkflowService>(shell, "_reportService")?.BackupAutomation ?? ownedBackup;
                            await coordinator.Host.TriggerAsync(Win7POS.Core.Online.OnlineSyncLane.CatalogDelta,
                                Win7POS.Core.Online.OnlineSyncLaneTrigger.Manual, deadline.Token);
                            // ShowDialog enters its nested Dispatcher frame before the
                            // async method returns a Task. Observe it before invoking.
                            var transitionFinished = false;
                            var observer = new TaskCompletionSource<bool>();
                            _ = shell.Dispatcher.BeginInvoke(new Action(async () =>
                            {
                                try
                                {
                                    while (!transitionFinished)
                                    {
                                        deadline.Token.ThrowIfCancellationRequested();
                                        var preflight = shell.OwnedWindows.OfType<PosStartOfDaySyncDialog>().FirstOrDefault();
                                        if (preflight?.Result?.CanEnterAuthenticatedShell == true && preflight.FindName("ContinueButton") is Button b && b.IsEnabled)
                                            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                        await Task.Delay(10, deadline.Token);
                                    }
                                    observer.TrySetResult(true);
                                }
                                catch (Exception error)
                                {
                                    foreach (Window child in shell.OwnedWindows.Cast<Window>().ToArray()) child.Close();
                                    observer.TrySetException(error);
                                }
                            }));
                            try { await (Task)ShellInvoke(shell, "RefreshShellReadinessAsync", true); }
                            finally { transitionFinished = true; }
                            await observer.Task;
                            check("full_authoritative_catalog_enables_commercial_view", ShellField<bool>(shell, "_shellCanSell") &&
                                ShellField<PosView>(shell, "PosViewControl") != null && session.IsLoggedIn && session.CurrentOperatorAuthorityVersion == version);
                            await VerifyShellScannerAndReturnAsync(shell, check, deadline.Token, "transition");
                        }
                        finally
                        {
                            var view = ShellField<PosView>(shell, "PosViewControl");
                            var model = view?.DataContext as PosViewModel;
                            var coordinator = ShellField<PosStartupCoordinator>(shell, "_startupCoordinator");
                            if (coordinator != null)
                            {
                                var stop = coordinator.StopAsync();
                                await WaitCatalogRetryUiAsync(() => stop.IsCompleted, deadline.Token); await stop;
                            }
                            if (model != null)
                            {
                                var initialization = model.InitializationTask;
                                await WaitCatalogRetryUiAsync(() => initialization.IsCompleted, deadline.Token); await initialization;
                            }
                            shell.CloseWithoutUserPrompt(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        }
                    }
                    if (ownedBackup != null)
                    {
                        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        using (await ownedBackup.EnterMaintenanceAsync(cleanup.Token)) ownedBackup.Dispose();
                        ownedBackup = null;
                    }
                    await VerifyCatalogRetryUiCaseAsync(factory, "normal_late_operator", proofs, save, deadline.Token);
                    check("stale_login_completion_never_logs_out_newer_operator", true);
                    using (var maintenance = await HistoricalSalesFoundationRunner.Fixture.CreateAsync(data))
                    {
                        var workflow = new PosWorkflowService(maintenance.Options, () => DateTimeOffset.UtcNow);
                        check("import_blocks_commands_then_restores_cart", await HistoricalSalesCartMaintenanceProbe.RunAsync(maintenance, workflow));
                    }
                    proofs.Status = "PASS"; save(); return "PASS: authenticated_shell_ux";
                }
                catch (Exception error)
                {
                    proofs.Status = "FAIL"; proofs.Failure = error.ToString(); save(); return "FAIL: authenticated_shell_ux; " + error.GetType().Name;
                }
                finally
                {
                    try
                    {
                        if (ownedBackup != null)
                        {
                            using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                            using (await ownedBackup.EnterMaintenanceAsync(cleanup.Token)) ownedBackup.Dispose();
                        }
                    }
                    catch (Exception error)
                    {
                        proofs.CleanupFailure = error.GetType().Name; proofs.Status = "FAIL"; save(); throw;
                    }
                    finally
                    {
                        new PosTrustedDeviceStore().Clear(); OperatorSessionHolder.Current = priorSession;
                        Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, priorUrl);
                    }
                }
            }
        }
        private static async Task VerifyShellScannerAndReturnAsync(MainWindow shell, Action<string, bool> check, CancellationToken token, string phase)
        {
            var view = ShellField<PosView>(shell, "PosViewControl");
            var vm = (PosViewModel)view.DataContext;
            await WaitCatalogRetryUiAsync(() => !vm.IsBusy, token);
            var box = (TextBox)view.FindName("BarcodeBox");
            view.RestoreScannerFocus(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            box.Text = "LOOP-REMOTE";
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); box.Focus();
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box),
                Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await WaitCatalogRetryUiAsync(() => vm.ItemsCount > 0 && !vm.IsBusy, token);
            var quantity = vm.ItemsCount; var total = vm.Total;
            var originalInitialization = vm.InitializationTask;
            check("scanner_adds_fixture_to_cart_" + phase, quantity > 0 && total > 0);
            ShellInvoke(shell, "OnMenuDailyReportClick", null, new RoutedEventArgs());
            var reportView = (DailyReportView)shell.FindName("DailyReportViewControl");
            await WaitCatalogRetryUiAsync(() => reportView.DataContext is DailyReportViewModel &&
                !((DailyReportViewModel)reportView.DataContext).IsBusy, token);
            reportView.Focus();
            shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(shell), Environment.TickCount,
                Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            check("report_escape_preserves_cart_and_scanner_focus_" + phase,
                ((TabControl)shell.FindName("MainTabControl")).SelectedIndex == 0 &&
                ReferenceEquals(view.DataContext, vm) && vm.ItemsCount == quantity && vm.Total == total && box.IsKeyboardFocused &&
                ReferenceEquals(originalInitialization, vm.InitializationTask));
        }
        private static async Task VerifyShellPaymentAsync(MainWindow shell, SqliteConnectionFactory factory,
            Action<string, bool> check, CancellationToken token)
        {
            var pos = (PosViewModel)ShellField<PosView>(shell, "PosViewControl").DataContext;
            var workflow = ShellField<PosWorkflowService>(shell, "_reportService");
            var total = pos.Total; var quantity = pos.ItemsCount;
            decimal stock;
            using (var connection = factory.Open())
            {
                check("ready_payment_starts_without_native_sale", connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == 0);
                stock = connection.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='LOOP-REMOTE'");
            }
            var fiscalBefore = await workflow.GetFiscalBoletaNumberAsync();
            check("ready_cart_allows_payment_command", total > 0 && quantity > 0 && pos.PayCommand.CanExecute(null));
            pos.PayCommand.Execute(null);
            var paymentView = (PaymentView)shell.FindName("PaymentViewControl");
            await WaitCatalogRetryUiAsync(() => paymentView.IsVisible && paymentView.DataContext is PaymentViewModel &&
                ((TabControl)shell.FindName("MainTabControl")).SelectedIndex == 3, token);
            var payment = (PaymentViewModel)paymentView.DataContext;
            // This synthetic sale has no printer, drawer or fiscal side effect.
            payment.ShouldPrint = false; payment.AutoPrintFiscalBoleta = false; payment.OpenDrawerForCurrentPayment = false;
            payment.SetExactTotalCommand.Execute(null);
            check("ready_payment_uses_real_main_surface", payment.IsValid && payment.ConfirmCommand.CanExecute(null) &&
                payment.CashAmountMinor == total && payment.CardAmountMinor == 0 && shell.IsVisible);
            var saleCode = payment.SaleCode;
            payment.ConfirmCommand.Execute(null);
            await WaitCatalogRetryUiAsync(() => paymentView.DataContext == null && pos.ItemsCount == 0 &&
                !pos.IsPaymentCommitInProgress && !string.IsNullOrWhiteSpace(pos.ReceiptPreview), token);
            var receipt = await workflow.GetLastReceiptPreviewAsync(pos.UseReceipt42);
            using (var connection = factory.Open())
                check("ready_main_sale_receipt_and_inventory_commit_once",
                    connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == 1 &&
                    connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox") == 1 &&
                    connection.ExecuteScalar<long>("SELECT SUM(lineTotal) FROM sale_lines") == total &&
                    connection.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='LOOP-REMOTE'") == stock - quantity &&
                    receipt == pos.ReceiptPreview && receipt.Contains(saleCode));
            check("ready_payment_returns_to_live_cash_without_fiscal_reservation", shell.IsVisible &&
                ((TabControl)shell.FindName("MainTabControl")).SelectedIndex == 0 &&
                await workflow.GetFiscalBoletaNumberAsync() == fiscalBefore);
        }

        private static async Task VerifyShellCatalogTransitionAsync(MainWindow shell, SqliteConnectionFactory factory,
            Action<string, bool> check, CancellationToken cancellationToken)
        {
            var coordinator = ShellField<PosStartupCoordinator>(shell, "_startupCoordinator");
            var state = new CatalogShopStateRepository(factory);
            var generation = coordinator.Host.CurrentGeneration;
            var refreshFlag = typeof(MainWindow).GetField("_shellReadinessRefreshing", BindingFlags.Instance | BindingFlags.NonPublic);
            var priorRefresh = (bool)refreshFlag.GetValue(shell);
            // The fixture controls intermediate metadata; the ordinary UI timer must not consume it.
            refreshFlag.SetValue(shell, true);
            try
            {
                using var c = factory.Open();
                var proof = c.Query<ShellCatalogProofSetting>("SELECT key,value FROM app_settings WHERE key LIKE @prefix",
                    new { prefix = CatalogShopStateRepository.AuthoritativeEmptyPrefix + "%" })
                    .ToDictionary(row => row.Key, row => row.Value);
                Action restoreProof = () =>
                {
                    using var tx = c.BeginTransaction();
                    c.Execute("DELETE FROM app_settings WHERE key LIKE @prefix",
                        new { prefix = CatalogShopStateRepository.AuthoritativeEmptyPrefix + "%" }, tx);
                    foreach (var item in proof)
                        c.Execute("INSERT INTO app_settings(key,value) VALUES(@key,@value)",
                            new { key = item.Key, value = item.Value }, tx);
                    tx.Commit();
                };
                Task<PosShellAdmission> read;
                using (await new CatalogShopTransitionBarrier(factory).EnterAsync(cancellationToken))
                {
                    await state.ClearAuthoritativeEmptyAsync(generation);
                    check("refresh_without_proof_never_authorizes_sales",
                        !await state.IsAuthoritativelyEmptyAsync(generation) && !await state.IsSaleSafeForOfficialShopAsync());
                    read = coordinator.ReadShellAdmissionAsync(cancellationToken);
                    check("shell_admission_waits_for_atomic_catalog_transition",
                        await Task.WhenAny(read, Task.Delay(60, cancellationToken)) != read);
                    restoreProof(); // Synthetic successful publication within the same transition.
                }
                check("shell_admission_observes_completed_empty_refresh",
                    (await read).Readiness == PosShellReadiness.AuthenticatedEmpty);
                try
                {
                    using (await new CatalogShopTransitionBarrier(factory).EnterAsync(cancellationToken))
                    {
                        await state.ClearAuthoritativeEmptyAsync(generation);
                        read = coordinator.ReadShellAdmissionAsync(cancellationToken);
                    }
                    check("failed_refresh_with_no_new_proof_remains_recovery",
                        (await read).Readiness == PosShellReadiness.Recovery);
                }
                finally
                {
                    using (await new CatalogShopTransitionBarrier(factory).EnterAsync(cancellationToken))
                        restoreProof();
                }
                using (await new CatalogShopTransitionBarrier(factory).EnterAsync(cancellationToken))
                using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    read = coordinator.ReadShellAdmissionAsync(cancel.Token);
                    cancel.Cancel();
                    var cancelled = false;
                    try { await read; } catch (OperationCanceledException) { cancelled = true; }
                    check("shell_admission_wait_is_cancellable", cancelled);
                }
                check("completed_refresh_keeps_shell_without_sale_authority",
                    shell.IsVisible && !coordinator.IsRecoveryMode &&
                    !ShellField<bool>(shell, "_shellCanSell") && !await state.IsSaleSafeForOfficialShopAsync());
            }
            finally { refreshFlag.SetValue(shell, priorRefresh); }
        }

        private sealed class ShellCatalogProofSetting
        {
            public string Key { get; set; }
            public string Value { get; set; }
        }
        private static T ShellField<T>(MainWindow shell, string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
        private static object ShellInvoke(MainWindow shell, string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(shell, args);
    }
}
