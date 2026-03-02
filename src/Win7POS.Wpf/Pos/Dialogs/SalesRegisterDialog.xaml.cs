using System.Windows;

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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SalesRegisterViewModel;
            if (vm != null && vm.LoadCommand.CanExecute(null))
                vm.LoadCommand.Execute(null);
        }
    }
}
