using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PrinterSettingsDialog : DialogShellWindow
    {
        public PrinterSettingsViewModel ViewModel { get; }

        public PrinterSettingsDialog(PrinterSettingsViewModel viewModel)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 520, minHeight: 320, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
            ViewModel = viewModel;
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
        }
    }
}
