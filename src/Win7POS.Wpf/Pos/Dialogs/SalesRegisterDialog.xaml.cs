using System;
using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SalesRegisterDialog : Window
    {
        public SalesRegisterDialog(SalesRegisterViewModel viewModel)
        {
            InitializeComponent();
            // Dimensioni fisse da XAML (980x700, NoResize); niente sizing adattivo per evitare resize con molti scontrini
            DataContext = viewModel;
            viewModel.RequestCloseDialog += () => Dispatcher.BeginInvoke(new Action(() => { try { Close(); } catch { } }));
            Loaded += OnLoaded;
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
