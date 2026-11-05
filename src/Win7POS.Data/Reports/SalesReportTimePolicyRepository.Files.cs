using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Win7POS.Core.Reports;

namespace Win7POS.Data.Reports
{
    public sealed class ApprovedSalesReportTimePolicy
    {
        internal ApprovedSalesReportTimePolicy(string sourcePath, string sha256, long bytes, SalesReportTimePolicy policy)
        { SourcePath = sourcePath; SourceSha256 = sha256; SourceBytes = bytes; Policy = policy; }
        internal string SourcePath { get; }
        public string SourceFileName => Path.GetFileName(SourcePath);
        public string SourceSha256 { get; }
        public long SourceBytes { get; }
        public SalesReportTimePolicy Policy { get; }
    }

    public sealed partial class SalesReportTimePolicyRepository
    {
        public const string ApprovedFileSchema = "win7pos-native-time-policy-v1";
        public const int MaximumPolicyFileBytes = 262144;
        public static Task<ApprovedSalesReportTimePolicy> ReadApprovedPolicyFileAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(filePath) || !string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("report_time_policy_file_invalid");
                var path = Path.GetFullPath(filePath);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                if (stream.Length < 2 || stream.Length > MaximumPolicyFileBytes) throw new InvalidOperationException("report_time_policy_limit");
                using var memory = new MemoryStream(); var buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (memory.Length + read > MaximumPolicyFileBytes) throw new InvalidOperationException("report_time_policy_limit");
                    memory.Write(buffer, 0, read);
                }
                var bytes = memory.ToArray();
                var json = new UTF8Encoding(false, true).GetString(bytes);
                if (json.Length > 0 && json[0] == '\ufeff') json = json.Substring(1);
                var root = ReadStrictJson(json);
                var fields = ObjectFields(root, "schemaVersion", "policyHash", "policy");
                if (ReadText(fields["schemaVersion"], 80) != ApprovedFileSchema) throw new InvalidOperationException("report_time_policy_file_schema");
                var declaredHash = ReadText(fields["policyHash"], 64);
                var policy = ParseStrictPolicy(fields["policy"]);
                if (declaredHash.Length != 64 || !string.Equals(declaredHash, policy.Hash, StringComparison.Ordinal))
                    throw new InvalidOperationException("report_time_policy_hash_mismatch");
                using var sha = SHA256.Create();
                return new ApprovedSalesReportTimePolicy(path, Hex(sha.ComputeHash(bytes)), bytes.LongLength, policy);
            }, cancellationToken);
        }
        public static string SerializeApprovedFile(SalesReportTimePolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            return "{\"schemaVersion\":\"" + ApprovedFileSchema + "\",\"policyHash\":\"" + policy.Hash + "\",\"policy\":" + Serialize(policy) + "}";
        }
        /// <summary>Rechecks the reviewed bytes and holds a no-write/no-delete handle until the caller finishes activation.</summary>
        public static async Task<IDisposable> VerifySourceAndHoldAsync(ApprovedSalesReportTimePolicy approved, CancellationToken cancellationToken)
        {
            if (approved == null) throw new ArgumentNullException(nameof(approved));
            var stream = new FileStream(approved.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            try
            {
                if (stream.Length != approved.SourceBytes || stream.Length > MaximumPolicyFileBytes) throw new InvalidOperationException("report_time_policy_source_changed");
                using var sha = SHA256.Create(); var buffer = new byte[8192]; int read; long total = 0;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested(); total = checked(total + read);
                    if (total > approved.SourceBytes || total > MaximumPolicyFileBytes) throw new InvalidOperationException("report_time_policy_source_changed");
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (total != approved.SourceBytes || Hex(sha.Hash) != approved.SourceSha256) throw new InvalidOperationException("report_time_policy_source_changed");
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        private static XElement ReadStrictJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumPolicyFileBytes) throw new InvalidOperationException("report_time_policy_limit");
            var quota = new XmlDictionaryReaderQuotas { MaxDepth = 12, MaxStringContentLength = MaximumPolicyFileBytes, MaxArrayLength = 2048, MaxBytesPerRead = 8192, MaxNameTableCharCount = 8192 };
            using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), quota);
            return XDocument.Load(reader, LoadOptions.None).Root ?? throw new InvalidOperationException("report_time_policy_invalid");
        }
        private static Dictionary<string, XElement> ObjectFields(XElement element, params string[] required)
        {
            if (element.Attribute("type")?.Value != "object" || element.Attributes().Any(attribute => attribute.Name != "type")) throw new InvalidOperationException("report_time_policy_object_invalid");
            var allowed = new HashSet<string>(required, StringComparer.Ordinal); var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var field in element.Elements())
                if (field.Name.NamespaceName.Length != 0 || !allowed.Contains(field.Name.LocalName) || result.ContainsKey(field.Name.LocalName)) throw new InvalidOperationException("report_time_policy_unknown_or_duplicate_field");
                else result.Add(field.Name.LocalName, field);
            if (result.Count != required.Length) throw new InvalidOperationException("report_time_policy_missing_field");
            return result;
        }
        private static string ReadText(XElement element, int maximum)
        {
            if (element.Attribute("type")?.Value != "string" || element.HasElements || element.Attributes().Any(attribute => attribute.Name != "type")) throw new InvalidOperationException("report_time_policy_text_invalid");
            var text = element.Value;
            if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) throw new InvalidOperationException("report_time_policy_text_limit");
            return text;
        }
        private static long ReadInteger(XElement element)
        {
            if (element.Attribute("type")?.Value != "number" || element.HasElements || element.Attributes().Any(attribute => attribute.Name != "type") ||
                !long.TryParse(element.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) throw new InvalidOperationException("report_time_policy_integer_invalid");
            return value;
        }
        private static SalesReportTimePolicy ParseStrictPolicy(XElement element)
        {
            var fields = ObjectFields(element, "id", "version", "provenance", "segments");
            var array = fields["segments"];
            if (array.Attribute("type")?.Value != "array" || array.Attributes().Any(attribute => attribute.Name != "type")) throw new InvalidOperationException("report_time_policy_segments_invalid");
            var items = array.Elements().ToArray();
            if (items.Length < 1 || items.Length > 2048 || items.Any(item => item.Name != "item")) throw new InvalidOperationException("report_time_policy_segment_limit");
            var segments = new List<SalesReportUtcSegment>(items.Length);
            foreach (var item in items)
            {
                var properties = ObjectFields(item, "fromUtcMs", "toUtcMs", "offsetMinutes");
                segments.Add(new SalesReportUtcSegment(ReadInteger(properties["fromUtcMs"]), ReadInteger(properties["toUtcMs"]), checked((int)ReadInteger(properties["offsetMinutes"]))));
            }
            return new SalesReportTimePolicy(ReadText(fields["id"], 160), ReadText(fields["version"], 80), ReadText(fields["provenance"], 2048), segments);
        }
    }
}
