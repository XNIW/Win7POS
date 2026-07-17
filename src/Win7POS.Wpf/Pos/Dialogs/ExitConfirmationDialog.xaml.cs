using System.Windows;
using Win7POS.Core.Util;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public enum ExitConfirmationChoice { Cancel, Minimize, CloseApplication }

    public partial class ExitConfirmationDialog : DialogShellWindow
    {
        public ExitConfirmationDialog(int itemCount, long total)
        {
            InitializeComponent();
            CartWarning.Visibility = itemCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            CartSummaryText.Text = PosLocalization.Current.Format(
                "exit.cartSummary",
                itemCount,
                MoneyClp.Format(total));
        }

        public ExitConfirmationChoice Choice { get; private set; } = ExitConfirmationChoice.Cancel;

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            Choice = ExitConfirmationChoice.Minimize;
            Close();
        }

        private void OnCloseApplicationClick(object sender, RoutedEventArgs e)
        {
            Choice = ExitConfirmationChoice.CloseApplication;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Choice = ExitConfirmationChoice.Cancel;
            Close();
        }
    }
}
