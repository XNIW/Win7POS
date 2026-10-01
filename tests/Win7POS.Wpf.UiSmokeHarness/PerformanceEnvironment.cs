using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal sealed class PerformanceEnvironment : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct PowerStatus
        { public byte Ac, Battery, Percent, Saver; public uint Life, FullLife; }
        [DllImport("kernel32.dll")] private static extern bool QueryUnbiasedInterruptTime(out ulong time);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, uint size, out uint needed);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("wtsapi32.dll")] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int information, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
        private readonly ulong _start;
        private readonly StreamWriter _writer;
        private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
        private volatile bool _suspended;
        private volatile bool _sessionInterrupted;
        internal bool Valid { get; private set; } = true;
        internal bool Suspended => _suspended;
        internal double AwakeSeconds
        {
            get
            {
                if (!QueryUnbiasedInterruptTime(out var time)) { Valid = false; return 0; }
                return (time - _start) / 10000000d;
            }
        }
        internal PerformanceEnvironment(string directory)
        {
            if (!QueryUnbiasedInterruptTime(out _start)) throw new InvalidOperationException("unbiased_awake_clock_unavailable");
            _writer = new StreamWriter(Path.Combine(directory, "qualification-environment.csv"));
            _writer.WriteLine("utc,awake_s,desktop_matches,session_active,own_foreground,visible,ac_line,battery_percent,suspended,session_interrupted,dpi_x,dpi_y,input_desktop_observed,thread_desktop_observed,desktop_access_error");
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            File.WriteAllText(Path.Combine(directory, "qualification-runtime.txt"),
                "utc=" + DateTimeOffset.UtcNow.ToString("O") + "\nCLR=" + Environment.Version + "\nWPF=" + typeof(Window).Assembly.FullName +
                "\nFrameworkFileVersion=" + FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location).FileVersion +
                "\nWpfFileVersion=" + FileVersionInfo.GetVersionInfo(typeof(Window).Assembly.Location).FileVersion +
                "\nOS=" + Environment.OSVersion + "\nprocess_arch=" + (Environment.Is64BitProcess ? "x64" : "x86") + "\nStopwatch.Frequency=" + Stopwatch.Frequency +
                "\nduration_clock=QueryUnbiasedInterruptTime (excludes sleep)\nphysical_scan_to_monitor_latency=NOT_MEASURED\n");
        }
        private void PowerChanged(object sender, PowerModeChangedEventArgs args)
        { if (args.Mode == PowerModes.Suspend || args.Mode == PowerModes.Resume) _suspended = true; }
        private void SessionChanged(object sender, SessionSwitchEventArgs args)
        { if (args.Reason != SessionSwitchReason.SessionUnlock) _sessionInterrupted = true; }
        private static string DesktopName(IntPtr handle)
        {
            var text = new StringBuilder(256);
            return handle != IntPtr.Zero && GetUserObjectInformation(handle, 2, text, 512, out _) ? text.ToString() : null;
        }
        internal void Sample(Window host)
        {
            var input = OpenInputDesktop(0, false, 1);
            var desktopError = input == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            var inputName = DesktopName(input);
            var threadName = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            var sameDesktop = !string.IsNullOrEmpty(inputName) && !string.IsNullOrEmpty(threadName) && inputName == threadName;
            if (input != IntPtr.Zero) CloseDesktop(input);
            var sessionActive = false;
            if (WTSQuerySessionInformation(IntPtr.Zero, _sessionId, 8, out var buffer, out var bytes))
            {
                try { sessionActive = bytes >= 4 && Marshal.ReadInt32(buffer) == 0; }
                finally { WTSFreeMemory(buffer); }
            }
            var foreground = GetForegroundWindow();
            var ownForeground = Application.Current.Windows.Cast<Window>().Any(window => window.IsVisible && new WindowInteropHelper(window).Handle == foreground);
            var powerKnown = GetSystemPowerStatus(out var power);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(host);
            Valid &= sameDesktop && sessionActive && ownForeground && host.IsVisible && !_suspended && !_sessionInterrupted && powerKnown && power.Ac <= 1;
            _writer.WriteLine(string.Join(",", DateTimeOffset.UtcNow.ToString("O"), AwakeSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                sameDesktop ? 1 : 0, sessionActive ? 1 : 0, ownForeground ? 1 : 0, host.IsVisible ? 1 : 0,
                powerKnown ? power.Ac : 255, powerKnown ? power.Percent : 255, _suspended ? 1 : 0, _sessionInterrupted ? 1 : 0,
                dpi.PixelsPerInchX.ToString(System.Globalization.CultureInfo.InvariantCulture), dpi.PixelsPerInchY.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(inputName) ? 0 : 1, string.IsNullOrEmpty(threadName) ? 0 : 1, desktopError));
        }
        internal void Flush() => _writer.Flush();
        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionSwitch -= SessionChanged;
            _writer.Dispose();
        }
    }
}
