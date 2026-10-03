using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Core.Hardware;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos
{
    public partial class PosView : UserControl
    {
        public event Action CatalogWarningDetailsRequested;
        private DispatcherOperation _focusOperation;
        private DispatcherOperation _scrollOperation;
        private ListBox _scrollTarget;
        private bool _barcodeComposing;

        public PosView()
        {
            InitializeComponent();
            var vm = PosViewComposition.CreateViewModel();
            vm.FocusBarcodeRequested += FocusBarcode;
            vm.StatusToastDetailsRequested += () => CatalogWarningDetailsRequested?.Invoke();
            DataContext = vm;
            TextCompositionManager.AddPreviewTextInputStartHandler(BarcodeBox, (sender, args) => _barcodeComposing = true);
            TextCompositionManager.AddPreviewTextInputHandler(BarcodeBox, (sender, args) => _barcodeComposing = false);
            BarcodeBox.LostKeyboardFocus += (sender, args) => _barcodeComposing = false;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Cancel only this view's replaceable visual work, never input,
            // workflow commands or operations owned by WPF.
            _focusOperation?.Abort();
            _scrollOperation?.Abort();
            _focusOperation = _scrollOperation = null;
            _scrollTarget = null;
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

            FocusBarcode();
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

        private void BarcodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Escape)) _barcodeComposing = false;
            if (_barcodeComposing || e.Key == Key.ImeProcessed || e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.Enter && key != Key.Tab) return;

            var vm = DataContext as PosViewModel;
            if (vm == null) return;
            var terminator = key == Key.Enter ? ScannerTerminator.Enter : ScannerTerminator.Tab;
            if (!vm.AcceptsScannerTerminator(terminator)) return;
            if (!string.IsNullOrWhiteSpace(vm.BarcodeInput)) e.Handled = true;

            if (string.IsNullOrWhiteSpace(vm.BarcodeInput))
            {
                if (key == Key.Enter && vm.PayCommand?.CanExecute(null) == true && vm.CartItems.Count > 0)
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
            var vm = DataContext as PosViewModel;
            var activeList = vm?.IsCartGridView == true
                ? CartGridListBox
                : CartListBox;
            if (activeList == null || activeList.Items.Count == 0) return;

            var count = activeList.Items.Count;
            var index = activeList.SelectedIndex;

            if (index < 0)
                index = delta >= 0 ? -1 : 0;

            index = (index + delta + count) % count;
            activeList.SelectedIndex = index;
            activeList.ScrollIntoView(activeList.SelectedItem);
        }

        private void CartListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var list = sender as ListBox;
            if (list?.IsVisible != true) return;
            QueueSelectionScroll(list);
            FocusBarcode();
        }

        private void CartListBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is ListBox list && list.IsVisible) QueueSelectionScroll(list);
        }

        private void QueueSelectionScroll(ListBox list)
        {
            _scrollTarget = list;
            if (_scrollOperation?.Status == DispatcherOperationStatus.Pending) return;
            _scrollOperation = Dispatcher.BeginInvoke((Action)(() =>
            {
                _scrollOperation = null;
                var target = _scrollTarget;
                _scrollTarget = null;
                if (IsLoaded && target?.IsVisible == true && target.SelectedItem != null)
                    target.ScrollIntoView(target.SelectedItem);
            }), DispatcherPriority.Input);
        }

        private void ExecuteIfCan(ICommand command)
        {
            if (command == null) return;
            if (!command.CanExecute(null)) return;
            command.Execute(null);
        }

        private void FocusBarcode()
        {
            if (!IsLoaded || _focusOperation?.Status == DispatcherOperationStatus.Pending) return;
            _focusOperation = Dispatcher.BeginInvoke((Action)(() =>
            {
                _focusOperation = null;
                var window = Window.GetWindow(this);
                if (!IsLoaded || !IsEnabled || !BarcodeBox.IsVisible || window?.IsActive != true) return;
                Keyboard.Focus(BarcodeBox);
                BarcodeBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        public void RestoreScannerFocus()
        {
            FocusBarcode();
        }
    }

    /// <summary>Mini Composition Root: assembla le dipendenze per PosViewModel.</summary>
    internal static class PosViewComposition
    {
        public static PosViewModel CreateViewModel()
        {
            var logger = new Infrastructure.FileLogger("PosView");
            var service = new PosWorkflowService();
            var options = PosDbOptions.Default();
            var factory = new SqliteConnectionFactory(options);
            var userRepo = new UserRepository(factory);
            var securityRepo = new SecurityRepository(factory);
            IOperatorSession operatorSession = OperatorSessionHolder.Current;
            if (operatorSession == null)
            {
                operatorSession = new OperatorSession(userRepo, securityRepo);
                OperatorSessionHolder.Current = operatorSession;
            }
            var permissionService = new PermissionService(operatorSession);
            var overrideAuthService = new OverrideAuthService(userRepo, operatorSession);
            return new PosViewModel(service, logger, permissionService, operatorSession, overrideAuthService, userRepo);
        }
    }
}
