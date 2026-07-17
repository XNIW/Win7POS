using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DailyReportDialog : DialogShellWindow
    {
        private readonly DailyReportViewModel _viewModel;

        public DailyReportDialog(DailyReportViewModel vm)
        {
            _viewModel = vm;
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(
                this,
                minWidth: 720,
                minHeight: 520,
                maxWidthPercent: 0.96,
                maxHeightPercent: 0.96,
                allowResize: true);
            DataContext = vm;
            Closed += OnClosed;
        }

        private void OnClosed(object sender, System.EventArgs e)
        {
            Closed -= OnClosed;
            _viewModel.Dispose();
            DataContext = null;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
