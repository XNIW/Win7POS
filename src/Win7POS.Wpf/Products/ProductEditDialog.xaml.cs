using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Win7POS.Core.Models;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public partial class ProductEditDialog : Window
    {
        public ProductEditViewModel ViewModel => (ProductEditViewModel)DataContext;

        public ProductEditDialog(ProductEditViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 560, minHeight: 480, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
            DataContext = vm;
            vm.RequestClose += OnRequestClose;
        }

        public static async Task<bool> ShowAsync(ProductEditMode mode, ProductDetailsRow source, ProductsWorkflowService service)
        {
            var vm = new ProductEditViewModel(mode, source, service);
            var categories = await service.GetCategoriesAsync().ConfigureAwait(true);
            var suppliers = await service.GetSuppliersAsync().ConfigureAwait(true);
            vm.SetCategories(categories);
            vm.SetSuppliers(suppliers);
            vm.SetSelectionFromSource(source);

            if (mode == ProductEditMode.New)
            {
                vm.PriceText = string.Empty;
                vm.PurchasePriceText = string.Empty;
            }

            var dlg = new ProductEditDialog(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            return dlg.ShowDialog() == true;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (PriceBox != null)
            {
                PriceBox.Focus();
                PriceBox.SelectAll();
            }
        }

        private void PriceBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (ViewModel?.ConfirmCommand?.CanExecute(null) == true)
            {
                ViewModel.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void OnRequestClose(bool success)
        {
            ViewModel.RequestClose -= OnRequestClose;
            DialogResult = success;
            Close();
        }
    }
}
