using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Core.Util;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class RefundViewModel : INotifyPropertyChanged
    {
        private bool _isFullVoid = true;
        private string _cashRefund = "0";
        private string _cardRefund = "0";
        private string _reason = string.Empty;
        private string _scanMessage = string.Empty;

        public RefundViewModel(RefundPreviewModel preview)
        {
            Preview = preview ?? throw new ArgumentNullException(nameof(preview));
            foreach (var x in preview.Lines)
            {
                var row = new RefundLineRow
                {
                    OriginalLineId = x.OriginalLineId,
                    Barcode = x.Barcode ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    UnitPriceMinor = x.UnitPriceMinor,
                    SoldQty = x.SoldQty,
                    RefundedQty = x.RefundedQty,
                    RemainingQty = x.RemainingQty,
                    QtyToRefund = 0
                };
                row.PropertyChanged += (_, __) => OnAmountsChanged();
                Lines.Add(row);
            }

            _isFullVoid = false;
            if (Preview.IsAlreadyVoided)
                _isFullVoid = false;
            _cashRefund = "0";
            _cardRefund = "0";
            ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(true), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
            AllCashCommand = new RelayCommand(_ => { CashRefund = MoneyClp.Format(RefundTotalMinor); CardRefund = "0"; }, _ => RefundTotalMinor > 0);
            AllCardCommand = new RelayCommand(_ => { CardRefund = MoneyClp.Format(RefundTotalMinor); CashRefund = "0"; }, _ => RefundTotalMinor > 0);
            ZeroRefundCommand = new RelayCommand(_ => { CashRefund = "0"; CardRefund = "0"; }, _ => true);
            SplitHalfCommand = new RelayCommand(_ =>
            {
                if (RefundTotalMinor <= 0) return;
                var half = RefundTotalMinor / 2;
                CashRefund = MoneyClp.Format(half);
                CardRefund = MoneyClp.Format(RefundTotalMinor - half);
            }, _ => RefundTotalMinor > 0);
        }

        public RefundPreviewModel Preview { get; }
        public ObservableCollection<RefundLineRow> Lines { get; } = new ObservableCollection<RefundLineRow>();

        public string SaleCodeText => Preview.OriginalSaleCode;
        public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Preview.OriginalCreatedAtMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        public string OriginalTotalText => MoneyClp.Format(Preview.OriginalTotalMinor);

        public bool IsFullVoid
        {
            get => _isFullVoid;
            set
            {
                if (value && !IsFullVoidEnabled) return;
                if (_isFullVoid == value) return;
                _isFullVoid = value;
                if (_isFullVoid)
                {
                    foreach (var x in Lines)
                        x.QtyToRefund = x.RemainingQty;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPartialReturn));
                OnAmountsChanged();
            }
        }

        public bool IsPartialReturn
        {
            get => !_isFullVoid;
            set => IsFullVoid = !value;
        }

        public bool IsFullVoidEnabled => !Preview.IsAlreadyVoided;

        public string CashRefund
        {
            get => _cashRefund;
            set
            {
                _cashRefund = value ?? string.Empty;
                OnPropertyChanged();
                OnAmountsChanged();
            }
        }

        public string CardRefund
        {
            get => _cardRefund;
            set
            {
                _cardRefund = value ?? string.Empty;
                OnPropertyChanged();
                OnAmountsChanged();
            }
        }

        public string Reason
        {
            get => _reason;
            set { _reason = value ?? string.Empty; OnPropertyChanged(); }
        }

        public long RefundTotalMinor
        {
            get
            {
                if (IsFullVoid)
                    return Lines.Sum(x => (long)x.RemainingQty * x.UnitPriceMinor);
                return Lines.Where(x => x.QtyToRefund > 0).Sum(x => (long)x.QtyToRefund * x.UnitPriceMinor);
            }
        }

        public string RefundTotalText => MoneyClp.Format(RefundTotalMinor);
        public long RemainingRefundableMinor => Preview.MaxRefundableMinor;
        public string RemainingRefundableText => MoneyClp.Format(RemainingRefundableMinor);
        public long AlreadyRefundedMinor => Preview.OriginalTotalMinor - Preview.MaxRefundableMinor;
        public string AlreadyRefundedText => MoneyClp.Format(AlreadyRefundedMinor);
        public int CashMinor => MoneyClp.Parse(CashRefund);
        public int CardMinor => MoneyClp.Parse(CardRefund);
        public bool IsValid => RefundTotalMinor > 0 && CashMinor >= 0 && CardMinor >= 0 && (long)CashMinor + (long)CardMinor == RefundTotalMinor;
        public long MissingMinor
        {
            get
            {
                if (CashMinor < 0 || CardMinor < 0) return RefundTotalMinor;
                var delta = RefundTotalMinor - ((long)CashMinor + (long)CardMinor);
                return delta > 0 ? delta : 0;
            }
        }
        public string MissingText => IsValid ? string.Empty : "Manca: " + MoneyClp.Format(MissingMinor);

        public string ScanMessage
        {
            get => _scanMessage;
            private set { _scanMessage = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(HasScanMessage)); }
        }

        /// <summary>True se c'è un messaggio da mostrare (es. "Quantità massima già resa"). Usato per Visibility del TextBlock.</summary>
        public bool HasScanMessage => !string.IsNullOrEmpty(_scanMessage);

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand AllCashCommand { get; }
        public ICommand AllCardCommand { get; }
        public ICommand SplitHalfCommand { get; }
        public ICommand ZeroRefundCommand { get; }

        public event Action<bool> RequestClose;

        /// <summary>Scansiona barcode: se presente una volta incrementa Da rendere; se già al massimo mostra messaggio.</summary>
        public void ApplyBarcodeScan(string barcode)
        {
            ScanMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(barcode)) return;
            var trimmed = barcode.Trim();
            var matches = Lines.Where(l => string.Equals(l.Barcode?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return;
            foreach (var line in matches)
            {
                if (line.RemainingQty <= 0) continue;
                if (line.QtyToRefund >= line.RemainingQty)
                {
                    ScanMessage = "Quantità massima già resa.";
                    return;
                }
                line.QtyToRefund = line.QtyToRefund + 1;
                return;
            }
        }
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

        public RefundCreateRequest BuildRequest()
        {
            var request = new RefundCreateRequest
            {
                OriginalSaleId = Preview.OriginalSaleId,
                IsFullVoid = IsFullVoid,
                Payment = new RefundPaymentInfo
                {
                    CashMinor = (long)CashMinor,
                    CardMinor = (long)CardMinor
                },
                Reason = (Reason ?? string.Empty).Trim()
            };

            foreach (var line in Lines)
            {
                if (IsFullVoid)
                {
                    if (line.RemainingQty <= 0) continue;
                    request.Lines.Add(new RefundLineRequest
                    {
                        OriginalLineId = line.OriginalLineId,
                        Barcode = line.Barcode ?? string.Empty,
                        Name = line.Name ?? string.Empty,
                        UnitPriceMinor = line.UnitPriceMinor,
                        QtyToRefund = line.RemainingQty
                    });
                    continue;
                }

                if (line.QtyToRefund <= 0) continue;
                request.Lines.Add(new RefundLineRequest
                {
                    OriginalLineId = line.OriginalLineId,
                    Barcode = line.Barcode ?? string.Empty,
                    Name = line.Name ?? string.Empty,
                    UnitPriceMinor = line.UnitPriceMinor,
                    QtyToRefund = line.QtyToRefund
                });
            }

            return request;
        }

        private void OnAmountsChanged()
        {
            OnPropertyChanged(nameof(RefundTotalMinor));
            OnPropertyChanged(nameof(RefundTotalText));
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(MissingMinor));
            OnPropertyChanged(nameof(MissingText));
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AllCashCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AllCardCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SplitHalfCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public sealed class RefundLineRow : INotifyPropertyChanged
        {
            private int _qtyToRefund;

            public long OriginalLineId { get; set; }
            public string Barcode { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public long UnitPriceMinor { get; set; }
            public int SoldQty { get; set; }
            public int RefundedQty { get; set; }
            public int RemainingQty { get; set; }

            public int QtyToRefund
            {
                get => _qtyToRefund;
                set
                {
                    var next = value;
                    if (next < 0) next = 0;
                    if (next > RemainingQty) next = RemainingQty;
                    if (_qtyToRefund == next) return;
                    _qtyToRefund = next;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RefundLineTotalMinor));
                    OnPropertyChanged(nameof(RefundLineTotalDisplay));
                }
            }

            public long RefundLineTotalMinor => (long)QtyToRefund * UnitPriceMinor;
            public string RefundLineTotalDisplay => MoneyClp.Format(RefundLineTotalMinor);
            public string UnitPriceDisplay => MoneyClp.Format(UnitPriceMinor);

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string name = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
