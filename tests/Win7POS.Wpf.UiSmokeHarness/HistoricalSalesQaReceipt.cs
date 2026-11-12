using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Win7POS.Wpf.UiSmokeHarness
{
    [DataContract]
    internal sealed class HistoricalSalesQaContext
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
        [DataMember(Name = "runId")] public string RunId { get; set; }
        [DataMember(Name = "runNonce")] public string RunNonce { get; set; }
        [DataMember(Name = "sourceSha")] public string SourceSha { get; set; }
        [DataMember(Name = "sourceFingerprintSha256")] public string SourceFingerprintSha256 { get; set; }
        [DataMember(Name = "harnessSha256")] public string HarnessSha256 { get; set; }
        [DataMember(Name = "wpfSha256")] public string WpfSha256 { get; set; }
        [DataMember(Name = "fixtureVersion")] public string FixtureVersion { get; set; }
        [DataMember(Name = "fixtureSha256")] public string FixtureSha256 { get; set; }
        [DataMember(Name = "timePolicyVersion")] public string TimePolicyVersion { get; set; }
        [DataMember(Name = "measurementTargetProfile", EmitDefaultValue = false)] public string MeasurementTargetProfile { get; set; }
        [DataMember(Name = "measurementTargetIdentitySha256", EmitDefaultValue = false)] public string MeasurementTargetIdentitySha256 { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaAssertion
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "expected", EmitDefaultValue = false)] public object Expected { get; set; }
        [DataMember(Name = "actual", EmitDefaultValue = false)] public object Actual { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaResult
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "rawStatus")] public string RawStatus { get; set; } = "NOT_RUN";
        [DataMember(Name = "completed")] public bool Completed { get; set; }
        [DataMember(Name = "durationMs")] public double DurationMs { get; set; }
        [DataMember(Name = "assertionResults")] public List<HistoricalSalesQaAssertion> AssertionResults { get; set; } = new List<HistoricalSalesQaAssertion>();
        [DataMember(Name = "counters", EmitDefaultValue = false)] public Dictionary<string, long> Counters { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaSummary
    {
        [DataMember(Name = "expected")] public int Expected { get; set; }
        [DataMember(Name = "executed")] public int Executed { get; set; }
        [DataMember(Name = "passed")] public int Passed { get; set; }
        [DataMember(Name = "failed")] public int Failed { get; set; }
        [DataMember(Name = "notRun")] public int NotRun { get; set; }
        [DataMember(Name = "timedOut")] public int TimedOut { get; set; }
        [DataMember(Name = "checksTotal")] public int ChecksTotal { get; set; }
        [DataMember(Name = "checksPassed")] public int ChecksPassed { get; set; }
        [DataMember(Name = "checksFailed")] public int ChecksFailed { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaProcess
    {
        [DataMember(Name = "pid")] public int Pid { get; set; }
        [DataMember(Name = "creationTimeUtc")] public string CreationTimeUtc { get; set; }
        [DataMember(Name = "architecture")] public string Architecture { get; set; }
        [DataMember(Name = "clr")] public string Clr { get; set; }
        [DataMember(Name = "os")] public string Os { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaSample
    {
        [DataMember(Name = "sequence")] public int Sequence { get; set; }
        [DataMember(Name = "phase")] public string Phase { get; set; }
        [DataMember(Name = "phaseSequence")] public int PhaseSequence { get; set; }
        [DataMember(Name = "monotonicTicks")] public long MonotonicTicks { get; set; }
        [DataMember(Name = "dueTicks")] public long DueTicks { get; set; }
        [DataMember(Name = "probeId")] public int ProbeId { get; set; }
        [DataMember(Name = "postedTicks")] public long PostedTicks { get; set; }
        [DataMember(Name = "completedTicks", EmitDefaultValue = false)] public long? CompletedTicks { get; set; }
        [DataMember(Name = "pendingAgeMs")] public double PendingAgeMs { get; set; }
        [DataMember(Name = "dropped")] public int Dropped { get; set; }
        [DataMember(Name = "suspended")] public int Suspended { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaMemorySample
    {
        [DataMember(Name = "sequence")] public int Sequence { get; set; }
        [DataMember(Name = "phase")] public string Phase { get; set; }
        [DataMember(Name = "phaseSequence")] public int PhaseSequence { get; set; }
        [DataMember(Name = "monotonicTicks")] public long MonotonicTicks { get; set; }
        [DataMember(Name = "dueTicks")] public long DueTicks { get; set; }
        [DataMember(Name = "privateBytes")] public long PrivateBytes { get; set; }
        [DataMember(Name = "dropped")] public int Dropped { get; set; }
        [DataMember(Name = "suspended")] public int Suspended { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaPhase
    {
        [DataMember(Name = "sequence")] public int Sequence { get; set; }
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "startTicks")] public long StartTicks { get; set; }
        [DataMember(Name = "endTicks")] public long EndTicks { get; set; }
        [DataMember(Name = "expectedSlots")] public int ExpectedSlots { get; set; }
        [DataMember(Name = "expectedMemorySlots")] public int ExpectedMemorySlots { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaTiming
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "startTicks")] public long StartTicks { get; set; }
        [DataMember(Name = "endTicks")] public long EndTicks { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaMeasurement
    {
        [DataMember(Name = "protocol")] public string Protocol { get; set; } = "HISTORY-MEASUREMENT-v2";
        [DataMember(Name = "startTicks")] public long StartTicks { get; set; }
        [DataMember(Name = "endTicks")] public long EndTicks { get; set; }
        [DataMember(Name = "dispatcherIntervalMs")] public int DispatcherIntervalMs { get; set; } = 50;
        [DataMember(Name = "memoryIntervalMs")] public int MemoryIntervalMs { get; set; } = 100;
        [DataMember(Name = "dispatcherPriority")] public string DispatcherPriority { get; set; } = "Background";
        [DataMember(Name = "samplerKind")] public string SamplerKind { get; set; } = "dedicated-thread";
        [DataMember(Name = "targetProfile")] public string TargetProfile { get; set; } = "reference-windows";
        [DataMember(Name = "targetIdentitySha256", EmitDefaultValue = false)] public string TargetIdentitySha256 { get; set; }
    }

    [DataContract]
    internal sealed class HistoricalSalesQaEnvelope
    {
        [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
        [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; }
        [DataMember(Name = "runId")] public string RunId { get; set; }
        [DataMember(Name = "runNonce")] public string RunNonce { get; set; }
        [DataMember(Name = "sourceSha")] public string SourceSha { get; set; }
        [DataMember(Name = "sourceFingerprintSha256")] public string SourceFingerprintSha256 { get; set; }
        [DataMember(Name = "harnessSha256")] public string HarnessSha256 { get; set; }
        [DataMember(Name = "wpfSha256")] public string WpfSha256 { get; set; }
        [DataMember(Name = "fixtureVersion")] public string FixtureVersion { get; set; }
        [DataMember(Name = "fixtureSha256")] public string FixtureSha256 { get; set; }
        [DataMember(Name = "timePolicyVersion")] public string TimePolicyVersion { get; set; }
        [DataMember(Name = "startedAtUtc")] public string StartedAtUtc { get; set; }
        [DataMember(Name = "completedAtUtc", EmitDefaultValue = false)] public string CompletedAtUtc { get; set; }
        [DataMember(Name = "durationMs")] public double DurationMs { get; set; }
        [DataMember(Name = "startedTicks")] public long StartedTicks { get; set; }
        [DataMember(Name = "completedTicks", EmitDefaultValue = false)] public long? CompletedTicks { get; set; }
        [DataMember(Name = "completed")] public bool Completed { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; } = "NOT_RUN";
        [DataMember(Name = "process")] public HistoricalSalesQaProcess Process { get; set; }
        [DataMember(Name = "summary")] public HistoricalSalesQaSummary Summary { get; set; }
        [DataMember(Name = "results")] public List<HistoricalSalesQaResult> Results { get; set; } = new List<HistoricalSalesQaResult>();
        [DataMember(Name = "lastCheckpoint")] public string LastCheckpoint { get; set; } = "starting";
        [DataMember(Name = "measurementCompleted", EmitDefaultValue = false)] public bool? MeasurementCompleted { get; set; }
        [DataMember(Name = "environmentValid", EmitDefaultValue = false)] public bool? EnvironmentValid { get; set; }
        [DataMember(Name = "thresholdsPassed", EmitDefaultValue = false)] public bool? ThresholdsPassed { get; set; }
        [DataMember(Name = "metrics", EmitDefaultValue = false)] public Dictionary<string, double> Metrics { get; set; }
        [DataMember(Name = "samples", EmitDefaultValue = false)] public List<HistoricalSalesQaSample> Samples { get; set; }
        [DataMember(Name = "memorySamples", EmitDefaultValue = false)] public List<HistoricalSalesQaMemorySample> MemorySamples { get; set; }
        [DataMember(Name = "phases", EmitDefaultValue = false)] public List<HistoricalSalesQaPhase> Phases { get; set; }
        [DataMember(Name = "timings", EmitDefaultValue = false)] public List<HistoricalSalesQaTiming> Timings { get; set; }
        [DataMember(Name = "measurement", EmitDefaultValue = false)] public HistoricalSalesQaMeasurement Measurement { get; set; }
    }

    internal sealed class HistoricalSalesQaCase
    {
        private readonly HistoricalSalesQaReceipt _owner;
        private readonly string[] _required;
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        internal HistoricalSalesQaCase(HistoricalSalesQaReceipt owner, HistoricalSalesQaResult result, string[] required)
        { _owner = owner; Result = result; _required = required; }
        internal HistoricalSalesQaResult Result { get; }
        internal bool Complete => _seen.Count == _required.Length;
        internal void PrivateDiagnostic(string text) => _owner.PrivateDiagnostic(Result.Id, text);
        internal void Check(string id, bool condition, object expected = null, object actual = null)
        {
            if (!_required.Contains(id, StringComparer.Ordinal) || !_seen.Add(id)) throw new InvalidOperationException("qa_history_assertion_id_invalid");
            Result.AssertionResults.Add(new HistoricalSalesQaAssertion { Id = id, Status = condition ? "PASS" : "FAIL", Expected = SafeScalar(expected ?? true), Actual = SafeScalar(actual ?? condition) });
            _owner.WriteCheckpoint();
        }
        internal void Counter(string name, long value)
        {
            if (!new[] { "rows", "sales", "cancellations", "rejected", "accepted", "new", "duplicates", "conflicts", "balanceClp", "nativeRows", "historicalRows", "revisionBefore", "revisionAfter" }.Contains(name, StringComparer.Ordinal)) throw new InvalidOperationException("qa_history_counter_invalid");
            if (name != "balanceClp" && value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (Result.Counters == null) Result.Counters = new Dictionary<string, long>();
            Result.Counters[name] = value;
        }
        private static object SafeScalar(object value)
        {
            if (value == null || value is bool || value is int || value is long || value is decimal) return value;
            if (value is double number && !double.IsNaN(number) && !double.IsInfinity(number)) return value;
            if (value is string text && new[] { "PASS", "FAIL", "absent", "present", "denied", "allowed", "unchanged", "unavailable", "x86", "DELETE", "FULL" }.Contains(text, StringComparer.Ordinal)) return text;
            return "unavailable";
        }
    }

    internal sealed class HistoricalSalesQaReceipt
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Dictionary<string, string[]> _contract;
        private readonly string _path;
        internal HistoricalSalesQaReceipt(string contextPath, string mode, string receiptPath)
        {
            Context = LoadContext(contextPath);
            Mode = mode;
            _path = Path.GetFullPath(receiptPath);
            _contract = Contract(mode);
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                Envelope = new HistoricalSalesQaEnvelope
                {
                    SchemaVersion = mode == "CloudSync" ? "win7pos-historical-cloud-sync-v1" : mode == "Load" ? "win7pos-historical-sales-load-v2" : mode == "Smoke" ? "win7pos-historical-sales-qa-v3" : "win7pos-historical-sales-foundation-v3",
                    ProtocolVersion = mode == "CloudSync" ? 1 : mode == "Load" ? 2 : 3,
                    RunId = Context.RunId, RunNonce = Context.RunNonce, SourceSha = Context.SourceSha, SourceFingerprintSha256 = Context.SourceFingerprintSha256,
                    HarnessSha256 = Context.HarnessSha256, WpfSha256 = Context.WpfSha256, FixtureVersion = Context.FixtureVersion, FixtureSha256 = Context.FixtureSha256, TimePolicyVersion = Context.TimePolicyVersion,
                    StartedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), StartedTicks = Stopwatch.GetTimestamp(), Summary = new HistoricalSalesQaSummary { Expected = _contract.Count },
                    Process = new HistoricalSalesQaProcess { Pid = process.Id, CreationTimeUtc = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Architecture = Environment.Is64BitProcess ? "x64" : "x86", Clr = Environment.Version.ToString(), Os = Environment.OSVersion.Version.ToString() }
                };
            }
            WriteCheckpoint();
        }
        internal string Mode { get; }
        internal HistoricalSalesQaContext Context { get; }
        internal HistoricalSalesQaEnvelope Envelope { get; }
        internal void PrivateDiagnostic(string scenario, string text)
        {
            try { File.AppendAllText(_path + ".private-failures.txt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                " scenario=" + scenario + " diagnostic=" + text + Environment.NewLine, new UTF8Encoding(false)); }
            catch { /* Preserve the assertion result when private diagnostics cannot be written. */ }
        }
        internal async Task<bool> RunAsync(string id, Func<HistoricalSalesQaCase, Task> body)
        {
            if (!_contract.TryGetValue(id, out var required) || Envelope.Results.Any(x => x.Id == id)) throw new InvalidOperationException("qa_history_scenario_id_invalid");
            var result = new HistoricalSalesQaResult { Id = id };
            Envelope.Results.Add(result); Envelope.LastCheckpoint = id; WriteCheckpoint();
            var test = new HistoricalSalesQaCase(this, result, required);
            var timer = Stopwatch.StartNew();
            try
            {
                await body(test).ConfigureAwait(true);
                result.Completed = test.Complete;
                result.RawStatus = test.Complete && result.AssertionResults.All(x => x.Status == "PASS") ? "PASS" : "FAIL";
            }
            catch (Exception error)
            {
                result.RawStatus = "FAIL"; result.Completed = false;
                // Full diagnostics remain in the owned private evidence tree.
                // Public serializers never inspect this basename or this text.
                try { File.AppendAllText(_path + ".private-failures.txt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " scenario=" + id + Environment.NewLine + error + Environment.NewLine, new UTF8Encoding(false)); }
                catch { /* The raw case remains FAIL even if secondary diagnosis IO fails. */ }
            }
            result.DurationMs = timer.Elapsed.TotalMilliseconds; WriteCheckpoint();
            return result.RawStatus == "PASS";
        }
        internal int Complete(bool partialCase = false)
        {
            Envelope.Completed = true; Envelope.CompletedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture); Envelope.CompletedTicks = Stopwatch.GetTimestamp(); Envelope.LastCheckpoint = "completed";
            Envelope.Status = Envelope.Results.Count > 0 && (partialCase || Envelope.Results.Count == _contract.Count) && Envelope.Results.All(x => x.Completed && x.RawStatus == "PASS") ? "PASS" : "FAIL";
            WriteCheckpoint(); return Envelope.Status == "PASS" ? 0 : 1;
        }
        internal void WriteCheckpoint()
        {
            Envelope.DurationMs = _watch.Elapsed.TotalMilliseconds;
            Envelope.Summary.Executed = Envelope.Results.Count;
            Envelope.Summary.Passed = Envelope.Results.Count(x => x.RawStatus == "PASS"); Envelope.Summary.Failed = Envelope.Results.Count(x => x.RawStatus == "FAIL");
            Envelope.Summary.NotRun = Envelope.Results.Count(x => x.RawStatus == "NOT_RUN"); Envelope.Summary.TimedOut = Envelope.Results.Count(x => x.RawStatus == "TIMEOUT");
            Envelope.Summary.ChecksTotal = Envelope.Results.Sum(x => x.AssertionResults.Count); Envelope.Summary.ChecksPassed = Envelope.Results.Sum(x => x.AssertionResults.Count(a => a.Status == "PASS")); Envelope.Summary.ChecksFailed = Envelope.Results.Sum(x => x.AssertionResults.Count(a => a.Status == "FAIL"));
            WriteJson(_path, Envelope);
        }
        internal static HistoricalSalesQaContext LoadContext(string path)
        {
            var context = ReadJson<HistoricalSalesQaContext>(path);
            if (context.SchemaVersion != "win7pos-historical-sales-context-v1" || !Regex.IsMatch(context.RunId ?? "", @"^Win7POS\.QA\.\d{8}T\d{9}Z\.[a-f0-9]{32}$") || !Regex.IsMatch(context.RunNonce ?? "", "^[a-f0-9]{32}$") || !Regex.IsMatch(context.SourceSha ?? "", "^[a-f0-9]{40}$") || context.FixtureVersion != "historical-sales-getnet-v1" || context.TimePolicyVersion != "synthetic-fixed-offset-v1") throw new InvalidOperationException("qa_history_context_invalid");
            foreach (var hash in new[] { context.SourceFingerprintSha256, context.HarnessSha256, context.WpfSha256, context.FixtureSha256 }) if (!Regex.IsMatch(hash ?? "", "^[a-f0-9]{64}$")) throw new InvalidOperationException("qa_history_context_invalid");
            if (context.MeasurementTargetProfile != null && context.MeasurementTargetProfile != "reference-windows" && context.MeasurementTargetProfile != "win7-sp1") throw new InvalidOperationException("qa_history_context_invalid");
            if (context.MeasurementTargetIdentitySha256 != null && !Regex.IsMatch(context.MeasurementTargetIdentitySha256, "^[a-f0-9]{64}$")) throw new InvalidOperationException("qa_history_context_invalid");
            if (context.MeasurementTargetProfile == "win7-sp1" && context.MeasurementTargetIdentitySha256 == null) throw new InvalidOperationException("qa_history_context_invalid");
            return context;
        }
        internal static T ReadJson<T>(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 2 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("qa_history_json_invalid");
            using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(stream);
        }
        internal static void WriteJson<T>(string path, T value)
        {
            var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var memory = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(memory, value);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { var bytes = memory.ToArray(); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            }
            if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full);
        }
        internal static Dictionary<string, string[]> Contract(string mode)
        {
            if(mode=="CloudSync")return new Dictionary<string,string[]>(StringComparer.Ordinal){["HIST_C_OPEN_REPLICA"]=new[]{"open_view_convergence"}};
            if (mode == "Smoke") return new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["HIST01_PROFILES_PREVIEW"] = new[] { "profiles_detected", "header_mapping", "exact_values", "text_ids", "policy_visible", "invalid_input_blocked" },
                ["HIST02_APPLY_REOPEN"] = new[] { "durable_receipt", "manifest_totals", "reopened_records", "operational_invariants" },
                ["HIST03_IDENTITY_DEDUP"] = new[] { "unchanged_zero_new", "renamed_reordered_overlap_zero", "distinct_ids_kept", "conflict_blocked" },
                ["HIST04_STALE_AUTH_SCOPE"] = new[] { "permission_revoked", "operator_changed", "shop_changed", "epoch_changed", "precommit_revocation" },
                ["HIST05_ATOMIC_CANCEL_RECOVERY"] = new[] { "cancel_rollback", "precommit_fault_rollback", "disk_fault_safe", "lost_response_reconciled" },
                ["HIST06_BATCH_CANCEL_REIMPORT"] = new[] { "owner_only_undo", "duplicate_batch_not_owner", "three_cycles", "audit_retained" },
                ["HIST07_REPORTS_EXPORT"] = new[] { "signed_totals", "hourly_parity", "operator_scope", "tenant_scope", "detail_routing", "csv_contract", "native_ticket_average", "snapshot_invalidation", "export_publication_authorization", "export_publication_outcome", "coverage_state_no_disclosure", "hourly_signed_geometry" },
                ["HIST08_UI_LIFECYCLE"] = new[] { "keyboard_focus", "dialog_standard", "nonblocking_dispatcher", "stale_result_ignored", "reopened_receipt",
                    "retry_empty_ui", "retry_expiry_ui", "retry_revocation_ui", "retry_generation_ui", "retry_cancel_ui",
                    "retry_normal_late_authority_ui", "retry_normal_finalization_fault_ui", "retry_normal_ready_ui" }
            };
            if (mode == "Load") return new Dictionary<string, string[]>(StringComparer.Ordinal) { ["HIST_LOAD_50K"] = new[] { "fixture_identity", "exact_counters", "phase_measurements", "memory_budget", "dispatcher_budget", "cancellation_budget", "mixed_query_budget", "ownership_cold_warm", "snapshot_invalidation", "maintenance_writer_exclusion", "environment_identity" } };
            if (mode != "CrashProbe") throw new ArgumentException("qa_history_mode_invalid");
            return new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["HIST_CRASH_HOT_JOURNAL"] = new[] { "same_existing_database", "prepared_sentinel", "reached_after_spill", "expected_abrupt_exit", "journal_recovery_completed", "atomic_data_and_revision", "operational_invariants", "owned_process_cleanup" },
                ["HIST_B_COMMIT_AUTHORITY"] = new[] { "foreground_preserves_history_cloud_writer_closed", "foreground_preserves_auth_denied", "foreground_preserves_history_cloud_access_denied", "foreground_preserves_history_cloud_unavailable", "foreground_preserves_history_response_invalid", "apply_revoke_before_insert", "apply_revoke_precommit", "undo_revoke_precommit", "expiry_before_commit", "operator_permission_changed", "linearized_commit_once" },
                ["HIST_B_PROCESS_ADMISSION"] = new[] { "real_holder_contender", "sqlite_busy_bounded", "workflow_cancel_no_orphan", "auth_cancel_no_orphan", "maintenance_cancel_no_orphan", "lock_order_no_deadlock", "commands_cart_restored" },
                ["HIST_B_ENTRYPOINT_AUTHORITY"] = new[] { "wpf_normal_rejected_before_init", "wpf_safe_rejected_before_init", "cli_rejected_before_seed", "harness_rejected_before_init", "distinct_databases_admitted", "case_shortpath_alias_collide", "reparse_hardlink_rejected", "restart_reacquires" },
                ["HIST_B_BACKUP_RESTORE"] = new[] { "backup_validate_publish", "aux_scope_reject", "candidate_swap_rebind", "rollback_restore", "epoch_stales_plan", "mapping_fk_preserved", "reimport_zero", "all_handles_released" }
            };
        }
    }
}
