using System.Windows;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SalesRegisterDialog : Window
    {
        public SalesRegisterDialog(PosViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as PosViewModel;
            if (vm != null && vm.LoadRecentSalesCommand.CanExecute(null))
                vm.LoadRecentSalesCommand.Execute(null);
        }
    }
}
