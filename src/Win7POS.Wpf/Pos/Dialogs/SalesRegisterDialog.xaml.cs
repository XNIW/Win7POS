using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SalesRegisterDialog : Window
    {
        public SalesRegisterDialog(SalesRegisterViewModel viewModel)
        {
            InitializeComponent();
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
