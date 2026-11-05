using System.Text;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Reports;
using Win7POS.Core.Tests.Data;
using Win7POS.Data;
using Win7POS.Data.Reports;

namespace Win7POS.Core.Tests.Reports;

[TestClass]
public sealed class HistoricalSalesTimePolicyFileTests
{
    [TestMethod]
    public async Task ApprovedPolicyFile_VerifiesDeclaredHashAndPreservesFrozenUtcRules()
    {
        var policy = Policy();
        using var file = new PolicyFile(SalesReportTimePolicyRepository.SerializeApprovedFile(policy));
        var approved = await SalesReportTimePolicyRepository.ReadApprovedPolicyFileAsync(file.Path, CancellationToken.None);
        Assert.AreEqual(policy.Hash, approved.Policy.Hash);
        Assert.AreEqual("fixture-canonical-zone", approved.Policy.PolicyId);
        Assert.AreEqual(0, approved.Policy.Segments.Single().OffsetMinutes);
        Assert.AreEqual(86400000L, approved.Policy.Segments.Single().ToUtcMs);
        Assert.AreEqual(new FileInfo(file.Path).Length, approved.SourceBytes);
        using (await SalesReportTimePolicyRepository.VerifySourceAndHoldAsync(approved, CancellationToken.None)) { }
        File.AppendAllText(file.Path, " ");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SalesReportTimePolicyRepository.VerifySourceAndHoldAsync(approved, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("unknown-root")]
    [DataRow("duplicate-root")]
    [DataRow("unknown-segment")]
    [DataRow("fractional-ms")]
    [DataRow("numeric-string")]
    [DataRow("wrong-hash")]
    public async Task ApprovedPolicyFile_RejectsAmbiguousUnknownOrInexactContent(string mutation)
    {
        var policy = Policy(); var json = SalesReportTimePolicyRepository.SerializeApprovedFile(policy);
        json = mutation switch
        {
            "unknown-root" => json.Insert(1, "\"unexpected\":true,"),
            "duplicate-root" => json.Insert(1, "\"schemaVersion\":\"" + SalesReportTimePolicyRepository.ApprovedFileSchema + "\","),
            "unknown-segment" => json.Replace("\"fromUtcMs\":0", "\"unexpected\":1,\"fromUtcMs\":0", StringComparison.Ordinal),
            "fractional-ms" => json.Replace("\"fromUtcMs\":0", "\"fromUtcMs\":0.5", StringComparison.Ordinal),
            "numeric-string" => json.Replace("\"fromUtcMs\":0", "\"fromUtcMs\":\"0\"", StringComparison.Ordinal),
            "wrong-hash" => json.Replace(policy.Hash, new string('a', 64), StringComparison.Ordinal),
            _ => throw new AssertFailedException("Unknown test mutation.")
        };
        Assert.AreNotEqual(SalesReportTimePolicyRepository.SerializeApprovedFile(policy), json);
        using var file = new PolicyFile(json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SalesReportTimePolicyRepository.ReadApprovedPolicyFileAsync(file.Path, CancellationToken.None));
    }

    [TestMethod]
    public async Task ApprovedPolicyFile_RejectsOversizedSourceBeforeParsing()
    {
        using var file = new PolicyFile(new string(' ', SalesReportTimePolicyRepository.MaximumPolicyFileBytes + 1));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SalesReportTimePolicyRepository.ReadApprovedPolicyFileAsync(file.Path, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolicyActivation_IsGuardedAtomicAndInvalidatesReportRevision()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Win7POS-time-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var owned = OwnedSqliteTestFile.Create(System.IO.Path.Combine(directory, "policy.db"));
            DbInitializer.EnsureCreated(owned.Options);
            var factory = owned.Factory;
            using var connection = factory.Open();
            var dataset = connection.ExecuteScalar<string>("SELECT dataset_id FROM historical_import_dataset WHERE singleton_id=1")!;
            var before = connection.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1");
            var policy = Policy(); var commits = 0; var minimum = TimeSpan.Zero;
            var guard = new HistoricalImportAuthorizationCommitGuard(HistoricalImportOperationKind.Apply, "policy-activation", policy.Hash,
                "shop-a", "SHOP-A", "device", 7, dataset, factory.AuthorityStamp.RestoreEpoch, 1, "generation", "staff", 1,
                () => { }, (remaining, commit) => { minimum = remaining; commits++; commit(); });
            var repository = new SalesReportTimePolicyRepository(factory);
            await repository.SaveApprovedAsync("shop-a", policy, "approved synthetic rule manifest", guard, CancellationToken.None);
            Assert.AreEqual(1, commits);
            Assert.AreEqual(SqliteConnectionFactory.DurableCommitSafetyBudget, minimum);
            Assert.AreEqual(policy.Hash, (await repository.GetActiveAsync("shop-a", CancellationToken.None))!.Hash);
            var after = connection.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1");
            Assert.IsTrue(after > before);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => repository.SaveApprovedAsync("shop-b", policy, "evidence", guard, CancellationToken.None));
            Assert.AreEqual(policy.Hash, (await repository.GetActiveAsync("shop-a", CancellationToken.None))!.Hash);
            var secondGuard = new HistoricalImportAuthorizationCommitGuard(HistoricalImportOperationKind.Apply, "policy-activation-shop-b", policy.Hash,
                "shop-b", "SHOP-B", "device-b", 8, dataset, factory.AuthorityStamp.RestoreEpoch, 1, "generation-b", "staff-b", 1,
                () => { }, (_, commit) => commit());
            await repository.SaveApprovedAsync("shop-b", policy, "independent approval for shop B", secondGuard, CancellationToken.None);
            Assert.AreEqual(policy.Hash, (await repository.GetActiveAsync("shop-a", CancellationToken.None))!.Hash);
            Assert.AreEqual(policy.Hash, (await repository.GetActiveAsync("shop-b", CancellationToken.None))!.Hash);
            Assert.AreEqual(2L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_report_native_time_policy WHERE is_active=1"));
            Assert.AreEqual("approved synthetic rule manifest", connection.ExecuteScalar<string>("SELECT evidence_reference FROM sales_report_native_time_policy WHERE shop_id='shop-a'"));
            Assert.AreEqual("independent approval for shop B", connection.ExecuteScalar<string>("SELECT evidence_reference FROM sales_report_native_time_policy WHERE shop_id='shop-b'"));
            Assert.AreEqual(2L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE action='SalesReportTimePolicyActivate'"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static SalesReportTimePolicy Policy() => new("fixture-canonical-zone", "manifest-v1", "approved synthetic manifest", new[] { new SalesReportUtcSegment(0, 86400000, 0) });
    private sealed class PolicyFile : IDisposable
    {
        internal PolicyFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Win7POS-time-policy-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(Path, content, new UTF8Encoding(false));
        }
        internal string Path { get; }
        public void Dispose() => File.Delete(Path);
    }
}
