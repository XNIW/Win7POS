using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DailyReportDialog : Window
    {
        public DailyReportDialog(DailyReportViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as DailyReportViewModel;
            if (vm != null && vm.LoadCommand.CanExecute(null))
                vm.LoadCommand.Execute(null);
        }
    }
}
