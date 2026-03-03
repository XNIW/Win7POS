using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DailyReportDialog : Window
    {
        public DailyReportDialog(DailyReportViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.75, heightPercent: 0.7, minWidth: 700, minHeight: 500);
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
