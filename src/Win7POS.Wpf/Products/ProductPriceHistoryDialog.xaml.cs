using System;
using System.Threading.Tasks;
using System.Windows;

namespace Win7POS.Wpf.Products
{
    public partial class ProductPriceHistoryDialog : Window
    {
        public ProductPriceHistoryViewModel ViewModel => (ProductPriceHistoryViewModel)DataContext;

        public ProductPriceHistoryDialog(ProductPriceHistoryViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            Loaded += async (s, e) =>
            {
                await vm.LoadAsync().ConfigureAwait(true);
            };
        }

        public static void ShowDialog(Window owner, long productId, string barcode, string name, int currentRetail, int currentPurchase)
        {
            var service = new ProductsWorkflowService();
            var vm = new ProductPriceHistoryViewModel(productId, barcode, name, currentRetail, currentPurchase, service);
            var dlg = new ProductPriceHistoryDialog(vm)
            {
                Owner = owner
            };
            dlg.ShowDialog();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
