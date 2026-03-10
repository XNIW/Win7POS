using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DailyReportDialog : Window
    {
        public DailyReportDialog(DailyReportViewModel vm)
        {
            InitializeComponent();
            // Dimensioni: XAML Width=1180 Height=760 MinWidth=1000 MinHeight=660 (nessun Max per ridimensionamento). Tab Giornaliero/Storico, KPI compatti.
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
