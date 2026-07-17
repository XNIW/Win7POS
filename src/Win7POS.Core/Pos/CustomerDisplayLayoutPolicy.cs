using System;

namespace Win7POS.Core.Pos
{
    public enum CustomerDisplayLayoutMode
    {
        Compact,
        Standard,
        Large,
        Portrait
    }

    public sealed class CustomerDisplayLayout
    {
        public CustomerDisplayLayoutMode Mode { get; set; }
        public double FontScale { get; set; }
        public double RowHeight { get; set; }
        public double TotalFontSize { get; set; }
        public double Spacing { get; set; }
    }

    public static class CustomerDisplayLayoutPolicy
    {
        public static CustomerDisplayLayout Determine(
            int physicalWidth,
            int physicalHeight,
            double systemDpiScaleX = 1.0,
            double systemDpiScaleY = 1.0)
        {
            if (physicalWidth <= 0) throw new ArgumentOutOfRangeException(nameof(physicalWidth));
            if (physicalHeight <= 0) throw new ArgumentOutOfRangeException(nameof(physicalHeight));
            if (systemDpiScaleX <= 0) systemDpiScaleX = 1.0;
            if (systemDpiScaleY <= 0) systemDpiScaleY = 1.0;

            var mode = physicalWidth < physicalHeight
                ? CustomerDisplayLayoutMode.Portrait
                : physicalHeight <= 600 || physicalWidth <= 800
                    ? CustomerDisplayLayoutMode.Compact
                    : physicalWidth >= 1920 && physicalHeight >= 1000
                        ? CustomerDisplayLayoutMode.Large
                        : CustomerDisplayLayoutMode.Standard;

            var dpi = Math.Max(systemDpiScaleX, systemDpiScaleY);
            var baseScale = mode == CustomerDisplayLayoutMode.Compact ? 0.88 :
                            mode == CustomerDisplayLayoutMode.Large ? 1.18 :
                            mode == CustomerDisplayLayoutMode.Portrait ? 1.02 : 1.0;
            var sharpScale = baseScale / Math.Max(1.0, Math.Min(2.0, dpi));

            return new CustomerDisplayLayout
            {
                Mode = mode,
                FontScale = sharpScale,
                RowHeight = mode == CustomerDisplayLayoutMode.Compact ? 42 : mode == CustomerDisplayLayoutMode.Large ? 62 : 52,
                TotalFontSize = mode == CustomerDisplayLayoutMode.Compact ? 34 : mode == CustomerDisplayLayoutMode.Large ? 58 : 44,
                Spacing = mode == CustomerDisplayLayoutMode.Compact ? 8 : mode == CustomerDisplayLayoutMode.Large ? 20 : 14
            };
        }
    }
}
