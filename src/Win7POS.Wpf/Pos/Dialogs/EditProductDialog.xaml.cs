using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class EditProductDialog : Window
    {
        public EditProductViewModel ViewModel { get; }

        public EditProductDialog(string barcode, string productName, long unitPriceMinor)
        {
            InitializeComponent();
            ViewModel = new EditProductViewModel(barcode, productName, unitPriceMinor);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;

            Loaded += (_, __) =>
            {
                Keyboard.Focus(PriceBox);
                PriceBox.SelectAll();
            };
        }

        private void PriceBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ViewModel.IsValid)
            {
                ViewModel.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
