using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Win7POS.Wpf.Pos
{
    public partial class PosView : UserControl
    {
        public PosView()
        {
            InitializeComponent();
            var vm = new PosViewModel();
            vm.FocusBarcodeRequested += FocusBarcode;
            DataContext = vm;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            FocusBarcode();
        }

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = DataContext as PosViewModel;
            if (vm == null) return;

            if (e.Key == Key.F2)
            {
                FocusBarcode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F4)
            {
                ExecuteIfCan(vm.PayCommand);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete)
            {
                ExecuteIfCan(vm.RemoveLineCommand);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Add || e.Key == Key.OemPlus)
            {
                ExecuteIfCan(vm.IncreaseQtyCommand);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
            {
                ExecuteIfCan(vm.DecreaseQtyCommand);
                e.Handled = true;
            }
        }

        private void ExecuteIfCan(ICommand command)
        {
            if (command == null) return;
            if (!command.CanExecute(null)) return;
            command.Execute(null);
        }

        private void FocusBarcode()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                Keyboard.Focus(BarcodeBox);
                BarcodeBox.SelectAll();
            }), DispatcherPriority.Input);
        }
    }
}
