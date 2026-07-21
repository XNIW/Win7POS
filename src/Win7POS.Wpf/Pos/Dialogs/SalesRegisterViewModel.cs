using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Core.Util;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum SalesRegisterFilter { Oggi, Ieri, Ultimi7Giorni, Mese, Periodo }

    public sealed class SalesRegisterViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PosWorkflowService _service;
        private readonly bool _useReceipt42;
        private readonly IPermissionService _permissionService;
        private readonly IOverrideAuthService _overrideAuthService;

        private SalesRegisterFilter _filter = SalesRegisterFilter.Oggi;
        private DateTime? _rangeFrom = null;
        private DateTime? _rangeTo = null;
        private string _codeSearch = "";
        private string _status = "";
        private bool _isBusy;
        private SaleRow _selectedSale;
        private string _detailSummary = "";
        private string _detailReceiptPreview = "";
        private bool _isPreviewLoading;
        private OperatorFilterItem _selectedOperator;
        private readonly bool _canViewAll;
        private readonly int? _forceOperatorId;
        private bool _isUnlocked;
        private CancellationTokenSource _selectionLoadCts;
        private int _selectionVersion;
        private CancellationTokenSource _listLoadCts;
        private int _listLoadVersion;
        private bool _disposed;

        public SalesRegisterViewModel(PosWorkflowService service, bool useReceipt42, IPermissionService permissionService, Action<long, SalesRegisterViewModel> onRequestRefund = null, bool isRefundScanMode = false, System.Collections.Generic.IReadOnlyList<(int id, string displayName)> operators = null, bool canViewAll = true, int? forceOperatorId = null, IOverrideAuthService overrideAuthService = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _useReceipt42 = useReceipt42;
            _permissionService = permissionService ?? throw new ArgumentNullException(nameof(permissionService));
            _overrideAuthService = overrideAuthService;
            _isRefundScanMode = isRefundScanMode;
            _canViewAll = canViewAll;
            _forceOperatorId = forceOperatorId;

            SalesList = new ObservableCollection<SaleRow>();
            DetailLines = new ObservableCollection<DetailLineRow>();
            OperatorFilterList = new ObservableCollection<OperatorFilterItem>();
            OperatorFilterList.Add(new OperatorFilterItem(0, string.Empty, true));
            if (operators != null)
            {
                foreach (var o in operators)
                    OperatorFilterList.Add(new OperatorFilterItem(o.id, o.displayName ?? ""));
                _selectedOperator = OperatorFilterList[0];
            }

            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            PrintCommand = new AsyncRelayCommand(PrintAsync, _ => !IsBusy && !IsPreviewLoading && SelectedSale != null && HasReceiptPreview);
            RefundCommand = new AsyncRelayCommand(RefundAsync, _ => !IsBusy && SelectedSale != null && SelectedSale.Kind == (int)SaleKind.Sale && !SelectedSale.IsVoided);
            ToggleFiscalPrintedModeCommand = new AsyncRelayCommand(ToggleFiscalPrintedModeAsync, _ => !IsBusy && _overrideAuthService != null);

            _onRequestRefund = onRequestRefund;

            PosLocalization.Current.LanguageChanged += OnLanguageChanged;
        }

        /// <summary>True se sbloccata con auth admin per refresh completo; le vendite stampate restano sempre incluse.</summary>
        public bool IsUnlocked
        {
            get => _isUnlocked;
            set { _isUnlocked = value; OnPropertyChanged(); OnPropertyChanged(nameof(LockGlyph)); }
        }

        public string LockGlyph => IsUnlocked ? PosLocalization.T("reports.fullViewShort") : PosLocalization.T("reports.limitedViewShort");

        /// <summary>True se il lucchetto è disponibile (overrideAuthService configurato).</summary>
        public bool HasLockFeature => _overrideAuthService != null;

        public ICommand ToggleFiscalPrintedModeCommand { get; }

        private readonly Action<long, SalesRegisterViewModel> _onRequestRefund;
        private readonly bool _isRefundScanMode;

        public bool IsRefundScanMode => _isRefundScanMode;
        public event Action RequestCloseDialog;

        public ObservableCollection<SaleRow> SalesList { get; }
        public ObservableCollection<DetailLineRow> DetailLines { get; }
        public ObservableCollection<OperatorFilterItem> OperatorFilterList { get; }

        public OperatorFilterItem SelectedOperator
        {
            get => _selectedOperator;
            set
            {
                if (_selectedOperator == value) return;
                _selectedOperator = value;
                OnPropertyChanged();
                if (LoadCommand.CanExecute(null)) LoadCommand.Execute(null);
            }
        }

        /// <summary>Mostra il filtro operatore solo se l'utente può vedere tutte le vendite (RegisterViewAll).</summary>
        public bool HasOperatorFilter => _canViewAll && OperatorFilterList.Count > 1;

        public SalesRegisterFilter Filter
        {
            get => _filter;
            set
            {
                if (_filter == value) return;
                _filter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilterOggi));
                OnPropertyChanged(nameof(FilterIeri));
                OnPropertyChanged(nameof(Filter7G));
                OnPropertyChanged(nameof(FilterMese));
                OnPropertyChanged(nameof(FilterPeriodo));
                OnPropertyChanged(nameof(IsRangeFilter));
                if (LoadCommand.CanExecute(null)) LoadCommand.Execute(null);
            }
        }

        public bool FilterOggi { get => Filter == SalesRegisterFilter.Oggi; set { if (value) Filter = SalesRegisterFilter.Oggi; } }
        public bool FilterIeri { get => Filter == SalesRegisterFilter.Ieri; set { if (value) Filter = SalesRegisterFilter.Ieri; } }
        public bool Filter7G { get => Filter == SalesRegisterFilter.Ultimi7Giorni; set { if (value) Filter = SalesRegisterFilter.Ultimi7Giorni; } }
        public bool FilterMese { get => Filter == SalesRegisterFilter.Mese; set { if (value) Filter = SalesRegisterFilter.Mese; } }
        public bool FilterPeriodo { get => Filter == SalesRegisterFilter.Periodo; set { if (value) Filter = SalesRegisterFilter.Periodo; } }
        public bool IsRangeFilter => Filter == SalesRegisterFilter.Periodo;

        public DateTime? RangeFrom
        {
            get => _rangeFrom ?? DateTime.Now.Date;
            set
            {
                _rangeFrom = value;
                OnPropertyChanged();
                if (IsRangeFilter && LoadCommand.CanExecute(null)) LoadCommand.Execute(null);
            }
        }

        public DateTime? RangeTo
        {
            get => _rangeTo ?? DateTime.Now.Date;
            set
            {
                _rangeTo = value;
                OnPropertyChanged();
                if (IsRangeFilter && LoadCommand.CanExecute(null)) LoadCommand.Execute(null);
            }
        }

        public string CodeSearch
        {
            get => _codeSearch;
            set { _codeSearch = value ?? ""; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value ?? ""; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public bool IsPreviewLoading
        {
            get => _isPreviewLoading;
            private set
            {
                if (_isPreviewLoading == value) return;
                _isPreviewLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowPreviewEmptyState));
                RaiseCanExecuteChanged();
            }
        }

        public SaleRow SelectedSale
        {
            get => _selectedSale;
            set
            {
                if (ReferenceEquals(_selectedSale, value)) return;
                _selectedSale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedSale));
                ClearSelectedSaleDetail();
                StartSelectedSaleLoad(value);
                RaiseCanExecuteChanged();
            }
        }

        public bool HasSelectedSale => SelectedSale != null;

        public string DetailSummary
        {
            get => _detailSummary;
            set { _detailSummary = value ?? ""; OnPropertyChanged(); }
        }

        public string DetailReceiptPreview
        {
            get => _detailReceiptPreview;
            private set
            {
                _detailReceiptPreview = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasReceiptPreview));
                OnPropertyChanged(nameof(ShowPreviewEmptyState));
                RaiseCanExecuteChanged();
            }
        }

        public bool HasReceiptPreview => !string.IsNullOrWhiteSpace(DetailReceiptPreview);
        public bool ShowPreviewEmptyState => !IsPreviewLoading && !HasReceiptPreview;

        public ICommand LoadCommand { get; }
        public ICommand PrintCommand { get; }
        public ICommand RefundCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public sealed class OperatorFilterItem : INotifyPropertyChanged
        {
            public int Id { get; }
            private readonly string _displayName;
            private readonly bool _isAll;
            public string DisplayName => _isAll ? PosLocalization.T("common.all") : _displayName;
            public OperatorFilterItem(int id, string displayName, bool isAll = false) { Id = id; _displayName = displayName ?? ""; _isAll = isAll; }

            public event PropertyChangedEventHandler PropertyChanged;
            public void NotifyLanguageChanged()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }

            internal void ClearSubscribers() => PropertyChanged = null;
        }

        private void GetCurrentRange(out long fromMs, out long toMs)
        {
            var now = DateTime.Now;
            DateTime from, to;
            switch (Filter)
            {
                case SalesRegisterFilter.Ieri:
                    from = now.Date.AddDays(-1);
                    to = now.Date;
                    break;
                case SalesRegisterFilter.Mese:
                    from = new DateTime(now.Year, now.Month, 1);
                    to = from.AddMonths(1);
                    break;
                case SalesRegisterFilter.Ultimi7Giorni:
                    from = now.Date.AddDays(-6);
                    to = now.Date.AddDays(1);
                    break;
                case SalesRegisterFilter.Periodo:
                    from = (RangeFrom ?? now.Date).Date;
                    var end = (RangeTo ?? from).Date;
                    if (end < from) { var tmp = from; from = end; end = tmp; }
                    to = end.AddDays(1);
                    break;
                default:
                    from = now.Date;
                    to = now.Date.AddDays(1);
                    break;
            }
            fromMs = new DateTimeOffset(from).ToUnixTimeMilliseconds();
            toMs = new DateTimeOffset(to).ToUnixTimeMilliseconds();
        }

        private async Task LoadAsync()
        {
            CancelListLoad();
            var cts = new CancellationTokenSource();
            _listLoadCts = cts;
            var version = ++_listLoadVersion;
            IsBusy = true;
            try
            {
                SalesList.Clear();
                SelectedSale = null;
                DetailLines.Clear();
                DetailSummary = "";
                DetailReceiptPreview = "";

                var includeFiscalPrinted = true;
                System.Collections.Generic.IReadOnlyList<RecentSaleItem> items;
                if (!string.IsNullOrWhiteSpace(CodeSearch))
                {
                    items = await _service.GetSalesByCodeFilterAsync(CodeSearch, includeFiscalPrinted).ConfigureAwait(true);
                    cts.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentListLoad(version)) return;
                    Status = PosLocalization.F("sales.searchStatus", items.Count);
                }
                else
                {
                    GetCurrentRange(out var fromMs, out var toMs);
                    int? operatorId;
                    if (_forceOperatorId.HasValue)
                        operatorId = _forceOperatorId;
                    else if (_canViewAll)
                    {
                        var opId = SelectedOperator?.Id;
                        operatorId = (opId.HasValue && opId.Value != 0) ? (int?)opId.Value : null;
                    }
                    else
                        operatorId = _forceOperatorId;
                    items = await _service.GetSalesBetweenAsync(fromMs, toMs, operatorId, includeFiscalPrinted).ConfigureAwait(true);
                    cts.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentListLoad(version)) return;

                    var countVendite = items.Count(i => i.Kind == (int)SaleKind.Sale && !i.VoidedBySaleId.HasValue);
                    var countResi = items.Count(i => i.Kind == (int)SaleKind.Refund);
                    var countStorni = items.Count(i => i.Kind == (int)SaleKind.Void);
                    var totalVendite = items.Where(i => i.Kind == (int)SaleKind.Sale && !i.VoidedBySaleId.HasValue).Sum(i => i.TotalMinor);
                    var totalResi = items.Where(i => i.Kind == (int)SaleKind.Refund).Sum(i => Math.Abs(i.TotalMinor));
                    var totalStorni = items.Where(i => i.Kind == (int)SaleKind.Void).Sum(i => Math.Abs(i.TotalMinor));
                    var netto = totalVendite - totalResi - totalStorni;
                    Status = PosLocalization.F(
                        "sales.summaryStatus",
                        items.Count,
                        countVendite,
                        MoneyClp.Format(totalVendite),
                        countResi,
                        MoneyClp.Format(totalResi),
                        countStorni,
                        MoneyClp.Format(totalStorni),
                        MoneyClp.Format(netto));
                }

                foreach (var x in items)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentListLoad(version)) return;
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedAtMs).ToLocalTime();
                    var isVoided = x.VoidedBySaleId.HasValue;
                    var kind = x.Kind;
                    SalesList.Add(new SaleRow
                    {
                        SaleId = x.SaleId,
                        SaleCode = x.SaleCode ?? "",
                        Kind = kind,
                        Total = x.TotalMinor,
                        TimeText = when.ToString("yyyy-MM-dd HH:mm"),
                        IsVoided = isVoided
                    });
                }

                if (_isRefundScanMode && SalesList.Count == 1)
                {
                    var single = SalesList[0];
                    if (single.Kind == (int)SaleKind.Sale && !single.IsVoided)
                    {
                        _onRequestRefund?.Invoke(single.SaleId, this);
                        RequestCloseDialog?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrentListLoad(version))
                    Status = PosLocalization.F("common.errorWithMessage", ex.Message);
            }
            finally
            {
                if (IsCurrentListLoad(version)) IsBusy = false;
            }
        }

        private bool IsCurrentListLoad(int version)
            => !_disposed && version == _listLoadVersion;

        private void CancelListLoad()
        {
            var cts = _listLoadCts;
            _listLoadCts = null;
            _listLoadVersion++;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        private void StartSelectedSaleLoad(SaleRow selected)
        {
            CancelSelectedSaleLoad();
            if (selected == null || _disposed)
            {
                IsPreviewLoading = false;
                return;
            }

            var cts = new CancellationTokenSource();
            _selectionLoadCts = cts;
            var version = ++_selectionVersion;
            IsPreviewLoading = true;
            _ = LoadDetailsAsync(selected, version, cts.Token);
        }

        private async Task LoadDetailsAsync(SaleRow selected, int version, CancellationToken cancellationToken)
        {
            if (selected == null) return;

            try
            {
                var detail = await _service.GetSaleDetailsAsync(selected.SaleId).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentSelection(selected, version)) return;
                if (detail == null)
                {
                    DetailSummary = PosLocalization.T("sales.notFound");
                    DetailReceiptPreview = "";
                    return;
                }

                DetailLines.Clear();
                foreach (var l in detail.Lines ?? Array.Empty<SaleLine>())
                    DetailLines.Add(new DetailLineRow
                    {
                        Name = l.Name ?? "-",
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice,
                        LineTotal = l.LineTotal
                    });

                DetailSummary = PosLocalization.F(
                    "sales.detailSummary",
                    MoneyClp.Format(detail.Sale.Total),
                    MoneyClp.Format(detail.Sale.PaidCash),
                    MoneyClp.Format(detail.Sale.PaidCard),
                    MoneyClp.Format(detail.Sale.Change),
                    DocumentStatusText(detail.Sale));

                var preview = await _service.GetReceiptPreviewBySaleIdAsync(selected.SaleId, _useReceipt42).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentSelection(selected, version)) return;
                DetailReceiptPreview = preview ?? "";
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                if (IsCurrentSelection(selected, version))
                {
                    DetailSummary = PosLocalization.T("sales.detailLoadError");
                    DetailReceiptPreview = "";
                }
            }
            finally
            {
                if (IsCurrentSelection(selected, version)) IsPreviewLoading = false;
            }
        }

        private bool IsCurrentSelection(SaleRow selected, int version)
            => !_disposed && version == _selectionVersion && ReferenceEquals(SelectedSale, selected);

        private void CancelSelectedSaleLoad()
        {
            var cts = _selectionLoadCts;
            _selectionLoadCts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        private void ClearSelectedSaleDetail()
        {
            DetailLines.Clear();
            DetailSummary = string.Empty;
            DetailReceiptPreview = string.Empty;
        }

        private static string DocumentStatusText(Sale sale)
        {
            if (sale == null)
            {
                return PosLocalization.T("sales.documentUnavailable");
            }

            if (sale.PdfPrinted)
            {
                return PosLocalization.T("sales.documentPrinted");
            }

            return sale.PaidCash > 0
                ? PosLocalization.T("sales.documentNotPrintedCash")
                : PosLocalization.T("sales.documentNotPrintedCard");
        }

        private async Task PrintAsync()
        {
            var selected = SelectedSale;
            var preview = DetailReceiptPreview;
            if (selected == null || string.IsNullOrWhiteSpace(preview)) return;
            IsBusy = true;
            try
            {
                _permissionService.Demand(
                    Win7POS.Core.Security.PermissionCodes.PosReprintReceipt,
                    PosLocalization.T("printer.reprintReceipt"));
                await _service.PrintReceiptTextAsync(
                    preview,
                    _useReceipt42,
                    "SALE_" + selected.SaleCode,
                    explicitUserAction: true).ConfigureAwait(true);
                Status = PosLocalization.F("sales.printStarted", selected.SaleCode);
            }
            catch (Exception ex)
            {
                Status = PosLocalization.F("sales.printFailed", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private Task RefundAsync()
        {
            if (SelectedSale == null) return Task.CompletedTask;
            _onRequestRefund?.Invoke(SelectedSale.SaleId, this);
            return Task.CompletedTask;
        }

        private async Task ToggleFiscalPrintedModeAsync()
        {
            if (_overrideAuthService == null) return;
            if (IsUnlocked)
            {
                IsUnlocked = false;
                if (LoadCommand.CanExecute(null)) LoadCommand.Execute(null);
                return;
            }
            var (ok, _) = await _overrideAuthService.RequestAdminOverrideAsync(PosLocalization.T("reports.fullSalesView")).ConfigureAwait(true);
            if (ok)
            {
                IsUnlocked = true;
                if (LoadCommand.CanExecute(null)) LoadCommand.Execute(null);
            }
        }

        private void RaiseCanExecuteChanged()
        {
            (LoadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RefundCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ToggleFiscalPrintedModeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(LockGlyph));
            foreach (var sale in SalesList)
            {
                sale.NotifyLanguageChanged();
            }
            foreach (var option in OperatorFilterList)
            {
                option.NotifyLanguageChanged();
            }

            StartSelectedSaleLoad(SelectedSale);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelListLoad();
            CancelSelectedSaleLoad();
            PosLocalization.Current.LanguageChanged -= OnLanguageChanged;
            ClearSelectedSaleDetail();
            foreach (var sale in SalesList) sale.ClearSubscribers();
            foreach (var option in OperatorFilterList) option.ClearSubscribers();
            SalesList.Clear();
            OperatorFilterList.Clear();
            (LoadCommand as AsyncRelayCommand)?.ClearSubscribers();
            (PrintCommand as AsyncRelayCommand)?.ClearSubscribers();
            (RefundCommand as AsyncRelayCommand)?.ClearSubscribers();
            (ToggleFiscalPrintedModeCommand as AsyncRelayCommand)?.ClearSubscribers();
            RequestCloseDialog = null;
            PropertyChanged = null;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class SaleRow : INotifyPropertyChanged
        {
            public long SaleId { get; set; }
            public string SaleCode { get; set; }
            public int Kind { get; set; }
            public string KindText => IsVoided || Kind == (int)SaleKind.Void
                ? PosLocalization.T("sales.kind.void")
                : Kind == (int)SaleKind.Refund
                    ? PosLocalization.T("sales.kind.refund")
                    : PosLocalization.T("sales.kind.sale");
            public long Total { get; set; }
            public string TotalDisplay => MoneyClp.Format(Total);
            public string TimeText { get; set; }
            public bool IsVoided { get; set; }
            public string VoidedText => IsVoided ? PosLocalization.T("sales.voided") : "";
            /// <summary>Vendita verde, Reso arancione, Storno rosso.</summary>
            public string BadgeBrush => IsVoided || Kind == (int)SaleKind.Void ? "#B71C1C" : (Kind == (int)SaleKind.Refund ? "#FF9800" : "#4CAF50");

            public event PropertyChangedEventHandler PropertyChanged;
            public void NotifyLanguageChanged()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KindText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VoidedText)));
            }

            internal void ClearSubscribers() => PropertyChanged = null;
        }

        public sealed class DetailLineRow
        {
            public string Name { get; set; }
            public int Quantity { get; set; }
            public long UnitPrice { get; set; }
            public long LineTotal { get; set; }
            public string UnitPriceDisplay => MoneyClp.Format(UnitPrice);
            public string LineTotalDisplay => MoneyClp.Format(LineTotal);
        }

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _execute;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
            public async void Execute(object parameter)
            {
                try
                {
                    await _execute().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "SalesRegister AsyncRelayCommand failed");
                }
            }
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            public void ClearSubscribers() => CanExecuteChanged = null;
        }
    }
}
