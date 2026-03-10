using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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
        private long _periodNetAmount;
        private long _periodGrossAmount;
        private long _periodRefundsAmount;
        private long _periodCashAmount;
        private long _periodCardAmount;
        private int _periodSalesCount;

        public DailyReportViewModel(Pos.PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            LoadCommand = new AsyncRelayCommand(LoadAsync, _ => !IsBusy);
            PrintSummaryCommand = new AsyncRelayCommand(PrintSummaryAsync, _ => !IsBusy && !string.IsNullOrEmpty(SummaryReceiptPreview));
            PrintSelectedHistoryCommand = new AsyncRelayCommand(PrintStampaRiepilogoAsync, _ => !IsBusy && CanPrintStampaRiepilogo());
            LoadHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync, _ => !IsBusy);
            ExportCommand = new AsyncRelayCommand(ExportAsync, _ => !IsBusy && CanExport());
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
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowFooterStatus)); }
        }

        /// <summary>Consente alla View di impostare il messaggio di stato (es. dopo Salva con nome).</summary>
        public void SetStatus(string message)
        {
            Status = message ?? string.Empty;
            OnPropertyChanged(nameof(ShowFooterStatus));
        }

        /// <summary>True per mostrare il messaggio di stato nel footer (evita duplicazione con badge: nasconde "Report caricato.").</summary>
        public bool ShowFooterStatus => !string.IsNullOrEmpty(Status) && Status != "Report caricato.";

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
                OnPropertyChanged(nameof(ShowSelectedRowDetail));
                OnPropertyChanged(nameof(ShowReceiptPreview));
                _ = LoadPreviewForSelectedHistoryAsync();
                RaiseCanExecuteChanged();
                (ExportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
        public ICommand ExportCommand { get; }
        public ICommand PrintSummaryCommand { get; }
        public ICommand PrintSelectedHistoryCommand { get; }
        public ICommand LoadHistoryCommand { get; }

        /// <summary>True se in Storico serve chiedere Periodo vs Giorno (ha righe e selezione).</summary>
        public bool NeedsExportScopeChoice => SelectedTabIndex == 1 && HasHistoryRows && HasHistorySelection;

        /// <summary>Richiesta export: nome base senza estensione + ambito. La View mostra SaveFileDialog e chiede contenuto.</summary>
        public event Action<ExportRequest> ExportRequested;

        /// <summary>In Storico: la View deve mostrare scelta "Periodo" / "Giorno selezionato", poi chiamare ChooseExportPeriod o ChooseExportDay.</summary>
        public event Action RequestExportScopeChoice;

        /// <summary>Riepilogo periodo (somma giorni caricati nello storico).</summary>
        public long PeriodNetAmount { get => _periodNetAmount; private set { _periodNetAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeriodNetDisplay)); OnPropertyChanged(nameof(PeriodTicketAverageDisplay)); } }
        public long PeriodGrossAmount { get => _periodGrossAmount; private set { _periodGrossAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeriodGrossDisplay)); } }
        public long PeriodRefundsAmount { get => _periodRefundsAmount; private set { _periodRefundsAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeriodRefundsDisplay)); } }
        public long PeriodCashAmount { get => _periodCashAmount; private set { _periodCashAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeriodCashDisplay)); } }
        public long PeriodCardAmount { get => _periodCardAmount; private set { _periodCardAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeriodCardDisplay)); } }
        public int PeriodSalesCount { get => _periodSalesCount; private set { _periodSalesCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PeriodTicketAverageDisplay)); } }
        public string PeriodNetDisplay => MoneyClp.FormatDisplay(PeriodNetAmount);
        public string PeriodGrossDisplay => MoneyClp.FormatDisplay(PeriodGrossAmount);
        public string PeriodRefundsDisplay => MoneyClp.FormatDisplay(PeriodRefundsAmount);
        public string PeriodCashDisplay => MoneyClp.FormatDisplay(PeriodCashAmount);
        public string PeriodCardDisplay => MoneyClp.FormatDisplay(PeriodCardAmount);
        public string PeriodTicketAverageDisplay => PeriodSalesCount > 0 ? MoneyClp.FormatDisplay(PeriodNetAmount / PeriodSalesCount) : "0";

        private int _markedCount;
        private long _markedNetAmount, _markedGrossAmount, _markedRefundsAmount, _markedCashAmount, _markedCardAmount;
        private int _markedSalesCount;
        public int MarkedCount { get => _markedCount; private set { _markedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailPanelTitle)); OnPropertyChanged(nameof(StampaRiepilogoSubtitle)); OnPropertyChanged(nameof(ShowMarkedSummary)); OnPropertyChanged(nameof(ShowSingleDayDetail)); OnPropertyChanged(nameof(ShowMultiDaySummary)); OnPropertyChanged(nameof(ShowSelectionPlaceholder)); OnPropertyChanged(nameof(ShowSelectedRowDetail)); OnPropertyChanged(nameof(ShowReceiptPreview)); } }
        public int MarkedSalesCount { get => _markedSalesCount; private set { _markedSalesCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkedTicketAverageDisplay)); } }
        public long MarkedNetAmount { get => _markedNetAmount; private set { _markedNetAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkedNetDisplay)); OnPropertyChanged(nameof(MarkedTicketAverageDisplay)); } }
        public long MarkedGrossAmount { get => _markedGrossAmount; private set { _markedGrossAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkedGrossDisplay)); } }
        public long MarkedRefundsAmount { get => _markedRefundsAmount; private set { _markedRefundsAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkedRefundsDisplay)); } }
        public long MarkedCashAmount { get => _markedCashAmount; private set { _markedCashAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkedCashDisplay)); } }
        public long MarkedCardAmount { get => _markedCardAmount; private set { _markedCardAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(MarkedCardDisplay)); } }
        public string MarkedNetDisplay => MoneyClp.FormatDisplay(MarkedNetAmount);
        public string MarkedGrossDisplay => MoneyClp.FormatDisplay(MarkedGrossAmount);
        public string MarkedRefundsDisplay => MoneyClp.FormatDisplay(MarkedRefundsAmount);
        public string MarkedCashDisplay => MoneyClp.FormatDisplay(MarkedCashAmount);
        public string MarkedCardDisplay => MoneyClp.FormatDisplay(MarkedCardAmount);
        public string MarkedTicketAverageDisplay => MarkedSalesCount > 0 ? MoneyClp.FormatDisplay(MarkedNetAmount / MarkedSalesCount) : "0";
        public bool HasMarkedRows => MarkedCount > 0;
        public bool ShowMarkedSummary => MarkedCount > 0;
        public bool ShowSingleDayDetail => MarkedCount == 1;
        public bool ShowMultiDaySummary => MarkedCount >= 2;
        /// <summary>Mostra placeholder "Seleziona una data..." quando nessun giorno è spuntato.</summary>
        public bool ShowSelectionPlaceholder => MarkedCount == 0;
        /// <summary>Mostra dettaglio riga selezionata (data + totali) quando nessun giorno è spuntato ma c'è una riga selezionata.</summary>
        public bool ShowSelectedRowDetail => MarkedCount == 0 && HasHistorySelection;
        /// <summary>Mostra anteprima ricevuta (1 giorno spuntato oppure riga selezionata senza spunte).</summary>
        public bool ShowReceiptPreview => ShowSingleDayDetail || ShowSelectedRowDetail;
        public string DetailPanelTitle => MarkedCount == 0 ? "Dettaglio selezione" : (MarkedCount == 1 ? "Dettaglio giorno selezionato" : "Riepilogo giorni selezionati");
        public string StampaRiepilogoSubtitle => GetStampaRiepilogoSubtitle();

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

        private bool CanExport()
        {
            if (SelectedTabIndex == 0)
                return TryParseDate(out _);
            return HasHistoryRows || HasHistorySelection || HasMarkedRows;
        }

        private async Task ExportAsync()
        {
            if (SelectedTabIndex == 0)
            {
                if (!TryParseDate(out var date))
                {
                    Status = "Data non valida. Usa yyyy-MM-dd.";
                    return;
                }
                var request = ExportRequest.ForDaily(date);
                ExportRequested?.Invoke(request);
                return;
            }
            if (SelectedTabIndex == 1)
            {
                if ((HasHistoryRows && HasHistorySelection) || HasMarkedRows)
                {
                    RequestExportScopeChoice?.Invoke();
                    return;
                }
                if (HasHistoryRows)
                {
                    ChooseExportPeriod();
                    return;
                }
                if (HasHistorySelection)
                {
                    ChooseExportDay();
                    return;
                }
                Status = "Carica lo storico, seleziona un giorno o spunta i giorni da esportare.";
            }
        }

        /// <summary>Chiamato dalla View dopo scelta "Esporta periodo". Crea la richiesta e solleva ExportRequested.</summary>
        public void ChooseExportPeriod()
        {
            if (!TryParseHistoryDates(out var from, out var to) || from > to)
            {
                Status = "Date periodo non valide.";
                return;
            }
            ExportRequested?.Invoke(ExportRequest.ForPeriod(from, to));
        }

        /// <summary>Chiamato dalla View dopo scelta "Esporta giorno selezionato". Crea la richiesta e solleva ExportRequested.</summary>
        public void ChooseExportDay()
        {
            if (SelectedHistoryRow == null)
            {
                Status = "Nessun giorno selezionato.";
                return;
            }
            ExportRequested?.Invoke(ExportRequest.ForDay(SelectedHistoryRow.Date));
        }

        /// <summary>Chiamato dalla View dopo scelta "Esporta giorni selezionati". Crea la richiesta e solleva ExportRequested.</summary>
        public void ChooseExportMarked()
        {
            var dates = HistoryRows.Where(r => r.IsMarked).Select(r => r.Date).ToList();
            if (dates.Count == 0)
            {
                Status = "Nessun giorno spuntato. Usa le checkbox per includere giorni.";
                return;
            }
            ExportRequested?.Invoke(ExportRequest.ForMarked(dates));
        }

        /// <summary>Restituisce il contenuto CSV per la richiesta (chiamato dalla View dopo SaveFileDialog con estensione .csv).</summary>
        public async Task<string> GetExportCsvContentAsync(ExportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            switch (request.Scope)
            {
                case ExportScope.Daily:
                case ExportScope.Day:
                    return await _service.GetDailyCsvContentAsync(request.Date.Value).ConfigureAwait(true);
                case ExportScope.Period:
                    return await _service.GetPeriodCsvContentAsync(request.From.Value, request.To.Value).ConfigureAwait(true);
                case ExportScope.Marked:
                    return await _service.GetDaysCsvContentAsync(request.Dates ?? Array.Empty<DateTime>()).ConfigureAwait(true);
                default:
                    throw new ArgumentOutOfRangeException(nameof(request));
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

        private bool CanPrintStampaRiepilogo()
        {
            if (SelectedTabIndex == 0)
                return !string.IsNullOrEmpty(SummaryReceiptPreview);
            if (MarkedCount >= 1) return true;
            return SelectedHistoryRow != null && !string.IsNullOrEmpty(SummaryReceiptPreview);
        }

        private async Task PrintStampaRiepilogoAsync()
        {
            string textToPrint = null;
            if (SelectedTabIndex == 0)
            {
                textToPrint = SummaryReceiptPreview;
            }
            else if (MarkedCount >= 2)
            {
                textToPrint = BuildMarkedAggregateReceiptText();
            }
            else if (MarkedCount == 1)
            {
                HistoryRow marked = null;
                foreach (var row in HistoryRows)
                    if (row.IsMarked) { marked = row; break; }
                if (marked != null)
                {
                    var summary = await _service.GetDailySummaryAsync(marked.Date).ConfigureAwait(true);
                    var shop = await _service.GetShopInfoAsync().ConfigureAwait(true);
                    var model = new DailyTakingsReceiptModel
                    {
                        Date = marked.Date,
                        SalesCount = summary.SalesCount,
                        TotalAmount = summary.TotalAmount,
                        CashAmount = summary.CashAmount,
                        CardAmount = summary.CardAmount,
                        GrossSalesAmount = summary.GrossSalesAmount,
                        RefundsAmount = summary.RefundsAmount,
                        NetAmount = summary.NetAmount
                    };
                    var lines = DailyTakingsReceiptFormatter.Format(model, shop ?? new ReceiptShopInfo());
                    textToPrint = string.Join(Environment.NewLine, lines);
                }
            }
            else if (SelectedHistoryRow != null)
            {
                textToPrint = SummaryReceiptPreview;
            }
            if (string.IsNullOrEmpty(textToPrint)) return;
            IsBusy = true;
            try
            {
                await _service.PrintReceiptTextAsync(textToPrint, true, "STAMPA_RIEPILOGO_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture)).ConfigureAwait(true);
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

        private string BuildMarkedAggregateReceiptText()
        {
            var lines = new List<string>();
            lines.Add("========================================");
            lines.Add("  RIEPILOGO SELEZIONE - " + MarkedCount + " giorni");
            lines.Add("========================================");
            lines.Add("");
            foreach (var row in HistoryRows)
            {
                if (!row.IsMarked) continue;
                lines.Add(row.DateText + "  Scontrini: " + row.SalesCount + "  Netto: " + row.NetDisplay);
            }
            lines.Add("----------------------------------------");
            lines.Add("Totale scontrini: " + MarkedSalesCount);
            lines.Add("Lorde:  " + MarkedGrossDisplay);
            lines.Add("Resi:   " + MarkedRefundsDisplay);
            lines.Add("Netto:  " + MarkedNetDisplay);
            lines.Add("Contanti: " + MarkedCashDisplay + "  Carta: " + MarkedCardDisplay);
            lines.Add("Ticket medio: " + MarkedTicketAverageDisplay);
            lines.Add("========================================");
            return string.Join(Environment.NewLine, lines);
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
                PeriodNetAmount = 0;
                PeriodGrossAmount = 0;
                PeriodRefundsAmount = 0;
                PeriodCashAmount = 0;
                PeriodCardAmount = 0;
                PeriodSalesCount = 0;
                long periodNet = 0, periodGross = 0, periodRefunds = 0, periodCash = 0, periodCard = 0;
                int periodCount = 0;
                foreach (var s in summaries)
                {
                    periodNet += s.NetAmount;
                    periodGross += s.GrossSalesAmount;
                    periodRefunds += s.RefundsAmount;
                    periodCash += s.CashAmount;
                    periodCard += s.CardAmount;
                    periodCount += s.SalesCount;
                    var row = new HistoryRow
                    {
                        Date = s.Date,
                        DateText = s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        SalesCount = s.SalesCount,
                        NetAmount = s.NetAmount,
                        GrossAmount = s.GrossSalesAmount,
                        RefundsAmount = s.RefundsAmount,
                        CashAmount = s.CashAmount,
                        CardAmount = s.CardAmount,
                        GrossDisplay = MoneyClp.FormatDisplay(s.GrossSalesAmount),
                        RefundsDisplay = MoneyClp.FormatDisplay(s.RefundsAmount),
                        NetDisplay = MoneyClp.FormatDisplay(s.NetAmount),
                        CashDisplay = MoneyClp.FormatDisplay(s.CashAmount),
                        CardDisplay = MoneyClp.FormatDisplay(s.CardAmount)
                    };
                    row.PropertyChanged += OnHistoryRowPropertyChanged;
                    HistoryRows.Add(row);
                }
                PeriodNetAmount = periodNet;
                PeriodGrossAmount = periodGross;
                PeriodRefundsAmount = periodRefunds;
                PeriodCashAmount = periodCash;
                PeriodCardAmount = periodCard;
                PeriodSalesCount = periodCount;
                HistoryRowCount = HistoryRows.Count;
                if (HistoryRows.Count > 0)
                    SelectedHistoryRow = HistoryRows[0];
                (ExportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

        private void OnHistoryRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HistoryRow.IsMarked))
                RefreshMarkedAggregates();
        }

        private void RefreshMarkedAggregates()
        {
            int count = 0, salesCount = 0;
            long net = 0, gross = 0, refunds = 0, cash = 0, card = 0;
            foreach (var row in HistoryRows)
            {
                if (!row.IsMarked) continue;
                count++;
                salesCount += row.SalesCount;
                net += row.NetAmount;
                gross += row.GrossAmount;
                refunds += row.RefundsAmount;
                cash += row.CashAmount;
                card += row.CardAmount;
            }
            MarkedCount = count;
            MarkedSalesCount = salesCount;
            MarkedNetAmount = net;
            MarkedGrossAmount = gross;
            MarkedRefundsAmount = refunds;
            MarkedCashAmount = cash;
            MarkedCardAmount = card;
            (PrintSelectedHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private string GetStampaRiepilogoSubtitle()
        {
            if (SelectedTabIndex == 0)
                return "Giorno caricato";
            if (MarkedCount == 0 && SelectedHistoryRow != null)
                return "1 giorno";
            if (MarkedCount == 1)
                return "1 giorno";
            if (MarkedCount >= 2)
                return MarkedCount + " giorni selezionati";
            if (HasHistoryRows && TryParseHistoryDates(out var from, out var to))
                return "Periodo " + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " → " + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return "";
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
            (ExportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintSummaryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintSelectedHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (LoadHistoryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

        public sealed class HistoryRow : INotifyPropertyChanged
        {
            private bool _isMarked;

            public DateTime Date { get; set; }
            public string DateText { get; set; }
            public int SalesCount { get; set; }
            public long NetAmount { get; set; }
            public long GrossAmount { get; set; }
            public long RefundsAmount { get; set; }
            public long CashAmount { get; set; }
            public long CardAmount { get; set; }
            public string GrossDisplay { get; set; }
            public string RefundsDisplay { get; set; }
            public string NetDisplay { get; set; }
            public string CashDisplay { get; set; }
            public string CardDisplay { get; set; }

            /// <summary>Se true, il giorno è incluso nel riepilogo/stampa/export selezione.</summary>
            public bool IsMarked
            {
                get => _isMarked;
                set { if (_isMarked == value) return; _isMarked = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Ambito export: giorno (Giornaliero o giorno selezionato), periodo, o giorni selezionati (checkbox).</summary>
    public enum ExportScope { Daily, Period, Day, Marked }

    /// <summary>Richiesta export: nome base senza estensione + ambito e date. La View mostra SaveFileDialog e chiede contenuto.</summary>
    public sealed class ExportRequest
    {
        public ExportScope Scope { get; set; }
        public string BaseFileName { get; set; }
        public DateTime? Date { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public System.Collections.Generic.IReadOnlyList<DateTime> Dates { get; set; }

        public static ExportRequest ForDaily(DateTime date)
        {
            return new ExportRequest
            {
                Scope = ExportScope.Daily,
                BaseFileName = "chiusura_" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Date = date
            };
        }

        public static ExportRequest ForPeriod(DateTime from, DateTime to)
        {
            return new ExportRequest
            {
                Scope = ExportScope.Period,
                BaseFileName = "chiusure_" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                From = from,
                To = to
            };
        }

        public static ExportRequest ForDay(DateTime date)
        {
            return new ExportRequest
            {
                Scope = ExportScope.Day,
                BaseFileName = "chiusura_" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Date = date
            };
        }

        public static ExportRequest ForMarked(System.Collections.Generic.IReadOnlyList<DateTime> dates)
        {
            if (dates == null || dates.Count == 0)
                throw new ArgumentException("Almeno un giorno richiesto.", nameof(dates));
            var min = dates[0];
            var max = dates[0];
            foreach (var d in dates)
            {
                if (d < min) min = d;
                if (d > max) max = d;
            }
            return new ExportRequest
            {
                Scope = ExportScope.Marked,
                BaseFileName = "chiusure_selezione_" + min.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + max.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                From = min,
                To = max,
                Dates = new System.Collections.Generic.List<DateTime>(dates)
            };
        }
    }
}
