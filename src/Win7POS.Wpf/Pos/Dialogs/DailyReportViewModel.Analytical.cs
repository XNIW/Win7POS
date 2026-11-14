using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Win7POS.Core.Reports;
using Win7POS.Core.Security;
using Win7POS.Core.Util;
using Win7POS.Data.Repositories;
using Win7POS.Data.Reports;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed partial class DailyReportViewModel
    {
        private IPermissionService _reportPermissions;
        private OperatorSession _reportSession;
        private SalesReportContext _reportContext;
        private SalesReportReadAuthority _reportReadAuthority;
        private Dispatcher _reportDispatcher;
        private CancellationTokenSource _reportCancellation;
        private DispatcherTimer _reportRevisionTimer;
        private SalesReportSnapshotCoordinator _reportCoordinator;
        private int _reportRefreshRequested, _reportRefreshQueued;
        private ReportSelection _reportRefreshSelection;
        private int _reportLoadVersion;
        private bool _reportAuthorityCurrent;
        private bool _reportFinalizing;
        private int _reportOriginIndex;
        private long _nativePositiveSalesClp;
        private long _periodNativePositiveSalesClp;
        public ICommand ImportHistoryCommand { get; private set; }
        public ICommand CancelReportCommand { get; private set; }
        public Task ReloadAnalyticalAsync() => SelectedTabIndex == 1 ? LoadAnalyticalHistoryAsync() : LoadAnalyticalDailyAsync();
        public string[] ReportOriginLabels => new[] { PosLocalization.T("historyReport.originAll"), PosLocalization.T("historyReport.originNative"), PosLocalization.T("historyReport.originGetnet") };
        public int ReportOriginIndex
        {
            get => _reportOriginIndex;
            set { if (value < 0 || value > 2 || value == _reportOriginIndex) return; _reportOriginIndex = value; InvalidateAnalyticalReport(); OnPropertyChanged(); }
        }
        private SalesReportOrigins SelectedReportOrigins => ReportOriginIndex == 1 ? SalesReportOrigins.Native : ReportOriginIndex == 2 ? SalesReportOrigins.Historical : SalesReportOrigins.All;
        public bool ReportNumbersVisible => Volatile.Read(ref _reportAuthorityCurrent) && _reportContext != null &&
            _reportSession?.IsSalesReportReadAuthorityCurrent(_reportReadAuthority, _reportContext.Filter) == true &&
            _reportContext.Snapshot.Coverage.CanPublish && _reportContext.Coordinator.IsCurrent(_reportContext.Snapshot.Token);
        public bool NativeReportComponentAvailable => ReportNumbersVisible&&(_reportContext.Filter.Origins&SalesReportOrigins.Native)!=0;
        public string NativeReceiptCountText => NativeReportComponentAvailable?SalesCountDisplay.ToString(CultureInfo.CurrentCulture):PosLocalization.T("historyReport.notAvailable");
        public string PeriodNativeReceiptCountText => NativeReportComponentAvailable?PeriodSalesCount.ToString(CultureInfo.CurrentCulture):PosLocalization.T("historyReport.notAvailable");
        public string MarkedNativeReceiptCountText => NativeReportComponentAvailable?MarkedSalesCount.ToString(CultureInfo.CurrentCulture):PosLocalization.T("historyReport.notAvailable");
        public string ReportCoverageText => CoverageText(_reportContext);
        private static string CoverageText(SalesReportContext context)
        {
            if(context==null)return PosLocalization.T("historyReport.coverageNotLoaded");
            var text=PosLocalization.T("historyReport.coverage."+context.Snapshot.Coverage.Status);
            if(context.NativeComponentUnavailable)text=PosLocalization.T("historyReport.nativeUnavailable")+" "+text;
            if(context.Snapshot.Coverage.CanPublish)
            {
                var calendar=context.Filter.SelectedDates.Count>0?context.Filter.SelectedDates.Count:(context.Filter.ToExclusive-context.Filter.FromInclusive).Days;
                text+=" "+PosLocalization.F("historyReport.observedDays",context.Snapshot.Days.Count,Math.Max(0,calendar-context.Snapshot.Days.Count));
            }
            return text;
        }
        public string HistoricalMovementCountText => ReportNumbersVisible ? PosLocalization.F("historyReport.importedCount", SelectedTabIndex == 1
            ? (MarkedCount > 0 ? HistoryRows.Where(row => row.IsMarked).Sum(row => row.HistoricalMovementCount) : SelectedHistoryRow?.HistoricalMovementCount ?? 0)
            : _reportContext.Snapshot.Summary.HistoricalMovementCount) : string.Empty;

        private void InitializeAnalyticalReports(IPermissionService permissions, OperatorSession session, Func<Task> importHistoryAction)
        {
            _reportPermissions = permissions; _reportSession = session;
            ImportHistoryCommand = new AsyncRelayCommand(async () => { if (importHistoryAction != null) { await importHistoryAction().ConfigureAwait(true); if (_reportContext != null && !_reportContext.Coordinator.IsCurrent(_reportContext.Snapshot.Token)) InvalidateAnalyticalReport(); } },
                _ => !IsBusy && importHistoryAction != null && (_reportPermissions?.Has(PermissionCodes.RegisterImportHistory) == true || _reportPermissions?.Has(PermissionCodes.RegisterCancelHistory) == true));
            CancelReportCommand = new RelayCommand(() => _reportCancellation?.Cancel(), _ => _reportCancellation != null && !_reportFinalizing);
            if (_reportSession != null) _reportSession.SessionChanged += OnReportSessionChanged;
            _reportDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged += OnReportSessionChanged;
            _reportCoordinator = _service.ReportSnapshotCoordinator;
            _reportCoordinator.SnapshotChanged += OnReportSnapshotChanged;
            _reportRevisionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, __) =>
            {
                if (_reportContext == null) return;
                if (_reportSession?.IsSalesReportReadAuthorityCurrent(_reportReadAuthority, _reportContext.Filter) != true)
                    InvalidateAnalyticalReport();
                else if (!_reportContext.Coordinator.IsCurrent(_reportContext.Snapshot.Token))
                    OnReportSnapshotChanged(this, EventArgs.Empty);
            }, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
            _reportRevisionTimer.Start();
        }
        private void OnReportSnapshotChanged(object sender, EventArgs args)
        {
            Interlocked.Exchange(ref _reportRefreshRequested, 1);
            ScheduleReportRefresh();
        }
        private void ScheduleReportRefresh()
        {
            if (_disposed || Volatile.Read(ref _reportRefreshRequested) == 0 || _reportDispatcher == null ||
                _reportDispatcher.HasShutdownStarted || Interlocked.CompareExchange(ref _reportRefreshQueued, 1, 0) != 0) return;
            _reportDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                try
                {
                    if (_disposed || IsBusy) return;
                    Interlocked.Exchange(ref _reportRefreshRequested, 0);
                    var context = _reportContext;
                    if (context != null && context.Coordinator.IsCurrent(context.Snapshot.Token)) return;
                    DemandAnalyticalPermission();
                    if (context != null && _reportRefreshSelection == null)
                        _reportRefreshSelection = new ReportSelection(context.Filter.Fingerprint,
                            HistoryRows.ToDictionary(row => row.Date, row => row.IsMarked), SelectedHistoryRow?.Date);
                    await ReloadAnalyticalAsync().ConfigureAwait(true);
                }
                catch (Exception error) { if (!_disposed) { InvalidateAnalyticalReport(); Status = ReportError(error); } }
                finally
                {
                    // A complete replica can arrive while a previous reload is
                    // suspended, or after a temporary coverage gap. Keep the
                    // user's selection until a current readable copy is shown.
                    if (_reportContext?.Snapshot.Coverage.CanPublish == true &&
                        _reportContext.Coordinator.IsCurrent(_reportContext.Snapshot.Token)) _reportRefreshSelection = null;
                    Interlocked.Exchange(ref _reportRefreshQueued, 0);
                    if (!IsBusy) ScheduleReportRefresh();
                }
            }));
        }
        private sealed class ReportSelection
        {
            internal ReportSelection(string filter, Dictionary<DateTime,bool> marks, DateTime? selected)
            { Filter=filter; Marks=marks; Selected=selected; }
            internal string Filter { get; }
            internal Dictionary<DateTime,bool> Marks { get; }
            internal DateTime? Selected { get; }
        }
        private void OnReportSessionChanged()
        {
            Volatile.Write(ref _reportAuthorityCurrent, false);
            Interlocked.Increment(ref _reportLoadVersion);
            var dispatcher = _reportDispatcher;
            if (!_disposed && dispatcher != null && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { if (!_disposed) InvalidateAnalyticalReport(); }));
        }
        private void InvalidateAnalyticalReport()
        {
            if (_disposed) return;
            Volatile.Write(ref _reportAuthorityCurrent, false);
            Interlocked.Increment(ref _reportLoadVersion); _reportCancellation?.Cancel(); _reportContext = null; _reportReadAuthority = null;
            CancelHistoryPreviewLoad(); CancelMarkedPreviewLoad();
            foreach (var row in HistoryRows) row.PropertyChanged -= OnHistoryRowPropertyChanged;
            HistoryRows.Clear(); HistoryRowCount = 0; SelectedHistoryRow = null;
            _nativePositiveSalesClp = 0; _periodNativePositiveSalesClp = 0; MarkedCount = 0; MarkedSalesCount = 0;
            MarkedNetAmount = 0; MarkedGrossAmount = 0; MarkedRefundsAmount = 0; MarkedCashAmount = 0; MarkedCardAmount = 0;
            _salesCount = 0; _totalAmount = 0; _cashAmount = 0; _cardAmount = 0; _grossSalesAmount = 0; _refundsAmount = 0; _netAmount = 0; _currentHeaderSummary = null;
            _periodNetAmount = 0; _periodGrossAmount = 0; _periodRefundsAmount = 0; _periodCashAmount = 0; _periodCardAmount = 0; _periodSalesCount = 0;
            SummaryReceiptPreview = string.Empty; SingleMarkedReceiptPreview = string.Empty; HourlySalesPoints.Clear();
            _reportLoaded = false; NotifyAnalyticalValues(); NotifyReportCoverage();
        }
        private void DisposeAnalyticalReports()
        {
            _reportRevisionTimer?.Stop(); _reportRevisionTimer = null;
            if (_reportCoordinator != null) _reportCoordinator.SnapshotChanged -= OnReportSnapshotChanged;
            _reportCoordinator = null;
            if (_reportSession != null) _reportSession.SessionChanged -= OnReportSessionChanged;
            PosOnlineSyncRevocationLatch.AuthorizationStateChanged -= OnReportSessionChanged;
            _reportCancellation?.Cancel(); _reportContext = null;
        }
        private void NotifyReportCoverage()
        {
            OnPropertyChanged(nameof(NativeReportComponentAvailable));OnPropertyChanged(nameof(NativeReceiptCountText));OnPropertyChanged(nameof(PeriodNativeReceiptCountText));OnPropertyChanged(nameof(MarkedNativeReceiptCountText));
            OnPropertyChanged(nameof(ReportNumbersVisible)); OnPropertyChanged(nameof(ReportCoverageText)); OnPropertyChanged(nameof(HistoricalMovementCountText));
            OnPropertyChanged(nameof(ReportOriginLabels)); OnPropertyChanged(nameof(ShowHistoryEmptyMessage));
            RaiseCanExecuteChanged(); (ImportHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (CancelReportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        private void DemandAnalyticalPermission()
        {
            if (_reportPermissions == null || _reportSession == null) throw new InvalidOperationException(PosLocalization.T("common.userPermissionDenied"));
            _reportPermissions.Demand(PermissionCodes.DailyCloseView, PosLocalization.T("historyReport.analyticalTitle"));
            if (!_reportSession.TryGetAuthorizationBoundUser(out _)) throw new InvalidOperationException(PosLocalization.T("common.userPermissionDenied"));
        }
        private async Task DemandAnalyticalCurrentAsync(CancellationToken cancellationToken)
        {
            DemandAnalyticalPermission();
            var context = _reportContext ?? throw new ReportSnapshotStaleException();
            DemandAnalyticalScope(context);
            await _service.DemandAnalyticalReportCurrentAsync(context, cancellationToken).ConfigureAwait(true);
            DemandAnalyticalPermission();
            DemandAnalyticalScope(context);
            if (!ReferenceEquals(context, _reportContext) || !context.Coordinator.IsCurrent(context.Snapshot.Token)) throw new ReportSnapshotStaleException();
        }

        private void GetAnalyticalOperatorScope(out bool canAll, out int? operatorId)
        {
            DemandAnalyticalPermission();
            if (!_reportSession.TryGetAuthorizationBoundUser(out var user)) throw new InvalidOperationException("report_authorization_required");
            canAll = _reportPermissions.Has(PermissionCodes.RegisterViewAll);
            operatorId = canAll ? (int?)null : user.Id;
        }
        private void DemandAnalyticalScope(SalesReportContext context)
        {
            if (context == null || !string.Equals(context.Snapshot.Token.ScopeHash, context.Filter.Fingerprint, StringComparison.Ordinal) ||
                !_reportSession.IsSalesReportReadAuthorityCurrent(_reportReadAuthority, context.Filter)) throw new ReportSnapshotStaleException();
        }
        private bool TryCaptureAnalyticalSnapshot(SalesReportContext context, SalesReportReadAuthority authority, Action captureOnly)
        {
            if (!string.Equals(context.Snapshot.Token.ScopeHash, context.Filter.Fingerprint, StringComparison.Ordinal)) throw new ReportSnapshotStaleException();
            return context.Coordinator.TryUseSnapshot(context.Snapshot.Token, () =>
            {
                if (!_reportSession.TryUseSalesReportReadAuthority(authority, context.Filter, captureOnly)) throw new ReportSnapshotStaleException();
            });
        }

        private async Task LoadAnalyticalDailyAsync()
        {
            if (!TryParseDate(out var date)) { Status = PosLocalization.T("reports.invalidDate"); return; }
            if (_disposed) return;
            InvalidateAnalyticalReport();
            _reportCancellation?.Cancel(); var cancellation = new CancellationTokenSource(); _reportCancellation = cancellation;
            var version = Interlocked.Increment(ref _reportLoadVersion); var origin = SelectedReportOrigins; IsBusy = true;
            try
            {
                GetAnalyticalOperatorScope(out var canAll, out var operatorId);
                var context = await _service.OpenAvailableAnalyticalReportAsync(date, date.AddDays(1), origin, canAll, operatorId, cancellation.Token).ConfigureAwait(true);
                cancellation.Token.ThrowIfCancellationRequested(); DemandAnalyticalPermission();
                if (_disposed || version != _reportLoadVersion) return;
                var authority = _reportSession.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.DailyCloseView);
                if (_disposed || version != Volatile.Read(ref _reportLoadVersion)) return;
                PublishAnalyticalSnapshot(context, authority, false, date, date);
                Status = ReportCoverageText;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (version == _reportLoadVersion) { _reportContext = null; SummaryReceiptPreview = string.Empty; HourlySalesPoints.Clear(); Status = ReportError(exception); NotifyReportCoverage(); } }
            finally { if (ReferenceEquals(_reportCancellation, cancellation)) { _reportCancellation = null; IsBusy = false; } cancellation.Dispose(); ScheduleReportRefresh(); }
        }

        private async Task LoadAnalyticalHistoryAsync()
        {
            if (!TryParseHistoryDates(out var from, out var to) || from > to || (to - from).TotalDays > 3660) { Status = PosLocalization.T("reports.invalidPeriodDates"); return; }
            if (_disposed) return;
            InvalidateAnalyticalReport();
            _reportCancellation?.Cancel(); var cancellation = new CancellationTokenSource(); _reportCancellation = cancellation;
            var version = Interlocked.Increment(ref _reportLoadVersion); var origin = SelectedReportOrigins; IsBusy = true;
            try
            {
                GetAnalyticalOperatorScope(out var canAll, out var operatorId);
                var context = await _service.OpenAvailableAnalyticalReportAsync(from, to.AddDays(1), origin, canAll, operatorId, cancellation.Token).ConfigureAwait(true);
                cancellation.Token.ThrowIfCancellationRequested(); DemandAnalyticalPermission();
                if (_disposed || version != _reportLoadVersion) return;
                var authority = _reportSession.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.DailyCloseView);
                if (_disposed || version != Volatile.Read(ref _reportLoadVersion)) return;
                PublishAnalyticalSnapshot(context, authority, true, from, to);
                Status = ReportCoverageText;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (version == _reportLoadVersion) { _reportContext = null; HistoryRows.Clear(); HistoryRowCount = 0; SummaryReceiptPreview = string.Empty; Status = ReportError(exception); NotifyReportCoverage(); } }
            finally { if (ReferenceEquals(_reportCancellation, cancellation)) { _reportCancellation = null; IsBusy = false; } cancellation.Dispose(); ScheduleReportRefresh(); }
        }

        private async Task LoadAnalyticalSelectedPreviewAsync(bool singleMarked)
        {
            var row = singleMarked ? HistoryRows.FirstOrDefault(item => item.IsMarked) : SelectedHistoryRow;
            var context = _reportContext;
            if (row == null || context == null || !context.Snapshot.Coverage.CanPublish || _disposed) return;
            if (singleMarked) IsMarkedPreviewLoading = true; else IsHistoryPreviewLoading = true;
            try
            {
                await DemandAnalyticalCurrentAsync(CancellationToken.None).ConfigureAwait(true);
                if (!ReferenceEquals(context, _reportContext) || _disposed) return;
                var selected = singleMarked ? HistoryRows.FirstOrDefault(item => item.IsMarked) : SelectedHistoryRow;
                if (!ReferenceEquals(selected, row)) return;
                var summary = context.Snapshot.Days.FirstOrDefault(day => day.Date == row.Date)?.Summary ?? SalesReportSummary.Empty;
                var preview = RenderAnalyticalSummary(row.Date, LegacySummary(row.Date, summary));
                var header = MarkedCount > 0 ? BuildHeaderSummaryFromMarkedRows() : BuildHeaderSummaryFromRow(row);
                if (!TryCaptureAnalyticalSnapshot(context, _reportReadAuthority, () =>
                {
                    if (singleMarked) _singleMarkedReceiptPreview = preview;
                    else { CaptureAnalyticalAmounts(summary); _summaryReceiptPreview = preview; _currentHeaderSummary = header; }
                })) throw new ReportSnapshotStaleException();
                NotifyAnalyticalValues();
            }
            catch (Exception exception) { if (exception is ReportSnapshotStaleException) InvalidateAnalyticalReport(); else Status = ReportError(exception); }
            finally { if (singleMarked) IsMarkedPreviewLoading = false; else IsHistoryPreviewLoading = false; }
        }

        private void PublishAnalyticalSnapshot(SalesReportContext context, SalesReportReadAuthority authority, bool history, DateTime from, DateTime to)
        {
            var snapshot = context.Snapshot;
            var selection = _reportRefreshSelection?.Filter == context.Filter.Fingerprint ? _reportRefreshSelection : null;
            var rows = new ObservableCollection<HistoryRow>();
            if (history && snapshot.Coverage.CanPublish)
            {
                foreach(var day in snapshot.Days.Where(day=>day.Date>=from&&day.Date<=to).OrderBy(day=>day.Date))
                {
                    var date=day.Date;var summary=day.Summary;
                    rows.Add(new HistoryRow { Date = date, DateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), SalesCount = checked((int)summary.NativePositiveReceiptCount),
                        NativeComponentAvailable=(context.Filter.Origins&SalesReportOrigins.Native)!=0,
                        NativePositiveSalesClp = summary.NativePositiveSalesClp, HistoricalMovementCount = summary.HistoricalMovementCount, NetAmount = summary.BalanceClp,
                        GrossAmount = summary.GrossPositiveClp, RefundsAmount = summary.ReversalsClp, CashAmount = summary.CashClp, CardAmount = summary.CardClp,
                        GrossDisplay = MoneyClp.FormatDisplay(summary.GrossPositiveClp), RefundsDisplay = MoneyClp.FormatDisplay(summary.ReversalsClp), NetDisplay = MoneyClp.FormatDisplay(summary.BalanceClp),
                        CashDisplay = (context.Filter.Origins&SalesReportOrigins.Native)!=0?MoneyClp.FormatDisplay(summary.CashClp):PosLocalization.T("historyReport.notAvailable"), CardDisplay = MoneyClp.FormatDisplay(summary.CardClp),
                        IsMarked = selection == null || !selection.Marks.TryGetValue(date, out var marked) || marked });
                }
            }
            var maximum = SalesReportHourlyGeometry.MaximumMagnitude(snapshot.HourlyAmounts);
            var points = new ObservableCollection<HourlySalesPoint>();
            for (var hour = 0; hour < 24; hour++) points.Add(new HourlySalesPoint { Hour = hour, AmountMinor = snapshot.HourlyAmounts[hour], AmountDisplay = MoneyClp.FormatDisplay(snapshot.HourlyAmounts[hour]),
                NormalizedHeight = maximum == 0 ? 0 : (double)((decimal)snapshot.HourlyAmounts[hour] / maximum), IsPeak = snapshot.HourlyAmounts[hour] > 0 && snapshot.HourlyAmounts[hour] == snapshot.HourlyAmounts.Max() });
            var selected = rows.FirstOrDefault(row => row.Date == selection?.Selected) ?? rows.FirstOrDefault();
            var displayed = history ? snapshot.Days.FirstOrDefault(day => day.Date == from)?.Summary ?? SalesReportSummary.Empty : snapshot.Summary;
            var preview = snapshot.Coverage.CanPublish ? RenderAnalyticalSummary(from, LegacySummary(from, displayed), CoverageText(context),(context.Filter.Origins&SalesReportOrigins.Native)!=0) : string.Empty;
            var headerSummary = history ? snapshot.Summary : displayed;
            var header = new HeaderSummary { SalesCount = checked((int)headerSummary.NativePositiveReceiptCount), NativePositiveSalesClp = headerSummary.NativePositiveSalesClp,
                NetAmount = headerSummary.BalanceClp, CashAmount = headerSummary.CashClp, CardAmount = headerSummary.CardClp, GrossAmount = headerSummary.GrossPositiveClp, RefundsAmount = headerSummary.ReversalsClp };
            var oldRows = HistoryRows;
            // Capture only backing fields/references. No notifications, collection events, queries or callbacks run in this gate.
            if (!TryCaptureAnalyticalSnapshot(context, authority, () =>
            {
                _reportContext = context; _reportReadAuthority = authority; Volatile.Write(ref _reportAuthorityCurrent, true);
                HistoryRows = rows; HourlySalesPoints = points; _selectedHistoryRow = selected; _historyRowCount = rows.Count;
                CaptureAnalyticalAmounts(displayed); _currentHeaderSummary = header; _summaryReceiptPreview = preview;
                _singleMarkedReceiptPreview = rows.Count == 1 ? preview : string.Empty;
                _reportLoaded = snapshot.Coverage.CanPublish; _isHistoryPreviewLoading = false; _isMarkedPreviewLoading = false;
                _periodNativePositiveSalesClp = history ? snapshot.Summary.NativePositiveSalesClp : 0;
                _periodNetAmount = history ? snapshot.Summary.BalanceClp : 0; _periodGrossAmount = history ? snapshot.Summary.GrossPositiveClp : 0;
                _periodRefundsAmount = history ? snapshot.Summary.ReversalsClp : 0; _periodCashAmount = history ? snapshot.Summary.CashClp : 0;
                _periodCardAmount = history ? snapshot.Summary.CardClp : 0; _periodSalesCount = history ? checked((int)snapshot.Summary.NativePositiveReceiptCount) : 0;
                _markedCount = rows.Count; _markedSalesCount = _periodSalesCount; _markedNetAmount = _periodNetAmount; _markedGrossAmount = _periodGrossAmount;
                _markedRefundsAmount = _periodRefundsAmount; _markedCashAmount = _periodCashAmount; _markedCardAmount = _periodCardAmount;
            })) throw new ReportSnapshotStaleException();
            foreach (var row in oldRows) row.PropertyChanged -= OnHistoryRowPropertyChanged;
            foreach (var row in rows) row.PropertyChanged += OnHistoryRowPropertyChanged;
            NotifyAnalyticalValues(); NotifyReportCoverage();
            if (history && selection != null) RefreshAnalyticalMarkedAggregates();
        }

        private void CaptureAnalyticalAmounts(SalesReportSummary summary)
        {
            _nativePositiveSalesClp = summary.NativePositiveSalesClp; _salesCount = checked((int)summary.NativePositiveReceiptCount);
            _totalAmount = summary.BalanceClp; _cashAmount = summary.CashClp; _cardAmount = summary.CardClp;
            _grossSalesAmount = summary.GrossPositiveClp; _refundsAmount = summary.ReversalsClp; _netAmount = summary.BalanceClp;
        }
        private void RefreshAnalyticalMarkedAggregates()
        {
            var context = _reportContext;
            if (context == null) return;
            var marked = HistoryRows.Where(row => row.IsMarked).ToArray();
            var header = marked.Length > 0 ? BuildHeaderSummaryFromMarkedRows() : SelectedTabIndex == 1 && SelectedHistoryRow != null ? BuildHeaderSummaryFromRow(SelectedHistoryRow) : BuildHeaderSummaryFromDaily();
            var count = marked.Length;
            var sales = checked(marked.Sum(row => row.SalesCount));
            var net = marked.Sum(row => row.NetAmount); var gross = marked.Sum(row => row.GrossAmount); var refunds = marked.Sum(row => row.RefundsAmount);
            var cash = marked.Sum(row => row.CashAmount); var card = marked.Sum(row => row.CardAmount);
            if (!TryCaptureAnalyticalSnapshot(context, _reportReadAuthority, () =>
            {
                _markedCount = count; _markedSalesCount = sales; _markedNetAmount = net; _markedGrossAmount = gross;
                _markedRefundsAmount = refunds; _markedCashAmount = cash; _markedCardAmount = card; _currentHeaderSummary = header;
            })) { InvalidateAnalyticalReport(); return; }
            NotifyAnalyticalValues();
            if (count == 1) _ = LoadAnalyticalSelectedPreviewAsync(true);
            else { CancelMarkedPreviewLoad(); SingleMarkedReceiptPreview = string.Empty; }
        }
        private void RefreshAnalyticalHeader()
        {
            var context = _reportContext;
            if (context == null) { _currentHeaderSummary = null; NotifyAnalyticalValues(); return; }
            var header = MarkedCount > 0 ? BuildHeaderSummaryFromMarkedRows() : SelectedTabIndex == 1 && SelectedHistoryRow != null ? BuildHeaderSummaryFromRow(SelectedHistoryRow) : BuildHeaderSummaryFromDaily();
            if (!TryCaptureAnalyticalSnapshot(context, _reportReadAuthority, () => _currentHeaderSummary = header)) { InvalidateAnalyticalReport(); return; }
            NotifyAnalyticalValues();
        }
        private void NotifyAnalyticalValues()
        {
            foreach (var property in new[] { nameof(HistoryRows), nameof(HourlySalesPoints), nameof(SelectedHistoryRow), nameof(HistoryRowCount), nameof(HasHistoryRows), nameof(ShowHistoryEmptyMessage),
                nameof(NativeReportComponentAvailable),nameof(NativeReceiptCountText),nameof(PeriodNativeReceiptCountText),nameof(MarkedNativeReceiptCountText),
                nameof(SalesCount), nameof(TotalAmount), nameof(CashAmount), nameof(CardAmount), nameof(GrossSalesAmount), nameof(RefundsAmount), nameof(NetAmount),
                nameof(SalesCountDisplay), nameof(NetAmountDisplay), nameof(CashAmountDisplay), nameof(CardAmountDisplay), nameof(GrossSalesAmountDisplay), nameof(RefundsAmountDisplay), nameof(TicketMedioDisplay),
                nameof(SummaryReceiptPreview), nameof(SingleMarkedReceiptPreview), nameof(IsHistoryPreviewLoading), nameof(IsMarkedPreviewLoading), nameof(HourlyPeakText), nameof(HourlyDayTotalText),
                nameof(PeriodNetDisplay), nameof(PeriodGrossDisplay), nameof(PeriodRefundsDisplay), nameof(PeriodCashDisplay), nameof(PeriodCardDisplay), nameof(PeriodSalesCount), nameof(PeriodTicketAverageDisplay),
                nameof(MarkedCount), nameof(MarkedSalesCount), nameof(MarkedNetDisplay), nameof(MarkedGrossDisplay), nameof(MarkedRefundsDisplay), nameof(MarkedCashDisplay), nameof(MarkedCardDisplay), nameof(MarkedTicketAverageDisplay),
                nameof(HasMarkedRows), nameof(HasHistorySelection), nameof(ShowDetailPanel), nameof(ShowPlaceholder), nameof(ShowMarkedSummary), nameof(ShowSingleDayDetail), nameof(ShowMultiDaySummary),
                nameof(ShowSelectionPlaceholder), nameof(ShowSelectedRowDetail), nameof(ShowReceiptPreview), nameof(DetailPanelTitle), nameof(HistoryToolbarSummary), nameof(HistoricalMovementCountText) }) OnPropertyChanged(property);
            RaiseCanExecuteChanged();
        }

        private void ApplyAnalyticalAmounts(SalesReportSummary summary)
        {
            _nativePositiveSalesClp = summary.NativePositiveSalesClp;
            SalesCount = checked((int)summary.NativePositiveReceiptCount); TotalAmount = summary.BalanceClp; CashAmount = summary.CashClp;
            CardAmount = summary.CardClp; GrossSalesAmount = summary.GrossPositiveClp; RefundsAmount = summary.ReversalsClp; NetAmount = summary.BalanceClp;
        }
        private static DailySalesSummary LegacySummary(DateTime date, SalesReportSummary summary) => new DailySalesSummary { Date = date, SalesCount = checked((int)summary.NativePositiveReceiptCount),
            TotalAmount = summary.BalanceClp, NetAmount = summary.BalanceClp, GrossSalesAmount = summary.GrossPositiveClp, RefundsAmount = summary.ReversalsClp, CashAmount = summary.CashClp, CardAmount = summary.CardClp };
        private string RenderAnalyticalSummary(DateTime date, DailySalesSummary summary, string coverageText = null,bool? nativeAvailable=null)
        {
            var width = _useReceipt42 ? 42 : 32; var lines = new List<string>();
            lines.AddRange(Win7POS.Core.Receipt.ReceiptTextLayout.WrapText(PosLocalization.T("historyReport.analyticalTitle"), width));
            lines.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            lines.AddRange(Win7POS.Core.Receipt.ReceiptTextLayout.WrapText(coverageText ?? ReportCoverageText, width));
            lines.AddRange(Win7POS.Core.Receipt.ReceiptTextLayout.TwoColumnLine(PosLocalization.T("historyReport.movementBalance"), MoneyClp.FormatDisplay(summary.NetAmount), width));
            lines.AddRange(Win7POS.Core.Receipt.ReceiptTextLayout.TwoColumnLine(PosLocalization.T("common.card"), MoneyClp.FormatDisplay(summary.CardAmount), width));
            lines.AddRange(Win7POS.Core.Receipt.ReceiptTextLayout.TwoColumnLine(PosLocalization.T("common.cash"), (nativeAvailable??NativeReportComponentAvailable)?MoneyClp.FormatDisplay(summary.CashAmount):PosLocalization.T("historyReport.notAvailable"), width));
            lines.AddRange(Win7POS.Core.Receipt.ReceiptTextLayout.WrapText(PosLocalization.T("historyReport.beforeFees"),width));
            lines.AddRange(Win7POS.Core.Receipt.ReceiptTextLayout.WrapText(PosLocalization.T("historyReport.cardCoverage"), width));
            return string.Join(Environment.NewLine, lines);
        }
        private void RenderHourly(IReadOnlyList<long> amounts)
        {
            var maximum = SalesReportHourlyGeometry.MaximumMagnitude(amounts); HourlySalesPoints.Clear();
            for (var hour = 0; hour < 24; hour++) HourlySalesPoints.Add(new HourlySalesPoint { Hour = hour, AmountMinor = amounts[hour], AmountDisplay = MoneyClp.FormatDisplay(amounts[hour]),
                NormalizedHeight = maximum == 0 ? 0 : (double)((decimal)amounts[hour] / maximum), IsPeak = amounts[hour] > 0 && amounts[hour] == amounts.Max() });
            OnPropertyChanged(nameof(HourlyPeakText)); OnPropertyChanged(nameof(HourlyDayTotalText));
        }
        private static string FormatNativeAverage(long positiveSalesClp, long count) => count <= 0 ? PosLocalization.T("historyReport.notAvailable") : MoneyClp.FormatDisplay(checked((long)decimal.Round((decimal)positiveSalesClp / count, 0, MidpointRounding.AwayFromZero)));
        private static string ReportError(Exception exception) => exception is ReportSnapshotStaleException ? PosLocalization.T("historyReport.snapshotStale") :
            exception.Message == "report_shop_required" ? PosLocalization.T("historyReport.shopRequired") : PosLocalization.T("historyReport.loadFailed");

        private async Task<SalesReportContext> GetAnalyticalExportContextAsync(ExportRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            await DemandAnalyticalCurrentAsync(cancellationToken).ConfigureAwait(true);
            var visible = _reportContext; DateTime from, to; IReadOnlyList<DateTime> dates = null;
            if (request.Scope == ExportScope.Marked)
            {
                dates = (request.Dates ?? Array.Empty<DateTime>()).Select(date => date.Date).Distinct().OrderBy(date => date).ToArray();
                if (dates.Count == 0 || dates.Count > 366) throw new InvalidOperationException("report_marked_date_limit");
                from = dates[0]; to = dates[dates.Count - 1].AddDays(1);
            }
            else if (request.Scope == ExportScope.Period) { from = request.From.Value.Date; to = request.To.Value.Date.AddDays(1); }
            else { from = request.Date.Value.Date; to = from.AddDays(1); }
            if (from < visible.Filter.FromInclusive || to > visible.Filter.ToExclusive) throw new ReportSnapshotStaleException();
            var context = await _service.OpenAnalyticalReportAsync(from, to, visible.Filter.Origins, visible.Filter.CanViewAllOperators, visible.Filter.OperatorId, visible.Filter.ReferenceSearch, cancellationToken, dates).ConfigureAwait(true);
            await DemandAnalyticalCurrentAsync(cancellationToken).ConfigureAwait(true);
            _reportSession.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.DailyCloseView);
            if (context.Snapshot.Token.DataRevision != visible.Snapshot.Token.DataRevision || context.Snapshot.Token.DatasetId != visible.Snapshot.Token.DatasetId ||
                context.Snapshot.Token.RestoreEpoch != visible.Snapshot.Token.RestoreEpoch || context.Snapshot.Token.AuthorityInstanceEpoch != visible.Snapshot.Token.AuthorityInstanceEpoch) throw new ReportSnapshotStaleException();
            return context;
        }
        public async Task<SalesReportExportResult> ExportToPathAsync(ExportRequest request, string path, bool overwriteAllowed, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); _reportCancellation = linked; IsBusy = true;
            try
            {
                var context = await GetAnalyticalExportContextAsync(request, linked.Token).ConfigureAwait(true);
                var authority = _reportSession.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.DailyCloseView);
                var progress = new Progress<string>(phase => { if (!ReferenceEquals(_reportCancellation, linked)) return; _reportFinalizing = phase == "finalizing"; Status = PosLocalization.T(_reportFinalizing ? "historyReport.exportFinalizing" : "historyReport.exportPreparing"); (CancelReportCommand as RelayCommand)?.RaiseCanExecuteChanged(); });
                return await new SalesReportExportService().ExportAsync(context, path, overwriteAllowed,
                    publication => _reportSession.PrepareSalesReportPublication(publication.Token, context.Filter, publication.TemporaryPath, publication.DestinationPath, publication.ContentSha256,
                        publication.ContentBytes, publication.OverwriteAllowed, PermissionCodes.DailyCloseView, linked.Token),
                    async ct => { DemandAnalyticalPermission(); if (!_reportSession.IsSalesReportReadAuthorityCurrent(authority, context.Filter)) throw new ReportSnapshotStaleException(); await _service.DemandAnalyticalReportCurrentAsync(context, ct).ConfigureAwait(false); }, linked.Token, progress).ConfigureAwait(true);
            }
            finally { if (ReferenceEquals(_reportCancellation, linked)) { _reportCancellation = null; _reportFinalizing = false; IsBusy = false; } NotifyReportCoverage(); }
        }
        private async Task<string> GetAnalyticalCsvContentAsync(ExportRequest request)
        {
            var context = await GetAnalyticalExportContextAsync(request, CancellationToken.None).ConfigureAwait(true);
            var authority = _reportSession.CaptureSalesReportReadAuthority(context.Filter, PermissionCodes.DailyCloseView);
            var builder = new StringBuilder(); builder.AppendLine(SalesReportCsvSerializer.Header); builder.AppendLine(SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Metadata(context.Snapshot)));
            var page = context.Snapshot.FirstPage;
            while (true)
            {
                foreach (var entry in page.Entries) builder.AppendLine(SalesReportCsvSerializer.Serialize(SalesReportCsvRow.Movement(context.Snapshot.Token, context.Snapshot.Coverage, entry)));
                if (builder.Length > 4 * 1024 * 1024) throw new InvalidOperationException("report_use_stream_export");
                if (!page.HasMore) break;
                page = await context.Query.GetPageAsync(context.Snapshot.Token, context.Filter, context.NativeTimePolicy, page.NextCursor, 200, CancellationToken.None).ConfigureAwait(true);
            }
            await _service.DemandAnalyticalReportCurrentAsync(context, CancellationToken.None).ConfigureAwait(true); DemandAnalyticalPermission();
            if (!_reportSession.IsSalesReportReadAuthorityCurrent(authority, context.Filter)) throw new ReportSnapshotStaleException();
            return builder.ToString();
        }
    }
}
