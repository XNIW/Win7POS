using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.ViewModels;

namespace Win7POS.Wpf
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => Keyboard.Focus(BarcodeBox);
        }

        private async void BarcodeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is PosViewModel vm)
            {
                await vm.OnBarcodeEnterAsync();
                e.Handled = true;
                Keyboard.Focus(BarcodeBox);
            }
        }
    }
}
