using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Core.Util;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum SalesRegisterFilter { Oggi, Ieri, Mese }

    public sealed class SalesRegisterViewModel : INotifyPropertyChanged
    {
        private readonly PosWorkflowService _service;
        private readonly bool _useReceipt42;

        private SalesRegisterFilter _filter = SalesRegisterFilter.Oggi;
        private string _codeSearch = "";
        private string _status = "";
        private bool _isBusy;
        private SaleRow _selectedSale;
        private string _detailSummary = "";
        private string _detailReceiptPreview = "";

        public SalesRegisterViewModel(PosWorkflowService service, bool useReceipt42, Action<long, SalesRegisterViewModel> onRequestRefund = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _useReceipt42 = useReceipt42;

            SalesList = new ObservableCollection<SaleRow>();
            DetailLines = new ObservableCollection<DetailLineRow>();

            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            PrintCommand = new AsyncRelayCommand(PrintAsync, _ => !IsBusy && SelectedSale != null);
            RefundCommand = new AsyncRelayCommand(RefundAsync, _ => !IsBusy && SelectedSale != null && SelectedSale.Kind == (int)SaleKind.Sale && !SelectedSale.IsVoided);

            _onRequestRefund = onRequestRefund;

            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SelectedSale))
                    _ = LoadDetailsAsync();
            };
        }

        private readonly Action<long, SalesRegisterViewModel> _onRequestRefund;

        public ObservableCollection<SaleRow> SalesList { get; }
        public ObservableCollection<DetailLineRow> DetailLines { get; }

        public SalesRegisterFilter Filter
        {
            get => _filter;
            set { _filter = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilterOggi)); OnPropertyChanged(nameof(FilterIeri)); OnPropertyChanged(nameof(FilterMese)); }
        }

        public bool FilterOggi { get => Filter == SalesRegisterFilter.Oggi; set { if (value) Filter = SalesRegisterFilter.Oggi; } }
        public bool FilterIeri { get => Filter == SalesRegisterFilter.Ieri; set { if (value) Filter = SalesRegisterFilter.Ieri; } }
        public bool FilterMese { get => Filter == SalesRegisterFilter.Mese; set { if (value) Filter = SalesRegisterFilter.Mese; } }

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

        public SaleRow SelectedSale
        {
            get => _selectedSale;
            set { _selectedSale = value; OnPropertyChanged(); }
        }

        public string DetailSummary
        {
            get => _detailSummary;
            set { _detailSummary = value ?? ""; OnPropertyChanged(); }
        }

        public string DetailReceiptPreview
        {
            get => _detailReceiptPreview;
            set { _detailReceiptPreview = value ?? ""; OnPropertyChanged(); }
        }

        public ICommand LoadCommand { get; }
        public ICommand PrintCommand { get; }
        public ICommand RefundCommand { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        private static void GetDateRange(SalesRegisterFilter filter, out long fromMs, out long toMs)
        {
            var now = DateTime.Now;
            DateTime from, to;
            switch (filter)
            {
                case SalesRegisterFilter.Ieri:
                    from = now.Date.AddDays(-1);
                    to = now.Date;
                    break;
                case SalesRegisterFilter.Mese:
                    from = new DateTime(now.Year, now.Month, 1);
                    to = from.AddMonths(1);
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
            IsBusy = true;
            try
            {
                SalesList.Clear();
                SelectedSale = null;
                DetailLines.Clear();
                DetailSummary = "";
                DetailReceiptPreview = "";

                System.Collections.Generic.IReadOnlyList<RecentSaleItem> items;
                if (!string.IsNullOrWhiteSpace(CodeSearch))
                {
                    items = await _service.GetSalesByCodeFilterAsync(CodeSearch).ConfigureAwait(true);
                    Status = items.Count + " vendite (ricerca codice).";
                }
                else
                {
                    GetDateRange(Filter, out var fromMs, out var toMs);
                    items = await _service.GetSalesBetweenAsync(fromMs, toMs).ConfigureAwait(true);
                    Status = items.Count + " vendite nel periodo.";
                }

                foreach (var x in items)
                {
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedAtMs).ToLocalTime();
                    SalesList.Add(new SaleRow
                    {
                        SaleId = x.SaleId,
                        SaleCode = x.SaleCode ?? "",
                        Kind = x.Kind,
                        KindText = x.Kind == (int)SaleKind.Refund ? "Reso" : "Vendita",
                        Total = x.TotalMinor,
                        TimeText = when.ToString("yyyy-MM-dd HH:mm"),
                        IsVoided = x.VoidedBySaleId.HasValue
                    });
                }
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadDetailsAsync()
        {
            if (SelectedSale == null)
            {
                DetailLines.Clear();
                DetailSummary = "";
                DetailReceiptPreview = "";
                return;
            }

            try
            {
                var detail = await _service.GetSaleDetailsAsync(SelectedSale.SaleId).ConfigureAwait(true);
                if (detail == null)
                {
                    DetailSummary = "Vendita non trovata.";
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

                DetailSummary = $"Totale: {MoneyClp.Format(detail.Sale.Total)}  |  " +
                    $"Contanti: {MoneyClp.Format(detail.Sale.PaidCash)}  |  " +
                    $"Carta: {MoneyClp.Format(detail.Sale.PaidCard)}  |  " +
                    $"Resto: {MoneyClp.Format(detail.Sale.Change)}";

                var preview = await _service.GetReceiptPreviewBySaleIdAsync(SelectedSale.SaleId, _useReceipt42).ConfigureAwait(true);
                DetailReceiptPreview = preview ?? "";
            }
            catch
            {
                DetailSummary = "Errore caricamento dettagli.";
                DetailReceiptPreview = "";
            }
        }

        private async Task PrintAsync()
        {
            if (SelectedSale == null) return;
            IsBusy = true;
            try
            {
                await _service.PrintReceiptBySaleIdAsync(SelectedSale.SaleId, _useReceipt42).ConfigureAwait(true);
                Status = "Stampa avviata: " + SelectedSale.SaleCode;
            }
            catch (Exception ex)
            {
                Status = "Stampa fallita: " + ex.Message;
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

        private void RaiseCanExecuteChanged()
        {
            (LoadCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RefundCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class SaleRow
        {
            public long SaleId { get; set; }
            public string SaleCode { get; set; }
            public int Kind { get; set; }
            public string KindText { get; set; }
            public int Total { get; set; }
            public string TotalDisplay => MoneyClp.Format(Total);
            public string TimeText { get; set; }
            public bool IsVoided { get; set; }
            public string VoidedText => IsVoided ? "Annullata" : "";
        }

        public sealed class DetailLineRow
        {
            public string Name { get; set; }
            public int Quantity { get; set; }
            public int UnitPrice { get; set; }
            public int LineTotal { get; set; }
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
            public async void Execute(object parameter) => await _execute().ConfigureAwait(true);
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
