using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Win7POS.Wpf.Infrastructure
{
    public static class MonitorHelper
    {
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_WINDOWPOSCHANGED = 0x0047;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        private static readonly ConditionalWeakTable<Window, HookRegistration> HookedWindows =
            new ConditionalWeakTable<Window, HookRegistration>();

        public static bool TryGetWorkArea(Window window, out Rect workArea)
        {
            if (TryGetRawWorkArea(window, out var rawWorkArea))
            {
                workArea = ConvertDeviceRectToDip(window, rawWorkArea);
                return true;
            }

            workArea = Rect.Empty;
            return false;
        }

        public static Rect GetWorkAreaOrPrimary(Window window)
        {
            if (window?.Owner != null && TryGetRawWorkArea(window.Owner, out var ownerWorkAreaPixels))
                return ConvertDeviceRectToDip(window, ownerWorkAreaPixels);

            if (TryGetWorkArea(window, out var windowWorkArea))
                return windowWorkArea;

            return SystemParameters.WorkArea;
        }

        public static Rect GetWorkAreaForExactWindowOrPrimary(Window window)
        {
            if (TryGetWorkArea(window, out var workArea))
                return workArea;

            return SystemParameters.WorkArea;
        }

        public static void AddWorkAreaClampHook(Window window, Action<Rect> refreshConstraints = null)
        {
            if (window == null)
                return;

            if (HookedWindows.TryGetValue(window, out _))
                return;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            var source = HwndSource.FromHwnd(handle);
            if (source == null)
                return;

            var initialPlacement = true;
            var refreshing = false;
            var lastWorkArea = Rect.Empty;
            HookRegistration registration = null;
            HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_WINDOWPOSCHANGED && initialPlacement && IsWindowVisible(hwnd))
                {
                    if (GetWindowRect(hwnd, out var firstVisible)) registration.FirstVisibleBoundsPixels = ToRect(firstVisible);
                    initialPlacement = false;
                }
                if ((msg == WM_SETTINGCHANGE || msg == WM_DISPLAYCHANGE) && !refreshing)
                {
                    refreshing = true;
                    try
                    {
                        if (GetWindowRect(hwnd, out var current) && TryGetRawWorkArea(window, out var area))
                        {
                            refreshConstraints?.Invoke(ConvertDeviceRectToDip(window, area));
                            var bounded = ConstrainToWorkArea(ToRect(current), ToRect(area), false);
                            SetWindowPos(hwnd, IntPtr.Zero, (int)bounded.X, (int)bounded.Y,
                                (int)bounded.Width, (int)bounded.Height, 0x0014); // no z-order/activation change
                        }
                    }
                    finally { refreshing = false; }
                }
                if (msg != WM_WINDOWPOSCHANGING || window.WindowState == WindowState.Maximized || lParam == IntPtr.Zero)
                    return IntPtr.Zero;

                var windowPos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                if (!GetWindowRect(hwnd, out var existing)) return IntPtr.Zero;
                var proposed = new Rect((windowPos.flags & SWP_NOMOVE) != 0 ? existing.Left : windowPos.x,
                    (windowPos.flags & SWP_NOMOVE) != 0 ? existing.Top : windowPos.y,
                    Math.Max(1, (windowPos.flags & SWP_NOSIZE) != 0 ? existing.Right - existing.Left : windowPos.cx),
                    Math.Max(1, (windowPos.flags & SWP_NOSIZE) != 0 ? existing.Bottom - existing.Top : windowPos.cy));
                RECT workAreaPixels;
                if (initialPlacement)
                {
                    if (!TryGetRawWorkArea(window.Owner ?? window, out workAreaPixels))
                        workAreaPixels = ConvertDipRectToDevice(window, SystemParameters.WorkArea);
                }
                else
                {
                    var requested = ToNativeRect(proposed);
                    if (!TryGetMonitorWorkArea(MonitorFromRect(ref requested, MONITOR_DEFAULTTONEAREST), out workAreaPixels))
                        workAreaPixels = ConvertDipRectToDevice(window, SystemParameters.WorkArea);
                }
                var workRect = ToRect(workAreaPixels);
                if (!refreshing && workRect != lastWorkArea)
                {
                    lastWorkArea = workRect;
                    refreshing = true;
                    try { refreshConstraints?.Invoke(ConvertDeviceRectToDip(window, workAreaPixels)); }
                    finally { refreshing = false; }
                }
                // WINDOWPOS fields covered by NOSIZE/NOMOVE are undefined. Fill
                // those from the current HWND, and clear a flag only when changing it.
                var boundedBounds = ConstrainToWorkArea(proposed, workRect, initialPlacement);
                if (boundedBounds.X != proposed.X || boundedBounds.Y != proposed.Y)
                {
                    windowPos.x = (int)boundedBounds.X; windowPos.y = (int)boundedBounds.Y;
                    windowPos.flags &= ~SWP_NOMOVE;
                }
                if (boundedBounds.Width != proposed.Width || boundedBounds.Height != proposed.Height)
                {
                    windowPos.cx = (int)boundedBounds.Width; windowPos.cy = (int)boundedBounds.Height;
                    windowPos.flags &= ~SWP_NOSIZE;
                }
                Marshal.StructureToPtr(windowPos, lParam, false);

                return IntPtr.Zero;
            };

            registration = new HookRegistration(source, hook);
            source.AddHook(hook);
            HookedWindows.Add(window, registration);

            window.Closed += (_, __) =>
            {
                if (HookedWindows.TryGetValue(window, out var registration))
                {
                    registration.Source.RemoveHook(registration.Hook);
                    HookedWindows.Remove(window);
                }
            };
        }

        private static bool TryGetRawWorkArea(Window window, out RECT workArea)
        {
            var handle = window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                workArea = default(RECT);
                return false;
            }

            var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                workArea = default(RECT);
                return false;
            }

            return TryGetMonitorWorkArea(monitor, out workArea);
        }

        private static bool TryGetMonitorWorkArea(IntPtr monitor, out RECT workArea)
        {
            var monitorInfo = new MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO))
            };

            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                workArea = default(RECT);
                return false;
            }

            workArea = monitorInfo.rcWork;
            return true;
        }

        // Pixel-space arithmetic shared by initial placement and subsequent
        // containment. A moved window is clamped only when a border leaves work.
        internal static Rect ConstrainToWorkArea(Rect proposed, Rect work, bool center)
        {
            var width = Math.Min(Math.Max(1, proposed.Width), Math.Max(1, work.Width));
            var height = Math.Min(Math.Max(1, proposed.Height), Math.Max(1, work.Height));
            var x = center ? work.Left + (work.Width - width) / 2 : Math.Min(Math.Max(proposed.Left, work.Left), work.Right - width);
            var y = center ? work.Top + (work.Height - height) / 2 : Math.Min(Math.Max(proposed.Top, work.Top), work.Bottom - height);
            return new Rect(Math.Round(x), Math.Round(y), width, height);
        }
        // Actual HWND bounds at its first visible native position, before repaint.
        // Internal read-only witness; expected monitor geometry is acquired independently by QA.
        internal static Rect? GetFirstVisibleBoundsPixels(Window window) =>
            HookedWindows.TryGetValue(window, out var registration) ? registration.FirstVisibleBoundsPixels : null;
        internal static bool TryReadWindowBoundsPixels(Window window, out Rect bounds, out Rect work)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (GetWindowRect(hwnd, out var native) && TryGetRawWorkArea(window.Owner ?? window, out var area))
            {
                bounds = ToRect(native); work = ToRect(area); return true;
            }
            bounds = Rect.Empty; work = Rect.Empty; return false;
        }
        private static Rect ToRect(RECT rect) => new Rect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
        private static RECT ToNativeRect(Rect rect) => new RECT { Left = (int)rect.Left, Top = (int)rect.Top, Right = (int)rect.Right, Bottom = (int)rect.Bottom };

        private static Rect ConvertDeviceRectToDip(Window window, RECT rect)
        {
            var source = window == null ? null : HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
            var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        private static RECT ConvertDipRectToDevice(Window window, Rect rect)
        {
            var source = window == null ? null : HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
            var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
            return new RECT
            {
                Left = (int)Math.Floor(topLeft.X),
                Top = (int)Math.Floor(topLeft.Y),
                Right = (int)Math.Ceiling(bottomRight.X),
                Bottom = (int)Math.Ceiling(bottomRight.Y)
            };
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private sealed class HookRegistration
        {
            public HookRegistration(HwndSource source, HwndSourceHook hook)
            {
                Source = source;
                Hook = hook;
            }

            public HwndSource Source { get; }

            public HwndSourceHook Hook { get; }
            public Rect? FirstVisibleBoundsPixels { get; set; }
        }
    }
}
