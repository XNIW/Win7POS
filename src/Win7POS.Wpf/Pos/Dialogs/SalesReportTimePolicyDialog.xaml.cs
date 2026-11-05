using System.ComponentModel;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SalesReportTimePolicyDialog : DialogShellWindow
    {
        private readonly SalesReportTimePolicyViewModel _viewModel;
        public SalesReportTimePolicyDialog(SalesReportTimePolicyViewModel viewModel)
        {
            InitializeComponent(); _viewModel = viewModel; _viewModel.OwnerWindow = this; DataContext = viewModel;
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 560, minHeight: 420, maxWidthPercent: 0.96, maxHeightPercent: 0.94, allowResize: true);
            Closing += OnClosing;
            Closed += (_, __) => { _viewModel.Dispose(); DataContext = null; };
        }
        private void OnClosing(object sender, CancelEventArgs eventArgs)
        {
            if (!_viewModel.IsBusy) return;
            eventArgs.Cancel = true; _viewModel.CancelCommand.Execute(null);
        }
        private void Close_Click(object sender, RoutedEventArgs eventArgs) => Close();
    }
}
