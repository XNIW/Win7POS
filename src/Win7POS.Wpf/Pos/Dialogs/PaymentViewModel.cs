using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Core.Receipt;
using Win7POS.Core.Util;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum PaymentActiveField { Cash, Card }

    public sealed class PaymentViewModel : INotifyPropertyChanged
    {
        private readonly int _totalDueMinor;
        private readonly PaymentReceiptDraft _draft;
        private readonly Func<string, string, Task<string>> _generateFiscalPdf;

        private string _cashReceived = "";
        private string _cardAmount = "0";
        private PaymentActiveField _activeField = PaymentActiveField.Cash;
        private bool _shouldPrint;
        private string _receiptPreviewText = "";
        private bool _showSiiWeb = true;
        private string _fiscalPreviewText = "";
        private string _fiscalStatus = "";

        public PaymentViewModel(int totalDueMinor, PaymentReceiptDraft draft = null, Func<string, string, Task<string>> generateFiscalPdf = null)
        {
            _totalDueMinor = totalDueMinor;
            _draft = draft;
            _generateFiscalPdf = generateFiscalPdf;
            _shouldPrint = draft?.DefaultPrint ?? false;
            NextBoletaNumber = draft?.NextBoletaNumber ?? 0;

            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
            AppendDigitCommand = new RelayCommand(AppendDigit, _ => true);
            BackspaceCommand = new RelayCommand(_ => Backspace(), _ => true);
            ClearFieldCommand = new RelayCommand(_ => ClearActiveField(), _ => true);
            AddQuickAmountCommand = new RelayCommand(AddQuickAmount, _ => true);
            SetExactTotalCommand = new RelayCommand(_ => SetExactTotal(), _ => true);
            SetRoundedTotalCommand = new RelayCommand(_ => SetRoundedTotal(), _ => true);
            PayAllCardCommand = new RelayCommand(_ => PayAllCard(), _ => true);
            GeneratePdfCommand = new RelayCommand(_ => _ = GeneratePdfAsync(), _ => _generateFiscalPdf != null);
            OpenSiiCommand = new RelayCommand(_ => OpenSii(), _ => true);

            UpdateReceiptPreviewText();
            UpdateFiscalPreviewText();
        }

        public string SaleCode => _draft?.SaleCode ?? "";
        public long CreatedAtMs => _draft?.CreatedAtMs ?? 0;
        public int NextBoletaNumber { get; }

        public bool ShouldPrint
        {
            get => _shouldPrint;
            set { _shouldPrint = value; OnPropertyChanged(); }
        }

        public string ReceiptPreviewText
        {
            get => _receiptPreviewText;
            private set { _receiptPreviewText = value ?? ""; OnPropertyChanged(); }
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
        public ICommand GeneratePdfCommand { get; }
        public ICommand OpenSiiCommand { get; }

        public bool ShowSiiWeb { get => _showSiiWeb; set { _showSiiWeb = value; OnPropertyChanged(); } }
        public string FiscalPreviewText { get => _fiscalPreviewText; private set { _fiscalPreviewText = value ?? ""; OnPropertyChanged(); } }
        public string FiscalStatus { get => _fiscalStatus; private set { _fiscalStatus = value ?? ""; OnPropertyChanged(); } }

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
            UpdateReceiptPreviewText();
            UpdateFiscalPreviewText();
            RaiseCanExecuteChanged();
        }

        private void UpdateFiscalPreviewText()
        {
            var shop = _draft?.ShopInfo;
            var name = shop?.Name ?? "Negozio";
            var rut = shop?.Rut ?? "";
            var addr = shop?.Address ?? "";
            var paidCash = CashAmountMinor >= 0 ? CashAmountMinor : 0;
            var paidCard = CardAmountMinor >= 0 ? CardAmountMinor : 0;
            var lines = new List<string>
            {
                name,
                !string.IsNullOrWhiteSpace(rut) ? "RUT: " + rut.Trim() : "",
                !string.IsNullOrWhiteSpace(addr) ? addr : "",
                "---",
                "Boleta N°: " + NextBoletaNumber,
                "Vendita: " + SaleCode,
                "Totale: " + MoneyClp.Format(_totalDueMinor),
                "Contanti: " + MoneyClp.Format(paidCash),
                "Carta: " + MoneyClp.Format(paidCard),
                "Resto: " + (IsValid ? MoneyClp.Format(ChangeDueMinor) : "-")
            };
            FiscalPreviewText = string.Join(Environment.NewLine, lines.Where(s => s != ""));
        }

        private async Task GeneratePdfAsync()
        {
            if (_generateFiscalPdf == null) return;
            FiscalStatus = "Generazione...";
            try
            {
                var path = await _generateFiscalPdf(FiscalPreviewText, SaleCode).ConfigureAwait(true);
                FiscalStatus = "Aperto: " + path;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            }
            catch (Exception ex)
            {
                FiscalStatus = "Errore: " + ex.Message;
            }
        }

        private void OpenSii()
        {
            try { Process.Start(new ProcessStartInfo("https://www.sii.cl") { UseShellExecute = true }); } catch { }
        }

        private void UpdateReceiptPreviewText()
        {
            if (_draft?.CartLines == null || _draft.CartLines.Count == 0)
            {
                ReceiptPreviewText = "Carrello vuoto.";
                return;
            }

            var change = IsValid ? ChangeDueMinor : 0;
            var paidCash = CashAmountMinor >= 0 ? CashAmountMinor : 0;
            var paidCard = CardAmountMinor >= 0 ? CardAmountMinor : 0;

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

            var options = _draft.UseReceipt42 ? ReceiptOptions.Default42Clp() : ReceiptOptions.Default32Clp();
            var shop = _draft?.ShopInfo ?? new ReceiptShopInfo { Name = "Win7POS", Address = "", Footer = "Grazie" };

            var lines = ReceiptFormatter.Format(sale, saleLines, options, shop);
            ReceiptPreviewText = string.Join(Environment.NewLine, lines);
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
            int rounded = ((_totalDueMinor + 500) / 1000) * 1000;
            if (rounded < _totalDueMinor) rounded += 1000;
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
            (GeneratePdfCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
