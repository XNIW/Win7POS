using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Util;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum DiscountMode { Percent, Amount }

    /// <summary>Contesto per anteprima sconto: riga selezionata o carrello.</summary>
    public sealed class DiscountPreviewContext
    {
        public string Barcode { get; set; }
        public string Name { get; set; }
        public int Quantity { get; set; }
        public long OriginalUnitPrice { get; set; }
        public long CurrentFinalUnitPrice { get; set; }
        public int? CurrentDiscountPercent { get; set; }
    }

    public sealed class DiscountViewModel : INotifyPropertyChanged
    {
        private string _valueText = "0";
        private DiscountMode _mode = DiscountMode.Percent;
        private bool _isBusy;
        private bool _applyToWholeCart;

        private readonly string _selectedLineBarcode;
        private readonly bool _hasCartItems;
        private readonly Func<int, long, bool, string, Task<bool>> _onApplyAsync; // percentOrZero, finalPriceMinor (solo amount), isPercent, lineBarcodeOrNull
        private readonly DiscountPreviewContext _previewContext;

        public DiscountViewModel(string selectedLineBarcode, bool hasCartItems, Func<int, long, bool, string, Task<bool>> onApplyAsync, DiscountPreviewContext previewContext = null)
        {
            _selectedLineBarcode = selectedLineBarcode?.Trim();
            _hasCartItems = hasCartItems;
            _onApplyAsync = onApplyAsync ?? throw new ArgumentNullException(nameof(onApplyAsync));
            _previewContext = previewContext;

            DigitCommand = new RelayCommandParam(Digit);
            BackspaceCommand = new RelayCommand(_ => Backspace(), _ => (ValueText ?? string.Empty).Length > 0);
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
            set { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPercentMode)); OnPropertyChanged(nameof(IsAmountMode)); OnPropertyChanged(nameof(ValueLabel)); OnPropertyChanged(nameof(ScopeText)); OnPropertyChanged(nameof(ValueLong)); OnPropertyChanged(nameof(CanConfirm)); RaisePreviewChanged(); RaiseCanExecuteChanged(); }
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
            get => _valueText ?? string.Empty;
            set
            {
                var incoming = value ?? string.Empty;

                if (IsPercentMode)
                {
                    var digits = new string(incoming.Where(char.IsDigit).ToArray());

                    if (digits.Length == 0)
                    {
                        _valueText = string.Empty;
                    }
                    else
                    {
                        if (!int.TryParse(digits, out var n))
                            n = 0;

                        if (n < 0) n = 0;
                        if (n > 100) n = 100;

                        _valueText = n.ToString();
                    }
                }
                else
                {
                    var sep = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
                    var idx = incoming.IndexOf('.');
                    if (idx < 0) idx = incoming.IndexOf(',');

                    if (idx >= 0)
                    {
                        var left = new string(incoming.Where((c, i) => i < idx && char.IsDigit(c)).ToArray()).TrimStart('0');
                        var right = new string(incoming.Where((c, i) => i > idx && char.IsDigit(c)).ToArray());

                        if (string.IsNullOrEmpty(left))
                            left = "0";

                        _valueText = left + sep + right;
                    }
                    else
                    {
                        var digits = new string(incoming.Where(char.IsDigit).ToArray());

                        if (digits.Length == 0)
                            _valueText = string.Empty;
                        else
                            _valueText = digits.TrimStart('0').Length == 0 ? "0" : digits.TrimStart('0');
                    }
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(ValueInt));
                OnPropertyChanged(nameof(ValueLong));
                OnPropertyChanged(nameof(CanConfirm));
                RaisePreviewChanged();
                RaiseCanExecuteChanged();
            }
        }

        private void RaisePreviewChanged()
        {
            OnPropertyChanged(nameof(PreviewFinalUnitPrice));
            OnPropertyChanged(nameof(OriginalPriceDisplay));
            OnPropertyChanged(nameof(PreviewFinalPriceDisplay));
            OnPropertyChanged(nameof(PreviewDiscountPercentDisplay));
            OnPropertyChanged(nameof(PreviewDiscountAmountDisplay));
            OnPropertyChanged(nameof(HasPreviewDiscount));
            OnPropertyChanged(nameof(IsRemovingDiscount));
        }

        public int ValueInt
        {
            get
            {
                var text = (ValueText ?? string.Empty).Trim();

                if (string.IsNullOrEmpty(text))
                    return 0;

                if (IsPercentMode)
                {
                    if (int.TryParse(text.Replace(",", "").Replace(".", ""), out var n) && n >= 0 && n <= 100)
                        return n;
                    return 0;
                }

                return MoneyClp.Parse(text);
            }
        }

        /// <summary>In modalità Importo: prezzo finale unitario inserito (parsed as long).</summary>
        public long ValueLong
        {
            get
            {
                if (IsPercentMode) return 0;
                var text = (ValueText ?? string.Empty).Trim().Replace(",", "").Replace(".", "").Replace(" ", "");
                if (string.IsNullOrEmpty(text)) return 0;
                return long.TryParse(text, out var n) && n >= 0 ? n : 0;
            }
        }

        public bool HasLineSelected => !string.IsNullOrEmpty(_selectedLineBarcode);

        public bool CanConfirm =>
            IsPercentMode
                ? ValueInt >= 0 && ValueInt <= 100 &&
                  ((ApplyToWholeCart && _hasCartItems) || (!ApplyToWholeCart && HasLineSelected))
                : ValueLong >= 0 && HasLineSelected;

        /// <summary>Prezzo unitario originale (sempre unitario, non totale riga).</summary>
        public long OriginalUnitPrice => _previewContext?.OriginalUnitPrice ?? 0;

        /// <summary>Anteprima prezzo finale unitario (coerente con applicazione che usa finalUnitPriceMinor).</summary>
        public long PreviewFinalUnitPrice
        {
            get
            {
                if (OriginalUnitPrice <= 0) return 0;
                if (IsPercentMode)
                {
                    var p = Math.Max(0, Math.Min(100, ValueInt));
                    return (OriginalUnitPrice * (100 - p)) / 100;
                }
                var final = ValueLong;
                if (final < 0) final = 0;
                if (final > OriginalUnitPrice) final = OriginalUnitPrice;
                return final;
            }
        }

        public string OriginalPriceDisplay => MoneyClp.Format(OriginalUnitPrice);
        public string PreviewFinalPriceDisplay => MoneyClp.Format(PreviewFinalUnitPrice);
        public string PreviewDiscountPercentDisplay
        {
            get
            {
                if (OriginalUnitPrice <= 0 || PreviewFinalUnitPrice >= OriginalUnitPrice) return string.Empty;
                var pct = (int)Math.Round((OriginalUnitPrice - PreviewFinalUnitPrice) * 100.0 / OriginalUnitPrice, MidpointRounding.AwayFromZero);
                return pct > 0 ? "-" + pct + "%" : string.Empty;
            }
        }
        public string PreviewDiscountAmountDisplay
        {
            get
            {
                if (OriginalUnitPrice <= 0 || PreviewFinalUnitPrice >= OriginalUnitPrice) return string.Empty;
                var amount = OriginalUnitPrice - PreviewFinalUnitPrice;
                return "Risparmio " + MoneyClp.Format(amount);
            }
        }
        public bool HasPreviewDiscount => OriginalUnitPrice > 0 && PreviewFinalUnitPrice < OriginalUnitPrice;
        public bool IsRemovingDiscount => OriginalUnitPrice > 0 && (IsPercentMode ? ValueInt == 0 : PreviewFinalUnitPrice >= OriginalUnitPrice);
        public bool ShowPreview => OriginalUnitPrice > 0;

        public ICommand DigitCommand { get; }
        public ICommand BackspaceCommand { get; }
        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        public event Action<bool> RequestClose;

        public event PropertyChangedEventHandler PropertyChanged;

        private void Digit(object digit)
        {
            var d = digit as string;
            if (string.IsNullOrEmpty(d)) return;

            if (d == "00")
            {
                if (string.IsNullOrEmpty(ValueText))
                    ValueText = "0";
                else
                    ValueText += "00";
                return;
            }

            if (d.Length != 1) return;
            var c = d[0];

            if (c >= '0' && c <= '9')
            {
                if (string.IsNullOrEmpty(ValueText))
                    ValueText = d;
                else if (ValueText == "0" && c != '0')
                    ValueText = d;
                else
                    ValueText += d;
            }
            else if (c == '.' || c == ',')
            {
                if (IsAmountMode && !ValueText.Contains(".") && !ValueText.Contains(","))
                    ValueText = string.IsNullOrEmpty(ValueText)
                        ? "0" + System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator
                        : ValueText + System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            }
        }

        private void Backspace()
        {
            var t = ValueText ?? string.Empty;
            if (t.Length <= 1)
                ValueText = string.Empty;
            else
                ValueText = t.Substring(0, t.Length - 1);
        }

        private async Task ConfirmAsync()
        {
            if (!CanConfirm) return;
            IsBusy = true;
            try
            {
                bool applied;
                if (IsPercentMode && ApplyToWholeCart)
                    applied = await _onApplyAsync(ValueInt, 0L, true, null).ConfigureAwait(true);
                else if (IsPercentMode && HasLineSelected)
                    applied = await _onApplyAsync(ValueInt, 0L, true, _selectedLineBarcode).ConfigureAwait(true);
                else if (IsAmountMode && HasLineSelected)
                    applied = await _onApplyAsync(0, ValueLong, false, _selectedLineBarcode).ConfigureAwait(true);
                else return;
                if (applied)
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
