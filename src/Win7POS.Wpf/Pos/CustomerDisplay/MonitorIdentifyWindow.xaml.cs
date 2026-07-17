using System;
using System.Windows;
using System.Windows.Threading;
using Win7POS.Wpf.Infrastructure.Displays;

namespace Win7POS.Wpf.Pos.CustomerDisplay
{
    public partial class MonitorIdentifyWindow : Window
    {
        private readonly DisplayMonitorInfo _monitor;
        private readonly DispatcherTimer _closeTimer;

        public MonitorIdentifyWindow(DisplayMonitorInfo monitor, int number)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            InitializeComponent();
            NumberText.Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            MonitorText.Text = monitor.Width + " × " + monitor.Height + (monitor.IsPrimary ? "  • Primary" : string.Empty);
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _closeTimer.Tick += OnCloseTimer;
            Closed += (_, __) => _closeTimer.Stop();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            PhysicalWindowPlacement.ApplyNoActivateToolWindow(this);
            PhysicalWindowPlacement.Apply(this, _monitor, false, true, showWindow: false);
            _closeTimer.Start();
        }

        private void OnCloseTimer(object sender, EventArgs e)
        {
            _closeTimer.Stop();
            Close();
        }
    }
}
