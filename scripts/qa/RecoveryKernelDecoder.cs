using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

// Uses only signed TraceEvent already installed with Visual Studio.
// This private diagnostic executable is never a product dependency.
public static class RecoveryKernelDecoder
{
    public static int Main(string[] args)
    {
        if (args.Length != 5) return 2;
        string libraryDirectory = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request)
        {
            string name = new AssemblyName(request.Name).Name;
            string path = Path.Combine(libraryDirectory, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        return Decode(args[1], args[2], int.Parse(args[3], CultureInfo.InvariantCulture), args[4]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Decode(string etl, string output, int pid, string threadIds)
    {
        var tids = new HashSet<int>();
        foreach (string id in threadIds.Split(',')) tids.Add(int.Parse(id, CultureInfo.InvariantCulture));
        var allowed = new HashSet<string>(("ProcessID ThreadID NewThreadID OldThreadID NewProcessID OldProcessID OldThreadState OldThreadWaitReason OldThreadWaitMode NewThreadPriority OldThreadPriority NewThreadWaitTime ElapsedTimeMSec ReadOffset VirtualAddress FileObject FileKey FileName OpenPath ByteCount IrpPtr NtStatus ExtraInfo IoSize TransferSize DiskNumber ByteOffset Offset IoFlags ImageBase ImageSize ImageChecksum DefaultBase DiskServiceTimeMSec IssuingThreadId").Split(' '), StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew(); int count = 0, switches = 0, faults = 0, io = 0;
        var json = new JavaScriptSerializer();
        using (var source = new ETWTraceEventSource(etl))
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
        {
            source.Kernel.All += delegate(TraceEvent e)
            {
                if (watch.Elapsed.TotalSeconds > 120) throw new InvalidOperationException("Kernel decoder exceeded finite parse budget.");
                if (e.ProcessID == pid && e.ThreadID > 0) tids.Add(e.ThreadID);
                var cs = e as CSwitchTraceData;
                bool selected = e.ProcessID == pid || tids.Contains(e.ThreadID) || (cs != null && (tids.Contains(cs.OldThreadID) || tids.Contains(cs.NewThreadID)));
                if (!selected) return;
                var data = new Dictionary<string, object>();
                foreach (string name in e.PayloadNames)
                {
                    if (!allowed.Contains(name)) continue;
                    object value = e.PayloadByName(name);
                    if (value is string && ((string)value).Length > 2048) continue;
                    if (value != null && value.GetType().IsEnum) value = value.ToString();
                    data[name] = value;
                }
                var fault = e as MemoryHardFaultTraceData;
                if (fault != null) { data["ElapsedTimeMSec"] = fault.ElapsedTimeMSec; data["FileName"] = fault.FileName; faults++; }
                var readWrite = e as FileIOReadWriteTraceData;
                if (readWrite != null) { data["FileName"] = readWrite.FileName; io++; }
                if (cs != null) switches++;
                writer.WriteLine(json.Serialize(new { utc = e.TimeStamp.ToUniversalTime().ToString("O"), relativeMs = e.TimeStampRelativeMSec, pid = e.ProcessID, tid = e.ThreadID, provider = e.ProviderGuid, eventName = e.EventName, opcodeName = e.OpcodeName, version = e.Version, pointerSize = e.PointerSize, data = data }));
                if (++count > 250000 || tids.Count > 128) throw new InvalidOperationException("Kernel target bound exceeded.");
            };
            source.Process();
            File.WriteAllText(output + ".receipt.json", json.Serialize(new { events = count, contextSwitches = switches, hardFaults = faults, fileReadsWrites = io, eventsLost = source.EventsLost, targetPid = pid, rawPublished = false, library = typeof(ETWTraceEventSource).Assembly.FullName }));
            if (source.EventsLost != 0 || count == 0 || switches == 0) return 3;
        }
        return 0;
    }
}
