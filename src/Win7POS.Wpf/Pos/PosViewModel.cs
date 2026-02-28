using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Printing;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Pos
{
    public sealed class PosViewModel : INotifyPropertyChanged
    {
        private readonly PosWorkflowService _service = new PosWorkflowService();
        private readonly FileLogger _logger = new FileLogger();
        private readonly IReceiptPrinter _printer = new WindowsSpoolerReceiptPrinter();

        private string _barcodeInput = string.Empty;
        private int _subtotal;
        private int _total;
        private bool _isBusy;
        private string _statusMessage = string.Empty;
        private string _receiptPreview = string.Empty;
        private bool _useReceipt42 = true;
        private bool _isLoadingSettings;
        private PosPrinterSettings _printerSettings = new PosPrinterSettings();

        private PosCartLineRow _selectedCartItem;
        private RecentSaleRow _selectedRecentSale;

        public ObservableCollection<PosCartLineRow> CartItems { get; } = new ObservableCollection<PosCartLineRow>();
        public ObservableCollection<RecentSaleRow> RecentSales { get; } = new ObservableCollection<RecentSaleRow>();
        public event Action FocusBarcodeRequested;

        public string BarcodeInput
        {
            get => _barcodeInput;
            set { _barcodeInput = value; OnPropertyChanged(); }
        }

        public int Subtotal
        {
            get => _subtotal;
            set { _subtotal = value; OnPropertyChanged(); }
        }

        public int Total
        {
            get => _total;
            set { _total = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string ReceiptPreview
        {
            get => _receiptPreview;
            set { _receiptPreview = value; OnPropertyChanged(); }
        }

        public bool UseReceipt42
        {
            get => _useReceipt42;
            set
            {
                if (_useReceipt42 == value) return;
                _useReceipt42 = value;
                OnPropertyChanged();
                if (!_isLoadingSettings)
                    _ = SaveUseReceipt42Async(value);
            }
        }

        public PosCartLineRow SelectedCartItem
        {
            get => _selectedCartItem;
            set { _selectedCartItem = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public RecentSaleRow SelectedRecentSale
        {
            get => _selectedRecentSale;
            set { _selectedRecentSale = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public ICommand AddBarcodeCommand { get; }
        public ICommand PayCommand { get; }
        public ICommand ReceiptPreviewCommand { get; }
        public ICommand LoadRecentSalesCommand { get; }
        public ICommand ReprintPreviewCommand { get; }
        public ICommand IncreaseQtyCommand { get; }
        public ICommand DecreaseQtyCommand { get; }
        public ICommand RemoveLineCommand { get; }
        public ICommand BackupDbCommand { get; }
        public ICommand PrinterSettingsCommand { get; }
        public ICommand PrintLastReceiptCommand { get; }

        public PosViewModel()
        {
            AddBarcodeCommand = new AsyncRelayCommand(AddBarcodeAsync, _ => !IsBusy);
            PayCommand = new AsyncRelayCommand(PayAsync, _ => !IsBusy);
            ReceiptPreviewCommand = new AsyncRelayCommand(ShowReceiptPreviewAsync, _ => !IsBusy);
            LoadRecentSalesCommand = new AsyncRelayCommand(LoadRecentSalesAsync, _ => !IsBusy);
            ReprintPreviewCommand = new AsyncRelayCommand(ReprintPreviewAsync, _ => !IsBusy && SelectedRecentSale != null);
            IncreaseQtyCommand = new AsyncRelayCommand(IncreaseQtyAsync, _ => !IsBusy && SelectedCartItem != null);
            DecreaseQtyCommand = new AsyncRelayCommand(DecreaseQtyAsync, _ => !IsBusy && SelectedCartItem != null);
            RemoveLineCommand = new AsyncRelayCommand(RemoveLineAsync, _ => !IsBusy && SelectedCartItem != null);
            BackupDbCommand = new AsyncRelayCommand(BackupDbAsync, _ => !IsBusy);
            PrinterSettingsCommand = new AsyncRelayCommand(OpenPrinterSettingsAsync, _ => !IsBusy);
            PrintLastReceiptCommand = new AsyncRelayCommand(PrintLastReceiptAsync, _ => !IsBusy);
            StatusMessage = "POS pronto.";
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            IsBusy = true;
            try
            {
                await _service.InitializeAsync().ConfigureAwait(true);
                _isLoadingSettings = true;
                try
                {
                    var savedUse42 = await _service.GetUseReceipt42Async().ConfigureAwait(true);
                    if (savedUse42.HasValue)
                        _useReceipt42 = savedUse42.Value;
                    OnPropertyChanged(nameof(UseReceipt42));
                }
                finally
                {
                    _isLoadingSettings = false;
                }
                _printerSettings = await _service.GetPrinterSettingsAsync().ConfigureAwait(true);
                var snapshot = await _service.GetSnapshotAsync().ConfigureAwait(true);
                ApplySnapshot(snapshot);
                await LoadRecentSalesAsync().ConfigureAwait(true);
                StatusMessage = "POS inizializzato.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore init POS: " + ex.Message;
                _logger.LogError(ex, "POS VM init failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SaveUseReceipt42Async(bool value)
        {
            try
            {
                await _service.SetUseReceipt42Async(value).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS VM save UseReceipt42 failed");
            }
        }

        private async Task AddBarcodeAsync()
        {
            var code = (BarcodeInput ?? string.Empty).Trim();
            if (code.Length == 0)
                return;

            IsBusy = true;
            try
            {
                var snapshot = await _service.AddByBarcodeAsync(code).ConfigureAwait(true);
                ApplySnapshot(snapshot);
                StatusMessage = "Prodotto aggiunto: " + code;
            }
            catch (PosException ex) when (ex.Code == PosErrorCode.ProductNotFound)
            {
                StatusMessage = "Prodotto non trovato: " + code;
                var askCreate = MessageBox.Show(
                    "Prodotto non trovato: " + code + "\nVuoi creare prodotto?",
                    "Prodotto non trovato",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (askCreate == MessageBoxResult.Yes)
                {
                    var dlg = new AddProductDialog(code)
                    {
                        Owner = Application.Current?.MainWindow
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        try
                        {
                            await _service.CreateProductAsync(code, dlg.ViewModel.ProductName, dlg.ViewModel.PriceMinor).ConfigureAwait(true);
                            var snapshot = await _service.AddByBarcodeAsync(code).ConfigureAwait(true);
                            ApplySnapshot(snapshot);
                            StatusMessage = "Prodotto creato e aggiunto: " + code;
                        }
                        catch (Exception createEx)
                        {
                            StatusMessage = "Errore creazione prodotto: " + createEx.Message;
                            _logger.LogError(createEx, "POS VM create product failed");
                        }
                    }
                }
            }
            catch (PosException ex)
            {
                StatusMessage = ex.Message;
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore AddByBarcode: " + ex.Message;
                _logger.LogError(ex, "POS VM add barcode failed");
            }
            finally
            {
                BarcodeInput = string.Empty;
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task PayAsync()
        {
            if (CartItems.Count == 0)
            {
                StatusMessage = "Carrello vuoto";
                return;
            }

            var dlg = new PaymentDialog(Total)
            {
                Owner = Application.Current?.MainWindow
            };
            var ok = dlg.ShowDialog() == true;
            if (!ok)
            {
                StatusMessage = "Pagamento annullato.";
                RequestFocusBarcode();
                return;
            }

            IsBusy = true;
            try
            {
                var payment = new PosPaymentInfo
                {
                    CashAmountMinor = dlg.ViewModel.CashAmountMinor,
                    CardAmountMinor = dlg.ViewModel.CardAmountMinor
                };
                var result = await _service.CompleteSaleAsync(payment).ConfigureAwait(true);
                ApplySnapshot(result.Snapshot);
                ReceiptPreview = UseReceipt42 ? result.Receipt42 : result.Receipt32;
                StatusMessage = "Pagamento OK: " + result.SaleCode;
                if (_printerSettings.AutoPrint)
                {
                    await PrintReceiptAsync(ReceiptPreview, result.SaleCode).ConfigureAwait(true);
                }
                await LoadRecentSalesAsync().ConfigureAwait(true);
            }
            catch (PosException ex)
            {
                StatusMessage = ex.Message;
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore Pay: " + ex.Message;
                _logger.LogError(ex, "POS VM pay failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task ShowReceiptPreviewAsync()
        {
            IsBusy = true;
            try
            {
                var preview = await _service.GetLastReceiptPreviewAsync(UseReceipt42).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(preview))
                {
                    StatusMessage = "Nessuna ricevuta disponibile. Esegui prima Pay.";
                    return;
                }

                ReceiptPreview = preview;
                StatusMessage = "Receipt preview aggiornata.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore receipt preview: " + ex.Message;
                _logger.LogError(ex, "POS VM preview failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task LoadRecentSalesAsync()
        {
            IsBusy = true;
            try
            {
                var items = await _service.GetRecentSalesAsync(20).ConfigureAwait(true);
                RecentSales.Clear();
                foreach (var x in items)
                {
                    var when = DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedAtMs).LocalDateTime;
                    RecentSales.Add(new RecentSaleRow
                    {
                        SaleId = x.SaleId,
                        SaleCode = x.SaleCode,
                        TimeText = when.ToString("yyyy-MM-dd HH:mm:ss"),
                        Total = x.TotalMinor
                    });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore caricamento vendite: " + ex.Message;
                _logger.LogError(ex, "POS VM load recent sales failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ReprintPreviewAsync()
        {
            if (SelectedRecentSale == null) return;

            IsBusy = true;
            try
            {
                var preview = await _service.GetReceiptPreviewBySaleIdAsync(SelectedRecentSale.SaleId, UseReceipt42).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(preview))
                {
                    StatusMessage = "Vendita non trovata.";
                    return;
                }

                ReceiptPreview = preview;
                StatusMessage = "Preview caricata: " + SelectedRecentSale.SaleCode;
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore ristampa preview: " + ex.Message;
                _logger.LogError(ex, "POS VM reprint preview failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task IncreaseQtyAsync()
        {
            if (SelectedCartItem == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.IncreaseQtyAsync(SelectedCartItem.Barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore Qty+: " + ex.Message;
                _logger.LogError(ex, "POS VM increase qty failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DecreaseQtyAsync()
        {
            if (SelectedCartItem == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.DecreaseQtyAsync(SelectedCartItem.Barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore Qty-: " + ex.Message;
                _logger.LogError(ex, "POS VM decrease qty failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RemoveLineAsync()
        {
            if (SelectedCartItem == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.RemoveLineAsync(SelectedCartItem.Barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore remove line: " + ex.Message;
                _logger.LogError(ex, "POS VM remove line failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task BackupDbAsync()
        {
            IsBusy = true;
            try
            {
                var outputPath = await _service.BackupDbAsync().ConfigureAwait(true);
                StatusMessage = "Backup DB creato: " + outputPath;
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore backup DB: " + ex.Message;
                _logger.LogError(ex, "POS VM backup db failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task OpenPrinterSettingsAsync()
        {
            var vm = new PrinterSettingsViewModel
            {
                PrinterName = _printerSettings.PrinterName,
                Copies = _printerSettings.Copies.ToString(),
                AutoPrint = _printerSettings.AutoPrint,
                SaveCopyToFile = _printerSettings.SaveCopyToFile,
                OutputDirectory = _printerSettings.OutputDirectory
            };

            var dlg = new PrinterSettingsDialog(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            var ok = dlg.ShowDialog() == true;
            if (!ok)
            {
                StatusMessage = "Impostazioni stampante annullate.";
                RequestFocusBarcode();
                return;
            }

            _printerSettings = new PosPrinterSettings
            {
                PrinterName = vm.PrinterName,
                Copies = vm.ParsedCopies < 1 ? 1 : vm.ParsedCopies,
                AutoPrint = vm.AutoPrint,
                SaveCopyToFile = vm.SaveCopyToFile,
                OutputDirectory = string.IsNullOrWhiteSpace(vm.OutputDirectory)
                    ? Path.Combine(Win7POS.Core.AppPaths.DataDirectory, "receipts")
                    : vm.OutputDirectory
            };

            try
            {
                await _service.SetPrinterSettingsAsync(_printerSettings).ConfigureAwait(true);
                StatusMessage = "Impostazioni stampante salvate.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore salvataggio impostazioni stampante: " + ex.Message;
                _logger.LogError(ex, "POS VM save printer settings failed");
            }
            finally
            {
                RequestFocusBarcode();
            }
        }

        private async Task PrintLastReceiptAsync()
        {
            IsBusy = true;
            try
            {
                var text = await _service.GetLastReceiptPreviewAsync(UseReceipt42).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(text))
                {
                    StatusMessage = "Nessuna ricevuta da stampare.";
                    return;
                }

                await PrintReceiptAsync(text, "LAST").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore stampa ricevuta: " + ex.Message;
                _logger.LogError(ex, "POS VM print last receipt failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task PrintReceiptAsync(string receiptText, string saleCode)
        {
            var outputDirectory = string.IsNullOrWhiteSpace(_printerSettings.OutputDirectory)
                ? Path.Combine(Win7POS.Core.AppPaths.DataDirectory, "receipts")
                : _printerSettings.OutputDirectory;
            var outputPath = Path.Combine(outputDirectory, "SALE_" + saleCode + ".txt");

            var printOptions = new ReceiptPrintOptions
            {
                PrinterName = _printerSettings.PrinterName,
                Copies = _printerSettings.Copies < 1 ? 1 : _printerSettings.Copies,
                CharactersPerLine = UseReceipt42 ? 42 : 32,
                SaveCopyToFile = _printerSettings.SaveCopyToFile,
                OutputPath = outputPath
            };

            try
            {
                await _printer.PrintAsync(receiptText, printOptions).ConfigureAwait(true);
                StatusMessage = _printerSettings.SaveCopyToFile
                    ? "Ricevuta stampata. Copia salvata: " + outputPath
                    : "Ricevuta stampata.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Stampa fallita: " + ex.Message;
                _logger.LogError(ex, "POS VM print failed");
            }
        }

        private void ApplySnapshot(PosWorkflowSnapshot snapshot)
        {
            CartItems.Clear();
            foreach (var item in snapshot.Lines)
            {
                CartItems.Add(new PosCartLineRow
                {
                    Barcode = item.Barcode,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    LineTotal = item.LineTotal
                });
            }

            Subtotal = snapshot.Subtotal;
            Total = snapshot.Total;
            if (!string.IsNullOrWhiteSpace(snapshot.Status))
                StatusMessage = snapshot.Status;
        }

        private void RaiseCanExecuteChanged()
        {
            (AddBarcodeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReceiptPreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (LoadRecentSalesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReprintPreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (IncreaseQtyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DecreaseQtyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RemoveLineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (BackupDbCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrinterSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintLastReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RequestFocusBarcode()
        {
            FocusBarcodeRequested?.Invoke();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class PosCartLineRow
        {
            public string Barcode { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public int UnitPrice { get; set; }
            public int LineTotal { get; set; }
        }

        public sealed class RecentSaleRow
        {
            public long SaleId { get; set; }
            public string SaleCode { get; set; } = string.Empty;
            public string TimeText { get; set; } = string.Empty;
            public int Total { get; set; }
        }

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
