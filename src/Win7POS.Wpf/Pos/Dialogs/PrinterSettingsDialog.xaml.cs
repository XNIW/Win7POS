using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PrinterSettingsDialog : Window
    {
        public PrinterSettingsViewModel ViewModel { get; }

        public PrinterSettingsDialog(PrinterSettingsViewModel viewModel)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.5, heightPercent: 0.55, minWidth: 480, minHeight: 380);
            ViewModel = viewModel;
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
        }
    }
}
