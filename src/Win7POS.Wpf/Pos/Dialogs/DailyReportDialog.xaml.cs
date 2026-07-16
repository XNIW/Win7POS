using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DailyReportDialog : DialogShellWindow
    {
        public DailyReportDialog(DailyReportViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(
                this,
                minWidth: 720,
                minHeight: 520,
                maxWidthPercent: 0.96,
                maxHeightPercent: 0.96,
                allowResize: true);
            DataContext = vm;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
