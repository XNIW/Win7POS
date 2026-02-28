using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DailyReportDialog : Window
    {
        public DailyReportDialog(DailyReportViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
