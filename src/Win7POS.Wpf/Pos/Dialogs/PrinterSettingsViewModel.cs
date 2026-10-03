using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Hardware;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Printing;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class PrinterSettingsViewModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly FileLogger _logger = new FileLogger("PrinterSettingsViewModel");
        private const string PrinterKickMode = "printer_kick";
        private const string DisabledMode = "disabled";

        private string _printerName = string.Empty;
        private string _copies = "1";
        private bool _receiptEnabled;
        private bool _autoPrint;
        private bool _allowWindowsDefault;
        private bool _allowVirtualPrinters;
        private string _testReceiptPreview = string.Empty;
        private string _testReceiptPreviewFirstLine = string.Empty;
        private string _testReceiptPreviewRest = string.Empty;
        private string _cashDrawerCommand = "27,112,0,25,250";
        private bool _cashDrawerEnabled;
        private string _cashDrawerMode = DisabledMode;
        private string _cashDrawerPrinterName = string.Empty;
        private bool _cashDrawerOpenOnCashSale = true;
        private bool _isTestOperationInProgress;
        private bool _isRefreshingPrinters;
        private Task _activeTestOperation = Task.CompletedTask;
        private Task _activeRefreshOperation = Task.CompletedTask;
        private bool _disposed;
        private ReceiptProfile _receiptProfile = ReceiptProfile.Thermal80mm42col;
        private CashDrawerPreset _drawerPreset = CashDrawerPreset.EscposPin2;
        private ScannerTerminator _scannerTerminator;
        private string _scannerPrefix = string.Empty, _scannerSuffix = string.Empty, _scannerMinimum = "1", _scannerMaximum = "128";
        private bool _scannerTrim = true, _scannerTestArmed, _diagnosticInProgress;
        private string _scannerNormalized = string.Empty, _scannerTestDetails = string.Empty, _diagnosticDetails = string.Empty;
        private DateTimeOffset? _scannerPassedAt, _drawerPassedAt;
        private bool _drawerCommandSent;
        private bool _drawerTestFailed;

        public sealed class HardwareOption<T>
        {
            public T Value { get; }
            public string Label { get; }
            public HardwareOption(T value, string key) { Value = value; Label = PosLocalization.T(key); }
        }
        public IReadOnlyList<HardwareOption<ReceiptProfile>> ReceiptProfiles => new[]
        {
            new HardwareOption<ReceiptProfile>(ReceiptProfile.Thermal58mm32col, "hardware.receipt58"),
            new HardwareOption<ReceiptProfile>(ReceiptProfile.Thermal80mm42col, "hardware.receipt80")
        };
        public IReadOnlyList<HardwareOption<ScannerTerminator>> ScannerTerminators => new[]
        {
            new HardwareOption<ScannerTerminator>(ScannerTerminator.Enter, "hardware.enter"), new HardwareOption<ScannerTerminator>(ScannerTerminator.Tab, "hardware.tab"),
            new HardwareOption<ScannerTerminator>(ScannerTerminator.EnterOrTab, "hardware.enterOrTab")
        };
        public IReadOnlyList<HardwareOption<CashDrawerPreset>> DrawerPresets => new[]
        {
            new HardwareOption<CashDrawerPreset>(CashDrawerPreset.EscposPin2, "hardware.pin2"), new HardwareOption<CashDrawerPreset>(CashDrawerPreset.EscposPin5, "hardware.pin5"),
            new HardwareOption<CashDrawerPreset>(CashDrawerPreset.Custom, "hardware.custom")
        };
        public ReceiptProfile ReceiptProfile
        {
            get => _receiptProfile;
            set { _receiptProfile = value; OnPropertyChanged(); ReceiptProfileChanged?.Invoke(); }
        }
        public CashDrawerPreset DrawerPreset
        {
            get => _drawerPreset;
            set { _drawerPreset = value; if (value != CashDrawerPreset.Custom) CashDrawerCommand = CashDrawerPresetPolicy.Command(value, null); OnPropertyChanged(); OnPropertyChanged(nameof(IsCustomDrawerPreset)); }
        }
        public bool IsCustomDrawerPreset => DrawerPreset == CashDrawerPreset.Custom;
        public ScannerTerminator ScannerTerminator { get => _scannerTerminator; set { _scannerTerminator = value; ScannerSettingsChanged(); } }
        public string ScannerPrefix { get => _scannerPrefix; set { _scannerPrefix = value ?? string.Empty; ScannerSettingsChanged(); } }
        public string ScannerSuffix { get => _scannerSuffix; set { _scannerSuffix = value ?? string.Empty; ScannerSettingsChanged(); } }
        public bool ScannerTrim { get => _scannerTrim; set { _scannerTrim = value; ScannerSettingsChanged(); } }
        public string ScannerMinimum { get => _scannerMinimum; set { _scannerMinimum = value ?? string.Empty; ScannerSettingsChanged(); } }
        public string ScannerMaximum { get => _scannerMaximum; set { _scannerMaximum = value ?? string.Empty; ScannerSettingsChanged(); } }
        public ScannerInputSettings ScannerSettings => new ScannerInputSettings
        {
            Terminator = ScannerTerminator, Prefix = ScannerPrefix, Suffix = ScannerSuffix, TrimWhitespace = ScannerTrim,
            MinimumLength = int.TryParse(ScannerMinimum, NumberStyles.None, CultureInfo.InvariantCulture, out var min) ? min : 0,
            MaximumLength = int.TryParse(ScannerMaximum, NumberStyles.None, CultureInfo.InvariantCulture, out var max) ? max : 0
        };
        public bool ScannerTestArmed => _scannerTestArmed;
        public string ScannerNormalized => _scannerNormalized;
        public string ScannerTestDetails => _scannerTestDetails;
        public string ScannerHealth => _scannerPassedAt.HasValue ? PosLocalization.Current.Format("hardware.testPassedAt", _scannerPassedAt.Value.ToLocalTime()) : PosLocalization.T("hardware.unknownNeedsTest");
        public string DrawerHealth => !CashDrawerEnabled ? PosLocalization.T("hardware.disabled") : _drawerTestFailed ? PosLocalization.T("hardware.testFailed") : _drawerPassedAt.HasValue ? PosLocalization.Current.Format("hardware.testPassedAt", _drawerPassedAt.Value.ToLocalTime()) : PosLocalization.T("hardware.configuredNeedsTest");
        public string PrinterHealth
        {
            get
            {
                if (!ReceiptEnabled) return PosLocalization.T("hardware.disabled");
                var queue = ResolveReceiptPrinter();
                if (queue == null) return PosLocalization.T("hardware.warningQueue");
                if (!queue.IsInventoryFresh || queue.OutputKind == PrinterOutputKind.Unknown) return PosLocalization.T("hardware.unknownNeedsTest");
                if (!queue.IsAvailable || queue.IsOffline || queue.IsPaused || queue.IsVirtual) return PosLocalization.T("hardware.warningQueue");
                return PosLocalization.T("hardware.configuredNeedsTest");
            }
        }
        public string DiagnosticDetails => _diagnosticDetails;
        public ICommand ArmScannerTestCommand { get; }
        public ICommand QueueDiagnosticsCommand { get; }
        public ICommand ConfirmDrawerTestCommand { get; }
        public event Action FocusScannerTestRequested;
        public event Action ReceiptProfileChanged;

        public void LoadScannerSettings(ScannerInputSettings value)
        {
            value = value ?? new ScannerInputSettings();
            ScannerTerminator = value.Terminator; ScannerPrefix = value.Prefix; ScannerSuffix = value.Suffix; ScannerTrim = value.TrimWhitespace;
            ScannerMinimum = value.MinimumLength.ToString(CultureInfo.InvariantCulture); ScannerMaximum = value.MaximumLength.ToString(CultureInfo.InvariantCulture);
        }
        private void ScannerSettingsChanged([CallerMemberName] string property = null)
        {
            _scannerPassedAt = null; _scannerTestArmed = false; _scannerNormalized = string.Empty; _scannerTestDetails = string.Empty;
            OnPropertyChanged(property); OnPropertyChanged(nameof(IsValid)); OnPropertyChanged(nameof(ScannerTestArmed));
            OnPropertyChanged(nameof(ScannerHealth)); OnPropertyChanged(nameof(ScannerNormalized)); OnPropertyChanged(nameof(ScannerTestDetails));
            RaiseCanExecuteChanged();
        }
        public void CompleteScannerTest(string raw, ScannerTerminator key, long elapsedMilliseconds)
        {
            if (!_scannerTestArmed || !ScannerInputPolicy.Accepts(ScannerSettings, key)) return;
            _scannerTestArmed = false;
            var valid = ScannerInputPolicy.TryNormalize(raw, ScannerSettings, out _scannerNormalized, out var error);
            _scannerPassedAt = valid ? DateTimeOffset.Now : (DateTimeOffset?)null;
            _scannerTestDetails = valid ? PosLocalization.Current.Format("hardware.scannerResult", key == ScannerTerminator.Enter ? PosLocalization.T("hardware.enter") : PosLocalization.T("hardware.tab"), elapsedMilliseconds) : PosLocalization.T(error);
            OnPropertyChanged(nameof(ScannerTestArmed)); OnPropertyChanged(nameof(ScannerNormalized)); OnPropertyChanged(nameof(ScannerTestDetails)); OnPropertyChanged(nameof(ScannerHealth));
        }
        public void RecordDrawerCommandSent(bool sent)
        {
            _drawerCommandSent = sent; _drawerPassedAt = null; _drawerTestFailed = !sent;
            OnPropertyChanged(nameof(DrawerHealth)); (ConfirmDrawerTestCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        private async Task ReadQueueDiagnosticsAsync()
        {
            _diagnosticInProgress = true; RaiseCanExecuteChanged();
            try
            {
                var result = await WindowsPrinterDiagnostics.ReadAsync(ResolveReceiptPrinter()?.Name ?? PrinterName).ConfigureAwait(true);
                if (_disposed) return;
                var queue = result.Value;
                _diagnosticDetails = result.Completed && queue?.Available == true
                    ? PosLocalization.Current.Format("hardware.queueDetails", queue.QueueName, queue.DriverName, queue.PortName, queue.Status.ToString("X8", CultureInfo.InvariantCulture), queue.JobCount, queue.JobsAvailable ? PosLocalization.T("hardware.jobsRead") : PosLocalization.T("hardware.jobsUnknown"))
                    : PosLocalization.T("hardware.diagnostic." + (result.Completed ? "unavailable" : result.ResultCode));
                OnPropertyChanged(nameof(DiagnosticDetails));
            }
            finally { _diagnosticInProgress = false; if (!_disposed) RaiseCanExecuteChanged(); }
        }

        public ObservableCollection<InstalledPrinterInfo> InstalledPrinters { get; } =
            new ObservableCollection<InstalledPrinterInfo>();

        public string PrinterName
        {
            get => _printerName;
            set
            {
                var next = value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(CashDrawerPrinterName) &&
                    !string.Equals(_printerName.Trim(), next.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    _drawerPassedAt = null; _drawerCommandSent = false; _drawerTestFailed = false;
                    OnPropertyChanged(nameof(DrawerHealth));
                    (ConfirmDrawerTestCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
                _printerName = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedPrinterSummary));
                OnPropertyChanged(nameof(PrinterHealth));
                OnPropertyChanged(nameof(CanTestPrint));
                OnPropertyChanged(nameof(CanTestCashDrawer));
                OnPropertyChanged(nameof(TestPrintStatusMessage));
                OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public string Copies
        {
            get => _copies;
            set
            {
                _copies = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public bool ReceiptEnabled
        {
            get => _receiptEnabled;
            set
            {
                _receiptEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
                OnPropertyChanged(nameof(PrinterHealth));
                OnPropertyChanged(nameof(CanTestPrint));
                OnPropertyChanged(nameof(TestPrintStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public bool AutoPrint
        {
            get => _autoPrint;
            set { _autoPrint = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); RaiseCanExecuteChanged(); }
        }

        public bool AllowWindowsDefault
        {
            get => _allowWindowsDefault;
            set
            {
                _allowWindowsDefault = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedPrinterSummary));
                OnPropertyChanged(nameof(CanTestPrint));
                OnPropertyChanged(nameof(TestPrintStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public bool AllowVirtualPrinters
        {
            get => _allowVirtualPrinters;
            set
            {
                _allowVirtualPrinters = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedPrinterSummary));
                OnPropertyChanged(nameof(CanTestPrint));
                OnPropertyChanged(nameof(TestPrintStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public string TestReceiptPreview
        {
            get => _testReceiptPreview;
            set
            {
                _testReceiptPreview = value ?? string.Empty;
                PosReceiptTextRenderer.SplitPreview(
                    _testReceiptPreview,
                    out _testReceiptPreviewFirstLine,
                    out _testReceiptPreviewRest);
                OnPropertyChanged();
                OnPropertyChanged(nameof(TestReceiptPreviewFirstLine));
                OnPropertyChanged(nameof(TestReceiptPreviewRest));
            }
        }

        public string TestReceiptPreviewFirstLine => _testReceiptPreviewFirstLine;
        public string TestReceiptPreviewRest => _testReceiptPreviewRest;

        public string CashDrawerCommand
        {
            get => _cashDrawerCommand;
            set
            {
                _cashDrawerCommand = value ?? string.Empty;
                _drawerPassedAt = null; _drawerCommandSent = false; _drawerTestFailed = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DrawerHealth));
                OnPropertyChanged(nameof(IsCashDrawerCommandValid));
                OnPropertyChanged(nameof(IsValid));
                OnPropertyChanged(nameof(CanTestCashDrawer));
                OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public bool CashDrawerEnabled
        {
            get => _cashDrawerEnabled;
            set
            {
                _cashDrawerEnabled = value;
                CashDrawerMode = value ? PrinterKickMode : DisabledMode;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCashDrawerCommandValid));
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public string CashDrawerMode
        {
            get => _cashDrawerMode;
            set
            {
                _cashDrawerMode = string.Equals(value, PrinterKickMode, StringComparison.OrdinalIgnoreCase)
                    ? PrinterKickMode
                    : DisabledMode;
                _cashDrawerEnabled = string.Equals(_cashDrawerMode, PrinterKickMode, StringComparison.OrdinalIgnoreCase);
                OnPropertyChanged();
                OnPropertyChanged(nameof(CashDrawerEnabled));
                _drawerPassedAt = null; _drawerCommandSent = false; _drawerTestFailed = false; OnPropertyChanged(nameof(DrawerHealth));
                OnPropertyChanged(nameof(IsCashDrawerCommandValid));
                OnPropertyChanged(nameof(IsValid));
                OnPropertyChanged(nameof(CanTestCashDrawer));
                OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public string CashDrawerPrinterName
        {
            get => _cashDrawerPrinterName;
            set
            {
                _cashDrawerPrinterName = value ?? string.Empty;
                _drawerPassedAt = null; _drawerCommandSent = false; _drawerTestFailed = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DrawerHealth));
                OnPropertyChanged(nameof(CanTestCashDrawer));
                OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public bool CashDrawerOpenOnCashSale
        {
            get => _cashDrawerOpenOnCashSale;
            set { _cashDrawerOpenOnCashSale = value; OnPropertyChanged(); }
        }

        public string SelectedPrinterSummary
        {
            get
            {
                var info = ResolveReceiptPrinter();
                if (info == null)
                    return PosLocalization.T("printer.noPosPrinterConfigured");
                return info.StatusText + (!info.IsPhysical ? " - " + PosLocalization.T("printer.virtualNotRecommended") : string.Empty);
            }
        }

        public bool IsCashDrawerCommandValid =>
            !CashDrawerEnabled ||
            (!string.IsNullOrWhiteSpace(CashDrawerCommand) &&
             WindowsSpoolerReceiptPrinter.IsCashDrawerCommandValid(CashDrawerCommand));
        public bool IsValid => ParsedCopies >= 1 &&
                               ParsedCopies <= ReceiptPrintOptions.MaximumCopies &&
                               IsCashDrawerCommandValid && ScannerInputPolicy.Validate(ScannerSettings).Count == 0 &&
                               (!AutoPrint || ReceiptEnabled);
        public bool IsTestOperationInProgress => _isTestOperationInProgress;
        public Task ActiveTestOperation => _activeTestOperation;
        public Task ActiveRefreshOperation => _activeRefreshOperation;

        public bool CanTestPrint =>
            !_disposed &&
            !IsTestOperationInProgress &&
            ReceiptEnabled &&
            IsUsableQueue(
                ResolveReceiptPrinter(),
                allowVirtualPrinter: AllowVirtualPrinters,
                requirePhysicalOutput: false);
        public bool CanTestCashDrawer =>
            !_disposed &&
            !IsTestOperationInProgress &&
            CashDrawerEnabled &&
            IsCashDrawerCommandValid &&
            IsUsableQueue(
                ResolveCashDrawerPrinter(),
                allowVirtualPrinter: false,
                requirePhysicalOutput: true);

        public string TestPrintStatusMessage => CanTestPrint
            ? PosLocalization.T("printer.testPrintReady")
            : PosLocalization.T("printer.testPrintUnavailable");

        public string TestCashDrawerStatusMessage =>
            !CashDrawerEnabled || string.IsNullOrWhiteSpace(CashDrawerCommand)
                ? PosLocalization.T("printer.testMissing")
                : !IsCashDrawerCommandValid
                    ? PosLocalization.T("printer.testInvalidCommand")
                    : CanTestCashDrawer
                        ? PosLocalization.T("printer.testReady")
                        : PosLocalization.T("printer.testQueueUnavailable");

        public int ParsedCopies
        {
            get
            {
                if (!int.TryParse(Copies, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return 0;
                return value;
            }
        }

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand TestCashDrawerCommand { get; }
        public ICommand TestPrintCommand { get; }
        public ICommand RefreshPrintersCommand { get; }

        public event Action<bool> RequestClose;
        public event Func<string, string, Task> TestCashDrawerRequested;
        public event Func<Task> TestPrintRequested;
        public event Func<Task> RefreshPrintersRequested;
        public event PropertyChangedEventHandler PropertyChanged;

        public PrinterSettingsViewModel()
        {
            ArmScannerTestCommand = new RelayCommand(_ =>
            {
                _scannerTestArmed = true; _scannerNormalized = string.Empty; _scannerTestDetails = string.Empty;
                OnPropertyChanged(nameof(ScannerTestArmed)); OnPropertyChanged(nameof(ScannerNormalized)); OnPropertyChanged(nameof(ScannerTestDetails));
                FocusScannerTestRequested?.Invoke();
            }, _ => !_disposed && ScannerInputPolicy.Validate(ScannerSettings).Count == 0);
            QueueDiagnosticsCommand = new RelayCommand(_ => { _ = ReadQueueDiagnosticsAsync(); }, _ => !_disposed && !_diagnosticInProgress);
            ConfirmDrawerTestCommand = new RelayCommand(_ =>
            {
                _drawerPassedAt = DateTimeOffset.Now; _drawerCommandSent = false; _drawerTestFailed = false; OnPropertyChanged(nameof(DrawerHealth));
                (ConfirmDrawerTestCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }, _ => !_disposed && _drawerCommandSent);
            ConfirmCommand = new RelayCommand(
                _ => RequestClose?.Invoke(true),
                _ => IsValid && !IsTestOperationInProgress && !_isRefreshingPrinters);
            CancelCommand = new RelayCommand(
                _ => RequestClose?.Invoke(false),
                _ => !IsTestOperationInProgress && !_isRefreshingPrinters);
            TestCashDrawerCommand = new RelayCommand(_ => StartCashDrawerTest(), _ => CanTestCashDrawer);
            TestPrintCommand = new RelayCommand(_ => StartPrintTest(), _ => CanTestPrint);
            RefreshPrintersCommand = new RelayCommand(
                _ => StartPrinterRefresh(),
                _ => !_disposed && !_isRefreshingPrinters);
            PosLocalization.Current.LanguageChanged += OnLanguageChanged;
        }

        private void StartPrintTest()
        {
            if (!CanTestPrint) return;
            _activeTestOperation = RunTestOperationAsync(() => InvokeAsync(TestPrintRequested));
        }

        private void StartCashDrawerTest()
        {
            if (!CanTestCashDrawer) return;
            var name = ResolveCashDrawerPrinter()?.Name ?? string.Empty;
            var command = CashDrawerCommand.Trim();
            _activeTestOperation = RunTestOperationAsync(() => InvokeAsync(TestCashDrawerRequested, name, command));
        }

        private async Task RunTestOperationAsync(Func<Task> operation)
        {
            SetTestOperationInProgress(true);
            try
            {
                await operation().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Printer settings test operation failed");
            }
            finally
            {
                SetTestOperationInProgress(false);
            }
        }

        private void StartPrinterRefresh()
        {
            if (_disposed || _isRefreshingPrinters) return;
            _activeRefreshOperation = RunPrinterRefreshAsync();
        }

        private async Task RunPrinterRefreshAsync()
        {
            SetRefreshingPrinters(true);
            try
            {
                await InvokeAsync(RefreshPrintersRequested).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Printer settings refresh failed");
            }
            finally
            {
                SetRefreshingPrinters(false);
            }
        }

        private static async Task InvokeAsync(Func<Task> handlers)
        {
            if (handlers == null) return;
            foreach (Func<Task> handler in handlers.GetInvocationList())
            {
                var task = handler();
                if (task != null)
                    await task.ConfigureAwait(true);
            }
        }

        private static async Task InvokeAsync(
            Func<string, string, Task> handlers,
            string printerName,
            string command)
        {
            if (handlers == null) return;
            foreach (Func<string, string, Task> handler in handlers.GetInvocationList())
            {
                var task = handler(printerName, command);
                if (task != null)
                    await task.ConfigureAwait(true);
            }
        }

        private void SetTestOperationInProgress(bool value)
        {
            if (_isTestOperationInProgress == value) return;
            _isTestOperationInProgress = value;
            if (_disposed) return;
            OnPropertyChanged(nameof(IsTestOperationInProgress));
            OnPropertyChanged(nameof(CanTestPrint));
            OnPropertyChanged(nameof(CanTestCashDrawer));
            OnPropertyChanged(nameof(TestPrintStatusMessage));
            OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
            RaiseCanExecuteChanged();
        }

        private void SetRefreshingPrinters(bool value)
        {
            if (_isRefreshingPrinters == value) return;
            _isRefreshingPrinters = value;
            if (_disposed) return;
            RaiseCanExecuteChanged();
        }

        public void ReplaceInstalledPrinters(IEnumerable<InstalledPrinterInfo> printers)
        {
            if (_disposed) return;
            InstalledPrinters.Clear();
            foreach (var printer in printers ?? Enumerable.Empty<InstalledPrinterInfo>())
            {
                InstalledPrinters.Add(printer);
            }

            OnPropertyChanged(nameof(SelectedPrinterSummary));
            OnPropertyChanged(nameof(PrinterHealth));
            OnPropertyChanged(nameof(CanTestPrint));
            OnPropertyChanged(nameof(CanTestCashDrawer));
            OnPropertyChanged(nameof(TestPrintStatusMessage));
            OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
            RaiseCanExecuteChanged();
        }

        private InstalledPrinterInfo FindPrinter(string name)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.Length == 0) return null;
            return InstalledPrinters.FirstOrDefault(x => string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase));
        }

        private InstalledPrinterInfo ResolveReceiptPrinter()
        {
            var selected = FindPrinter(PrinterName);
            if (selected != null || !string.IsNullOrWhiteSpace(PrinterName) || !AllowWindowsDefault)
                return selected;

            return InstalledPrinters.FirstOrDefault(x => x.IsDefault);
        }

        private InstalledPrinterInfo ResolveCashDrawerPrinter()
        {
            var name = string.IsNullOrWhiteSpace(CashDrawerPrinterName)
                ? PrinterName
                : CashDrawerPrinterName;
            return FindPrinter(name);
        }

        private static bool IsUsableQueue(
            InstalledPrinterInfo printer,
            bool allowVirtualPrinter,
            bool requirePhysicalOutput)
        {
            return printer != null &&
                   !string.IsNullOrWhiteSpace(printer.Name) &&
                   printer.IsInventoryFresh &&
                   printer.IsAvailable &&
                   !printer.IsOffline &&
                   !printer.IsPaused &&
                   (printer.IsPhysical || (!requirePhysicalOutput && allowVirtualPrinter));
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
            OnPropertyChanged(nameof(TestPrintStatusMessage));
            OnPropertyChanged(nameof(SelectedPrinterSummary));
            OnPropertyChanged(nameof(PrinterHealth));
            OnPropertyChanged(nameof(ScannerHealth)); OnPropertyChanged(nameof(DrawerHealth));
            OnPropertyChanged(nameof(ReceiptProfiles)); OnPropertyChanged(nameof(ScannerTerminators)); OnPropertyChanged(nameof(DrawerPresets));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PosLocalization.Current.LanguageChanged -= OnLanguageChanged;
            RequestClose = null;
            TestCashDrawerRequested = null;
            TestPrintRequested = null;
            RefreshPrintersRequested = null;
            PropertyChanged = null;
            FocusScannerTestRequested = null;
            ReceiptProfileChanged = null;
            _scannerNormalized = string.Empty;
        }

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestCashDrawerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestPrintCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshPrintersCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ArmScannerTestCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (QueueDiagnosticsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
    }
}
