using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // A separate functional process. The original historical Smoke keeps its
    // eight cases, 52 checks and 60-second body budget unchanged.
    internal sealed class HistoricalImportGuidedUxSmoke
    {
        private readonly string _data;
        private readonly string _detail;
        private readonly CancellationToken _token;
        private int _checks;
        private int _failedCases;
        private readonly HashSet<string> _observations=new HashSet<string>(StringComparer.Ordinal);
        private HistoricalImportGuidedUxSmoke(string data,CancellationToken token,bool web)
        { _data=data;_detail=Path.Combine(data,web?"historical-web-import-ux-checks.txt":"historical-import-guided-ux-checks.txt");_token=token; }

        internal static Task<string> RunAsync(string data)=>RunSuiteAsync(data,false);
        internal static Task<string> RunWebAsync(string data)=>RunSuiteAsync(data,true);
        private static async Task<string> RunSuiteAsync(string data,bool web)
        {
            var scenario=web?"HISTORY_WEB_IMPORT_UX":"HISTORY_IMPORT_GUIDED_UX";
            TypedQaDiagnosticWriter.BeginScenario(scenario,"functional-completion");
            var language=PosLocalization.Current.CurrentLanguage;var culture=Thread.CurrentThread.CurrentCulture;
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(52)))
            {
                var probe=new HistoricalImportGuidedUxSmoke(data,deadline.Token,web);
                HistoricalSalesFoundationRunner.Fixture.ReuseEmptySchemaForSmoke=true;
                try
                {
                    PosLocalization.Current.SetLanguage("en");Thread.CurrentThread.CurrentCulture=CultureInfo.InvariantCulture;
                    if(web)await probe.RunCaseAsync("web_writer_http_controls",probe.WebWriterHttpControlsAsync);
                    else
                    {
                    await probe.RunCaseAsync("operation_states",probe.OperationStatesAsync);
                    await probe.RunCaseAsync("failed_baseline_retry",probe.FailedBaselineRetryAsync);
                    await probe.RunCaseAsync("late_authority",probe.LateAnalysisAfterAuthorityChangeAsync);
                    await probe.RunCaseAsync("late_qualified_response",probe.LateQualifiedResponseAsync);
                    await probe.RunCaseAsync("qualified_preview_scope",probe.QualifiedPreviewAndScopeAsync);
                    await probe.RunCaseAsync("already_imported",probe.AlreadyImportedAsync);
                    await probe.RunCaseAsync("report_coverage",probe.ReportCoverageAsync);
                    }
                    var required=web?new[]{"server_denied_never_publishes_analysis","web_prepare_pending_is_terminal_without_commit","web_review_uses_http_and_only_web_proofs",
                        "active_local_owner_does_not_bypass_web_review","web_lost_ack_retains_single_financial_write","web_receipt_recovery_never_recommits",
                        "web_duplicate_review_is_noop","missing_web_proofs_block_without_csharp_fallback"}:new[]{"new_file_clears_import_status","refresh_terminates_working","retry_requires_explicit_command","retry_lost_ack_preserves_single_intent","retry_stale_generation_retains_readable_local_history","nonretryable_baseline_has_no_retry_action",
                        "modern_clp_does_not_require_legacy_checkbox","unresolved_time_keeps_preview_and_blocks_confirm",
                        "declined_only_is_terminal_and_not_income","late_authority_callback_cannot_publish",
                        "late_qualified_response_cannot_publish",
                        "qualified_modern_preview_uses_server_evidence","new_file_invalidates_qualified_plan",
                        "out_of_scope_file_cannot_borrow_proof","revoked_provider_proof_cannot_prepare",
                        "already_imported_file_has_no_second_write","undone_file_is_not_reported_as_already_registered","empty_calendar_is_not_loaded_data",
                        "available_history_survives_missing_native_policy","unobserved_days_are_not_zero_rows"};
                    probe.Check("required_paths_executed",required.All(probe._observations.Contains));
                    TypedQaDiagnosticWriter.CompleteScenario(probe._failedCases==0);
                    return (probe._failedCases==0?"PASS":"FAIL")+" "+scenario+" checks="+probe._checks.ToString(CultureInfo.InvariantCulture);
                }
                catch(Exception error)
                {
                    TypedQaDiagnosticWriter.CompleteScenario(false,error);
                    File.AppendAllText(probe._detail,"FAIL scenario "+error.GetType().Name+Environment.NewLine);
                    return "FAIL "+scenario+" checks="+probe._checks.ToString(CultureInfo.InvariantCulture);
                }
                finally
                {
                    HistoricalSalesFoundationRunner.Fixture.ReleaseSmokeTemplate();
                    PosLocalization.Current.SetLanguage(language);Thread.CurrentThread.CurrentCulture=culture;
                }
            }
        }

        private async Task RunCaseAsync(string id,Func<Task> run)
        {
            if(_token.IsCancellationRequested){_failedCases++;File.AppendAllText(_detail,"NOT_RUN "+id+Environment.NewLine);return;}
            var clock=System.Diagnostics.Stopwatch.StartNew();
            try{await run();File.AppendAllText(_detail,"PASS case_"+id+Environment.NewLine);}
            catch(Exception error)
            {
                _failedCases++;TypedQaDiagnosticWriter.RecordException(error);
                File.AppendAllText(_detail,"FAIL case_"+id+" "+error.GetType().Name+Environment.NewLine);
            }
            finally{File.AppendAllText(_detail,"TIMING case_"+id+" duration_ms="+clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)+Environment.NewLine);}
        }

        private void Check(string id,bool condition,[CallerFilePath]string sourceFile="",[CallerLineNumber]int sourceLine=0)
        {
            _token.ThrowIfCancellationRequested();_checks++;
            _observations.Add(id);
            TypedQaDiagnosticWriter.RecordAssertion(condition,sourceFile:sourceFile,sourceLine:sourceLine,observationId:id);
            File.AppendAllText(_detail,(condition?"PASS ":"FAIL ")+id+Environment.NewLine);
            if(!condition)throw new InvalidOperationException("qa_guided_import_"+id);
        }
        private static string T(string key)=>PosLocalization.T("history."+key);
        private string Source(string root,string name,params string[][] rows)
        {
            var path=Path.Combine(root,name+".xlsx");
            HistoricalSalesFixtureWriter.Write(path,HistoricalSalesFixtureWriter.ModernHeaders,rows);return path;
        }
        private async Task PumpAsync()
        { _token.ThrowIfCancellationRequested();await Dispatcher.Yield(DispatcherPriority.ContextIdle);_token.ThrowIfCancellationRequested(); }
        private async Task UntilAsync(Func<bool> predicate)
        {
            var wait=System.Diagnostics.Stopwatch.StartNew();
            while(!predicate())
            {
                _token.ThrowIfCancellationRequested();
                if(wait.ElapsedMilliseconds>=8000)throw new TimeoutException("qa_guided_import_state_timeout");
                await Task.Delay(10,_token);await PumpAsync();
            }
            await PumpAsync();
        }
        private void Capture(string name,FrameworkElement view)
        {
            view.UpdateLayout();
            var transform=PresentationSource.FromVisual(view)?.CompositionTarget?.TransformToDevice??Matrix.Identity;
            var width=checked((int)Math.Ceiling(view.ActualWidth*transform.M11));
            var height=checked((int)Math.Ceiling(view.ActualHeight*transform.M22));
            Check("synthetic_capture_has_bounded_visible_surface",view.IsVisible&&width>0&&height>0&&(long)width*height<=16000000);
            var bitmap=new RenderTargetBitmap(width,height,96*transform.M11,96*transform.M22,PixelFormats.Pbgra32);
            var surface=(view as Control)?.Background??(view as Panel)?.Background??Window.GetWindow(view)?.Background;
            var composite=new DrawingVisual();
            using(var drawing=composite.RenderOpen())
            {
                // Render the owned window's actual opaque surface behind a
                // transparent UserControl, so an exported PNG matches the view.
                drawing.DrawRectangle(surface,null,new Rect(0,0,view.ActualWidth,view.ActualHeight));
                drawing.DrawRectangle(new VisualBrush(view),null,new Rect(0,0,view.ActualWidth,view.ActualHeight));
            }
            bitmap.Render(composite);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=new FileStream(Path.Combine(_data,name+".png"),FileMode.CreateNew,FileAccess.Write,FileShare.None))encoder.Save(file);
        }
        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
        {
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is T match)yield return match;
                foreach(var descendant in Descendants<T>(child))yield return descendant;
            }
        }
        private async Task SelectTabAsync(DependencyObject view,int index)
        {
            var tabs=Descendants<TabControl>(view).First();tabs.SelectedIndex=index;await PumpAsync();
        }
        private async Task InvokeAsync(FrameworkElement view,ICommand command,Func<bool> completed)
        {
            var button=Descendants<Button>(view).Single(b=>ReferenceEquals(b.Command,command));
            button.BringIntoView();await PumpAsync();view.UpdateLayout();
            Check("command_control_enabled",button.IsVisible&&button.IsEnabled&&command.CanExecute(null));
            // The actual WPF peer invokes ButtonBase.OnClick and its bound command.
            // It does not call the view-model command or set the analysis/plan.
            var provider=(IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
            provider.Invoke();await PumpAsync();await UntilAsync(completed);
        }
        private async Task ChooseAsync(HistoricalSalesImportDialog dialog,HistoricalSalesImportViewModel model,string path)
        {
            model.SelectFilesForTesting=owner=>new[]{path};
            await SelectTabAsync(dialog,0);
            await InvokeAsync(dialog,model.SelectCommand,()=>!model.IsBusy);
        }
        private async Task AnalyzeAsync(HistoricalSalesImportDialog dialog,HistoricalSalesImportViewModel model)
            =>await InvokeAsync(dialog,model.AnalyzeCommand,()=>!model.IsBusy);

        private async Task OperationStatesAsync()
        {
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(_data))
            {
                var service=f.CreateWorkflow("offline",initialBaseline:false);
                using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                {
                    var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false};owner.Show();
                    var dialog=new HistoricalSalesImportDialog(model){Owner=owner};
                    try
                    {
                        dialog.Show();await PumpAsync();
                        await SelectTabAsync(dialog,2);
                        await InvokeAsync(dialog,model.SyncCloudCommand,()=>!model.IsBusy);
                        var transferFailure=model.TransferStatus;
                        Check("transfer_failure_has_terminal_outcome",transferFailure.Length>0&&transferFailure!=T("working"));
                        var source=Source(f.Root,"guided-modern",HistoricalSalesFixtureWriter.ModernRow("guided-modern",1000));
                        var before=f.State();
                        await ChooseAsync(dialog,model,source);
                        Check("new_file_clears_import_status",model.ImportStatus.Length==0&&model.Status.Length==0&&model.Summary.Length==0);
                        Check("transfer_status_stays_in_its_tab",model.TransferStatus==transferFailure);
                        await AnalyzeAsync(dialog,model);
                        Check("modern_clp_does_not_require_legacy_checkbox",!model.LegacyClpConfirmed&&model.Summary.Contains("sales 1")&&!model.ImportStatus.Contains(T("errors")));
                        Check("unresolved_time_keeps_preview_and_blocks_confirm",model.CoverageDetails.Length>0&&model.ImportStatus==T("needsEvidence")&&!model.ApplyCommand.CanExecute(null));
                        Check("missing_requirement_and_remedy_are_visible",model.RequirementsText.Contains(T("requireMerchant"))&&model.RequirementsText.Contains(T("requirementsAction"))&&
                            BoundTextIsFullyVisible(dialog,"RequirementsText"));
                        Capture("guided-import-unresolved-synthetic",dialog);
                        Check("analysis_does_not_write_ledger",f.State()==before);
                        await SelectTabAsync(dialog,1);
                        await InvokeAsync(dialog,model.RefreshBatchesCommand,()=>!model.IsBusy);
                        Check("refresh_terminates_working",model.BatchesStatus==T("operationComplete")&&model.Status==model.BatchesStatus);
                        await SelectTabAsync(dialog,0);
                        Check("refresh_does_not_replace_import_status",model.Status==T("needsEvidence"));
                        var declined=Source(f.Root,"guided-declined",HistoricalSalesFixtureWriter.ModernRow("",1000,rejected:true));
                        await ChooseAsync(dialog,model,declined);await AnalyzeAsync(dialog,model);
                        Check("declined_only_is_terminal_and_not_income",model.ImportStatus==T("declinedOnly")&&model.ImportStepText==T("stepResult")&&!model.ApplyCommand.CanExecute(null)&&f.State()==before);
                        Capture("guided-import-declined-synthetic",dialog);
                        dialog.UpdateLayout();
                        Check("guided_dialog_fits_current_work_area",OperatorDialogGeometrySmoke.SurfaceFitsWorkArea(dialog));
                    }
                    finally{dialog.Close();owner.Close();}
                }
            }
        }

        private async Task FailedBaselineRetryAsync()
        {
            foreach(var reason in new[]{"baseline_job_failed","baseline_review_denied"})
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(_data))
            {
                var service=f.CreateWorkflow("offline",initialBaseline:false);
                var state=await f.Service.ReadDatasetAsync(_token);
                await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(f.Generation,1);
                Action demand=()=>f.Factory.AccessContext.DemandPath(f.Factory.DbPath);
                var capture=typeof(OperatorSession).GetMethod("CaptureHistoricalCloudExportGuard",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var guard=(HistoricalCloudExportGuard)capture.Invoke(f.Operator,new object[]{f.ShopId,state.DatasetId,state.DataRevision,f.Factory.AuthorityStamp.RestoreEpoch,demand});
                var exportRoot=Path.Combine(Path.GetTempPath(),"r-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(exportRoot);
                var archive=Path.Combine(exportRoot,"failed-history.w7history.zip");
                var exported=await new HistoricalCloudExporter(f.Factory).ExportBaselineAsync(archive,Path.Combine(exportRoot,"failed-history.db"),guard,Guid.Parse(state.RestoreLineageId).ToString("D"),_token);
                var store=new HistoricalBaselineStore(f.Factory,f.Generation,demand);
                var flow=await store.SavePreparedAsync(exported,"90000000-0000-4000-8000-000000003019",1,guard,_token);
                await store.SaveUploadAsync(flow.FlowId,Guid.NewGuid().ToString("D"),_token);await store.SaveReadyAsync(flow.FlowId,_token);
                await store.SaveRunAsync(flow.FlowId,Guid.NewGuid().ToString("D"),_token);await store.MarkAttentionAsync(flow.FlowId,reason,_token);
                var facts=f.State();
                long IntentCount(){using(var read=f.Factory.Open())return read.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_baseline_retry");}
                using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                {
                    var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false};owner.Show();var dialog=new HistoricalSalesImportDialog(model){Owner=owner};
                    try
                    {
                        dialog.Show();await PumpAsync();await SelectTabAsync(dialog,1);
                        await InvokeAsync(dialog,model.RefreshBatchesCommand,()=>!model.IsBusy);await SelectTabAsync(dialog,2);
                        Check("failed_baseline_does_not_claim_replica_complete",model.ReceptionStatus==T("baselineReceptionPending")&&model.BaselineStatus==T("baselineInterrupted"));
                        if(reason!="baseline_job_failed")
                        {Check("nonretryable_baseline_has_no_retry_action",!model.RetryBaselineCommand.CanExecute(null)&&model.BaselineRetryVisibility==Visibility.Collapsed);continue;}
                        Check("retry_requires_explicit_command",IntentCount()==0&&model.RetryBaselineCommand.CanExecute(null));
                        await InvokeAsync(dialog,model.RetryBaselineCommand,()=>!model.IsBusy);
                        Check("retry_lost_ack_preserves_single_intent",IntentCount()==1&&!model.RetryBaselineCommand.CanExecute(null)&&model.TransferStatus==T("baselineRetryRequested")&&model.BaselineStatus==T("baselineRetryPending")&&f.State()==facts);
                        Capture("guided-baseline-retry-pending-synthetic",dialog);
                        var old=f.Generation;var next=new OnlineSyncGeneration(Guid.NewGuid().ToString("D"),Guid.NewGuid().ToString("D"),old.ShopDeviceId,old.ShopId,old.ShopCode,old.StaffId,old.StaffCredentialVersion);
                        await new OnlineSyncGenerationRepository(f.Factory).ActivateAndRecoverAsync(next,2);
                        await SelectTabAsync(dialog,1);await InvokeAsync(dialog,model.RefreshBatchesCommand,()=>!model.IsBusy);
                        Check("retry_stale_generation_retains_readable_local_history",model.BatchesStatus==T("operationComplete")&&!model.RetryBaselineCommand.CanExecute(null)&&IntentCount()==1&&f.State()==facts);
                        f.Operator.Logout();await PumpAsync();Check("retry_authority_loss_hides_action",!model.RetryBaselineCommand.CanExecute(null)&&model.BaselineRetryVisibility==Visibility.Collapsed&&model.BaselineStatus.Length==0);
                    }
                    finally{dialog.Close();owner.Close();}
                }
            }
        }

        private async Task LateAnalysisAfterAuthorityChangeAsync()
        {
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(_data))
            using(var release=new ManualResetEventSlim())
            {
                var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var service=f.CreateWorkflow();
                service.SetHistoricalAnalyzerFactoryForTesting(()=>new HistoricalSalesImportAnalyzer(token=>
                { entered.TrySetResult(true);release.Wait(_token); }));
                using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                {
                    var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false};owner.Show();
                    var dialog=new HistoricalSalesImportDialog(model){Owner=owner};
                    try
                    {
                        dialog.Show();await PumpAsync();
                        var source=Source(f.Root,"guided-late",HistoricalSalesFixtureWriter.ModernRow("guided-late",1000));
                        await ChooseAsync(dialog,model,source);
                        var button=Descendants<Button>(dialog).Single(b=>ReferenceEquals(b.Command,model.AnalyzeCommand));
                        Check("late_analysis_starts_from_enabled_control",button.IsEnabled&&button.IsVisible);
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        await UntilAsync(()=>entered.Task.IsCompleted&&model.IsBusy);
                        f.Operator.Logout();await PumpAsync();release.Set();
                        await UntilAsync(()=>!model.IsBusy);
                        Check("late_authority_callback_cannot_publish",!model.ImportReadAuthorityCurrent&&model.Summary.Length==0&&model.CoverageDetails.Length==0&&model.ImportStatus==T("authorizationChanged")&&!model.ApplyCommand.CanExecute(null));
                    }
                    finally
                    {
                        release.Set();
                        var closeClock=System.Diagnostics.Stopwatch.StartNew();
                        while(model.IsBusy&&closeClock.ElapsedMilliseconds<2000){await Task.Delay(10);await Dispatcher.Yield(DispatcherPriority.ContextIdle);}
                        dialog.Close();owner.Close();
                    }
                }
            }
        }

        private async Task AlreadyImportedAsync()
        {
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(_data))
            {
                var source=Path.Combine(f.Root,"guided-existing.xlsx");
                HistoricalSalesFixtureWriter.Write(source,HistoricalSalesFixtureWriter.LegacyHeaders,new[]{HistoricalSalesFixtureWriter.LegacyRow("guided-existing",1000)});
                var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions{LegacyClpConfirmed=true});
                var registered=await f.ApplyAsync(await f.PreparePlanAsync(analysis));
                var service=f.CreateWorkflow();var before=f.State();
                using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                {
                    var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false};owner.Show();
                    var dialog=new HistoricalSalesImportDialog(model){Owner=owner};
                    try
                    {
                        dialog.Show();await PumpAsync();
                        await ChooseAsync(dialog,model,source);await AnalyzeAsync(dialog,model);
                        Check("already_imported_file_has_no_second_write",model.ImportStatus==T("alreadyImported")&&model.ImportStepText==T("stepResult")&&!model.ApplyCommand.CanExecute(null)&&f.State()==before&&f.LocalWriteRequests==0);
                        await f.UndoAsync(registered);await PumpAsync();await UntilAsync(()=>!model.IsBusy);
                        var undone=f.State();await ChooseAsync(dialog,model,source);await AnalyzeAsync(dialog,model);
                        Check("undone_file_is_not_reported_as_already_registered",model.ImportStatus==T("needsEvidence")&&model.ImportStepText==T("stepVerify")&&
                            model.RequirementsText.Contains(T("requireMerchant"))&&!model.ApplyCommand.CanExecute(null)&&f.State()==undone&&f.LocalWriteRequests==0);
                    }
                    finally{dialog.Close();owner.Close();}
                }
            }
        }

        private async Task LateQualifiedResponseAsync()
        {
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(_data))
            {
                var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var response=new TaskCompletionSource<HistoricalCloudResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var source=Source(f.Root,"guided-late-config",HistoricalSalesFixtureWriter.ModernRow("guided-late-config",1000));
                var config=new HistoricalQualifiedConfigFixture(f.ShopId,new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions()));
                var service=f.CreateWorkflow();
                service.SetHistoricalCloudRequestForTesting(async(actor,operation,payload,ct)=>
                {
                    if(operation!="qualified_config")throw new InvalidOperationException("qa_guided_unexpected_qualified_operation");
                    actor.DemandCurrent();entered.TrySetResult(true);var result=await response.Task.ConfigureAwait(false);ct.ThrowIfCancellationRequested();
                    return PosOnlineResult<HistoricalCloudResponse>.Ok(new HistoricalCloudResponse
                    {Ok=true,ShopId=actor.Trusted.ShopId,DeviceId=actor.Trusted.ShopDeviceId,SessionId=actor.Trusted.PosSessionId,Result=result});
                });
                using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                {
                    var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false};owner.Show();
                    var dialog=new HistoricalSalesImportDialog(model){Owner=owner};
                    try
                    {
                        dialog.Show();await PumpAsync();await ChooseAsync(dialog,model,source);
                        var button=Descendants<Button>(dialog).Single(b=>ReferenceEquals(b.Command,model.AnalyzeCommand));
                        Check("metadata_read_starts_from_enabled_control",button.IsEnabled&&button.IsVisible);
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        await UntilAsync(()=>entered.Task.IsCompleted&&model.IsBusy);
                        f.Operator.Logout();await PumpAsync();response.TrySetResult(config.Read());await UntilAsync(()=>!model.IsBusy);
                        Check("late_qualified_response_cannot_publish",!model.ImportReadAuthorityCurrent&&model.Summary.Length==0&&model.CoverageDetails.Length==0&&
                            model.RequirementsText.Length==0&&model.ImportStatus==T("authorizationChanged")&&!model.ApplyCommand.CanExecute(null));
                    }
                    finally
                    {
                        response.TrySetResult(config.Read());
                        var closeClock=System.Diagnostics.Stopwatch.StartNew();
                        while(model.IsBusy&&closeClock.ElapsedMilliseconds<2000){await Task.Delay(10);await Dispatcher.Yield(DispatcherPriority.ContextIdle);}
                        dialog.Close();owner.Close();
                    }
                }
            }
        }

        private async Task QualifiedPreviewAndScopeAsync()
        {
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(_data))
            {
                var source=Source(f.Root,"guided-qualified",HistoricalSalesFixtureWriter.ModernRow("guided-qualified",1000));
                var config=new HistoricalQualifiedConfigFixture(f.ShopId,new HistoricalSalesImportAnalyzer().Analyze(new[]{source},new HistoricalSalesImportOptions()));
                var service=f.CreateWorkflow(qualifiedConfig:config.Read);var before=f.State();
                using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                {
                    var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false};owner.Show();
                    var dialog=new HistoricalSalesImportDialog(model){Owner=owner};
                    try
                    {
                        dialog.Show();await PumpAsync();await ChooseAsync(dialog,model,source);await AnalyzeAsync(dialog,model);
                        Check("qualified_modern_preview_uses_server_evidence",!model.LegacyClpConfirmed&&model.ImportStatus==T("analysisReady")&&
                            model.RequirementsText==T("requirementsReady")&&model.PreviewCommand.CanExecute(null)&&config.Reads==1);
                        Check("ordinary_import_has_no_technical_proof_editors",!Descendants<CheckBox>(dialog).Any(c=>BindingPath(c,CheckBox.IsCheckedProperty)=="LegacyClpConfirmed")&&
                            !Descendants<ComboBox>(dialog).Any(c=>BindingPath(c,ComboBox.SelectedIndexProperty)=="TimeChoice")&&
                            !Descendants<TextBox>(dialog).Any(t=>new[]{"TemporalEvidence","MerchantEvidence","OverlapEvidence"}.Contains(BindingPath(t,TextBox.TextProperty))));
                        await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                        Check("qualified_preview_is_reviewable_without_writing",model.ImportStatus==T("confirmReady")&&model.ApplyCommand.CanExecute(null)&&config.Reads==2&&f.State()==before&&f.LocalWriteRequests==0);
                        Capture("guided-import-qualified-synthetic",dialog);
                        var other=Source(f.Root,"guided-other-scope",HistoricalSalesFixtureWriter.ModernRow("guided-other-scope",2000));
                        await ChooseAsync(dialog,model,other);
                        Check("new_file_invalidates_qualified_plan",model.Summary.Length==0&&!model.ApplyCommand.CanExecute(null)&&!model.PreviewCommand.CanExecute(null));
                        await AnalyzeAsync(dialog,model);
                        Check("out_of_scope_file_cannot_borrow_proof",model.ImportStatus==T("needsEvidence")&&model.RequirementsText.Contains(T("requireMerchant"))&&
                            !model.PreviewCommand.CanExecute(null)&&!model.ApplyCommand.CanExecute(null));
                        await ChooseAsync(dialog,model,source);await AnalyzeAsync(dialog,model);
                        Check("qualified_scope_can_be_reviewed_again",model.PreviewCommand.CanExecute(null));
                        config.Revoke(HistoricalEvidenceKind.ModernTime);
                        await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                        Check("changed_qualification_invalidates_review",model.ImportStatus==T("reviewStale")&&model.RequirementsText.Contains(T("requireTimeAnalysis"))&&
                            !model.ApplyCommand.CanExecute(null)&&!model.PreviewCommand.CanExecute(null));
                        await AnalyzeAsync(dialog,model);
                        Check("revoked_provider_proof_cannot_prepare",model.ImportStatus==T("needsEvidence")&&model.RequirementsText.Contains(T("requireProviderTime"))&&
                            !model.ApplyCommand.CanExecute(null)&&!model.PreviewCommand.CanExecute(null)&&f.State()==before&&f.LocalWriteRequests==0);
                    }
                    finally{dialog.Close();owner.Close();}
                }
            }
        }

        private async Task ReportCoverageAsync()
        {
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreateAsync(_data))
            {
                var service=f.CreateWorkflow();
                using(var report=new DailyReportViewModel(service,permissions:new PermissionService(f.Operator),session:f.Operator,allowPrinting:false))
                {
                    var view=new DailyReportView{DataContext=report};
                    var owner=new Window{Content=view,Width=1180,Height=760,ShowInTaskbar=false,Background=Brushes.White};
                    try
                    {
                        owner.Show();await PumpAsync();await UntilAsync(()=>!report.IsBusy);
                        await SelectTabAsync(view,1);
                        SetText(view,"HistoryFromText","2000-01-01");SetText(view,"HistoryToText","2000-01-03");await PumpAsync();
                        await InvokeAsync(view,report.LoadHistoryCommand,()=>!report.IsBusy);
                        Check("empty_calendar_is_not_loaded_data",report.ReportNumbersVisible&&report.HistoryRows.Count==0&&report.HistoryToolbarSummary==PosLocalization.T("historyReport.noRecordedData")&&report.ReportCoverageText.Contains(PosLocalization.F("historyReport.observedDays",0,3)));
                        Check("ordinary_report_has_no_origin_or_time_editor",!Descendants<ComboBox>(view).Any(c=>BindingPath(c,ComboBox.SelectedIndexProperty)=="ReportOriginIndex")&&!Descendants<Button>(view).Any(b=>b.Content?.ToString()==PosLocalization.T("reportTimePolicy.configure")));
                        await f.ApplyAsync(await f.PlanAsync());
                        await UntilAsync(()=>!report.IsBusy&&report.ReportNumbersVisible&&report.HistoryRows.Count==1);
                        Check("report_updates_after_committed_history",report.HistoryRows[0].Date==new DateTime(2000,1,2));
                        await InvokeAsync(view,report.LoadHistoryCommand,()=>!report.IsBusy);
                        Check("available_history_survives_missing_native_policy",report.ReportNumbersVisible&&report.HistoryRows.Count==1&&report.HistoryRows[0].Date==new DateTime(2000,1,2)&&report.HistoricalMovementCountText==PosLocalization.F("historyReport.importedCount",1)&&report.ReportCoverageText.Contains(PosLocalization.T("historyReport.nativeUnavailable")));
                        Check("known_history_amounts_remain_exact",report.PeriodNetAmount==1000&&report.PeriodGrossAmount==1000&&report.HistoryRows[0].HistoricalMovementCount==1&&report.HistoryRows[0].CardAmount==1000&&report.HistoryRows[0].NetAmount==1000);
                        Check("unobserved_days_are_not_zero_rows",report.ReportCoverageText.Contains(PosLocalization.F("historyReport.observedDays",1,2))&&report.HistoryToolbarSummary.Contains("1 days with recorded movements"));
                        Check("unknown_native_receipts_and_cash_are_not_zero",report.NativeReceiptCountText==PosLocalization.T("historyReport.notAvailable")&&report.CashAmountDisplay==PosLocalization.T("historyReport.notAvailable")&&report.HistoryRows[0].CashDisplay==PosLocalization.T("historyReport.notAvailable"));
                        Capture("guided-report-observed-days-synthetic",view);
                        var from=report.HistoryFromText;var to=report.HistoryToText;
                        using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                        {
                            var dialog=new HistoricalSalesImportDialog(model){Owner=owner};dialog.Show();await PumpAsync();dialog.Close();await PumpAsync();
                            Check("closing_import_preserves_owner_session_and_filters",owner.IsVisible&&report.HistoryFromText==from&&report.HistoryToText==to&&f.Operator.TryGetAuthorizationBoundUser(out _));
                        }
                    }
                    finally{owner.Close();view.DataContext=null;}
                }
            }
        }
        private async Task WebWriterHttpControlsAsync()
        {
            using(var f=await HistoricalSalesFoundationRunner.Fixture.CreatePersistentAsync(_data))
            {
                var path=Source(f.Root,"guided-web-http",HistoricalSalesFixtureWriter.ModernRow("guided-web-http",1000));
                var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{path},new HistoricalSalesImportOptions());
                var registered=new HistoricalSalesImportAnalyzer().Analyze(new[]{path},new HistoricalSalesImportOptions
                {ModernTimeInterpretation=HistoricalSalesModernTimeInterpretation.IsoUtc,TemporalPolicyVersion="synthetic-local-seed-v1",TemporalEvidenceReference="synthetic-prior-local-seed"});
                await f.ApplyAsync(await f.PreparePlanAsync(registered));
                var recognizedWhileLocal=await f.Service.AreFilesAlreadyActiveAsync(f.ShopId,analysis,_token);
                using(var http=new HistoricalWebHttpFixture(f,analysis))
                {
                    var service=await http.WorkflowAsync();var before=f.State();
                    using(var model=new HistoricalSalesImportViewModel(service,f.Operator))
                    {
                        var owner=new Window{Width=1024,Height=768,ShowInTaskbar=false};owner.Show();
                        var dialog=new HistoricalSalesImportDialog(model){Owner=owner};
                        try
                        {
                            dialog.Show();await PumpAsync();await UntilAsync(()=>!model.IsBusy);
                            await ChooseAsync(dialog,model,path);await AnalyzeAsync(dialog,model);
                            Check("server_denial_fixture_starts_with_visible_analysis",model.Summary.Length>0&&model.CoverageDetails.Length>0);
                            http.DenyConfig=true;var authority=f.Operator.CurrentOperatorAuthorityVersion;
                            await AnalyzeAsync(dialog,model);
                            Check("server_denied_never_publishes_analysis",f.Operator.CurrentOperatorAuthorityVersion==authority&&model.Summary.Length==0&&model.CoverageDetails.Length==0&&model.RequirementsText.Length==0&&
                                model.ImportStatus.Contains(T("operationAccessDenied"))&&!model.ApplyCommand.CanExecute(null));
                            http.DenyConfig=false;await AnalyzeAsync(dialog,model);
                            Check("web_writer_allows_ordinary_review_but_closes_local_mutation",model.CanImport&&model.LocalHistoryWriterClosed&&model.PreviewCommand.CanExecute(null)&&!model.ApplyCommand.CanExecute(null)&&f.LocalWriteRequests==0);
                            await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                            Check("web_prepare_pending_is_terminal_without_commit",model.ImportStatus==T("webPreparing")&&model.ImportStatus!=T("working")&&!model.ApplyCommand.CanExecute(null)&&http.CommitCalls==0&&f.State()==before);
                            await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                            Check("web_review_uses_http_and_only_web_proofs",model.ImportStatus==T("confirmReady")&&model.ApplyCommand.CanExecute(null)&&http.ConfiguredEvidence.Length==3&&
                                http.Operations.Contains("web_configure")&&http.Operations.Contains("web_review")&&http.SourcePosts==1&&http.CommitCalls==0&&f.LocalWriteRequests==0);
                            using(var db=f.Factory.Open())
                                Check("active_local_owner_does_not_bypass_web_review",recognizedWhileLocal&&!await f.Service.AreFilesAlreadyActiveAsync(f.ShopId,analysis,_token)&&
                                    db.ExecuteScalar<long>("SELECT COUNT(*) FROM historical_transactions WHERE is_active=1")==1&&model.ImportStatus!=T("alreadyImported")&&
                                    model.Summary.Contains("New 1;")&&http.Operations.Contains("web_review")&&f.State()==before);
                            Capture("guided-web-review-synthetic",dialog);
                            var backupsBefore=WebBackupCount();
                            await ConfirmThroughControlsAsync(dialog,model,()=>http.CommitCalls==1&&!model.IsBusy);
                            Check("web_lost_ack_retains_single_financial_write",http.FinancialWrites==1&&http.CommitCalls==1&&!model.ApplyCommand.CanExecute(null)&&WebBackupCount()==backupsBefore+1&&
                                f.State()==before&&f.LocalWriteRequests==0);
                            var oldSession=f.Generation.PosSessionId;var freshSession=await f.RenewSamePrincipalV6ForTestingAsync();http.RefreshExpectedActor();
                            await PumpAsync();await UntilAsync(()=>!model.IsBusy);await ChooseAsync(dialog,model,path);await AnalyzeAsync(dialog,model);
                            await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                            Check("web_receipt_recovery_never_recommits",model.ImportStepText==T("stepResult")&&http.ReceiptOperationId.Length>0&&model.ImportStatus.Contains(http.ReceiptOperationId)&&http.FinancialWrites==1&&http.CommitCalls==1&&
                                freshSession!=oldSession&&http.ReceiptSessions.Contains(oldSession)&&http.ReceiptSessions.Contains(freshSession)&&WebBackupCount()==backupsBefore+1&&!model.ApplyCommand.CanExecute(null)&&f.State()==before);
                            http.Noop=true;
                            Check("missing_original_probe_is_owned",Path.GetFullPath(path).StartsWith(Path.GetFullPath(f.Root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
                            var held=path+".held";File.Move(path,held);
                            try
                            {
                                var available=await service.PrepareHistoricalWebImportAsync(analysis,http.ConfigDigest,f.Operator,_token);
                                Check("verified_web_sources_need_no_original_disk_read",available.State=="queued"&&http.SourcePosts==1&&http.FinancialWrites==1&&http.CommitCalls==1);
                            }
                            finally{File.Move(held,path);}
                            await ChooseAsync(dialog,model,path);await AnalyzeAsync(dialog,model);
                            await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                            if(model.ImportStatus==T("webPreparing"))await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                            Check("web_duplicate_review_is_noop",model.ImportStatus==T("alreadyImported")&&model.ImportStepText==T("stepResult")&&!model.ApplyCommand.CanExecute(null)&&
                                http.CommitCalls==1&&http.FinancialWrites==1&&http.SourcePosts==1&&f.State()==before&&f.LocalWriteRequests==0&&http.FailureType.Length==0);
                            http.MissingWebProof=true;await ChooseAsync(dialog,model,path);await AnalyzeAsync(dialog,model);
                            await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                            if(model.ImportStatus==T("webPreparing"))await InvokeAsync(dialog,model.PreviewCommand,()=>!model.IsBusy);
                            Check("missing_web_proofs_block_without_csharp_fallback",model.ImportStatus==T("needsEvidence")&&model.RequirementsText.Contains(T("requireMerchant"))&&
                                !model.ApplyCommand.CanExecute(null)&&http.ConfiguredEvidence.Length==0&&http.CommitCalls==1&&http.FinancialWrites==1&&f.State()==before);
                        }
                        finally{dialog.Close();owner.Close();}
                    }
                }
            }
        }
        private static int WebBackupCount()=>Directory.Exists(Win7POS.Core.AppPaths.BackupsDirectory)?Directory.GetFiles(Win7POS.Core.AppPaths.BackupsDirectory,"pos_pre_history_web_*.db").Length:0;
        private async Task ConfirmThroughControlsAsync(HistoricalSalesImportDialog dialog,HistoricalSalesImportViewModel model,Func<bool> completed)
        {
            var confirmed=0;
            var timer=new DispatcherTimer(TimeSpan.FromMilliseconds(10),DispatcherPriority.Send,(_,__)=>
            {
                var popup=Application.Current.Windows.OfType<Win7POS.Wpf.Import.ApplyConfirmDialog>().SingleOrDefault(w=>w.Owner==dialog&&w.IsVisible);
                if(popup==null||confirmed!=0)return;
                var accept=Descendants<Button>(popup).Single(b=>b.IsDefault);confirmed++;
                ((IInvokeProvider)new ButtonAutomationPeer(accept).GetPattern(PatternInterface.Invoke)).Invoke();
            },Dispatcher.CurrentDispatcher);
            try
            {
                timer.Start();await InvokeAsync(dialog,model.ApplyCommand,()=>confirmed==1&&completed()&&!Application.Current.Windows.OfType<Win7POS.Wpf.Import.ApplyConfirmDialog>().Any(w=>w.Owner==dialog&&w.IsVisible));
                Check("web_confirm_uses_actual_default_control_once",confirmed==1);
            }
            finally
            {
                timer.Stop();
                foreach(var popup in Application.Current.Windows.OfType<Win7POS.Wpf.Import.ApplyConfirmDialog>().Where(w=>w.Owner==dialog).ToArray())popup.Close();
            }
        }
        private static string BindingPath(DependencyObject target,DependencyProperty property)
            =>BindingOperations.GetBindingExpression(target,property)?.ParentBinding.Path?.Path;
        private static bool BoundTextIsFullyVisible(DependencyObject view,string property)
        {
            var text=Descendants<TextBlock>(view).Single(t=>BindingPath(t,TextBlock.TextProperty)==property);
            if(!text.IsVisible||text.ActualHeight<=0||text.ActualWidth<=0)return false;
            DependencyObject ancestor=text;
            while((ancestor=VisualTreeHelper.GetParent(ancestor))!=null)
            {
                if(!(ancestor is ScrollViewer viewport))continue;
                var bounds=text.TransformToAncestor(viewport).TransformBounds(new Rect(text.RenderSize));
                return bounds.Left>=-1&&bounds.Top>=-1&&bounds.Right<=viewport.ActualWidth+1&&bounds.Bottom<=viewport.ActualHeight+1;
            }
            return false;
        }
        private static void SetText(DependencyObject view,string property,string value)
        {
            var input=Descendants<TextBox>(view).Single(t=>BindingPath(t,TextBox.TextProperty)==property);
            input.SetCurrentValue(TextBox.TextProperty,value);input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }
}
