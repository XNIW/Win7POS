using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Receipt;
using Win7POS.Core.Reports;
using Win7POS.Core.Util;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class DailyReportViewModel : INotifyPropertyChanged
    {
        private readonly Pos.PosWorkflowService _service;

        private string _dateText = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private string _status = string.Empty;
        private int _salesCount;
        private long _totalAmount;
        private long _cashAmount;
        private long _cardAmount;
        private long _grossSalesAmount;
        private long _refundsAmount;
        private long _netAmount;
        private bool _isBusy;
        private string _summaryReceiptPreview = string.Empty;
        private string _historyFromText = DateTime.Now.AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private string _historyToText = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private bool _reportLoaded;
        private int _selectedTabIndex;

        public DailyReportViewModel(Pos.PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, _ => !IsBusy);
            PrintSummaryCommand = new AsyncRelayCommand(PrintSummaryAsync, _ => !IsBusy && !string.IsNullOrEmpty(SummaryReceiptPreview));
            PrintSelectedHistoryCommand = new AsyncRelayCommand(PrintSummaryAsync, _ => !IsBusy && SelectedHistoryRow != null && !string.IsNullOrEmpty(SummaryReceiptPreview));
            LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, _ => !IsBusy);
            ExportHistoryCsvCommand = new AsyncRelayCommand(ExportHistoryCsvAsync, _ => !IsBusy && SelectedHistoryRow != null);
            FilterOggiCommand = new AsyncRelayCommand(ApplyFilterOggiAsync, _ => !IsBusy);
            FilterIeriCommand = new AsyncRelayCommand(ApplyFilterIeriAsync, _ => !IsBusy);
            FilterQuestaSettimanaCommand = new AsyncRelayCommand(ApplyFilterQuestaSettimanaAsync, _ => !IsBusy);
            FilterQuestoMeseCommand = new AsyncRelayCommand(ApplyFilterQuestoMeseAsync, _ => !IsBusy);
            FilterMeseScorsoCommand = new AsyncRelayCommand(ApplyFilterMeseScorsoAsync, _ => !IsBusy);
            FilterAnnoCorrenteCommand = new AsyncRelayCommand(ApplyFilterAnnoCorrenteAsync, _ => !IsBusy);
            HistoryRows = new ObservableCollection<HistoryRow>();
        }

        public string DateText
        {
            get => _dateText;
            set { _dateText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public int SalesCount
        {
            get => _salesCount;
            set { _salesCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TicketMedioDisplay)); }
        }

        public long TotalAmount
        {
            get => _totalAmount;
            set { _totalAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalAmountDisplay)); }
        }

        public long CashAmount
        {
            get => _cashAmount;
            set { _cashAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CashAmountDisplay)); }
        }

        public long CardAmount
        {
            get => _cardAmount;
            set { _cardAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CardAmountDisplay)); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public long GrossSalesAmount
        {
            get => _grossSalesAmount;
            set { _grossSalesAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(GrossSalesAmountDisplay)); }
        }

        public long RefundsAmount
        {
            get => _refundsAmount;
            set { _refundsAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(RefundsAmountDisplay)); }
        }

        public long NetAmount
        {
            get => _netAmount;
            set { _netAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetAmountDisplay)); OnPropertyChanged(nameof(TicketMedioDisplay)); }
        }

        public string TotalAmountDisplay => MoneyClp.FormatDisplay(TotalAmount);
        public string CashAmountDisplay => MoneyClp.FormatDisplay(CashAmount);
        public string CardAmountDisplay => MoneyClp.FormatDisplay(CardAmount);
        public string GrossSalesAmountDisplay => MoneyClp.FormatDisplay(GrossSalesAmount);
        public string RefundsAmountDisplay => MoneyClp.FormatDisplay(RefundsAmount);
        public string NetAmountDisplay => MoneyClp.FormatDisplay(NetAmount);
        public string TicketMedioDisplay => SalesCount > 0 ? MoneyClp.FormatDisplay(NetAmount / SalesCount) : "0";

        /// <summary>Badge stato: "Report caricato" / "Nessun dato" per header.</summary>
        public string StatusBadgeText => !_reportLoaded ? "" : (SalesCount > 0 || NetAmount != 0 ? "Report caricato" : "Nessun dato");

        /// <summary>True se il badge stato va mostrato (evita converter StringToVisibility).</summary>
        public bool ShowStatusBadge => !string.IsNullOrEmpty(StatusBadgeText);

        public string SummaryReceiptPreview
        {
            get => _summaryReceiptPreview;
            set { _summaryReceiptPreview = value ?? string.Empty; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public string HistoryFromText
        {
            get => _historyFromText;
            set { _historyFromText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string HistoryToText
        {
            get => _historyToText;
            set { _historyToText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public ObservableCollection<HistoryRow> HistoryRows { get; }

        private HistoryRow _selectedHistoryRow;
        public HistoryRow SelectedHistoryRow
        {
            get => _selectedHistoryRow;
            set
            {
                if (_selectedHistoryRow == value) return;
                _selectedHistoryRow = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasHistorySelection));
                OnPropertyChanged(nameof(PreviewPlaceholderText));
                OnPropertyChanged(nameof(ShowDetailPanel));
                OnPropertyChanged(nameof(ShowPlaceholder));
                _ = LoadPreviewForSelectedHistoryAsync();
                RaiseCanExecuteChanged();
                (ExportHistoryCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public bool HasHistorySelection => SelectedHistoryRow != null;

        private int _historyRowCount;
        public int HistoryRowCount { get => _historyRowCount; private set { _historyRowCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHistoryRows)); OnPropertyChanged(nameof(ShowHistoryEmptyMessage)); } }
        public bool HasHistoryRows => HistoryRowCount > 0;

        /// <summary>True per mostrare "Nessun movimento nel periodo" (evita converter inverso).</summary>
        public bool ShowHistoryEmptyMessage => !HasHistoryRows;

        /// <summary>Testo placeholder quando nessuna riga storico selezionata.</summary>
        public string PreviewPlaceholderText => HasHistorySelection ? "" : "Seleziona una data dallo storico per vedere il dettaglio.";

        /// <summary>True per mostrare dettaglio giorno nello storico (evita converter).</summary>
        public bool ShowDetailPanel => HasHistorySelection;

        /// <summary>True per mostrare placeholder "Seleziona una data..." nello storico (evita converter inverso).</summary>
        public bool ShowPlaceholder => !HasHistorySelection;

        public ICommand LoadCommand { get; }
        public ICommand FilterOggiCommand { get; }
        public ICommand FilterIeriCommand { get; }
        public ICommand FilterQuestaSettimanaCommand { get; }
        public ICommand FilterQuestoMeseCommand { get; }
        public ICommand FilterMeseScorsoCommand { get; }
        public ICommand FilterAnnoCorrenteCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand PrintSummaryCommand { get; }
        public ICommand PrintSelectedHistoryCommand { get; }
        public ICommand LoadHistoryCommand { get; }
        public ICommand ExportHistoryCsvCommand { get; }

        /// <summary>0 = Giornaliero, 1 = Storico. Cambiato dai filtri rapidi (es. Settimana → tab Storico).</summary>
        public int SelectedTabIndex { get => _selectedTabIndex; set { _selectedTabIndex = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;

        private async Task LoadAsync()
        {
            if (!TryParseDate(out var date))
            {
                Status = "Data non valida. Usa yyyy-MM-dd.";
                return;
            }

            IsBusy = true;
            try
            {
                DailySalesSummary summary = await _service.GetDailySummaryAsync(date).ConfigureAwait(true);
                SalesCount = summary.SalesCount;
                TotalAmount = summary.TotalAmount;
                CashAmount = summary.CashAmount;
                CardAmount = summary.CardAmount;
                GrossSalesAmount = summary.GrossSalesAmount;
                RefundsAmount = summary.RefundsAmount;
                NetAmount = summary.NetAmount;
                var shop = await _service.GetShopInfoAsync().ConfigureAwait(true);
                UpdateSummaryReceiptPreview(date, summary, shop);
                _reportLoaded = true;
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(ShowStatusBadge));
                Status = "Report caricato.";
            }
            catch (Exception ex)
            {
                Status = "Errore caricamento report: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExportCsvAsync()
        {
            if (!TryParseDate(out var date))
            {
                Status = "Data non valida. Usa yyyy-MM-dd.";
                return;
            }

            IsBusy = true;
            try
            {
                var path = await _service.ExportDailyCsvAsync(date).ConfigureAwait(true);
                Status = "Export CSV: " + path;
            }
            catch (Exception ex)
            {
                Status = "Errore export CSV: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExportHistoryCsvAsync()
        {
            if (SelectedHistoryRow == null) return;
            IsBusy = true;
            try
            {
                var path = await _service.ExportDailyCsvAsync(SelectedHistoryRow.Date).ConfigureAwait(true);
                Status = "Export CSV: " + path;
            }
            catch (Exception ex)
            {
                Status = "Errore export CSV: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void UpdateSummaryReceiptPreview(DateTime date, DailySalesSummary summary, ReceiptShopInfo shop = null)
        {
            var model = new DailyTakingsReceiptModel
            {
                Date = date,
                SalesCount = summary.SalesCount,
                TotalAmount = summary.TotalAmount,
                CashAmount = summary.CashAmount,
                CardAmount = summary.CardAmount,
                GrossSalesAmount = summary.GrossSalesAmount,
                RefundsAmount = summary.RefundsAmount,
                NetAmount = summary.NetAmount
            };
            var lines = DailyTakingsReceiptFormatter.Format(model, shop ?? new ReceiptShopInfo());
            SummaryReceiptPreview = string.Join(Environment.NewLine, lines);
        }

        private async Task PrintSummaryAsync()
        {
            if (string.IsNullOrEmpty(SummaryReceiptPreview)) return;
            IsBusy = true;
            try
            {
                var result = await _service.PrintReceiptTextAsync(SummaryReceiptPreview, true, "DAILY_SUMMARY_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture)).ConfigureAwait(true);
                Status = "Stampa avviata.";
            }
            catch (Exception ex)
            {
                Status = "Errore stampa: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadHistoryAsync()
        {
            if (!TryParseHistoryDates(out var from, out var to))
            {
                Status = "Date storico non valide. Usa yyyy-MM-dd.";
                return;
            }
            if (from > to)
            {
                Status = "Data da deve essere <= data a.";
                return;
            }

            IsBusy = true;
            try
            {
                var summaries = await _service.GetDailySummariesAsync(from, to).ConfigureAwait(true);
                HistoryRows.Clear();
                HistoryRowCount = 0;
                foreach (var s in summaries)
                {
                    HistoryRows.Add(new HistoryRow
                    {
                        Date = s.Date,
                        DateText = s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        SalesCount = s.SalesCount,
                        GrossDisplay = MoneyClp.FormatDisplay(s.GrossSalesAmount),
                        RefundsDisplay = MoneyClp.FormatDisplay(s.RefundsAmount),
                        NetDisplay = MoneyClp.FormatDisplay(s.NetAmount),
                        CashDisplay = MoneyClp.FormatDisplay(s.CashAmount),
                        CardDisplay = MoneyClp.FormatDisplay(s.CardAmount)
                    });
                }
                HistoryRowCount = HistoryRows.Count;
                Status = summaries.Count + " giorni caricati nello storico.";
            }
            catch (Exception ex)
            {
                Status = "Errore caricamento storico: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool TryParseHistoryDates(out DateTime from, out DateTime to)
        {
            from = default;
            to = default;
            return DateTime.TryParseExact(HistoryFromText ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
                && DateTime.TryParseExact(HistoryToText ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to);
        }

        private bool TryParseDate(out DateTime date)
        {
            return DateTime.TryParseExact(
                DateText ?? string.Empty,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private async Task LoadPreviewForSelectedHistoryAsync()
        {
            if (SelectedHistoryRow == null) return;
            try
            {
                var summary = await _service.GetDailySummaryAsync(SelectedHistoryRow.Date).ConfigureAwait(true);
                SalesCount = summary.SalesCount;
                TotalAmount = summary.TotalAmount;
                CashAmount = summary.CashAmount;
                CardAmount = summary.CardAmount;
                GrossSalesAmount = summary.GrossSalesAmount;
                RefundsAmount = summary.RefundsAmount;
                NetAmount = summary.NetAmount;
                var shop = await _service.GetShopInfoAsync().ConfigureAwait(true);
                UpdateSummaryReceiptPreview(SelectedHistoryRow.Date, summary, shop);
            }
            catch { SummaryReceiptPreview = ""; }
        }

        private static DateTime StartOfWeek(DateTime d)
        {
            var diff = (7 + (d.DayOfWeek - DayOfWeek.Monday)) % 7;
            return d.Date.AddDays(-diff);
        }

        private async Task ApplyFilterOggiAsync()
        {
            SelectedTabIndex = 0; // resta su Giornaliero
            DateText = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await LoadAsync().ConfigureAwait(true);
        }

        private async Task ApplyFilterIeriAsync()
        {
            SelectedTabIndex = 0; // resta su Giornaliero
            DateText = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await LoadAsync().ConfigureAwait(true);
        }

        private async Task ApplyFilterQuestaSettimanaAsync()
        {
            SelectedTabIndex = 1;
            var today = DateTime.Now.Date;
            var from = StartOfWeek(today);
            HistoryFromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            HistoryToText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await LoadHistoryAsync().ConfigureAwait(true);
        }

        private async Task ApplyFilterQuestoMeseAsync()
        {
            SelectedTabIndex = 1;
            var now = DateTime.Now;
            var from = new DateTime(now.Year, now.Month, 1);
            HistoryFromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            HistoryToText = now.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await LoadHistoryAsync().ConfigureAwait(true);
        }

        private async Task ApplyFilterMeseScorsoAsync()
        {
            SelectedTabIndex = 1;
            var now = DateTime.Now;
            var from = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
            var to = from.AddMonths(1).AddDays(-1);
            HistoryFromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            HistoryToText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await LoadHistoryAsync().ConfigureAwait(true);
        }

        private async Task ApplyFilterAnnoCorrenteAsync()
        {
            SelectedTabIndex = 1;
            var now = DateTime.Now;
            var from = new DateTime(now.Year, 1, 1);
            HistoryFromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            HistoryToText = now.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await LoadHistoryAsync().ConfigureAwait(true);
        }

        private void RaiseCanExecuteChanged()
        {
            (LoadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintSummaryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintSelectedHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (LoadHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportHistoryCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FilterOggiCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FilterIeriCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FilterQuestaSettimanaCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FilterQuestoMeseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FilterMeseScorsoCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (FilterAnnoCorrenteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> executeAsync, Func<object, bool> canExecute = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

            public async void Execute(object parameter)
            {
                try
                {
                    await _executeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "DailyReport AsyncRelayCommand failed");
                }
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        public sealed class HistoryRow
        {
            public DateTime Date { get; set; }
            public string DateText { get; set; }
            public int SalesCount { get; set; }
            public string GrossDisplay { get; set; }
            public string RefundsDisplay { get; set; }
            public string NetDisplay { get; set; }
            public string CashDisplay { get; set; }
            public string CardDisplay { get; set; }
        }
    }
}
