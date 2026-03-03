using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SalesRegisterDialog : Window
    {
        public SalesRegisterDialog(SalesRegisterViewModel viewModel)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.85, heightPercent: 0.8, minWidth: 900, minHeight: 600);
            DataContext = viewModel;
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
        }
    }
}
