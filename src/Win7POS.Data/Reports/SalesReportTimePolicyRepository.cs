using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Reports;
using Win7POS.Core.Audit;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Reports
{
    public sealed partial class SalesReportTimePolicyRepository
    {
        private readonly SqliteConnectionFactory _factory;
        public SalesReportTimePolicyRepository(SqliteConnectionFactory factory) { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); }
        public async Task<SalesReportTimePolicy> GetActiveAsync(string shopId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(shopId)) throw new ArgumentException("An authoritative shop is required.");
            using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            var row = await connection.QuerySingleOrDefaultAsync<PolicyRow>(new CommandDefinition(@"SELECT policy_hash AS Hash,CASE WHEN length(policy_json)<=262144 THEN policy_json ELSE NULL END AS Json
FROM sales_report_native_time_policy WHERE shop_id=@shopId COLLATE NOCASE AND is_active=1", new { shopId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (row == null) return null;
            if (string.IsNullOrWhiteSpace(row.Json)) throw new InvalidOperationException("report_time_policy_invalid");
            var policy = Deserialize(row.Json);
            if (policy.Hash != row.Hash) throw new InvalidOperationException("report_time_policy_hash_mismatch");
            return policy;
        }
        public async Task SaveApprovedAsync(string shopId, SalesReportTimePolicy policy, string evidenceReference, HistoricalImportAuthorizationCommitGuard guard, CancellationToken cancellationToken, string sourceSha256 = null)
        {
            if (policy == null || guard == null || string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 500 ||
                guard.Kind != HistoricalImportOperationKind.Apply || guard.ShopId != shopId || guard.RequestFingerprint != policy.Hash) throw new InvalidOperationException("report_time_policy_authorization_invalid");
            if (sourceSha256 != null && (sourceSha256.Length != 64 || sourceSha256.Any(character => !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f'))))
                throw new ArgumentException("report_time_policy_source_hash_invalid");
            var json = Serialize(policy);
            var coordinator = SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath);
            using (var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var fence = SqliteConnectionFactory.AcquireExclusiveCommitFence(connection))
            using (var transaction = connection.BeginTransaction())
            {
                guard.DemandStillValid();
                var dataset = await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT dataset_id FROM historical_import_dataset WHERE singleton_id=1", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (dataset != guard.DatasetId || _factory.AuthorityStamp.RestoreEpoch != guard.RestoreEpoch) throw new ReportSnapshotStaleException();
                await connection.ExecuteAsync(new CommandDefinition("UPDATE sales_report_native_time_policy SET is_active=0 WHERE shop_id=@shopId COLLATE NOCASE AND is_active=1", new { shopId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                var written = await connection.ExecuteAsync(new CommandDefinition(@"INSERT INTO sales_report_native_time_policy(policy_hash,shop_id,policy_version,evidence_reference,policy_json,is_active)
VALUES(@hash,@shopId,@version,@evidenceReference,@json,1)
ON CONFLICT(shop_id,policy_hash) DO UPDATE SET is_active=1 WHERE policy_json=excluded.policy_json",
                    new { hash = policy.Hash, shopId, version = policy.Version, evidenceReference, json }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (written != 1) throw new InvalidOperationException("report_time_policy_identity_conflict");
                await new AuditLogRepository().AppendAsync(connection, transaction, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "SalesReportTimePolicyActivate",
                    AuditDetails.Kv(("operationId", guard.OperationId), ("shopId", shopId), ("policyHash", policy.Hash),
                        ("operatorId", guard.OperatorId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("evidence", evidenceReference), ("sourceSha256", sourceSha256))).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                guard.DemandStillValid();
                var authority = _factory.AuthorityStamp;
                // No connection opening, transaction admission or writes while holding the publication gate.
                using (await coordinator.EnterPublicationAsync(cancellationToken).ConfigureAwait(false))
                    coordinator.CommitAndPublishReportRevision(connection, transaction, () => guard.CommitIfStillValid(SqliteConnectionFactory.DurableCommitSafetyBudget, () => transaction.Commit()), authority);
            }
        }
        public static string Serialize(SalesReportTimePolicy policy)
        {
            var body = new Body { Id = policy.PolicyId, Version = policy.Version, Provenance = policy.Provenance, Segments = policy.Segments.Select(segment => new Segment { From = segment.FromUtcMs, To = segment.ToUtcMs, Offset = segment.OffsetMinutes }).ToArray() };
            using var stream = new MemoryStream(); new DataContractJsonSerializer(typeof(Body)).WriteObject(stream, body);
            var result = Encoding.UTF8.GetString(stream.ToArray()); if (result.Length > 262144) throw new InvalidOperationException("report_time_policy_limit"); return result;
        }
        public static SalesReportTimePolicy Deserialize(string json)
        {
            if (json == null || json.Length > 262144) throw new InvalidOperationException("report_time_policy_limit");
            return ParseStrictPolicy(ReadStrictJson(json));
        }
        private sealed class PolicyRow { public string Hash { get; set; } public string Json { get; set; } }
        [DataContract] private sealed class Body
        {
            [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; }
            [DataMember(Name = "version", IsRequired = true)] public string Version { get; set; }
            [DataMember(Name = "provenance", IsRequired = true)] public string Provenance { get; set; }
            [DataMember(Name = "segments", IsRequired = true)] public Segment[] Segments { get; set; }
        }
        [DataContract] private sealed class Segment
        {
            [DataMember(Name = "fromUtcMs", IsRequired = true)] public long From { get; set; }
            [DataMember(Name = "toUtcMs", IsRequired = true)] public long To { get; set; }
            [DataMember(Name = "offsetMinutes", IsRequired = true)] public int Offset { get; set; }
        }
    }
}
