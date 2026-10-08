using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public partial class ProductPriceHistoryDialog : DialogShellWindow
    {
        private TextBox _priceFocusBeforeBusy;
        public ProductPriceHistoryViewModel ViewModel => (ProductPriceHistoryViewModel)DataContext;

        public ProductPriceHistoryDialog(ProductPriceHistoryViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.PriceInputFocusRequested += FocusPriceInput;
            vm.PropertyChanged += ViewModel_PropertyChanged;
            Closed += (s, e) =>
            {
                vm.PriceInputFocusRequested -= FocusPriceInput;
                vm.PropertyChanged -= ViewModel_PropertyChanged;
                _priceFocusBeforeBusy = null;
            };
            Loaded += async (s, e) =>
            {
                await vm.LoadAsync().ConfigureAwait(true);
            };
        }

        private void FocusPriceInput(bool retail)
        {
            var input = retail ? RetailPriceInput : PurchasePriceInput;
            input.Focus();
            input.SelectAll();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ProductPriceHistoryViewModel.IsBusy)) return;
            if (ViewModel.IsBusy)
            {
                _priceFocusBeforeBusy = RetailPriceInput.IsKeyboardFocusWithin ? RetailPriceInput
                    : PurchasePriceInput.IsKeyboardFocusWithin ? PurchasePriceInput : null;
                return;
            }
            var input = _priceFocusBeforeBusy;
            _priceFocusBeforeBusy = null;
            if (input == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && input.IsEnabled) input.Focus();
            }), DispatcherPriority.Input);
        }

        public static void ShowDialog(Window owner, long productId, string barcode, string name, long currentRetail, int currentPurchase, bool canEditPrices)
        {
            var service = new ProductsWorkflowService();
            var vm = new ProductPriceHistoryViewModel(productId, barcode, name, currentRetail, currentPurchase, service, canEditPrices);
            var dlg = new ProductPriceHistoryDialog(vm)
            {
                Owner = owner ?? DialogOwnerHelper.GetSafeOwner()
            };
            WindowSizingHelper.CapMaxHeightToOwner(dlg);
            dlg.ShowDialog();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
