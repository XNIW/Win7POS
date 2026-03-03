using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class AboutSupportDialog : Window
    {
        public AboutSupportDialog(AboutSupportViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.65, heightPercent: 0.6, minWidth: 680, minHeight: 420);
            DataContext = vm;
        }
    }
}
