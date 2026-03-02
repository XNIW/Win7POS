using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Globalization;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Util;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Pos
{
    public sealed class PosViewModel : INotifyPropertyChanged
    {
        private readonly PosWorkflowService _service = new PosWorkflowService();
        private readonly FileLogger _logger = new FileLogger();

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
            set { _total = value; OnPropertyChanged(); OnPropertyChanged(nameof(TotalDisplay)); }
        }

        public int ItemsCount => CartItems.Sum(x => x.Quantity);

        public string TotalDisplay => MoneyClp.Format(Total);

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
        public ICommand DailyReportCommand { get; }
        public ICommand DbMaintenanceCommand { get; }
        public ICommand AboutSupportCommand { get; }
        public ICommand RefundCommand { get; }
        public ICommand PrintSelectedReceiptCommand { get; }
        public ICommand ClearCartCommand { get; }
        public ICommand IncreaseQtyForLineCommand { get; }
        public ICommand DecreaseQtyForLineCommand { get; }
        public ICommand RemoveLineForLineCommand { get; }

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
            DailyReportCommand = new AsyncRelayCommand(OpenDailyReportAsync, _ => !IsBusy);
            DbMaintenanceCommand = new AsyncRelayCommand(OpenDbMaintenanceAsync, _ => !IsBusy);
            AboutSupportCommand = new AsyncRelayCommand(OpenAboutSupportAsync, _ => !IsBusy);
            RefundCommand = new AsyncRelayCommand(OpenRefundAsync, _ => !IsBusy && SelectedRecentSale != null && SelectedRecentSale.Kind == (int)SaleKind.Sale);
            PrintSelectedReceiptCommand = new AsyncRelayCommand(PrintSelectedReceiptAsync, _ => !IsBusy && SelectedRecentSale != null);
            ClearCartCommand = new AsyncRelayCommand(ClearCartAsync, _ => !IsBusy && CartItems.Count > 0);
            IncreaseQtyForLineCommand = new AsyncRelayCommandParam(IncreaseQtyForLineAsync, _ => !IsBusy);
            DecreaseQtyForLineCommand = new AsyncRelayCommandParam(DecreaseQtyForLineAsync, _ => !IsBusy);
            RemoveLineForLineCommand = new AsyncRelayCommandParam(RemoveLineForLineAsync, _ => !IsBusy);
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

            var draft = new PaymentReceiptDraft
            {
                SaleCode = SaleCodeGenerator.NewCode("V"),
                CreatedAtMs = UnixTime.NowMs(),
                CartLines = CartItems.Select(x => new PaymentReceiptDraftLine
                {
                    Barcode = x.Barcode,
                    Name = x.Name,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    LineTotal = x.LineTotal
                }).ToList(),
                UseReceipt42 = UseReceipt42,
                DefaultPrint = _printerSettings.AutoPrint
            };

            PaymentDialog dlg;
            try
            {
                dlg = new PaymentDialog(Total, draft) { Owner = Application.Current?.MainWindow };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment dialog init failed (XAML/binding).");
                MessageBox.Show(
                    "Errore apertura finestra pagamento.\n" +
                    "Controlla: C:\\ProgramData\\Win7POS\\logs\\app.log\n\n" +
                    ex.Message,
                    "Pay error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StatusMessage = "Errore apertura finestra pagamento (binding).";
                RequestFocusBarcode();
                return;
            }

            bool ok;
            try
            {
                ok = dlg.ShowDialog() == true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment dialog ShowDialog failed.");
                MessageBox.Show("Errore Pay.\n\n" + ex.Message, "Pay error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusMessage = "Errore Pay (dialog).";
                RequestFocusBarcode();
                return;
            }

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
                var result = await _service.CompleteSaleAsync(
                    payment,
                    dlg.ViewModel.SaleCode,
                    dlg.ViewModel.CreatedAtMs).ConfigureAwait(true);
                ApplySnapshot(result.Snapshot);
                ReceiptPreview = UseReceipt42 ? result.Receipt42 : result.Receipt32;
                StatusMessage = "Pagamento OK: " + result.SaleCode;
                if (dlg.ViewModel.ShouldPrint)
                {
                    var printed = await PrintReceiptAsync(ReceiptPreview, result.SaleCode).ConfigureAwait(true);
                    if (!printed)
                    {
                        MessageBox.Show(
                            "Ricevuta non stampata.\nControlla impostazioni stampante e log.",
                            "Stampa fallita",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
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
                        Total = x.TotalMinor,
                        Kind = x.Kind,
                        KindText = x.Kind == (int)SaleKind.Refund ? "Refund" : "Sale",
                        RelatedSaleId = x.RelatedSaleId,
                        VoidedBySaleId = x.VoidedBySaleId,
                        StatusText = x.VoidedBySaleId.HasValue ? "VOIDED" : string.Empty
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

        private async Task PrintSelectedReceiptAsync()
        {
            if (SelectedRecentSale == null)
            {
                StatusMessage = "Seleziona una vendita.";
                return;
            }

            IsBusy = true;
            try
            {
                var preview = await _service.GetReceiptPreviewBySaleIdAsync(SelectedRecentSale.SaleId, UseReceipt42).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(preview))
                {
                    StatusMessage = "Ricevuta non disponibile.";
                    return;
                }

                ReceiptPreview = preview;
                await PrintReceiptAsync(preview, "SALE_" + SelectedRecentSale.SaleCode).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore stampa selezionato: " + ex.Message;
                _logger.LogError(ex, "POS VM print selected receipt failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
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

        private async Task ClearCartAsync()
        {
            IsBusy = true;
            try
            {
                var snapshot = await _service.ClearCartAsync().ConfigureAwait(true);
                ApplySnapshot(snapshot);
                StatusMessage = "Carrello svuotato.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore svuota carrello: " + ex.Message;
                _logger.LogError(ex, "POS VM clear cart failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task IncreaseQtyForLineAsync(object parameter)
        {
            var row = parameter as PosCartLineRow;
            if (row == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.IncreaseQtyAsync(row.Barcode).ConfigureAwait(true);
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

        private async Task DecreaseQtyForLineAsync(object parameter)
        {
            var row = parameter as PosCartLineRow;
            if (row == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.DecreaseQtyAsync(row.Barcode).ConfigureAwait(true);
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

        private async Task RemoveLineForLineAsync(object parameter)
        {
            var row = parameter as PosCartLineRow;
            if (row == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.RemoveLineAsync(row.Barcode).ConfigureAwait(true);
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

        private async Task<bool> PrintReceiptAsync(string receiptText, string saleCode)
        {
            try
            {
                var result = await _service.PrintReceiptTextAsync(receiptText, UseReceipt42, saleCode).ConfigureAwait(true);
                StatusMessage = result.SavedCopy
                    ? "Ricevuta stampata. Copia salvata: " + result.OutputPath
                    : "Ricevuta stampata.";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = "Stampa fallita: " + ex.Message;
                _logger.LogError(ex, "POS VM print failed");
                return false;
            }
        }

        private Task OpenDailyReportAsync()
        {
            var vm = new DailyReportViewModel(_service);
            var dlg = new DailyReportDialog(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            dlg.ShowDialog();
            RequestFocusBarcode();
            return Task.CompletedTask;
        }

        private Task OpenDbMaintenanceAsync()
        {
            var vm = new DbMaintenanceViewModel(_service);
            var dlg = new DbMaintenanceDialog(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            dlg.ShowDialog();
            RequestFocusBarcode();
            return Task.CompletedTask;
        }

        private Task OpenAboutSupportAsync()
        {
            var vm = new AboutSupportViewModel(_service);
            var dlg = new AboutSupportDialog(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            dlg.ShowDialog();
            RequestFocusBarcode();
            return Task.CompletedTask;
        }

        private async Task OpenRefundAsync()
        {
            if (SelectedRecentSale == null)
            {
                StatusMessage = "Seleziona una vendita.";
                return;
            }

            IsBusy = true;
            try
            {
                var preview = await _service.BuildRefundPreviewAsync(SelectedRecentSale.SaleId).ConfigureAwait(true);
                IsBusy = false;

                var vm = new RefundViewModel(preview);
                var dlg = new RefundDialog(vm)
                {
                    Owner = Application.Current?.MainWindow
                };
                var ok = dlg.ShowDialog() == true;
                if (!ok)
                {
                    StatusMessage = "Reso annullato.";
                    return;
                }

                IsBusy = true;
                var req = vm.BuildRequest();
                var result = await _service.CreateRefundAsync(req, UseReceipt42, _printerSettings.AutoPrint).ConfigureAwait(true);
                ReceiptPreview = UseReceipt42 ? result.Receipt42 : result.Receipt32;
                StatusMessage = "Reso completato: " + result.RefundSaleCode;
                await LoadRecentSalesAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore reso/storno: " + ex.Message;
                _logger.LogError(ex, "POS VM refund failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
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
            OnPropertyChanged(nameof(TotalDisplay));
            if (!string.IsNullOrWhiteSpace(snapshot.Status))
                StatusMessage = snapshot.Status;
            OnPropertyChanged(nameof(ItemsCount));
            (ClearCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public string BuildCartReceiptPreview()
        {
            var lines = new System.Collections.Generic.List<string>();
            lines.Add("Win7POS");
            lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            lines.Add("----------------------------------------");
            foreach (var item in CartItems)
            {
                lines.Add(item.Name);
                lines.Add($"  {item.Quantity} x {MoneyClp.Format(item.UnitPrice)} = {MoneyClp.Format(item.LineTotal)}");
            }
            lines.Add("----------------------------------------");
            var totalPaid = 0;
            lines.Add("Totale: " + MoneyClp.Format(Total));
            lines.Add("Pagato: " + MoneyClp.Format(totalPaid));
            lines.Add("Resto: " + MoneyClp.Format(0));
            return string.Join(Environment.NewLine, lines);
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
            (DailyReportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DbMaintenanceCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (AboutSupportCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RefundCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PrintSelectedReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ClearCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (IncreaseQtyForLineCommand as AsyncRelayCommandParam)?.RaiseCanExecuteChanged();
            (DecreaseQtyForLineCommand as AsyncRelayCommandParam)?.RaiseCanExecuteChanged();
            (RemoveLineForLineCommand as AsyncRelayCommandParam)?.RaiseCanExecuteChanged();
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
            public string UnitPriceDisplay => MoneyClp.Format(UnitPrice);
            public string LineTotalDisplay => MoneyClp.Format(LineTotal);
        }

        public sealed class RecentSaleRow
        {
            public long SaleId { get; set; }
            public string SaleCode { get; set; } = string.Empty;
            public string TimeText { get; set; } = string.Empty;
            public int Total { get; set; }
            public string TotalDisplay => MoneyClp.Format(Total);
            public int Kind { get; set; }
            public string KindText { get; set; } = string.Empty;
            public long? RelatedSaleId { get; set; }
            public long? VoidedBySaleId { get; set; }
            public string StatusText { get; set; } = string.Empty;
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

        private sealed class AsyncRelayCommandParam : ICommand
        {
            private readonly Func<object, Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommandParam(Func<object, Task> executeAsync, Func<object, bool> canExecute = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

            public async void Execute(object parameter)
            {
                await _executeAsync(parameter).ConfigureAwait(true);
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
