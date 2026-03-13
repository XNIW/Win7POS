using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DbMaintenanceDialog : DialogShellWindow
    {
        public DbMaintenanceDialog(DbMaintenanceViewModel vm)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 640, minHeight: 420, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
            DataContext = vm;
        }
    }
}
