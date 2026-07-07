using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PermissionDeniedDialog : DialogShellWindow
    {
        public PermissionDeniedDialog(string message)
        {
            InitializeComponent();
            MessageText.Text = message ?? string.Empty;
        }

        public bool SwitchRequested { get; private set; }

        public static bool ShowSwitchPrompt(Window owner, string message)
        {
            var dlg = new PermissionDeniedDialog(message)
            {
                Owner = DialogOwnerHelper.GetSafeOwner(owner)
            };
            dlg.ShowDialog();
            return dlg.SwitchRequested;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            SwitchRequested = false;
            DialogResult = false;
            Close();
        }

        private void OnSwitchClick(object sender, RoutedEventArgs e)
        {
            SwitchRequested = true;
            DialogResult = true;
            Close();
        }
    }
}
