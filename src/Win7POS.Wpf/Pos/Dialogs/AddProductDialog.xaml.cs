using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class AddProductDialog : Window
    {
        public AddProductViewModel ViewModel { get; }

        public AddProductDialog(string barcode, PosWorkflowService service = null)
        {
            InitializeComponent();
            ViewModel = new AddProductViewModel(barcode);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;

            Loaded += async (_, __) =>
            {
                try
                {
                    if (service != null)
                    {
                        var suppliers = await service.GetSuppliersAsync().ConfigureAwait(true);
                        var categories = await service.GetCategoriesAsync().ConfigureAwait(true);
                        ViewModel.SetSuppliers(suppliers);
                        ViewModel.SetCategories(categories);
                    }
                    else
                    {
                        ViewModel.SetSuppliers(System.Array.Empty<Win7POS.Data.Repositories.SupplierListItem>());
                        ViewModel.SetCategories(System.Array.Empty<Win7POS.Data.Repositories.CategoryListItem>());
                    }
                }
                catch
                {
                    ViewModel.SetSuppliers(System.Array.Empty<Win7POS.Data.Repositories.SupplierListItem>());
                    ViewModel.SetCategories(System.Array.Empty<Win7POS.Data.Repositories.CategoryListItem>());
                }
                Keyboard.Focus(NameBox);
                NameBox.SelectAll();
            };
        }
    }
}
