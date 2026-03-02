using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Win7POS.Core.Util;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum PaymentActiveField { Cash, Card }

    public sealed class PaymentViewModel : INotifyPropertyChanged
    {
        private readonly int _totalDueMinor;
        private readonly string _cartReceiptPreview;

        private string _cashReceived = "";
        private string _cardAmount = "0";
        private PaymentActiveField _activeField = PaymentActiveField.Cash;

        public PaymentViewModel(int totalDueMinor, string cartReceiptPreview = null)
        {
            _totalDueMinor = totalDueMinor;
            _cartReceiptPreview = cartReceiptPreview ?? string.Empty;

            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
            AppendDigitCommand = new RelayCommand(AppendDigit, _ => true);
            BackspaceCommand = new RelayCommand(_ => Backspace(), _ => true);
            ClearFieldCommand = new RelayCommand(_ => ClearActiveField(), _ => true);
            AddQuickAmountCommand = new RelayCommand(AddQuickAmount, _ => true);
            SetExactTotalCommand = new RelayCommand(_ => SetExactTotal(), _ => true);
            SetRoundedTotalCommand = new RelayCommand(_ => SetRoundedTotal(), _ => true);
            PayAllCardCommand = new RelayCommand(_ => PayAllCard(), _ => true);
        }

        public string CartReceiptPreview => _cartReceiptPreview;
        public string TotalDueText => MoneyClp.Format(_totalDueMinor);
        public string TotalPaidText => MoneyClp.Format(CashAmountMinor + CardAmountMinor);

        public PaymentActiveField ActiveField
        {
            get => _activeField;
            set { _activeField = value; OnPropertyChanged(); }
        }

        public string CashReceived
        {
            get => _cashReceived;
            set
            {
                _cashReceived = value ?? string.Empty;
                OnPropertyChanged();
                NotifyDerived();
            }
        }

        public string CardAmount
        {
            get => _cardAmount;
            set
            {
                _cardAmount = value ?? string.Empty;
                OnPropertyChanged();
                NotifyDerived();
            }
        }

        public string ChangeDueText => MoneyClp.Format(ChangeDueMinor);
        public string MissingAmountText => IsValid ? string.Empty : "Manca: " + MoneyClp.Format(MissingAmountMinor);
        public string RestoOrMissingDisplay => IsValid ? "Resto: " + ChangeDueText : MissingAmountText;

        public bool IsValid => CashAmountMinor >= 0 && CardAmountMinor >= 0 && CashAmountMinor + CardAmountMinor >= _totalDueMinor;

        public int CashAmountMinor => MoneyClp.Parse(CashReceived);
        public int CardAmountMinor => MoneyClp.Parse(CardAmount);

        public int ChangeDueMinor
        {
            get
            {
                if (!IsValid) return 0;
                return CashAmountMinor + CardAmountMinor - _totalDueMinor;
            }
        }

        public int MissingAmountMinor
        {
            get
            {
                var paid = CashAmountMinor + CardAmountMinor;
                if (CashAmountMinor < 0 || CardAmountMinor < 0) return _totalDueMinor;
                var missing = _totalDueMinor - paid;
                return missing > 0 ? missing : 0;
            }
        }

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand AppendDigitCommand { get; }
        public ICommand BackspaceCommand { get; }
        public ICommand ClearFieldCommand { get; }
        public ICommand AddQuickAmountCommand { get; }
        public ICommand SetExactTotalCommand { get; }
        public ICommand SetRoundedTotalCommand { get; }
        public ICommand PayAllCardCommand { get; }

        public event Action<bool> RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        public bool TryConfirm()
        {
            if (!IsValid) return false;
            RequestClose?.Invoke(true);
            return true;
        }

        public void Cancel()
        {
            RequestClose?.Invoke(false);
        }

        private void NotifyDerived()
        {
            OnPropertyChanged(nameof(ChangeDueText));
            OnPropertyChanged(nameof(MissingAmountText));
            OnPropertyChanged(nameof(RestoOrMissingDisplay));
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(TotalPaidText));
            RaiseCanExecuteChanged();
        }

        private void AppendDigit(object parameter)
        {
            var s = parameter as string;
            if (string.IsNullOrEmpty(s)) return;
            int current = GetActiveFieldMinor();
            int add;
            if (s == "00")
                add = current * 100;
            else if (s.Length == 1 && s[0] >= '0' && s[0] <= '9')
                add = current * 10 + (s[0] - '0');
            else
                return;
            SetActiveFieldMinor(add);
        }

        private void Backspace()
        {
            int current = GetActiveFieldMinor();
            SetActiveFieldMinor(current / 10);
        }

        private void ClearActiveField()
        {
            SetActiveFieldMinor(0);
        }

        private int GetActiveFieldMinor()
        {
            return _activeField == PaymentActiveField.Cash ? CashAmountMinor : CardAmountMinor;
        }

        private void SetActiveFieldMinor(int minor)
        {
            if (minor < 0) minor = 0;
            var text = MoneyClp.Format(minor);
            if (_activeField == PaymentActiveField.Cash)
                CashReceived = text;
            else
                CardAmount = text;
        }

        private void AddQuickAmount(object parameter)
        {
            int amountMinor = 0;
            if (parameter is int i)
                amountMinor = i;
            else if (parameter is string str && int.TryParse(str, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                amountMinor = parsed;
            if (amountMinor <= 0) return;
            int current = GetActiveFieldMinor();
            SetActiveFieldMinor(current + amountMinor);
        }

        private void SetExactTotal()
        {
            SetActiveFieldMinor(_totalDueMinor);
        }

        private void SetRoundedTotal()
        {
            int rounded = ((_totalDueMinor + 2500) / 5000) * 5000;
            if (rounded < _totalDueMinor) rounded += 5000;
            SetActiveFieldMinor(rounded);
        }

        private void PayAllCard()
        {
            CardAmount = MoneyClp.Format(_totalDueMinor);
            CashReceived = "0";
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
