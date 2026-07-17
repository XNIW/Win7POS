using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Win7POS.Core.Pos;
using Win7POS.Core.Util;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.CustomerDisplay
{
    public sealed class CustomerDisplayViewModel : INotifyPropertyChanged
    {
        private CustomerDisplaySnapshot _snapshot = CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
        private CustomerDisplaySettings _settings = CustomerDisplaySettings.CreateDefault(2);
        private CustomerDisplayLayout _layout = CustomerDisplayLayoutPolicy.Determine(1024, 768);

        public ObservableCollection<CustomerDisplayLineRow> Lines { get; } = new ObservableCollection<CustomerDisplayLineRow>();

        public string ShopName => _settings.ShowShopName ? _snapshot.ShopName : string.Empty;
        public string StateMessage => Text("customerDisplay.state." + MessageSuffix());
        public string ItemCountText => PosLocalization.Current.FormatForLanguage(LanguageCode(), "customerDisplay.itemCount", _snapshot.ItemCount);
        public string SubtotalText => MoneyClp.Format(_snapshot.Subtotal);
        public string DiscountText => MoneyClp.Format(_snapshot.Discount);
        public string TotalText => MoneyClp.Format(_snapshot.Total);
        public string PaidText => MoneyClp.Format(_snapshot.Paid);
        public string ChangeText => MoneyClp.Format(_snapshot.Change);
        public string UnitPriceHeader => Text("customerDisplay.unitPrice");
        public string LineTotalHeader => Text("customerDisplay.lineTotal");
        public string ItemHeader => Text("customerDisplay.item");
        public string QuantityHeader => Text("customerDisplay.quantity");
        public string SubtotalLabel => Text("customerDisplay.subtotal");
        public string DiscountLabel => Text("customerDisplay.discount");
        public string TotalLabel => Text("customerDisplay.total");
        public string PaidLabel => Text("customerDisplay.paid");
        public string ChangeLabel => Text("customerDisplay.change");

        public bool HasLines => Lines.Count > 0 && _snapshot.State == CustomerDisplayState.CartActive;
        public bool ShowStateMessage => !HasLines || _snapshot.State == CustomerDisplayState.Payment;
        public bool ShowLandscapeHeader => HasLines && IsLandscape;
        public bool ShowSubtotal => _settings.ShowSubtotal && HasLines;
        public bool ShowDiscount => _settings.ShowDiscount && _snapshot.Discount > 0 && HasLines;
        public bool ShowItemCount => _settings.ShowItemCount && HasLines;
        public bool ShowPaid => _snapshot.State == CustomerDisplayState.Completed && _snapshot.Paid > 0;
        public bool ShowChange => _snapshot.State == CustomerDisplayState.Completed && _snapshot.Change > 0;
        public bool IsPortrait => _layout.Mode == CustomerDisplayLayoutMode.Portrait;
        public bool IsLandscape => !IsPortrait;
        public double RowHeight => _layout.RowHeight;
        public double TotalFontSize => _layout.TotalFontSize * UserFontScale();
        public double BodyFontSize => 18 * _layout.FontScale * UserFontScale();
        public Thickness ContentPadding => new Thickness(_layout.Spacing);
        public Brush Background => BrushFor("background");
        public Brush Foreground => BrushFor("foreground");
        public Brush MutedForeground => BrushFor("muted");
        public Brush Accent => BrushFor("accent");
        public string LastChangedLineKey => _snapshot.LastChangedLineKey;

        public void Apply(
            CustomerDisplaySnapshot snapshot,
            CustomerDisplaySettings settings,
            CustomerDisplayLayout layout)
        {
            _snapshot = snapshot ?? CustomerDisplayProjection.Empty(DateTimeOffset.UtcNow);
            _settings = settings?.Clone() ?? CustomerDisplaySettings.CreateDefault(2);
            _layout = layout ?? CustomerDisplayLayoutPolicy.Determine(1024, 768);

            Lines.Clear();
            foreach (var line in _snapshot.Lines)
            {
                Lines.Add(new CustomerDisplayLineRow(
                    line,
                    _settings.ShowBarcode,
                    _settings.ShowUnitPrice,
                    _settings.ShowLineTotal,
                    string.Equals(line.StableKey, _snapshot.LastChangedLineKey, StringComparison.Ordinal)));
            }

            RaiseAll();
        }

        private string MessageSuffix()
        {
            switch (_snapshot.State)
            {
                case CustomerDisplayState.Payment: return "payment";
                case CustomerDisplayState.Completed: return "thankYou";
                case CustomerDisplayState.Locked: return "locked";
                case CustomerDisplayState.Unavailable: return "unavailable";
                default: return "welcome";
            }
        }

        private string Text(string key) => PosLocalization.Current.TextForLanguage(LanguageCode(), key);

        private string LanguageCode()
        {
            switch (_settings.CustomerLanguage)
            {
                case CustomerDisplayLanguage.IT: return "it";
                case CustomerDisplayLanguage.ES: return "es";
                case CustomerDisplayLanguage.ZH: return "zh-CN";
                case CustomerDisplayLanguage.EN: return "en";
                default: return PosLocalization.Current.CurrentLanguage;
            }
        }

        private double UserFontScale()
        {
            switch (_settings.FontScale)
            {
                case CustomerDisplayFontScale.Small: return 0.88;
                case CustomerDisplayFontScale.Large: return 1.18;
                default: return 1.0;
            }
        }

        private Brush BrushFor(string role)
        {
            if (_settings.Theme == CustomerDisplayTheme.HighContrast)
            {
                if (role == "background") return Brushes.Black;
                if (role == "accent") return Brushes.Yellow;
                if (role == "muted") return Brushes.White;
                return Brushes.White;
            }

            if (_settings.Theme == CustomerDisplayTheme.Light)
            {
                if (role == "background") return new SolidColorBrush(Color.FromRgb(247, 245, 250));
                if (role == "accent") return new SolidColorBrush(Color.FromRgb(75, 46, 103));
                if (role == "muted") return new SolidColorBrush(Color.FromRgb(94, 82, 104));
                return new SolidColorBrush(Color.FromRgb(36, 31, 42));
            }

            if (role == "background") return new SolidColorBrush(Color.FromRgb(24, 20, 29));
            if (role == "accent") return new SolidColorBrush(Color.FromRgb(220, 183, 255));
            if (role == "muted") return new SolidColorBrush(Color.FromRgb(203, 194, 211));
            return Brushes.White;
        }

        private void RaiseAll()
        {
            OnPropertyChanged(string.Empty);
            OnPropertyChanged(nameof(Lines));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class CustomerDisplayLineRow
    {
        public CustomerDisplayLineRow(
            CustomerDisplayLine line,
            bool showBarcode,
            bool showUnitPrice,
            bool showLineTotal,
            bool isHighlighted)
        {
            StableKey = line.StableKey;
            Name = line.Name;
            Barcode = showBarcode ? line.Barcode : string.Empty;
            QuantityText = line.Quantity.ToString(System.Globalization.CultureInfo.CurrentCulture);
            UnitPriceText = MoneyClp.Format(line.UnitPrice);
            LineTotalText = MoneyClp.Format(line.LineTotal);
            ShowBarcode = showBarcode && !string.IsNullOrWhiteSpace(line.Barcode);
            ShowUnitPrice = showUnitPrice;
            ShowLineTotal = showLineTotal;
            IsHighlighted = isHighlighted;
        }

        public string StableKey { get; }
        public string Name { get; }
        public string Barcode { get; }
        public string QuantityText { get; }
        public string UnitPriceText { get; }
        public string LineTotalText { get; }
        public bool ShowBarcode { get; }
        public bool ShowUnitPrice { get; }
        public bool ShowLineTotal { get; }
        public bool IsHighlighted { get; }
    }
}
