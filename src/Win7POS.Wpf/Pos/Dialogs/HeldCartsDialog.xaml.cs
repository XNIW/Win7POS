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
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.4, heightPercent: 0.55, minWidth: 420, minHeight: 400);
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
