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
using Win7POS.Wpf.Fiscal;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.Pos
{
    public sealed class PosViewModel : INotifyPropertyChanged
    {
        private readonly PosWorkflowService _service;
        private readonly FileLogger _logger;

        private string _barcodeInput = string.Empty;
        private long _subtotal;
        private long _total;
        private bool _isBusy;
        private string _statusMessage = string.Empty;
        private string _receiptPreview = string.Empty;
        private bool _useReceipt42 = true;
        private bool _isLoadingSettings;
        private PosPrinterSettings _printerSettings = new PosPrinterSettings();

        private PosCartLineRow _selectedCartItem;
        private RecentSaleRow _selectedRecentSale;
        private int? _pendingInputQuantity;

        public ObservableCollection<PosCartLineRow> CartItems { get; } = new ObservableCollection<PosCartLineRow>();
        public ObservableCollection<RecentSaleRow> RecentSales { get; } = new ObservableCollection<RecentSaleRow>();
        public event Action FocusBarcodeRequested;

        public string BarcodeInput
        {
            get => _barcodeInput;
            set { _barcodeInput = value; OnPropertyChanged(); }
        }

        public long Subtotal
        {
            get => _subtotal;
            set
            {
                _subtotal = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiscountAmount));
                OnPropertyChanged(nameof(HasDiscount));
                OnPropertyChanged(nameof(OriginalTotalDisplay));
                OnPropertyChanged(nameof(DiscountAmountDisplay));
            }
        }

        public long Total
        {
            get => _total;
            set
            {
                _total = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalDisplay));
                OnPropertyChanged(nameof(FinalTotalDisplay));
                OnPropertyChanged(nameof(DiscountAmount));
                OnPropertyChanged(nameof(HasDiscount));
                OnPropertyChanged(nameof(DiscountAmountDisplay));
            }
        }

        public int ItemsCount => CartItems.Sum(x => x.Quantity);

        public string TotalDisplay => MoneyClp.Format(Total);

        /// <summary>Importo totale sconti (Subtotal - Total).</summary>
        public long DiscountAmount => Math.Max(0, Subtotal - Total);

        public bool HasDiscount => DiscountAmount > 0;

        /// <summary>Totale prima degli sconti (per UI footer).</summary>
        public string OriginalTotalDisplay => MoneyClp.Format(Subtotal);

        public string DiscountAmountDisplay => MoneyClp.Format(DiscountAmount);

        /// <summary>Totale finale da pagare (per UI footer).</summary>
        public string FinalTotalDisplay => MoneyClp.Format(Total);

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

        public int? PendingInputQuantity
        {
            get => _pendingInputQuantity;
            set { _pendingInputQuantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(PendingInputQuantityDisplay)); }
        }

        public string PendingInputQuantityDisplay =>
            PendingInputQuantity.HasValue ? "Q.tà pronta: " + PendingInputQuantity.Value : string.Empty;

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
        public ICommand OpenSalesRegisterCommand { get; }
        public ICommand OpenResoScanCommand { get; }
        public ICommand OpenShopSettingsCommand { get; }
        public ICommand OpenDiscountCommand { get; }
        public ICommand OpenEditProductCommand { get; }
        public ICommand OpenChangeQuantityCommand { get; }
        public ICommand OpenChangeQuantityForLineCommand { get; }
        public ICommand SuspendCartCommand { get; }
        public ICommand RecoverCartCommand { get; }

        /// <summary>Costruttore con dipendenze iniettate. Se null, usa istanze di default (compatibilità designer XAML).</summary>
        public PosViewModel(PosWorkflowService service = null, FileLogger logger = null)
        {
            _service = service ?? new PosWorkflowService();
            _logger = logger ?? new FileLogger();

            AddBarcodeCommand = new AsyncRelayCommand(AddBarcodeAsync, _ => !IsBusy, _logger);
            PayCommand = new AsyncRelayCommand(PayAsync, _ => !IsBusy, _logger);
            ReceiptPreviewCommand = new AsyncRelayCommand(ShowReceiptPreviewAsync, _ => !IsBusy, _logger);
            LoadRecentSalesCommand = new AsyncRelayCommand(LoadRecentSalesAsync, _ => !IsBusy, _logger);
            ReprintPreviewCommand = new AsyncRelayCommand(ReprintPreviewAsync, _ => !IsBusy && SelectedRecentSale != null, _logger);
            IncreaseQtyCommand = new AsyncRelayCommand(IncreaseQtyAsync, _ => !IsBusy && SelectedCartItem != null, _logger);
            DecreaseQtyCommand = new AsyncRelayCommand(DecreaseQtyAsync, _ => !IsBusy && SelectedCartItem != null, _logger);
            RemoveLineCommand = new AsyncRelayCommand(RemoveLineAsync, _ => !IsBusy && SelectedCartItem != null, _logger);
            BackupDbCommand = new AsyncRelayCommand(BackupDbAsync, _ => !IsBusy, _logger);
            PrinterSettingsCommand = new AsyncRelayCommand(OpenPrinterSettingsAsync, _ => !IsBusy, _logger);
            PrintLastReceiptCommand = new AsyncRelayCommand(PrintLastReceiptAsync, _ => !IsBusy, _logger);
            DailyReportCommand = new AsyncRelayCommand(OpenDailyReportAsync, _ => !IsBusy, _logger);
            DbMaintenanceCommand = new AsyncRelayCommand(OpenDbMaintenanceAsync, _ => !IsBusy, _logger);
            AboutSupportCommand = new AsyncRelayCommand(OpenAboutSupportAsync, _ => !IsBusy, _logger);
            RefundCommand = new AsyncRelayCommand(OpenRefundAsync, _ => !IsBusy && SelectedRecentSale != null && SelectedRecentSale.Kind == (int)SaleKind.Sale, _logger);
            PrintSelectedReceiptCommand = new AsyncRelayCommand(PrintSelectedReceiptAsync, _ => !IsBusy && SelectedRecentSale != null, _logger);
            ClearCartCommand = new AsyncRelayCommand(ClearCartAsync, _ => !IsBusy && CartItems.Count > 0, _logger);
            IncreaseQtyForLineCommand = new AsyncRelayCommandParam(IncreaseQtyForLineAsync, _ => !IsBusy, _logger);
            DecreaseQtyForLineCommand = new AsyncRelayCommandParam(DecreaseQtyForLineAsync, _ => !IsBusy, _logger);
            RemoveLineForLineCommand = new AsyncRelayCommandParam(RemoveLineForLineAsync, _ => !IsBusy, _logger);
            OpenSalesRegisterCommand = new AsyncRelayCommand(OpenSalesRegisterAsync, _ => !IsBusy, _logger);
            OpenResoScanCommand = new AsyncRelayCommand(OpenResoScanAsync, _ => !IsBusy, _logger);
            OpenShopSettingsCommand = new AsyncRelayCommand(OpenShopSettingsAsync, _ => !IsBusy, _logger);
            OpenDiscountCommand = new RelayCommand(_ => OpenDiscount(), _ => !IsBusy && (SelectedCartItem != null || CartItems.Count > 0));
            OpenEditProductCommand = new RelayCommand(OpenEditProductExecute, OpenEditProductCanExecute);
            OpenChangeQuantityCommand = new RelayCommand(_ => OpenChangeQuantity(), _ => !IsBusy && SelectedCartItem != null && !SelectedCartItem.IsDiscountLine);
            OpenChangeQuantityForLineCommand = new RelayCommand(p => OpenChangeQuantityForLine(p as PosCartLineRow), p => !IsBusy && p is PosCartLineRow row && !row.IsDiscountLine);
            SuspendCartCommand = new AsyncRelayCommand(SuspendCartAsync, _ => !IsBusy && CartItems.Count > 0, _logger);
            RecoverCartCommand = new AsyncRelayCommand(RecoverCartAsync, _ => !IsBusy, _logger);
            CatalogEvents.CatalogChanged += OnCatalogChanged;
            StatusMessage = "POS pronto.";
        }

        private void OnCatalogChanged(string barcode)
        {
            if (string.IsNullOrEmpty(barcode))
            {
                _ = RefreshCartFromDatabaseAsync("Carrello sincronizzato col database.");
                return;
            }
            _ = SyncCartLineFromCatalogAsync(barcode);
        }

        private async Task SyncCartLineFromCatalogAsync(string barcode)
        {
            try
            {
                var snapshot = await _service.SyncCartLineFromCatalogAsync(barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot);
                StatusMessage = snapshot.Status ?? "Carrello sincronizzato.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore sync riga carrello: " + ex.Message;
                _logger.LogError(ex, "POS VM sync cart line failed");
            }
        }

        private async Task RefreshCartFromDatabaseAsync(string reason)
        {
            try
            {
                var snapshot = await _service.GetSnapshotAsync().ConfigureAwait(true);
                ApplySnapshot(snapshot);
                StatusMessage = reason;
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore refresh carrello: " + ex.Message;
                _logger.LogError(ex, "POS VM refresh cart failed");
            }
        }

        /// <summary>Avvia l'inizializzazione (chiamato da PosView.Loaded per evitare schermata bianca al primo render).</summary>
        public void StartInitialize()
        {
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
            var input = (BarcodeInput ?? string.Empty).Trim();
            if (input.Length == 0)
                return;

            if (await TryHandleQuantityInputAsync(input).ConfigureAwait(true))
            {
                PendingInputQuantity = null;
                BarcodeInput = string.Empty;
                RequestFocusBarcode();
                return;
            }

            var inputQty = PendingInputQuantity.GetValueOrDefault(1);

            if (TryParseQuantityPrefix(input, out var qty, out var barcodePart))
            {
                IsBusy = true;
                try
                {
                    await _service.AddByBarcodeAsync(barcodePart).ConfigureAwait(true);
                    var snapshot = await _service.SetQtyAsync(barcodePart, qty).ConfigureAwait(true);
                    ApplySnapshot(snapshot, barcodePart);
                    StatusMessage = "Aggiunto: " + barcodePart + " x " + qty;
                    PendingInputQuantity = null;
                }
                catch (Exception ex)
                {
                    StatusMessage = "Errore: " + ex.Message;
                    _logger.LogError(ex, "POS VM add with qty failed");
                }
                finally
                {
                    PendingInputQuantity = null;
                    BarcodeInput = string.Empty;
                    IsBusy = false;
                    RequestFocusBarcode();
                }
                return;
            }

            if (TryParseManualPriceClp(input, out var priceMinor))
            {
                IsBusy = true;
                try
                {
                    var manualKey = DiscountKeys.ManualPrefix + priceMinor;
                    var snapshot = await _service.AddManualPriceAsync(priceMinor).ConfigureAwait(true);
                    if (PendingInputQuantity.HasValue && inputQty > 1)
                        snapshot = await _service.SetQtyAsync(manualKey, inputQty).ConfigureAwait(true);
                    ApplySnapshot(snapshot, manualKey);
                    StatusMessage = "Aggiunto (senza codice): " + MoneyClp.Format(priceMinor);
                    return;
                }
                catch (PosException ex) when (ex.Code == PosErrorCode.InvalidPrice)
                {
                    StatusMessage = ex.Message;
                    return;
                }
                finally
                {
                    PendingInputQuantity = null;
                    BarcodeInput = string.Empty;
                    IsBusy = false;
                    RequestFocusBarcode();
                }
            }

            IsBusy = true;
            try
            {
                var snapshot = await _service.AddByBarcodeAsync(input).ConfigureAwait(true);
                if (PendingInputQuantity.HasValue && inputQty > 1)
                    snapshot = await _service.SetQtyAsync(input, inputQty).ConfigureAwait(true);
                ApplySnapshot(snapshot, input);
                StatusMessage = "Prodotto aggiunto: " + input;
            }
            catch (PosException ex) when (ex.Code == PosErrorCode.ProductNotFound)
            {
                StatusMessage = "Prodotto non trovato: " + input + " (creazione rapida)";

                var productsService = new ProductsWorkflowService();
                var draft = new ProductDetailsRow { Barcode = input };
                var ok = await ProductEditDialog.ShowAsync(ProductEditMode.New, draft, productsService).ConfigureAwait(true);

                if (ok)
                {
                    try
                    {
                        var snapshot = await _service.AddByBarcodeAsync(input).ConfigureAwait(true);
                        if (PendingInputQuantity.HasValue && inputQty > 1)
                            snapshot = await _service.SetQtyAsync(input, inputQty).ConfigureAwait(true);
                        ApplySnapshot(snapshot, input);
                        StatusMessage = "Prodotto creato e aggiunto: " + input;
                    }
                    catch (Exception createEx)
                    {
                        StatusMessage = "Errore aggiunta al carrello: " + createEx.Message;
                        _logger.LogError(createEx, "POS VM add after create failed");
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
                PendingInputQuantity = null;
                BarcodeInput = string.Empty;
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        /// <summary>Se il campo barcode contiene solo un numero positivo, lo memorizza come quantità pronta e svuota il campo. Ritorna true se gestito.</summary>
        public bool TryArmPendingQuantityFromBarcodeInput()
        {
            var input = (BarcodeInput ?? string.Empty).Trim();
            if (!int.TryParse(input, out var qty) || qty <= 0)
                return false;
            PendingInputQuantity = qty;
            BarcodeInput = string.Empty;
            StatusMessage = "Quantità pronta: " + qty;
            return true;
        }

        private static bool TryParseQuantityPrefix(string input, out int qty, out string barcodePart)
        {
            qty = 0;
            barcodePart = null;
            var star = input.IndexOf('*');
            if (star <= 0 || star >= input.Length - 1) return false;
            if (!int.TryParse(input.Substring(0, star).Trim(), out qty) || qty <= 0) return false;
            barcodePart = input.Substring(star + 1).Trim();
            return barcodePart.Length > 0;
        }

        private async Task<bool> TryHandleQuantityInputAsync(string input)
        {
            if (SelectedCartItem == null) return false;

            if (input.StartsWith("=", StringComparison.Ordinal))
            {
                if (!int.TryParse(input.Substring(1).Trim(), out var qty) || qty < 0)
                {
                    StatusMessage = "Quantità non valida.";
                    return true;
                }
                if (qty == 0)
                {
                    try
                    {
                        var barcode = SelectedCartItem.Barcode;
                        var indexBefore = CartItems.IndexOf(SelectedCartItem);
                        var snapshot = await _service.RemoveLineAsync(barcode).ConfigureAwait(true);
                        var preferIndex = indexBefore < CartItems.Count - 1 ? indexBefore : (indexBefore > 0 ? indexBefore - 1 : (int?)null);
                        ApplySnapshot(snapshot, preferBarcode: null, preferIndex: preferIndex);
                        StatusMessage = "Riga rimossa.";
                    }
                    catch (Exception ex)
                    {
                        StatusMessage = "Errore: " + ex.Message;
                        _logger.LogError(ex, "POS VM set qty 0 failed");
                    }
                    return true;
                }
                IsBusy = true;
                try
                {
                    var barcode = SelectedCartItem.Barcode;
                    var snapshot = await _service.SetQtyAsync(barcode, qty).ConfigureAwait(true);
                    ApplySnapshot(snapshot, barcode);
                    StatusMessage = "Quantità aggiornata: " + qty;
                }
                catch (Exception ex)
                {
                    StatusMessage = "Errore modifica quantità: " + ex.Message;
                    _logger.LogError(ex, "POS VM set qty failed");
                }
                finally { IsBusy = false; }
                return true;
            }

            if (input.StartsWith("+", StringComparison.Ordinal) && int.TryParse(input.Substring(1).Trim(), out var deltaPlus) && deltaPlus > 0)
            {
                IsBusy = true;
                try
                {
                    var barcodePlus = SelectedCartItem.Barcode;
                    for (var i = 0; i < deltaPlus; i++)
                    {
                        var snapshot = await _service.IncreaseQtyAsync(barcodePlus).ConfigureAwait(true);
                        ApplySnapshot(snapshot, barcodePlus);
                    }
                    StatusMessage = "Quantità +" + deltaPlus;
                }
                catch (Exception ex)
                {
                    StatusMessage = "Errore: " + ex.Message;
                    _logger.LogError(ex, "POS VM increase qty failed");
                }
                finally { IsBusy = false; }
                return true;
            }

            if (input.StartsWith("-", StringComparison.Ordinal) && int.TryParse(input.Substring(1).Trim(), out var deltaMinus) && deltaMinus > 0)
            {
                IsBusy = true;
                try
                {
                    var barcodeMinus = SelectedCartItem.Barcode;
                    var current = SelectedCartItem.Quantity;
                    for (var i = 0; i < deltaMinus && current > 1; i++)
                    {
                        var snapshot = await _service.DecreaseQtyAsync(barcodeMinus).ConfigureAwait(true);
                        ApplySnapshot(snapshot, barcodeMinus);
                        current--;
                    }
                    StatusMessage = "Quantità -" + deltaMinus;
                }
                catch (Exception ex)
                {
                    StatusMessage = "Errore: " + ex.Message;
                    _logger.LogError(ex, "POS VM decrease qty failed");
                }
                finally { IsBusy = false; }
                return true;
            }

            return false;
        }

        private async Task PayAsync()
        {
            if (CartItems.Count == 0)
            {
                StatusMessage = "Carrello vuoto";
                return;
            }

            var shop = await _service.GetShopInfoAsync().ConfigureAwait(true);
            var nextBoleta = await _service.GetFiscalBoletaNumberAsync().ConfigureAwait(true) + 1;
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
                DefaultPrint = true,
                ShopInfo = shop,
                NextBoletaNumber = nextBoleta
            };

            var fiscalPdf = new FiscalPdfService();
            var vm = new PaymentViewModel(Total, draft,
                (text, code) => fiscalPdf.GenerateFiscalPdfAsync(text, code),
                async (text, code) => await _service.PrintReceiptTextAsync(text, UseReceipt42, "FISCAL_" + code, isFiscalPrint: true).ConfigureAwait(true));

            bool ok;
            try
            {
                var mainWindow = Application.Current?.MainWindow as MainWindow;
                if (mainWindow == null)
                {
                    _logger.LogError(null, "MainWindow not available for payment screen.");
                    StatusMessage = "Errore: finestra principale non disponibile.";
                    RequestFocusBarcode();
                    return;
                }
                ok = await mainWindow.ShowPaymentScreenAsync(vm).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment screen failed.");
                MessageBox.Show("Errore Pay.\n\n" + ex.Message, "Pay error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusMessage = "Errore Pay.";
                RequestFocusBarcode();
                return;
            }

            if (vm.WasSuspended)
            {
                await SuspendCartAsync().ConfigureAwait(true);
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
                    CashAmountMinor = vm.CashAmountMinor,
                    CardAmountMinor = vm.CardAmountMinor
                };
                var result = await _service.CompleteSaleAsync(
                    payment,
                    vm.SaleCode,
                    vm.CreatedAtMs).ConfigureAwait(true);
                ApplySnapshot(result.Snapshot);
                ReceiptPreview = UseReceipt42 ? result.Receipt42 : result.Receipt32;
                StatusMessage = "Pagamento OK: " + result.SaleCode;

                var fiscalPrinted = false;
                if (vm.ShouldPrint)
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

                if (vm.AutoPrintPdfSii)
                    fiscalPrinted = await vm.TriggerAutoPrintPdfIfEnabledAsync().ConfigureAwait(true);

                if (fiscalPrinted)
                    await _service.SetFiscalBoletaNumberAsync(vm.NextBoletaNumber).ConfigureAwait(true);

                if (!fiscalPrinted && vm.AutoPrintPdfSii && vm.CardAmountMinor > 0 && vm.CashAmountMinor == 0)
                    StatusMessage = "Pagamento OK: " + result.SaleCode + " (PDF SII non stampato: pagamento con carta)";

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
            if (SelectedCartItem == null || IsDiscountLine(SelectedCartItem.Barcode)) return;
            var barcode = SelectedCartItem.Barcode;
            IsBusy = true;
            try
            {
                var snapshot = await _service.IncreaseQtyAsync(barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot, barcode);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore Qty+: " + ex.Message;
                _logger.LogError(ex, "POS VM increase qty failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task DecreaseQtyAsync()
        {
            if (SelectedCartItem == null || IsDiscountLine(SelectedCartItem.Barcode)) return;
            var barcode = SelectedCartItem.Barcode;
            IsBusy = true;
            try
            {
                var snapshot = await _service.DecreaseQtyAsync(barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot, barcode);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore Qty-: " + ex.Message;
                _logger.LogError(ex, "POS VM decrease qty failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task RemoveLineAsync()
        {
            if (SelectedCartItem == null) return;
            var barcode = SelectedCartItem.Barcode;
            var indexBefore = CartItems.IndexOf(SelectedCartItem);
            IsBusy = true;
            try
            {
                var snapshot = await _service.RemoveLineAsync(barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot, preferBarcode: null, preferIndex: indexBefore < CartItems.Count - 1 ? indexBefore : (indexBefore > 0 ? indexBefore - 1 : (int?)null));
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore remove line: " + ex.Message;
                _logger.LogError(ex, "POS VM remove line failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
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
            if (IsDiscountLine(row.Barcode)) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.IncreaseQtyAsync(row.Barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot, row.Barcode);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore Qty+: " + ex.Message;
                _logger.LogError(ex, "POS VM increase qty failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task DecreaseQtyForLineAsync(object parameter)
        {
            var row = parameter as PosCartLineRow;
            if (row == null) return;
            if (IsDiscountLine(row.Barcode)) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.DecreaseQtyAsync(row.Barcode).ConfigureAwait(true);
                ApplySnapshot(snapshot, row.Barcode);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore Qty-: " + ex.Message;
                _logger.LogError(ex, "POS VM decrease qty failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task RemoveLineForLineAsync(object parameter)
        {
            var row = parameter as PosCartLineRow;
            if (row == null) return;
            var indexBefore = CartItems.IndexOf(row);
            IsBusy = true;
            try
            {
                var snapshot = await _service.RemoveLineAsync(row.Barcode).ConfigureAwait(true);
                var preferIndex = indexBefore < CartItems.Count - 1 ? indexBefore : (indexBefore > 0 ? indexBefore - 1 : (int?)null);
                ApplySnapshot(snapshot, preferBarcode: null, preferIndex: preferIndex);
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore remove line: " + ex.Message;
                _logger.LogError(ex, "POS VM remove line failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
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
            try
            {
                var vm = new DailyReportViewModel(_service);
                var dlg = new DailyReportDialog(vm)
                {
                    Owner = Application.Current?.MainWindow
                };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily report dialog failed (XAML/binding).");
                StatusMessage = "Errore apertura Incasso giornaliero.";
                MessageBox.Show("Errore apertura Incasso giornaliero.\n\n" + ex.Message, "Daily report", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            try
            {
                var vm = new AboutSupportViewModel(_service);
                var dlg = new AboutSupportDialog(vm)
                {
                    Owner = Application.Current?.MainWindow
                };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "About/Support dialog failed (XAML/binding).");
                StatusMessage = "Errore apertura About/Support.";
                MessageBox.Show("Errore apertura About/Support.\n\n" + ex.Message, "About", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            await OpenRefundForSaleIdThenRefreshAsync(SelectedRecentSale.SaleId, null).ConfigureAwait(true);
        }

        private async Task OpenRefundForSaleIdThenRefreshAsync(long saleId, Dialogs.SalesRegisterViewModel registerVm)
        {
            IsBusy = true;
            try
            {
                var preview = await _service.BuildRefundPreviewAsync(saleId).ConfigureAwait(true);
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
                if (registerVm != null && registerVm.LoadCommand.CanExecute(null))
                    registerVm.LoadCommand.Execute(null);
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

        private Task OpenSalesRegisterAsync()
        {
            return OpenSalesRegisterInternalAsync(isRefundScanMode: false);
        }

        private Task OpenResoScanAsync()
        {
            return OpenSalesRegisterInternalAsync(isRefundScanMode: true);
        }

        private Task OpenSalesRegisterInternalAsync(bool isRefundScanMode)
        {
            try
            {
                var registerVm = new Dialogs.SalesRegisterViewModel(_service, UseReceipt42, (saleId, regVm) =>
                {
                    _ = OpenRefundForSaleIdThenRefreshAsync(saleId, regVm);
                }, isRefundScanMode);
                var dlg = new Dialogs.SalesRegisterDialog(registerVm)
                {
                    Owner = Application.Current?.MainWindow
                };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sales register dialog failed (XAML/binding).");
                StatusMessage = isRefundScanMode ? "Errore apertura Reso." : "Errore apertura Registro vendite.";
                MessageBox.Show("Errore apertura " + (isRefundScanMode ? "Reso" : "Registro vendite") + ".\n\n" + ex.Message, isRefundScanMode ? "Reso" : "Registro vendite", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            RequestFocusBarcode();
            return Task.CompletedTask;
        }

        private Task OpenShopSettingsAsync()
        {
            try
            {
                var vm = new Dialogs.ShopSettingsViewModel(_service);
                var dlg = new Dialogs.ShopSettingsDialog(vm)
                {
                    Owner = Application.Current?.MainWindow
                };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shop settings dialog failed (XAML/binding).");
                StatusMessage = "Errore apertura Impostazioni negozio.";
                MessageBox.Show("Errore apertura Impostazioni negozio.\n\n" + ex.Message, "Impostazioni negozio", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            RequestFocusBarcode();
            return Task.CompletedTask;
        }

        private async Task SuspendCartAsync()
        {
            IsBusy = true;
            try
            {
                var result = await _service.SuspendCartAsync().ConfigureAwait(true);
                if (result.Success)
                {
                    var snapshot = await _service.GetSnapshotAsync().ConfigureAwait(true);
                    ApplySnapshot(snapshot);
                    StatusMessage = result.Message;
                }
                else
                {
                    StatusMessage = result.Message;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore sospensione: " + ex.Message;
                _logger.LogError(ex, "Suspend cart failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RecoverCartAsync()
        {
            var vm = new Dialogs.HeldCartsViewModel(_service, snapshot =>
            {
                ApplySnapshot(snapshot);
                StatusMessage = snapshot?.Status ?? "Carrello recuperato.";
            });
            var dlg = new Dialogs.HeldCartsDialog(vm) { Owner = Application.Current?.MainWindow };

            try
            {
                await vm.LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recover cart LoadAsync failed");
                StatusMessage = "Errore caricamento sospesi: " + ex.Message;
            }

            dlg.ShowDialog();
        }

        private static bool CanEditCartLine(PosCartLineRow line)
        {
            if (line == null) return false;
            if (line.IsDiscountLine) return false;
            if (!string.IsNullOrEmpty(line.Barcode) && line.Barcode.StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private async void OpenEditProductExecute(object parameter)
        {
            var line = parameter as PosCartLineRow ?? SelectedCartItem;
            if (line == null || !CanEditCartLine(line)) return;
            try
            {
                var productsService = new ProductsWorkflowService();
                var product = await productsService.GetByBarcodeDetailsAsync(line.Barcode).ConfigureAwait(true);
                if (product == null)
                {
                    StatusMessage = "Prodotto non trovato.";
                    return;
                }

                var ok = await ProductEditDialog.ShowAsync(ProductEditMode.Edit, product, productsService).ConfigureAwait(true);
                if (ok)
                {
                    await RefreshCartFromDatabaseAsync("Carrello aggiornato dopo modifica prodotto.").ConfigureAwait(true);
                    RequestFocusBarcode();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS full edit product dialog failed.");
                StatusMessage = "Errore modifica prodotto.";
            }
        }

        private bool OpenEditProductCanExecute(object parameter)
        {
            var line = parameter as PosCartLineRow ?? SelectedCartItem;
            return !IsBusy && line != null && CanEditCartLine(line);
        }

        private void OpenChangeQuantity()
        {
            if (SelectedCartItem == null || SelectedCartItem.IsDiscountLine) return;
            var dlg = new Dialogs.ChangeQuantityDialog(SelectedCartItem.Name ?? "", SelectedCartItem.Quantity)
            {
                Owner = Application.Current?.MainWindow
            };
            if (dlg.ShowDialog() == true)
                _ = SetSelectedLineQtyAsync(dlg.Quantity);
        }

        private void OpenChangeQuantityForLine(PosCartLineRow row)
        {
            if (row == null || row.IsDiscountLine) return;
            SelectedCartItem = row;
            OpenChangeQuantity();
        }

        private async Task SetSelectedLineQtyAsync(int qty)
        {
            if (SelectedCartItem == null) return;
            IsBusy = true;
            try
            {
                var snapshot = await _service.SetQtyByLineAsync(SelectedCartItem.LineKey, qty).ConfigureAwait(true);
                ApplySnapshot(snapshot);
                StatusMessage = qty <= 0 ? "Riga rimossa." : "Quantità aggiornata: " + qty;
                RequestFocusBarcode();
            }
            catch (Exception ex)
            {
                StatusMessage = "Errore modifica quantità: " + ex.Message;
                _logger.LogError(ex, "POS VM set line qty failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OpenDiscount()
        {
            try
            {
                var selectedBarcode = SelectedCartItem?.Barcode;
                var hasCart = CartItems.Count > 0;
                var dlg = new Dialogs.DiscountDialog(selectedBarcode ?? string.Empty, hasCart, _service, this)
                {
                    Owner = Application.Current?.MainWindow
                };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discount dialog failed.");
                StatusMessage = "Errore apertura Sconto.";
            }
        }

        private static bool TryParseManualPriceClp(string raw, out int price)
        {
            price = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var s = raw.Trim();

            bool allDigits = s.All(char.IsDigit);
            if (allDigits)
            {
                if (s.Length > 6) return false;
                return int.TryParse(s, out price) && price > 0;
            }

            if (s.Contains(".") || s.Contains(","))
            {
                int lastDot = s.LastIndexOf('.');
                int lastComma = s.LastIndexOf(',');
                int lastSep = Math.Max(lastDot, lastComma);

                var before = lastSep >= 0 ? s.Substring(0, lastSep) : s;
                before = before.Replace(".", "").Replace(",", "").Replace(" ", "");

                if (!before.All(char.IsDigit)) return false;
                if (!int.TryParse(before, out price)) return false;
                return price > 0;
            }

            return false;
        }

        public void ApplyDiscountSnapshot(PosWorkflowSnapshot snapshot)
        {
            if (snapshot != null) ApplySnapshot(snapshot);
        }

        /// <summary>Applica lo snapshot. preferBarcode: riga da selezionare; preferIndex: indice da selezionare (es. dopo rimozione).</summary>
        private void ApplySnapshot(PosWorkflowSnapshot snapshot, string preferBarcode = null, int? preferIndex = null)
        {
            CartItems.Clear();
            foreach (var item in snapshot.Lines)
            {
                CartItems.Add(new PosCartLineRow
                {
                    LineKey = item.LineKey ?? string.Empty,
                    Barcode = item.Barcode,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    LineTotal = item.LineTotal,
                    StockQty = item.StockQty,
                    DiscountAmountMinor = item.DiscountAmountMinor,
                    DiscountPercent = item.DiscountPercent
                });
            }

            Subtotal = snapshot.Subtotal;
            Total = snapshot.Total;
            OnPropertyChanged(nameof(TotalDisplay));
            OnPropertyChanged(nameof(OriginalTotalDisplay));
            OnPropertyChanged(nameof(DiscountAmountDisplay));
            OnPropertyChanged(nameof(FinalTotalDisplay));
            OnPropertyChanged(nameof(HasDiscount));
            if (!string.IsNullOrWhiteSpace(snapshot.Status))
                StatusMessage = snapshot.Status;
            OnPropertyChanged(nameof(ItemsCount));

            PosCartLineRow selected = null;
            if (!string.IsNullOrWhiteSpace(preferBarcode))
                selected = CartItems.LastOrDefault(x => string.Equals(x.Barcode, preferBarcode, StringComparison.OrdinalIgnoreCase));
            if (selected == null && preferIndex.HasValue && preferIndex.Value >= 0 && preferIndex.Value < CartItems.Count)
                selected = CartItems[preferIndex.Value];
            if (selected == null && CartItems.Count > 0)
                selected = CartItems[CartItems.Count - 1];
            SelectedCartItem = selected;

            (ClearCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (OpenDiscountCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenEditProductCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChangeQuantityCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChangeQuantityForLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SuspendCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            (OpenSalesRegisterCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (OpenShopSettingsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (OpenDiscountCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenEditProductCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChangeQuantityCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChangeQuantityForLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SuspendCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RecoverCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private void RequestFocusBarcode()
        {
            FocusBarcodeRequested?.Invoke();
        }

        private static bool IsDiscountLine(string barcode)
            => DiscountKeys.IsDiscount(barcode ?? "");

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class PosCartLineRow
        {
            public string LineKey { get; set; } = string.Empty;
            public string Barcode { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public long UnitPrice { get; set; }
            public long LineTotal { get; set; }
            public int StockQty { get; set; }
            public long DiscountAmountMinor { get; set; }
            public int DiscountPercent { get; set; }

            public bool HasDiscount => !IsDiscountLine && DiscountAmountMinor > 0;
            public string UnitPriceDisplay => MoneyClp.Format(UnitPrice);
            public string LineTotalDisplay => MoneyClp.Format(LineTotal);
            public string OriginalUnitPriceDisplay => MoneyClp.Format(UnitPrice);
            public string OriginalLineTotalDisplay => MoneyClp.Format(LineTotal);
            public string DiscountedUnitPriceDisplay => HasDiscount && Quantity > 0 ? MoneyClp.Format((LineTotal - DiscountAmountMinor) / Quantity) : MoneyClp.Format(UnitPrice);
            public string DiscountedLineTotalDisplay => HasDiscount ? MoneyClp.Format(LineTotal - DiscountAmountMinor) : MoneyClp.Format(LineTotal);
            public string DiscountPercentDisplay => HasDiscount && DiscountPercent > 0 ? "-" + DiscountPercent + "%" : string.Empty;
            public string DiscountAmountDisplay => HasDiscount ? "Risparmio " + MoneyClp.Format(DiscountAmountMinor) : string.Empty;

            /// <summary>Nome da mostrare in carrello/scontrino: per sconti aggiunge prefisso "— " per evidenza.</summary>
            public string DisplayName => IsDiscountLine ? "— " + (Name ?? "Sconto") : (Name ?? "");
            public string StockDisplay => IsDiscountLine || (Barcode ?? "").StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase) ? "" : "Stock: " + StockQty;
            public bool IsDiscountLine => DiscountKeys.IsDiscount(Barcode ?? "");
            /// <summary>True se la riga è modificabile (no sconto, no manual).</summary>
            public bool IsEditable => !IsDiscountLine && (string.IsNullOrEmpty(Barcode) || !Barcode.StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase));
        }

        public sealed class RecentSaleRow
        {
            public long SaleId { get; set; }
            public string SaleCode { get; set; } = string.Empty;
            public string TimeText { get; set; } = string.Empty;
            public long Total { get; set; }
            public string TotalDisplay => MoneyClp.Format(Total);
            public int Kind { get; set; }
            public string KindText { get; set; } = string.Empty;
            public long? RelatedSaleId { get; set; }
            public long? VoidedBySaleId { get; set; }
            public string StatusText { get; set; } = string.Empty;
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            private readonly Func<object, bool> _canExecute;

            public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
            public void Execute(object parameter) => _execute(parameter);
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;
            private readonly FileLogger _logger;

            public AsyncRelayCommand(Func<Task> executeAsync, Func<object, bool> canExecute = null, FileLogger logger = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
                _logger = logger ?? new FileLogger();
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
                    UiErrorHandler.Handle(ex, _logger, "AsyncRelayCommand failed");
                }
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class AsyncRelayCommandParam : ICommand
        {
            private readonly Func<object, Task> _executeAsync;
            private readonly Func<object, bool> _canExecute;
            private readonly FileLogger _logger;

            public AsyncRelayCommandParam(Func<object, Task> executeAsync, Func<object, bool> canExecute = null, FileLogger logger = null)
            {
                _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
                _canExecute = canExecute;
                _logger = logger ?? new FileLogger();
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

            public async void Execute(object parameter)
            {
                try
                {
                    await _executeAsync(parameter).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    UiErrorHandler.Handle(ex, _logger, "AsyncRelayCommandParam failed");
                }
            }

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
