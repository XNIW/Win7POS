using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
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

        public ObservableCollection<InstalledPrinterInfo> InstalledPrinters { get; } =
            new ObservableCollection<InstalledPrinterInfo>();

        public string PrinterName
        {
            get => _printerName;
            set
            {
                _printerName = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedPrinterSummary));
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
                OnPropertyChanged(nameof(CanTestPrint));
                OnPropertyChanged(nameof(TestPrintStatusMessage));
                RaiseCanExecuteChanged();
            }
        }

        public bool AutoPrint
        {
            get => _autoPrint;
            set { _autoPrint = value; OnPropertyChanged(); }
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
                OnPropertyChanged();
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
                OnPropertyChanged();
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
                return info.StatusText + (info.IsVirtual ? " - " + PosLocalization.T("printer.virtualNotRecommended") : string.Empty);
            }
        }

        public bool IsCashDrawerCommandValid =>
            !CashDrawerEnabled ||
            (!string.IsNullOrWhiteSpace(CashDrawerCommand) &&
             WindowsSpoolerReceiptPrinter.IsCashDrawerCommandValid(CashDrawerCommand));
        public bool IsValid => ParsedCopies >= 1 && IsCashDrawerCommandValid;
        public bool IsTestOperationInProgress => _isTestOperationInProgress;
        public Task ActiveTestOperation => _activeTestOperation;
        public Task ActiveRefreshOperation => _activeRefreshOperation;

        public bool CanTestPrint =>
            !_disposed &&
            !IsTestOperationInProgress &&
            ReceiptEnabled &&
            IsUsableQueue(ResolveReceiptPrinter(), AllowVirtualPrinters);
        public bool CanTestCashDrawer =>
            !_disposed &&
            !IsTestOperationInProgress &&
            CashDrawerEnabled &&
            IsCashDrawerCommandValid &&
            IsUsableQueue(ResolveCashDrawerPrinter(), allowVirtualPrinter: false);

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
                if (!int.TryParse(Copies, out var value)) return 0;
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

        private static bool IsUsableQueue(InstalledPrinterInfo printer, bool allowVirtualPrinter)
        {
            return printer != null &&
                   !string.IsNullOrWhiteSpace(printer.Name) &&
                   printer.IsAvailable &&
                   !printer.IsOffline &&
                   !printer.IsPaused &&
                   (allowVirtualPrinter || !printer.IsVirtual);
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
            OnPropertyChanged(nameof(TestPrintStatusMessage));
            OnPropertyChanged(nameof(SelectedPrinterSummary));
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
        }

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestCashDrawerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestPrintCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshPrintersCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
