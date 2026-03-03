using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class AddProductDialog : Window
    {
        public AddProductViewModel ViewModel { get; }

        public AddProductDialog(string barcode, PosWorkflowService service = null, bool focusRetailPrice = false)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.45, heightPercent: 0.65, minWidth: 420, minHeight: 420);
            ViewModel = new AddProductViewModel(barcode);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
            _focusRetailPrice = focusRetailPrice;

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

                if (_focusRetailPrice)
                {
                    Keyboard.Focus(RetailPriceBox);
                    RetailPriceBox.SelectAll();
                }
                else
                {
                    Keyboard.Focus(NameBox);
                    NameBox.SelectAll();
                }
            };
        }

        private readonly bool _focusRetailPrice;

        private void RetailPriceBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ViewModel.IsValid)
            {
                ViewModel.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
