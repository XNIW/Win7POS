using System;
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
            var vm = PosViewComposition.CreateViewModel();
            vm.FocusBarcodeRequested += FocusBarcode;
            DataContext = vm;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            FocusBarcode();
            (DataContext as PosViewModel)?.StartInitialize();
        }

        private void CartListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as PosViewModel;
            if (vm == null) return;
            if (vm.OpenEditProductCommand?.CanExecute(null) == true)
                vm.OpenEditProductCommand.Execute(null);
        }

        private void BarcodeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            var vm = DataContext as PosViewModel;
            if (vm == null) return;

            if (string.IsNullOrWhiteSpace(vm.BarcodeInput))
            {
                if (vm.PayCommand?.CanExecute(null) == true && vm.CartItems.Count > 0)
                {
                    vm.PayCommand.Execute(null);
                    e.Handled = true;
                }
                return;
            }

            if (vm.AddBarcodeCommand?.CanExecute(null) == true)
            {
                vm.AddBarcodeCommand.Execute(null);
                e.Handled = true;
            }
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

            if (e.Key == Key.Up)
            {
                MoveSelection(-1);
                FocusBarcode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down)
            {
                MoveSelection(1);
                FocusBarcode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Delete)
            {
                ExecuteIfCan(vm.RemoveLineCommand);
                FocusBarcode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Add || e.Key == Key.OemPlus)
            {
                ExecuteIfCan(vm.OpenChangeQuantityCommand);
                FocusBarcode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
            {
                ExecuteIfCan(vm.DecreaseQtyCommand);
                FocusBarcode();
                e.Handled = true;
            }
        }

        private void MoveSelection(int delta)
        {
            if (CartListBox == null || CartListBox.Items.Count == 0) return;
            var index = CartListBox.SelectedIndex;
            if (index < 0) index = CartListBox.Items.Count - 1;
            index += delta;
            if (index < 0) index = 0;
            if (index >= CartListBox.Items.Count) index = CartListBox.Items.Count - 1;
            CartListBox.SelectedIndex = index;
        }

        private void CartListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FocusBarcode();
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

    /// <summary>Mini Composition Root: assembla le dipendenze per PosViewModel.</summary>
    internal static class PosViewComposition
    {
        public static PosViewModel CreateViewModel()
        {
            var logger = new Infrastructure.FileLogger();
            var service = new PosWorkflowService();
            return new PosViewModel(service, logger);
        }
    }
}
