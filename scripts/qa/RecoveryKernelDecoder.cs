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
        int primaryTid = 0, omittedWorkerSwitches = 0, omittedWorkerStacks = 0;
        var allowed = new HashSet<string>(("ProcessID ThreadID NewThreadID OldThreadID NewProcessID OldProcessID OldThreadState OldThreadWaitReason OldThreadWaitMode NewThreadPriority OldThreadPriority NewThreadWaitTime ElapsedTimeMSec ReadOffset VirtualAddress FileObject FileKey FileName OpenPath ByteCount IrpPtr NtStatus ExtraInfo IoSize TransferSize DiskNumber ByteOffset Offset IoFlags ImageBase ImageSize ImageChecksum DefaultBase DiskServiceTimeMSec IssuingThreadId").Split(' '), StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew(); int count = 0, switches = 0, faults = 0, io = 0;
        var json = new JavaScriptSerializer();
        using (var source = new ETWTraceEventSource(etl))
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
        {
            source.Kernel.All += delegate(TraceEvent e)
            {
                if (watch.Elapsed.TotalSeconds > 120) throw new InvalidOperationException("Kernel decoder exceeded finite parse budget.");
                // The first target Thread/Start is the process entry/UI thread.
                // Keep all target paging/I/O, but avoid exporting render-worker
                // scheduling stacks which exhausted the bound before 60 seconds.
                if (primaryTid == 0 && e.ProcessID == pid && e.EventName == "Thread/Start") primaryTid = e.ThreadID;
                if (e.ProcessID == pid && e.ThreadID > 0) tids.Add(e.ThreadID);
                var cs = e as CSwitchTraceData;
                bool selected = e.ProcessID == pid || tids.Contains(e.ThreadID) || (cs != null && (tids.Contains(cs.OldThreadID) || tids.Contains(cs.NewThreadID)));
                if (!selected) return;
                if (cs != null && cs.OldThreadID != primaryTid && cs.NewThreadID != primaryTid) { omittedWorkerSwitches++; return; }
                if (e is StackWalkStackTraceData && e.ThreadID != primaryTid) { omittedWorkerStacks++; return; }
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
                if (cs != null)
                {
                    // The installed reader recognizes v5 but its old getters
                    // choose the v1 wait-field offsets. v2+ has a stable 24-byte
                    // prefix, verified against Microsoft's current kernel parser.
                    byte[] raw = e.EventData();
                    if (e.Version < 2 || raw.Length < 24) throw new InvalidOperationException("Unsupported context-switch layout.");
                    data["NewThreadID"] = BitConverter.ToInt32(raw, 0);
                    data["OldThreadID"] = BitConverter.ToInt32(raw, 4);
                    data["OldThreadWaitReason"] = raw[12];
                    data["OldThreadWaitMode"] = raw[13];
                    data["OldThreadState"] = raw[14];
                    data["NewThreadWaitTime"] = BitConverter.ToInt32(raw, 16);
                    data["RawMetadataBase64"] = Convert.ToBase64String(raw, 0, Math.Min(32, raw.Length));
                    switches++;
                }
                if (fault != null && e.EventDataLength <= 64)
                    data["RawMetadataBase64"] = Convert.ToBase64String(e.EventData());
                var stack = e as StackWalkStackTraceData;
                if (stack != null)
                {
                    var addresses = new List<string>();
                    for (int i = 0; i < Math.Min(stack.FrameCount, 96); i++) addresses.Add(stack.InstructionPointer(i).ToString("X", CultureInfo.InvariantCulture));
                    data["EventTimeStampRelativeMSec"] = stack.EventTimeStampRelativeMSec;
                    data["Frames"] = addresses;
                    data["FramesTruncated"] = stack.FrameCount > 96;
                }
                writer.WriteLine(json.Serialize(new { utc = e.TimeStamp.ToUniversalTime().ToString("O"), relativeMs = e.TimeStampRelativeMSec, pid = e.ProcessID, tid = e.ThreadID, provider = e.ProviderGuid, eventName = e.EventName, opcodeName = e.OpcodeName, version = e.Version, pointerSize = e.PointerSize, data = data }));
                if (++count > 250000 || tids.Count > 128) throw new InvalidOperationException("Kernel target bound exceeded.");
            };
            source.Process();
            File.WriteAllText(output + ".receipt.json", json.Serialize(new { events = count, contextSwitches = switches, hardFaults = faults, fileReadsWrites = io, eventsLost = source.EventsLost, targetPid = pid, primaryThreadId = primaryTid, omittedWorkerSwitches, omittedWorkerStacks, rawPublished = false, library = typeof(ETWTraceEventSource).Assembly.FullName }));
            if (source.EventsLost != 0 || count == 0 || switches == 0 || primaryTid == 0) return 3;
        }
        return 0;
    }
}
