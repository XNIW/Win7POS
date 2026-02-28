using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class AboutSupportDialog : Window
    {
        public AboutSupportDialog(AboutSupportViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
