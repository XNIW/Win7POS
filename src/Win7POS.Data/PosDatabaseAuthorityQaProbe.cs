using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Win7POS.Data
{
    /// <summary>Finite, token-bound bootstrap witness. Never opens SQLite or initializes a database.</summary>
    public static class PosDatabaseAuthorityQaProbe
    {
        [DataContract]
        private sealed class Context
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
            [DataMember(Name = "runId")] public string RunId { get; set; }
            [DataMember(Name = "runNonce")] public string RunNonce { get; set; }
            [DataMember(Name = "rootDirectory")] public string RootDirectory { get; set; }
            [DataMember(Name = "fixtureToken")] public string FixtureToken { get; set; }
        }

        [DataContract]
        private sealed class Proof
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; } = "win7pos-authority-probe-v1";
            [DataMember(Name = "runId")] public string RunId { get; set; }
            [DataMember(Name = "runNonce")] public string RunNonce { get; set; }
            [DataMember(Name = "entryPoint")] public string EntryPoint { get; set; }
            [DataMember(Name = "stage")] public string Stage { get; set; }
            [DataMember(Name = "pid")] public int Pid { get; set; }
            [DataMember(Name = "creationTimeUtc")] public string CreationTimeUtc { get; set; }
            [DataMember(Name = "writeStarted")] public bool WriteStarted { get; set; }
            [DataMember(Name = "databaseIdentityHash")] public string DatabaseIdentityHash { get; set; }
        }

        public static bool TryRun(string[] args, PosDbOptions options, string entryPoint, out int exitCode)
        {
            exitCode = 2;
            if (args == null || !args.Contains("--qa-authority-probe", StringComparer.Ordinal)) return false;
            try
            {
                if (!PosDatabasePathIdentity.IsWindows || options == null ||
                    !new[] { "wpf-normal", "wpf-safe", "cli", "harness" }.Contains(entryPoint, StringComparer.Ordinal) ||
                    Environment.GetEnvironmentVariable("WIN7POS_ADMIN_WEB_BASE_URL") != "http://127.0.0.1:9") return true;
                var contextPath = Argument(args, "--qa-context");
                var mode = Argument(args, "--qa-probe-mode");
                if (mode != "Hold" && mode != "Attempt") return true;
                var context = Read<Context>(contextPath);
                if (context.SchemaVersion != "win7pos-authority-probe-context-v1" ||
                    !Regex.IsMatch(context.RunId ?? "", @"^Win7POS\.QA\.\d{8}T\d{9}Z\.[a-f0-9]{32}$") ||
                    !Regex.IsMatch(context.RunNonce ?? "", "^[a-f0-9]{32}$") ||
                    !Regex.IsMatch(context.FixtureToken ?? "", "^[a-f0-9]{32}$")) return true;
                var root = Path.GetFullPath(context.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var work = Directory.GetParent(root);
                if (work == null || !Regex.IsMatch(work.Name, @"^W7Q\.[A-Za-z0-9_-]{22}$") ||
                    !File.Exists(Path.Combine(work.FullName, "qa-work.json")) ||
                    !Path.GetFullPath(options.DbPath).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                PosDatabasePathIdentity.RejectReparseComponents(root);
                var db = Path.GetFullPath(options.DbPath);
                if (!File.Exists(db) || new FileInfo(db).Length == 0 ||
                    !string.Equals(File.ReadAllText(db + ".synthetic-owner"), context.FixtureToken, StringComparison.Ordinal)) return true;
                var ready = OwnedPath(Argument(args, "--qa-ready"), root);
                var release = OwnedPath(Argument(args, "--qa-release"), root);
                var receipt = OwnedPath(Argument(args, "--qa-receipt"), root);
                using (var process = Process.GetCurrentProcess())
                {
                    var proof = new Proof { RunId = context.RunId, RunNonce = context.RunNonce, EntryPoint = entryPoint, Pid = process.Id,
                        CreationTimeUtc = process.StartTime.ToUniversalTime().ToString("O"), DatabaseIdentityHash = DatabaseIdentityHash(db) };
                    PosDatabaseAuthorityLease owner;
                    try { owner = PosDatabaseAuthorityLease.AcquireOperational(options); }
                    catch (PosDatabaseAuthorityException error) when (error.Code == "database_in_use")
                    { proof.Stage = "denied_before_init"; Write(receipt, proof); exitCode = 78; return true; }
                    using (owner)
                    {
                        if (mode == "Attempt") { proof.Stage = "acquired_before_init"; Write(receipt, proof); exitCode = 0; return true; }
                        proof.Stage = "held"; Write(receipt, proof); Write(ready, proof);
                        var clock = Stopwatch.StartNew();
                        while (!File.Exists(release))
                        {
                            if (clock.ElapsedMilliseconds >= 115000) { proof.Stage = "hold_timeout"; Write(receipt, proof); return true; }
                            Thread.Sleep(20);
                        }
                        PosDatabasePathIdentity.RejectReparseComponents(release);
                        if (!string.Equals(File.ReadAllText(release), context.RunNonce, StringComparison.Ordinal)) return true;
                        proof.Stage = "released"; Write(receipt, proof); exitCode = 0; return true;
                    }
                }
            }
            catch { return true; } // No exception/cell/path is printed by a QA bootstrap fallback.
        }

        public static string DatabaseIdentityHash(string db)
        {
            var identity = PosDatabasePathIdentity.FileIdentity(db, false);
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
        }

        private static string Argument(string[] args, string key)
        {
            var indexes = Enumerable.Range(0, args.Length).Where(i => args[i] == key).ToArray();
            if (indexes.Length != 1 || indexes[0] + 1 >= args.Length) throw new ArgumentException("qa_probe_argument_invalid");
            return args[indexes[0] + 1];
        }
        private static string OwnedPath(string path, string root)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("qa_probe_path_invalid");
            PosDatabasePathIdentity.RejectReparseComponents(full); return full;
        }
        private static T Read<T>(string path)
        {
            PosDatabasePathIdentity.RejectReparseComponents(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 16384) throw new InvalidOperationException("qa_probe_context_invalid");
            using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        private static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
}
