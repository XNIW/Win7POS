using System.Windows;

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
                MessageBox.Show("Inserire il nome del ruolo.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!CodeBox.IsReadOnly && string.IsNullOrWhiteSpace(RoleCode))
            {
                MessageBox.Show("Inserire il codice del ruolo (es. mio_ruolo).", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
