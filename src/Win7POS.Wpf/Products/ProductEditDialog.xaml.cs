using System;
using System.Threading.Tasks;
using System.Windows;
using Win7POS.Core.Models;

namespace Win7POS.Wpf.Products
{
    public partial class ProductEditDialog : Window
    {
        public ProductEditViewModel ViewModel => (ProductEditViewModel)DataContext;

        public ProductEditDialog(ProductEditViewModel vm)
        {
            InitializeComponent();
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

            var dlg = new ProductEditDialog(vm)
            {
                Owner = Application.Current?.MainWindow
            };
            return dlg.ShowDialog() == true;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (Owner != null)
            {
                MaxHeight = Math.Max(520, Owner.ActualHeight - 60);
                MaxWidth = Math.Max(700, Owner.ActualWidth - 60);
            }
            UpdateLayout();
        }

        private void OnRequestClose(bool success)
        {
            ViewModel.RequestClose -= OnRequestClose;
            DialogResult = success;
            Close();
        }
    }
}
