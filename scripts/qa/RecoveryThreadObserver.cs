using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Read-only, optional observer. It holds the launch Process, never attaches a
// debugger, suspends a thread, warms WPF, or changes the functional deadline.
public sealed class RecoveryThreadObserver : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    readonly Process target;
    readonly Thread worker;
    readonly ManualResetEvent stop = new ManualResetEvent(false);
    readonly List<string> rows = new List<string>(20000);
    readonly string path;
    public string Error { get; private set; }
    public int Samples { get; private set; }
    public RecoveryThreadObserver(Process process, string outputPath)
    {
        target = process; path = outputPath;
        rows.Add("utc,kind,pid,tid,name,state,wait,cpu_ticks,idle_ticks,kernel_ticks,user_ticks,sample_ms");
        worker = new Thread(Observe) { IsBackground = true, Name = "Recovery read-only observer" };
        worker.Start();
    }
    void Observe()
    {
        try
        {
            var duration = Stopwatch.StartNew();
            while (!stop.WaitOne(0) && duration.ElapsedMilliseconds < 65000 && Samples < 650)
            {
                var sample = Stopwatch.StartNew();
                if (target.HasExited) break;
                string utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                long idle, kernel, user;
                if (!GetSystemTimes(out idle, out kernel, out user))
                    throw new System.ComponentModel.Win32Exception();
                target.Refresh();
                rows.Add(utc + ",system," + target.Id + ",0,host,,," + target.TotalProcessorTime.Ticks + "," + idle + "," + kernel + "," + user + ",");
                int threadCount = 0;
                foreach (ProcessThread thread in target.Threads)
                {
                    using (thread)
                    {
                        if (++threadCount > 64) throw new InvalidOperationException("Target thread count exceeds observer bound.");
                        try
                        {
                            var state = thread.ThreadState;
                            string reason = state == System.Diagnostics.ThreadState.Wait ? thread.WaitReason.ToString() : "";
                            rows.Add(utc + ",thread," + target.Id + "," + thread.Id + ",target," + state + "," + reason + "," + thread.TotalProcessorTime.Ticks + ",,,,");
                        }
                        catch (InvalidOperationException) { /* Thread ended between snapshot and query. */ }
                        catch (System.ComponentModel.Win32Exception) { /* Thread ended between snapshot and query. */ }
                    }
                }
                // Only relevant known process names, once per second. Access
                // failures are explicit; absence is not interpreted as zero CPU.
                if (Samples % 10 == 0)
                {
                    foreach (string name in new[] { "VBCSCompiler", "MsMpEng", "MpDefenderCoreService", "dotnet", "pwsh" })
                    {
                        foreach (var process in Process.GetProcessesByName(name))
                        {
                            using (process)
                            {
                                try { rows.Add(utc + ",ambient," + process.Id + ",0," + name + ",,," + process.TotalProcessorTime.Ticks + ",,,,"); }
                                catch (Exception ex) { rows.Add(utc + ",ambient," + process.Id + ",0," + name + "," + ex.GetType().Name + ",,,,,,"); }
                            }
                        }
                    }
                }
                rows.Add(utc + ",sample," + target.Id + ",0,observer,,,,,,," + sample.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
                Samples++;
                if (rows.Count > 50000) throw new InvalidOperationException("Observer row bound exceeded.");
                stop.WaitOne(Math.Max(1, 100 - (int)sample.ElapsedMilliseconds));
            }
        }
        catch (Exception ex) { if (!target.HasExited) Error = ex.GetType().Name + ": " + ex.Message; }
    }
    public void Dispose()
    {
        stop.Set();
        if (!worker.Join(3000)) throw new InvalidOperationException("Read-only observer did not stop within its cleanup budget.");
        File.WriteAllLines(path, rows);
        stop.Dispose();
        if (Error != null) throw new InvalidOperationException(Error);
    }
}
