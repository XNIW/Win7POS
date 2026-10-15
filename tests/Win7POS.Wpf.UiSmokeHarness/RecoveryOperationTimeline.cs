using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Win7POS.Core;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // QA observation only. No file/console sink runs between the two snapshots.
    internal static class RecoveryOperationTimeline
    {
        private static readonly List<string> Rows = new List<string>();
        private static int _written;
        internal static bool Enabled => Environment.GetEnvironmentVariable("WIN7POS_QA_RECOVERY_OPERATIONS") == "1";

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            internal ulong ReadOperations, WriteOperations, OtherOperations;
            internal ulong ReadBytes, WriteBytes, OtherBytes;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        internal sealed class Snapshot
        {
            internal long Tick, ThreadCpu;
            internal DateTime Utc;
            internal double ProcessCpu;
            internal IoCounters Io;
            internal bool ThreadValid, IoValid;
        }

        internal static Snapshot Begin()
        {
            if (!Enabled) return null;
            using (var process = Process.GetCurrentProcess())
            {
                var result = new Snapshot { ProcessCpu = process.TotalProcessorTime.TotalMilliseconds };
                result.ThreadValid = GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
                result.ThreadCpu = kernel + user;
                result.IoValid = GetProcessIoCounters(process.Handle, out result.Io);
                result.Tick = Stopwatch.GetTimestamp();
                result.Utc = DateTime.UtcNow;
                return result;
            }
        }

        internal static void End(string operation, Snapshot before)
        {
            if (before == null) return;
            var endTick = Stopwatch.GetTimestamp();
            var endUtc = DateTime.UtcNow;
            var after = Begin();
            if (Rows.Count >= 1024) throw new InvalidOperationException("Recovery diagnostic operation limit exceeded.");
            Rows.Add(string.Format(CultureInfo.InvariantCulture,
                "operation={0}; start_qpc={1}; end_qpc={2}; frequency={3}; elapsed_ms={4:F3}; process_cpu_ms={5:F3}; ui_thread_cpu_ms={6:F3}; thread_counter_valid={7}; io_valid={8}; read_bytes={9}; write_bytes={10}; read_operations={11}; write_operations={12}; start_utc={13:O}; end_utc={14:O}",
                operation, before.Tick, endTick, Stopwatch.Frequency,
                (endTick - before.Tick) * 1000d / Stopwatch.Frequency, after.ProcessCpu - before.ProcessCpu,
                (after.ThreadCpu - before.ThreadCpu) / 10000d, before.ThreadValid && after.ThreadValid,
                before.IoValid && after.IoValid, after.Io.ReadBytes - before.Io.ReadBytes,
                after.Io.WriteBytes - before.Io.WriteBytes, after.Io.ReadOperations - before.Io.ReadOperations,
                after.Io.WriteOperations - before.Io.WriteOperations, before.Utc, endUtc));
        }

        internal static void Flush()
        {
            if (!Enabled || Rows.Count == _written) return;
            File.AppendAllLines(Path.Combine(AppPaths.DataDirectory, "recovery-operation-timeline.txt"), Rows.GetRange(_written, Rows.Count - _written));
            _written = Rows.Count;
        }
    }
}
