using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class AddProductDialog : Window
    {
        public AddProductViewModel ViewModel { get; }

        public AddProductDialog(string barcode)
        {
            InitializeComponent();
            ViewModel = new AddProductViewModel(barcode);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
            Loaded += (_, __) =>
            {
                Keyboard.Focus(NameBox);
                NameBox.SelectAll();
            };
        }
    }
}
