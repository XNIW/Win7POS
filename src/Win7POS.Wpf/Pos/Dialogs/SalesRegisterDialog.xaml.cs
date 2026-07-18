using System;
using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SalesRegisterDialog : DialogShellWindow
    {
        private SalesRegisterViewModel _viewModel;

        public SalesRegisterDialog(SalesRegisterViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(
                this,
                minWidth: 720,
                minHeight: 520,
                maxWidthPercent: 0.96,
                maxHeightPercent: 0.96,
                allowResize: true);
            DataContext = viewModel;
            viewModel.RequestCloseDialog += OnRequestCloseDialog;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnRequestCloseDialog()
            => Dispatcher.BeginInvoke(new Action(() => { try { Close(); } catch { } }));

        private void OnClosed(object sender, EventArgs e)
        {
            Closed -= OnClosed;
            Loaded -= OnLoaded;
            _viewModel.RequestCloseDialog -= OnRequestCloseDialog;
            _viewModel.Dispose();
            _viewModel = null;
            DataContext = null;
            Content = null;
        }

        private void CodeSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is SalesRegisterViewModel vm && vm.LoadCommand.CanExecute(null))
            {
                vm.LoadCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SalesRegisterViewModel;
            if (vm != null && vm.LoadCommand.CanExecute(null))
                vm.LoadCommand.Execute(null);
            CodeSearchBox.Focus();
            CodeSearchBox.SelectAll();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            try { Close(); } catch { }
        }

        private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F5 && DataContext is SalesRegisterViewModel vm && vm.LoadCommand.CanExecute(null))
            {
                vm.LoadCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
