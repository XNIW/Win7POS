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
            if (window?.Owner != null && TryGetWorkArea(window.Owner, out var ownerWorkArea))
                return ownerWorkArea;

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

        public static void AddWorkAreaClampHook(Window window)
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

            HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_WINDOWPOSCHANGING || window.WindowState == WindowState.Maximized || lParam == IntPtr.Zero)
                    return IntPtr.Zero;

                var windowPos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                if ((windowPos.flags & SWP_NOMOVE) != 0)
                    return IntPtr.Zero;

                if (!TryGetRawWorkArea(window, out var workAreaPixels))
                    workAreaPixels = ConvertDipRectToDevice(window, SystemParameters.WorkArea);

                var changed = false;

                if ((windowPos.flags & SWP_NOSIZE) == 0)
                {
                    var maxWidth = Math.Max(1, workAreaPixels.Right - workAreaPixels.Left);
                    var maxHeight = Math.Max(1, workAreaPixels.Bottom - workAreaPixels.Top);

                    if (windowPos.cx > maxWidth)
                    {
                        windowPos.cx = maxWidth;
                        changed = true;
                    }

                    if (windowPos.cy > maxHeight)
                    {
                        windowPos.cy = maxHeight;
                        changed = true;
                    }
                }

                var maxX = workAreaPixels.Right - windowPos.cx;
                var maxY = workAreaPixels.Bottom - windowPos.cy;
                var clampedX = Math.Min(Math.Max(windowPos.x, workAreaPixels.Left), maxX);
                var clampedY = Math.Min(Math.Max(windowPos.y, workAreaPixels.Top), maxY);

                if (clampedX != windowPos.x)
                {
                    windowPos.x = clampedX;
                    changed = true;
                }

                if (clampedY != windowPos.y)
                {
                    windowPos.y = clampedY;
                    changed = true;
                }

                if (changed)
                    Marshal.StructureToPtr(windowPos, lParam, false);

                return IntPtr.Zero;
            };

            source.AddHook(hook);
            HookedWindows.Add(window, new HookRegistration(source, hook));

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
        }
    }
}
