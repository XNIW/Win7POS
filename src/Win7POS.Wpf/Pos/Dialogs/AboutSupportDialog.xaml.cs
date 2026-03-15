using System.Windows;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class AboutSupportDialog : DialogShellWindow
    {
        public AboutSupportDialog(AboutSupportViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
