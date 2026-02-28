using System.Windows.Controls;

namespace Win7POS.Wpf.Products
{
    public partial class ProductsView : UserControl
    {
        public ProductsView()
        {
            InitializeComponent();
            DataContext = new ProductsViewModel();
        }
    }
}
