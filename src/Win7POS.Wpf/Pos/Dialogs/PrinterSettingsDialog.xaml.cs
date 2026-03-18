using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PrinterSettingsDialog : DialogShellWindow
    {
        public PrinterSettingsViewModel ViewModel { get; }

        public PrinterSettingsDialog(PrinterSettingsViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
        }
    }
}
