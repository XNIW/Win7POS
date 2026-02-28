using System.Windows;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DbMaintenanceDialog : Window
    {
        public DbMaintenanceDialog(DbMaintenanceViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
