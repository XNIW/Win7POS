using Win7POS.Core.Pos;

namespace Win7POS.Wpf.Infrastructure.Displays
{
    public sealed class DisplayMonitorInfo
    {
        public string DeviceName { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
        public int BoundsLeft { get; set; }
        public int BoundsTop { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int WorkAreaLeft { get; set; }
        public int WorkAreaTop { get; set; }
        public int WorkingWidth { get; set; }
        public int WorkingHeight { get; set; }
        public int BitsPerPixel { get; set; }
        public string Orientation => Height > Width ? "portrait" : "landscape";

        public CustomerDisplayMonitorDescriptor ToDescriptor()
        {
            return new CustomerDisplayMonitorDescriptor(
                DeviceName,
                IsPrimary,
                BoundsLeft,
                BoundsTop,
                Width,
                Height);
        }
    }
}
