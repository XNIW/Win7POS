using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;

// Raw host traces remain private. Export only the identified synthetic target's
// events and the file names needed to interpret its own I/O and hard faults.
public static class RecoveryKernelDecoder
{
    static XmlNode Node(XmlNode node, string name) { return node.SelectSingleNode("*[local-name()='" + name + "']"); }
    static string Text(XmlNode node, string name) { return Node(node, name)?.InnerText.Trim() ?? ""; }
    static uint Number(string text)
    {
        uint value;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ? value : 0)
            : (uint.TryParse(text, out value) ? value : 0);
    }
    static IEnumerable<XmlDocument> Events(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using (var reader = XmlReader.Create(path, settings))
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Event") continue;
                using (var sub = reader.ReadSubtree())
                {
                    var document = new XmlDocument { XmlResolver = null }; document.Load(sub); yield return document;
                }
            }
    }
    static Dictionary<string, string> Payload(XmlDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlNode data in document.SelectNodes("//*[local-name()='EventData']/*[local-name()='Data']"))
            result[data.Attributes["Name"]?.Value ?? ""] = data.InnerText;
        foreach (XmlNode data in document.SelectNodes("//*[local-name()='UserData']/*/*")) result[data.LocalName] = data.InnerText;
        return result;
    }
    static string Get(Dictionary<string, string> data, string name) { return data.TryGetValue(name, out var value) ? value : ""; }
    public static int Export(string xmlPath, string output, uint targetPid, HashSet<uint> tids)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var document in Events(xmlPath))
        {
            if (watch.Elapsed.TotalSeconds > 120) throw new InvalidOperationException("Kernel first-pass parse budget exceeded.");
            var data = Payload(document);
            string name = Get(data, "FileName"), key = Get(data, "FileObject");
            if (name.Length > 0 && key.Length > 0) files[key] = name;
            if (Number(Get(data, "ProcessId")) == targetPid && Number(Get(data, "ThreadId")) != 0) tids.Add(Number(Get(data, "ThreadId")));
            if (files.Count > 100000 || tids.Count > 128) throw new InvalidOperationException("Kernel mapping bound exceeded.");
        }
        var allowed = new HashSet<string>(("ProcessId ThreadId TThreadId NewThreadId OldThreadId OldThreadState OldThreadWaitReason OldThreadWaitMode NewThreadPriority OldThreadPriority NewThreadWaitTime InitialTime ReadOffset VirtualAddress FileObject FileKey FileName OpenPath ByteCount IrpPtr NtStatus ExtraInfo IoSize TransferSize DiskNumber ByteOffset Offset IoFlags EventTimeStamp TimeStamp ImageBase ImageSize ImageChecksum DefaultBase ResponseTime HighResResponseTime IssuingThreadId").Split(' '), StringComparer.OrdinalIgnoreCase);
        int count = 0;
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
        foreach (var document in Events(xmlPath))
        {
            if (watch.Elapsed.TotalSeconds > 240) throw new InvalidOperationException("Kernel total parse budget exceeded.");
            var root = document.DocumentElement; var system = Node(root, "System"); var execution = Node(system, "Execution");
            var data = Payload(document);
            uint pid = Number(execution?.Attributes["ProcessID"]?.Value ?? ""), tid = Number(execution?.Attributes["ThreadID"]?.Value ?? "");
            bool selected = pid == targetPid || tids.Contains(tid);
            foreach (var field in new[] { "ThreadId", "TThreadId", "OldThreadId", "NewThreadId", "IssuingThreadId" })
                selected |= tids.Contains(Number(Get(data, field)));
            if (!selected) continue;
            var safe = new Dictionary<string, string>();
            foreach (var pair in data)
                if ((allowed.Contains(pair.Key) || (pair.Key.StartsWith("Stack", StringComparison.Ordinal) && pair.Key.Length <= 7)) && pair.Value.Length < 2048) safe[pair.Key] = pair.Value;
            if (files.TryGetValue(Get(data, "FileObject"), out var file)) safe["ResolvedFileName"] = file;
            var rendering = Node(root, "RenderingInfo");
            var row = new { utc = Node(system,"TimeCreated")?.Attributes["SystemTime"]?.Value, pid, tid,
                provider = Node(system,"Provider")?.Attributes["Guid"]?.Value, providerName = Node(system,"Provider")?.Attributes["Name"]?.Value,
                id = Text(system,"EventID"), version = Text(system,"Version"), opcode = Text(system,"Opcode"),
                opcodeName = rendering == null ? "" : Text(rendering,"Opcode"), task = rendering == null ? "" : Text(rendering,"Task"), data = safe };
            writer.WriteLine(JsonSerializer.Serialize(row));
            if (++count > 250000) throw new InvalidOperationException("Kernel target event bound exceeded.");
        }
        return count;
    }
}
