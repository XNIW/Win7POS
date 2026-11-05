using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Win7POS.Core.Models;
using Win7POS.Core.Reports;
using Win7POS.Core.Security;
using Win7POS.Core.Util;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed partial class SalesRegisterViewModel
    {
        private OperatorSession _registerSession;
        private bool _registerAuthorityCurrent;
        private SalesReportContext _registerContext;
        private SalesReportReadAuthority _registerReadAuthority;
        private Dispatcher _registerDispatcher;
        private SalesReportCursor _registerNextCursor;
        private bool _registerHasMore;
        private int _registerOriginIndex;
        private DispatcherTimer _registerRevisionTimer;
        public ICommand LoadMoreCommand { get; private set; }
        public bool HasAnalyticalControls => !_isRefundScanMode;
        public bool RegisterNumbersVisible => _isRefundScanMode || (Volatile.Read(ref _registerAuthorityCurrent) && _registerContext?.Snapshot.Coverage.CanPublish == true &&
            _registerSession?.IsSalesReportReadAuthorityCurrent(_registerReadAuthority, _registerContext.Filter) == true && _registerContext.Coordinator.IsCurrent(_registerContext.Snapshot.Token));
        public string[] RegisterOriginLabels => new[] { PosLocalization.T("historyReport.originAll"), PosLocalization.T("historyReport.originNative"), PosLocalization.T("historyReport.originGetnet") };
        public int RegisterOriginIndex
        {
            get => _registerOriginIndex;
            set { if (value < 0 || value > 2 || value == _registerOriginIndex) return; _registerOriginIndex = value; ClearAnalyticalRegister(); OnPropertyChanged(); if (!IsBusy) LoadCommand.Execute(null); }
        }
        public string RegisterCoverageText => _registerContext == null ? PosLocalization.T("historyReport.coverageNotLoaded") : PosLocalization.T("historyReport.coverage." + _registerContext.Snapshot.Coverage.Status);
        private void InitializeAnalyticalRegister(OperatorSession session)
        {
            _registerSession = session;
            LoadMoreCommand = new AsyncRelayCommand(LoadMoreAnalyticalRegisterAsync, _ => !IsBusy && RegisterNumbersVisible && _registerHasMore);
            if (_isRefundScanMode) return;
            if (_registerSession != null) _registerSession.SessionChanged += RegisterSessionChanged;
            _registerDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged += RegisterSessionChanged;
            _registerRevisionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, __) =>
            {
                if (_registerContext != null && ((!RegisterNumbersVisible && _registerContext.Snapshot.Coverage.CanPublish) || !_registerContext.Coordinator.IsCurrent(_registerContext.Snapshot.Token))) ClearAnalyticalRegister();
            }, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
            _registerRevisionTimer.Start();
        }
        private void RegisterSessionChanged()
        {
            Volatile.Write(ref _registerAuthorityCurrent, false);
            Interlocked.Increment(ref _listLoadVersion); Interlocked.Increment(ref _selectionVersion);
            var dispatcher = _registerDispatcher;
            if (!_disposed && dispatcher != null && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { if (!_disposed) ClearAnalyticalRegister(); }));
        }
        private void ClearAnalyticalRegister()
        {
            if (_disposed) return;
            Volatile.Write(ref _registerAuthorityCurrent, false);
            CancelListLoad(); CancelSelectedSaleLoad(); _registerContext = null; _registerReadAuthority = null; _registerHasMore = false; _registerNextCursor = null;
            SelectedSale = null; SalesList.Clear(); ClearSelectedSaleDetail();
            Status = PosLocalization.T("historyReport.snapshotStale"); OnPropertyChanged(nameof(RegisterCoverageText)); OnPropertyChanged(nameof(RegisterNumbersVisible));
            IsBusy = false;
            (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        private void DisposeAnalyticalRegister()
        {
            _registerRevisionTimer?.Stop(); _registerRevisionTimer = null;
            if (_registerSession != null) _registerSession.SessionChanged -= RegisterSessionChanged;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged -= RegisterSessionChanged;
            _registerContext = null;
        }
        private void DemandAnalyticalRegisterPermission()
        {
            _permissionService.Demand(PermissionCodes.RegisterView, PosLocalization.T("sales.register.title"));
            if (_registerSession == null || !_registerSession.TryGetAuthorizationBoundUser(out _)) throw new InvalidOperationException("report_authorization_required");
        }
        private async Task DemandAnalyticalRegisterCurrentAsync(CancellationToken cancellationToken)
        {
            DemandAnalyticalRegisterPermission(); var context = _registerContext ?? throw new ReportSnapshotStaleException();
            DemandAnalyticalRegisterScope(context);
            await _service.DemandAnalyticalReportCurrentAsync(context, cancellationToken).ConfigureAwait(true);
            DemandAnalyticalRegisterPermission(); DemandAnalyticalRegisterScope(context); if (!ReferenceEquals(context, _registerContext)) throw new ReportSnapshotStaleException();
        }
        private void DemandAnalyticalRegisterScope(SalesReportContext context)
        {
            if (!string.Equals(context.Snapshot.Token.ScopeHash, context.Filter.Fingerprint, StringComparison.Ordinal) ||
                !_registerSession.IsSalesReportReadAuthorityCurrent(_registerReadAuthority, context.Filter)) throw new ReportSnapshotStaleException();
        }
        private bool TryCaptureAnalyticalRegister(SalesReportContext context, SalesReportReadAuthority authority, SalesReportReadToken token, Action captureOnly)
        {
            if (!token.Equals(context.Snapshot.Token) || !string.Equals(token.ScopeHash, context.Filter.Fingerprint, StringComparison.Ordinal)) throw new ReportSnapshotStaleException();
            return context.Coordinator.TryUseSnapshot(token, () =>
            {
                if (!_registerSession.TryUseSalesReportReadAuthority(authority, context.Filter, captureOnly)) throw new ReportSnapshotStaleException();
            });
        }
        private void GetCommercialRegisterRange(out DateTime from, out DateTime to)
        {
            var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
            if (Filter == SalesRegisterFilter.Ieri) { from = today.AddDays(-1); to = today; }
            else if (Filter == SalesRegisterFilter.Ultimi7Giorni) { from = today.AddDays(-6); to = today.AddDays(1); }
            else if (Filter == SalesRegisterFilter.Mese) { from = new DateTime(today.Year, today.Month, 1); to = from.AddMonths(1); }
            else if (Filter == SalesRegisterFilter.Periodo)
            {
                from = DateTime.SpecifyKind((RangeFrom ?? today).Date, DateTimeKind.Unspecified); to = DateTime.SpecifyKind((RangeTo ?? today).Date.AddDays(1), DateTimeKind.Unspecified);
                if (to <= from) throw new ArgumentException("report_period_invalid");
            }
            else { from = today; to = today.AddDays(1); }
        }
        private async Task LoadAnalyticalRegisterAsync()
        {
            if (_disposed) return;
            ClearAnalyticalRegister();
            CancelListLoad(); var cancellation = new CancellationTokenSource(); _listLoadCts = cancellation; var version = ++_listLoadVersion;
            IsBusy = true; SelectedSale = null; SalesList.Clear(); ClearSelectedSaleDetail();
            try
            {
                DemandAnalyticalRegisterPermission(); GetCommercialRegisterRange(out var from, out var to);
                if (!_registerSession.TryGetAuthorizationBoundUser(out var user)) throw new InvalidOperationException("report_authorization_required");
                var canAll = _canViewAll && _permissionService.Has(PermissionCodes.RegisterViewAll);
                var operatorId = canAll ? _forceOperatorId ?? (SelectedOperator?.Id > 0 ? SelectedOperator.Id : (int?)null) : user.Id;
                var origins = RegisterOriginIndex == 1 ? SalesReportOrigins.Native : RegisterOriginIndex == 2 ? SalesReportOrigins.Historical : SalesReportOrigins.All;
                var context = await _service.OpenAnalyticalReportAsync(from, to, origins, canAll, operatorId, CodeSearch, cancellation.Token).ConfigureAwait(true);
                cancellation.Token.ThrowIfCancellationRequested(); DemandAnalyticalRegisterPermission();
                if (!IsCurrentListLoad(version)) return;
                var authority = _registerSession.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.RegisterView);
                if (!IsCurrentListLoad(version)) return;
                ReplaceAnalyticalPage(context.Snapshot.FirstPage, context, authority);
                OnPropertyChanged(nameof(RegisterCoverageText));
            }
            catch (OperationCanceledException) { }
            catch { if (IsCurrentListLoad(version)) { _registerContext = null; SalesList.Clear(); Status = PosLocalization.T("historyReport.loadFailed"); OnPropertyChanged(nameof(RegisterCoverageText)); } }
            finally { if (IsCurrentListLoad(version)) { IsBusy = false; (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
        }
        private void ReplaceAnalyticalPage(SalesReportPage page, SalesReportContext context = null, SalesReportReadAuthority authority = null)
        {
            context = context ?? _registerContext ?? throw new ReportSnapshotStaleException();
            authority = authority ?? _registerReadAuthority;
            var rows = new ObservableCollection<SaleRow>(page.Entries.Select(entry => new SaleRow
            {
                ReportKey = entry.Key, SaleId = entry.Key.Origin == SalesReportOrigin.Native ? entry.Key.InternalId : 0,
                SaleCode = entry.Reference, Kind = entry.MovementKind == SalesReportMovementKind.Sale ? (int)SaleKind.Sale : entry.MovementKind == SalesReportMovementKind.Refund ? (int)SaleKind.Refund : (int)SaleKind.Void,
                Total = entry.SignedAmountClp, TimeText = entry.BusinessDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), IsVoided = entry.IsVoided
            }));
            var emptyLines = new ObservableCollection<DetailLineRow>();
            var status = context.Snapshot.Coverage.CanPublish ? PosLocalization.F("historyReport.registerSummary", context.Snapshot.Summary.NativePositiveReceiptCount,
                context.Snapshot.Summary.HistoricalMovementCount, MoneyClp.FormatDisplay(context.Snapshot.Summary.BalanceClp)) : PosLocalization.T("historyReport.coverage." + context.Snapshot.Coverage.Status);
            var capturedContext = context;
            if (!TryCaptureAnalyticalRegister(context, authority, page.Token, () =>
            {
                _registerContext = capturedContext; _registerReadAuthority = authority; Volatile.Write(ref _registerAuthorityCurrent, true); SalesList = rows; DetailLines = emptyLines;
                _selectedSale = null; _detailSummary = string.Empty; _detailReceiptPreview = string.Empty; _status = status;
                _registerNextCursor = page.NextCursor; _registerHasMore = page.HasMore;
            })) throw new ReportSnapshotStaleException();
            NotifyAnalyticalRegisterValues();
        }
        private void NotifyAnalyticalRegisterValues()
        {
            foreach (var property in new[] { nameof(SalesList), nameof(SelectedSale), nameof(DetailLines), nameof(DetailSummary), nameof(DetailReceiptPreview), nameof(HasReceiptPreview), nameof(Status), nameof(RegisterCoverageText), nameof(RegisterNumbersVisible) }) OnPropertyChanged(property);
            RaiseCanExecuteChanged(); (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
        private async Task LoadMoreAnalyticalRegisterAsync()
        {
            var context = _registerContext; if (context == null || !_registerHasMore) return; IsBusy = true;
            try
            {
                await DemandAnalyticalRegisterCurrentAsync(CancellationToken.None).ConfigureAwait(true);
                var page = await context.Query.GetPageAsync(context.Snapshot.Token, context.Filter, context.NativeTimePolicy, _registerNextCursor, 200, CancellationToken.None).ConfigureAwait(true);
                DemandAnalyticalRegisterPermission();
                if (!ReferenceEquals(context, _registerContext) || !context.Coordinator.IsCurrent(page.Token)) throw new ReportSnapshotStaleException();
                ReplaceAnalyticalPage(page);
            }
            catch { ClearAnalyticalRegister(); }
            finally { IsBusy = false; (LoadMoreCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); }
        }
        private async Task LoadAnalyticalRegisterDetailAsync(SaleRow selected, int version, CancellationToken cancellationToken)
        {
            if (selected?.ReportKey == null) { IsPreviewLoading = false; return; }
            var context = _registerContext;
            if (context == null) { IsPreviewLoading = false; return; }
            try
            {
                await DemandAnalyticalRegisterCurrentAsync(cancellationToken).ConfigureAwait(true);
                var entry = await context.Query.GetEntryAsync(context.Snapshot.Token, context.Filter, context.NativeTimePolicy, selected.ReportKey, cancellationToken).ConfigureAwait(true);
                if (entry == null) throw new ReportSnapshotStaleException();
                var lines = new ObservableCollection<DetailLineRow>();
                string summaryText, receiptText;
                if (entry.Key.Origin == SalesReportOrigin.Historical)
                {
                    DemandAnalyticalRegisterPermission(); if (!IsCurrentSelection(selected, version)) return;
                    receiptText = string.Empty;
                    summaryText = PosLocalization.F("historyReport.detail", PosLocalization.T("historyReport.originGetnet"), entry.Reference,
                        entry.BusinessDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture), MoneyClp.FormatDisplay(entry.CardClp), PosLocalization.T("historyReport.itemsUnavailable"));
                }
                else
                {
                    var detail = await _service.GetSaleDetailsAsync(entry.Key.InternalId).ConfigureAwait(true);
                    var preview = await _service.GetReceiptPreviewBySaleIdAsync(entry.Key.InternalId, _useReceipt42).ConfigureAwait(true);
                    await DemandAnalyticalRegisterCurrentAsync(cancellationToken).ConfigureAwait(true);
                    if (!IsCurrentSelection(selected, version) || detail == null) return;
                    foreach (var line in detail.Lines ?? Array.Empty<SaleLine>()) lines.Add(new DetailLineRow { Name = line.Name ?? "-", Quantity = line.Quantity, UnitPrice = line.UnitPrice, LineTotal = line.LineTotal });
                    summaryText = PosLocalization.F("sales.detailSummary", MoneyClp.Format(detail.Sale.Total), MoneyClp.Format(detail.Sale.PaidCash), MoneyClp.Format(detail.Sale.PaidCard), MoneyClp.Format(detail.Sale.Change), DocumentStatusText(detail.Sale));
                    receiptText = preview ?? string.Empty;
                }
                if (!TryCaptureAnalyticalRegister(context, _registerReadAuthority, context.Snapshot.Token, () => { DetailLines = lines; _detailSummary = summaryText; _detailReceiptPreview = receiptText; })) throw new ReportSnapshotStaleException();
                NotifyAnalyticalRegisterValues();
            }
            catch (OperationCanceledException) { }
            catch { if (IsCurrentSelection(selected, version)) ClearSelectedSaleDetail(); }
            finally { if (IsCurrentSelection(selected, version)) IsPreviewLoading = false; }
        }
    }
}
