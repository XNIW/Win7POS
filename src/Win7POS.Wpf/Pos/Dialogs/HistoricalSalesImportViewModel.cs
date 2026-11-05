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

    public sealed class HistoricalSalesImportViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PosWorkflowService _service;
        private readonly OperatorSession _session;
        private readonly PermissionService _permissions;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private CancellationTokenSource _operation;
        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private HistoricalSalesImportAnalysis _analysis;
        private HistoricalSalesImportPlan _plan;
        private HistoricalSalesApplyPreview _correctionPreview;
        private HistoricalImportContext _correctionContext;
        private string _reviewedInput;
        private bool _correctionsApproved;
        private string _correctionEvidence = "", _correctionSummary = "";
        private string _planForm;
        private readonly Dictionary<string, string> _groupIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _busy, _legacyClp, _merchantLimitExceeded;
        private int _timeChoice;
        private string _temporalEvidence = "", _merchantEvidence = "", _overlapEvidence = "", _summary = "", _coverageDetails = "", _status = "", _shop = "";
        private DateTime? _nativeStart;
        private HistoricalSalesBatch _selectedBatch;
        private long _readVersion;
        private OperatorSession.HistoricalReadAuthority _readAuthority;
        private readonly DispatcherTimer _readExpiryTimer;
        private bool _publicationAuthorityCurrent;
        private int _disposed;
        private readonly Dispatcher _dispatcher;
        public HistoricalSalesImportViewModel(PosWorkflowService service, OperatorSession session)
        {
            _service = service; _session = session; _permissions = new PermissionService(session);
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            SelectCommand = new HistoryCommand(SelectAsync, () => !IsBusy && CanImport);
            AnalyzeCommand = new HistoryCommand(AnalyzeAsync, () => !IsBusy && CanImport && _paths.Count > 0);
            PreviewCommand = new HistoryCommand(PreviewAsync, () => !IsBusy && CanPrepare);
            ApplyCommand = new HistoryCommand(ApplyAsync, () => !IsBusy && _plan != null && _planForm == FormFingerprint && CanImport);
            RefreshBatchesCommand = new HistoryCommand(RefreshBatchesAsync, () => !IsBusy && _permissions.Has(PermissionCodes.RegisterViewAll));
            ViewReceiptCommand = new HistoryCommand(ViewReceiptAsync, () => !IsBusy && SelectedBatch != null && _permissions.Has(PermissionCodes.RegisterViewAll));
            UndoCommand = new HistoryCommand(UndoAsync, () => !IsBusy && SelectedBatch?.State == "Active" && _permissions.Has(PermissionCodes.RegisterCancelHistory));
            CancelCommand = new HistoryCommand(() => { _operation?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
            AddGroupCommand = new HistoryCommand(() => { AddGroup(); return Task.CompletedTask; }, () => !IsBusy && CanImport);
            _session.SessionChanged += OnSessionChanged;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged += OnSessionChanged;
            _readExpiryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, __) =>
            {
                if (Volatile.Read(ref _publicationAuthorityCurrent) && !ImportReadAuthorityCurrent) OnSessionChanged();
            }, _dispatcher);
            _readExpiryTimer.Start();
        }
        public Window OwnerWindow { get; set; }
        public ObservableCollection<HistoricalMerchantDraft> Merchants { get; } = new ObservableCollection<HistoricalMerchantDraft>();
        public ObservableCollection<string> Groups { get; } = new ObservableCollection<string>();
        public ObservableCollection<HistoricalSalesBatch> Batches { get; } = new ObservableCollection<HistoricalSalesBatch>();
        public IReadOnlyList<string> TimeChoices => new[] { T("timeUnresolved"), T("timeUtc"), T("timeLocal") };
        public bool IsBusy { get => _busy; private set { _busy = value; Changed(); UpdateCommands(); } }
        public bool CanEdit => !IsBusy;
        public bool CanImport => _permissions.Has(PermissionCodes.RegisterImportHistory);
        public string Shop { get => _shop; private set { _shop = value; Changed(); } }
        public string Status { get => _status; private set { _status = value; Changed(); } }
        public bool ImportReadAuthorityCurrent => Volatile.Read(ref _publicationAuthorityCurrent) &&
            _readVersion == _session.CurrentOperatorAuthorityVersion &&
            _readAuthority?.IsCurrentInMemory == true;
        public string Summary { get => ImportReadAuthorityCurrent ? _summary : ""; private set { _summary = value; Changed(); } }
        public string CoverageDetails { get => ImportReadAuthorityCurrent ? _coverageDetails : ""; private set { _coverageDetails = value; Changed(); } }
        public string SelectedFiles => string.Format(CultureInfo.CurrentCulture, T("filesSelected"), _paths.Count);
        public bool LegacyClpConfirmed { get => _legacyClp; set { _legacyClp = value; InvalidateAnalysis(); Changed(); } }
        public int TimeChoice { get => _timeChoice; set { _timeChoice = value; InvalidateAnalysis(); Changed(); } }
        public string TemporalEvidence { get => _temporalEvidence; set { _temporalEvidence = value; InvalidateAnalysis(); Changed(); } }
        public string MerchantEvidence { get => _merchantEvidence; set { _merchantEvidence = value; InvalidateCorrections(); Changed(); UpdateCommands(); } }
        public string OverlapEvidence { get => _overlapEvidence; set { _overlapEvidence = value; InvalidateCorrections(); Changed(); UpdateCommands(); } }
        public DateTime? NativeStart { get => _nativeStart; set { _nativeStart = value; InvalidateCorrections(); Changed(); UpdateCommands(); } }
        public string CorrectionSummary { get => ImportReadAuthorityCurrent ? _correctionSummary : ""; private set { _correctionSummary = value; Changed(); } }
        public bool CanApproveCorrections => !IsBusy && _correctionPreview?.CorrectionCount > 0 && !_correctionPreview.CorrectionsTruncated;
        public bool CorrectionsApproved { get => _correctionsApproved; set { _correctionsApproved = value; _plan = null; Changed(); UpdateCommands(); } }
        public string CorrectionEvidence { get => _correctionEvidence; set { _correctionEvidence = value; _plan = null; Changed(); UpdateCommands(); } }
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
        private bool CanPrepare => CanImport && !_merchantLimitExceeded && _analysis?.CanApply == true && NativeStart.HasValue && !string.IsNullOrWhiteSpace(MerchantEvidence) && !string.IsNullOrWhiteSpace(OverlapEvidence) && Merchants.All(m => !string.IsNullOrWhiteSpace(m.Group));
        private string InputFingerprint => string.Join("|", NativeStart?.Ticks, MerchantEvidence, OverlapEvidence, string.Join(";", Merchants.Select(m => m.Group + ":" + m.BindingId + ":" + (m.Group != null && _groupIds.TryGetValue(m.Group, out var id) ? id : ""))));
        private string FormFingerprint => InputFingerprint + "|" + CorrectionsApproved + "|" + CorrectionEvidence;
        private static string T(string key) => PosLocalization.T("history." + key);
        private Task SelectAsync()
        {
            var picker = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", Multiselect = true, CheckFileExists = true };
            if (picker.ShowDialog(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()) == true) { _paths = picker.FileNames; InvalidateAnalysis(); Changed(nameof(SelectedFiles)); }
            return Task.CompletedTask;
        }
        private async Task AnalyzeAsync()
        {
            await ExecuteAsync(async ct =>
            {
                _permissions.Demand(PermissionCodes.RegisterImportHistory, T("title"));
                var options = new HistoricalSalesImportOptions { LegacyClpConfirmed = LegacyClpConfirmed, ModernTimeInterpretation = (HistoricalSalesModernTimeInterpretation)TimeChoice, TemporalEvidenceReference = TemporalEvidence };
                var analysis = await _service.AnalyzeHistoricalSalesAsync(_paths, options, ct).ConfigureAwait(true);
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
                _analysis = analysis; InvalidateCorrections(); Merchants.Clear(); Groups.Clear(); _groupIds.Clear();
                CoverageDetails = selection.Coverage;
                _merchantLimitExceeded = selection.TotalCount > HistoricalSalesImportPlan.MaximumMerchantAssignments;
                var drafts = new Dictionary<string, HistoricalMerchantDraft>(StringComparer.Ordinal);
                foreach (var selected in selection.Displayed)
                {
                    var draft = new HistoricalMerchantDraft { Merchant = selected.Merchant, BindingId = Guid.NewGuid().ToString("N") };
                    draft.PropertyChanged += (_, __) => { InvalidateCorrections(); UpdateCommands(); }; Merchants.Add(draft);
                    drafts.Add(selected.AliasKey, draft);
                }
                IReadOnlyList<HistoricalMerchantBindingInfo> existing = Array.Empty<HistoricalMerchantBindingInfo>();
                try { existing = await _service.GetHistoricalMerchantBindingsAsync(_session, ct).ConfigureAwait(true); }
                catch (InvalidOperationException error) when (error.Message == "historical_shop_binding_required") { }
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                foreach (var group in existing.Where(b => drafts.ContainsKey(b.AliasKey)).GroupBy(b => b.ScopeId))
                {
                    var label = AddGroup(group.Key);
                    foreach (var binding in group)
                        if (drafts.TryGetValue(binding.AliasKey, out var draft)) { draft.Group = label; draft.BindingId = binding.BindingId; }
                }
                var s = analysis.Summary;
                Summary = string.Format(CultureInfo.CurrentCulture, T("analysisSummary"), s.SourceRows, s.SaleCount, s.CancellationCount, s.RejectedCount, s.GrossClp, s.KnownNetClp, s.KnownNetCount, s.AcceptedCount,
                    s.FromBusinessDateTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—", s.ToBusinessDateTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—");
                Status = _merchantLimitExceeded ? string.Format(CultureInfo.CurrentCulture, T("merchantLimit"), selection.TotalCount, HistoricalSalesImportPlan.MaximumMerchantAssignments) : analysis.HasErrors ? T("errors") + Environment.NewLine + string.Join(Environment.NewLine, analysis.Diagnostics.Where(d => d.Severity == HistoricalSalesDiagnosticSeverity.Error).Take(12).Select(d => DisplayDiagnostic(d, analysis))) : analysis.NeedsEvidence ? T("needsEvidence") : T("analysisReady");
            }).ConfigureAwait(true);
        }
        private string AddGroup(string id = null)
        {
            var label = string.Format(CultureInfo.CurrentCulture, T("merchantGroup"), Groups.Count + 1);
            Groups.Add(label); _groupIds.Add(label, id ?? Guid.NewGuid().ToString("N")); return label;
        }
        private async Task PreviewAsync()
        {
            await ExecuteAsync(async ct =>
            {
                var context = await _service.GetHistoricalImportContextAsync(_session, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                Shop = context.ShopCode;
                var sameReview = _correctionContext != null && _reviewedInput == InputFingerprint && _correctionContext.ShopId == context.ShopId &&
                    _correctionContext.DatasetId == context.DatasetId && _correctionContext.DataRevision == context.DataRevision && _correctionContext.RestoreEpoch == context.RestoreEpoch;
                var approvals = sameReview && CorrectionsApproved && !string.IsNullOrWhiteSpace(CorrectionEvidence) && CanApproveCorrectionsWhenBusy
                    ? _correctionPreview.Corrections.Select(c => c.Approve(CorrectionEvidence)).ToArray() : Array.Empty<HistoricalSalesCorrectionApproval>();
                if (!sameReview) InvalidateCorrections();
                // Capture only immutable inputs on the Dispatcher. Hashing and
                // validation of the complete analysis belong to the worker.
                var analysis = _analysis; var nativeStart = NativeStart.Value; var overlapEvidence = OverlapEvidence; var merchantEvidence = MerchantEvidence;
                var inputs = Merchants.Select(m => new MerchantPlanInput(m.Merchant, _groupIds[m.Group], m.BindingId)).ToArray();
                var operationId = Guid.NewGuid().ToString("N");
                var plan = await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var result = new HistoricalSalesImportPlan(operationId, context.ShopId, context.DatasetId, context.DataRevision, context.RestoreEpoch, analysis,
                        inputs.Select(m => new HistoricalMerchantAssignment(m.Merchant, m.ScopeId, m.BindingId, merchantEvidence)), nativeStart, overlapEvidence, approvals);
                    ct.ThrowIfCancellationRequested(); return result;
                }, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                var preview = await _service.PreviewHistoricalSalesAsync(plan, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                Summary += Environment.NewLine + string.Format(CultureInfo.CurrentCulture, T("applyPreview"), preview.NewCount, preview.DuplicateCount, preview.ConflictCount, preview.NewGrossClp, preview.KnownNewNetClp, preview.KnownNewNetCount);
                _correctionPreview = preview; _correctionContext = context; _reviewedInput = InputFingerprint;
                CorrectionSummary = preview.CorrectionCount == 0 ? "" : string.Format(CultureInfo.CurrentCulture, T("correctionCount"), preview.CorrectionCount) + Environment.NewLine +
                    string.Join(Environment.NewLine, preview.Corrections.Select(c => FormatCorrection(c.PreviousTransactionId, c.SourceSheet, c.SourceRow, c.Previous, c.Proposed))) +
                    (preview.CorrectionsTruncated ? Environment.NewLine + T("correctionTruncated") : "");
                if (preview.CanApply) { _plan = plan; _planForm = FormFingerprint; Status = T("confirmReady"); }
                else { _plan = null; Status = preview.ConflictCount > 0 ? T("conflicts") : T("correctionRequired"); }
            }).ConfigureAwait(true);
        }
        private async Task ApplyAsync()
        {
            if (_plan == null || _planForm != FormFingerprint || !ApplyConfirmDialog.ShowConfirm(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(), T("apply"), Shop + Environment.NewLine + Summary + Environment.NewLine + string.Format(CultureInfo.CurrentCulture, T("correctionCount"), _plan.CorrectionApprovals.Count) + Environment.NewLine + T("backupNotice"))) return;
            await ExecuteAsync(async ct =>
            {
                var receipt = await _service.ApplyHistoricalSalesAsync(_plan, _session, ct).ConfigureAwait(true);
                DemandPublication(ct, PermissionCodes.RegisterImportHistory);
                Status = ReceiptText(receipt);
                _plan = null;
                if (_permissions.Has(PermissionCodes.RegisterViewAll)) await RefreshBatchesCoreAsync(ct).ConfigureAwait(true);
            }).ConfigureAwait(true);
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
            }, PermissionCodes.RegisterCancelHistory).ConfigureAwait(true);
        }
        private Task RefreshBatchesAsync() => ExecuteAsync(RefreshBatchesCoreAsync, PermissionCodes.RegisterViewAll);
        private async Task RefreshBatchesCoreAsync(CancellationToken ct)
        {
            var batches = await _service.ReadHistoricalBatchesAsync(_session, ct).ConfigureAwait(true);
            DemandPublication(ct, PermissionCodes.RegisterViewAll);
            Batches.Clear(); foreach (var b in batches) Batches.Add(b);
        }
        private async Task ExecuteAsync(Func<CancellationToken, Task> work, string permission = PermissionCodes.RegisterImportHistory)
        {
            if (IsBusy || Volatile.Read(ref _disposed) != 0) return;
            if (PosOnlineSyncRevocationLatch.ReadAuthorizationStamp().Inactive) return;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; _readVersion = _session.CurrentOperatorAuthorityVersion;
            Volatile.Write(ref _publicationAuthorityCurrent, false); IsBusy = true; Status = T("working");
            try
            {
                _readAuthority = _session.CaptureHistoricalUiReadAuthority(permission);
                Volatile.Write(ref _publicationAuthorityCurrent, true);
                await work(operation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { Status = T("cancelled"); }
            catch (Exception e) { Status = T("failed") + " " + (e is InvalidOperationException ? e.Message : e.GetType().Name); }
            finally { _operation = null; IsBusy = false; Changed(nameof(CanEdit)); }
        }
        internal Task AnalyzeFilesForTestingAsync(IReadOnlyList<string> paths) { _paths = paths; return AnalyzeAsync(); }
        internal Task RefreshBatchesForTestingAsync() => RefreshBatchesAsync();
        private void DemandPublication(CancellationToken ct, string permission)
        {
            ct.ThrowIfCancellationRequested();
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
        }, PermissionCodes.RegisterViewAll);
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
        private void InvalidateCorrections() { _plan = null; _correctionPreview = null; _correctionContext = null; _reviewedInput = null; _correctionsApproved = false; _correctionEvidence = ""; CorrectionSummary = ""; Changed(nameof(CorrectionsApproved)); Changed(nameof(CorrectionEvidence)); Changed(nameof(CanApproveCorrections)); }
        private void InvalidateAnalysis() { _analysis = null; _merchantLimitExceeded = false; InvalidateCorrections(); Summary = ""; CoverageDetails = ""; Merchants.Clear(); UpdateCommands(); }
        private void OnSessionChanged()
        {
            Volatile.Write(ref _publicationAuthorityCurrent, false);
            if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _operation?.Cancel(); _readAuthority = null; InvalidateAnalysis(); Batches.Clear(); SelectedBatch = null; Shop = "";
                Status = T("authorizationChanged"); Changed(nameof(ImportReadAuthorityCurrent)); UpdateCommands();
            }));
        }
        private void UpdateCommands()
        {
            Changed(nameof(CanEdit));
            Changed(nameof(CanApproveCorrections));
            foreach (var c in new[] { SelectCommand, AnalyzeCommand, PreviewCommand, ApplyCommand, RefreshBatchesCommand, UndoCommand, CancelCommand, AddGroupCommand, ViewReceiptCommand }) (c as HistoryCommand)?.Refresh();
        }
        private void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public event PropertyChangedEventHandler PropertyChanged;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _publicationAuthorityCurrent, false);
            _session.SessionChanged -= OnSessionChanged;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged -= OnSessionChanged;
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
