using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class HeldCartsDialog : Window
    {
        public HeldCartsViewModel ViewModel { get; }

        public HeldCartsDialog(HeldCartsViewModel vm)
        {
            InitializeComponent();
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
