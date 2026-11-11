using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Win7POS.Wpf.Products;
using Win7POS.Wpf.Import;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core;
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
        private delegate void CatalogRetryUiCheck(string id, bool ok,
            [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0);

        internal static async Task<string> VerifyAuthenticatedShellUxAsync(string data)
        {
            TypedQaDiagnosticWriter.BeginScenario("AUTHENTICATED_SHELL_UX", "functional-completion");
            var options = PosDbOptions.Default();
            using (var authority = PosDatabaseAuthorityLease.AcquireSyntheticNew(options))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(52)))
            using (var server = new ArticleLoopbackServer(holdFirstCatalogResponse: true, authoritativelyEmptyCatalog: true))
            {
                var priorUrl = Environment.GetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable);
                var priorSession = OperatorSessionHolder.Current;
                var proofs = new CatalogRetryUiProof { Case = "ordinary_main_window_navigation" };
                Win7POS.Data.Backup.BackupAutomationService ownedBackup = null;
                var receipt = Path.Combine(data, "authenticated-shell-ux.private.json");
                Action save = () => WriteJson(receipt, proofs);
                CatalogRetryUiCheck check = (id, ok, sourceFile, sourceLine) =>
                    CheckCatalogRetryUi(proofs, save, id, ok, sourceFile, sourceLine);
                try
                {
                    Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, server.BaseUrl);
                    PosAdminWebOptions.SaveBaseUrl(new Uri(server.BaseUrl));
                    var factory = new SqliteConnectionFactory(options.WithAccessContext(authority.AccessContext));
                    DbInitializer.EnsureCreated(options.WithAccessContext(authority.AccessContext));
                    for (var reopen = 0; reopen < 4; reopen++)
                    {
                        server.UseCatalogSnapshot(empty: reopen < 2);
                        server.StaffRoleKey = reopen == 1 ? "pos_cashier" : "pos_admin";
                        OperatorSessionHolder.Current = null;
                        var commercialBefore=ReadShellCommercialCounts(factory);
                        var clock = new ShellSyncClock();
                        var shell = new MainWindow(clock);
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
                            await WaitCatalogRetryUiAsync(() => !login.IsVisible, deadline.Token);
                            var admissionTimer = Stopwatch.StartNew();
                            await WaitCatalogRetryUiAsync(() => ShellField<bool>(shell, "_shellAuthenticated") &&
                                ShellField<PosView>(shell,"PosViewControl")?.DataContext is PosViewModel model &&
                                model.IsInitialized && !model.IsBusy, deadline.Token);
                            var admittedView=ShellField<PosView>(shell,"PosViewControl");
                            var admittedModel=(PosViewModel)admittedView.DataContext;
                            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                            check("initial_barcode_focus_"+reopen,((TextBox)admittedView.FindName("BarcodeBox")).IsKeyboardFocused);
                            check("login_real_workstation_"+reopen, admittedModel.AddBarcodeCommand.CanExecute(null) &&
                                !shell.OwnedWindows.OfType<PosStartOfDaySyncDialog>().Any());
                            WriteJson(Path.Combine(data,"shell-login-latency-"+reopen+".private.json"),
                                new ShellLatencyProof { AuthClosedToInteractiveMilliseconds=admissionTimer.Elapsed.TotalMilliseconds });
                            if(reopen==0)
                            {
                                await WaitCatalogRetryUiAsync(()=>server.FirstCatalogRequestBlocked.IsCompleted,deadline.Token);
                                check("cart_interactive_before_first_catalog_response",!login.IsVisible && !admittedModel.IsBusy &&
                                    !ShellField<bool>(shell,"_shellCanSell") && admittedModel.AddBarcodeCommand.CanExecute(null));
                                await VerifyHeldCatalogInputAsync(shell,check,deadline.Token,data);
                                server.ReleaseCatalogResponse();
                            }
                            var activeCoordinator=ShellField<PosStartupCoordinator>(shell,"_startupCoordinator");
                            await WaitCatalogRetryUiAsync(()=>activeCoordinator.GetSnapshot()?.Lanes.Any(x=>x.Lane==OnlineSyncLane.CatalogDelta &&
                                !x.InFlight && x.LastOutcome!=null && (reopen>=2 ? x.LastOutcome.CatalogSaleSafe : x.LastOutcome.Code=="catalog_authoritatively_empty"))==true &&
                                ShellField<bool>(shell,"_shellCanSell")== (reopen>=2),deadline.Token);
                            var session = (OperatorSession)OperatorSessionHolder.Current;
                            var version = session.CurrentOperatorAuthorityVersion;
                            mainHandle = new System.Windows.Interop.WindowInteropHelper(shell).Handle;
                            var process = Process.GetCurrentProcess().Id;
                            if (reopen >= 2)
                            {
                                check("ready_login_no_historical_popup", shell.IsVisible && session.IsLoggedIn && !login.IsVisible &&
                                    !shell.OwnedWindows.OfType<HistoricalOnboardingDialog>().Any() && ShellField<bool>(shell, "_shellCanSell"));
                                await VerifyShellScannerAndReturnAsync(shell, check, deadline.Token, "ready_login");
                                await VerifyShellPaymentAsync(shell, factory, check, deadline.Token);
                                if(reopen==2) await VerifyPublishedSnapshotContinuityAsync(shell,factory,server,clock,check,deadline.Token,data);
                                else check("restart_after_cancelled_catalog_preserves_committed_data",commercialBefore[0]>=2 && ReadShellCommercialCounts(factory)[0]==commercialBefore[0]+1);
                                continue;
                            }
                            check("login_empty_shell_" + reopen, shell.IsVisible && session.IsLoggedIn && session.EnsureAuthorizationValid() &&
                                !login.IsVisible && login.DialogResult == true && !shell.OwnedWindows.OfType<HistoricalOnboardingDialog>().Any() &&
                                ReferenceEquals(ShellField<PosView>(shell, "PosViewControl"),admittedView) && !ShellField<bool>(shell, "_shellCanSell"));
                            check("empty_navigation_no_commercial_writes_"+reopen,
                                commercialBefore.SequenceEqual(ReadShellCommercialCounts(factory)));
                            if (reopen == 0) Program.HarnessWindow.SaveVisual(shell.Content as FrameworkElement,
                                Path.Combine(data, "authenticated-shell-empty-render.png"));

                            var permissions = new PermissionService(session);
                            if (reopen == 0)
                            {
                                await WaitCatalogRetryUiAsync(()=>activeCoordinator.GetSnapshot().Lanes.All(x=>!x.InFlight),deadline.Token);
                                await ShellField<Task>(shell,"_catalogPublicationTask");
                                await ShellField<Task>(shell,"_syncStatusTask");
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
                                await VerifyShellPermissionDeniedAsync(shell,factory,check,deadline.Token);
                                continue;
                            }
                            await ClickShellMenuAsync(shell,"DailyReport",deadline.Token);
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
                            await ClickShellMenuAsync(shell,"Pos",deadline.Token);
                            await ClickShellMenuAsync(shell,"DailyReport",deadline.Token);
                            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                            check("report_reopen_reuses_filters_and_model", ReferenceEquals(reportView.DataContext, report) && report.HistoryFromText == "2000-01-01");
                            await ClickShellMenuAsync(shell,"Pos",deadline.Token);
                            await WaitCatalogRetryUiAsync(()=>activeCoordinator.GetSnapshot()?.Lanes.Any(x=>x.Lane==OnlineSyncLane.Heartbeat &&
                                !x.InFlight && x.NextDueAt.HasValue)==true,deadline.Token);
                            await VerifyShellUnknownEditorAsync(shell,true,check,deadline.Token);
                            server.HoldNextCatalogResponse();
                            await VerifyShellUnknownEditorAsync(shell,false,check,deadline.Token);
                            await WaitCatalogRetryUiAsync(()=>server.NextCatalogRequestBlocked.IsCompleted,deadline.Token);
                            check("local_ack_wakes_catalog_without_manual_trigger",server.ArticleRequestCount>0 &&
                                !await new CatalogShopStateRepository(factory).IsSaleSafeForOfficialShopAsync());
                            SubmitShellBarcode(admittedView,"2000");
                            await WaitCatalogRetryUiAsync(()=>!admittedModel.IsBusy && admittedModel.AddBarcodeCommand.CanExecute(null),deadline.Token);
                            await VerifyShellPaymentAsync(shell,factory,check,deadline.Token,"FIRST-LOCAL","empty_local");
                            check("periodic_refresh_keeps_same_workstation",ReferenceEquals(admittedView,ShellField<PosView>(shell,"PosViewControl")) &&
                                !admittedModel.IsBusy && !activeCoordinator.IsRecoveryMode);
                            server.UseCatalogSnapshot(empty:false);
                            server.ReleaseNextCatalogResponse();
                            await WaitCatalogRetryUiAsync(()=>ShellField<bool>(shell,"_shellCanSell"),deadline.Token);
                            check("full_authoritative_catalog_enables_commercial_view",ReferenceEquals(admittedView,ShellField<PosView>(shell,"PosViewControl")) &&
                                session.IsLoggedIn && session.CurrentOperatorAuthorityVersion==version);
                            ownedBackup = ShellField<PosWorkflowService>(shell,"_reportService")?.BackupAutomation ?? ownedBackup;
                            await VerifyShellScannerAndReturnAsync(shell, check, deadline.Token, "transition");
                        }
                        finally
                        {
                            using var cleanupDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            var view = ShellField<PosView>(shell, "PosViewControl");
                            var model = view?.DataContext as PosViewModel;
                            var coordinator = ShellField<PosStartupCoordinator>(shell, "_startupCoordinator");
                            if (coordinator != null)
                            {
                                var stop = coordinator.StopAsync();
                                await WaitCatalogRetryUiAsync(() => stop.IsCompleted, cleanupDeadline.Token); await stop;
                            }
                            if (model != null)
                            {
                                var initialization = model.InitializationTask;
                                await WaitCatalogRetryUiAsync(() => initialization.IsCompleted, cleanupDeadline.Token); await initialization;
                            }
                            shell.CloseWithoutUserPrompt();
                            await WaitCatalogRetryUiAsync(()=>!shell.IsVisible,cleanupDeadline.Token);
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
                        var workflow = maintenance.CreateWorkflow();
                        check("import_blocks_commands_then_restores_cart", await HistoricalSalesCartMaintenanceProbe.RunAsync(maintenance, workflow));
                    }
                    proofs.Status = "PASS"; save(); TypedQaDiagnosticWriter.CompleteScenario(true); return "PASS: authenticated_shell_ux";
                }
                catch (Exception error)
                {
                    TypedQaDiagnosticWriter.RecordException(error);
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
        private static async Task VerifyShellScannerAndReturnAsync(MainWindow shell, CatalogRetryUiCheck check, CancellationToken token, string phase)
        {
            var view = ShellField<PosView>(shell, "PosViewControl");
            var vm = (PosViewModel)view.DataContext;
            await WaitCatalogRetryUiAsync(() => !vm.IsBusy, token);
            var box = (TextBox)view.FindName("BarcodeBox");
            view.RestoreScannerFocus(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var beforeCount=vm.ItemsCount;
            box.Text = "LOOP-REMOTE";
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); box.Focus();
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box),
                Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await WaitCatalogRetryUiAsync(() => vm.ItemsCount == beforeCount+1 && !vm.IsBusy, token);
            var quantity = vm.ItemsCount; var total = vm.Total;
            var originalInitialization = vm.InitializationTask;
            check("scanner_adds_fixture_to_cart_" + phase, quantity > 0 && total > 0);
            await ClickShellMenuAsync(shell,"DailyReport",token);
            var reportView = (DailyReportView)shell.FindName("DailyReportViewControl");
            await WaitCatalogRetryUiAsync(() => reportView.DataContext is DailyReportViewModel &&
                !((DailyReportViewModel)reportView.DataContext).IsBusy, token);
            reportView.Focus();
            var overlayAtEscape=((FrameworkElement)shell.FindName("SideMenuOverlay")).Visibility;
            var reportBusyAtEscape=((DailyReportViewModel)reportView.DataContext).IsBusy;
            shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(shell), Environment.TickCount,
                Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            // Bounded scalar state at the failing return checkpoint; no barcode
            // text or retained control references in the evidence.
            var selected=((TabControl)shell.FindName("MainTabControl")).SelectedIndex;
            var sameModel=ReferenceEquals(view.DataContext,vm);var sameCart=vm.ItemsCount==quantity&&vm.Total==total;
            var focused=box.IsKeyboardFocused;var sameInitialization=ReferenceEquals(originalInitialization,vm.InitializationTask);
            var pending=(DispatcherOperation)typeof(PosView).GetField("_focusOperation",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(view);
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory,"shell-return-focus.private.txt"),
                "phase="+phase+"; selected="+selected+"; same_model="+sameModel+"; same_cart="+sameCart+"; focused="+focused+
                "; same_initialization="+sameInitialization+"; view_loaded="+view.IsLoaded+"; view_visible="+view.IsVisible+
                "; view_enabled="+view.IsEnabled+"; box_visible="+box.IsVisible+"; window_active="+shell.IsActive+
                "; focus_operation="+(pending?.Status.ToString()??"none")+"; keyboard_target="+(Keyboard.FocusedElement?.GetType().Name??"none")+
                "; overlay_at_escape="+overlayAtEscape+"; report_busy_at_escape="+reportBusyAtEscape+Environment.NewLine);
            check("report_escape_preserves_cart_and_scanner_focus_" + phase,
                selected==0&&sameModel&&sameCart&&focused&&sameInitialization);
        }
        private static async Task VerifyShellPaymentAsync(MainWindow shell, SqliteConnectionFactory factory,
            CatalogRetryUiCheck check, CancellationToken token, string stockBarcode="LOOP-REMOTE", string phase="ready")
        {
            var pos = (PosViewModel)ShellField<PosView>(shell, "PosViewControl").DataContext;
            var workflow = ShellField<PosWorkflowService>(shell, "_reportService");
            var total = pos.Total; var quantity = pos.ItemsCount;
            decimal stock;
            var before=ReadShellCommercialCounts(factory);
            var stockQuantity=pos.CartItems.Where(x=>x.Barcode==stockBarcode).Sum(x=>x.Quantity);
            using (var connection = factory.Open())
            {
                check("ready_payment_starts_without_native_sale_"+phase, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == before[0]);
                stock = connection.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode=@barcode",new{barcode=stockBarcode});
            }
            var fiscalBefore = await workflow.GetFiscalBoletaNumberAsync();
            check("ready_cart_allows_payment_command_"+phase, total > 0 && quantity > 0 && pos.PayCommand.CanExecute(null));
            pos.PayCommand.Execute(null);
            var paymentView = (PaymentView)shell.FindName("PaymentViewControl");
            await WaitCatalogRetryUiAsync(() => paymentView.IsVisible && paymentView.DataContext is PaymentViewModel &&
                ((TabControl)shell.FindName("MainTabControl")).SelectedIndex == 3, token);
            var payment = (PaymentViewModel)paymentView.DataContext;
            // This synthetic sale has no printer, drawer or fiscal side effect.
            payment.ShouldPrint = false; payment.AutoPrintFiscalBoleta = false; payment.OpenDrawerForCurrentPayment = false;
            payment.SetExactTotalCommand.Execute(null);
            check("ready_payment_uses_real_main_surface_"+phase, payment.IsValid && payment.ConfirmCommand.CanExecute(null) &&
                payment.CashAmountMinor == total && payment.CardAmountMinor == 0 && shell.IsVisible);
            var saleCode = payment.SaleCode;
            payment.ConfirmCommand.Execute(null);
            await WaitCatalogRetryUiAsync(() => paymentView.DataContext == null && pos.ItemsCount == 0 &&
                !pos.IsPaymentCommitInProgress && !string.IsNullOrWhiteSpace(pos.ReceiptPreview), token);
            var receipt = await workflow.GetLastReceiptPreviewAsync(pos.UseReceipt42);
            using (var connection = factory.Open())
                check("ready_main_sale_receipt_and_inventory_commit_once_"+phase,
                    connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == before[0]+1 &&
                    connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox") == before[3]+1 &&
                    connection.ExecuteScalar<long>("SELECT SUM(lineTotal) FROM sale_lines WHERE saleId=(SELECT id FROM sales WHERE code=@code)",new{code=saleCode}) == total &&
                    connection.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode=@barcode",new{barcode=stockBarcode}) == stock - stockQuantity &&
                    receipt == pos.ReceiptPreview && receipt.Contains(saleCode));
            check("ready_payment_returns_to_live_cash_without_fiscal_reservation_"+phase, shell.IsVisible &&
                ((TabControl)shell.FindName("MainTabControl")).SelectedIndex == 0 &&
                await workflow.GetFiscalBoletaNumberAsync() == fiscalBefore);
        }

        private static long[] ReadShellCommercialCounts(SqliteConnectionFactory factory)
        {
            using var c=factory.Open();
            return new[]{"sales","sale_lines","local_stock_movements","sales_sync_outbox","historical_import_operation"}
                .Select(table=>c.ExecuteScalar<long>("SELECT COUNT(*) FROM "+table)).ToArray();
        }

        private static async Task VerifyShellUnknownEditorAsync(MainWindow shell,bool cancel,CatalogRetryUiCheck check,CancellationToken token)
        {
            var view=ShellField<PosView>(shell,"PosViewControl");var model=(PosViewModel)view.DataContext;
            var before=model.ItemsCount;
            var observed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _=shell.Dispatcher.BeginInvoke(new Action(async()=>
            {
                try
                {
                    await WaitCatalogRetryUiAsync(()=>shell.OwnedWindows.OfType<ProductEditDialog>().Any(),token);
                    var dialog=shell.OwnedWindows.OfType<ProductEditDialog>().Single();
                    if(cancel) dialog.ViewModel.CancelCommand.Execute(null);
                    else
                    {
                        dialog.ViewModel.ProductName="Synthetic first local";
                        dialog.ViewModel.PriceText="1500";
                        dialog.ViewModel.PurchasePriceText="500";
                        dialog.ViewModel.StockText="5";
                        if(!dialog.ViewModel.ConfirmCommand.CanExecute(null))throw new InvalidOperationException("local_editor_not_valid");
                        dialog.ViewModel.ConfirmCommand.Execute(null);
                    }
                    await WaitCatalogRetryUiAsync(()=>!dialog.IsVisible,token);observed.TrySetResult(true);
                }
                catch(Exception error){observed.TrySetException(error);}
            }));
            SubmitShellBarcode(view,cancel?"UNKNOWN-CANCEL":"FIRST-LOCAL");await observed.Task;
            await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.AddBarcodeCommand.CanExecute(null),token);
            check(cancel?"unknown_create_cancel_restores_input":"first_local_created_through_scanner_editor",
                model.ItemsCount==before+(cancel?0:1) && !shell.OwnedWindows.OfType<ProductEditDialog>().Any());
        }

        private static async Task VerifyShellPermissionDeniedAsync(MainWindow shell,SqliteConnectionFactory factory,CatalogRetryUiCheck check,CancellationToken token)
        {
            var view=ShellField<PosView>(shell,"PosViewControl");var model=(PosViewModel)view.DataContext;
            var before=ReadShellCommercialCounts(factory);var count=model.ItemsCount;
            var observed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _=shell.Dispatcher.BeginInvoke(new Action(async()=>
            {
                try
                {
                    await WaitCatalogRetryUiAsync(()=>shell.OwnedWindows.OfType<ApplyConfirmDialog>().Any(),token);
                    var prompt=shell.OwnedWindows.OfType<ApplyConfirmDialog>().Single();
                    Program.HarnessWindow.FindVisualDescendants<Button>(prompt).Single(x=>x.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    observed.TrySetResult(true);
                }catch(Exception error){observed.TrySetException(error);}
            }));
            SubmitShellBarcode(view,"UNKNOWN-DENIED");await observed.Task;
            await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.AddBarcodeCommand.CanExecute(null),token);
            check("catalog_permission_denied_override_declined_keeps_scanner",model.ItemsCount==count &&
                before.SequenceEqual(ReadShellCommercialCounts(factory)) && !shell.OwnedWindows.OfType<ProductEditDialog>().Any());
        }

        private static async Task PulseShellCatalogAsync(MainWindow shell,ShellSyncClock clock,CancellationToken token)
        {
            var coordinator=ShellField<PosStartupCoordinator>(shell,"_startupCoordinator");
            DateTimeOffset? next=null;
            await WaitCatalogRetryUiAsync(()=>
            {
                next=coordinator.GetSnapshot()?.Lanes.Where(x=>!x.InFlight && x.NextDueAt.HasValue &&
                    (x.Lane==OnlineSyncLane.Heartbeat || x.Lane==OnlineSyncLane.CatalogDelta)).Select(x=>x.NextDueAt).OrderBy(x=>x).FirstOrDefault();
                return next.HasValue && clock.HasScheduledAt(next.Value);
            },token);
            clock.AdvanceTo(next.Value);
        }

        private static async Task VerifyPublishedSnapshotContinuityAsync(MainWindow shell,SqliteConnectionFactory factory,
            ArticleLoopbackServer server,ShellSyncClock clock,CatalogRetryUiCheck check,CancellationToken token,string data)
        {
            var view=ShellField<PosView>(shell,"PosViewControl");var model=(PosViewModel)view.DataContext;
            var box=(TextBox)view.FindName("BarcodeBox");
            var coordinator=ShellField<PosStartupCoordinator>(shell,"_startupCoordinator");
            await VerifyExclusiveCatalogApplyInputAsync(shell,factory,check,token,data);
            server.HoldNextCatalogResponse();await PulseShellCatalogAsync(shell,clock,token);
            await WaitCatalogRetryUiAsync(()=>server.NextCatalogRequestBlocked.IsCompleted,token);
            var count=model.ItemsCount;SubmitShellBarcode(view,"LOOP-REMOTE");
            await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.ItemsCount==count+1,token);
            box.Text="UNSENT-CODE";box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();box.Select(2,4);box.Focus();
            var selected=model.SelectedCartItem;var total=model.Total;
            await ClickShellMenuAsync(shell,"DailyReport",token);
            var reportView=(DailyReportView)shell.FindName("DailyReportViewControl");
            await WaitCatalogRetryUiAsync(()=>reportView.DataContext is DailyReportViewModel r && !r.IsBusy,token);
            var report=(DailyReportViewModel)reportView.DataContext;report.SelectedTabIndex=1;
            var childDone=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _=shell.Dispatcher.BeginInvoke(new Action(async()=>
            {
                HistoricalSalesImportDialog import=null;
                try
                {
                    await WaitCatalogRetryUiAsync(()=>shell.OwnedWindows.OfType<HistoricalSalesImportDialog>().Any(),token);
                    import=shell.OwnedWindows.OfType<HistoricalSalesImportDialog>().Single();
                    import.Activate();import.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    var focused=Keyboard.FocusedElement;
                    var requests=server.CatalogRequestCount;server.ReleaseNextCatalogResponse();
                    await WaitCatalogRetryUiAsync(()=>coordinator.GetSnapshot().Lanes.Any(x=>x.Lane==OnlineSyncLane.CatalogDelta && !x.InFlight),token);
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    check("catalog_publication_preserves_import_report_cart_and_input",import.IsVisible &&
                        ReferenceEquals(Keyboard.FocusedElement,focused) && ReferenceEquals(reportView.DataContext,report) &&
                        ReferenceEquals(view,ShellField<PosView>(shell,"PosViewControl")) && model.Total==total &&
                        ReferenceEquals(model.SelectedCartItem,selected) && model.BarcodeInput=="UNSENT-CODE" && box.SelectionStart==2 && box.SelectionLength==4);
                    import.Close();childDone.TrySetResult(true);
                }catch(Exception error){childDone.TrySetException(error);}
                finally{if(import?.IsVisible==true)import.Close();}
            }));
            report.ImportHistoryCommand.Execute(null);await childDone.Task;
            await ClickShellMenuAsync(shell,"Pos",token);
            foreach(var disconnect in new[]{false,true})
            {
                var requests=server.CatalogRequestCount;server.FailNextCatalogResponse(disconnect);
                await PulseShellCatalogAsync(shell,clock,token);
                await WaitCatalogRetryUiAsync(()=>server.CatalogRequestCount>requests && coordinator.GetSnapshot().Lanes.Any(x=>x.Lane==OnlineSyncLane.CatalogDelta && !x.InFlight),token);
                count=model.ItemsCount;var timer=Stopwatch.StartNew();SubmitShellBarcode(view,"LOOP-REMOTE");
                await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.ItemsCount==count+1,token);
                check(disconnect?"valid_snapshot_survives_transport_loss":"valid_snapshot_survives_503",ShellField<bool>(shell,"_shellCanSell") &&
                    ReferenceEquals(view,ShellField<PosView>(shell,"PosViewControl")) && !coordinator.IsRecoveryMode);
                WriteJson(Path.Combine(data,"shell-known-barcode-"+(disconnect?"offline":"503")+".private.json"),new ShellLatencyProof{BarcodeFeedbackMilliseconds=timer.Elapsed.TotalMilliseconds});
            }
            var before=ReadShellCommercialCounts(factory);
            server.HoldNextCatalogResponse();await PulseShellCatalogAsync(shell,clock,token);
            await WaitCatalogRetryUiAsync(()=>server.NextCatalogRequestBlocked.IsCompleted,token);
            server.AllowCancelledCatalogPeer();shell.CloseWithoutUserPrompt();
            await WaitCatalogRetryUiAsync(()=>!shell.IsVisible,token);
            check("close_during_network_pull_joins_owned_work",ShellField<bool>(shell,"_ownedWorkStopped") &&
                coordinator.GetSnapshot()==null && before.SequenceEqual(ReadShellCommercialCounts(factory)));
            server.ReleaseNextCatalogResponse();
        }

        private static async Task VerifyExclusiveCatalogApplyInputAsync(MainWindow shell,SqliteConnectionFactory factory,
            CatalogRetryUiCheck check,CancellationToken token,string data)
        {
            var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task watchdog=Task.CompletedTask;
            var publisher=Task.Run(async()=>
            {
                using(var connection=factory.Open())
                {
                    connection.Execute("BEGIN EXCLUSIVE;");
                    try{entered.TrySetResult(true);await release.Task.ConfigureAwait(false);}
                    finally{connection.Execute("ROLLBACK;");}
                }
            });
            try
            {
                await WaitCatalogRetryUiAsync(()=>entered.Task.IsCompleted || publisher.IsCompleted,token);
                if(publisher.IsFaulted)await publisher;
                var view=ShellField<PosView>(shell,"PosViewControl");var model=(PosViewModel)view.DataContext;
                var before=model.ItemsCount;var timer=Stopwatch.StartNew();
                var probe=new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                // A bounded independent watchdog releases the fixture's SQL lock
                // even if a regression blocks the Dispatcher in the public scan.
                watchdog=Task.Run(async()=>
                {
                    await Task.WhenAny(probe.Task,Task.Delay(250)).ConfigureAwait(false);
                    release.TrySetResult(true);
                });
                SubmitShellBarcode(view,"LOOP-REMOTE");
                _=shell.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>probe.TrySetResult(timer.Elapsed.TotalMilliseconds)));
                await watchdog;await publisher;
                var latency=await probe.Task;
                await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.ItemsCount==before+1,token);
                check("input_during_exclusive_catalog_apply_within_250ms",latency<=250);
                WriteJson(Path.Combine(data,"shell-apply-input-latency.private.json"),new ShellLatencyProof{InputCompletionMilliseconds=latency});
            }
            finally
            {
                release.TrySetResult(true);
                await publisher; // Own the fixture's connection through release, including failures.
                await watchdog;
            }
        }

        private static async Task ClickShellMenuAsync(MainWindow shell,string tag,CancellationToken token)
        {
            ((Button)shell.FindName("HamburgerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var panel=(FrameworkElement)shell.FindName("SideMenuPanel"); panel.UpdateLayout();
            Program.HarnessWindow.FindVisualDescendants<Button>(panel)
                .Single(x=>string.Equals(x.Tag as string,tag,StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var expected=tag=="Pos"?0:2;
            await WaitCatalogRetryUiAsync(()=>((TabControl)shell.FindName("MainTabControl")).SelectedIndex==expected &&
                ((FrameworkElement)shell.FindName("SideMenuOverlay")).Visibility!=Visibility.Visible &&
                (expected!=0 || ShellField<PosView>(shell,"PosViewControl")?.IsVisible==true),token);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if(expected==0)File.AppendAllText(Path.Combine(AppPaths.DataDirectory,"shell-return-focus.private.txt"),
                "phase=menu_pos_settled; focused="+((TextBox)ShellField<PosView>(shell,"PosViewControl").FindName("BarcodeBox")).IsKeyboardFocused+
                "; keyboard_target="+(Keyboard.FocusedElement?.GetType().Name??"none")+Environment.NewLine);
        }

        private static void SubmitShellBarcode(PosView view,string text)
        {
            var box=(TextBox)view.FindName("BarcodeBox");
            box.Text=text; box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); box.Focus();
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(box),
                Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
        }

        private static async Task VerifyHeldCatalogInputAsync(MainWindow shell,CatalogRetryUiCheck check,CancellationToken token,string data)
        {
            var view=ShellField<PosView>(shell,"PosViewControl");
            var model=(PosViewModel)view.DataContext;
            var timer=Stopwatch.StartNew();
            var input=new TaskCompletionSource<bool>();
            await shell.Dispatcher.InvokeAsync(new Action(()=>input.TrySetResult(true)),DispatcherPriority.Input).Task;
            await input.Task;
            var completion=timer.Elapsed.TotalMilliseconds;
            check("input_completion_during_catalog_within_250ms",completion<=250);
            timer.Restart();
            SubmitShellBarcode(view,"2000");
            SubmitShellBarcode(view,"2000");
            SubmitShellBarcode(view,"2000");
            await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.AddBarcodeCommand.CanExecute(null),token);
            var scannerCompletion=timer.Elapsed.TotalMilliseconds;
            check("rapid_input_queued_once_per_event",model.ItemsCount==3 && model.Total==6000 && model.CartItems.Count==1);
            model.RemoveLineCommand.Execute(null);
            await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.ItemsCount==0,token);
            SubmitShellBarcode(view,"PENDING-SCAN");
            await WaitCatalogRetryUiAsync(()=>!model.IsBusy && model.AddBarcodeCommand.CanExecute(null),token);
            check("first_download_unknown_is_feedback_without_quick_create",model.ItemsCount==0 &&
                !shell.OwnedWindows.OfType<ProductEditDialog>().Any());
            WriteJson(Path.Combine(data,"shell-held-input-latency.private.json"),new ShellLatencyProof{InputCompletionMilliseconds=completion,ScannerThreeInputsMilliseconds=scannerCompletion});
        }

        [System.Runtime.Serialization.DataContract]
        private sealed class ShellLatencyProof
        {
            [System.Runtime.Serialization.DataMember] public double AuthClosedToInteractiveMilliseconds { get; set; }
            [System.Runtime.Serialization.DataMember] public double InputCompletionMilliseconds { get; set; }
            [System.Runtime.Serialization.DataMember] public double ScannerThreeInputsMilliseconds { get; set; }
            [System.Runtime.Serialization.DataMember] public double BarcodeFeedbackMilliseconds { get; set; }
        }

        private sealed class ShellSyncClock : IOnlineSyncClock
        {
            private readonly object _gate=new object();
            private readonly List<ScheduledShellDelay> _delays=new List<ScheduledShellDelay>();
            private DateTimeOffset _now=DateTimeOffset.UtcNow;
            public DateTimeOffset UtcNow {get{lock(_gate)return _now;}}
            public Task DelayAsync(TimeSpan delay,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                ScheduledShellDelay pending;
                lock(_gate){pending=new ScheduledShellDelay{Due=_now+delay};_delays.Add(pending);}
                var registration=token.Register(()=>pending.Completion.TrySetCanceled());
                return AwaitAndReleaseAsync(pending,registration);
            }
            private static async Task AwaitAndReleaseAsync(ScheduledShellDelay pending,CancellationTokenRegistration registration)
            {try{await pending.Completion.Task.ConfigureAwait(false);}finally{registration.Dispose();}}
            internal bool HasScheduledAt(DateTimeOffset due)
            {lock(_gate)return _delays.Any(x=>x.Due==due && !x.Completion.Task.IsCompleted);}
            internal void AdvanceTo(DateTimeOffset next)
            {
                ScheduledShellDelay[] due;
                lock(_gate){if(next>_now)_now=next;due=_delays.Where(x=>x.Due<=_now).ToArray();foreach(var d in due)_delays.Remove(d);}
                foreach(var d in due)d.Completion.TrySetResult(true);
            }
            private sealed class ScheduledShellDelay
            {internal DateTimeOffset Due;internal readonly TaskCompletionSource<bool> Completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);}
        }

        private static T ShellField<T>(MainWindow shell, string field) => (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
        private static object ShellInvoke(MainWindow shell, string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(shell, args);
    }
}
