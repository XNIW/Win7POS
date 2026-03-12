using System.Windows;
using Win7POS.Wpf.Import;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class RoleEditDialog : Window
    {
        public string RoleCode => (CodeBox?.Text ?? "").Trim();
        public string RoleName => (NameBox?.Text ?? "").Trim();

        public RoleEditDialog(string title, string initialCode = "", string initialName = "", bool codeReadOnly = false)
        {
            InitializeComponent();
            Title = title ?? "Ruolo";
            CodeBox.Text = initialCode ?? "";
            NameBox.Text = initialName ?? "";
            if (codeReadOnly && CodeRow != null) CodeRow.Visibility = Visibility.Collapsed;
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(RoleName))
            {
                ModernMessageDialog.Show(this, Title ?? "Ruolo", "Inserire il nome del ruolo.");
                return;
            }
            if (!CodeBox.IsReadOnly && string.IsNullOrWhiteSpace(RoleCode))
            {
                ModernMessageDialog.Show(this, Title ?? "Ruolo", "Inserire il codice del ruolo (es. mio_ruolo).");
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
