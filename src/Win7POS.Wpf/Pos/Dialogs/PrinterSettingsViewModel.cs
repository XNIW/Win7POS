using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class PrinterSettingsViewModel : INotifyPropertyChanged
    {
        private string _printerName = string.Empty;
        private string _copies = "1";
        private bool _autoPrint = true;
        private bool _saveCopyToFile;
        private string _outputDirectory = string.Empty;

        public string PrinterName
        {
            get => _printerName;
            set { _printerName = value ?? string.Empty; OnPropertyChanged(); }
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

        public bool AutoPrint
        {
            get => _autoPrint;
            set { _autoPrint = value; OnPropertyChanged(); }
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

        public bool IsValid => ParsedCopies >= 1;

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

        public event Action<bool> RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        public PrinterSettingsViewModel()
        {
            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
        }

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
