using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class AddProductViewModel : INotifyPropertyChanged
    {
        private readonly CultureInfo _it = CultureInfo.GetCultureInfo("it-IT");
        private string _productName = string.Empty;
        private string _priceText = "0";

        public AddProductViewModel(string barcode)
        {
            Barcode = (barcode ?? string.Empty).Trim();
            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
        }

        public string Barcode { get; }

        public string ProductName
        {
            get => _productName;
            set
            {
                _productName = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
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

        public int PriceMinor => ParseMoneyToMinor(PriceText);

        public bool IsValid => Barcode.Length > 0 && ProductName.Trim().Length > 0 && PriceMinor >= 0;

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        public event Action<bool> RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private int ParseMoneyToMinor(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;
            var raw = text.Trim();

            decimal value;
            if (!decimal.TryParse(raw, NumberStyles.Number, _it, out value))
            {
                var normalized = raw.Replace(',', '.');
                if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
                    return -1;
            }

            if (value < 0) return -1;
            return (int)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
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
    }
}
