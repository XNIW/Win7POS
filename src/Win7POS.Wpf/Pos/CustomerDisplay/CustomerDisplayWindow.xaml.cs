using System;
using System.Windows;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Infrastructure.Displays;

namespace Win7POS.Wpf.Pos.CustomerDisplay
{
    public partial class CustomerDisplayWindow : Window
    {
        private readonly CustomerDisplayViewModel _viewModel = new CustomerDisplayViewModel();
        private DisplayMonitorInfo _preparedMonitor;
        private bool _preparedUseWorkingArea;
        private bool _preparedAlwaysOnTop;

        public CustomerDisplayWindow()
        {
            InitializeComponent();
            DataContext = _viewModel;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            PhysicalWindowPlacement.ApplyNoActivateToolWindow(this);
            ApplyPreparedPlacement(showWindow: false);
        }

        public void PrepareDisplay(
            CustomerDisplaySnapshot snapshot,
            CustomerDisplaySettings settings,
            DisplayMonitorInfo monitor)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));

            var useWorkingArea = settings.UseWorkingArea || !settings.FullScreen;
            var layout = CustomerDisplayLayoutPolicy.Determine(
                useWorkingArea ? monitor.WorkingWidth : monitor.Width,
                useWorkingArea ? monitor.WorkingHeight : monitor.Height);
            _viewModel.Apply(snapshot, settings, layout);
            Topmost = settings.AlwaysOnTop;
            _preparedMonitor = monitor;
            _preparedUseWorkingArea = useWorkingArea;
            _preparedAlwaysOnTop = settings.AlwaysOnTop;
            ApplyPreparedPlacement(showWindow: false);

            QueueChangedLineScroll();
        }

        public void UpdateDisplay(
            CustomerDisplaySnapshot snapshot,
            CustomerDisplaySettings settings,
            DisplayMonitorInfo monitor)
        {
            PrepareDisplay(snapshot, settings, monitor);
            ApplyPreparedPlacement(showWindow: true);
        }

        private void ApplyPreparedPlacement(bool showWindow)
        {
            if (_preparedMonitor == null) return;
            PhysicalWindowPlacement.Apply(
                this,
                _preparedMonitor,
                _preparedUseWorkingArea,
                _preparedAlwaysOnTop,
                showWindow);
        }

        private void QueueChangedLineScroll()
        {
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
