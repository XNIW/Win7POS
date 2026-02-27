using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class PaymentViewModel : INotifyPropertyChanged
    {
        private readonly int _totalDueMinor;

        private string _cashReceived = "0";
        private string _cardAmount = "0";

        public PaymentViewModel(int totalDueMinor)
        {
            _totalDueMinor = totalDueMinor;
            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
        }

        public string TotalDueText => FormatMoney(_totalDueMinor);

        public string CashReceived
        {
            get => _cashReceived;
            set
            {
                _cashReceived = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChangeDueText));
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public string CardAmount
        {
            get => _cardAmount;
            set
            {
                _cardAmount = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChangeDueText));
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public string ChangeDueText => FormatMoney(ChangeDueMinor);

        public bool IsValid => CashAmountMinor >= 0 && CardAmountMinor >= 0 && CashAmountMinor + CardAmountMinor >= _totalDueMinor;

        public int CashAmountMinor => ParseMoneyToMinor(CashReceived);
        public int CardAmountMinor => ParseMoneyToMinor(CardAmount);

        public int ChangeDueMinor
        {
            get
            {
                if (!IsValid) return 0;
                return CashAmountMinor + CardAmountMinor - _totalDueMinor;
            }
        }

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

        private static string FormatMoney(int valueMinor)
        {
            var value = valueMinor / 100.0m;
            return value.ToString("N2", CultureInfo.GetCultureInfo("it-IT"));
        }

        private static int ParseMoneyToMinor(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var normalized = text.Trim().Replace(',', '.');
            if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                return -1;
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
