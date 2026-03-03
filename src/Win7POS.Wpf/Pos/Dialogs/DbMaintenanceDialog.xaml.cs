using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DbMaintenanceDialog : Window
    {
        public DbMaintenanceDialog(DbMaintenanceViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.7, heightPercent: 0.65, minWidth: 680, minHeight: 420);
            DataContext = vm;
        }
    }
}
