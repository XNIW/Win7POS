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

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum PaymentActiveField { Cash, Card }

    public sealed class PaymentViewModel : INotifyPropertyChanged
    {
        private readonly long _totalDueMinor;
        private readonly PaymentReceiptDraft _draft;
        private readonly Func<string, string, Task<string>> _generateFiscalPdf;
        private readonly Func<string, string, Task> _printFiscalToThermal;

        private string _cashReceived = "";
        private string _cardAmount = "0";
        private PaymentActiveField _activeField = PaymentActiveField.Cash;
        private bool _shouldPrint;
        private string _receiptPreviewText = "";
        private string _receiptPreviewFirstLine = "";
        private string _receiptPreviewRest = "";
        private int _nextBoletaNumber;
        private string _fiscalPreviewText = "";
        private string _fiscalStatus = "";
        private bool _autoPrintPdfSii;
        private bool _openDrawerForCurrentPayment;

        public PaymentViewModel(long totalDueMinor, PaymentReceiptDraft draft = null, Func<string, string, Task<string>> generateFiscalPdf = null, Func<string, string, Task> printFiscalToThermal = null, bool openDrawerDefault = true)
        {
            _totalDueMinor = totalDueMinor;
            _draft = draft;
            _generateFiscalPdf = generateFiscalPdf;
            _printFiscalToThermal = printFiscalToThermal;

            _cashReceived = totalDueMinor > 0
                ? totalDueMinor.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            _cardAmount = "0";
            _activeField = PaymentActiveField.Cash;

            _shouldPrint = draft?.DefaultPrint ?? false;
            _autoPrintPdfSii = true;
            _openDrawerForCurrentPayment = openDrawerDefault;
            _nextBoletaNumber = draft?.NextBoletaNumber ?? 0;

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
            PrintPdfCommand = new RelayCommand(_ => _ = StampaPdfAsync(), _ => _generateFiscalPdf != null);
            IncrementBoletaCommand = new RelayCommand(_ => NextBoletaNumber += 1, _ => true);
            DecrementBoletaCommand = new RelayCommand(_ => { if (NextBoletaNumber > 0) NextBoletaNumber -= 1; }, _ => true);

            UpdateReceiptPreviewText();
            UpdateFiscalPreviewText();
        }

        public string SaleCode => _draft?.SaleCode ?? "";
        public long CreatedAtMs => _draft?.CreatedAtMs ?? 0;

        public int NextBoletaNumber
        {
            get => _nextBoletaNumber;
            set
            {
                if (value < 0) value = 0;
                _nextBoletaNumber = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NextBoletaNumberText));
                UpdateFiscalPreviewText();
            }
        }

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

        public bool AutoPrintPdfSii
        {
            get => _autoPrintPdfSii;
            set { _autoPrintPdfSii = value; OnPropertyChanged(); }
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
        public string MissingAmountText => IsValid ? string.Empty : "Manca: " + MoneyClp.Format(MissingAmountMinor);
        public string RestoOrMissingDisplay => IsValid ? "Resto: " + ChangeDueText : MissingAmountText;

        public bool IsValid => CashAmountMinor >= 0 && CardAmountMinor >= 0 && (long)CashAmountMinor + (long)CardAmountMinor >= _totalDueMinor;

        /// <summary>True se il pagamento include contanti (solo in quel caso si stampa automaticamente il PDF SII).</summary>
        public bool IsCashPayment => CashAmountMinor > 0;

        public int CashAmountMinor => MoneyClp.Parse(CashReceived);
        public int CardAmountMinor => MoneyClp.Parse(CardAmount);

        public long ChangeDueMinor
        {
            get
            {
                if (!IsValid) return 0;
                return (long)CashAmountMinor + (long)CardAmountMinor - _totalDueMinor;
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
        public ICommand PrintPdfCommand { get; }
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
            OnPropertyChanged(nameof(MissingAmountText));
            OnPropertyChanged(nameof(RestoOrMissingDisplay));
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(TotalPaidText));
            UpdateReceiptPreviewText();
            UpdateFiscalPreviewText();
            RaiseCanExecuteChanged();
        }

        private static readonly CultureInfo EsCl = CultureInfo.GetCultureInfo("es-CL");

        private static string Fmt(long v) => v.ToString("#,0", EsCl);

        private void UpdateFiscalPreviewText()
        {
            var shop = _draft?.ShopInfo ?? new ReceiptShopInfo();
            var currentDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            long venta = _totalDueMinor;
            long iva = (long)Math.Round(venta * 15.9657 / 100.0, MidpointRounding.AwayFromZero);
            var formattedAmount = Fmt(venta);
            var formattedIVA = Fmt(iva);
            var formattedNum = Fmt(NextBoletaNumber);

            var lines = new List<string>
            {
                "",
                shop.Name ?? "",
                shop.Rut ?? "",
                "Giro: VENTA PRENDAS DE",
                "VESTIR,CALZADO,FERRETERIA,MENAJE,AR",
                "T.EN GENERAL",
                shop.Address ?? "",
                string.IsNullOrWhiteSpace(shop.City) ? "" : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(shop.City.Trim().ToLowerInvariant()),
                $"BOLETA ELECTRÓNICA NUMERO: {formattedNum}",
                "REF. VENDEDOR: 24231788-2",
                $"Fecha: {currentDate}",
                "",
                "Dirección: Santiago",
                "",
                "Venta",
                $"                           $ {formattedAmount}",
                "",
                "El IVA incluido en esta boleta es",
                $"de: $ {formattedIVA}",
                "",
                "Timbre Electrónico SII",
                "Res. 99 de 2014",
                "Verifique documento en sii.cl"
            };
            var full = string.Join(Environment.NewLine, lines);
            FiscalPreviewText = full;

            const string marker = "Timbre Electrónico SII";
            var idx = lines.FindIndex(s => (s ?? "").Trim().Equals(marker, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = lines.Count;

            FiscalHeaderText = string.Join(Environment.NewLine, lines.Take(idx));
            FiscalFooterText = string.Join(Environment.NewLine, lines.Skip(idx));

            OnPropertyChanged(nameof(FiscalHeaderText));
            OnPropertyChanged(nameof(FiscalFooterText));
            OnPropertyChanged(nameof(FiscalPreviewText));
        }

        /// <summary>Se AutoPrintPdfSii è attivo e il pagamento include contanti, stampa il PDF SII. Ritorna true solo se il PDF è stato davvero stampato.</summary>
        public async Task<bool> TriggerAutoPrintPdfIfEnabledAsync()
        {
            if (!_autoPrintPdfSii)
                return false;

            if (CashAmountMinor <= 0)
            {
                FiscalStatus = "PDF SII non stampato: pagamento con carta.";
                return false;
            }

            return await StampaPdfAsync().ConfigureAwait(true);
        }

        /// <summary>Genera PDF e invia il testo fiscale alla stampante termica (stessa dello scontrino). File PDF eliminato dopo 15s. Ritorna true se stampato a stampante.</summary>
        private async Task<bool> StampaPdfAsync()
        {
            if (_generateFiscalPdf == null)
                return false;

            FiscalStatus = "Generazione PDF...";
            string path = null;
            try
            {
                path = await _generateFiscalPdf(FiscalPreviewText, SaleCode).ConfigureAwait(true);

                if (_printFiscalToThermal != null)
                {
                    FiscalStatus = "Invio a stampante...";
                    await _printFiscalToThermal(FiscalPreviewText, SaleCode).ConfigureAwait(true);

                    if (!string.IsNullOrEmpty(path))
                    {
                        var pathToDelete = path;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(15000).ConfigureAwait(false);
                            try
                            {
                                if (System.IO.File.Exists(pathToDelete)) System.IO.File.Delete(pathToDelete);
                            }
                            catch (System.Exception delEx)
                            {
                                try { new Win7POS.Wpf.Infrastructure.FileLogger("PaymentViewModel").LogWarning("Cleanup PDF temporaneo fallito: " + pathToDelete, delEx); } catch { }
                            }
                        });
                    }

                    FiscalStatus = "Stampato.";
                    return true;
                }

                FiscalStatus = "PDF salvato: " + path;
                return false;
            }
            catch (Exception ex)
            {
                FiscalStatus = "Errore: " + ex.Message;
                return false;
            }
        }

        private void UpdateReceiptPreviewText()
        {
            if (_draft?.CartLines == null || _draft.CartLines.Count == 0)
            {
                ReceiptPreviewText = "Carrello vuoto.";
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

            var options = _draft.UseReceipt42 ? ReceiptOptions.Default42Clp() : ReceiptOptions.Default32Clp();
            var shop = _draft?.ShopInfo ?? new ReceiptShopInfo { Name = "Win7POS", Address = "", Footer = "Grazie" };

            var lines = new List<string>(ReceiptFormatter.Format(sale, saleLines, options, shop));
            // Stessa struttura della stampante: riga "Scontrino: XXX" in fondo (sotto la stampante viene disegnato il barcode Code128)
            if (!string.IsNullOrEmpty(sale.Code))
            {
                lines.Add("");
                lines.Add("Scontrino: " + sale.Code);
            }
            ReceiptPreviewText = string.Join(Environment.NewLine, lines);
            // Anteprima = stampa: prima riga (nome negozio) in grassetto e più grande, resto uguale
            ReceiptPreviewFirstLine = lines.Count > 0 ? (lines[0] ?? "") : "";
            ReceiptPreviewRest = lines.Count > 1 ? string.Join(Environment.NewLine, lines.Skip(1)) : "";
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

        private void AddQuickAmount(object parameter)
        {
            var add = ParseQuickAmount(parameter);
            if (add <= 0) return;

            PrepareCashQuickAction();
            CashReceived = (CashAmountMinor + add).ToString(CultureInfo.InvariantCulture);
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
        }

        private void SetRoundedTotal()
        {
            PrepareCashQuickAction();
            var rounded = RoundCashAmount(_totalDueMinor);
            CashReceived = rounded.ToString(CultureInfo.InvariantCulture);
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
            (PrintPdfCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
