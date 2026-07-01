using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Printing;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class PrinterSettingsViewModel : INotifyPropertyChanged
    {
        private const string PrinterKickMode = "printer_kick";
        private const string DisabledMode = "disabled";

        private string _printerName = string.Empty;
        private string _copies = "1";
        private bool _receiptEnabled;
        private bool _autoPrint;
        private bool _allowWindowsDefault;
        private bool _allowVirtualPrinters;
        private bool _saveCopyToFile;
        private string _outputDirectory = string.Empty;
        private string _cashDrawerCommand = "27,112,0,25,250";
        private bool _cashDrawerEnabled;
        private string _cashDrawerMode = DisabledMode;
        private string _cashDrawerPrinterName = string.Empty;
        private bool _cashDrawerOpenOnCashSale = true;

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
            set { _receiptEnabled = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public bool AutoPrint
        {
            get => _autoPrint;
            set { _autoPrint = value; OnPropertyChanged(); }
        }

        public bool AllowWindowsDefault
        {
            get => _allowWindowsDefault;
            set { _allowWindowsDefault = value; OnPropertyChanged(); }
        }

        public bool AllowVirtualPrinters
        {
            get => _allowVirtualPrinters;
            set { _allowVirtualPrinters = value; OnPropertyChanged(); }
        }

        public bool SaveCopyToFile
        {
            get => _saveCopyToFile;
            set { _saveCopyToFile = value; OnPropertyChanged(); }
        }

        public string OutputDirectory
        {
            get => _outputDirectory;
            set { _outputDirectory = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string CashDrawerCommand
        {
            get => _cashDrawerCommand;
            set
            {
                _cashDrawerCommand = value ?? string.Empty;
                OnPropertyChanged();
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
                var info = FindPrinter(PrinterName);
                if (info == null)
                    return PosLocalization.T("printer.noPosPrinterConfigured");
                return info.StatusText + (info.IsVirtual ? " - " + PosLocalization.T("printer.virtualNotRecommended") : string.Empty);
            }
        }

        public bool IsValid => ParsedCopies >= 1;
        public bool CanTestCashDrawer => CashDrawerEnabled && !string.IsNullOrWhiteSpace(CashDrawerCommand);

        public string TestCashDrawerStatusMessage => CanTestCashDrawer
            ? PosLocalization.T("printer.testReady")
            : PosLocalization.T("printer.testMissing");

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
        public event Action<string, string> TestCashDrawerRequested;
        public event Action TestPrintRequested;
        public event Action RefreshPrintersRequested;
        public event PropertyChangedEventHandler PropertyChanged;

        public PrinterSettingsViewModel()
        {
            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
            TestCashDrawerCommand = new RelayCommand(_ =>
            {
                var name = string.IsNullOrWhiteSpace(CashDrawerPrinterName) ? PrinterName : CashDrawerPrinterName;
                var cmd = string.IsNullOrWhiteSpace(CashDrawerCommand) ? "27,112,0,25,250" : CashDrawerCommand.Trim();
                TestCashDrawerRequested?.Invoke(name, cmd);
            }, _ => CanTestCashDrawer);
            TestPrintCommand = new RelayCommand(_ => TestPrintRequested?.Invoke(), _ => ReceiptEnabled);
            RefreshPrintersCommand = new RelayCommand(_ => RefreshPrintersRequested?.Invoke(), _ => true);
            PosLocalization.Current.LanguageChanged += OnLanguageChanged;
        }

        public void ReplaceInstalledPrinters(IEnumerable<InstalledPrinterInfo> printers)
        {
            InstalledPrinters.Clear();
            foreach (var printer in printers ?? Enumerable.Empty<InstalledPrinterInfo>())
            {
                InstalledPrinters.Add(printer);
            }

            OnPropertyChanged(nameof(SelectedPrinterSummary));
        }

        private InstalledPrinterInfo FindPrinter(string name)
        {
            var value = (name ?? string.Empty).Trim();
            if (value.Length == 0) return null;
            return InstalledPrinters.FirstOrDefault(x => string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase));
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(TestCashDrawerStatusMessage));
            OnPropertyChanged(nameof(SelectedPrinterSummary));
        }

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestCashDrawerCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TestPrintCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
