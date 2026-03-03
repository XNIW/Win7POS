using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Win7POS.Core.Util;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class EditProductViewModel : INotifyPropertyChanged
    {
        private string _barcode = string.Empty;
        private string _productName = string.Empty;
        private string _priceText = "0";

        public EditProductViewModel(string barcode, string productName, long unitPriceMinor)
        {
            Barcode = barcode ?? string.Empty;
            ProductName = (productName ?? string.Empty).Trim();
            PriceText = unitPriceMinor > 0 ? MoneyClp.Format(unitPriceMinor) : "0";
            ConfirmCommand = new RelayCommand(_ => Confirm(), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
        }

        private void Confirm()
        {
            RequestClose?.Invoke(true);
        }

        public string Barcode
        {
            get => _barcode;
            set { _barcode = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string ProductName
        {
            get => _productName;
            set { _productName = value ?? string.Empty; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public string PriceText
        {
            get => _priceText;
            set
            {
                _priceText = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public long PriceMinor => MoneyClp.Parse(PriceText);
        public bool IsValid => PriceMinor > 0;

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        public event Action<bool> RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
