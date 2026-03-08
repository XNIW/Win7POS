using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        private void CartRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as ListBoxItem;
            if (item == null) return;

            item.IsSelected = true;
            item.Focusable = false;

            // Non rubare il focus se il click è su un pulsante della riga (+, -, ✕, modifica prezzo): lasciamo che il comando del pulsante venga eseguito.
            if (IsClickFromButton(e))
                return;

            Dispatcher.BeginInvoke((Action)(() =>
            {
                FocusBarcode();
            }), DispatcherPriority.Input);
        }

        private static bool IsClickFromButton(MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is Button)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
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

            if (e.Key == Key.Oem2 || e.Key == Key.Divide)
            {
                if (vm.OpenDiscountCommand?.CanExecute(null) == true)
                {
                    vm.OpenDiscountCommand.Execute(null);
                    FocusBarcode();
                    e.Handled = true;
                }
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
                if (!string.IsNullOrWhiteSpace(vm.BarcodeInput) && vm.TryArmPendingQuantityFromBarcodeInput())
                {
                    FocusBarcode();
                    e.Handled = true;
                    return;
                }
                ExecuteIfCan(vm.OpenChangeQuantityCommand);
                FocusBarcode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
            {
                ExecuteIfCan(vm.RemoveLineCommand);
                FocusBarcode();
                e.Handled = true;
                return;
            }
        }

        private void MoveSelection(int delta)
        {
            if (CartListBox == null || CartListBox.Items.Count == 0) return;

            var count = CartListBox.Items.Count;
            var index = CartListBox.SelectedIndex;

            if (index < 0)
                index = delta >= 0 ? -1 : 0;

            index = (index + delta + count) % count;
            CartListBox.SelectedIndex = index;
            CartListBox.ScrollIntoView(CartListBox.SelectedItem);
        }

        private void CartListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CartListBox?.SelectedItem != null)
                CartListBox.ScrollIntoView(CartListBox.SelectedItem);
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
