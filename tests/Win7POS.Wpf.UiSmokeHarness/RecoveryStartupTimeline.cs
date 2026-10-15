using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Observation only: no fixture, WPF initialization or warmup is moved here.
    internal static class RecoveryStartupTimeline
    {
        private static List<string> _events;
        private static long _origin;
        private static string _directory;
        private static int _writeFailures;

        internal static void Start(string[] args)
        {
            var origin = Stopwatch.GetTimestamp();
            if (Array.IndexOf(args, "--functional-completion-smoke") < 0) return;
            var scenario = Array.IndexOf(args, "--scenario");
            if (scenario < 0 || scenario + 1 >= args.Length || args[scenario + 1] != "IMPORT_RECOVERY_completion") return;
            _origin = origin;
            _events = new List<string>(12);
            Record("main_first_reachable_probe");
        }

        internal static void Attach(string directory)
        {
            if (_events == null) return;
            _directory = directory;
            Record("data_directory_ready");
        }

        internal static void Record(string phase)
        {
            if (_events == null || _events.Count >= 12) return;
            try
            {
                var elapsed = (Stopwatch.GetTimestamp() - _origin) * 1000.0 / Stopwatch.Frequency;
                using (var process = Process.GetCurrentProcess())
                    _events.Add(string.Format(CultureInfo.InvariantCulture,
                        "phase={0}; since_main_probe_ms={1:F3}; creation_to_marker_wall_ms={2:F3}; process_cpu_ms={3:F3}; pid={4}; thread={5}; pointer_bytes={6}; clr={7}; write_failures={8}",
                        phase, elapsed, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds,
                        process.TotalProcessorTime.TotalMilliseconds, process.Id, Thread.CurrentThread.ManagedThreadId,
                        IntPtr.Size, Environment.Version, _writeFailures));
                // At most twelve small snapshots. Timestamps are captured before
                // file I/O; this instrumentation does not diagnose pre-Main causes.
                if (_directory != null)
                    File.WriteAllLines(Path.Combine(_directory, "recovery-startup-timeline.txt"), _events);
            }
            catch { _writeFailures++; }
        }
    }
}
