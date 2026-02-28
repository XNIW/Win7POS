using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class DailyReportViewModel : INotifyPropertyChanged
    {
        private readonly Pos.PosWorkflowService _service;

        private string _dateText = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private string _status = string.Empty;
        private int _salesCount;
        private int _totalAmount;
        private int _cashAmount;
        private int _cardAmount;
        private int _grossSalesAmount;
        private int _refundsAmount;
        private int _netAmount;
        private bool _isBusy;

        public DailyReportViewModel(Pos.PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, _ => !IsBusy);
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

        public int TotalAmount
        {
            get => _totalAmount;
            set { _totalAmount = value; OnPropertyChanged(); }
        }

        public int CashAmount
        {
            get => _cashAmount;
            set { _cashAmount = value; OnPropertyChanged(); }
        }

        public int CardAmount
        {
            get => _cardAmount;
            set { _cardAmount = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public int GrossSalesAmount
        {
            get => _grossSalesAmount;
            set { _grossSalesAmount = value; OnPropertyChanged(); }
        }

        public int RefundsAmount
        {
            get => _refundsAmount;
            set { _refundsAmount = value; OnPropertyChanged(); }
        }

        public int NetAmount
        {
            get => _netAmount;
            set { _netAmount = value; OnPropertyChanged(); }
        }

        public ICommand LoadCommand { get; }
        public ICommand ExportCsvCommand { get; }

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
    }
}
