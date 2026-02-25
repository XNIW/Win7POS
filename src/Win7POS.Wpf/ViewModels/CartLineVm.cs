using Win7POS.Wpf.ViewModels;

namespace Win7POS.Wpf.ViewModels
{
    public sealed class CartLineVm : ObservableObject
    {
        public long? ProductId { get; set; }

        public string Barcode { get; set; }
        public string Name { get; set; }

        private int _quantity = 1;
        public int Quantity
        {
            get => _quantity;
            set { if (SetProperty(ref _quantity, value)) RaisePropertyChanged(nameof(LineTotal)); }
        }

        public int UnitPrice { get; set; }
        public int LineTotal => Quantity * UnitPrice;
    }
}
