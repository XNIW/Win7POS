using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class AboutSupportDialog : Window
    {
        public AboutSupportDialog(AboutSupportViewModel vm)
        {
            InitializeComponent();
            // Dimensioni compatte da XAML (520x400), Build nascosto in UI ma disponibile in Copia info
            DataContext = vm;
        }
    }
}
