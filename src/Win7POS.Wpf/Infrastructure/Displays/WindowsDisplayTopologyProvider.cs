using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Win7POS.Wpf.Infrastructure.Displays
{
    public sealed class WindowsDisplayTopologyProvider : IDisplayTopologyProvider
    {
        public IReadOnlyList<DisplayMonitorInfo> GetMonitors()
        {
            return Screen.AllScreens
                .Select(screen => new DisplayMonitorInfo
                {
                    DeviceName = screen.DeviceName ?? string.Empty,
                    IsPrimary = screen.Primary,
                    BoundsLeft = screen.Bounds.Left,
                    BoundsTop = screen.Bounds.Top,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height,
                    WorkAreaLeft = screen.WorkingArea.Left,
                    WorkAreaTop = screen.WorkingArea.Top,
                    WorkingWidth = screen.WorkingArea.Width,
                    WorkingHeight = screen.WorkingArea.Height,
                    BitsPerPixel = screen.BitsPerPixel
                })
                .OrderBy(x => x.BoundsLeft)
                .ThenBy(x => x.BoundsTop)
                .ThenBy(x => x.DeviceName, System.StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }
    }
}
