using System.Collections.Generic;

namespace Win7POS.Wpf.Infrastructure.Displays
{
    public interface IDisplayTopologyProvider
    {
        IReadOnlyList<DisplayMonitorInfo> GetMonitors();
    }
}
