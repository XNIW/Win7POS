using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PaymentDialog : Window
    {
        public PaymentViewModel ViewModel { get; }

        public PaymentDialog(int totalDueMinor)
        {
            InitializeComponent();
            ViewModel = new PaymentViewModel(totalDueMinor);
            ViewModel.RequestClose += OnRequestClose;
            DataContext = ViewModel;
        }

        private void OnRequestClose(bool ok)
        {
            DialogResult = ok;
        }
    }
}
