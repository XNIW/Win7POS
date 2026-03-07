using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Util;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum DiscountMode { Percent, Amount }

    public sealed class DiscountViewModel : INotifyPropertyChanged
    {
        private string _valueText = "0";
        private DiscountMode _mode = DiscountMode.Percent;
        private bool _isBusy;
        private bool _applyToWholeCart;

        private readonly string _selectedLineBarcode;
        private readonly bool _hasCartItems;
        private readonly Func<int, bool, string, Task> _onApplyAsync; // value, isPercent, lineBarcodeOrNull

        public DiscountViewModel(string selectedLineBarcode, bool hasCartItems, Func<int, bool, string, Task> onApplyAsync)
        {
            _selectedLineBarcode = selectedLineBarcode?.Trim();
            _hasCartItems = hasCartItems;
            _onApplyAsync = onApplyAsync ?? throw new ArgumentNullException(nameof(onApplyAsync));

            DigitCommand = new RelayCommandParam(Digit);
            BackspaceCommand = new RelayCommand(_ => Backspace(), _ => (ValueText ?? "").Length > 0);
            ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, _ => CanConfirm && !IsBusy);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public DiscountMode Mode
        {
            get => _mode;
            set { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPercentMode)); OnPropertyChanged(nameof(IsAmountMode)); OnPropertyChanged(nameof(ValueLabel)); OnPropertyChanged(nameof(ScopeText)); OnPropertyChanged(nameof(CanConfirm)); RaiseCanExecuteChanged(); }
        }

        public bool ApplyToWholeCart
        {
            get => _applyToWholeCart;
            set { _applyToWholeCart = value; OnPropertyChanged(); OnPropertyChanged(nameof(ScopeText)); OnPropertyChanged(nameof(CanConfirm)); RaiseCanExecuteChanged(); }
        }

        public string ScopeText => ApplyToWholeCart
            ? "Applicazione: intero carrello"
            : "Applicazione: prodotto selezionato";

        public bool IsPercentMode
        {
            get => Mode == DiscountMode.Percent;
            set { if (value) Mode = DiscountMode.Percent; }
        }
        public bool IsAmountMode
        {
            get => Mode == DiscountMode.Amount;
            set { if (value) Mode = DiscountMode.Amount; }
        }

        public string ValueLabel => IsPercentMode ? "Sconto (%)" : "Sconto ($)";

        public string ValueText
        {
            get => _valueText ?? "0";
            set
            {
                _valueText = value ?? "0";
                OnPropertyChanged();
                OnPropertyChanged(nameof(ValueInt));
                OnPropertyChanged(nameof(CanConfirm));
                RaiseCanExecuteChanged();
            }
        }

        public int ValueInt
        {
            get
            {
                if (IsPercentMode)
                {
                    if (int.TryParse((ValueText ?? "").Trim().Replace(",", "").Replace(".", ""), out var n) && n >= 0 && n <= 100)
                        return n;
                    return 0;
                }
                return MoneyClp.Parse(ValueText);
            }
        }

        public bool HasLineSelected => !string.IsNullOrEmpty(_selectedLineBarcode);

        public bool CanConfirm =>
            (IsPercentMode && ValueInt >= 1 && ValueInt <= 100 && (
                (ApplyToWholeCart && _hasCartItems) ||
                (!ApplyToWholeCart && HasLineSelected))) ||
            (IsAmountMode && ValueInt > 0 && HasLineSelected);

        public ICommand DigitCommand { get; }
        public ICommand BackspaceCommand { get; }
        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        public event Action<bool> RequestClose;

        public event PropertyChangedEventHandler PropertyChanged;

        private void Digit(object digit)
        {
            var d = digit as string;
            if (string.IsNullOrEmpty(d) || d.Length != 1) return;
            var c = d[0];
            if (c >= '0' && c <= '9')
            {
                if (ValueText == "0" && c != '0') ValueText = d;
                else if (ValueText != "0") ValueText += d;
                else ValueText = d;
            }
            else if (c == '.' || c == ',')
            {
                if (IsAmountMode && !ValueText.Contains(".") && !ValueText.Contains(","))
                    ValueText += System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            }
        }

        private void Backspace()
        {
            var t = ValueText ?? "0";
            if (t.Length <= 1) ValueText = "0";
            else ValueText = t.Substring(0, t.Length - 1);
        }

        private async Task ConfirmAsync()
        {
            if (!CanConfirm) return;
            IsBusy = true;
            try
            {
                if (IsPercentMode && ApplyToWholeCart)
                    await _onApplyAsync(ValueInt, true, null).ConfigureAwait(true);
                else if (IsPercentMode && HasLineSelected)
                    await _onApplyAsync(ValueInt, true, _selectedLineBarcode).ConfigureAwait(true);
                else if (IsAmountMode && HasLineSelected)
                    await _onApplyAsync(ValueInt, false, _selectedLineBarcode).ConfigureAwait(true);
                else return;
                RequestClose?.Invoke(true);
            }
            finally { IsBusy = false; }
        }

        private void RaiseCanExecuteChanged()
        {
            (BackspaceCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ConfirmCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

        private sealed class RelayCommandParam : ICommand
        {
            private readonly Action<object> _execute;

            public RelayCommandParam(Action<object> execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _execute(parameter);
            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class AsyncRelayCommand : ICommand
        {
            private readonly Func<Task> _execute;
            private readonly Func<object, bool> _canExecute;

            public AsyncRelayCommand(Func<Task> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);
            public async void Execute(object parameter)
            {
                try
                {
                    await _execute().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Win7POS.Wpf.Infrastructure.UiErrorHandler.Handle(ex, null, "Discount AsyncRelayCommand failed");
                }
            }
#pragma warning disable CS0067
            public event EventHandler CanExecuteChanged;
#pragma warning restore CS0067
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
