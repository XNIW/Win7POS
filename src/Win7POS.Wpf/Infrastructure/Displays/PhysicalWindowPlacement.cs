using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Win7POS.Wpf.Infrastructure.Displays
{
    public static class PhysicalWindowPlacement
    {
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public static void ApplyNoActivateToolWindow(Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var current = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, current | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public static void Apply(
            Window window,
            DisplayMonitorInfo monitor,
            bool useWorkingArea,
            bool topmost)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var left = useWorkingArea ? monitor.WorkAreaLeft : monitor.BoundsLeft;
            var top = useWorkingArea ? monitor.WorkAreaTop : monitor.BoundsTop;
            var width = useWorkingArea ? monitor.WorkingWidth : monitor.Width;
            var height = useWorkingArea ? monitor.WorkingHeight : monitor.Height;
            if (width <= 0 || height <= 0) return;

            if (!SetWindowPos(
                    handle,
                    topmost ? HwndTopmost : HwndNotTopmost,
                    left,
                    top,
                    width,
                    height,
                    SwpNoActivate | SwpShowWindow))
            {
                throw new InvalidOperationException("customer_display_placement_failed");
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);
    }
}
