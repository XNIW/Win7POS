using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class HeldCartsDialog : Window
    {
        public HeldCartsViewModel ViewModel { get; }

        public HeldCartsDialog(HeldCartsViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 520, minHeight: 400, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
            ViewModel = vm ?? throw new System.ArgumentNullException(nameof(vm));
            DataContext = ViewModel;
            ViewModel.RequestClose += recovered =>
            {
                DialogResult = recovered;
                Close();
            };
        }
    }
}
