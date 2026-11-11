using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>
    /// One bounded local acceptance scenario through the real first-login transport,
    /// bootstrap catalog pull, WPF product view models, durable scheduler lane, and
    /// canonical pull apply. The server is loopback-only and keeps synthetic state.
    /// </summary>
    internal static partial class ArticleMutationLoopbackHarness
    {
        internal static async Task<bool> VerifyEmptyShopBootstrapAsync(string data, HistoricalSalesQaCase uiTest = null,
            OperatorDialogGeometrySmoke geometry = null)
        {
            // The real bootstrap also reconciles the process-wide image cache,
            // whose scope store belongs to the current product profile. Own that
            // same fresh QA profile, rather than creating an unrelated second DB.
            var options = PosDbOptions.Default();
            if (!string.Equals(Path.GetFullPath(options.DbPath), Path.GetFullPath(Path.Combine(data, "pos.db")),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("empty_history_loopback_profile_scope_mismatch");
            var authority = PosDatabaseAuthorityLease.AcquireSyntheticNew(options);
            Exception primaryFailure = null;
            try
            {
                options = options.WithAccessContext(authority.AccessContext);
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);
                var store = new PosTrustedDeviceStore();
                store.Clear();
                using (var server = new ArticleLoopbackServer(authoritativelyEmptyCatalog: true))
                using (var host = new PosOnlineSyncSupervisorHost(factory))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    // The canonical worker deliberately inherits a closed loopback
                    // endpoint. Scope this synthetic server's explicit endpoint to
                    // this serial scenario because the real supervisor reloads the
                    // configured endpoint for each lane; restore it even on failure.
                    var previousBaseUrl = Environment.GetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable);
                    try
                    {
                    Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, server.BaseUrl);
                    PosAdminWebOptions.SaveBaseUrl(new Uri(server.BaseUrl));
                    var result = await new PosOnlineBootstrapService(factory, store, host).BootstrapAsync(
                        new PosAdminWebOptions(new Uri(server.BaseUrl)), new PosFirstLoginRequest
                        {
                            Credential = "synthetic-loopback-credential", ShopCode = ShopCode, StaffCode = "ARTICLEPOS",
                            Device = new PosFirstLoginDevice { AppVersion = "empty-history-loopback", DeviceIdentifier = "synthetic-empty-history-device", DisplayName = "Synthetic empty history" }
                        }, "2468", timeout.Token).ConfigureAwait(true);
                    if (!result.CanEnterWorkstation || result.CatalogCompleted || server.CatalogRequestCount != 0)
                        throw new InvalidOperationException("bootstrap_must_transfer_before_catalog");
                    // Component qualification of the empty proof, separate from the
                    // public automatic-entry acceptance in AuthenticatedShellUxSmoke.
                    result=await CompleteCatalogForQaAsync(result,host,timeout.Token);
                    if (!result.CanPrepareEmptyShopHistory || result.CanOpenPos || result.CatalogSaleSafe ||
                        !result.FirstLoginSucceeded || !result.TrustedSessionPersisted || !result.CatalogCompleted ||
                        server.CatalogRequestCount != 1 || !store.TryRead(out var trusted))
                        throw new InvalidOperationException("empty_history_loopback_bootstrap_" + result.Code + "_catalog_requests_" + server.CatalogRequestCount);
                    var users = new UserRepository(factory);
                    var username = await users.FindTrustedRemoteStaffUsernameAsync(trusted.ShopId, trusted.ShopCode,
                        trusted.StaffId, trusted.StaffCode, trusted.StaffCredentialVersion);
                    var session = new OperatorSession(users, new SecurityRepository(factory),
                        new PosOfflineAuthorizationLeaseGuard(store, () => DateTimeOffset.UtcNow, async current =>
                            PosOnlineSyncSupervisorHost.TryCreateGeneration(current, out var currentGeneration) &&
                            await new OnlineSyncGenerationRepository(factory).IsCurrentAndActiveAsync(currentGeneration)));
                    try
                    {
                        if (await session.LoginAsync(username, "2468") != LoginResult.Success || !session.CurrentUser.IsAdmin)
                            throw new InvalidOperationException("empty_history_loopback_mirror_login_denied");
                        var access = await HistoricalOnboardingAccess.CreateAsync(factory, session, host.CurrentGeneration, timeout.Token);
                        await access.DemandAsync(timeout.Token);
                        if (!access.IsCurrentInMemory || await PosCatalogPullService.IsCatalogSaleSafeAsync(factory) || server.ArticleRequestCount != 0)
                            throw new InvalidOperationException("empty_history_loopback_sales_guard_failed");
                        var reader = new PosSyncStatusReader(factory, store);
                        var snapshot = await reader.ReadAsync();
                        var emptyText = Win7POS.Wpf.Localization.PosLocalization.T("history.catalogEmptyVerified");
                        if (snapshot.CatalogSaleSafe || !snapshot.RequiresAttention || snapshot.CatalogHasError ||
                            snapshot.CatalogErrorRetryable || snapshot.CatalogRepairRequired || snapshot.CatalogHasMore ||
                            snapshot.CatalogBootstrapText != emptyText || snapshot.CatalogReadinessText != emptyText ||
                            snapshot.CatalogCompletenessText != emptyText || !snapshot.SummaryText.StartsWith(emptyText, StringComparison.Ordinal))
                            throw new InvalidOperationException("empty_history_loopback_truthful_status_failed" +
                                ";saleSafe=" + snapshot.CatalogSaleSafe + ";attention=" + snapshot.RequiresAttention +
                                ";hasError=" + snapshot.CatalogHasError + ";retry=" + snapshot.CatalogErrorRetryable +
                                ";repair=" + snapshot.CatalogRepairRequired + ";hasMore=" + snapshot.CatalogHasMore +
                                ";bootstrapMatches=" + (snapshot.CatalogBootstrapText == emptyText) +
                                ";readinessMatches=" + (snapshot.CatalogReadinessText == emptyText) +
                                ";completenessMatches=" + (snapshot.CatalogCompletenessText == emptyText) +
                                ";summaryMatches=" + snapshot.SummaryText.StartsWith(emptyText, StringComparison.Ordinal));
                        // A status string cannot replace the scoped observation.
                        await new CatalogShopStateRepository(factory).ClearAuthoritativeEmptyAsync(host.CurrentGeneration);
                        var stale = await reader.ReadAsync();
                        if (stale.CatalogBootstrapText == emptyText || stale.CatalogSaleSafe || !stale.CatalogRepairRequired)
                            throw new InvalidOperationException("empty_history_loopback_stale_status_promoted");
                        await VerifyPreservedCatalogCannotResurrectAsync(factory, host, server, timeout.Token);
                        await VerifyCatalogRetryUiAsync(factory, data, timeout.Token, uiTest, geometry);
                        return true;
                    }
                    finally { session.LogoutForced(); store.Clear(); }
                    }
                    catch (Exception error) { primaryFailure = error; throw; }
                    finally
                    {
                        try { await host.StopAsync().ConfigureAwait(true); }
                        catch (Exception cleanup)
                        {
                            File.WriteAllText(Path.Combine(data, "history-onboarding-host-cleanup.private.json"),
                                "{\"status\":\"FAIL\",\"exceptionType\":\"" + cleanup.GetType().Name + "\",\"primaryFailurePreserved\":" + (primaryFailure != null ? "true" : "false") + "}");
                            if (primaryFailure == null) throw;
                        }
                        finally
                        {
                            Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable, previousBaseUrl);
                            store.Clear();
                        }
                    }
                }
            }
            catch (Exception error) { primaryFailure = primaryFailure ?? error; throw; }
            finally
            {
                try { SqliteConnectionFactory.ClearAllPools(); authority.Dispose(); }
                catch (Exception cleanup)
                {
                    File.WriteAllText(Path.Combine(data, "history-onboarding-ui-cleanup.private.json"),
                        "{\"status\":\"FAIL\",\"exceptionType\":\"" + cleanup.GetType().Name + "\",\"primaryFailurePreserved\":" + (primaryFailure != null ? "true" : "false") + "}");
                    if (primaryFailure == null) throw;
                }
            }
        }

        // This private receipt contains only fixed case/assertion names, booleans
        // and request counts. Credentials remain exclusively in the synthetic
        // PasswordBox and in-memory loopback transport, never in this artifact.
        [DataContract]
        private sealed class CatalogRetryUiProof
        {
            [DataMember] public string Case;
            [DataMember] public string Status = "RUNNING";
            [DataMember] public string Failure;
            [DataMember] public string CleanupFailure;
            [DataMember] public bool FinalizationFaultInjected;
            [DataMember] public string FaultInjectionExceptionType;
            [DataMember] public int? FaultInjectionSqliteCode;
            [DataMember] public int? FaultInjectionSqliteExtendedCode;
            [DataMember] public int FirstLoginRequests;
            [DataMember] public int CatalogRequests;
            [DataMember] public List<CatalogRetryUiAssertion> Assertions = new List<CatalogRetryUiAssertion>();
        }
        [DataContract]
        private sealed class CatalogRetryUiAssertion
        {
            [DataMember] public string Id;
            [DataMember] public bool Passed;
        }

        private static async Task VerifyCatalogRetryUiAsync(SqliteConnectionFactory factory, string data,
            CancellationToken cancellationToken, HistoricalSalesQaCase uiTest, OperatorDialogGeometrySmoke geometry = null)
        {
            var proofs = new List<CatalogRetryUiProof>();
            var receiptPath = Path.Combine(data, "history-onboarding-ui.private.json");
            foreach (var variant in geometry != null ? new[] { "retry_empty" } : new[] { "retry_empty", "lease_expired", "revoked", "generation_changed", "cancelled",
                "normal_ready", "normal_late_generation", "normal_finalization_fault" })
            {
                var proof = new CatalogRetryUiProof { Case = variant }; proofs.Add(proof); WriteJson(receiptPath, proofs);
                try
                {
                    await VerifyCatalogRetryUiCaseAsync(factory, variant, proof, () => WriteJson(receiptPath, proofs), cancellationToken, geometry);
                    proof.Status = "PASS";
                }
                catch (Exception error)
                {
                    proof.Status = "FAIL";
                    var code = error.Message ?? string.Empty;
                    proof.Failure = error is OperationCanceledException ? "bounded_wait_cancelled" :
                        code.StartsWith("connect_", StringComparison.Ordinal) || code.StartsWith("retry_", StringComparison.Ordinal) ||
                        code.StartsWith("ui_control_missing_", StringComparison.Ordinal) ? code : error.GetType().Name;
                }
                WriteJson(receiptPath, proofs);
                var publicId = variant == "retry_empty" ? "retry_empty_ui" : variant == "lease_expired" ? "retry_expiry_ui" :
                    variant == "revoked" ? "retry_revocation_ui" : variant == "generation_changed" ? "retry_generation_ui" :
                    variant == "cancelled" ? "retry_cancel_ui" : variant == "normal_ready" ? "retry_normal_ready_ui" :
                    variant == "normal_late_generation" ? "retry_normal_late_authority_ui" : "retry_normal_finalization_fault_ui";
                uiTest?.Check(publicId, proof.Status == "PASS" && proof.CleanupFailure == null);
            }
            var failed = proofs.FirstOrDefault(item => item.Status != "PASS");
            if (failed != null && uiTest == null) throw new InvalidOperationException("empty_history_ui_" + failed.Case + "_" + failed.Failure);
        }

        private static void CheckCatalogRetryUi(CatalogRetryUiProof proof, Action persist, string id, bool passed,
            [System.Runtime.CompilerServices.CallerFilePath] string sourceFile="",
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine=0)
        {
            proof.Assertions.Add(new CatalogRetryUiAssertion { Id = id, Passed = passed }); persist();
            TypedQaDiagnosticWriter.RecordAssertion(passed,true,passed,sourceFile,sourceLine);
            // A process deadline can terminate before catch/finally. Preserve the
            // last completed checkpoint without exporting private proof contents.
            TypedQaDiagnosticWriter.FlushCheckpoint();
            if (!passed) throw new InvalidOperationException(id);
        }

        private static bool AccessDialogBusy(PosOnlineFirstLoginDialog dialog) =>
            (bool)typeof(PosOnlineFirstLoginDialog).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
        private static bool AccessDialogInitialized(PosOnlineFirstLoginDialog dialog) =>
            !(bool)typeof(PosOnlineFirstLoginDialog).GetField("_initializing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog);
        private static bool AccessDialogOperationFinished(PosOnlineFirstLoginDialog dialog) =>
            typeof(PosOnlineFirstLoginDialog).GetField("_activeCts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(dialog) == null;
        private static object AccessDialogPendingLogin(PosOnlineFirstLoginDialog dialog) =>
            typeof(PosOnlineFirstLoginDialog).GetField("_pendingAuthenticatedLogin", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog);
        private static T AccessControl<T>(PosOnlineFirstLoginDialog dialog, string name) where T : FrameworkElement =>
            dialog.FindName(name) as T ?? throw new InvalidOperationException("ui_control_missing_" + name);
        private static void AccessClick(PosOnlineFirstLoginDialog dialog, string name) =>
            AccessControl<Button>(dialog, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static async Task WaitCatalogRetryUiAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            while (!condition())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellationToken).ConfigureAwait(true);
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }

        // Component authority-transfer matrix. Public automatic-workstation acceptance
        // runs separately in AUTHENTICATED_SHELL_UX with MainWindow + real supervisor.
        private static async Task VerifyCatalogRetryUiCaseAsync(SqliteConnectionFactory factory,string variant,
            CatalogRetryUiProof proof,Action persist,CancellationToken cancellationToken,OperatorDialogGeometrySmoke geometry=null)
        {
            var previousOperator=OperatorSessionHolder.Current;
            var previousUrl=Environment.GetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable);
            var store=new PosTrustedDeviceStore(); store.Clear();
            using(var server=new ArticleLoopbackServer(authoritativelyEmptyCatalog:true))
            using(var host=new PosOnlineSyncSupervisorHost(factory))
            {
                long clockAdvance=0;
                var session=new OperatorSession(new UserRepository(factory),new SecurityRepository(factory),
                    new PosOfflineAuthorizationLeaseGuard(store,()=>DateTimeOffset.UtcNow.AddSeconds(
                        Interlocked.Read(ref clockAdvance)/(double)Stopwatch.Frequency),async current=>
                        PosOnlineSyncSupervisorHost.TryCreateGeneration(current,out var g) &&
                        await new OnlineSyncGenerationRepository(factory).IsCurrentAndActiveAsync(g),
                        ()=>checked(Stopwatch.GetTimestamp()+Interlocked.Read(ref clockAdvance)),Stopwatch.Frequency));
                OperatorSessionHolder.Current=session;
                Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable,server.BaseUrl);
                var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen};
                geometry?.ConfigureOwner(owner);
                PosOnlineFirstLoginDialog dialog=null;
                var settingsRenamed=false;
                try
                {
                    var result=await new PosOnlineBootstrapService(factory,store,host).BootstrapAsync(
                        new PosAdminWebOptions(new Uri(server.BaseUrl)),new PosFirstLoginRequest
                        {ShopCode=ShopCode,StaffCode="ARTICLEPOS",Credential="2468",Device=new PosFirstLoginDevice
                          {DeviceIdentifier="synthetic-component-device",DisplayName="Synthetic component",AppVersion="1.0"}},
                        "2468",cancellationToken);
                    CheckCatalogRetryUi(proof,persist,"auth_transfer_does_not_wait_for_catalog",
                        result.CanEnterWorkstation && !result.CatalogStarted && server.CatalogRequestCount==0 && !session.IsLoggedIn);
                    owner.Show();
                    dialog=new PosOnlineFirstLoginDialog(factory,false,host){Owner=owner};
                    geometry?.ObserveOpening(dialog,"access");
                    var finished=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var injected=false; Exception completionFailure=null; bool accepted=false;
                    Func<CancellationToken,Task> finalAuthority=async token=>
                    {
                        if(variant=="lease_expired") Interlocked.Exchange(ref clockAdvance,checked(5L*60*60*Stopwatch.Frequency));
                        else if(variant=="revoked") PosOnlineSyncRevocationLatch.Revoke(host.CurrentGeneration);
                        else if(variant=="generation_changed" || variant=="normal_late_generation")
                        {
                            var old=host.CurrentGeneration;
                            var next=new OnlineSyncGeneration(OnlineSyncGeneration.CreateGenerationId(),old.PosSessionId,
                                old.ShopDeviceId,old.ShopId,old.ShopCode,old.StaffId,old.StaffCredentialVersion);
                            await new OnlineSyncGenerationRepository(factory).ActivateAndRecoverAsync(next,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            if(variant=="generation_changed") store.SaveFirstLogin(server.FirstLoginFixtureResponse(),next.GenerationId);
                        }
                        else if(variant=="normal_late_operator")
                        {
                            if(await session.LoginAsync(session.CurrentUser.Username,"2468")!=LoginResult.Success)
                                throw new InvalidOperationException("retry_replacement_operator_failed");
                        }
                        else if(variant=="normal_finalization_fault")
                        {
                            using(var c=factory.Open())c.Execute("ALTER TABLE app_settings RENAME TO qa_settings_finalization_fault;");
                            settingsRenamed=true;proof.FinalizationFaultInjected=true;
                        }
                        else if(variant=="cancelled") dialog.Close();
                        injected=true;
                    };
                    dialog.ContentRendered+=async (_,__)=>
                    {
                        try
                        {
                            await WaitCatalogRetryUiAsync(()=>AccessDialogInitialized(dialog) && !AccessDialogBusy(dialog),cancellationToken);
                            var method=typeof(PosOnlineFirstLoginDialog).GetMethod("CompleteOnlineSignInAsync",BindingFlags.Instance|BindingFlags.NonPublic);
                            try {accepted=await (Task<bool>)method.Invoke(dialog,new object[]{ShopCode,"ARTICLEPOS","2468","component","online",cancellationToken,finalAuthority});}
                            catch(Exception error){completionFailure=error;}
                            finished.TrySetResult(true);
                        }
                        catch(Exception error){finished.TrySetException(error);}
                        finally{if(dialog.IsVisible)dialog.Close();}
                    };
                    dialog.ShowDialog(); await finished.Task;
                    if(settingsRenamed)
                    {
                        proof.FaultInjectionExceptionType=completionFailure?.GetType().Name;
                        proof.FaultInjectionSqliteCode=(completionFailure as Microsoft.Data.Sqlite.SqliteException)?.SqliteErrorCode;
                        proof.FaultInjectionSqliteExtendedCode=(completionFailure as Microsoft.Data.Sqlite.SqliteException)?.SqliteExtendedErrorCode;
                        using(var c=factory.Open())c.Execute("ALTER TABLE qa_settings_finalization_fault RENAME TO app_settings;");
                        settingsRenamed=false;
                        CheckCatalogRetryUi(proof,persist,"retry_normal_finalization_fault_injected",proof.FinalizationFaultInjected && completionFailure!=null);
                    }
                    CheckCatalogRetryUi(proof,persist,"retry_final_authority_boundary_executed",injected);
                    var positive=variant=="retry_empty" || variant=="normal_ready";
                    if(positive)
                        CheckCatalogRetryUi(proof,persist,"retry_normal_authority_transfers_without_catalog",
                            accepted && dialog.DialogResult==true && session.IsLoggedIn && session.EnsureAuthorizationValid() &&
                            dialog.AccessMode==PosAuthenticatedAccessMode.Normal && !dialog.IsVisible);
                    else if(variant=="normal_late_operator")
                        CheckCatalogRetryUi(proof,persist,"retry_stale_callback_preserves_newer_operator",
                            !accepted && dialog.DialogResult!=true && session.IsLoggedIn && session.EnsureAuthorizationValid() && session.CurrentOperatorAuthorityVersion>=2);
                    else
                        CheckCatalogRetryUi(proof,persist,"retry_changed_authority_never_transfers_operator",
                            !accepted && dialog.DialogResult!=true && !session.IsLoggedIn);
                    using(var password=AccessControl<PasswordBox>(dialog,"CredentialBox").SecurePassword)
                        CheckCatalogRetryUi(proof,persist,"retry_no_retained_masked_or_pending_credential",password.Length==0 && AccessDialogPendingLogin(dialog)==null);
                    CheckCatalogRetryUi(proof,persist,"retry_no_compulsory_history_or_catalog_request",
                        dialog.FindName("HistoryButton")==null && server.CatalogRequestCount==0 && server.FirstLoginRequestCount==1);
                    if(geometry!=null && positive)
                    {
                        // This component owns a manually-triggered host, without
                        // a startup coordinator or continuous lanes. Register its
                        // real credentialed request gate for the real loopback HTTP
                        // admission; maintenance has no background runner to stop.
                        using var historyRequests=PosOnlineSyncSignalBus.Register(host.Signal,host.TriggerAsync,
                            ()=>Task.CompletedTask,_=>Task.CompletedTask,requestHost:host);
                        var catalog=await host.TriggerAsync(OnlineSyncLane.CatalogDelta,OnlineSyncLaneTrigger.FirstBootstrap,cancellationToken);
                        CheckCatalogRetryUi(proof,persist,"geometry_component_has_verified_empty",catalog.Code=="catalog_authoritatively_empty");
                        var access=await HistoricalOnboardingAccess.CreateAsync(factory,session,host.CurrentGeneration,cancellationToken);
                        var service=new Win7POS.Wpf.Pos.PosWorkflowService(factory,()=>DateTimeOffset.UtcNow);
                        var workspace=new HistoricalOnboardingDialog(access,service){Owner=owner};
                        workspace.Show();await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        await geometry.VerifyWorkspaceAsync(workspace,factory,cancellationToken);workspace.Close();
                    }
                    proof.FirstLoginRequests=server.FirstLoginRequestCount;proof.CatalogRequests=server.CatalogRequestCount;persist();
                }
                finally
                {
                    try
                    {
                        if(settingsRenamed){using(var c=factory.Open())c.Execute("ALTER TABLE qa_settings_finalization_fault RENAME TO app_settings;");}
                        if(dialog?.IsVisible==true)dialog.Close();owner.Close();await host.StopAsync();
                    }
                    finally{session.LogoutForced();store.Clear();OperatorSessionHolder.Current=previousOperator;
                        Environment.SetEnvironmentVariable(PosAdminWebOptions.BaseUrlEnvironmentVariable,previousUrl);}
                }
            }
        }

        private static async Task VerifyPreservedCatalogCannotResurrectAsync(SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost host, ArticleLoopbackServer server, CancellationToken cancellationToken)
        {
            var state = new CatalogShopStateRepository(factory);
            server.UseCatalogSnapshot(empty: false);
            var nonempty = await host.TriggerAsync(OnlineSyncLane.CatalogDelta, OnlineSyncLaneTrigger.Manual, cancellationToken);
            var generation = host.CurrentGeneration;
            var original = await state.EnsureAndLoadCursorAsync(generation.ShopId, generation.ShopCode, generation);
            var exactness = await state.LoadExactnessAsync();
            if (!nonempty.Success || !nonempty.CatalogSaleSafe || !await PosCatalogPullService.IsCatalogSaleSafeAsync(factory) ||
                exactness.Status != CatalogCompletenessStatus.Verified || string.IsNullOrWhiteSpace(original.Cursor) ||
                await new ProductRepository(factory).CountActiveRemoteProductsAsync() != 1)
                throw new InvalidOperationException("empty_history_old_catalog_baseline_failed");

            // Request a full server snapshot while preserving all prior local
            // evidence until the new response qualifies. No manual reset/seed.
            server.UseCatalogSnapshot(empty: true);
            var empty = await host.TriggerAsync(OnlineSyncLane.CatalogDelta, OnlineSyncLaneTrigger.AdministratorRepair, cancellationToken);
            var preserved = await state.EnsureAndLoadCursorAsync(generation.ShopId, generation.ShopCode, generation);
            exactness = await state.LoadExactnessAsync();
            if (!empty.Success || empty.Code != "catalog_authoritatively_empty" || empty.CatalogSaleSafe ||
                await PosCatalogPullService.IsCatalogSaleSafeAsync(factory) ||
                !await state.IsAuthoritativelyEmptyAsync(generation) || original.Cursor != preserved.Cursor ||
                original.Epoch != preserved.Epoch || exactness.Status != CatalogCompletenessStatus.Verified ||
                await new ProductRepository(factory).CountActiveRemoteProductsAsync() != 1)
                throw new InvalidOperationException("empty_history_preserved_catalog_empty_fence_failed");

            // A terminal delta without authoritative totals says nothing about
            // whether the preserved old rows still belong to the remote shop.
            server.UseEmptyTerminalDelta();
            var delta = await host.TriggerAsync(OnlineSyncLane.CatalogDelta, OnlineSyncLaneTrigger.Manual, cancellationToken);
            if (delta.CatalogSaleSafe || await PosCatalogPullService.IsCatalogSaleSafeAsync(factory) ||
                !string.IsNullOrEmpty(server.LastCatalogRequestCursor) ||
                await new ProductRepository(factory).CountActiveRemoteProductsAsync() != 1)
                throw new InvalidOperationException("empty_history_terminal_delta_resurrected_old_catalog");

            // Only a newly validated nonempty full chain restores sale readiness.
            server.UseCatalogSnapshot(empty: false);
            var recovered = await host.TriggerAsync(OnlineSyncLane.CatalogDelta, OnlineSyncLaneTrigger.Manual, cancellationToken);
            if (!recovered.Success || !recovered.CatalogSaleSafe || !await PosCatalogPullService.IsCatalogSaleSafeAsync(factory))
                throw new InvalidOperationException("empty_history_nonempty_full_recovery_failed");
        }

        // Existing staging/component fixtures require a fully qualified catalog
        // before exercising mutations. This explicit QA step is not login UX.
        internal static async Task<PosOnlineBootstrapResult> CompleteCatalogForQaAsync(PosOnlineBootstrapResult authenticated,
            PosOnlineSyncSupervisorHost host,CancellationToken token)
        {
            if(authenticated?.CanEnterWorkstation!=true)return authenticated;
            var lane=await host.TriggerAsync(OnlineSyncLane.CatalogDelta,OnlineSyncLaneTrigger.FirstBootstrap,token).ConfigureAwait(true);
            if(lane.Success && lane.CatalogSaleSafe)
                return PosOnlineBootstrapResult.Ok(PosCatalogPullOutcome.CompletedOk(lane.CatalogPagesProcessed,diagnostic:lane.CatalogDiagnostic),
                    authenticated.ClientRequestId,authenticated.ServerRequestId,authenticated.CfRay,authenticated.HttpStatus);
            var outcome=lane.Success && lane.Code=="catalog_authoritatively_empty"
                ? PosCatalogPullOutcome.AuthoritativelyEmpty(lane.CatalogPagesProcessed)
                : PosCatalogPullOutcome.Failure(lane.Code,lane.AuthenticationDenied,lane.CatalogHasMore,lane.CatalogPagesProcessed,diagnostic:lane.CatalogDiagnostic);
            return PosOnlineBootstrapResult.CatalogIncomplete(lane.Code,"QA catalog preparation",lane.AuthenticationDenied,lane.CatalogHasMore,outcome);
        }

        private const string ShopCode = "ARTICLELOOP";
        private const string ShopId =
            "10000000-0000-4000-8000-000000000245";
        private const string StaffId =
            "20000000-0000-4000-8000-000000000245";
        private const string DeviceId =
            "30000000-0000-4000-8000-000000000245";
        private const string SessionId =
            "40000000-0000-4000-8000-000000000245";
        private const string RemoteSourceId =
            "50000000-0000-4000-8000-000000000245";

        internal static async Task<string> RunAsync(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var report = new LoopbackReport
            {
                StartedAtUtc = UtcNow()
            };

            try
            {
                report.StaUiDispatchMarshaled =
                    await StagingArticleMutationAcceptance
                        .RunStaUiDispatchRegressionAsync()
                        .ConfigureAwait(true);
                Require(
                    report.StaUiDispatchMarshaled,
                    "sta_ui_dispatch_regression_failed");
                report.CleanupManifestRemoteChildCounts =
                    StagingArticleMutationAcceptance
                        .HasCompleteCleanupRemoteChildIds(
                            new[] {
                                "price-1",
                                "price-2",
                                "price-3",
                                "price-4"
                            },
                            new[] {
                                "movement-1",
                                "movement-2",
                                "movement-3"
                            }) &&
                    !StagingArticleMutationAcceptance
                        .HasCompleteCleanupRemoteChildIds(
                            new[] { "price-1", "price-2" },
                            new[] { "movement-1", "movement-2" });
                Require(
                    report.CleanupManifestRemoteChildCounts,
                    "cleanup_manifest_remote_child_count_regression_failed");

                var options = PosDbOptions.Default();
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);
                using (var server = new ArticleLoopbackServer(
                    holdFirstCatalogResponse: true))
                using (var host = new PosOnlineSyncSupervisorHost(factory))
                using (var timeout = new CancellationTokenSource(
                    TimeSpan.FromMinutes(2)))
                {
                    PosAdminWebOptions.SaveBaseUrl(new Uri(server.BaseUrl));
                    var store = new PosTrustedDeviceStore();
                    var markerDirectory = Path.Combine(
                        outputDirectory,
                        "run-consumed-callback-regression");
                    Directory.CreateDirectory(markerDirectory);
                    var markerPath = Path.Combine(
                        markerDirectory,
                        "run-consumed-redacted.json");
                    var bootstrapTask = new PosOnlineBootstrapService(
                            factory,
                            store,
                            host)
                        .BootstrapAsync(
                            new PosAdminWebOptions(new Uri(server.BaseUrl)),
                            new PosFirstLoginRequest
                            {
                                Credential = "synthetic-loopback-credential",
                                ShopCode = ShopCode,
                                StaffCode = "ARTICLEPOS",
                                Device = new PosFirstLoginDevice
                                {
                                    AppVersion = "article-loopback",
                                    DeviceIdentifier =
                                        "article-loopback-device",
                                    DisplayName =
                                        "Article mutation loopback"
                                }
                            },
                            "2468",
                            timeout.Token,
                            requestReachedServerObserved: () =>
                                StagingAcceptanceWpfHarness
                                    .WriteRunConsumedMarkerAtomically(
                                        markerDirectory,
                                        "ASUSART_POST_PR68_CALLBACK_REGRESSION"));
                    var authenticated=await bootstrapTask.ConfigureAwait(true);
                    Require(authenticated.CanEnterWorkstation && File.Exists(markerPath) && server.CatalogRequestCount==0,
                        "authentication_or_run_consumed_marker_waited_for_catalog");
                    var catalogTask=CompleteCatalogForQaAsync(authenticated,host,timeout.Token);
                    var catalogBlocked = await Task.WhenAny(
                            server.FirstCatalogRequestBlocked,
                            Task.Delay(
                                TimeSpan.FromSeconds(10),
                                timeout.Token))
                        .ConfigureAwait(true);
                    Require(
                        ReferenceEquals(
                            catalogBlocked,
                            server.FirstCatalogRequestBlocked) &&
                        File.Exists(markerPath) &&
                        !catalogTask.IsCompleted,
                        "run_consumed_marker_not_durable_before_catalog_return");
                    server.ReleaseCatalogResponse();
                    var bootstrap = await catalogTask.ConfigureAwait(true);
                    Require(
                        bootstrap.Success &&
                        bootstrap.FirstLoginSucceeded &&
                        bootstrap.TrustedSessionPersisted &&
                        bootstrap.CatalogCompleted &&
                        bootstrap.CatalogSaleSafe &&
                        bootstrap.CanOpenPos,
                        "bootstrap_not_complete_" + SafeCode(bootstrap.Code));
                    Require(
                        store.TryRead(out var trustedSession),
                        "trusted_session_not_readable");
                    var authority = PosOfflineAuthorizationLeasePolicy.Evaluate(
                        trustedSession,
                        DateTimeOffset.UtcNow);
                    Require(
                        trustedSession.OfflineAuthorizationAttested &&
                        authority.Allowed,
                        "offline_authority_not_valid_" +
                        SafeCode(authority.Code));
                    Require(
                        host.CurrentGeneration != null,
                        "sync_generation_not_active");
                    report.FirstLogin = true;
                    report.FullCatalogPull = true;
                    report.OfflineAuthority = true;

                    var workflow = ProductsWorkflowService.CreateDefault();
                    var replayCreate = await CreateViewModelAsync(
                        ProductEditMode.New,
                        null,
                        workflow).ConfigureAwait(true);
                    Populate(
                        replayCreate,
                        "LOOP-CREATE-REPLAY",
                        "Loopback replay create",
                        800,
                        350,
                        4,
                        "LOOP-REPLAY-ITEM");
                    await SubmitAsync(
                        replayCreate,
                        exerciseOffDispatcher: true).ConfigureAwait(true);
                    report.OffDispatcherSubmitMarshaled = true;

                    var replayOutcome = await TriggerArticlesAsync(
                        host,
                        timeout.Token).ConfigureAwait(true);
                    var replayPending = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait');").ConfigureAwait(false);
                    var replayBlocked = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'failed_blocked';").ConfigureAwait(false);
                    Require(
                        server.LostResponseCount == 1,
                        "lost_response_retry_not_observed_" +
                        SafeCode(replayOutcome.Code) +
                        "_requests_" +
                        server.ArticleRequestCount.ToString(
                            CultureInfo.InvariantCulture) +
                        "_pending_" +
                        replayPending.ToString(
                            CultureInfo.InvariantCulture) +
                        "_blocked_" +
                        replayBlocked.ToString(
                            CultureInfo.InvariantCulture));
                    await MakeRetriesDueAsync(factory).ConfigureAwait(false);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    Require(
                        server.DuplicateReplayCount == 1,
                        "duplicate_replay_not_observed");
                    report.DuplicateReplay = true;

                    var dependentCreate = await CreateViewModelAsync(
                        ProductEditMode.New,
                        null,
                        workflow).ConfigureAwait(true);
                    Populate(
                        dependentCreate,
                        "LOOP-DEPENDENT",
                        "Loopback dependent create",
                        900,
                        400,
                        6,
                        "LOOP-DEPENDENT-ITEM");
                    await SubmitAsync(dependentCreate).ConfigureAwait(true);
                    var dependentRow = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-DEPENDENT")
                        .ConfigureAwait(true);
                    Require(
                        dependentRow != null &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM products
WHERE id = @id
  AND remote_product_id IS NULL;",
                            new { id = dependentRow?.Id ?? 0 })
                        .ConfigureAwait(false) == 1,
                        "dependent_create_remote_identity_assigned_too_early");
                    var dependentEdit = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        dependentRow,
                        workflow).ConfigureAwait(true);
                    Populate(
                        dependentEdit,
                        "LOOP-DEPENDENT-EDIT",
                        "Loopback dependent edited offline",
                        900,
                        400,
                        6,
                        "LOOP-DEPENDENT-ITEM-EDIT");
                    await SubmitAsync(dependentEdit).ConfigureAwait(true);
                    Require(
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE local_product_id = @id
  AND state = 'waiting_dependency';",
                            new { id = dependentRow.Id })
                        .ConfigureAwait(false) > 0,
                        "dependent_edit_not_waiting");
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.DependentEdit = true;

                    var remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE")
                        .ConfigureAwait(true);
                    Require(
                        remoteSource != null &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM products
WHERE id = @id
  AND remote_product_id = @remoteProductId;",
                            new
                            {
                                id = remoteSource?.Id ?? 0,
                                remoteProductId = RemoteSourceId
                            })
                        .ConfigureAwait(false) == 1,
                        "bootstrap_source_missing");
                    var fullEdit = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        fullEdit,
                        "LOOP-REMOTE-EDIT",
                        "Loopback remote edited",
                        1400,
                        600,
                        15,
                        "LOOP-REMOTE-ITEM-EDIT");
                    fullEdit.Name2 = "Loopback second name";
                    fullEdit.SelectedStockReason = fullEdit.StockReasons.First(
                        item => item.Code == "found");
                    await SubmitAsync(fullEdit).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.ProductUpdate = true;
                    report.RetailPrice = true;
                    report.PurchasePrice = true;
                    report.StockPlusFive = true;

                    remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE-EDIT")
                        .ConfigureAwait(true);
                    var stockMinusTwo = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        stockMinusTwo,
                        remoteSource.Barcode,
                        remoteSource.Name,
                        checked((int)remoteSource.UnitPrice),
                        remoteSource.PurchasePrice,
                        13,
                        remoteSource.ArticleCode);
                    stockMinusTwo.Name2 = remoteSource.Name2;
                    stockMinusTwo.SelectedStockReason =
                        stockMinusTwo.StockReasons.First(
                            item => item.Code == "count_correction");
                    await SubmitAsync(stockMinusTwo).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.StockMinusTwo = true;

                    var duplicateSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-DEPENDENT-EDIT")
                        .ConfigureAwait(true);
                    var duplicate = await CreateViewModelAsync(
                        ProductEditMode.Duplicate,
                        duplicateSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        duplicate,
                        "LOOP-DUPLICATE",
                        "Loopback duplicate",
                        950,
                        425,
                        2,
                        "LOOP-DUPLICATE-ITEM");
                    await SubmitAsync(duplicate).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    var duplicateRow = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-DUPLICATE")
                        .ConfigureAwait(true);
                    Require(
                        duplicateRow != null &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM products
WHERE id = @id
  AND remote_product_id IS NOT NULL;",
                            new { id = duplicateRow?.Id ?? 0 })
                        .ConfigureAwait(false) == 1,
                        "duplicate_remote_identity_missing");
                    report.DuplicateProduct = true;

                    await workflow.SetProductActiveAsync(
                            duplicateRow.Id,
                            false)
                        .ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    await workflow.SetProductActiveAsync(
                            duplicateRow.Id,
                            true)
                        .ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    report.DeactivateReactivate = true;

                    remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE-EDIT")
                        .ConfigureAwait(true);
                    var conflict = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        conflict,
                        remoteSource.Barcode,
                        "LOCAL CONFLICT NAME",
                        checked((int)remoteSource.UnitPrice),
                        remoteSource.PurchasePrice,
                        remoteSource.StockQty,
                        remoteSource.ArticleCode);
                    conflict.Name2 = remoteSource.Name2;
                    await SubmitAsync(conflict).ConfigureAwait(true);
                    server.AdvanceRemoteRevision(RemoteSourceId);
                    var conflictOutcome = await TriggerArticlesAsync(
                        host,
                        timeout.Token).ConfigureAwait(true);
                    Require(
                        !conflictOutcome.Success &&
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'failed_blocked'
  AND last_typed_code = 'failed_conflict';")
                        .ConfigureAwait(false) == 1,
                        "stale_conflict_not_blocked");
                    report.StaleConflict = true;

                    var beforePullOutbox = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                        .ConfigureAwait(false);
                    var pull = await new PosCatalogPullService(factory)
                        .TryPullCatalogForSupervisorAsync(
                            new PosAdminWebOptions(
                                new Uri(server.BaseUrl)),
                            trustedSession,
                            generation: host.CurrentGeneration,
                            executionContext: null,
                            forceFullRepair: true,
                            bootstrapRun: false,
                            cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                    Require(
                        pull.Completed &&
                        pull.CatalogSaleSafe,
                        "canonical_pull_failed_" +
                        SafeCode(pull.StatusCode));
                    var afterPullOutbox = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                        .ConfigureAwait(false);
                    Require(
                        beforePullOutbox == afterPullOutbox,
                        "canonical_pull_created_outbound_echo");
                    report.CanonicalPull = true;
                    report.ZeroEcho = true;

                    remoteSource = await workflow
                        .GetByBarcodeDetailsAsync("LOOP-REMOTE-EDIT")
                        .ConfigureAwait(true);
                    var correction = await CreateViewModelAsync(
                        ProductEditMode.Edit,
                        remoteSource,
                        workflow).ConfigureAwait(true);
                    Populate(
                        correction,
                        remoteSource.Barcode,
                        "Loopback conflict resolved",
                        checked((int)remoteSource.UnitPrice),
                        remoteSource.PurchasePrice,
                        remoteSource.StockQty,
                        remoteSource.ArticleCode);
                    correction.Name2 = remoteSource.Name2;
                    await SubmitAsync(correction).ConfigureAwait(true);
                    await DrainArticlesAsync(
                        factory,
                        host,
                        timeout.Token).ConfigureAwait(true);
                    Require(
                        await CountAsync(
                            factory,
                            @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'completed'
  AND last_typed_code = 'failed_conflict'
  AND resolution_code = 'superseded_by_correction'
  AND superseded_by_mutation_id IS NOT NULL;")
                        .ConfigureAwait(false) == 1,
                        "conflict_not_superseded_by_correction");
                    report.ConflictResolved = true;

                    var beforeFinalPullOutbox = await CountAsync(
                        factory,
                        "SELECT COUNT(1) FROM article_mutation_outbox;")
                        .ConfigureAwait(false);
                    var finalPull = await new PosCatalogPullService(factory)
                        .TryPullCatalogForSupervisorAsync(
                            new PosAdminWebOptions(
                                new Uri(server.BaseUrl)),
                            trustedSession,
                            generation: host.CurrentGeneration,
                            executionContext: null,
                            forceFullRepair: true,
                            bootstrapRun: false,
                            cancellationToken: timeout.Token)
                        .ConfigureAwait(false);
                    Require(
                        finalPull.Completed &&
                        finalPull.CatalogSaleSafe,
                        "final_canonical_pull_failed_" +
                        SafeCode(finalPull.StatusCode));
                    Require(
                        beforeFinalPullOutbox ==
                        await CountAsync(
                            factory,
                            "SELECT COUNT(1) FROM article_mutation_outbox;")
                            .ConfigureAwait(false),
                        "final_canonical_pull_created_outbound_echo");

                    await VerifyFinalStateAsync(
                        factory,
                        server,
                        replayCreate.Barcode,
                        dependentEdit.Barcode,
                        duplicate.Barcode)
                        .ConfigureAwait(false);
                    report.PendingWork = 0;
                    report.BlockedConflicts = 0;
                    report.PriceHistoryDuplicates = 0;
                    report.StockMovementDuplicates = 0;
                    report.SalesRows = 0;
                    report.ArticleRequests = server.ArticleRequestCount;
                    report.CatalogRequests = server.CatalogRequestCount;
                    report.Passed = true;
                    report.Code = "success";
                    await host.StopAsync().ConfigureAwait(false);
                }

                return "PASS: firstLogin=True; offlineAuthority=True; " +
                    "fullCatalog=True; create=True; duplicateReplay=True; " +
                    "staUiDispatchMarshaled=True; " +
                    "cleanupManifestRemoteChildCounts=True; " +
                    "offDispatcherSubmitMarshaled=True; " +
                    "dependentEdit=True; update=True; retail=True; " +
                    "purchase=True; stock=+5,-2; conflict=True; " +
                    "conflictResolved=True; " +
                    "duplicate=True; deactivate=True; reactivate=True; " +
                    "canonicalPull=True; zeroEcho=True; pending=0; " +
                    "blocked=0; duplicateHistory=0; duplicateMovement=0; " +
                    "sales=0";
            }
            catch (Exception ex)
            {
                report.Passed = false;
                report.Code = SafeCode(ex.Message);
                report.ExceptionType = ex.GetType().Name;
                return "FAIL article mutation loopback: " + report.Code;
            }
            finally
            {
                report.CompletedAtUtc = UtcNow();
                WriteJson(
                    Path.Combine(
                        outputDirectory,
                        "article-mutation-loopback-result.json"),
                    report);
            }
        }

        private static async Task<ProductEditViewModel> CreateViewModelAsync(
            ProductEditMode mode,
            ProductDetailsRow source,
            ProductsWorkflowService workflow)
        {
            var viewModel = new ProductEditViewModel(
                mode,
                source,
                workflow);
            viewModel.SetCategories(
                await workflow.GetCategoriesAsync().ConfigureAwait(true));
            viewModel.SetSuppliers(
                await workflow.GetSuppliersAsync().ConfigureAwait(true));
            viewModel.SetSelectionFromSource(source);
            return viewModel;
        }

        private static void Populate(
            ProductEditViewModel viewModel,
            string barcode,
            string name,
            int retail,
            int purchase,
            decimal stock,
            string itemNumber)
        {
            viewModel.Barcode = barcode;
            viewModel.ProductName = name;
            viewModel.PriceText = retail.ToString(
                CultureInfo.InvariantCulture);
            viewModel.PurchasePriceText = purchase.ToString(
                CultureInfo.InvariantCulture);
            viewModel.StockText = stock.ToString(
                CultureInfo.InvariantCulture);
            viewModel.ArticleCode = itemNumber;
        }

        private static async Task SubmitAsync(
            ProductEditViewModel viewModel,
            bool exerciseOffDispatcher = false)
        {
            var completed = new TaskCompletionSource<bool>();
            var completionOnDispatcher = false;
            Action<bool> handler = null;
            handler = success =>
            {
                completionOnDispatcher =
                    System.Windows.Application.Current?.Dispatcher
                        .CheckAccess() == true;
                viewModel.RequestClose -= handler;
                completed.TrySetResult(success);
            };
            viewModel.RequestClose += handler;
            Action submit = () =>
            {
                Require(
                    viewModel.ConfirmCommand.CanExecute(null),
                    "view_model_save_disabled");
                viewModel.ConfirmCommand.Execute(null);
            };
            if (exerciseOffDispatcher)
                await Task.Run(submit).ConfigureAwait(true);
            else
                submit();
            var finished = await Task.WhenAny(
                completed.Task,
                Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(true);
            if (!ReferenceEquals(finished, completed.Task))
            {
                viewModel.RequestClose -= handler;
                throw new TimeoutException("view_model_save_timeout");
            }
            Require(
                await completed.Task.ConfigureAwait(true),
                "view_model_save_cancelled");
            if (exerciseOffDispatcher)
            {
                Require(
                    completionOnDispatcher,
                    "view_model_save_not_marshaled_to_dispatcher");
            }
        }

        private static async Task<OnlineSyncLaneOutcome>
            TriggerArticlesAsync(
                PosOnlineSyncSupervisorHost host,
                CancellationToken cancellationToken)
        {
            return await host.TriggerAsync(
                    OnlineSyncLane.ArticleMutationOutbox,
                    OnlineSyncLaneTrigger.Manual,
                    cancellationToken)
                .ConfigureAwait(true);
        }

        private static async Task DrainArticlesAsync(
            SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost host,
            CancellationToken cancellationToken)
        {
            for (var pass = 0; pass < 40; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = await CountAsync(
                    factory,
                    @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait');")
                    .ConfigureAwait(false);
                if (remaining == 0)
                    return;

                await MakeRetriesDueAsync(factory).ConfigureAwait(false);
                var outcome = await TriggerArticlesAsync(
                    host,
                    cancellationToken).ConfigureAwait(true);
                Require(
                    !outcome.AuthenticationDenied,
                    "loopback_authentication_denied");
                if (!outcome.Success &&
                    !string.Equals(
                        outcome.Code,
                        "article_mutation_blocked",
                        StringComparison.Ordinal))
                {
                    var retries = await CountAsync(
                        factory,
                        @"SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'retry_wait';").ConfigureAwait(false);
                    Require(
                        retries > 0,
                        "article_drain_failed_" +
                        SafeCode(outcome.Code));
                }
            }
            throw new InvalidOperationException(
                "article_drain_budget_exhausted");
        }

        private static async Task MakeRetriesDueAsync(
            SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET next_attempt_at = 0
WHERE state = 'retry_wait';").ConfigureAwait(false);
            }
        }

        private static async Task<long> CountAsync(
            SqliteConnectionFactory factory,
            string sql,
            object arguments = null)
        {
            using (var connection = factory.Open())
            {
                return await connection.ExecuteScalarAsync<long>(
                        sql,
                        arguments)
                    .ConfigureAwait(false);
            }
        }

        private static async Task VerifyFinalStateAsync(
            SqliteConnectionFactory factory,
            ArticleLoopbackServer server,
            params string[] createdBarcodes)
        {
            using (var connection = factory.Open())
            {
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait');") == 0,
                    "pending_article_work_remains");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state IN (
  'waiting_dependency',
  'pending',
  'in_progress',
  'retry_wait',
  'failed_blocked');") == 0,
                    "unresolved_article_work_remains");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'completed'
  AND last_typed_code = 'failed_conflict'
  AND resolution_code = 'superseded_by_correction'
  AND superseded_by_mutation_id IS NOT NULL;") == 1,
                    "resolved_conflict_evidence_mismatch");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT remote_price_id
  FROM product_price_history
  WHERE remote_price_id IS NOT NULL
  GROUP BY remote_price_id
  HAVING COUNT(1) > 1
);") == 0,
                    "duplicate_remote_price_history");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT article_mutation_id
  FROM product_price_history
  WHERE article_mutation_id IS NOT NULL
  GROUP BY article_mutation_id
  HAVING COUNT(1) > 1
);") == 0,
                    "duplicate_local_price_history");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM (
  SELECT mutation_id
  FROM article_manual_stock_adjustments
  GROUP BY mutation_id
  HAVING COUNT(1) > 1
);") == 0,
                    "duplicate_manual_stock_adjustment");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_manual_stock_adjustments adjustment
JOIN products product
  ON product.id = adjustment.local_product_id
WHERE product.remote_product_id = @remoteProductId;",
                        new { remoteProductId = RemoteSourceId }) == 2,
                    "manual_stock_adjustment_count_mismatch");
                Require(
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales;") == 0 &&
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sale_lines;") == 0 &&
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales_sync_outbox;") == 0 &&
                    await connection.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM local_stock_movements;") == 0,
                    "article_mutation_created_sale_or_revenue_rows");
                Require(
                    await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_attempts
WHERE completed_at IS NULL;") == 0,
                    "article_attempt_ledger_incomplete");
                foreach (var barcode in createdBarcodes)
                {
                    Require(
                        await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM products
WHERE barcode = @barcode
  AND remote_product_id IS NOT NULL;",
                            new { barcode }) == 1,
                        "remote_identity_missing_" +
                        SafeCode(barcode));
                }

                var kindCounts = (await connection.QueryAsync<KindCount>(@"
SELECT mutation_kind AS Kind,
       COUNT(1) AS Count
FROM article_mutation_outbox
WHERE state = 'completed'
GROUP BY mutation_kind;")).ToDictionary(
                    row => row.Kind,
                    row => row.Count,
                    StringComparer.Ordinal);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductCreate,
                    2);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductDuplicate,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductUpdate,
                    2);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductRetailPriceChange,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductPurchasePriceChange,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductManualStockAdjustment,
                    2);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductDeactivate,
                    1);
                RequireKind(
                    kindCounts,
                    PosArticleMutationKinds.ProductActivate,
                    1);
            }

            Require(
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductCreate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductDuplicate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductUpdate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductRetailPriceChange) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductPurchasePriceChange) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductManualStockAdjustment) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductDeactivate) &&
                server.AppliedKinds.Contains(
                    PosArticleMutationKinds.ProductActivate),
                "loopback_server_did_not_apply_full_matrix");
        }

        private static void RequireKind(
            IDictionary<string, long> counts,
            string kind,
            long minimum)
        {
            Require(
                counts.TryGetValue(kind, out var count) &&
                count >= minimum,
                "completed_kind_missing_" + SafeCode(kind));
        }

        private static void Require(bool condition, string code)
        {
            if (!condition)
                throw new InvalidOperationException(code);
        }

        private static string UtcNow()
        {
            return DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);
        }

        private static string SafeCode(string value)
        {
            var source = (value ?? string.Empty).Trim().ToLowerInvariant();
            var builder = new StringBuilder();
            foreach (var character in source)
            {
                if (builder.Length >= 96)
                    break;
                if ((character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' ||
                    character == '-')
                {
                    builder.Append(character);
                }
                else if (builder.Length > 0 &&
                         builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }
            return builder.Length == 0
                ? "loopback_failure"
                : builder.ToString().TrimEnd('_');
        }

        private static void WriteJson(string path, object value)
        {
            var serializer = new DataContractJsonSerializer(
                value.GetType());
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                serializer.WriteObject(stream, value);
            }
        }

        private sealed class KindCount
        {
            public string Kind { get; set; }
            public long Count { get; set; }
        }

        [DataContract]
        private sealed class LoopbackReport
        {
            [DataMember(Order = 1)]
            public bool Passed { get; set; }
            [DataMember(Order = 2)]
            public string Code { get; set; }
            [DataMember(Order = 3)]
            public string ExceptionType { get; set; }
            [DataMember(Order = 4)]
            public string StartedAtUtc { get; set; }
            [DataMember(Order = 5)]
            public string CompletedAtUtc { get; set; }
            [DataMember(Order = 6)]
            public bool FirstLogin { get; set; }
            [DataMember(Order = 7)]
            public bool OfflineAuthority { get; set; }
            [DataMember(Order = 8)]
            public bool FullCatalogPull { get; set; }
            [DataMember(Order = 9)]
            public bool DuplicateReplay { get; set; }
            [DataMember(Order = 10)]
            public bool DependentEdit { get; set; }
            [DataMember(Order = 11)]
            public bool ProductUpdate { get; set; }
            [DataMember(Order = 12)]
            public bool RetailPrice { get; set; }
            [DataMember(Order = 13)]
            public bool PurchasePrice { get; set; }
            [DataMember(Order = 14)]
            public bool StockPlusFive { get; set; }
            [DataMember(Order = 15)]
            public bool StockMinusTwo { get; set; }
            [DataMember(Order = 16)]
            public bool StaleConflict { get; set; }
            [DataMember(Order = 17)]
            public bool DuplicateProduct { get; set; }
            [DataMember(Order = 18)]
            public bool DeactivateReactivate { get; set; }
            [DataMember(Order = 19)]
            public bool CanonicalPull { get; set; }
            [DataMember(Order = 20)]
            public bool ZeroEcho { get; set; }
            [DataMember(Order = 21)]
            public long PendingWork { get; set; }
            [DataMember(Order = 22)]
            public long BlockedConflicts { get; set; }
            [DataMember(Order = 23)]
            public long PriceHistoryDuplicates { get; set; }
            [DataMember(Order = 24)]
            public long StockMovementDuplicates { get; set; }
            [DataMember(Order = 25)]
            public long SalesRows { get; set; }
            [DataMember(Order = 26)]
            public int ArticleRequests { get; set; }
            [DataMember(Order = 27)]
            public int CatalogRequests { get; set; }
            [DataMember(Order = 28)]
            public bool OffDispatcherSubmitMarshaled { get; set; }
            [DataMember(Order = 29)]
            public bool ConflictResolved { get; set; }
            [DataMember(Order = 30)]
            public bool StaUiDispatchMarshaled { get; set; }
            [DataMember(Order = 31)]
            public bool CleanupManifestRemoteChildCounts { get; set; }
        }

        private sealed class ArticleLoopbackServer : IDisposable
        {
            private const string CategoryId =
                "60000000-0000-4000-8000-000000000245";
            private const string SupplierId =
                "70000000-0000-4000-8000-000000000245";
            private readonly object _gate = new object();
            private readonly CancellationTokenSource _cancellation =
                new CancellationTokenSource();
            private readonly TaskCompletionSource<bool>
                _firstCatalogRequestBlocked =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool>
                _releaseCatalogResponse =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<bool> _nextCatalogRequestBlocked =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private TaskCompletionSource<bool> _releaseNextCatalogResponse =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TcpListener _listener;
            private readonly HistoricalPublicationAdmissionFixture _historicalPublication =
                new HistoricalPublicationAdmissionFixture(ShopId,DeviceId,"90000000-0000-4000-8000-000000000245",allNewFixture:true);
            private readonly Task _serverTask;
            private readonly Dictionary<string, RemoteProduct> _products =
                new Dictionary<string, RemoteProduct>(
                    StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ReplayRecord> _replays =
                new Dictionary<string, ReplayRecord>(
                    StringComparer.Ordinal);
            private readonly List<RemotePrice> _prices =
                new List<RemotePrice>();
            private readonly HashSet<string> _appliedKinds =
                new HashSet<string>(StringComparer.Ordinal);
            private long _catalogRevision = 40;
            private long _revisionCounter;
            private int _articleRequestCount;
            private int _catalogRequestCount;
            private int _firstLoginRequestCount;
            private int _holdNextCatalogResponse;
            private int _nextCatalogFault;
            private int _allowCancelledCatalogPeer;
            private readonly bool _failFirstCatalog;
            private readonly string _cursorScope;
            private int _duplicateReplayCount;
            private int _lostResponseCount;
            private bool _lostCreateResponse;
            private readonly bool _holdFirstCatalogResponse;
            private bool _authoritativelyEmptyCatalog;
            private bool _emptyTerminalDelta;
            private string _lastCatalogRequestCursor;
            private int _catalogResponseHeld;

            internal ArticleLoopbackServer(
                bool holdFirstCatalogResponse = false, bool authoritativelyEmptyCatalog = false, bool failFirstCatalog = false)
            {
                _holdFirstCatalogResponse =
                    holdFirstCatalogResponse;
                _authoritativelyEmptyCatalog = authoritativelyEmptyCatalog;
                _failFirstCatalog = failFirstCatalog;
                _cursorScope = failFirstCatalog ? Guid.NewGuid().ToString("N") + "-" : string.Empty;
                var revision =
                    "2026-07-28T12:00:00.123456Z";
                _products.Add(
                    RemoteSourceId,
                    new RemoteProduct
                    {
                        RemoteProductId = RemoteSourceId,
                        Barcode = "LOOP-REMOTE",
                        ItemNumber = "LOOP-REMOTE-ITEM",
                        PrimaryName = "Loopback remote source",
                        SecondaryName = string.Empty,
                        CategoryId = CategoryId,
                        SupplierId = SupplierId,
                        RetailPrice = 1000,
                        PurchasePrice = 500,
                        StockQuantity = 10,
                        Active = true,
                        Revision = revision
                    });
                _prices.Add(
                    new RemotePrice
                    {
                        EffectiveAt = revision,
                        Price = 1000,
                        PriceId =
                            "80000000-0000-4000-8000-000000000245",
                        ProductId = RemoteSourceId,
                        Source = "catalog_pull",
                        Type = "retail"
                    });
                _prices.Add(
                    new RemotePrice
                    {
                        EffectiveAt = revision,
                        Price = 500,
                        PriceId =
                            "90000000-0000-4000-8000-000000000245",
                        ProductId = RemoteSourceId,
                        Source = "catalog_pull",
                        Type = "purchase"
                    });

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                var port =
                    ((IPEndPoint)_listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" +
                    port.ToString(CultureInfo.InvariantCulture) + "/";
                _serverTask = Task.Run(
                    () => RunAsync(_cancellation.Token));
            }

            internal string BaseUrl { get; }
            internal Task FirstCatalogRequestBlocked =>
                _firstCatalogRequestBlocked.Task;
            internal int ArticleRequestCount =>
                Volatile.Read(ref _articleRequestCount);
            internal int CatalogRequestCount =>
                Volatile.Read(ref _catalogRequestCount);
            internal int FirstLoginRequestCount => Volatile.Read(ref _firstLoginRequestCount);
            internal Task NextCatalogRequestBlocked => _nextCatalogRequestBlocked.Task;
            internal void HoldNextCatalogResponse()
            {
                lock (_gate)
                {
                    _nextCatalogRequestBlocked=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _releaseNextCatalogResponse=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Interlocked.Exchange(ref _holdNextCatalogResponse,1);
                }
            }
            internal void FailNextCatalogResponse(bool disconnect) => Interlocked.Exchange(ref _nextCatalogFault,disconnect?2:1);
            internal void AllowCancelledCatalogPeer() => Interlocked.Exchange(ref _allowCancelledCatalogPeer,1);
            internal void ReleaseNextCatalogResponse() => _releaseNextCatalogResponse.TrySetResult(true);
            internal PosFirstLoginResponse FirstLoginFixtureResponse() => BuildFirstLoginResponse();
            internal string LastCatalogRequestCursor { get { lock (_gate) return _lastCatalogRequestCursor; } }
            internal void UseCatalogSnapshot(bool empty)
            {
                lock (_gate) { _authoritativelyEmptyCatalog = empty; _emptyTerminalDelta = false; }
            }
            internal void UseEmptyTerminalDelta()
            {
                lock (_gate) { _authoritativelyEmptyCatalog = true; _emptyTerminalDelta = true; }
            }
            internal int DuplicateReplayCount =>
                Volatile.Read(ref _duplicateReplayCount);
            internal int LostResponseCount =>
                Volatile.Read(ref _lostResponseCount);
            internal ISet<string> AppliedKinds
            {
                get
                {
                    lock (_gate)
                    {
                        return new HashSet<string>(
                            _appliedKinds,
                            StringComparer.Ordinal);
                    }
                }
            }

            internal void AdvanceRemoteRevision(string remoteProductId)
            {
                lock (_gate)
                {
                    Require(
                        _products.TryGetValue(
                            remoteProductId,
                            out var product),
                        "server_remote_product_missing");
                    product.PrimaryName =
                        "SERVER CONCURRENT NAME";
                    product.Revision = NextRevision();
                    _catalogRevision++;
                }
            }

            internal void ReleaseCatalogResponse()
            {
                _releaseCatalogResponse.TrySetResult(true);
            }

            public void Dispose()
            {
                ReleaseCatalogResponse();
                ReleaseNextCatalogResponse();
                _cancellation.Cancel();
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }
                try
                {
                    _serverTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
                _cancellation.Dispose();
            }

            private async Task RunAsync(
                CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener
                            .AcceptTcpClientAsync()
                            .ConfigureAwait(false);
                        await HandleClientAsync(
                                client,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (IOException) when (Interlocked.Exchange(ref _allowCancelledCatalogPeer,0)==1)
                    {
                        client?.Dispose(); // Explicit close-during-catalog fixture; client cancellation is expected.
                    }
                    catch (ObjectDisposedException)
                    {
                        client?.Dispose();
                    }
                    catch (SocketException)
                    {
                        client?.Dispose();
                        if (!cancellationToken.IsCancellationRequested)
                            throw;
                    }
                }
            }

            private async Task HandleClientAsync(
                TcpClient client,
                CancellationToken cancellationToken)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var request = await ReadRequestAsync(
                            stream,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (request.RequestLine.StartsWith(
                            "POST /api/pos/auth/first-login ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _firstLoginRequestCount);
                        await WriteResponseAsync(
                            stream,
                            200,
                            Serialize(BuildFirstLoginResponse()),
                            cancellationToken)
                        .ConfigureAwait(false);
                        return;
                    }
                    if (request.RequestLine.StartsWith("POST /api/pos/payment-history ", StringComparison.OrdinalIgnoreCase))
                    {
                        // The geometry scenario uses the real net48 HTTP client:
                        // verify actual lease tokens and return only the local-write contract.
                        var history = Deserialize<HistoricalCloudRequest>(request.Body);
                        var lease = BuildFirstLoginResponse();
                        if (history == null || history.DeviceId != DeviceId || history.SessionId != SessionId ||
                            history.DeviceToken != lease.TrustedDeviceToken || history.SessionToken != lease.Session.SessionToken ||
                            StaffRoleKey != "pos_admin")
                        {
                            await WriteResponseAsync(stream, 403, Encoding.UTF8.GetBytes("{\"ok\":false,\"code\":\"history_cloud_access_denied\"}"), cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        string result;
                        if (history.Operation == "authority")
                            result = "{\"dataset_id\":\"90000000-0000-4000-8000-000000000245\",\"revision\":\"0\",\"writer_epoch\":\"1\",\"writer_authority\":\"local\",\"inventory_verified\":false,\"protocol_version\":1}";
                        else if (history.Operation == "local_prepare" || history.Operation == "local_stage" || history.Operation == "local_review" || history.Operation == "local_authorize")
                        {
                            lock(_gate) result=HistoricalPublicationAdmissionFixture.Wire(history.Operation,_historicalPublication.Request(history.Operation,history.Payload));
                        }
                        else
                        {
                            await WriteResponseAsync(stream, 400, Encoding.UTF8.GetBytes("{\"ok\":false,\"code\":\"history_input_invalid\"}"), cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        var body = "{\"ok\":true,\"shop_id\":\"" + ShopId + "\",\"device_id\":\"" + DeviceId + "\",\"session_id\":\"" + SessionId + "\",\"result\":" + result + "}";
                        await WriteResponseAsync(stream, 200, Encoding.UTF8.GetBytes(body), cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (request.RequestLine.StartsWith("POST /api/pos/session/heartbeat ", StringComparison.OrdinalIgnoreCase))
                    {
                        var authenticated=BuildFirstLoginResponse();
                        await WriteResponseAsync(stream,200,Serialize(new PosHeartbeatResponse
                        {
                            Ok=true,ServerTime=authenticated.ServerTime,Session=authenticated.Session,
                            CatalogRevision=_catalogRevision.ToString(CultureInfo.InvariantCulture),
                            CatalogChangesAvailable=true,NextPollAfterSeconds=30
                        }),cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (request.RequestLine.StartsWith(
                            "POST /api/pos/catalog/pull ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var catalogRequest = Deserialize<PosCatalogPullRequest>(request.Body);
                        lock (_gate) _lastCatalogRequestCursor = catalogRequest.SyncCursor ?? string.Empty;
                        var catalogRequestNumber = Interlocked.Increment(ref _catalogRequestCount);
                        var fault=Interlocked.Exchange(ref _nextCatalogFault,0);
                        if(fault==2) return; // Controlled transport loss, not a fabricated catalog response.
                        if(fault==1)
                        {
                            await WriteResponseAsync(stream,503,Encoding.UTF8.GetBytes("{\"ok\":false,\"code\":\"temporarily_unavailable\"}"),cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        if (_failFirstCatalog && catalogRequestNumber == 1)
                        {
                            await WriteResponseAsync(stream, 409, Encoding.UTF8.GetBytes("{\"ok\":false,\"code\":\"unmapped\"}"), cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }
                        if (Interlocked.Exchange(ref _holdNextCatalogResponse, 0) == 1)
                        {
                            _nextCatalogRequestBlocked.TrySetResult(true);
                            await _releaseNextCatalogResponse.Task.ConfigureAwait(false);
                        }
                        if (_holdFirstCatalogResponse &&
                            Interlocked.CompareExchange(
                                ref _catalogResponseHeld,
                                1,
                                0) == 0)
                        {
                            _firstCatalogRequestBlocked.TrySetResult(true);
                            await _releaseCatalogResponse.Task
                                .ConfigureAwait(false);
                        }
                        await WriteResponseAsync(
                            stream,
                            200,
                            Serialize(BuildCatalogResponse()),
                            cancellationToken)
                        .ConfigureAwait(false);
                        return;
                    }
                    if (request.RequestLine.StartsWith(
                            "POST " +
                            PosArticleMutationContract.EndpointPath +
                            " ",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(
                            ref _articleRequestCount);
                        var envelope =
                            Deserialize<MutationEnvelopeWire>(
                                request.Body);
                        var response =
                            ApplyArticleMutations(
                                envelope,
                                out var loseResponse);
                        if (loseResponse)
                        {
                            Interlocked.Increment(
                                ref _lostResponseCount);
                            await WriteResponseAsync(
                                    stream,
                                    500,
                                    Encoding.UTF8.GetBytes(
                                        "{\"ok\":false,\"code\":\"synthetic_lost_response\"}"),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            return;
                        }
                        await WriteResponseAsync(
                                stream,
                                200,
                                Serialize(response),
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(
                            stream,
                            404,
                            Encoding.UTF8.GetBytes(
                                "{\"ok\":false,\"code\":\"not_found\"}"),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            private PosArticleMutationResponse ApplyArticleMutations(
                MutationEnvelopeWire envelope,
                out bool loseResponse)
            {
                loseResponse = false;
                Require(
                    envelope != null &&
                    string.Equals(
                        envelope.SchemaVersion,
                        PosArticleMutationContract.SchemaVersion,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        envelope.ShopId,
                        ShopId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        envelope.ShopDeviceId,
                        DeviceId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        envelope.StaffId,
                        StaffId,
                        StringComparison.Ordinal) &&
                    envelope.StaffCredentialVersion == 7 &&
                    string.Equals(
                        envelope.PosSessionId,
                        SessionId,
                        StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(
                        envelope.DeviceToken) &&
                    !string.IsNullOrWhiteSpace(
                        envelope.SessionToken) &&
                    envelope.Mutations != null &&
                    envelope.Mutations.Length > 0 &&
                    envelope.Mutations.Length <=
                        PosArticleMutationContract.MaximumBatchCount,
                    "server_invalid_article_envelope");

                var results =
                    new List<PosArticleMutationResult>();
                lock (_gate)
                {
                    foreach (var mutation in envelope.Mutations)
                    {
                        results.Add(
                            ApplyOne(
                                mutation,
                                ref loseResponse));
                    }
                }
                var allCompleted = results.All(
                    item =>
                        string.Equals(
                            item.DeliveryStatus,
                            PosArticleMutationStatusPolicy.Applied,
                            StringComparison.Ordinal) ||
                        string.Equals(
                            item.DeliveryStatus,
                            PosArticleMutationStatusPolicy.DuplicateReplay,
                            StringComparison.Ordinal));
                return new PosArticleMutationResponse
                {
                    Ok = allCompleted,
                    Code = "success",
                    SchemaVersion =
                        PosArticleMutationContract.SchemaVersion,
                    ServerTime = UtcNow(),
                    Results = results.ToArray()
                };
            }

            private PosArticleMutationResult ApplyOne(
                MutationWire mutation,
                ref bool loseResponse)
            {
                Require(
                    mutation != null &&
                    PosArticleMutationIntentPolicy.IsSafeId(
                        mutation.MutationId) &&
                    PosArticleMutationIntentPolicy.IsSafeId(
                        mutation.IdempotencyKey) &&
                    PosArticleMutationIntentPolicy.IsSafeId(
                        mutation.AttemptToken) &&
                    !string.IsNullOrWhiteSpace(
                        mutation.PayloadHash),
                    "server_invalid_mutation");

                if (_replays.TryGetValue(
                        mutation.MutationId,
                        out var replay))
                {
                    Require(
                        string.Equals(
                            replay.PayloadHash,
                            mutation.PayloadHash,
                            StringComparison.Ordinal),
                        "server_replay_hash_mismatch");
                    Interlocked.Increment(
                        ref _duplicateReplayCount);
                    return new PosArticleMutationResult
                    {
                        DeliveryStatus =
                            PosArticleMutationStatusPolicy
                                .DuplicateReplay,
                        Ack = CloneAck(replay.Ack)
                    };
                }

                RemoteProduct product;
                if (string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds.ProductCreate,
                        StringComparison.Ordinal))
                {
                    product = new RemoteProduct
                    {
                        RemoteProductId =
                            Guid.NewGuid().ToString("D"),
                        Active = true
                    };
                    ApplyProductFields(product, mutation.Changes);
                    product.Revision = NextRevision();
                    _products.Add(
                        product.RemoteProductId,
                        product);
                }
                else if (string.Equals(
                    mutation.MutationKind,
                    PosArticleMutationKinds.ProductDuplicate,
                    StringComparison.Ordinal))
                {
                    Require(
                        _products.TryGetValue(
                            mutation.RemoteProductId ??
                            string.Empty,
                            out var source),
                        "server_duplicate_source_missing");
                    Require(
                        string.Equals(
                            source.Revision,
                            mutation.BaseRevision,
                            StringComparison.Ordinal),
                        "server_duplicate_base_mismatch");
                    product = source.Clone();
                    product.RemoteProductId =
                        Guid.NewGuid().ToString("D");
                    product.Active = true;
                    ApplyProductFields(product, mutation.Changes);
                    product.Revision = NextRevision();
                    _products.Add(
                        product.RemoteProductId,
                        product);
                }
                else
                {
                    if (!_products.TryGetValue(
                            mutation.RemoteProductId ??
                            string.Empty,
                            out product))
                    {
                        return Failure(
                            mutation,
                            PosArticleMutationStatusPolicy.FailedConflict);
                    }
                    if (!string.Equals(
                        product.Revision,
                        mutation.BaseRevision,
                        StringComparison.Ordinal))
                    {
                        return Failure(
                            mutation,
                            PosArticleMutationStatusPolicy.FailedConflict);
                    }

                    switch (mutation.MutationKind)
                    {
                        case PosArticleMutationKinds.ProductUpdate:
                            ApplyProductFields(
                                product,
                                mutation.Changes);
                            break;
                        case PosArticleMutationKinds
                            .ProductRetailPriceChange:
                            product.RetailPrice = ReadInt(
                                mutation.Changes,
                                PosArticleMutationFields.Price);
                            break;
                        case PosArticleMutationKinds
                            .ProductPurchasePriceChange:
                            product.PurchasePrice = ReadInt(
                                mutation.Changes,
                                PosArticleMutationFields.Price);
                            break;
                        case PosArticleMutationKinds
                            .ProductManualStockAdjustment:
                            product.StockQuantity = checked(
                                product.StockQuantity +
                                ReadInt(
                                    mutation.Changes,
                                    PosArticleMutationFields
                                        .QuantityDelta));
                            break;
                        case PosArticleMutationKinds.ProductDeactivate:
                            product.Active = false;
                            break;
                        case PosArticleMutationKinds.ProductActivate:
                            product.Active = true;
                            break;
                        default:
                            throw new InvalidOperationException(
                                "server_unknown_mutation_kind");
                    }
                    product.Revision = NextRevision();
                }

                _catalogRevision++;
                _appliedKinds.Add(mutation.MutationKind);
                string priceHistoryId = null;
                string stockMovementId = null;
                if (string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds
                            .ProductRetailPriceChange,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds
                            .ProductPurchasePriceChange,
                        StringComparison.Ordinal))
                {
                    priceHistoryId =
                        Guid.NewGuid().ToString("D");
                    _prices.Add(
                        new RemotePrice
                        {
                            EffectiveAt =
                                mutation.OccurredAt,
                            Price = ReadInt(
                                mutation.Changes,
                                PosArticleMutationFields.Price),
                            PriceId = priceHistoryId,
                            ProductId =
                                product.RemoteProductId,
                            Source = "article_mutation",
                            Type = string.Equals(
                                mutation.MutationKind,
                                PosArticleMutationKinds
                                    .ProductRetailPriceChange,
                                StringComparison.Ordinal)
                                ? "retail"
                                : "purchase"
                        });
                }
                if (string.Equals(
                    mutation.MutationKind,
                    PosArticleMutationKinds
                        .ProductManualStockAdjustment,
                    StringComparison.Ordinal))
                {
                    stockMovementId =
                        Guid.NewGuid().ToString("D");
                }

                var ack = new PosArticleMutationAck
                {
                    AttemptToken = mutation.AttemptToken,
                    AuthoritativeRevision = product.Revision,
                    CatalogRevision = _catalogRevision.ToString(
                        CultureInfo.InvariantCulture),
                    Code = PosArticleMutationStatusPolicy.Applied,
                    IdempotencyKey = mutation.IdempotencyKey,
                    MutationId = mutation.MutationId,
                    PayloadHash = mutation.PayloadHash,
                    PriceHistoryId = priceHistoryId,
                    RemoteProductId = product.RemoteProductId,
                    Retryable = false,
                    SchemaVersion =
                        PosArticleMutationContract.SchemaVersion,
                    ServerTimestamp = product.Revision,
                    Status = PosArticleMutationStatusPolicy.Applied,
                    StockMovementId = stockMovementId,
                    Terminal = true
                };
                _replays.Add(
                    mutation.MutationId,
                    new ReplayRecord
                    {
                        Ack = CloneAck(ack),
                        PayloadHash = mutation.PayloadHash
                    });

                if (!_lostCreateResponse &&
                    string.Equals(
                        mutation.MutationKind,
                        PosArticleMutationKinds.ProductCreate,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        product.Barcode,
                        "LOOP-CREATE-REPLAY",
                        StringComparison.Ordinal))
                {
                    _lostCreateResponse = true;
                    loseResponse = true;
                }

                return new PosArticleMutationResult
                {
                    DeliveryStatus =
                        PosArticleMutationStatusPolicy.Applied,
                    Ack = ack
                };
            }

            private PosArticleMutationResult Failure(
                MutationWire mutation,
                string code)
            {
                return new PosArticleMutationResult
                {
                    DeliveryStatus = code,
                    Ack = new PosArticleMutationAck
                    {
                        AttemptToken = mutation.AttemptToken,
                        AuthoritativeRevision = null,
                        CatalogRevision =
                            _catalogRevision.ToString(
                                CultureInfo.InvariantCulture),
                        Code = code,
                        IdempotencyKey = mutation.IdempotencyKey,
                        MutationId = mutation.MutationId,
                        PayloadHash = mutation.PayloadHash,
                        PriceHistoryId = null,
                        RemoteProductId =
                            mutation.RemoteProductId,
                        Retryable = false,
                        SchemaVersion =
                            PosArticleMutationContract.SchemaVersion,
                        ServerTimestamp = NextRevision(),
                        Status = code,
                        StockMovementId = null,
                        Terminal = true
                    }
                };
            }

            internal string StaffRoleKey = "pos_admin";

            private PosFirstLoginResponse BuildFirstLoginResponse()
            {
                var now = DateTimeOffset.UtcNow;
                return new PosFirstLoginResponse
                {
                    Device = new PosTrustedDeviceResponse
                    {
                        ShopDeviceId = DeviceId,
                        Status = "active",
                        Trusted = true
                    },
                    EffectiveOfflineAuthorizationExpiresAt =
                        now.AddHours(4).ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                    Ok = true,
                    Policy = ValidPolicy(),
                    ServerTime = now.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    Session = new PosSessionResponse
                    {
                        ExpiresAt = now.AddHours(4).ToString(
                            "O",
                            CultureInfo.InvariantCulture),
                        HeartbeatAfterSeconds = 300,
                        PosSessionId = SessionId,
                        SessionToken =
                            "synthetic-loopback-session-token"
                    },
                    Shop = Shop(),
                    Staff = new PosStaffResponse
                    {
                        CredentialVersion = 7,
                        DisplayName =
                            "Synthetic article operator",
                        RoleKey = StaffRoleKey,
                        StaffCode = "ARTICLEPOS",
                        StaffId = StaffId
                    },
                    TrustedDeviceToken =
                        "synthetic-loopback-device-token"
                };
            }

            private PosCatalogPullResponse BuildCatalogResponse()
            {
                lock (_gate)
                {
                    var now = UtcNow();
                    var active = _products.Values
                        .Where(item => item.Active)
                        .OrderBy(
                            item => item.RemoteProductId,
                            StringComparer.Ordinal)
                        .ToArray();
                    var inactive = _products.Values
                        .Where(item => !item.Active)
                        .OrderBy(
                            item => item.RemoteProductId,
                            StringComparer.Ordinal)
                        .ToArray();
                    var response = new PosCatalogPullResponse
                    {
                        Catalog = new PosCatalogPayload
                        {
                            Categories = new[]
                            {
                                new PosCatalogCategoryResponse
                                {
                                    CategoryId = CategoryId,
                                    Name =
                                        "Loopback category",
                                    UpdatedAt = now
                                }
                            },
                            Suppliers = new[]
                            {
                                new PosCatalogSupplierResponse
                                {
                                    Name =
                                        "Loopback supplier",
                                    SupplierId = SupplierId,
                                    UpdatedAt = now
                                }
                            },
                            Products = active
                                .Select(ToCatalogProduct)
                                .ToArray(),
                            Prices = _prices
                                .OrderBy(
                                    item => item.PriceId,
                                    StringComparer.Ordinal)
                                .Select(ToCatalogPrice)
                                .ToArray(),
                            Tombstones =
                                new PosCatalogTombstonesResponse
                                {
                                    Categories =
                                        Array.Empty<
                                            PosCatalogCategoryTombstoneResponse>(),
                                    Suppliers =
                                        Array.Empty<
                                            PosCatalogSupplierTombstoneResponse>(),
                                    Products = inactive
                                        .Select(
                                            item =>
                                                new PosCatalogProductTombstoneResponse
                                                {
                                                    DeletedAt =
                                                        item.Revision,
                                                    ProductId =
                                                        item.RemoteProductId,
                                                    UpdatedAt =
                                                        item.Revision
                                                })
                                        .ToArray()
                                }
                        },
                        CatalogSummary =
                            new PosCatalogSummaryResponse
                            {
                                ActiveProducts =
                                    active.LongLength,
                                Categories = 1,
                                Prices = _prices.Count,
                                Products = _products.Count,
                                Suppliers = 1
                            },
                        CatalogVersion =
                            "article-loopback-v" +
                            _catalogRevision.ToString(
                                CultureInfo.InvariantCulture),
                        Code = "success",
                        GeneratedAt = now,
                        HasMore = false,
                        Ok = true,
                        Policy = ValidPolicy(),
                        SchemaVersion =
                            PosOnlineContract.CatalogPullSchemaVersion,
                        ServerTime = now,
                        Shop = Shop(),
                        SyncCursor =
                            "article-loopback-cursor-" +
                            _cursorScope +
                            _catalogRevision.ToString(
                                CultureInfo.InvariantCulture) +
                            "-" +
                            _catalogRequestCount.ToString(
                                CultureInfo.InvariantCulture),
                        SyncMode = "full_refresh"
                    };
                    if (_authoritativelyEmptyCatalog)
                    {
                        response.Catalog = new PosCatalogPayload
                        {
                            Categories = Array.Empty<PosCatalogCategoryResponse>(), Suppliers = Array.Empty<PosCatalogSupplierResponse>(),
                            Products = Array.Empty<PosCatalogProductResponse>(), Prices = Array.Empty<PosCatalogPriceResponse>(),
                            Tombstones = new PosCatalogTombstonesResponse
                            {
                                Categories = Array.Empty<PosCatalogCategoryTombstoneResponse>(), Suppliers = Array.Empty<PosCatalogSupplierTombstoneResponse>(),
                                Products = Array.Empty<PosCatalogProductTombstoneResponse>()
                            }
                        };
                        response.CatalogSummary = new PosCatalogSummaryResponse { Products = 0, ActiveProducts = 0, Categories = 0, Suppliers = 0, Prices = 0 };
                    }
                    if (_emptyTerminalDelta)
                    {
                        response.CatalogSummary = null;
                        response.SyncMode = "delta";
                    }
                    return response;
                }
            }

            private string NextRevision()
            {
                var ticks = Interlocked.Increment(
                    ref _revisionCounter);
                return DateTimeOffset.UtcNow
                    .AddTicks(ticks)
                    .ToUniversalTime()
                    .ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                        CultureInfo.InvariantCulture);
            }

            private static PosShopResponse Shop()
            {
                return new PosShopResponse
                {
                    ShopCode = ShopCode,
                    ShopId = ShopId,
                    ShopName =
                        "Article mutation loopback shop",
                    ShopStatus = "active",
                    Source = "qa_loopback",
                    UpdatedAt = UtcNow()
                };
            }

            private static PosPolicyResponse ValidPolicy()
            {
                return new PosPolicyResponse
                {
                    Capabilities =
                        new PosPolicyCapabilitiesResponse
                        {
                            CatalogPull =
                                PosOnlineContract
                                    .CatalogCapabilityVersion,
                            FiscalDocumentMode =
                                "local_receipt_redacted",
                            LocalReceiptPrinting = true,
                            LocalStaffMirror =
                                "current_staff_only",
                            OfflineSales = true,
                            PaymentMethods = new[]
                            {
                                PosOnlineContract.PaymentCash,
                                PosOnlineContract.PaymentCard,
                                PosOnlineContract.PaymentOther
                            },
                            SalesSync =
                                PosOnlineContract
                                    .SalesSchemaVersion
                        },
                    ContractVersion =
                        PosOnlineContract.PolicyContractVersion,
                    Limitations = new[]
                    {
                        "first_activation_requires_online"
                    },
                    OfflinePolicy =
                        new PosOfflinePolicyResponse
                        {
                            FirstActivationRequiresOnline =
                                true,
                            Mode =
                                "offline_first_after_activation",
                            PendingSalesRetention =
                                "local_outbox_until_server_ack",
                            RevocationEnforcement =
                                "next_online_check"
                        },
                    PaymentPolicy =
                        new PosPaymentPolicyResponse
                        {
                            Currency = "CLP",
                            FallbackMethod =
                                PosOnlineContract.PaymentOther,
                            SupportedMethods = new[]
                            {
                                PosOnlineContract.PaymentCash,
                                PosOnlineContract.PaymentCard,
                                PosOnlineContract.PaymentOther
                            },
                            UnsupportedMethods =
                                new[] { "transfer" }
                        },
                    StaffPolicy =
                        new PosStaffPolicyResponse
                        {
                            CredentialMaterial =
                                "not_synced",
                            MustChangeCredential =
                                "online_required",
                            OfflineMirror =
                                "current_staff_only"
                        },
                    TaxPolicy = new PosTaxPolicyResponse
                    {
                        DefaultTaxClp = 0,
                        FiscalAuthorityIntegration =
                            "not_configured",
                        Status = "not_configured"
                    }
                };
            }

            private static PosCatalogProductResponse ToCatalogProduct(
                RemoteProduct product)
            {
                return new PosCatalogProductResponse
                {
                    Barcode = product.Barcode,
                    CategoryId = product.CategoryId,
                    ItemNumber = product.ItemNumber,
                    ProductId = product.RemoteProductId,
                    ProductName = product.PrimaryName,
                    PurchasePrice = product.PurchasePrice,
                    RetailPrice = product.RetailPrice,
                    SecondProductName =
                        product.SecondaryName,
                    StockQuantity = product.StockQuantity,
                    SupplierId = product.SupplierId,
                    UpdatedAt = product.Revision
                };
            }

            private static PosCatalogPriceResponse ToCatalogPrice(
                RemotePrice price)
            {
                return new PosCatalogPriceResponse
                {
                    EffectiveAt = price.EffectiveAt,
                    Price = price.Price,
                    PriceId = price.PriceId,
                    ProductId = price.ProductId,
                    Source = price.Source,
                    Type = price.Type
                };
            }

            private static void ApplyProductFields(
                RemoteProduct product,
                IDictionary<string, object> changes)
            {
                if (Has(changes, PosArticleMutationFields.Barcode))
                {
                    product.Barcode = ReadString(
                        changes,
                        PosArticleMutationFields.Barcode);
                }
                if (Has(changes, PosArticleMutationFields.ItemNumber))
                {
                    product.ItemNumber = ReadNullableString(
                        changes,
                        PosArticleMutationFields.ItemNumber);
                }
                if (Has(changes, PosArticleMutationFields.PrimaryName))
                {
                    product.PrimaryName = ReadString(
                        changes,
                        PosArticleMutationFields.PrimaryName);
                }
                if (Has(changes, PosArticleMutationFields.SecondaryName))
                {
                    product.SecondaryName = ReadNullableString(
                        changes,
                        PosArticleMutationFields.SecondaryName);
                }
                if (Has(changes, PosArticleMutationFields.CategoryId))
                {
                    product.CategoryId = ReadNullableString(
                        changes,
                        PosArticleMutationFields.CategoryId);
                }
                if (Has(changes, PosArticleMutationFields.SupplierId))
                {
                    product.SupplierId = ReadNullableString(
                        changes,
                        PosArticleMutationFields.SupplierId);
                }
                if (Has(changes, PosArticleMutationFields.RetailPrice))
                {
                    product.RetailPrice = ReadInt(
                        changes,
                        PosArticleMutationFields.RetailPrice);
                }
                if (Has(changes, PosArticleMutationFields.PurchasePrice))
                {
                    product.PurchasePrice = ReadInt(
                        changes,
                        PosArticleMutationFields.PurchasePrice);
                }
                if (Has(changes, PosArticleMutationFields.StockQuantity))
                {
                    product.StockQuantity = ReadInt(
                        changes,
                        PosArticleMutationFields.StockQuantity);
                }
                Require(
                    !string.IsNullOrWhiteSpace(product.Barcode) &&
                    !string.IsNullOrWhiteSpace(product.PrimaryName),
                    "server_product_fields_incomplete");
            }

            private static bool Has(
                IDictionary<string, object> changes,
                string key)
            {
                return changes != null &&
                    changes.ContainsKey(key);
            }

            private static string ReadString(
                IDictionary<string, object> changes,
                string key)
            {
                var value = ReadNullableString(changes, key);
                Require(
                    !string.IsNullOrWhiteSpace(value),
                    "server_missing_string_" + SafeCode(key));
                return value;
            }

            private static string ReadNullableString(
                IDictionary<string, object> changes,
                string key)
            {
                object value = null;
                Require(
                    changes != null &&
                    changes.TryGetValue(key, out value),
                    "server_missing_change_" + SafeCode(key));
                return value == null
                    ? null
                    : Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture);
            }

            private static int ReadInt(
                IDictionary<string, object> changes,
                string key)
            {
                object value = null;
                Require(
                    changes != null &&
                    changes.TryGetValue(key, out value) &&
                    value != null,
                    "server_missing_number_" + SafeCode(key));
                return Convert.ToInt32(
                    value,
                    CultureInfo.InvariantCulture);
            }

            private static PosArticleMutationAck CloneAck(
                PosArticleMutationAck ack)
            {
                return new PosArticleMutationAck
                {
                    AttemptToken = ack.AttemptToken,
                    AuthoritativeRevision =
                        ack.AuthoritativeRevision,
                    CatalogRevision = ack.CatalogRevision,
                    Code = ack.Code,
                    IdempotencyKey = ack.IdempotencyKey,
                    MutationId = ack.MutationId,
                    PayloadHash = ack.PayloadHash,
                    PriceHistoryId = ack.PriceHistoryId,
                    RemoteProductId = ack.RemoteProductId,
                    Retryable = ack.Retryable,
                    SchemaVersion = ack.SchemaVersion,
                    ServerTimestamp = ack.ServerTimestamp,
                    Status = ack.Status,
                    StockMovementId = ack.StockMovementId,
                    Terminal = ack.Terminal
                };
            }
        }

        [DataContract]
        private sealed class MutationEnvelopeWire
        {
            [DataMember(Name = "schemaVersion")]
            public string SchemaVersion { get; set; }
            [DataMember(Name = "appVersion")]
            public string AppVersion { get; set; }
            [DataMember(Name = "shopId")]
            public string ShopId { get; set; }
            [DataMember(Name = "shopDeviceId")]
            public string ShopDeviceId { get; set; }
            [DataMember(Name = "staffId")]
            public string StaffId { get; set; }
            [DataMember(Name = "staffCredentialVersion")]
            public int StaffCredentialVersion { get; set; }
            [DataMember(Name = "posSessionId")]
            public string PosSessionId { get; set; }
            [DataMember(Name = "deviceToken")]
            public string DeviceToken { get; set; }
            [DataMember(Name = "sessionToken")]
            public string SessionToken { get; set; }
            [DataMember(Name = "mutations")]
            public MutationWire[] Mutations { get; set; }
        }

        [DataContract]
        private sealed class MutationWire
        {
            [DataMember(Name = "mutationId")]
            public string MutationId { get; set; }
            [DataMember(Name = "idempotencyKey")]
            public string IdempotencyKey { get; set; }
            [DataMember(Name = "payloadHash")]
            public string PayloadHash { get; set; }
            [DataMember(Name = "attemptToken")]
            public string AttemptToken { get; set; }
            [DataMember(Name = "mutationKind")]
            public string MutationKind { get; set; }
            [DataMember(Name = "clientProductId")]
            public string ClientProductId { get; set; }
            [DataMember(Name = "remoteProductId")]
            public string RemoteProductId { get; set; }
            [DataMember(Name = "baseRevision")]
            public string BaseRevision { get; set; }
            [DataMember(Name = "localSequence")]
            public long LocalSequence { get; set; }
            [DataMember(Name = "fieldMask")]
            public string[] FieldMask { get; set; }
            [DataMember(Name = "changes")]
            public Dictionary<string, object> Changes { get; set; }
            [DataMember(Name = "createdAt")]
            public string CreatedAt { get; set; }
            [DataMember(Name = "occurredAt")]
            public string OccurredAt { get; set; }
        }

        private sealed class RemoteProduct
        {
            public string RemoteProductId { get; set; }
            public string Barcode { get; set; }
            public string ItemNumber { get; set; }
            public string PrimaryName { get; set; }
            public string SecondaryName { get; set; }
            public string CategoryId { get; set; }
            public string SupplierId { get; set; }
            public int RetailPrice { get; set; }
            public int PurchasePrice { get; set; }
            public int StockQuantity { get; set; }
            public bool Active { get; set; }
            public string Revision { get; set; }

            public RemoteProduct Clone()
            {
                return new RemoteProduct
                {
                    RemoteProductId = RemoteProductId,
                    Barcode = Barcode,
                    ItemNumber = ItemNumber,
                    PrimaryName = PrimaryName,
                    SecondaryName = SecondaryName,
                    CategoryId = CategoryId,
                    SupplierId = SupplierId,
                    RetailPrice = RetailPrice,
                    PurchasePrice = PurchasePrice,
                    StockQuantity = StockQuantity,
                    Active = Active,
                    Revision = Revision
                };
            }
        }

        private sealed class RemotePrice
        {
            public string EffectiveAt { get; set; }
            public int Price { get; set; }
            public string PriceId { get; set; }
            public string ProductId { get; set; }
            public string Source { get; set; }
            public string Type { get; set; }
        }

        private sealed class ReplayRecord
        {
            public PosArticleMutationAck Ack { get; set; }
            public string PayloadHash { get; set; }
        }

        private sealed class RequestData
        {
            public string RequestLine { get; set; }
            public byte[] Body { get; set; }
        }

        private static T Deserialize<T>(byte[] bytes)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(T),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                });
            using (var stream = new MemoryStream(
                bytes ?? Array.Empty<byte>()))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        private static byte[] Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        private static async Task<RequestData> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var bytes = new List<byte>();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        0,
                        buffer.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;
                bytes.AddRange(buffer.Take(read));
                headerEnd = IndexOfHeaderEnd(bytes);
            }
            Require(headerEnd >= 0, "loopback_http_headers_incomplete");

            var headers = Encoding.ASCII.GetString(
                bytes.Take(headerEnd).ToArray());
            var contentLength = 0;
            foreach (var line in headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 &&
                    string.Equals(
                        line.Substring(0, separator).Trim(),
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(
                        line.Substring(separator + 1).Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out contentLength);
                }
            }

            var bodyStart = headerEnd + 4;
            while (bytes.Count - bodyStart < contentLength)
            {
                var read = await stream.ReadAsync(
                        buffer,
                        0,
                        buffer.Length,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;
                bytes.AddRange(buffer.Take(read));
            }
            Require(
                bytes.Count - bodyStart >= contentLength,
                "loopback_http_body_incomplete");
            var requestLine = headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None)[0];
            return new RequestData
            {
                RequestLine = requestLine,
                Body = bytes
                    .Skip(bodyStart)
                    .Take(contentLength)
                    .ToArray()
            };
        }

        private static int IndexOfHeaderEnd(
            IList<byte> bytes)
        {
            for (var index = 3; index < bytes.Count; index++)
            {
                if (bytes[index - 3] == 13 &&
                    bytes[index - 2] == 10 &&
                    bytes[index - 1] == 13 &&
                    bytes[index] == 10)
                {
                    return index - 3;
                }
            }
            return -1;
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            byte[] body,
            CancellationToken cancellationToken)
        {
            var reason = statusCode == 200
                ? "OK"
                : statusCode == 500
                    ? "Internal Server Error"
                    : "Not Found";
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " +
                statusCode.ToString(
                    CultureInfo.InvariantCulture) +
                " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " +
                body.Length.ToString(
                    CultureInfo.InvariantCulture) +
                "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(
                    header,
                    0,
                    header.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync(
                    body,
                    0,
                    body.Length,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
