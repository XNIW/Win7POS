using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos
{
    public partial class PosView : UserControl
    {
        public PosView()
        {
            InitializeComponent();
            DataContext = new PosViewModel();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(BarcodeBox);
        }
    }
}
