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

        public DailyReportViewModel(Pos.PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, _ => !IsBusy);
            PrintSummaryCommand = new AsyncRelayCommand(PrintSummaryAsync, _ => !IsBusy && !string.IsNullOrEmpty(SummaryReceiptPreview));
            LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, _ => !IsBusy);
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
            set { _salesCount = value; OnPropertyChanged(); }
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
            set { _netAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(NetAmountDisplay)); }
        }

        public string TotalAmountDisplay => MoneyClp.Format(TotalAmount);
        public string CashAmountDisplay => MoneyClp.Format(CashAmount);
        public string CardAmountDisplay => MoneyClp.Format(CardAmount);
        public string GrossSalesAmountDisplay => MoneyClp.Format(GrossSalesAmount);
        public string RefundsAmountDisplay => MoneyClp.Format(RefundsAmount);
        public string NetAmountDisplay => MoneyClp.Format(NetAmount);

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

        public ICommand LoadCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand PrintSummaryCommand { get; }
        public ICommand LoadHistoryCommand { get; }

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
                foreach (var s in summaries)
                {
                    HistoryRows.Add(new HistoryRow
                    {
                        DateText = s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        SalesCount = s.SalesCount,
                        TotalDisplay = MoneyClp.Format(s.TotalAmount),
                        CashDisplay = MoneyClp.Format(s.CashAmount),
                        CardDisplay = MoneyClp.Format(s.CardAmount),
                        NetDisplay = MoneyClp.Format(s.NetAmount)
                    });
                }
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

        private void RaiseCanExecuteChanged()
        {
            (LoadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportCsvCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintSummaryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (LoadHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
                await _executeAsync().ConfigureAwait(true);
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        public sealed class HistoryRow
        {
            public string DateText { get; set; }
            public int SalesCount { get; set; }
            public string TotalDisplay { get; set; }
            public string CashDisplay { get; set; }
            public string CardDisplay { get; set; }
            public string NetDisplay { get; set; }
        }
    }
}
