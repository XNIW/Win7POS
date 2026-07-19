using System;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PrinterSettingsDialog : DialogShellWindow
    {
        public PrinterSettingsViewModel ViewModel { get; }

        public PrinterSettingsDialog(PrinterSettingsViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            ViewModel.RequestClose += OnRequestClose;
            Closed += OnDialogClosed;
            DataContext = ViewModel;
        }

        private void OnRequestClose(bool ok)
        {
            DialogResult = ok;
        }

        private void OnDialogClosed(object sender, EventArgs e)
        {
            Closed -= OnDialogClosed;
            ViewModel.RequestClose -= OnRequestClose;
            DataContext = null;
            ViewModel.Dispose();
        }
    }
}
