using System;
using System.Windows;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Infrastructure.Displays;

namespace Win7POS.Wpf.Pos.CustomerDisplay
{
    public partial class CustomerDisplayWindow : Window
    {
        private readonly CustomerDisplayViewModel _viewModel = new CustomerDisplayViewModel();

        public CustomerDisplayWindow()
        {
            InitializeComponent();
            DataContext = _viewModel;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            PhysicalWindowPlacement.ApplyNoActivateToolWindow(this);
        }

        public void UpdateDisplay(
            CustomerDisplaySnapshot snapshot,
            CustomerDisplaySettings settings,
            DisplayMonitorInfo monitor)
        {
            var useWorkingArea = settings.UseWorkingArea || !settings.FullScreen;
            var layout = CustomerDisplayLayoutPolicy.Determine(
                useWorkingArea ? monitor.WorkingWidth : monitor.Width,
                useWorkingArea ? monitor.WorkingHeight : monitor.Height);
            _viewModel.Apply(snapshot, settings, layout);
            Topmost = settings.AlwaysOnTop;
            PhysicalWindowPlacement.Apply(this, monitor, useWorkingArea, settings.AlwaysOnTop);

            if (!string.IsNullOrWhiteSpace(_viewModel.LastChangedLineKey))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var item in LinesList.Items)
                    {
                        if (item is CustomerDisplayLineRow row &&
                            string.Equals(row.StableKey, _viewModel.LastChangedLineKey, StringComparison.Ordinal))
                        {
                            LinesList.ScrollIntoView(item);
                            break;
                        }
                    }
                }));
            }
        }
    }
}
