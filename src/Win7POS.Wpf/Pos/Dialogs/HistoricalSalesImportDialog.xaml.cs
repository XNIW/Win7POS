using System.ComponentModel;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class HistoricalSalesImportDialog : DialogShellWindow
    {
        private readonly HistoricalSalesImportViewModel _viewModel;
        public HistoricalSalesImportDialog(HistoricalSalesImportViewModel viewModel)
        {
            InitializeComponent(); _viewModel = viewModel; _viewModel.OwnerWindow = this; DataContext = viewModel;
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 720, minHeight: 520, maxWidthPercent: 0.96, maxHeightPercent: 0.94, allowResize: true);
            Closing += OnClosing;
            Closed += (_, __) => { _viewModel.Dispose(); DataContext = null; };
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_viewModel.IsBusy) return;
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
        }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
