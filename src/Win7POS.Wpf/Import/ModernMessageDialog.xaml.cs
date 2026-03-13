using System.Windows;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Import
{
    /// <summary>Dialog moderno con messaggio e pulsante OK (sostituisce MessageBox per info/warning).</summary>
    public partial class ModernMessageDialog : DialogShellWindow
    {
        public ModernMessageDialog(string title, string message)
        {
            InitializeComponent();
            Title = title ?? "Win7POS";
            TitleText.Text = title ?? "Win7POS";
            MessageText.Text = message ?? "";
        }

        public static void Show(Window owner, string title, string message)
        {
            var dlg = new ModernMessageDialog(title ?? "Win7POS", message ?? "")
            {
                Owner = owner ?? Application.Current?.MainWindow
            };
            dlg.ShowDialog();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
