using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Import
{
    public partial class ApplyConfirmDialog : DialogShellWindow
    {
        public ApplyConfirmDialog(string title, string message)
        {
            InitializeComponent();
            Title = title ?? PosLocalization.T("common.confirm");
            TitleText.Text = title ?? PosLocalization.T("common.confirm");
            MessageText.Text = message ?? "";
        }

        public static bool ShowConfirm(Window owner, string title, string message)
        {
            var dlg = new ApplyConfirmDialog(title ?? PosLocalization.T("common.confirm"), message ?? "")
            {
                Owner = DialogOwnerHelper.GetSafeOwner(owner)
            };
            return dlg.ShowDialog() == true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
