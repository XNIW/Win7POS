using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Win7POS.Core;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Typed sidecar only. Original result files, assertions and private failures remain intact.
    internal static class TypedQaDiagnosticWriter
    {
        private static readonly object Gate = new object();
        private static readonly List<Event> Events = new List<Event>();
        private static string _scenario;
        private static string _stepOverride;
        private static bool _dropped;
        internal static void BeginScenario(string scenario, string stepId = null) { lock (Gate) { _scenario = scenario; if (stepId != null) _stepOverride = stepId; Flush(); } }

        internal static void RecordAssertion(bool condition, object expected = null, object actual = null,
            [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0, string observationId = null)
        {
            lock (Gate)
            {
                if (Events.Count >= 2048)
                {
                    _dropped = true;
                    if (condition) return;
                    var pass = Events.FindIndex(item => item.Status == "PASS");
                    Events.RemoveAt(pass >= 0 ? pass : 0);
                    for (var index = 0; index < Events.Count; index++) Events[index].Sequence = index;
                }
                Events.Add(new Event { Sequence = Events.Count, StepId = Step(), ScenarioId = _scenario,
                    AssertionId = "QA_" + Path.GetFileNameWithoutExtension(sourceFile) + "_L" + sourceLine.ToString(CultureInfo.InvariantCulture),
                    ObservationId = observationId,
                    Status = condition ? "PASS" : "FAIL", Expected = Scalar(expected ?? true), Actual = Scalar(actual ?? condition),
                    Checkpoint = "assertion", Code = condition ? "none" : "assertion_failed" });
                if (!condition) Flush();
            }
        }

        internal static void RecordException(Exception error)
        {
            var frames = new StackTrace(error, true).GetFrames() ?? Array.Empty<StackFrame>();
            var frame = frames.FirstOrDefault(item => item.GetMethod()?.DeclaringType?.Namespace == typeof(TypedQaDiagnosticWriter).Namespace && item.GetMethod()?.Name != "Require" && item.GetMethod()?.Name != "RequireDenied");
            var type = frame?.GetMethod()?.DeclaringType;
            while (type?.DeclaringType != null) type = type.DeclaringType;
            RecordAssertion(false, true, false, frame?.GetFileName() ?? (type != null ? type.Name + ".cs" : "unknown.cs"), frame?.GetFileLineNumber() ?? 0);
            try { File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "qa-diagnostics.private-failures.txt"), DateTimeOffset.UtcNow.ToString("O") + " scenario=" + _scenario + Environment.NewLine + error + Environment.NewLine); } catch { }
        }

        internal static void CompleteScenario(bool passed, Exception error = null)
        {
            if (error != null) RecordException(error);
            lock (Gate)
            {
                if (!passed && !Events.Any(item => item.Status == "FAIL")) _dropped = true;
                Flush();
            }
        }

        internal static void RecordResult(string stepId, string result,
            [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0)
        {
            _stepOverride = stepId;
            var passed = result != null && result.StartsWith("PASS", StringComparison.Ordinal);
            RecordAssertion(passed, true, passed, sourceFile, sourceLine, "scenario_success");
            var flags = stepId == "product-image-profile" ? new[] { "product_image_profile_dpapi_isolated", "shared_unchanged", "plaintext_secrets", "offline_authority_cloned", "supervisor_profile_injected", "cleanup_exact", "staging_diagnostic_redacted", "net48_request_serialization", "net48_json_stringify_response", "net48_storage_error_mapping", "net48_code128_roundtrip" }
                : stepId == "bounded-logging" ? new[] { "worker_alive_after_shutdown" } : Array.Empty<string>();
            foreach (var flag in flags)
            {
                var match = Regex.Match(result ?? "", @"(?<![A-Za-z0-9_])" + Regex.Escape(flag) + @"=(true|false)(?=[\s;.]|$)", RegexOptions.IgnoreCase);
                if (!match.Success) { _dropped = true; continue; }
                var actual = string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                var expected = flag != "plaintext_secrets" && flag != "offline_authority_cloned" && flag != "worker_alive_after_shutdown";
                RecordAssertion(actual == expected, expected, actual, sourceFile, sourceLine, flag);
            }
            if (stepId == "bounded-logging")
                foreach (var metric in new[] { new { Id = "producer_p95_us", Limit = 5000d }, new { Id = "producer_max_us", Limit = 250000d } })
                {
                    var match = Regex.Match(result ?? "", @"(?<![A-Za-z0-9_])" + metric.Id + @"=([0-9]+(?:\.[0-9]+)?)(?=[\s;.]|$)");
                    if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var actual)) { _dropped = true; continue; }
                    RecordAssertion(actual <= metric.Limit, metric.Limit, actual, sourceFile, sourceLine, metric.Id);
                }
            CompleteScenario(passed);
        }

        private static string Step()
        {
            var value = Environment.GetEnvironmentVariable("WIN7POS_QA_DIAGNOSTIC_STEP");
            if (string.IsNullOrWhiteSpace(value)) value = _stepOverride;
            return new[] { "product-image-profile", "functional-completion", "authorization-lease", "bounded-logging", "product-paging-dispatcher" }.Contains(value, StringComparer.Ordinal) ? value : "unknown";
        }
        private static object Scalar(object value)
        {
            if (value == null || value is bool || value is int || value is long || value is decimal) return value;
            if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number)) return value;
            if (value is string code && new[] { "clock_rollback", "last_server_time_invalid", "local_receipt_time_invalid", "offline_attestation_invalid", "offline_attestation_required", "offline_lease_expired", "session_expiry_invalid", "session_window_invalid", "trusted_session_missing", "trusted_time_continuity_lost", "sync_generation_inactive" }.Contains(code, StringComparer.Ordinal)) return code;
            _dropped = true; return "unavailable";
        }
        private static void Flush()
        {
            try
            {
                var context = new Envelope
                {
                    RunId = Environment.GetEnvironmentVariable("WIN7POS_QA_RUN_ID"), SourceFingerprintSha256 = Environment.GetEnvironmentVariable("WIN7POS_QA_SOURCE_FINGERPRINT"),
                    HarnessSha256 = HashAssembly(), Results = Events.ToArray(), DiagnosticCompleteness = _dropped ? "partial" : "complete"
                };
                HistoricalSalesQaReceipt.WriteJson(Path.Combine(AppPaths.DataDirectory, "qa-diagnostics.json"), context);
            }
            catch { _dropped = true; } // Never hide an original test failure or disclose raw text on console.
        }
        private static string HashAssembly()
        { using (var stream = File.OpenRead(typeof(TypedQaDiagnosticWriter).Assembly.Location)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        [DataContract] private sealed class Envelope
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion = "win7pos-qa-diagnostics-v1";
            [DataMember(Name = "runId")] public string RunId;
            [DataMember(Name = "sourceFingerprintSha256")] public string SourceFingerprintSha256;
            [DataMember(Name = "harnessSha256")] public string HarnessSha256;
            [DataMember(Name = "diagnosticCompleteness")] public string DiagnosticCompleteness;
            [DataMember(Name = "results")] public Event[] Results;
        }
        [DataContract] private sealed class Event
        {
            [DataMember(Name = "sequence")] public int Sequence;
            [DataMember(Name = "stepId")] public string StepId;
            [DataMember(Name = "scenarioId", EmitDefaultValue = false)] public string ScenarioId;
            [DataMember(Name = "assertionId")] public string AssertionId;
            [DataMember(Name = "observationId", EmitDefaultValue = false)] public string ObservationId;
            [DataMember(Name = "status")] public string Status;
            [DataMember(Name = "expected")] public object Expected;
            [DataMember(Name = "actual")] public object Actual;
            [DataMember(Name = "checkpoint")] public string Checkpoint;
            [DataMember(Name = "code")] public string Code;
        }
    }
}
