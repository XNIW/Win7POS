using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core.Import;
using Win7POS.Core.Security;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;
using System.Windows.Threading;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class HistoricalMerchantDraft : INotifyPropertyChanged
    {
        private string _group;
        internal HistoricalSalesMerchant Merchant { get; set; }
        internal string BindingId { get; set; }
        public string MerchantLabel => string.Join(" / ", new[] { Merchant.Local, Merchant.Seller, Merchant.SellerId, Merchant.LocalNumber, Merchant.Rut }.Where(x => !string.IsNullOrWhiteSpace(x)));
        public string Group { get => _group; set { _group = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Group))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed partial class HistoricalSalesImportViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PosWorkflowService _service;
        private readonly OperatorSession _session;
        private readonly PermissionService _permissions;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private CancellationTokenSource _operation;
        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private HistoricalSalesImportAnalysis _analysis;
        private HistoricalSalesImportPlan _plan;
        private HistoricalQualificationContext _qualification;
        private HistoricalQualificationResolution _resolution;
        private IReadOnlyList<HistoricalMerchantBindingInfo> _existingBindings=Array.Empty<HistoricalMerchantBindingInfo>();
        private string _requirements="",_planConfigDigest,_planQualificationFingerprint;
        private bool _localScopeUnresolved;
        private HistoricalSalesApplyPreview _correctionPreview;
        private HistoricalImportContext _correctionContext;
        private string _reviewedInput;
        private bool _correctionsApproved;
        private string _correctionEvidence = "", _correctionSummary = "";
        private string _planForm;
        private readonly Dictionary<string, string> _groupIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _busy, _legacyClp, _merchantLimitExceeded, _confirmingApply,_cloudLocalWriterClosed;
        private int _timeChoice;
        private string _temporalEvidence = "", _merchantEvidence = "", _overlapEvidence = "", _summary = "", _analysisSummary = "", _coverageDetails = "", _shop = "";
        private readonly string[] _operationStatuses = { "", "", "" };
        private int _selectedTabIndex, _operationArea;
        private long _inputVersion, _operationInputVersion, _authorityGeneration;
        private bool _importFinished, _populatingMerchants;
        private string _publicationStatus="",_receptionStatus="";
        private DateTime? _nativeStart;
        private HistoricalSalesBatch _selectedBatch;
        private long _readVersion;
        private OperatorSession.HistoricalReadAuthority _readAuthority;
        private readonly DispatcherTimer _readExpiryTimer;
        private bool _publicationAuthorityCurrent;
        private int _disposed;
        private readonly Dispatcher _dispatcher;
        private int _refreshRequested, _refreshQueued;
        public HistoricalSalesImportViewModel(PosWorkflowService service, OperatorSession session)
        {
            _service = service; _session = session; _permissions = new PermissionService(session);
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            SelectCommand = new HistoryCommand(SelectAsync, () => !IsBusy && CanAnalyze);
            AnalyzeCommand = new HistoryCommand(AnalyzeAsync, () => !IsBusy && CanAnalyze && _paths.Count > 0);
            PreviewCommand = new HistoryCommand(PreviewAsync, () => !IsBusy && CanPrepare);
            ApplyCommand = new HistoryCommand(ApplyAsync, () => !IsBusy && !_confirmingApply && (_plan != null || WebReviewCanApply) && _planForm == FormFingerprint && CanImport);
            RefreshBatchesCommand = new HistoryCommand(RefreshBatchesAsync, () => !IsBusy && _permissions.Has(PermissionCodes.RegisterViewAll));
            ViewReceiptCommand = new HistoryCommand(ViewReceiptAsync, () => !IsBusy && SelectedBatch != null && _permissions.Has(PermissionCodes.RegisterViewAll));
            UndoCommand = new HistoryCommand(UndoAsync, () => !IsBusy && !_cloudLocalWriterClosed && SelectedBatch?.State == "Active" && _permissions.Has(PermissionCodes.RegisterCancelHistory));
            CancelCommand = new HistoryCommand(() => { _operation?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
            AddGroupCommand = new HistoryCommand(() => { AddGroup(); return Task.CompletedTask; }, () => !IsBusy && CanImport);
            PrepareCloudCommand = new HistoryCommand(PrepareCloudAsync,()=>!IsBusy&&_permissions.Has(PermissionCodes.RegisterImportHistory));
            SyncCloudCommand = new HistoryCommand(()=>ExecuteAsync(async ct=>{await _service.SynchronizeHistoricalCloudAsync(_session,PosWorkflowService.HistoricalCloudBuildIdentity,ct);DemandPublication(ct,PermissionCodes.RegisterViewAll);Status=T("cloudSynced");},PermissionCodes.RegisterViewAll,area:2),()=>!IsBusy&&_permissions.Has(PermissionCodes.RegisterViewAll));
            _session.SessionChanged += OnSessionChanged;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged += OnSessionChanged;
            _service.ReportSnapshotCoordinator.SnapshotChanged += OnHistoricalSnapshotChanged;
            _service.ReportSnapshotCoordinator.HistoricalSyncStatusChanged += OnHistoricalSnapshotChanged;
            _readExpiryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, __) =>
            {
                if (Volatile.Read(ref _publicationAuthorityCurrent) && !ImportReadAuthorityCurrent) OnSessionChanged();
            }, _dispatcher);
            _readExpiryTimer.Start();
        }
        public Window OwnerWindow { get; set; }
        internal Func<Window,IReadOnlyList<string>> SelectFilesForTesting { get; set; }
        public ObservableCollection<HistoricalMerchantDraft> Merchants { get; } = new ObservableCollection<HistoricalMerchantDraft>();
        public ObservableCollection<string> Groups { get; } = new ObservableCollection<string>();
        public ObservableCollection<HistoricalSalesBatch> Batches { get; } = new ObservableCollection<HistoricalSalesBatch>();
        public IReadOnlyList<string> TimeChoices => new[] { T("timeUnresolved"), T("timeUtc"), T("timeLocal") };
        public bool IsBusy { get => _busy; private set { _busy = value; Changed(); UpdateCommands(); } }
        public bool CanEdit => !IsBusy;
        private bool CanAnalyze=>_permissions.Has(PermissionCodes.RegisterImportHistory);
        public bool CanImport => CanAnalyze&&(_historyWriterAuthority=="web"||!_cloudLocalWriterClosed);
        public bool LocalHistoryWriterClosed=>_cloudLocalWriterClosed;
        public string Shop { get => _shop; private set { _shop = value; Changed(); } }
        public int SelectedTabIndex { get => _selectedTabIndex; set { if(value<0||value>2||value==_selectedTabIndex)return;_selectedTabIndex=value;Changed();Changed(nameof(Status));UpdateCommands(); } }
        public string Status { get => VisibleStatus(SelectedTabIndex); private set => SetStatus(_operation==null?SelectedTabIndex:_operationArea,value); }
        public string ImportStatus => VisibleStatus(0);
        public string BatchesStatus => VisibleStatus(1);
        public string TransferStatus => VisibleStatus(2);
        public string ImportStepText => T(_importFinished?"stepResult":_plan!=null||WebReviewCanApply?"stepConfirm":_analysis!=null?"stepVerify":"stepChoose");
        public bool ShowImportConfirmation => SelectedTabIndex==0;
        public Visibility ImportConfirmationVisibility => ShowImportConfirmation?Visibility.Visible:Visibility.Collapsed;
        public string PublicationStatus => ImportReadAuthorityCurrent?_publicationStatus:"";
        public string ReceptionStatus => ImportReadAuthorityCurrent?_receptionStatus:"";
        public bool ImportReadAuthorityCurrent => Volatile.Read(ref _publicationAuthorityCurrent) &&
            _readVersion == _session.CurrentOperatorAuthorityVersion &&
            _readAuthority?.IsCurrentInMemory == true;
        public string Summary { get => ImportReadAuthorityCurrent ? _summary : ""; private set { _summary = value; Changed(); } }
        public string CoverageDetails { get => ImportReadAuthorityCurrent ? _coverageDetails : ""; private set { _coverageDetails = value; Changed(); } }
        public string RequirementsText=>ImportReadAuthorityCurrent?_requirements:"";
        public string SelectedFiles => string.Format(CultureInfo.CurrentCulture, T("filesSelected"), _paths.Count);
        public bool LegacyClpConfirmed { get => _legacyClp; set { if(_legacyClp==value)return;_legacyClp = value; InputChanged(); InvalidateAnalysis(); Changed(); } }
        public int TimeChoice { get => _timeChoice; set { if(_timeChoice==value)return;_timeChoice = value; InputChanged(); InvalidateAnalysis(); Changed(); } }
        public string TemporalEvidence { get => _temporalEvidence; set { if(_temporalEvidence==value)return;_temporalEvidence = value; InputChanged(); InvalidateAnalysis(); Changed(); } }
        public string MerchantEvidence { get => _merchantEvidence; set { if(_merchantEvidence==value)return;_merchantEvidence = value; InputChanged(); InvalidateCorrections(); Changed(); UpdateCommands(); } }
        public string OverlapEvidence { get => _overlapEvidence; set { if(_overlapEvidence==value)return;_overlapEvidence = value; InputChanged(); InvalidateCorrections(); Changed(); UpdateCommands(); } }
        public DateTime? NativeStart { get => _nativeStart; set { if(_nativeStart==value)return;_nativeStart = value; InputChanged(); InvalidateCorrections(); Changed(); UpdateCommands(); } }
        public string CorrectionSummary { get => ImportReadAuthorityCurrent ? _correctionSummary : ""; private set { _correctionSummary = value; Changed(); } }
        public bool CanApproveCorrections => !IsBusy && _correctionPreview?.CorrectionCount > 0 && !_correctionPreview.CorrectionsTruncated;
        public bool CorrectionsApproved { get => _correctionsApproved; set { if(_correctionsApproved==value)return;_correctionsApproved = value; InputChanged(); _plan = null; Changed(); UpdateCommands(); } }
        public string CorrectionEvidence { get => _correctionEvidence; set { if(_correctionEvidence==value)return;_correctionEvidence = value; InputChanged(); _plan = null; Changed(); UpdateCommands(); } }
        public HistoricalSalesBatch SelectedBatch { get => _selectedBatch; set { _selectedBatch = value; Changed(); UpdateCommands(); } }
        public ICommand SelectCommand { get; }
        public ICommand AnalyzeCommand { get; }
        public ICommand PreviewCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand RefreshBatchesCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand ViewReceiptCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand AddGroupCommand { get; }
        public ICommand PrepareCloudCommand { get; }
        public ICommand SyncCloudCommand { get; }
        private async Task PrepareCloudAsync()
        {
            await ExecuteAsync(async ct=>
            {
                HistoricalCloudExportReceipt receipt;
                try{receipt=await _service.PrepareHistoricalCloudTransferAsync(_session,null,null,ct);}
                catch(InvalidOperationException e) when(e.Message=="history_cloud_export_path_required")
                {
                    var picker=new SaveFileDialog{Filter="Getnet history (*.w7history.zip)|*.w7history.zip",FileName="Getnet-history-"+DateTime.Today.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+".w7history.zip",AddExtension=true,DefaultExt=".zip",OverwritePrompt=true};
                    if(picker.ShowDialog(OwnerWindow??DialogOwnerHelper.GetSafeOwner())!=true){Status=T("cancelled");return;}
                    receipt=await _service.PrepareHistoricalCloudTransferAsync(_session,picker.FileName,Path.ChangeExtension(picker.FileName,"verified.db"),ct);
                }
                DemandPublication(ct,PermissionCodes.RegisterImportHistory);_cloudLocalWriterClosed=true;Changed(nameof(CanImport));Changed(nameof(LocalHistoryWriterClosed));Status=T("cloudPrepared")+Environment.NewLine+receipt.ArchivePath;
            },area:2);
        }
        private bool CanPrepare => CanImport && !_importFinished && !_merchantLimitExceeded && _qualification!=null && _resolution!=null &&
            (IsWebWriter?_analysis!=null&&!_analysis.HasErrors&&_analysis.AcceptedTransactions.Count>0:!_localScopeUnresolved&&_analysis?.CanApply==true&&_resolution.CanPrepare);
        private string InputFingerprint => (_qualification?.ConfigDigest??"")+"|"+(_resolution?.Fingerprint??"");
        private string FormFingerprint => InputFingerprint + "|" + CorrectionsApproved + "|" + CorrectionEvidence;
        private static string T(string key) => PosLocalization.T("history." + key);
        private Task SelectAsync()
        {
            if(SelectFilesForTesting!=null)
            {
                var paths=SelectFilesForTesting(OwnerWindow??DialogOwnerHelper.GetSafeOwner());
                if(paths!=null&&paths.Count>0)SelectPaths(paths);
                return Task.CompletedTask;
            }
            var picker = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", Multiselect = true, CheckFileExists = true };
            if (picker.ShowDialog(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()) == true) SelectPaths(picker.FileNames);
            return Task.CompletedTask;
        }
        private void SelectPaths(IReadOnlyList<string> paths)
        {
            InputChanged(); _paths=paths.ToArray(); InvalidateAnalysis(); SetStatus(0,""); Changed(nameof(SelectedFiles));
        }
        private async Task AnalyzeAsync()
        {
            await ExecuteAsync(async ct =>
            {
                _permissions.Demand(PermissionCodes.RegisterImportHistory, T("title"));
                InvalidateAnalysis();Status=T("working");
                var paths=_paths.ToArray();
                // Start with parser facts. Operator-entered text, a checkbox,
                // the PC zone and plausible opening hours grant no authority.
                var analysis = await _service.AnalyzeHistoricalSalesAsync(paths,new HistoricalSalesImportOptions(),ct).ConfigureAwait(true);
                var known=false;
                HistoricalQualificationContext qualification=null;HistoricalQualificationResolution resolution=null;var unavailable=false;
                if(!analysis.HasErrors&&analysis.AcceptedTransactions.Count>0)
                {
                    try
                    {
                        qualification=await _service.ReadHistoricalQualificationAsync(_session,ct).ConfigureAwait(true);
                        DemandPublication(ct,PermissionCodes.RegisterImportHistory);ObserveHistoryWriter(qualification.WriterAuthority);
                        // A retained local owner cannot prove a no-op after the
                        // shared writer cancelled it. Confirm the current writer
                        // before using exact local source recognition.
                        known=!IsWebWriter&&qualification.WriterAuthority=="local"&&await _service.AreHistoricalFilesAlreadyActiveAsync(analysis,_session,ct).ConfigureAwait(true);
                        if(!known)
                        {
                            resolution=await ResolveAsync(analysis,qualification,ct).ConfigureAwait(true);
                            var options=IsWebWriter?null:QualifiedOptions(analysis,qualification,resolution);
                            if(options!=null)
                            {
                                analysis=await _service.AnalyzeHistoricalSalesAsync(paths,options,ct).ConfigureAwait(true);
                                if(!analysis.HasErrors)resolution=await ResolveAsync(analysis,qualification,ct).ConfigureAwait(true);
                                else resolution=null;
                            }
                        }
                    }
                    catch(HistoricalCloudRequestException e) when(!e.Denied){unavailable=true;}
                    catch(InvalidOperationException e) when(e.Message==HistoricalQualificationResponseMapper.InvalidCode){unavailable=true;}
                }
                var coverageText = new[] { T("coverageFile"), T("coverageObserved"), T("coverageIso"), T("coverageLocal"), T("coverageExample"), T("coverageNote") };
                var selection = await Task.Run(() =>
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var displayed = new List<MerchantSelection>();
                    foreach (var row in analysis.AcceptedTransactions)
                    {
                        ct.ThrowIfCancellationRequested();
                        var alias = HistoricalMerchantAssignment.MerchantKey(row.Merchant);
                        if (seen.Add(alias) && displayed.Count < HistoricalSalesImportPlan.MaximumMerchantAssignments)
                            displayed.Add(new MerchantSelection(row.Merchant, alias));
                    }
                    return new MerchantSelectionResult(displayed.ToArray(), seen.Count, BuildCoverageDetails(analysis, coverageText, ct));
                }, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                _analysis = analysis;_qualification=qualification;_resolution=resolution; InvalidateCorrections(); Merchants.Clear(); Groups.Clear(); _groupIds.Clear();
                CoverageDetails = selection.Coverage;
                _merchantLimitExceeded = selection.TotalCount > HistoricalSalesImportPlan.MaximumMerchantAssignments;
                var drafts = new Dictionary<string, HistoricalMerchantDraft>(StringComparer.Ordinal);
                foreach (var selected in selection.Displayed)
                {
                    var draft = new HistoricalMerchantDraft { Merchant = selected.Merchant, BindingId = Guid.NewGuid().ToString("N") };
                    draft.PropertyChanged += (_, __) => { if(_populatingMerchants||!Merchants.Contains(draft))return;InputChanged(); InvalidateCorrections(); UpdateCommands(); }; Merchants.Add(draft);
                    drafts.Add(selected.AliasKey, draft);
                }
                IReadOnlyList<HistoricalMerchantBindingInfo> existing = Array.Empty<HistoricalMerchantBindingInfo>();
                try { existing = await _service.GetHistoricalMerchantBindingsAsync(_session, ct).ConfigureAwait(true); }
                catch (InvalidOperationException error) when (error.Message == "historical_shop_binding_required") { }
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                _existingBindings=existing;
                _populatingMerchants=true;
                try{foreach (var group in existing.Where(b => drafts.ContainsKey(b.AliasKey)).GroupBy(b => b.ScopeId))
                {
                    var label = AddGroup(group.Key);
                    foreach (var binding in group)
                        if (drafts.TryGetValue(binding.AliasKey, out var draft)) { draft.Group = label; draft.BindingId = binding.BindingId; }
                }}finally{_populatingMerchants=false;}
                var s = analysis.Summary;
                _analysisSummary = string.Format(CultureInfo.CurrentCulture, T("analysisSummary"), s.SourceRows, s.SaleCount, s.CancellationCount, s.RejectedCount, s.GrossClp, s.KnownNetClp, s.KnownNetCount, s.AcceptedCount,
                    s.FromBusinessDateTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—", s.ToBusinessDateTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—");
                Summary=_analysisSummary;
                _localScopeUnresolved=!IsWebWriter&&resolution!=null&&qualification!=null&&existing.Any(b=>resolution.Merchants.TryGetValue(b.AliasKey,out var merchant)&&!PosWorkflowService.QualifiedScopeMatches(qualification,b.ScopeId,merchant));
                _importFinished=known||!analysis.HasErrors&&s.AcceptedCount==0&&s.RejectedCount>0;
                SetRequirements(known||_importFinished?"":IsWebWriter&&qualification!=null?T("webVerifyRequirements"):BuildRequirements(analysis,resolution,unavailable));
                Status = _merchantLimitExceeded ? string.Format(CultureInfo.CurrentCulture, T("merchantLimit"), selection.TotalCount, HistoricalSalesImportPlan.MaximumMerchantAssignments) : analysis.HasErrors ? T("errors") + Environment.NewLine + string.Join(Environment.NewLine, analysis.Diagnostics.Where(d => d.Severity == HistoricalSalesDiagnosticSeverity.Error).Take(12).Select(d => DisplayDiagnostic(d, analysis))) : known?T("alreadyImported"):_importFinished?T("declinedOnly"):CanPrepare?T("analysisReady"):T("needsEvidence");
            }).ConfigureAwait(true);
        }
        private string AddGroup(string id = null)
        {
            var label = string.Format(CultureInfo.CurrentCulture, T("merchantGroup"), Groups.Count + 1);
            Groups.Add(label); _groupIds.Add(label, id ?? Guid.NewGuid().ToString("N")); return label;
        }
        private static Task<HistoricalQualificationResolution> ResolveAsync(HistoricalSalesImportAnalysis analysis,HistoricalQualificationContext qualification,CancellationToken ct)
            =>Task.Run(()=>HistoricalQualificationResolver.Resolve(qualification.ShopId,HistoricalQualificationResolver.CreateSubjects(analysis),qualification.Evidence),ct);
        private static HistoricalSalesImportOptions QualifiedOptions(HistoricalSalesImportAnalysis analysis,HistoricalQualificationContext qualification,HistoricalQualificationResolution resolution)
        {
            if(resolution.Missing.Contains(HistoricalQualificationNeed.MerchantMapping)||resolution.Missing.Contains(HistoricalQualificationNeed.ConflictingPolicy))return null;
            var legacy=analysis.Files.Any(f=>f.Profile==HistoricalSalesProfile.GetnetLegacy)&&!resolution.Missing.Contains(HistoricalQualificationNeed.LegacyCurrency);
            var times=resolution.Evidence.Where(e=>e.Kind==HistoricalEvidenceKind.ModernTime).ToArray();
            var modern=times.Length>0&&!resolution.Missing.Contains(HistoricalQualificationNeed.ProviderTime)&&times.Select(e=>e.Interpretation+"|"+e.PolicyVersion).Distinct().Count()==1;
            if(!legacy&&!modern)return null;
            return new HistoricalSalesImportOptions{LegacyClpConfirmed=legacy,
                ModernTimeInterpretation=modern?times[0].Interpretation:HistoricalSalesModernTimeInterpretation.Unresolved,
                TemporalPolicyVersion=modern?times[0].PolicyVersion:"getnet-commercial-time-v1",
                TemporalEvidenceReference=modern?PosWorkflowService.QualifiedHistoryTimeReference(qualification,resolution):""};
        }
        private void SetRequirements(string text){_requirements=text??"";Changed(nameof(RequirementsText));}
        private string BuildRequirements(HistoricalSalesImportAnalysis analysis,HistoricalQualificationResolution resolution,bool unavailable)
        {
            if(analysis.HasErrors||analysis.AcceptedTransactions.Count==0)return "";
            if(unavailable||resolution==null)return T("requirementsUnavailable");
            var lines=new List<string>();
            if(_historyWriterAuthority=="frozen")lines.Add(T("frozenPreparation"));
            foreach(var need in resolution.Missing)
            {
                var key=need==HistoricalQualificationNeed.MerchantMapping?"requireMerchant":need==HistoricalQualificationNeed.LegacyCurrency?"requireLegacyCurrency":
                    need==HistoricalQualificationNeed.ProviderTime?"requireProviderTime":need==HistoricalQualificationNeed.NativeStart?"requireNativeStart":
                    need==HistoricalQualificationNeed.NativeOverlap?"requireNativeOverlap":need==HistoricalQualificationNeed.TimeAnalysis?"requireTimeAnalysis":
                    need==HistoricalQualificationNeed.ExplicitCurrency?"requireCurrency":"requireConflict";
                lines.Add(T(key));
            }
            if(_localScopeUnresolved)lines.Add(T("requireLocalScope"));
            if(resolution.Missing.Contains(HistoricalQualificationNeed.ProviderTime))
            {
                var modern=analysis.AcceptedTransactions.Where(r=>r.Profile==HistoricalSalesProfile.GetnetModern).ToArray();
                foreach(var row in modern.Take(1).Concat(modern.Skip(Math.Max(1,modern.Length-1))))
                    lines.Add(string.Format(CultureInfo.CurrentCulture,T("providerSample"),row.Source.RowNumber,BoundCoverage(row.Timestamp.RawTimestamp,160),BoundCoverage(row.Timestamp.RawOffset,32),BoundCoverage(row.PaymentId,96),BoundCoverage(row.AuthorizationCode,64)));
            }
            if(lines.Count==0)return T("requirementsReady");
            lines.Add(T("requirementsAction"));return string.Join(Environment.NewLine,lines.Distinct());
        }
        private async Task PreviewAsync()
        {
            await ExecuteAsync(async ct =>
            {
                _plan=null;_planForm=null;
                var context = await _service.GetHistoricalImportContextAsync(_session, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                Shop = context.ShopCode;
                var fresh=await _service.ReadHistoricalQualificationAsync(_session,ct).ConfigureAwait(true);
                var resolved=await ResolveAsync(_analysis,fresh,ct).ConfigureAwait(true);
                DemandPublication(ct,PermissionCodes.RegisterImportHistory);
                ObserveHistoryWriter(fresh.WriterAuthority);
                if(_qualification!=null&&_qualification.ConfigDigest!=fresh.ConfigDigest)
                {
                    InvalidateCorrections();_qualification=fresh;_resolution=null;
                    SetRequirements(T("requireTimeAnalysis")+Environment.NewLine+T("requirementsAction"));Status=T("reviewStale");return;
                }
                if(_qualification?.ConfigDigest!=fresh.ConfigDigest||_resolution?.Fingerprint!=resolved.Fingerprint)InvalidateCorrections();
                _qualification=fresh;_resolution=resolved;
                if(IsWebWriter){await PreviewWebAsync(ct).ConfigureAwait(true);return;}
                _existingBindings=await _service.GetHistoricalMerchantBindingsAsync(_session,ct).ConfigureAwait(true);
                DemandPublication(ct,PermissionCodes.RegisterImportHistory);
                _localScopeUnresolved=_existingBindings.Any(b=>resolved.Merchants.TryGetValue(b.AliasKey,out var merchant)&&!PosWorkflowService.QualifiedScopeMatches(fresh,b.ScopeId,merchant));
                SetRequirements(BuildRequirements(_analysis,resolved,false));
                if(!resolved.CanPrepare||_localScopeUnresolved||!CanImport){Status=T("needsEvidence");return;}
                var sameReview = _correctionContext != null && _reviewedInput == InputFingerprint && _correctionContext.ShopId == context.ShopId &&
                    _correctionContext.DatasetId == context.DatasetId && _correctionContext.DataRevision == context.DataRevision && _correctionContext.RestoreEpoch == context.RestoreEpoch;
                var approvals = sameReview && CorrectionsApproved && !string.IsNullOrWhiteSpace(CorrectionEvidence) && CanApproveCorrectionsWhenBusy
                    ? _correctionPreview.Corrections.Select(c => c.Approve(CorrectionEvidence)).ToArray() : Array.Empty<HistoricalSalesCorrectionApproval>();
                if (!sameReview) InvalidateCorrections();
                // Capture only immutable inputs on the Dispatcher. Hashing and
                // validation of the complete analysis belong to the worker.
                var analysis = _analysis; var nativeStart = new DateTime(resolved.Evidence.Where(e=>e.Kind==HistoricalEvidenceKind.NativeStart).Select(e=>e.NativeStartTicks.Value).Distinct().Single(),DateTimeKind.Unspecified);
                var overlapEvidence=PosWorkflowService.QualifiedHistoryReference(fresh,resolved);var merchantEvidence=overlapEvidence;
                var inputs = Merchants.Select(m=>{var alias=HistoricalMerchantAssignment.MerchantKey(m.Merchant);var binding=_existingBindings.SingleOrDefault(b=>b.AliasKey==alias);
                    return new MerchantPlanInput(m.Merchant,binding?.ScopeId??resolved.Merchants[alias],binding?.BindingId??Guid.NewGuid().ToString("N"));}).ToArray();
                var operationId = Guid.NewGuid().ToString("N");
                var plan = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var result = new HistoricalSalesImportPlan(operationId, context.ShopId, context.DatasetId, context.DataRevision, context.RestoreEpoch, analysis,
                        inputs.Select(m => new HistoricalMerchantAssignment(m.Merchant, m.ScopeId, m.BindingId, merchantEvidence)), nativeStart, overlapEvidence, approvals,
                        new HistoricalQualifiedInputSelection(fresh.ConfigDigest,resolved.Fingerprint,resolved.Evidence.Select(e=>e.Id)));
                    ct.ThrowIfCancellationRequested(); return result;
                }, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                var preview = await _service.PreviewHistoricalSalesAsync(plan, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                Summary = _analysisSummary + Environment.NewLine + string.Format(CultureInfo.CurrentCulture, T("applyPreview"), preview.NewCount, preview.DuplicateCount, preview.ConflictCount, preview.NewGrossClp, preview.KnownNewNetClp, preview.KnownNewNetCount);
                _correctionPreview = preview; _correctionContext = context; _reviewedInput = InputFingerprint;
                CorrectionSummary = preview.CorrectionCount == 0 ? "" : string.Format(CultureInfo.CurrentCulture, T("correctionCount"), preview.CorrectionCount) + Environment.NewLine +
                    string.Join(Environment.NewLine, preview.Corrections.Select(c => FormatCorrection(c.PreviousTransactionId, c.SourceSheet, c.SourceRow, c.Previous, c.Proposed))) +
                    (preview.CorrectionsTruncated ? Environment.NewLine + T("correctionTruncated") : "");
                if (preview.CanApply&&preview.NewCount==0&&preview.DuplicateCount>0) { _plan=null;_importFinished=true;Status=T("alreadyImported"); }
                else if (preview.CanApply) { _plan = plan; _planForm = FormFingerprint;_planConfigDigest=fresh.ConfigDigest;_planQualificationFingerprint=resolved.Fingerprint; Status = T("confirmReady"); }
                else { _plan = null; Status = preview.ConflictCount > 0 ? T("conflicts") : T("correctionRequired"); }
            }).ConfigureAwait(true);
        }
        private async Task ApplyAsync()
        {
            if(_webReview!=null){await ApplyWebAsync().ConfigureAwait(true);return;}
            var plan = _plan;
            var form = _planForm;
            var configDigest=_planConfigDigest;var qualificationFingerprint=_planQualificationFingerprint;var attempted=false;
            if (IsBusy || _confirmingApply || plan == null || form != FormFingerprint || !CanImport) return;
            // ShowConfirm runs a nested Dispatcher frame. Keep this operation's
            // immutable plan and reject another command until it has returned.
            _confirmingApply = true; UpdateCommands();
            try
            {
                if (!ApplyConfirmDialog.ShowConfirm(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(), T("apply"), Shop + Environment.NewLine + Summary + Environment.NewLine + string.Format(CultureInfo.CurrentCulture, T("correctionCount"), plan.CorrectionApprovals.Count) + Environment.NewLine + T("backupNotice"))) return;
                if (!ReferenceEquals(plan, _plan) || form != FormFingerprint || !CanImport)
                {
                    _plan = null; Status = T("reviewStale"); return;
                }
                attempted=true;
                await ExecuteAsync(async ct =>
                {
                    var receipt = await _service.ApplyQualifiedHistoricalSalesAsync(plan,configDigest,qualificationFingerprint,_session,ct).ConfigureAwait(true);
                    DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                    Status = ReceiptText(receipt);
                    _plan = null; _importFinished=true;
                    if (_permissions.Has(PermissionCodes.RegisterViewAll)) await RefreshBatchesCoreAsync(ct).ConfigureAwait(true);
                }).ConfigureAwait(true);
            }
            finally { if(attempted&&ReferenceEquals(_plan,plan))_plan=null;_confirmingApply = false; UpdateCommands(); }
        }
        private async Task UndoAsync()
        {
            var batch = SelectedBatch;
            if (batch == null || !ApplyConfirmDialog.ShowConfirm(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(), T("undo"), T("undoNotice") + Environment.NewLine + batch.Id)) return;
            await ExecuteAsync(async ct =>
            {
                var result = await _service.UndoHistoricalSalesAsync(batch, _session, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterCancelHistory);
                Status = string.Format(CultureInfo.CurrentCulture, T("receipt"), result.OperationId, result.InsertedCount, result.DuplicateCount, result.RejectedCount, result.SignedAmountClp, result.CurrentBatchState);
                InvalidateCorrections(); if (_permissions.Has(PermissionCodes.RegisterViewAll)) await RefreshBatchesCoreAsync(ct).ConfigureAwait(true);
            }, PermissionCodes.RegisterCancelHistory,area:1).ConfigureAwait(true);
        }
        private Task RefreshBatchesAsync() => ExecuteAsync(RefreshBatchesCoreAsync, PermissionCodes.RegisterViewAll,area:1);
        private async Task RefreshBatchesCoreAsync(CancellationToken ct)
        {
            var batches = await _service.ReadHistoricalBatchesAsync(_session, ct).ConfigureAwait(true);
            DemandPublication(ct, PermissionCodes.RegisterViewAll);
            Batches.Clear(); foreach (var b in batches) Batches.Add(b);
            var delivery=await _service.ReadHistoricalPublicationSummaryAsync(_session,ct).ConfigureAwait(true);
            DemandPublication(ct,PermissionCodes.RegisterViewAll);
            _publicationStatus=string.Format(CultureInfo.CurrentCulture,T("publicationState"),delivery.LocalOperations,delivery.Pending,delivery.Confirmed,delivery.Attention,delivery.Unregistered,
                delivery.LastConfirmedRevision??T("notConfirmed"));
            if(delivery.BaselineCovered>0)_publicationStatus+=" "+string.Format(CultureInfo.CurrentCulture,T("baselineCovered"),delivery.BaselineCovered);
            Changed(nameof(PublicationStatus));
        }
        private async Task ExecuteAsync(Func<CancellationToken, Task> work, string permission = PermissionCodes.RegisterImportHistory, bool keepStatus = false,int area=0)
        {
            if (IsBusy || Volatile.Read(ref _disposed) != 0) return;
            if (PosOnlineSyncRevocationLatch.ReadAuthorizationStamp().Inactive) return;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; _operationArea=area;_operationInputVersion=Interlocked.Read(ref _inputVersion);var inputVersion=_operationInputVersion;_readVersion = _session.CurrentOperatorAuthorityVersion;
            Volatile.Write(ref _publicationAuthorityCurrent, false); IsBusy = true; if (!keepStatus) Status = T("working");
            var authorityGeneration=Interlocked.Read(ref _authorityGeneration);
            bool Current()=>Volatile.Read(ref _disposed)==0&&authorityGeneration==Interlocked.Read(ref _authorityGeneration)&&_readVersion==_session.CurrentOperatorAuthorityVersion&&(area!=0||inputVersion==Interlocked.Read(ref _inputVersion));
            try
            {
                _readAuthority = _session.CaptureHistoricalUiReadAuthority(permission);
                Volatile.Write(ref _publicationAuthorityCurrent, true);
                if(_permissions.Has(PermissionCodes.RegisterViewAll))
                {
                    var cloud=await _service.ReadHistoricalCloudStateAsync(_session,operation.Token).ConfigureAwait(true);
                    DemandPublication(operation.Token,permission);
                    ObserveHistoryWriter(cloud?.WriterAuthority,cloud?.EverWeb!=0&&cloud?.WriterAuthority=="local");
                    _receptionStatus=cloud?.PublishedRevision==null?T("noCompleteCopy"):string.Format(CultureInfo.CurrentCulture,T("receptionState"),
                        cloud.PublishedRevision.Value,cloud.LastSuccessUtcMs.HasValue?DateTimeOffset.FromUnixTimeMilliseconds(cloud.LastSuccessUtcMs.Value).ToLocalTime().ToString("g",CultureInfo.CurrentCulture):T("notConfirmed"),
                        string.IsNullOrEmpty(cloud.StaleReason)&&cloud.PublishedRevision.Value>=cloud.ServerRevision?T("copyCurrent"):T("copyRetained"));Changed(nameof(ReceptionStatus));
                }
                await work(operation.Token).ConfigureAwait(true);
                DemandPublication(operation.Token,permission);
                if(!keepStatus&&_operationStatuses[area]==T("working"))Status=T("operationComplete");
            }
            catch (OperationCanceledException) { if(Current())Status = T("cancelled"); }
            catch (Exception e)
            {
                if(Current())
                {
                    if((e as HistoricalCloudRequestException)?.Denied==true)
                    {
                        // A newly denied server request must not leave a previous
                        // protected result visible under an in-memory permission.
                        Interlocked.Increment(ref _authorityGeneration);Interlocked.Increment(ref _inputVersion);
                        Volatile.Write(ref _publicationAuthorityCurrent,false);_readAuthority=null;
                        Interlocked.Exchange(ref _refreshRequested,0);InvalidateAnalysis();Batches.Clear();SelectedBatch=null;Shop="";
                        _publicationStatus="";_receptionStatus="";Changed(nameof(PublicationStatus));Changed(nameof(ReceptionStatus));
                        for(var deniedArea=0;deniedArea<3;deniedArea++)SetStatus(deniedArea,T("authorizationChanged"));
                    }
                    Status=OperationFailure(e);
                }
            }
            finally { _operation = null; IsBusy = false; Changed(nameof(CanEdit)); ScheduleHistoricalRefresh(); }
        }
        internal Task AnalyzeFilesForTestingAsync(IReadOnlyList<string> paths) { SelectPaths(paths); return AnalyzeAsync(); }
        internal Task RefreshBatchesForTestingAsync() => RefreshBatchesAsync();
        private void DemandPublication(CancellationToken ct, string permission)
        {
            ct.ThrowIfCancellationRequested();
            if(_operationArea==0&&_operationInputVersion!=Interlocked.Read(ref _inputVersion))throw new OperationCanceledException(ct);
            if (!ImportReadAuthorityCurrent || Volatile.Read(ref _disposed) != 0) throw new OperationCanceledException(ct);
            _permissions.Demand(permission, permission);
            if (!ImportReadAuthorityCurrent) throw new OperationCanceledException(ct);
        }
        private Task ViewReceiptAsync() => ExecuteAsync(async ct =>
        {
            var receipt = await _service.ReadHistoricalReceiptAsync(SelectedBatch.ApplyOperationId, _session, ct).ConfigureAwait(true);
            DemandPublication(ct, PermissionCodes.RegisterViewAll);
            if (receipt != null)
            {
                Status = ReceiptText(receipt);
                CorrectionSummary = string.Join(Environment.NewLine, (receipt.CorrectionApprovals ?? Array.Empty<HistoricalSalesCorrectionApproval>()).Take(HistoricalSalesApplyPreview.MaxDisplayedCorrections)
                    .Select(a => FormatCorrection(a.PreviousTransactionId, a.SourceSheet, a.SourceRow, a.Previous, a.Proposed) + " | " + a.EvidenceReference));
            }
            else Status=T("receiptUnavailable");
        }, PermissionCodes.RegisterViewAll,area:1);
        private bool CanApproveCorrectionsWhenBusy => _correctionPreview?.CorrectionCount > 0 && !_correctionPreview.CorrectionsTruncated;
        private static string DisplayDiagnostic(HistoricalSalesImportDiagnostic diagnostic, HistoricalSalesImportAnalysis analysis)
        {
            var code = diagnostic.Code ?? "";
            var category = code.Contains("header") || code.Contains("profile") || code.Contains("candidate_sheets") ? "diagnosticHeader" :
                code.Contains("identifier") ? "diagnosticIdentifier" : code.Contains("currency") ? "diagnosticCurrency" :
                code.Contains("timestamp") || code.Contains("offset") || code.Contains("policy") ? "diagnosticTime" :
                code.Contains("state") || code.Contains("movement_unknown") || code.Contains("prepaid") ? "diagnosticState" :
                code.Contains("amount") || code.Contains("fee") || code.Contains("financial") || code.Contains("overflow") || code.Contains("sign") || code.Contains("tip") ? "diagnosticEconomics" : "diagnosticInput";
            var source = diagnostic.Source;
            var file = analysis.Files.FirstOrDefault(f => f.FileId == source?.FileId);
            string Bound(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Length <= 96 ? value : value.Substring(0, 96) + "…";
            return string.Format(CultureInfo.CurrentCulture, T("diagnosticLocation"), Bound(file == null ? "" : Path.GetFileName(file.Path)), Bound(source?.SheetName),
                source != null && source.RowNumber > 0 ? source.RowNumber.ToString(CultureInfo.InvariantCulture) : "—", Bound(diagnostic.Field), diagnostic.ColumnNumber?.ToString(CultureInfo.InvariantCulture) ?? "—", T(category));
        }
        private static string FormatCorrection(long id, string sheet, int row, HistoricalSalesEconomicValue oldValue, HistoricalSalesEconomicValue newValue)
            => string.Format(CultureInfo.CurrentCulture, T("correctionLine"), id, sheet, row,
                new DateTime(oldValue.BusinessTicks).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), new DateTime(newValue.BusinessTicks).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                oldValue.GrossClp, newValue.GrossClp, oldValue.NetClp?.ToString(CultureInfo.InvariantCulture) ?? "—", newValue.NetClp?.ToString(CultureInfo.InvariantCulture) ?? "—",
                oldValue.FeeClp?.ToString(CultureInfo.InvariantCulture) ?? "—", newValue.FeeClp?.ToString(CultureInfo.InvariantCulture) ?? "—", oldValue.FeeVatClp?.ToString(CultureInfo.InvariantCulture) ?? "—", newValue.FeeVatClp?.ToString(CultureInfo.InvariantCulture) ?? "—",
                oldValue.TransactionState, newValue.TransactionState);
        private static string ReceiptText(HistoricalSalesOperationReceipt receipt) => string.Format(CultureInfo.CurrentCulture, T("receipt"), receipt.OperationId, receipt.InsertedCount, receipt.DuplicateCount, receipt.RejectedCount, receipt.SignedAmountClp, receipt.CurrentBatchState) + " " + string.Format(CultureInfo.CurrentCulture, T("correctionCount"), receipt.CorrectionCount);
        private void InvalidateCorrections() { _plan = null;_webReview=null;_planConfigDigest=null;_planQualificationFingerprint=null; _correctionPreview = null; _correctionContext = null; _reviewedInput = null; _correctionsApproved = false; _correctionEvidence = ""; CorrectionSummary = ""; Changed(nameof(CorrectionsApproved)); Changed(nameof(CorrectionEvidence)); Changed(nameof(CanApproveCorrections)); }
        private void InvalidateAnalysis() { _analysis = null;_qualification=null;_resolution=null;_localScopeUnresolved=false;_existingBindings=Array.Empty<HistoricalMerchantBindingInfo>();SetRequirements(""); _analysisSummary="";_importFinished=false;_merchantLimitExceeded = false; InvalidateCorrections(); Summary = ""; CoverageDetails = "";SetStatus(0,""); Merchants.Clear(); UpdateCommands(); }
        private void InputChanged(){Interlocked.Increment(ref _inputVersion);_importFinished=false;if(_operationArea==0)_operation?.Cancel();}
        private void SetStatus(int area,string value){_operationStatuses[area]=value??"";Changed(nameof(Status));Changed(area==0?nameof(ImportStatus):area==1?nameof(BatchesStatus):nameof(TransferStatus));}
        private string VisibleStatus(int area)
        {
            var text=_operationStatuses[area];
            var accessDenied=T("operationAccessDenied")+Environment.NewLine+T("diagnosticCode")+": operationAccessDenied";
            return ImportReadAuthorityCurrent||text.Length==0||text==T("working")||text==T("authorizationChanged")||text==accessDenied?text:T("authorizationChanged");
        }
        private static string OperationFailure(Exception error)
        {
            var code=(error as HistoricalCloudRequestException)?.Code??(error is InvalidOperationException?error.Message:"");
            var key=(error as HistoricalCloudRequestException)?.Denied==true?"operationAccessDenied":
                code=="history_cloud_initial_sync_required"?"initialSyncRequired":code=="history_cloud_writer_closed"||code=="history_local_writer_closed"?"localWriterClosed":
                code=="auth_denied"||code=="history_cloud_access_denied"?"operationAccessDenied":
                code=="history_resnapshot_required"||code=="historical_plan_stale"||code=="history_review_changed"?"reviewStale":
                error is UnauthorizedAccessException?"operationFileDenied":error is IOException?"operationFileFailed":
                error is HistoricalCloudRequestException?"operationServerFailed":"failed";
            // Closed local codes only: exception messages, paths and response bodies never reach this diagnostic.
            return T(key)+Environment.NewLine+T("diagnosticCode")+": "+key;
        }
        private void OnHistoricalSnapshotChanged(object sender, EventArgs args)
        {
            Interlocked.Exchange(ref _refreshRequested, 1);
            ScheduleHistoricalRefresh();
        }
        private void ScheduleHistoricalRefresh()
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _refreshRequested) == 0 ||
                _dispatcher.HasShutdownStarted || Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0) return;
            var refreshAuthority=Interlocked.Read(ref _authorityGeneration);
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                try
                {
                    if (Volatile.Read(ref _disposed) != 0 || IsBusy||refreshAuthority!=Interlocked.Read(ref _authorityGeneration)||Volatile.Read(ref _refreshRequested)==0) return;
                    Interlocked.Exchange(ref _refreshRequested, 0);
                    if (!_permissions.Has(PermissionCodes.RegisterViewAll)) return;
                    var selectedOperation = SelectedBatch?.ApplyOperationId;
                    await ExecuteAsync(async ct =>
                    {
                        await RefreshBatchesCoreAsync(ct).ConfigureAwait(true);
                        SelectedBatch = Batches.FirstOrDefault(batch => batch.ApplyOperationId == selectedOperation);
                    }, PermissionCodes.RegisterViewAll, keepStatus: true,area:1).ConfigureAwait(true);
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshQueued, 0);
                    if (!IsBusy) ScheduleHistoricalRefresh();
                }
            }));
        }
        private void OnSessionChanged()
        {
            Interlocked.Increment(ref _authorityGeneration);
            Interlocked.Increment(ref _inputVersion);
            Volatile.Write(ref _publicationAuthorityCurrent, false);
            if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _operation?.Cancel(); _readAuthority = null; InvalidateAnalysis(); Batches.Clear(); SelectedBatch = null; Shop = "";
                _historyWriterAuthority="local";_cloudLocalWriterClosed=false;
                Changed(nameof(CanImport));Changed(nameof(LocalHistoryWriterClosed));Changed(nameof(HistoryWriteNotice));
                _publicationStatus="";_receptionStatus="";Changed(nameof(PublicationStatus));Changed(nameof(ReceptionStatus));
                for(var area=0;area<3;area++)SetStatus(area,T("authorizationChanged"));Changed(nameof(ImportReadAuthorityCurrent)); UpdateCommands();
            }));
        }
        private void UpdateCommands()
        {
            Changed(nameof(CanEdit));
            Changed(nameof(ImportStepText));Changed(nameof(ShowImportConfirmation));Changed(nameof(ImportConfirmationVisibility));
            Changed(nameof(CanApproveCorrections));
            foreach (var c in new[] { SelectCommand, AnalyzeCommand, PreviewCommand, ApplyCommand, RefreshBatchesCommand, UndoCommand, CancelCommand, AddGroupCommand, ViewReceiptCommand,PrepareCloudCommand,SyncCloudCommand }) (c as HistoryCommand)?.Refresh();
        }
        private void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public event PropertyChangedEventHandler PropertyChanged;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _publicationAuthorityCurrent, false);
            _session.SessionChanged -= OnSessionChanged;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged -= OnSessionChanged;
            _service.ReportSnapshotCoordinator.SnapshotChanged -= OnHistoricalSnapshotChanged;
            _service.ReportSnapshotCoordinator.HistoricalSyncStatusChanged -= OnHistoricalSnapshotChanged;
            _readExpiryTimer.Stop();
            _lifetime.Cancel(); _lifetime.Dispose();
        }
        private sealed class MerchantSelection
        {
            internal MerchantSelection(HistoricalSalesMerchant merchant, string alias) { Merchant = merchant; AliasKey = alias; }
            internal HistoricalSalesMerchant Merchant { get; }
            internal string AliasKey { get; }
        }
        private sealed class MerchantSelectionResult
        {
            internal MerchantSelectionResult(MerchantSelection[] displayed, int totalCount, string coverage) { Displayed = displayed; TotalCount = totalCount; Coverage = coverage; }
            internal MerchantSelection[] Displayed { get; }
            internal int TotalCount { get; }
            internal string Coverage { get; }
        }
        private static string BuildCoverageDetails(HistoricalSalesImportAnalysis analysis, string[] text, CancellationToken ct)
        {
            var byFile = analysis.Files.ToDictionary(f => f.FileId, _ => new FileCoverage(), StringComparer.Ordinal);
            foreach (var row in analysis.AcceptedTransactions)
            {
                ct.ThrowIfCancellationRequested(); var observed = byFile[row.Source.FileId]; observed.Accepted++;
                observed.Business.Add(row.BusinessDateTime);
                if (row.Profile == HistoricalSalesProfile.GetnetModern && row.Timestamp.NeedsEvidence)
                {
                    observed.NeedsTimeEvidence = true; observed.Iso.Add(row.Timestamp.IsoBusinessDateTime); observed.Local.Add(row.Timestamp.ExportedLocalDateTime);
                    if (observed.Examples.Count < 3) observed.Examples.Add(row);
                }
            }
            foreach (var row in analysis.RejectedRows) { ct.ThrowIfCancellationRequested(); byFile[row.Source.FileId].Rejected++; }
            var result = new StringBuilder();
            foreach (var file in analysis.Files)
            {
                ct.ThrowIfCancellationRequested(); var observed = byFile[file.FileId];
                result.AppendLine(string.Format(CultureInfo.CurrentCulture, text[0], BoundCoverage(Path.GetFileName(file.Path), 96), BoundCoverage(file.SheetName, 96), file.SourceRows, observed.Accepted, observed.Rejected));
                if (observed.NeedsTimeEvidence)
                {
                    result.AppendLine(string.Format(CultureInfo.CurrentCulture, text[2], Clock(observed.Iso.From), Clock(observed.Iso.To)));
                    result.AppendLine(string.Format(CultureInfo.CurrentCulture, text[3], Clock(observed.Local.From), Clock(observed.Local.To)));
                    foreach (var row in observed.Examples)
                        result.AppendLine(string.Format(CultureInfo.CurrentCulture, text[4], row.Source.RowNumber, BoundCoverage(row.Timestamp.RawTimestamp, 160), BoundCoverage(row.Timestamp.RawOffset, 32), Clock(row.Timestamp.IsoBusinessDateTime), Clock(row.Timestamp.ExportedLocalDateTime)));
                }
                else result.AppendLine(string.Format(CultureInfo.CurrentCulture, text[1], Clock(observed.Business.From), Clock(observed.Business.To)));
            }
            result.Append(text[5]); return result.ToString();
        }
        private static string Clock(DateTime? value) => value?.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) ?? "—";
        private static string BoundCoverage(string value, int maximum) => string.IsNullOrEmpty(value) ? "—" : value.Length <= maximum ? value : value.Substring(0, maximum) + "…";
        private sealed class FileCoverage
        {
            internal int Accepted, Rejected; internal bool NeedsTimeEvidence;
            internal readonly ClockRange Business = new ClockRange(), Iso = new ClockRange(), Local = new ClockRange();
            internal readonly List<HistoricalSalesTransaction> Examples = new List<HistoricalSalesTransaction>(3);
        }
        private sealed class ClockRange
        {
            internal DateTime? From, To;
            internal void Add(DateTime? value) { if (!value.HasValue) return; if (!From.HasValue || value.Value < From.Value) From = value; if (!To.HasValue || value.Value > To.Value) To = value; }
        }
        private sealed class MerchantPlanInput
        {
            internal MerchantPlanInput(HistoricalSalesMerchant merchant, string scopeId, string bindingId) { Merchant = merchant; ScopeId = scopeId; BindingId = bindingId; }
            internal HistoricalSalesMerchant Merchant { get; }
            internal string ScopeId { get; }
            internal string BindingId { get; }
        }
        private sealed class HistoryCommand : ICommand
        {
            private readonly Func<Task> _run; private readonly Func<bool> _can;
            internal HistoryCommand(Func<Task> run, Func<bool> can) { _run = run; _can = can; }
            public bool CanExecute(object parameter) => _can();
            public async void Execute(object parameter) { if (CanExecute(parameter)) await _run().ConfigureAwait(true); }
            public event EventHandler CanExecuteChanged;
            internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
