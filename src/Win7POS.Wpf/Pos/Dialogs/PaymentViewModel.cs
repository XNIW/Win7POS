using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;
using Win7POS.Core.Util;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum PaymentActiveField { Cash, Card }

    public sealed class PaymentViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly long _totalDueMinor;
        private readonly PaymentReceiptDraft _draft;
        private readonly Func<string, string, Task> _printFiscalToThermal;

        private string _cashReceived = "";
        private string _cardAmount = "0";
        private PaymentActiveField _activeField = PaymentActiveField.Cash;
        private bool _shouldPrint;
        private string _receiptPreviewText = "";
        private string _receiptPreviewFirstLine = "";
        private string _receiptPreviewRest = "";
        private int _nextBoletaNumber;
        private readonly int _minimumBoletaNumber;
        private string _fiscalPreviewText = "";
        private string _fiscalStatus = "";
        private string _fiscalStatusKey = "payment.pending";
        private bool _autoPrintFiscalBoleta;
        private bool _openDrawerForCurrentPayment;

        public PaymentViewModel(long totalDueMinor, PaymentReceiptDraft draft = null, Func<string, string, Task> printFiscalToThermal = null, bool openDrawerDefault = true)
        {
            _totalDueMinor = totalDueMinor;
            _draft = draft;
            _printFiscalToThermal = printFiscalToThermal;

            _cashReceived = totalDueMinor > 0
                ? totalDueMinor.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            _cardAmount = "0";
            _activeField = PaymentActiveField.Cash;

            _shouldPrint = draft?.DefaultPrint ?? false;
            _autoPrintFiscalBoleta = !App.IsSafeStart;
            _openDrawerForCurrentPayment = openDrawerDefault;
            _minimumBoletaNumber = Math.Max(1, draft?.NextBoletaNumber ?? 1);
            _nextBoletaNumber = _minimumBoletaNumber;
            SetFiscalStatusKey("payment.pending");

            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
            SuspendCommand = new RelayCommand(_ => { WasSuspended = true; RequestClose?.Invoke(false); }, _ => true);
            AppendDigitCommand = new RelayCommand(AppendDigit, _ => true);
            BackspaceCommand = new RelayCommand(_ => Backspace(), _ => true);
            ClearFieldCommand = new RelayCommand(_ => ClearActiveField(), _ => true);
            AddQuickAmountCommand = new RelayCommand(AddQuickAmount, _ => true);
            SetExactTotalCommand = new RelayCommand(_ => SetExactTotal(), _ => true);
            SetRoundedTotalCommand = new RelayCommand(_ => SetRoundedTotal(), _ => true);
            PayAllCardCommand = new RelayCommand(_ => PayAllCard(), _ => true);
            IncrementBoletaCommand = new RelayCommand(
                _ => NextBoletaNumber = checked(NextBoletaNumber + 1),
                _ => NextBoletaNumber < int.MaxValue);
            DecrementBoletaCommand = new RelayCommand(
                _ => NextBoletaNumber -= 1,
                _ => NextBoletaNumber > _minimumBoletaNumber);

            UpdateReceiptPreviewText();
            UpdateFiscalPreviewText();
            PosLocalization.Current.LanguageChanged += OnLanguageChanged;
        }

        public string SaleCode => _draft?.SaleCode ?? "";
        public long CreatedAtMs => _draft?.CreatedAtMs ?? 0;

        public int NextBoletaNumber
        {
            get => _nextBoletaNumber;
            set
            {
                if (value < _minimumBoletaNumber) value = _minimumBoletaNumber;
                if (_nextBoletaNumber == value) return;
                _nextBoletaNumber = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NextBoletaNumberText));
                OnPropertyChanged(nameof(NextBoletaNumberLabel));
                UpdateFiscalPreviewText();
                (IncrementBoletaCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DecrementBoletaCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public string NextBoletaNumberLabel => PosLocalization.Current.Format(
            "payment.nextBoletaNumber",
            NextBoletaNumberText);

        public string PaidLabelPrefix => PosLocalization.Current.Text("payment.paid") + " ";

        public string NextBoletaNumberText
        {
            get => _nextBoletaNumber.ToString(CultureInfo.InvariantCulture);
            set
            {
                var parsed = MoneyClp.Parse(value ?? "");
                if (parsed >= 0) NextBoletaNumber = parsed;
            }
        }

        public bool ShouldPrint
        {
            get => _shouldPrint;
            set { _shouldPrint = value; OnPropertyChanged(); }
        }

        public bool AutoPrintFiscalBoleta
        {
            get => _autoPrintFiscalBoleta;
            set
            {
                var effectiveValue = !App.IsSafeStart && value;
                if (_autoPrintFiscalBoleta == effectiveValue) return;
                _autoPrintFiscalBoleta = effectiveValue;
                OnPropertyChanged();
            }
        }

        /// <summary>Per questa vendita: apri cassetto se contanti &gt; 0. Default da impostazioni stampante.</summary>
        public bool OpenDrawerForCurrentPayment
        {
            get => _openDrawerForCurrentPayment;
            set { _openDrawerForCurrentPayment = value; OnPropertyChanged(); }
        }

        public string ReceiptPreviewText
        {
            get => _receiptPreviewText;
            private set { _receiptPreviewText = value ?? ""; OnPropertyChanged(); }
        }

        /// <summary>Prima riga (nome negozio) per anteprima = stampa: grassetto e più grande.</summary>
        public string ReceiptPreviewFirstLine
        {
            get => _receiptPreviewFirstLine;
            private set { _receiptPreviewFirstLine = value ?? ""; OnPropertyChanged(); }
        }

        /// <summary>Resto righe per anteprima = stampa.</summary>
        public string ReceiptPreviewRest
        {
            get => _receiptPreviewRest;
            private set { _receiptPreviewRest = value ?? ""; OnPropertyChanged(); }
        }

        [Obsolete("Use ReceiptPreviewText")]
        public string CartReceiptPreview => ReceiptPreviewText;
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
        public string MissingAmountText => IsCardOverBalance
            ? PosLocalization.Current.Text("payment.cardOverBalance")
            : IsValid ? string.Empty : PosLocalization.Current.Format("payment.missing", MoneyClp.Format(MissingAmountMinor));
        public string RestoOrMissingDisplay => IsValid
            ? PosLocalization.Current.Format("payment.changeCashOnly", ChangeDueText)
            : MissingAmountText;

        public bool IsCardOverBalance => CardAmountMinor > Math.Max(0, _totalDueMinor - CashAmountMinor);
        public bool IsValid => CashAmountMinor >= 0 &&
            CardAmountMinor >= 0 &&
            !IsCardOverBalance &&
            (long)CashAmountMinor + (long)CardAmountMinor >= _totalDueMinor;

        /// <summary>True se il pagamento include contanti (solo in quel caso si stampa automaticamente la boleta).</summary>
        public bool IsCashPayment => CashAmountMinor > 0;

        public int CashAmountMinor => MoneyClp.Parse(CashReceived);
        public int CardAmountMinor => MoneyClp.Parse(CardAmount);

        public long ChangeDueMinor
        {
            get
            {
                if (!IsValid) return 0;
                var balanceAfterCard = Math.Max(0, _totalDueMinor - CardAmountMinor);
                return Math.Max(0, (long)CashAmountMinor - balanceAfterCard);
            }
        }

        public long MissingAmountMinor
        {
            get
            {
                var paid = (long)CashAmountMinor + (long)CardAmountMinor;
                if (CashAmountMinor < 0 || CardAmountMinor < 0) return _totalDueMinor;
                var missing = _totalDueMinor - paid;
                return missing > 0 ? missing : 0;
            }
        }

        public bool WasSuspended { get; private set; }

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SuspendCommand { get; }
        public ICommand AppendDigitCommand { get; }
        public ICommand BackspaceCommand { get; }
        public ICommand ClearFieldCommand { get; }
        public ICommand AddQuickAmountCommand { get; }
        public ICommand SetExactTotalCommand { get; }
        public ICommand SetRoundedTotalCommand { get; }
        public ICommand PayAllCardCommand { get; }
        public ICommand IncrementBoletaCommand { get; }
        public ICommand DecrementBoletaCommand { get; }

        public string FiscalPreviewText { get => _fiscalPreviewText; private set { _fiscalPreviewText = value ?? ""; OnPropertyChanged(); } }
        public string FiscalHeaderText { get; private set; }
        public string FiscalFooterText { get; private set; }
        public string FiscalStatus { get => _fiscalStatus; private set { _fiscalStatus = value ?? ""; OnPropertyChanged(); } }

        public event Action<bool> RequestClose;
        /// <summary>Richiesta di rifocalizzare il campo contanti (es. dopo PayAllCard) per fix caret visivo.</summary>
        public event Action RequestCashRefocus;
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
            OnPropertyChanged(nameof(IsCardOverBalance));
            OnPropertyChanged(nameof(MissingAmountText));
            OnPropertyChanged(nameof(RestoOrMissingDisplay));
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(TotalPaidText));
            UpdateReceiptPreviewText();
            UpdateFiscalPreviewText();
            RaiseCanExecuteChanged();
        }

        private void UpdateFiscalPreviewText()
        {
            var shop = _draft?.ShopInfo ?? new ReceiptShopInfo();
            long venta = _totalDueMinor;
            long iva = (long)Math.Round(venta * 15.9657 / 100.0, MidpointRounding.AwayFromZero);
            var full = FiscalBoletaTextRenderer.Render(
                shop,
                CreatedAtMs,
                NextBoletaNumber,
                venta,
                iva,
                _draft?.UseReceipt42 == true ? 42 : 32);
            FiscalPreviewText = full;

            var lines = full.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            var idx = lines.FindIndex(s => (s ?? "").Trim().Equals(
                FiscalBoletaTextRenderer.SiiStampMarker,
                StringComparison.Ordinal));
            if (idx < 0) idx = lines.Count;

            FiscalHeaderText = string.Join(Environment.NewLine, lines.Take(idx));
            FiscalFooterText = string.Join(Environment.NewLine, lines.Skip(idx));

            OnPropertyChanged(nameof(FiscalHeaderText));
            OnPropertyChanged(nameof(FiscalFooterText));
            OnPropertyChanged(nameof(FiscalPreviewText));
        }

        /// <summary>Se la stampa automatica è attiva e il pagamento include contanti, invia la boleta direttamente alla stampante.</summary>
        public async Task<bool> TriggerAutoPrintFiscalBoletaIfEnabledAsync()
        {
            if (App.IsSafeStart)
                return false;

            if (!_autoPrintFiscalBoleta)
                return false;

            if (CashAmountMinor <= 0)
            {
                SetFiscalStatusKey("payment.notPrintedCardOnly");
                return false;
            }

            return await PrintFiscalBoletaAsync().ConfigureAwait(true);
        }

        /// <summary>Invia il testo fiscale direttamente alla stampante termica senza creare file locali.</summary>
        private async Task<bool> PrintFiscalBoletaAsync()
        {
            if (App.IsSafeStart)
                return false;

            if (_printFiscalToThermal == null)
                throw new InvalidOperationException(
                    PosLocalization.T("printer.fiscalPrintUnavailable"));

            SetFiscalStatusKey("payment.sendingBoletaPrinter");
            await _printFiscalToThermal(FiscalPreviewText, SaleCode).ConfigureAwait(true);
            SetFiscalStatusKey("payment.printed");
            return true;
        }

        private void SetFiscalStatusKey(string key)
        {
            _fiscalStatusKey = string.IsNullOrWhiteSpace(key) ? "payment.pending" : key;
            FiscalStatus = PosLocalization.T(_fiscalStatusKey);
        }

        private void UpdateReceiptPreviewText()
        {
            if (_draft?.CartLines == null || _draft.CartLines.Count == 0)
            {
                ReceiptPreviewText = PosLocalization.Current.Text("pos.status.cartEmpty");
                return;
            }

            var change = IsValid ? ChangeDueMinor : 0L;
            var paidCash = CashAmountMinor >= 0 ? (long)CashAmountMinor : 0L;
            var paidCard = CardAmountMinor >= 0 ? (long)CardAmountMinor : 0L;

            var sale = new Sale
            {
                Code = _draft.SaleCode,
                CreatedAt = _draft.CreatedAtMs,
                Total = _totalDueMinor,
                PaidCash = paidCash,
                PaidCard = paidCard,
                Change = change
            };

            var saleLines = _draft.CartLines.Select(x => new SaleLine
            {
                Barcode = x.Barcode,
                Name = x.Name ?? "-",
                Quantity = x.Quantity,
                UnitPrice = x.UnitPrice,
                LineTotal = x.LineTotal
            }).ToList();

            var shop = _draft?.ShopInfo ?? new ReceiptShopInfo { Name = "Win7POS", Address = "", Footer = PosLocalization.T("receipt.thanks") };
            var receiptText = PosReceiptTextRenderer.BuildReceipt(
                sale,
                saleLines,
                _draft.UseReceipt42,
                shop);
            ReceiptPreviewText = receiptText;
            PosReceiptTextRenderer.SplitPreview(
                receiptText,
                out var firstLine,
                out var remainingText);
            ReceiptPreviewFirstLine = firstLine;
            ReceiptPreviewRest = remainingText;
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

        private void SetActiveFieldMinor(long minor)
        {
            if (minor < 0) minor = 0;
            var text = MoneyClp.Format(minor);
            if (_activeField == PaymentActiveField.Cash)
                CashReceived = text;
            else
                CardAmount = text;
        }

        /// <summary>Simmetrico a PayAllCard: azzera carta e attiva contanti prima di applicare rapidi contanti.</summary>
        private void PrepareCashQuickAction()
        {
            CardAmount = "0";
            ActiveField = PaymentActiveField.Cash;
        }

        /// <summary>Richiede refocus su CashBox con caret in fondo (dopo rapidi cash).</summary>
        private void RequestCashCaretToEnd()
        {
            ActiveField = PaymentActiveField.Cash;
            RequestCashRefocus?.Invoke();
        }

        private void AddQuickAmount(object parameter)
        {
            var add = ParseQuickAmount(parameter);
            if (add <= 0) return;

            PrepareCashQuickAction();
            CashReceived = (CashAmountMinor + add).ToString(CultureInfo.InvariantCulture);
            RequestCashCaretToEnd();
        }

        private static int ParseQuickAmount(object parameter)
        {
            if (parameter is int i) return i;
            if (parameter is string str && int.TryParse(str, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0;
        }

        private void SetExactTotal()
        {
            PrepareCashQuickAction();
            CashReceived = _totalDueMinor.ToString(CultureInfo.InvariantCulture);
            RequestCashCaretToEnd();
        }

        private void SetRoundedTotal()
        {
            PrepareCashQuickAction();
            var rounded = RoundCashAmount(_totalDueMinor);
            CashReceived = rounded.ToString(CultureInfo.InvariantCulture);
            RequestCashCaretToEnd();
        }

        private static long RoundCashAmount(long minor)
        {
            var rounded = ((minor + 500) / 1000) * 1000;
            if (rounded < minor) rounded += 1000;
            return rounded > int.MaxValue ? int.MaxValue : rounded;
        }

        private void PayAllCard()
        {
            CashReceived = "0";
            CardAmount = _totalDueMinor.ToString(CultureInfo.InvariantCulture);
            ActiveField = PaymentActiveField.Cash;
            RequestCashRefocus?.Invoke();
        }

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            OnPropertyChanged(nameof(NextBoletaNumberLabel));
            OnPropertyChanged(nameof(PaidLabelPrefix));
            SetFiscalStatusKey(_fiscalStatusKey);
            NotifyDerived();
        }

        public void Dispose()
        {
            PosLocalization.Current.LanguageChanged -= OnLanguageChanged;
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
