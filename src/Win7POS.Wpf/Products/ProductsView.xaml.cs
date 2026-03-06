using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Win7POS.Wpf.Products
{
    public partial class ProductsView : UserControl
    {
        public ProductsView()
        {
            InitializeComponent();
            DataContext = new ProductsViewModel();
        }

        private void ProductsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is ProductsViewModel vm && vm.SelectedProduct != null && vm.EditProductCommand?.CanExecute(null) == true)
                vm.EditProductCommand.Execute(null);
        }
    }
}
