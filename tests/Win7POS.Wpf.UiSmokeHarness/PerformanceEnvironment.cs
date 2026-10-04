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
        private readonly string _runtimePath;
        private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
        private volatile bool _suspended;
        private volatile bool _sessionInterrupted;
        private bool _firstSample = true;
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
            _runtimePath = Path.Combine(directory, "qualification-runtime.txt");
            _writer.WriteLine("utc,awake_s,desktop_matches,session_active,own_foreground,visible,ac_line,battery_percent,suspended,session_interrupted,dpi_x,dpi_y,input_desktop_observed,thread_desktop_observed,desktop_access_error");
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            File.WriteAllText(_runtimePath,
                "utc=" + DateTimeOffset.UtcNow.ToString("O") + "\nCLR=" + Environment.Version + "\nWPF=" + typeof(Window).Assembly.FullName +
                "\nFrameworkFileVersion=" + FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location).FileVersion +
                "\nWpfFileVersion=" + FileVersionInfo.GetVersionInfo(typeof(Window).Assembly.Location).FileVersion +
                "\nOS=" + Environment.OSVersion + "\nprocess_arch=" + (Environment.Is64BitProcess ? "x64" : "x86") + "\nStopwatch.Frequency=" + Stopwatch.Frequency +
                "\nprocess_id=" + Process.GetCurrentProcess().Id + "\nsession_id=" + _sessionId + "\nui_thread_id=" + GetCurrentThreadId() +
                "\nworking_directory=" + Environment.CurrentDirectory + "\nendpoint_origin=" + EndpointOrigin() +
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
        private static string EndpointOrigin()
        {
            return Uri.TryCreate(Environment.GetEnvironmentVariable("WIN7POS_ADMIN_WEB_BASE_URL"), UriKind.Absolute, out var endpoint)
                ? endpoint.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) : "UNOBSERVED";
        }
        internal void Sample(Window host)
        {
            var input = OpenInputDesktop(0, false, 1);
            var desktopError = input == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            string inputName, threadName;
            try
            {
                inputName = DesktopName(input);
                threadName = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            }
            finally { if (input != IntPtr.Zero) CloseDesktop(input); }
            if (_firstSample)
            {
                _firstSample = false;
                File.AppendAllText(_runtimePath,
                    "first_environment_sample_utc=" + DateTimeOffset.UtcNow.ToString("O") + "\ninput_desktop=" + inputName + "\nthread_desktop=" + threadName + "\n");
            }
            var sameDesktop = !string.IsNullOrEmpty(inputName) && !string.IsNullOrEmpty(threadName) && inputName == threadName;
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
        internal void SampleOrThrow(Window host)
        {
            Sample(host);
            // Invalidity is cumulative. Preserve the decisive child sample before
            // unwinding the view and subscriptions; later valid input cannot repair it.
            Flush();
            if (!Valid) throw new InvalidOperationException("qualification_environment_invalid");
        }
        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionSwitch -= SessionChanged;
            _writer.Dispose();
        }
    }
}
