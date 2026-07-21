using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Globalization;
using System.Windows.Threading;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Security;
using Win7POS.Core.Util;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.Pos
{
    /// <summary>ViewModel POS. Tutti i punti sensibili passano da Demand/TryDemandOrOverride: vendita (PosSell), pagamento (PosPay), sconto, refund, void, sospendi/recupera carrello, registro vendite, ristampa, impostazioni negozio/stampante, backup/restore DB, manutenzione DB, modifica catalogo, utenti/ruoli.</summary>
    public sealed class PosViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PosWorkflowService _service;
        private readonly FileLogger _logger;
        private readonly IPermissionService _permissionService;
        private readonly IOperatorSession _operatorSession;
        private readonly IOverrideAuthService _overrideAuthService;
        private readonly Win7POS.Data.Repositories.UserRepository _userRepo;

        private string _barcodeInput = string.Empty;
        private long _subtotal;
        private long _total;
        private bool _isBusy;
        private bool _isPaymentCommitInProgress;
        private string _statusMessage = string.Empty;
        private string _statusToastMessage = string.Empty;
        private bool _isStatusToastVisible;
        private PosNoticeSeverity _statusToastSeverity = PosNoticeSeverity.Info;
        private readonly DispatcherTimer _statusToastTimer;
        private readonly EventHandler _statusToastTickHandler;
        private readonly EventHandler _languageChangedHandler;
        private bool _disposed;
        private string _receiptPreview = string.Empty;
        private bool _useReceipt42 = true;
        private bool _isLoadingSettings;
        private PosPrinterSettings _printerSettings = new PosPrinterSettings();
        private string _lastPrintFailureMessage = string.Empty;

        private PosCartLineRow _selectedCartItem;
        private RecentSaleRow _selectedRecentSale;
        private int? _pendingInputQuantity;
        private CustomerDisplaySnapshot _lastCustomerCartSnapshot = CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
        private string _customerDisplayShopName = string.Empty;
        private IReadOnlyList<PaymentReceiptDraftLine> _paymentReceiptDraftLines =
            Array.Empty<PaymentReceiptDraftLine>();

        public ObservableCollection<PosCartLineRow> CartItems { get; } = new ObservableCollection<PosCartLineRow>();
        public ObservableCollection<RecentSaleRow> RecentSales { get; } = new ObservableCollection<RecentSaleRow>();
        public event Action FocusBarcodeRequested;
        public event Action<CustomerDisplaySnapshot> CustomerDisplaySnapshotChanged;
        public CustomerDisplaySnapshot CurrentCustomerDisplaySnapshot { get; private set; } =
            CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);

        public void SetCustomerDisplayShopName(string shopName)
        {
            var normalized = string.IsNullOrWhiteSpace(shopName) ? string.Empty : shopName.Trim();
            if (string.Equals(_customerDisplayShopName, normalized, StringComparison.Ordinal)) return;
            _customerDisplayShopName = normalized;
            PublishCustomerCartSnapshot();
        }

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
            set => SetStatus(value, PosNoticeSeverity.Info);
        }

        public string StatusToastMessage
        {
            get => _statusToastMessage;
            private set { _statusToastMessage = value ?? string.Empty; OnPropertyChanged(); }
        }

        public bool IsStatusToastVisible
        {
            get => _isStatusToastVisible;
            private set { _isStatusToastVisible = value; OnPropertyChanged(); }
        }

        public PosNoticeSeverity StatusToastSeverity
        {
            get => _statusToastSeverity;
            private set
            {
                _statusToastSeverity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusToastSeverityText));
                OnPropertyChanged(nameof(StatusToastIconText));
                OnPropertyChanged(nameof(StatusToastBackground));
                OnPropertyChanged(nameof(StatusToastBorderBrush));
                OnPropertyChanged(nameof(StatusToastForeground));
                OnPropertyChanged(nameof(CanDismissStatusToast));
            }
        }

        public string StatusToastSeverityText =>
            PosLocalization.Current.Text("notice." + StatusToastSeverity.ToString().ToLowerInvariant());

        public string StatusToastIconText
        {
            get
            {
                switch (StatusToastSeverity)
                {
                    case PosNoticeSeverity.Success: return "✓";
                    case PosNoticeSeverity.Warning: return "!";
                    case PosNoticeSeverity.Error: return "×";
                    default: return "i";
                }
            }
        }

        public Brush StatusToastBackground => NoticeBrush(
            StatusToastSeverity,
            Color.FromRgb(240, 247, 255),
            Color.FromRgb(235, 248, 238),
            Color.FromRgb(255, 248, 225),
            Color.FromRgb(255, 238, 238));

        public Brush StatusToastBorderBrush => NoticeBrush(
            StatusToastSeverity,
            Color.FromRgb(92, 137, 184),
            Color.FromRgb(63, 132, 77),
            Color.FromRgb(184, 132, 31),
            Color.FromRgb(185, 56, 56));

        public Brush StatusToastForeground => NoticeBrush(
            StatusToastSeverity,
            Color.FromRgb(38, 77, 117),
            Color.FromRgb(31, 100, 48),
            Color.FromRgb(110, 74, 0),
            Color.FromRgb(126, 31, 31));

        public bool CanDismissStatusToast => PosNoticePolicy.CanDismissManually(StatusToastSeverity);

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
            PendingInputQuantity.HasValue
                ? PosLocalization.Current.Format("pos.cart.quantityReady", PendingInputQuantity.Value)
                : string.Empty;

        private bool IsCashDrawerConfigured =>
            _printerSettings.CashDrawerEnabled &&
            string.Equals(_printerSettings.CashDrawerMode, "printer_kick", StringComparison.OrdinalIgnoreCase);

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
        public ICommand OpenCashDrawerCommand { get; }
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
        public ICommand OpenUserManagementCommand { get; }
        public ICommand DismissStatusToastCommand { get; }

        /// <summary>Crea un ViewModel per la schermata Chiusura cassa (pagina integrata).</summary>
        public Dialogs.DailyReportViewModel CreateDailyReportViewModel() => new Dialogs.DailyReportViewModel(_service, _overrideAuthService);

        /// <summary>Costruttore con dipendenze iniettate. Se null, usa istanze di default (compatibilità designer XAML).</summary>
        public PosViewModel(PosWorkflowService service = null, FileLogger logger = null, IPermissionService permissionService = null, IOperatorSession operatorSession = null, IOverrideAuthService overrideAuthService = null, Win7POS.Data.Repositories.UserRepository userRepo = null)
        {
            _service = service ?? new PosWorkflowService();
            _logger = logger ?? new FileLogger("PosViewModel");
            _permissionService = permissionService ?? DenyAllPermissionService.Instance;
            _operatorSession = operatorSession;
            _overrideAuthService = overrideAuthService;
            _userRepo = userRepo;
            _statusToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusToastTickHandler = (_, __) =>
            {
                _statusToastTimer.Stop();
                IsStatusToastVisible = false;
            };
            _statusToastTimer.Tick += _statusToastTickHandler;
            DismissStatusToastCommand = new RelayCommand(_ => DismissStatusToast());

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
            OpenCashDrawerCommand = new AsyncRelayCommand(OpenCashDrawerAsync, _ => !IsBusy && IsCashDrawerConfigured, _logger);
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
            OpenUserManagementCommand = new AsyncRelayCommand(OpenUserManagementAsync, _ => !IsBusy && _permissionService.Has(PermissionCodes.UsersManage), _logger);
            CatalogEvents.CatalogChanged += OnCatalogChanged;
            _languageChangedHandler = (_, __) =>
            {
                if (_disposed) return;
                OnPropertyChanged(nameof(PendingInputQuantityDisplay));
                foreach (var row in CartItems)
                {
                    row.RaiseLocalizedProperties();
                }
            };
            PosLocalization.Current.LanguageChanged += _languageChangedHandler;
            PublishCustomerCartSnapshot();
            SetStatus(PosLocalization.Current.Text("pos.status.ready"), PosNoticeSeverity.Info, suppressToast: true);
        }

        public bool IsPaymentCommitInProgress
        {
            get => _isPaymentCommitInProgress;
            private set { _isPaymentCommitInProgress = value; OnPropertyChanged(); }
        }

        private void OnCatalogChanged(string barcode)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(barcode))
            {
                _ = RefreshCartFromDatabaseAsync(PosLocalization.Current.Text("pos.status.cartSyncedDb"));
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
                SetStatus(snapshot.Status ?? PosLocalization.Current.Text("pos.status.cartSynced"), PosNoticeSeverity.Info);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM sync cart line failed");
            }
        }

        private async Task RefreshCartFromDatabaseAsync(string reason)
        {
            try
            {
                var snapshot = await _service.GetSnapshotAsync().ConfigureAwait(true);
                ApplySnapshot(snapshot);
                SetStatus(reason, PosNoticeSeverity.Info);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                RaiseCanExecuteChanged();
                var snapshot = await _service.GetSnapshotAsync().ConfigureAwait(true);
                ApplySnapshot(snapshot);
                await LoadRecentSalesAsync().ConfigureAwait(true);
                SetStatus(string.Empty, PosNoticeSeverity.Info, suppressToast: true);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
            try { _permissionService.Demand(PermissionCodes.PosSell, PosLocalization.Current.Text("sales.kind.sale")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); return; }
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
                    SetStatus(PosLocalization.Current.Format("pos.status.added", barcodePart, qty), PosNoticeSeverity.Success);
                    PendingInputQuantity = null;
                }
                catch (Exception ex)
                {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                    SetStatus(PosLocalization.Current.Format("pos.status.manualAdded", MoneyClp.Format(priceMinor)), PosNoticeSeverity.Success);
                    return;
                }
                catch (PosException ex) when (ex.Code == PosErrorCode.InvalidPrice)
                {
                    SetStatus(ex.Message, PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("pos.status.productAdded", input), PosNoticeSeverity.Success);
            }
            catch (PosException ex) when (ex.Code == PosErrorCode.ProductNotFound)
            {
                SetStatus(PosLocalization.Current.Format("pos.status.productNotFoundQuickCreate", input), PosNoticeSeverity.Warning);

                if (!(await TryDemandOrOverrideAsync(PermissionCodes.CatalogEdit, PosLocalization.Current.Text("pos.status.quickProductCreate")).ConfigureAwait(true)))
                    return;

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
                        SetStatus(PosLocalization.Current.Format("pos.status.productCreatedAdded", input), PosNoticeSeverity.Success);
                    }
                    catch (Exception createEx)
                    {
                        SetStatus(PosLocalization.Current.Format("common.errorWithMessage", createEx.Message), PosNoticeSeverity.Error);
                        _logger.LogError(createEx, "POS VM add after create failed");
                    }
                }
            }
            catch (PosException ex)
            {
                SetStatus(ex.Message, PosNoticeSeverity.Error);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
            SetStatus(PosLocalization.Current.Format("pos.cart.quantityReady", qty), PosNoticeSeverity.Info);
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
                    SetStatus(PosLocalization.Current.Text("pos.status.quantityInvalid"), PosNoticeSeverity.Warning);
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
                        SetStatus(PosLocalization.Current.Text("pos.status.lineRemoved"), PosNoticeSeverity.Success);
                    }
                    catch (Exception ex)
                    {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                    SetStatus(PosLocalization.Current.Format("pos.status.quantityUpdated", qty), PosNoticeSeverity.Success);
                }
                catch (Exception ex)
                {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                    SetStatus(PosLocalization.Current.Format("pos.status.quantityPlus", deltaPlus), PosNoticeSeverity.Success);
                }
                catch (Exception ex)
                {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                    SetStatus(PosLocalization.Current.Format("pos.status.quantityMinus", deltaMinus), PosNoticeSeverity.Success);
                }
                catch (Exception ex)
                {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
                    _logger.LogError(ex, "POS VM decrease qty failed");
                }
                finally { IsBusy = false; }
                return true;
            }

            return false;
        }

        private async Task PayAsync()
        {
            try { _permissionService.Demand(PermissionCodes.PosPay, PosLocalization.Current.Text("operations.pay")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); return; }
            if (CartItems.Count == 0)
            {
                SetStatus(PosLocalization.Current.Text("pos.status.cartEmpty"), PosNoticeSeverity.Warning);
                return;
            }

            var shop = await _service.GetShopInfoAsync().ConfigureAwait(true);
            var nextBoleta = await _service.GetFiscalBoletaNumberAsync().ConfigureAwait(true) + 1;
            var draft = new PaymentReceiptDraft
            {
                SaleCode = SaleCodeGenerator.NewCode("V"),
                CreatedAtMs = UnixTime.NowMs(),
                CartLines = _paymentReceiptDraftLines,
                UseReceipt42 = UseReceipt42,
                DefaultPrint = _printerSettings.ReceiptEnabled && _printerSettings.AutoPrint,
                ShopInfo = shop,
                NextBoletaNumber = nextBoleta
            };

            using var vm = new PaymentViewModel(Total, draft,
                async (text, code) =>
                {
                    await _service.PrintReceiptTextAsync(
                        text,
                        UseReceipt42,
                        "FISCAL_" + code,
                        isFiscalPrint: true,
                        automaticAfterSale: true).ConfigureAwait(true);
                },
                openDrawerDefault: IsCashDrawerConfigured && _printerSettings.CashDrawerOpenOnCashSale);

            PublishCustomerState(CustomerDisplayState.Payment, "payment");
            bool ok;
            try
            {
                var mainWindow = DialogOwnerHelper.GetSafeOwner() as MainWindow;
                if (mainWindow == null)
                {
                    _logger.LogError(null, "MainWindow not available for payment screen.");
                    SetStatus(PosLocalization.Current.Text("pos.status.mainWindowUnavailable"), PosNoticeSeverity.Error);
                    PublishCustomerCartSnapshot();
                    RequestFocusBarcode();
                    return;
                }
                ok = await mainWindow.ShowPaymentScreenAsync(vm).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Payment screen failed.");
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("pos.status.payError"), PosLocalization.Current.Format("pos.status.payErrorDetail", ex.Message));
                SetStatus(PosLocalization.Current.Text("pos.status.payError"), PosNoticeSeverity.Error);
                PublishCustomerCartSnapshot();
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
                SetStatus(PosLocalization.Current.Text("pos.status.paymentCancelled"), PosNoticeSeverity.Info);
                PublishCustomerCartSnapshot();
                RequestFocusBarcode();
                return;
            }

            try
            {
                _permissionService.Demand(PermissionCodes.PosPay, PosLocalization.Current.Text("operations.pay"));
            }
            catch (PosAuthorizationLeaseException ex)
            {
                StatusMessage = ex.Message;
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.Current.Text("common.userPermissionDenied"),
                    ex.Message);
                PublishCustomerCartSnapshot();
                return;
            }

            IsBusy = true;
            IsPaymentCommitInProgress = true;
            try
            {
                if (vm.AutoPrintFiscalBoleta && vm.CashAmountMinor > 0)
                {
                    vm.NextBoletaNumber = await _service
                        .ReserveFiscalBoletaNumberAsync(vm.NextBoletaNumber)
                        .ConfigureAwait(true);
                }

                var payment = new PosPaymentInfo
                {
                    CashAmountMinor = vm.CashAmountMinor,
                    CardAmountMinor = vm.CardAmountMinor
                };
                var operatorId = _operatorSession?.CurrentUser?.Id;
                var result = await _service.CompleteSaleAsync(
                    payment,
                    vm.SaleCode,
                    vm.CreatedAtMs,
                    operatorId,
                    draft.ShopInfo).ConfigureAwait(true);
                var completedCart = _lastCustomerCartSnapshot;
                ApplySnapshot(result.Snapshot);
                PublishCustomerSnapshot(CustomerDisplayProjection.Completed(
                    completedCart,
                    result.TotalMinor,
                    result.PaidMinor,
                    result.ChangeMinor,
                    DateTimeOffset.UtcNow));
                ReceiptPreview = UseReceipt42 ? result.Receipt42 : result.Receipt32;
                SetStatus(PosLocalization.Current.Format("pos.status.paymentOk", result.SaleCode), PosNoticeSeverity.Success);

                await TryAutoOpenDrawerAfterPaymentAsync(vm).ConfigureAwait(true);

                var fiscalBoletaPrinted = false;
                if (vm.ShouldPrint)
                {
                    var printed = await PrintReceiptAsync(ReceiptPreview, result.SaleCode, automaticAfterSale: true).ConfigureAwait(true);
                    if (!printed)
                    {
                        ModernMessageDialog.Show(
                            DialogOwnerHelper.GetSafeOwner(),
                            PosLocalization.Current.Text("pos.status.printFailedTitle"),
                            PosLocalization.Current.Format(
                                "printer.saleSavedPrintWarning",
                                string.IsNullOrWhiteSpace(_lastPrintFailureMessage)
                                    ? PosLocalization.Current.Text("pos.status.receiptNotPrintedCheckPrinter")
                                    : _lastPrintFailureMessage));
                    }
                }

                if (vm.AutoPrintFiscalBoleta)
                {
                    try
                    {
                        fiscalBoletaPrinted = await vm
                            .TriggerAutoPrintFiscalBoletaIfEnabledAsync()
                            .ConfigureAwait(true);
                    }
                    catch (Exception fiscalPrintEx)
                    {
                        _logger.LogError(
                            fiscalPrintEx,
                            "Fiscal boleta print failed after sale commit. saleCode=" +
                            result.SaleCode +
                            " boletaNumber=" +
                            vm.NextBoletaNumber.ToString(CultureInfo.InvariantCulture));
                        SetStatus(
                            PosLocalization.Current.Format(
                                "pos.status.paymentOkFiscalPrintFailed",
                                result.SaleCode,
                                vm.NextBoletaNumber),
                            PosNoticeSeverity.Warning);
                        ModernMessageDialog.Show(
                            DialogOwnerHelper.GetSafeOwner(),
                            PosLocalization.Current.Text("pos.status.printFailedTitle"),
                            PosLocalization.Current.Format(
                                "printer.fiscalSaleSavedPrintWarning",
                                vm.NextBoletaNumber,
                                string.IsNullOrWhiteSpace(fiscalPrintEx.Message)
                                    ? PosLocalization.Current.Text("pos.status.receiptNotPrintedCheckPrinter")
                                    : fiscalPrintEx.Message));
                    }
                }

                if (fiscalBoletaPrinted)
                {
                    try
                    {
                        await _service.MarkPdfPrintedAsync(result.SaleId).ConfigureAwait(true);
                    }
                    catch (Exception fiscalStatusEx)
                    {
                        _logger.LogError(
                            fiscalStatusEx,
                            "Fiscal boleta printed but status persistence failed. saleCode=" +
                            result.SaleCode +
                            " boletaNumber=" +
                            vm.NextBoletaNumber.ToString(CultureInfo.InvariantCulture));
                        SetStatus(
                            PosLocalization.Current.Format(
                                "pos.status.paymentOkFiscalStatusSaveFailed",
                                result.SaleCode,
                                vm.NextBoletaNumber),
                            PosNoticeSeverity.Warning);
                        ModernMessageDialog.Show(
                            DialogOwnerHelper.GetSafeOwner(),
                            PosLocalization.Current.Text("pos.status.printFailedTitle"),
                            PosLocalization.Current.Format(
                                "printer.fiscalPrintedStatusSaveWarning",
                                vm.NextBoletaNumber));
                    }
                }

                if (!fiscalBoletaPrinted && vm.AutoPrintFiscalBoleta && vm.CardAmountMinor > 0 && vm.CashAmountMinor == 0)
                    SetStatus(PosLocalization.Current.Format("pos.status.paymentOkCardOnly", result.SaleCode), PosNoticeSeverity.Warning);

                await LoadRecentSalesAsync().ConfigureAwait(true);
            }
            catch (PosException ex)
            {
                SetStatus(ex.Message, PosNoticeSeverity.Error);
                PublishCustomerCartSnapshot();
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM pay failed");
                PublishCustomerCartSnapshot();
            }
            finally
            {
                IsPaymentCommitInProgress = false;
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task TryAutoOpenDrawerAfterPaymentAsync(PaymentViewModel vm)
        {
            if (vm == null) return;
            if (!IsCashDrawerConfigured) return;
            if (!vm.OpenDrawerForCurrentPayment) return;
            if (vm.CashAmountMinor <= 0) return;

            try
            {
                await _service.OpenCashDrawerAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS VM auto drawer open failed");
                SetStatus(PosLocalization.Current.Text("pos.status.paymentOkDrawerFailed"), PosNoticeSeverity.Warning);
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.Current.Text("printer.cashDrawer"),
                    PosLocalization.Current.Format("printer.saleSavedDrawerWarning", ex.Message));
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
                    SetStatus(PosLocalization.Current.Text("pos.status.noReceiptPayFirst"), PosNoticeSeverity.Warning);
                    return;
                }

                ReceiptPreview = preview;
                SetStatus(PosLocalization.Current.Text("pos.status.receiptPreviewUpdated"), PosNoticeSeverity.Success);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM load recent sales failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ReprintPreviewAsync()
        {
            try { _permissionService.Demand(PermissionCodes.PosReprintReceipt, PosLocalization.Current.Text("printer.reprintReceipt")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); return; }
            if (SelectedRecentSale == null) return;

            IsBusy = true;
            try
            {
                var preview = await _service.GetReceiptPreviewBySaleIdAsync(SelectedRecentSale.SaleId, UseReceipt42).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(preview))
                {
                    SetStatus(PosLocalization.Current.Text("pos.status.saleNotFound"), PosNoticeSeverity.Warning);
                    return;
                }

                ReceiptPreview = preview;
                SetStatus(PosLocalization.Current.Format("pos.status.receiptPreviewLoaded", SelectedRecentSale.SaleCode), PosNoticeSeverity.Success);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM reprint preview failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task PrintSelectedReceiptAsync()
        {
            try { _permissionService.Demand(PermissionCodes.PosReprintReceipt, PosLocalization.Current.Text("printer.printReceipt")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); return; }
            if (SelectedRecentSale == null)
            {
                SetStatus(PosLocalization.Current.Text("pos.status.selectSale"), PosNoticeSeverity.Warning);
                return;
            }

            IsBusy = true;
            try
            {
                var preview = await _service.GetReceiptPreviewBySaleIdAsync(SelectedRecentSale.SaleId, UseReceipt42).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(preview))
                {
                    SetStatus(PosLocalization.Current.Text("pos.status.receiptUnavailable"), PosNoticeSeverity.Warning);
                    return;
                }

                ReceiptPreview = preview;
                await PrintReceiptAsync(preview, "SALE_" + SelectedRecentSale.SaleCode, explicitUserAction: true).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Text("pos.status.cartCleared"), PosNoticeSeverity.Success);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
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
            try { _permissionService.Demand(PermissionCodes.DbBackup, PosLocalization.Current.Text("operations.dbBackup")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); return; }
            IsBusy = true;
            try
            {
                var outputPath = await _service.BackupDbAsync().ConfigureAwait(true);
                _operatorSession?.LogSecurityEvent(
                    SecurityEventCodes.DbBackup,
                    "backupFile=" + Path.GetFileName(outputPath ?? string.Empty));
                SetStatus(PosLocalization.Current.Format("pos.status.dbBackupCreated", outputPath), PosNoticeSeverity.Success);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("pos.status.dbBackupError", ex.Message), PosNoticeSeverity.Error);
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
            if (App.IsSafeStart)
            {
                SetStatus(
                    PosLocalization.Current.Text("printer.settingsDisabledSafeStart"),
                    PosNoticeSeverity.Warning);
                RequestFocusBarcode();
                return;
            }

            if (!(await TryDemandOrOverrideAsync(PermissionCodes.SettingsPrinter, PosLocalization.Current.Text("printer.title")).ConfigureAwait(true))) { RequestFocusBarcode(); return; }
            var vm = new PrinterSettingsViewModel
            {
                PrinterName = _printerSettings.PrinterName,
                Copies = _printerSettings.Copies.ToString(),
                ReceiptEnabled = _printerSettings.ReceiptEnabled,
                AutoPrint = _printerSettings.AutoPrint,
                AllowWindowsDefault = _printerSettings.AllowWindowsDefault,
                AllowVirtualPrinters = _printerSettings.AllowVirtualPrinters,
                CashDrawerCommand = _printerSettings.CashDrawerCommand,
                CashDrawerEnabled = _printerSettings.CashDrawerEnabled,
                CashDrawerMode = _printerSettings.CashDrawerMode,
                CashDrawerPrinterName = _printerSettings.CashDrawerPrinterName,
                CashDrawerOpenOnCashSale = _printerSettings.CashDrawerOpenOnCashSale,
                TestReceiptPreview = await _service.BuildPrinterTestReceiptAsync(UseReceipt42).ConfigureAwait(true)
            };
            Func<Task> refreshPrintersHandler = null;
            Func<Task> testPrintHandler = null;
            Func<string, string, Task> testCashDrawerHandler = null;

            try
            {
                vm.ReplaceInstalledPrinters(await _service.GetInstalledPrintersAsync().ConfigureAwait(true));
                refreshPrintersHandler = async () =>
                {
                    try
                    {
                        vm.ReplaceInstalledPrinters(await _service.GetInstalledPrintersAsync().ConfigureAwait(true));
                        SetStatus(PosLocalization.Current.Text("printer.printersReloaded"), PosNoticeSeverity.Success);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(PosLocalization.Current.Format("printer.discoveryError", ex.Message), PosNoticeSeverity.Error);
                        _logger.LogError(ex, "POS VM printer discovery failed");
                    }
                };
                testPrintHandler = async () =>
                {
                    try
                    {
                        await _service.TestReceiptPrinterAsync(
                            ToPrinterSettings(vm),
                            vm.TestReceiptPreview,
                            UseReceipt42).ConfigureAwait(true);
                        SetStatus(PosLocalization.Current.Text("printer.testPrintSent"), PosNoticeSeverity.Success);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(PosLocalization.Current.Format("printer.testPrintError", ex.Message), PosNoticeSeverity.Error);
                        ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("printer.testPrint"), ex.Message);
                    }
                };
                testCashDrawerHandler = async (name, cmd) =>
                {
                    try
                    {
                        await _service.TestCashDrawerAsync(name, cmd).ConfigureAwait(true);
                        SetStatus(PosLocalization.Current.Text("printer.commandSent"), PosNoticeSeverity.Success);
                    }
                    catch (Exception ex)
                    {
                        SetStatus(PosLocalization.Current.Format("printer.testError", ex.Message), PosNoticeSeverity.Error);
                        ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("printer.testDrawer"), ex.Message);
                    }
                };

                vm.RefreshPrintersRequested += refreshPrintersHandler;
                vm.TestPrintRequested += testPrintHandler;
                vm.TestCashDrawerRequested += testCashDrawerHandler;

                var dlg = new PrinterSettingsDialog(vm)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                var ok = dlg.ShowDialog() == true;
                if (!ok)
                {
                    SetStatus(PosLocalization.Current.Text("printer.settingsCancelled"), PosNoticeSeverity.Info);
                    RequestFocusBarcode();
                    return;
                }

                _printerSettings = ToPrinterSettings(vm);

                try
                {
                    await _service.SetPrinterSettingsAsync(_printerSettings).ConfigureAwait(true);
                    _printerSettings = await _service.GetPrinterSettingsAsync().ConfigureAwait(true);
                    SetStatus(PosLocalization.Current.Text("printer.settingsSaved"), PosNoticeSeverity.Success);
                    RaiseCanExecuteChanged();
                }
                catch (Exception ex)
                {
                    SetStatus(PosLocalization.Current.Format("printer.settingsSaveError", ex.Message), PosNoticeSeverity.Error);
                    _logger.LogError(ex, "POS VM save printer settings failed");
                }
                finally
                {
                    RequestFocusBarcode();
                }
            }
            finally
            {
                if (refreshPrintersHandler != null)
                    vm.RefreshPrintersRequested -= refreshPrintersHandler;
                if (testPrintHandler != null)
                    vm.TestPrintRequested -= testPrintHandler;
                if (testCashDrawerHandler != null)
                    vm.TestCashDrawerRequested -= testCashDrawerHandler;
                vm.Dispose();
            }
        }

        private async Task PrintLastReceiptAsync()
        {
            try { _permissionService.Demand(PermissionCodes.PosReprintReceipt, PosLocalization.Current.Text("pos.cart.printLast")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); return; }
            IsBusy = true;
            try
            {
                var text = await _service.GetLastReceiptPreviewAsync(UseReceipt42).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetStatus(PosLocalization.Current.Text("printer.noReceipt"), PosNoticeSeverity.Warning);
                    return;
                }

                var saleCode = await _service.GetLastSaleCodeAsync().ConfigureAwait(true);
                await PrintReceiptAsync(
                    text,
                    string.IsNullOrWhiteSpace(saleCode) ? "LAST" : "LAST_" + saleCode,
                    explicitUserAction: true)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("printer.receiptPrintError", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM print last receipt failed");
            }
            finally
            {
                IsBusy = false;
                RequestFocusBarcode();
            }
        }

        private async Task OpenCashDrawerAsync()
        {
            IsBusy = true;
            try
            {
                await _service.OpenCashDrawerAsync().ConfigureAwait(true);
                SetStatus(PosLocalization.Current.Text("printer.drawerOpened"), PosNoticeSeverity.Success);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("printer.drawerOpenError", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM open cash drawer failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static PosPrinterSettings ToPrinterSettings(PrinterSettingsViewModel vm)
        {
            return new PosPrinterSettings
            {
                PrinterName = vm.PrinterName,
                Copies = vm.ParsedCopies,
                ReceiptEnabled = vm.ReceiptEnabled,
                AutoPrint = vm.AutoPrint,
                AllowWindowsDefault = vm.AllowWindowsDefault,
                AllowVirtualPrinters = vm.AllowVirtualPrinters,
                CashDrawerCommand = vm.CashDrawerCommand,
                CashDrawerEnabled = vm.CashDrawerEnabled,
                CashDrawerMode = vm.CashDrawerEnabled ? "printer_kick" : "disabled",
                CashDrawerPrinterName = vm.CashDrawerPrinterName,
                CashDrawerOpenOnCashSale = vm.CashDrawerOpenOnCashSale
            };
        }

        private async Task<bool> PrintReceiptAsync(
            string receiptText,
            string saleCode,
            bool automaticAfterSale = false,
            bool explicitUserAction = false)
        {
            try
            {
                _lastPrintFailureMessage = string.Empty;
                var result = await _service.PrintReceiptTextAsync(
                    receiptText,
                    UseReceipt42,
                    saleCode,
                    automaticAfterSale: automaticAfterSale,
                    explicitUserAction: explicitUserAction).ConfigureAwait(true);
                SetStatus(PosLocalization.Current.Text("printer.receiptPrinted"), PosNoticeSeverity.Success);
                return true;
            }
            catch (Exception ex)
            {
                _lastPrintFailureMessage = ex.Message;
                SetStatus(PosLocalization.Current.Format("sales.printFailed", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM print failed");
                return false;
            }
        }

        private Task OpenDailyReportAsync()
        {
            try
            {
                _permissionService.Demand(PermissionCodes.DailyCloseView, PosLocalization.Current.Text("operations.dailyClose"));
                var vm = new DailyReportViewModel(_service, _overrideAuthService);
                var dlg = new DailyReportDialog(vm)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                dlg.ShowDialog();
            }
            catch (InvalidOperationException ex)
            {
                SetStatus(ex.Message, PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily report dialog failed (XAML/binding).");
                SetStatus(PosLocalization.Current.Text("pos.status.dailyReportOpenError"), PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("reports.closeTitle"), PosLocalization.Current.Format("common.errorWithMessage", ex.Message));
            }
            RequestFocusBarcode();
            return Task.CompletedTask;
        }

        private Task OpenDbMaintenanceAsync()
        {
            try { _permissionService.Demand(PermissionCodes.DbMaintenance, PosLocalization.Current.Text("operations.dbMaintenance")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); RequestFocusBarcode(); return Task.CompletedTask; }
            var vm = new DbMaintenanceViewModel(
                _service,
                async () => await TryDemandOrOverrideAsync(
                    PermissionCodes.DbRestore,
                    PosLocalization.Current.Text("operations.dbRestore")).ConfigureAwait(true),
                () => _permissionService.Has(PermissionCodes.DbBackup),
                () => _permissionService.Has(PermissionCodes.CatalogImport),
                () => _permissionService.Has(PermissionCodes.DbMaintenance));
            var dlg = new DbMaintenanceDialog(vm)
            {
                Owner = DialogOwnerHelper.GetSafeOwner()
            };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);
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
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "About/Support dialog failed (XAML/binding).");
                SetStatus(PosLocalization.Current.Text("pos.status.aboutOpenError"), PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("shell.aboutSupport"), PosLocalization.Current.Format("common.errorWithMessage", ex.Message));
            }
            RequestFocusBarcode();
            return Task.CompletedTask;
        }

        private async Task<bool> TryDemandOrOverrideAsync(string permissionCode, string operationText)
        {
            try
            {
                _permissionService.Demand(permissionCode, operationText);
                return true;
            }
            catch (PosAuthorizationLeaseException ex)
            {
                StatusMessage = ex.Message;
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.Current.Text("common.userPermissionDenied"),
                    ex.Message);
                return false;
            }
            catch (InvalidOperationException)
            {
                if (_overrideAuthService == null)
                {
                    SetStatus(PosLocalization.Current.Format("common.permissionDeniedOperation", operationText), PosNoticeSeverity.Error);
                    ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), StatusMessage);
                    return false;
                }
                if (!ApplyConfirmDialog.ShowConfirm(DialogOwnerHelper.GetSafeOwner(), operationText, PosLocalization.Current.Text("common.supervisorOverridePrompt"))) { _operatorSession?.LogSecurityEvent(SecurityEventCodes.OverrideDenied, "permission=" + permissionCode + " op=" + operationText + " reason=user_declined"); return false; }
                _operatorSession?.LogSecurityEvent(SecurityEventCodes.OverrideRequested, "permission=" + permissionCode + " op=" + operationText);
                var (ok, authorizerId) = await _overrideAuthService.RequestOverrideAsync(operationText, permissionCode).ConfigureAwait(true);
                if (!ok || !authorizerId.HasValue)
                {
                    _operatorSession?.LogSecurityEvent(SecurityEventCodes.OverrideDenied, "permission=" + permissionCode + " op=" + operationText + " reason=authorizer_failed");
                    _operatorSession?.LogSecurityEvent(SecurityEventCodes.OverrideFailed, "permission=" + permissionCode + " op=" + operationText);
                    return false;
                }
                _operatorSession?.LogOverride(permissionCode, operationText, authorizerId.Value);
                _operatorSession?.LogSecurityEvent(SecurityEventCodes.OverrideGranted, "permission=" + permissionCode + " op=" + operationText + " authorizerId=" + authorizerId.Value);
                return true;
            }
        }

        private async Task OpenRefundAsync()
        {
            if (!(await TryDemandOrOverrideAsync(PermissionCodes.PosRefund, PosLocalization.Current.Text("sales.refundVoid")).ConfigureAwait(true))) return;
            if (SelectedRecentSale == null)
            {
                SetStatus(PosLocalization.Current.Text("pos.status.selectSale"), PosNoticeSeverity.Warning);
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
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                var ok = dlg.ShowDialog() == true;
                if (!ok)
                {
                    SetStatus(PosLocalization.Current.Text("pos.status.refundCancelled"), PosNoticeSeverity.Info);
                    return;
                }

                var req = vm.BuildRequest();
                if (_operatorSession == null || !_operatorSession.EnsureAuthorizationValid())
                {
                    StatusMessage = PosLocalization.Current.Text("access.login.authorizationExpired");
                    ModernMessageDialog.Show(
                        DialogOwnerHelper.GetSafeOwner(),
                        PosLocalization.Current.Text("common.userPermissionDenied"),
                        StatusMessage);
                    return;
                }

                if (req.IsFullVoid && !(await TryDemandOrOverrideAsync(PermissionCodes.PosVoidSale, PosLocalization.Current.Text("sales.kind.void")).ConfigureAwait(true)))
                {
                    SetStatus(PosLocalization.Current.Text("pos.status.voidPermissionDenied"), PosNoticeSeverity.Error);
                    return;
                }

                IsBusy = true;
                var result = await _service.CreateRefundAsync(req, UseReceipt42, _printerSettings.AutoPrint).ConfigureAwait(true);
                _operatorSession?.LogSecurityEvent(SecurityEventCodes.Refund, "originalSaleId=" + saleId + " refundCode=" + result.RefundSaleCode);
                ReceiptPreview = UseReceipt42 ? result.Receipt42 : result.Receipt32;
                SetStatus(PosLocalization.Current.Format("pos.status.returnCompleted", result.RefundSaleCode), PosNoticeSeverity.Success);
                await LoadRecentSalesAsync().ConfigureAwait(true);
                if (registerVm != null && registerVm.LoadCommand.CanExecute(null))
                    registerVm.LoadCommand.Execute(null);
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("pos.status.refundError", ex.Message), PosNoticeSeverity.Error);
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

        private async Task OpenSalesRegisterInternalAsync(bool isRefundScanMode)
        {
            try
            {
                var permissionService = _permissionService ??
                    throw new InvalidOperationException(
                        PosLocalization.Current.Text("common.userPermissionDenied"));
                permissionService.Demand(PermissionCodes.RegisterView, PosLocalization.Current.Text("sales.register.title"));
                var canViewAll = permissionService.Has(PermissionCodes.RegisterViewAll);
                var currentUserId = _operatorSession?.CurrentUser?.Id;
                System.Collections.Generic.IReadOnlyList<(int id, string displayName)> operators = null;
                if (_userRepo != null)
                {
                    var users = await _userRepo.ListAsync().ConfigureAwait(true);
                    operators = users.Select(u => (u.Id, u.DisplayName)).ToList();
                }
                var registerVm = new Dialogs.SalesRegisterViewModel(_service, UseReceipt42, permissionService, (saleId, regVm) =>
                {
                    _ = OpenRefundForSaleIdThenRefreshAsync(saleId, regVm);
                }, isRefundScanMode, operators, canViewAll: canViewAll, forceOperatorId: canViewAll ? null : currentUserId, overrideAuthService: _overrideAuthService);
                var dlg = new Dialogs.SalesRegisterDialog(registerVm)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                dlg.ShowDialog();
            }
            catch (InvalidOperationException ex)
            {
                SetStatus(ex.Message, PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sales register dialog failed (XAML/binding).");
                SetStatus(
                    isRefundScanMode
                        ? PosLocalization.Current.Text("pos.status.refundOpenError")
                        : PosLocalization.Current.Text("pos.status.salesRegisterOpenError"),
                    PosNoticeSeverity.Error);
                ModernMessageDialog.Show(
                    DialogOwnerHelper.GetSafeOwner(),
                    isRefundScanMode ? PosLocalization.Current.Text("refund.title") : PosLocalization.Current.Text("sales.register.title"),
                    PosLocalization.Current.Format("common.errorWithMessage", ex.Message));
            }
            RequestFocusBarcode();
        }

        /// <summary>Crea il ViewModel per la pagina Utenti e ruoli (navigazione full-page). Richiede permesso UsersManage.</summary>
        public Dialogs.UserManagementViewModel CreateUserManagementViewModel()
        {
            _permissionService.Demand(PermissionCodes.UsersManage, PosLocalization.Current.Text("operations.usersRoles"));
            var vm = new Dialogs.UserManagementViewModel();
            vm.CanManageRoles = _permissionService.Has(PermissionCodes.RolesManage);
            if (_operatorSession != null && _operatorSession.IsLoggedIn)
            {
                vm.CurrentOperatorDisplay = PosLocalization.Current.Format(
                    "operator.switch.currentOperator",
                    _operatorSession.CurrentDisplayName,
                    _operatorSession.CurrentRoleName);
                vm.CurrentOperatorUsername = _operatorSession.CurrentUser?.Username ?? "";
            }
            else
            {
                vm.CurrentOperatorDisplay = PosLocalization.Current.Text("operator.switch.noCurrentOperator");
                vm.CurrentOperatorUsername = "";
            }
            return vm;
        }

        /// <summary>Fallback temporaneo: apre la dialog modale. Il flusso ufficiale è la pagina full-screen via menu (CreateUserManagementViewModel + navigazione tab).</summary>
        private async Task OpenUserManagementAsync()
        {
            try
            {
                var vm = CreateUserManagementViewModel();
                var dlg = new Dialogs.UserManagementDialog(vm)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                dlg.ShowDialog();
            }
            catch (InvalidOperationException ex)
            {
                SetStatus(ex.Message, PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User management dialog failed");
                SetStatus(PosLocalization.Current.Text("pos.status.usersOpenError"), PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("shell.usersRoles"), PosLocalization.Current.Format("common.errorWithMessage", ex.Message));
            }
            RequestFocusBarcode();
        }

        private async Task OpenShopSettingsAsync()
        {
            try
            {
                if (!(await TryDemandOrOverrideAsync(PermissionCodes.SettingsShop, PosLocalization.Current.Text("operations.shopSettings")).ConfigureAwait(true))) { RequestFocusBarcode(); return; }
                var vm = new Dialogs.ShopSettingsViewModel(_service);
                var dlg = new Dialogs.ShopSettingsDialog(vm)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                dlg.ShowDialog();
            }
            catch (InvalidOperationException ex)
            {
                SetStatus(ex.Message, PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shop settings dialog failed (XAML/binding).");
                SetStatus(PosLocalization.Current.Text("pos.status.shopSettingsOpenError"), PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("settings.shopTitle"), PosLocalization.Current.Format("common.errorWithMessage", ex.Message));
            }
            RequestFocusBarcode();
        }

        private async Task SuspendCartAsync()
        {
            try { _permissionService.Demand(PermissionCodes.PosSuspendCart, PosLocalization.Current.Text("operations.suspendCart")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); RequestFocusBarcode(); return; }
            IsBusy = true;
            try
            {
                var result = await _service.SuspendCartAsync().ConfigureAwait(true);
                if (result.Success)
                {
                    var snapshot = await _service.GetSnapshotAsync().ConfigureAwait(true);
                    ApplySnapshot(snapshot);
                    SetStatus(result.Message, PosNoticeSeverity.Success);
                }
                else
                {
                    SetStatus(result.Message, PosNoticeSeverity.Warning);
                }
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "Suspend cart failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RecoverCartAsync()
        {
            try { _permissionService.Demand(PermissionCodes.PosRecoverCart, PosLocalization.Current.Text("operations.recoverCart")); }
            catch (InvalidOperationException ex) { SetStatus(ex.Message, PosNoticeSeverity.Error); ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message); RequestFocusBarcode(); return; }
            var vm = new Dialogs.HeldCartsViewModel(_service, snapshot =>
            {
                ApplySnapshot(snapshot);
                SetStatus(snapshot?.Status ?? PosLocalization.Current.Text("pos.status.cartRecovered"), PosNoticeSeverity.Success);
            });
            var dlg = new Dialogs.HeldCartsDialog(vm) { Owner = DialogOwnerHelper.GetSafeOwner() };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);

            try
            {
                await vm.LoadAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recover cart LoadAsync failed");
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
            }

            dlg.ShowDialog();
            RequestFocusBarcode();
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
                if (!(await TryDemandOrOverrideAsync(PermissionCodes.CatalogEdit, PosLocalization.Current.Text("operations.editProduct")).ConfigureAwait(true))) return;
                var productsService = new ProductsWorkflowService();
                var product = await productsService.GetByBarcodeDetailsAsync(line.Barcode).ConfigureAwait(true);
                if (product == null)
                {
                    SetStatus(PosLocalization.Current.Text("pos.status.productNotFound"), PosNoticeSeverity.Warning);
                    return;
                }

                var ok = await ProductEditDialog.ShowAsync(ProductEditMode.Edit, product, productsService).ConfigureAwait(true);
                if (ok)
                {
                    await RefreshCartFromDatabaseAsync(PosLocalization.Current.Text("pos.status.cartUpdatedAfterProductEdit")).ConfigureAwait(true);
                }
            }
            catch (InvalidOperationException ex)
            {
                SetStatus(ex.Message, PosNoticeSeverity.Error);
                ModernMessageDialog.Show(DialogOwnerHelper.GetSafeOwner(), PosLocalization.Current.Text("common.userPermissionDenied"), ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS full edit product dialog failed.");
                SetStatus(PosLocalization.Current.Text("pos.status.productEditError"), PosNoticeSeverity.Error);
            }
            finally
            {
                RequestFocusBarcode();
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
                Owner = DialogOwnerHelper.GetSafeOwner()
            };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);
            if (dlg.ShowDialog() == true)
                _ = SetSelectedLineQtyAsync(dlg.Quantity);
            RequestFocusBarcode();
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
                SetStatus(
                    qty <= 0
                        ? PosLocalization.Current.Text("pos.status.lineRemoved")
                        : PosLocalization.Current.Format("pos.status.quantityUpdated", qty),
                    PosNoticeSeverity.Success);
                RequestFocusBarcode();
            }
            catch (Exception ex)
            {
                SetStatus(PosLocalization.Current.Format("common.errorWithMessage", ex.Message), PosNoticeSeverity.Error);
                _logger.LogError(ex, "POS VM set line qty failed");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async void OpenDiscount()
        {
            try
            {
                if (!(await TryDemandOrOverrideAsync(PermissionCodes.PosDiscount, PosLocalization.Current.Text("operations.discount")).ConfigureAwait(true))) return;
                var selectedBarcode = SelectedCartItem?.Barcode;
                var hasCart = CartItems.Count > 0;
                Dialogs.DiscountPreviewContext previewContext = null;
                if (SelectedCartItem != null && !IsDiscountLine(SelectedCartItem.Barcode))
                {
                    var line = SelectedCartItem;
                    var originalUnit = line.UnitPrice;
                    var currentFinal = (line.DiscountAmountMinor > 0 && line.Quantity > 0)
                        ? (line.LineTotal - line.DiscountAmountMinor) / line.Quantity
                        : originalUnit;
                    var currentPct = line.DiscountPercent > 0 ? (int?)line.DiscountPercent : null;
                    previewContext = new Dialogs.DiscountPreviewContext
                    {
                        Barcode = line.Barcode ?? string.Empty,
                        Name = line.Name ?? string.Empty,
                        Quantity = line.Quantity,
                        OriginalUnitPrice = originalUnit,
                        CurrentFinalUnitPrice = currentFinal,
                        CurrentDiscountPercent = currentPct
                    };
                }
                var maxDiscountPercent = _operatorSession?.CurrentUser?.MaxDiscountPercent ?? 0;
                var dlg = new Dialogs.DiscountDialog(
                    selectedBarcode ?? string.Empty,
                    hasCart,
                    _service,
                    this,
                    maxDiscountPercent,
                    () => TryDemandOrOverrideAsync(PermissionCodes.PosDiscountOverLimit, PosLocalization.Current.Text("operations.discountOverLimit")),
                    previewContext)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner()
                };
                WindowSizingHelper.CapMaxHeightToOwner(dlg);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discount dialog failed.");
                SetStatus(PosLocalization.Current.Text("pos.status.discountOpenError"), PosNoticeSeverity.Error);
            }
            finally
            {
                RequestFocusBarcode();
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

        public void SetStatus(
            string message,
            PosNoticeSeverity severity,
            bool suppressToast = false)
        {
            _statusMessage = message ?? string.Empty;
            OnPropertyChanged(nameof(StatusMessage));

            if (suppressToast || string.IsNullOrWhiteSpace(_statusMessage))
            {
                _statusToastTimer.Stop();
                StatusToastMessage = string.Empty;
                IsStatusToastVisible = false;
                return;
            }

            StatusToastSeverity = severity;
            StatusToastMessage = _statusMessage;
            IsStatusToastVisible = true;

            _statusToastTimer.Stop();
            var delay = PosNoticePolicy.GetAutoDismissDelay(severity);
            if (delay.HasValue)
            {
                _statusToastTimer.Interval = delay.Value;
                _statusToastTimer.Start();
            }
        }

        private void DismissStatusToast()
        {
            _statusToastTimer.Stop();
            IsStatusToastVisible = false;
            RequestFocusBarcode();
        }

        private static Brush NoticeBrush(
            PosNoticeSeverity severity,
            Color info,
            Color success,
            Color warning,
            Color error)
        {
            switch (severity)
            {
                case PosNoticeSeverity.Success: return new SolidColorBrush(success);
                case PosNoticeSeverity.Warning: return new SolidColorBrush(warning);
                case PosNoticeSeverity.Error: return new SolidColorBrush(error);
                default: return new SolidColorBrush(info);
            }
        }

        public void ApplyDiscountSnapshot(PosWorkflowSnapshot snapshot)
        {
            if (snapshot != null) ApplySnapshot(snapshot);
        }

        /// <summary>Applica lo snapshot. preferBarcode: riga da selezionare; preferIndex: indice da selezionare (es. dopo rimozione). Le righe sconto (DISC:*) non vengono mostrate: lo sconto è fuso nella riga prodotto.</summary>
        private void ApplySnapshot(PosWorkflowSnapshot snapshot, string preferBarcode = null, int? preferIndex = null)
        {
            _paymentReceiptDraftLines = CreatePaymentReceiptLines(snapshot.Lines);
            CartItems.Clear();
            foreach (var item in snapshot.Lines)
            {
                if (IsDiscountLine(item.Barcode ?? ""))
                    continue;
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
                SetStatus(snapshot.Status, PosNoticeSeverity.Info, suppressToast: true);
            OnPropertyChanged(nameof(ItemsCount));

            PosCartLineRow selected = null;
            if (!string.IsNullOrWhiteSpace(preferBarcode))
                selected = CartItems.LastOrDefault(x => string.Equals(x.Barcode, preferBarcode, StringComparison.OrdinalIgnoreCase));
            if (selected == null && preferIndex.HasValue && preferIndex.Value >= 0 && preferIndex.Value < CartItems.Count)
                selected = CartItems[preferIndex.Value];
            if (selected == null && CartItems.Count > 0)
                selected = CartItems[CartItems.Count - 1];
            SelectedCartItem = selected;

            PublishCustomerCartSnapshot(StableCustomerKey(selected));

            (ClearCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (OpenDiscountCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenEditProductCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChangeQuantityCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (OpenChangeQuantityForLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SuspendCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        internal static IReadOnlyList<PaymentReceiptDraftLine> CreatePaymentReceiptLines(
            IReadOnlyList<PosCartLine> lines)
        {
            return (lines ?? Array.Empty<PosCartLine>()).Select(item => new PaymentReceiptDraftLine
            {
                Barcode = item.Barcode,
                Name = item.Name,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = item.LineTotal
            }).ToList();
        }

        private void PublishCustomerCartSnapshot(string lastChangedLineKey = null)
        {
            var projectionLines = CartItems.Select(item =>
            {
                var netLineTotal = Math.Max(0, item.LineTotal - item.DiscountAmountMinor);
                return new CustomerDisplayProjectionLine
                {
                    StableKey = StableCustomerKey(item),
                    Name = item.Name ?? string.Empty,
                    Barcode = item.Barcode ?? string.Empty,
                    Quantity = item.Quantity,
                    UnitPrice = item.Quantity > 0 ? netLineTotal / item.Quantity : item.UnitPrice,
                    LineTotal = netLineTotal,
                    LineKind = CustomerDisplayLineKind.Item
                };
            }).ToList();

            _lastCustomerCartSnapshot = CustomerDisplayProjection.Cart(
                projectionLines,
                Subtotal,
                Total,
                _customerDisplayShopName,
                lastChangedLineKey,
                true,
                DateTimeOffset.UtcNow);
            PublishCustomerSnapshot(_lastCustomerCartSnapshot);
        }

        private void PublishCustomerState(CustomerDisplayState state, string messageCode)
        {
            PublishCustomerSnapshot(CustomerDisplayProjection.WithState(
                _lastCustomerCartSnapshot,
                state,
                messageCode,
                timestamp: DateTimeOffset.UtcNow));
        }

        private void PublishCustomerSnapshot(CustomerDisplaySnapshot snapshot)
        {
            CurrentCustomerDisplaySnapshot = snapshot ?? CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
            CustomerDisplaySnapshotChanged?.Invoke(CurrentCustomerDisplaySnapshot);
        }

        private static string StableCustomerKey(PosCartLineRow row)
        {
            if (row == null) return string.Empty;
            var barcode = (row.Barcode ?? string.Empty).Trim();
            if (barcode.StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase))
                return "manual:" + (row.Name ?? string.Empty).Trim() + ":" + row.UnitPrice;
            return string.IsNullOrWhiteSpace(barcode)
                ? "item:" + (row.Name ?? string.Empty).Trim() + ":" + row.UnitPrice
                : "item:" + barcode;
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

        public void RaiseCanExecuteChanged()
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
            (OpenCashDrawerCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            (OpenUserManagementCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private void RequestFocusBarcode()
        {
            FocusBarcodeRequested?.Invoke();
        }

        private static bool IsDiscountLine(string barcode)
            => DiscountKeys.IsDiscount(barcode ?? "");

        public event PropertyChangedEventHandler PropertyChanged;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CatalogEvents.CatalogChanged -= OnCatalogChanged;
            PosLocalization.Current.LanguageChanged -= _languageChangedHandler;
            _statusToastTimer.Stop();
            _statusToastTimer.Tick -= _statusToastTickHandler;
            FocusBarcodeRequested = null;
            CustomerDisplaySnapshotChanged = null;
            PropertyChanged = null;
        }
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class PosCartLineRow : INotifyPropertyChanged
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

            public bool HasDiscount => (DiscountAmountMinor > 0 || DiscountPercent > 0) && !IsDiscountLine;
            public string UnitPriceDisplay => MoneyClp.Format(UnitPrice);
            public string LineTotalDisplay => MoneyClp.Format(LineTotal);
            public string OriginalUnitPriceDisplay => MoneyClp.Format(UnitPrice);
            public string OriginalLineTotalDisplay => MoneyClp.Format(LineTotal);
            public string DiscountedUnitPriceDisplay => HasDiscount && Quantity > 0 ? MoneyClp.Format((LineTotal - DiscountAmountMinor) / Quantity) : MoneyClp.Format(UnitPrice);
            public string DiscountedLineTotalDisplay => HasDiscount ? MoneyClp.Format(LineTotal - DiscountAmountMinor) : MoneyClp.Format(LineTotal);
            public string DiscountPercentDisplay => HasDiscount && DiscountPercent > 0 ? "-" + DiscountPercent + "%" : string.Empty;
            public string DiscountAmountDisplay => HasDiscount
                ? PosLocalization.Current.Format("pos.status.savings", MoneyClp.Format(DiscountAmountMinor))
                : string.Empty;

            /// <summary>Nome da mostrare in carrello/scontrino: per sconti aggiunge prefisso "— " per evidenza.</summary>
            public string DisplayName => IsDiscountLine ? "- " + (Name ?? PosLocalization.Current.Text("discount.title")) : (Name ?? "");
            public string StockDisplay => IsDiscountLine || (Barcode ?? "").StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase) ? "" : PosLocalization.Current.Format("pos.status.stock", StockQty);
            public bool IsDiscountLine => DiscountKeys.IsDiscount(Barcode ?? "");
            /// <summary>True se la riga è modificabile (no sconto, no manual).</summary>
            public bool IsEditable => !IsDiscountLine && (string.IsNullOrEmpty(Barcode) || !Barcode.StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase));

            public event PropertyChangedEventHandler PropertyChanged;

            public void RaiseLocalizedProperties()
            {
                OnPropertyChanged(nameof(DiscountAmountDisplay));
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(StockDisplay));
            }

            private void OnPropertyChanged([CallerMemberName] string name = null)
            {
                var handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(name));
                }
            }
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
                _logger = logger ?? new FileLogger("PosViewModel");
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
                _logger = logger ?? new FileLogger("PosViewModel");
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
