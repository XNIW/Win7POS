using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Fixture = Win7POS.Core.Tests.Data.CatalogImportSupersessionTests.Fixture;
using SyntheticPeer = Win7POS.Core.Tests.Data.CatalogImportSupersessionTests.SyntheticPeer;

namespace Win7POS.Core.Tests.Data;

/// <summary>
/// Real builder/SQLite/journal/HTTP generation. The stateful peer is synthetic;
/// only a subsequent unchanged-byte Admin/SQL replay can qualify the contract.
/// Accepted predecessor events are real C# HTTP apply calls, never seeded ACKs.
/// </summary>
[TestClass]
public sealed class CatalogImportSupersessionInteropEvidenceTests
{
    private const int Count = 1001;

    [TestMethod]
    [DataRow("successor")]
    [DataRow("retirement_reply_lost")]
    [DataRow("zerochild")]
    [DataRow("zerochild_reply_lost")]
    public Task Supersession_ExportsSequentialActualWireAndPersistentConvergence(string scenario) => RunAsync(scenario, null);

    [TestMethod]
    [DataRow("successor")]
    [DataRow("retirement_reply_lost")]
    [DataRow("zerochild")]
    [DataRow("zerochild_reply_lost")]
    public async Task RecordedAdminSupersessionResponses_ReenterAllExactRequestsAndConverge(string scenario)
    {
        using var recorded = new RecordedResponses(scenario);
        await RunAsync(scenario, recorded);
    }

    [TestMethod]
    [DataRow("successor")]
    [DataRow("retirement_reply_lost")]
    [DataRow("zerochild")]
    [DataRow("zerochild_reply_lost")]
    public async Task HistoricalAdminPlanWithoutPartCounts_IsRejectedBeforeFirstChildRetirement(string scenario)
    {
        using var recorded = new RecordedResponses(scenario, historicalMissingItemCount: true);
        var error = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => RunAsync(scenario, recorded));
        Assert.AreEqual("receipt_conflict", error.Code);
        recorded.VerifyRejectedBeforeFirstChildRetirement();
    }

    private static async Task RunAsync(string scenario, RecordedResponses? recorded)
    {
        using var fixture = new Fixture();
        using var peer = new SyntheticPeer();
        using var evidence = new Capture(recorded?.HistoricalMissingItemCount == true ? scenario + "-historical-missing-count" : scenario, fixture, recorded != null);
        peer.ExchangeObserved = (route, request, response, dropped) =>
        { recorded?.VerifyObserved(route, request, response, dropped); evidence.Observe(route, request, response, dropped); };
        if (recorded != null)
        {
            peer.ResponseBytesOverride = (route, request, ignoredSyntheticResponse) => recorded.Response(route, request);
            peer.ChildCanonicalHash = recorded.CanonicalHash;
        }
        else if (evidence.HasAdminParser) peer.ChildCanonicalHash = evidence.CanonicalHash;
        var zeroChild = scenario.StartsWith("zerochild", StringComparison.Ordinal);
        var applier = new SupplierExcelImportApplier(fixture.Factory);
        var preview = await applier.BuildPreviewAsync(Enumerable.Range(0, Count).Select(CatalogImportInteropEvidenceTests.Row).ToArray());
        var createdAt = recorded?.CreatedAt ?? DateTimeOffset.UtcNow.ToString("O");
        preview.OperationCreatedAtUtc = createdAt;
        var original = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "supersession-interop.xlsx", "test");
        var initial = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = original });
        Assert.AreEqual(0, initial.Errors);
        var rootId = initial.CatalogImportOutboxId;
        evidence.Write("original.persisted.json", fixture.Saved(rootId));
        Assert.AreEqual(original.PayloadHash, Hash(fixture.Saved(rootId)));
        if (recorded != null) Assert.AreEqual(recorded.OriginalJson, fixture.Saved(rootId));
        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1,last_error_code='synthetic_legacy_response_lost' WHERE id=@id", new { id = rootId });
            connection.Execute("UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id", new { id = rootId });
        }
        evidence.State("legacy-uncertain-seeded", rootId);
        var service = fixture.Service();
        evidence.Phase = "root-proof-lookup-retirement";
        var draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
        Assert.IsFalse(draft.CanCommit);
        await service.RetireAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        draft.OperationCreatedAtUtc = createdAt;
        draft.Rows[0].RetailPrice = "1300";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        evidence.Phase = "predecessor-registration-response-lost";
        peer.DropNextPlanResponse = true;
        var error = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
        Assert.AreEqual("receipt_unavailable", error.Code);
        var frozen = fixture.Text("SELECT plan_document_json FROM catalog_import_prepared_plan");
        var predecessor = Read<PosCatalogImportRecoveryPlanDocument>(frozen);
        Assert.AreEqual(2, predecessor.Parts.Length);
        AssertBeforeCommit(fixture, rootId, original.PayloadJson);
        evidence.Write("predecessor.document.json", frozen);
        evidence.State("predecessor-registered-reply-lost", rootId);

        // These represent a different executor completing frozen remote work.
        // The calls use the real DTO, authentication projection and HTTP client;
        // the local database deliberately receives no ACK from that executor.
        evidence.Phase = "external-executor-real-http-apply";
        foreach (var part in predecessor.Parts.Take(zeroChild ? 2 : 1))
        {
            using var client = new PosAdminWebClient(peer.Options);
            var request = CatalogImportRecoveryProofTransport.Authenticate(new PosCatalogImportRecoveryApplyRequest
                { PlanId = predecessor.PlanId, PartIndex = part.Index }, Trusted());
            var result = await client.CatalogImportRecoveryApplyAsync(request, CancellationToken.None);
            Assert.IsTrue(result.Success, result.Code); Assert.AreEqual("accepted", result.Value.Status);
            Assert.AreEqual(part.Request.Batch.ClientImportId, result.Value.ClientImportId);
        }
        AssertBeforeCommit(fixture, rootId, original.PayloadJson);
        evidence.State("external-apply-no-local-ack", rootId);
        draft.Rows[0].RetailPrice = "1400";
        if (!zeroChild) draft.Rows[1000].RetailPrice = "1500";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        evidence.Phase = "restart-settle-predecessor";
        service = fixture.Restart();
        draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
        if (scenario == "retirement_reply_lost")
        {
            peer.DropNextRetirement = (predecessor.PlanId, 1);
            if (recorded?.HistoricalMissingItemCount == true)
            {
                // The old real response must fail its count validation before
                // either child's retirement is sent, independently of the
                // later lost-reply scenario. Keep the production exception.
                await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
                Assert.Fail("Historical plan response without itemCount was accepted.");
            }
            error = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.RetirePreparedPlanAsync(
                draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None));
            Assert.AreEqual("receipt_retirement_unavailable", error.Code);
            var settled = Read<PosCatalogImportReceiptResponse[]>(fixture.Text("SELECT settlement_json FROM catalog_import_recovery_supersession"));
            Assert.AreEqual("accepted", settled[0].Status); Assert.IsNull(settled[1]);
            AssertBeforeCommit(fixture, rootId, original.PayloadJson);
            evidence.State("second-retirement-reply-lost", rootId);
            service = fixture.Restart();
            draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
            Assert.IsTrue(draft.HasPreparedPlan); Assert.IsFalse(draft.CanCommit);
        }
        draft = await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        Assert.AreEqual(zeroChild ? Count : 1000, draft.Supersession.CarryCoverage.Count);
        AssertBeforeCommit(fixture, rootId, original.PayloadJson);
        evidence.State(recorded == null ? "predecessor-authoritatively-settled-by-synthetic-peer" : "predecessor-settled-by-actual-admin-recorded-receipts", rootId);
        evidence.Phase = "successor-registration";
        if (scenario == "zerochild_reply_lost")
        {
            peer.DropNextPlanResponse = true;
            error = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
            Assert.AreEqual("receipt_unavailable", error.Code);
            var empty = fixture.Text("SELECT plan_document_json FROM catalog_import_prepared_plan");
            Assert.AreEqual(0, Read<PosCatalogImportRecoveryPlanDocument>(empty).Parts.Length);
            evidence.Write("zerochild.prepared-before-restart.json", empty);
            evidence.State("zerochild-registration-reply-lost", rootId);
            AssertBeforeCommit(fixture, rootId, original.PayloadJson);
            draft.Rows[0].RetailPrice = "1450";
            await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
            service = fixture.Restart();
            draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
            Assert.AreEqual(empty, fixture.Text("SELECT plan_document_json FROM catalog_import_prepared_plan"));
            evidence.Phase = "zerochild-retry-exact-registration";
        }
        var committed = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, committed.Errors);
        var successor = peer.Plans.Values.Last();
        Assert.AreEqual(predecessor.PlanId, successor.Supersedes.PlanId);
        Assert.AreEqual(zeroChild ? 0 : 1, successor.Parts.Length);
        Assert.AreEqual(zeroChild ? 0 : 1, successor.Supersedes.RetiredChildren.Length);
        Assert.AreEqual(Count, successor.Coverage.Length);
        if (!zeroChild)
        {
            Assert.AreEqual(peer.PartHash(predecessor.PlanId, 1), successor.Supersedes.RetiredChildren[0].CanonicalPayloadHash);
            Assert.AreEqual("INTEROP-1000", successor.Parts[0].Request.Items.Single().Barcode);
            Assert.IsTrue(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
            Assert.AreEqual("failed_blocked", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = rootId }));
            var partial = await service.GetPlanProgressAsync(rootId, CancellationToken.None);
            Assert.AreEqual(Count, partial.TotalRows); Assert.AreEqual(1000, partial.CompletedRows);
            Assert.AreEqual(2, partial.TotalParts); Assert.AreEqual(1, partial.CompletedParts);
        }
        else
        {
            Assert.IsTrue(committed.RecoveryAlreadyConverged);
            Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_plan"));
        }
        evidence.Write("successor.document.json", peer.PlanDocuments.Last());
        evidence.State("local-commit-complete", rootId);
        evidence.Phase = "successor-local-sync";
        await CatalogImportSupersessionTests.DrainAsync(fixture, peer, zeroChild ? 0 : 1);
        if (!zeroChild)
        {
            var complete = await service.GetPlanProgressAsync(rootId, CancellationToken.None);
            Assert.AreEqual(Count, complete.CompletedRows); Assert.AreEqual(2, complete.CompletedParts);
        }
        Assert.AreEqual(zeroChild ? 2 : 1, peer.ApplyRequests.Count(request => request.Plan == predecessor.PlanId));
        Assert.AreEqual(zeroChild ? 0 : 1, peer.ApplyRequests.Count(request => request.Plan == successor.PlanId));
        Assert.AreEqual(original.PayloadJson, fixture.Saved(rootId));
        Assert.AreEqual("recovered", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = rootId }));
        Assert.AreEqual(2 * Count + (zeroChild ? 1 : 2), fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(Count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
        Assert.AreEqual(Count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE purchase_price=900"));
        Assert.AreEqual(zeroChild ? 1000 : 999, fixture.Number("SELECT COUNT(*) FROM products WHERE unitPrice=1200"));
        Assert.AreEqual(1300, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
        Assert.AreEqual(zeroChild ? 1200 : 1500, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-1000'"));
        Assert.IsFalse(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
        var owner = fixture.Number("SELECT id FROM catalog_import_outbox WHERE client_import_id=@id", new { id = predecessor.Parts[0].Request.Batch.ClientImportId });
        var saved = await fixture.Restart().PrepareLocalAsync(owner, Trusted(), null!, CancellationToken.None);
        Assert.IsTrue(saved.HasSavedDraft); Assert.AreEqual(scenario == "zerochild_reply_lost" ? "1450" : "1400", saved.Rows[0].RetailPrice);
        var retries = peer.PlanDocuments.Where(document => Read<PosCatalogImportRecoveryPlanDocument>(document).PlanId == successor.PlanId).ToArray();
        Assert.IsTrue(retries.All(document => document == retries[0]));
        Assert.AreEqual(scenario == "zerochild_reply_lost" ? 2 : 1, retries.Length);
        if (scenario == "retirement_reply_lost") evidence.AssertIdenticalRetirementRetry(predecessor.PlanId, 1);
        if (recorded != null)
        {
            recorded.VerifyFinal(fixture);
            evidence.Write("actual-database-reingest.json", JsonSerializer.Serialize(new { scenario,
                responseArchiveSha256 = RecordedResponses.AdminZipHash, requestArchiveSha256 = RecordedResponses.SourceZipHash,
                requestsVerified = recorded.RequestCount, productsVerified = Count, priceIdentitiesVerified = 2 * Count,
                parentClosed = true, originalUnchanged = true, deferredDraftPreserved = true,
                scope = "actual Admin handlers and isolated economic PostgreSQL; synthetic outer auth/dependencies; no deployed acceptance" }));
        }
        evidence.State("closed-with-deferred-operator-draft", rootId);
        evidence.Complete(new { scenario, rowCount = Count, operationCreatedAtUtc = createdAt, rootId, originalPayloadHash = original.PayloadHash,
            originalClientImportId = original.ClientImportId, originalIdempotencyKey = original.IdempotencyKey,
            predecessorPlanId = predecessor.PlanId, successorPlanId = successor.PlanId, deferredDraftOwnerId = owner,
            actualExternalApplyCount = zeroChild ? 2 : 1, finalLocalHistoryCount = 2 * Count + (zeroChild ? 1 : 2) });
    }

    private static void AssertBeforeCommit(Fixture fixture, long rootId, string original)
    {
        Assert.AreEqual(original, fixture.Saved(rootId)); Assert.AreEqual(1, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(2 * Count, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(Count, fixture.Number("SELECT COUNT(*) FROM products WHERE unitPrice=1200"));
        Assert.AreEqual(Count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
    }
    private static PosTrustedDeviceSession Trusted() => CatalogImportSupersessionTests.Trusted();
    private static string Hash(string text) => CatalogImportOutboxPayloadBuilder.Sha256Hex(text);
    private static string Write<T>(T value) => CatalogImportRecoveryService.Serialize(value);
    private static T Read<T>(string value) => CatalogImportRecoveryService.Deserialize<T>(value);

    private sealed class RecordedResponses : IDisposable
    {
        internal const string SourceZipHash = "fc77839f807f3870f34db4f99447c3cda2c82b7827ae46560bfee06caae97d44";
        internal const string AdminZipHash = "7baef9303b7be9c20ae4dc183aae0e305c4948efea035a7ab42dc3521d876797";
        private const string HistoricalAdminZipHash = "d603a60e720f44590e35429fc5832e3e4e42da9be0b56acfacbd9c9198b9a3e0";
        private readonly ZipArchive source;
        private readonly ZipArchive admin;
        private readonly string scenario;
        private readonly string sourcePrefix;
        private readonly JsonElement[] exchanges;
        private readonly JsonElement[] responses;
        private readonly JsonElement sourceScenario;
        private readonly JsonElement serverCase;
        private readonly Dictionary<string, string> products = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> prices = new(StringComparer.Ordinal);
        private int nextIndex;
        internal string CreatedAt => sourceScenario.GetProperty("operationCreatedAtUtc").GetString()!;
        internal string OriginalJson => Text(source, sourcePrefix + "original.persisted.json");
        internal int RequestCount => responses.Length;
        internal bool HistoricalMissingItemCount { get; }

        internal RecordedResponses(string scenario, bool historicalMissingItemCount = false)
        {
            this.scenario = scenario; HistoricalMissingItemCount = historicalMissingItemCount; sourcePrefix = "supersession-" + scenario + "/";
            var fixtureRoot = Path.Combine(Capture.FindSourceRoot(), "tests", "fixtures", "pos-catalog-import-wire-v1");
            source = Open(Path.Combine(fixtureRoot, "candidate-supersession", "requests-and-synthetic-generation-evidence.zip"), SourceZipHash);
            admin = historicalMissingItemCount
                ? Open(Path.Combine(fixtureRoot, "admin-db-3da549a5", "supersession-115-handler-db-responses.zip"), HistoricalAdminZipHash)
                : Open(Path.Combine(fixtureRoot, "admin-db-2a34851c", "supersession-115-c827-handler-db-responses.zip"), AdminZipHash);
            var sourceManifest = JsonDocument.Parse(Text(source, sourcePrefix + "manifest.json")).RootElement;
            Assert.IsTrue(sourceManifest.GetProperty("originalUnchanged").GetBoolean());
            foreach (var file in sourceManifest.GetProperty("files").EnumerateArray())
            {
                var bytes = Bytes(source, sourcePrefix + file.GetProperty("path").GetString());
                Assert.AreEqual(file.GetProperty("bytes").GetInt64(), bytes.LongLength);
                Assert.AreEqual(file.GetProperty("sha256").GetString(), HashBytes(bytes));
            }
            sourceScenario = JsonDocument.Parse(Text(source, sourcePrefix + "scenario.json")).RootElement.Clone();
            Assert.AreEqual(scenario, sourceScenario.GetProperty("scenario").GetString());
            Assert.AreEqual(Count, sourceScenario.GetProperty("rowCount").GetInt32());
            Assert.AreEqual(sourceScenario.GetProperty("originalPayloadHash").GetString(), Hash(OriginalJson));
            exchanges = JsonDocument.Parse(Text(source, sourcePrefix + "exchanges.json")).RootElement.EnumerateArray().Select(value => value.Clone()).ToArray();
            var manifestBytes = Bytes(admin, "response-manifest.json");
            Assert.AreEqual(historicalMissingItemCount ? "707d242f564a9f6e3db50626ef22503eabdfedebffed9909048480d01ec1742f" :
                "0474f4e6b907dd13c9983a6ca5be7fb4ca3978f79ecd7c6c89f40fd6507bc1ca", HashBytes(manifestBytes));
            var manifest = JsonDocument.Parse(manifestBytes).RootElement;
            Assert.AreEqual(115, manifest.GetProperty("responses").GetArrayLength());
            if (historicalMissingItemCount)
            {
                Assert.AreEqual("9a87550244a00a322d70bc066ac19c0a84f6c494", manifest.GetProperty("corpusCommit").GetString());
                Assert.AreEqual(SourceZipHash, manifest.GetProperty("corpusZipSHA256").GetString());
                Assert.AreEqual(4, manifest.GetProperty("cases").GetArrayLength());
                foreach (var reference in manifest.GetProperty("references").EnumerateArray())
                    Assert.AreEqual(reference.GetProperty("sha256").GetString(), HashBytes(Bytes(admin, reference.GetProperty("archiveFile").GetString()!)));
                serverCase = manifest.GetProperty("cases").EnumerateArray().Single(value => value.GetProperty("scenario").GetString() == scenario).Clone();
            }
            else
            {
                Assert.AreEqual("54a7a3cf7c01e60ea032d9d7444e9b445fa36511", manifest.GetProperty("sourceCommit").GetString());
                Assert.AreEqual("c827afcaeae4fc4270b0b0d5078c4d7ce7084ff9d315bde4492f46982d3a80cf", manifest.GetProperty("sqlSHA256").GetString());
                Assert.AreEqual(HistoricalAdminZipHash, manifest.GetProperty("previousArchivePreserved").GetProperty("sha256").GetString());
                Assert.AreEqual(4, manifest.GetProperty("scenarioReceipts").GetArrayLength());
                foreach (var pin in manifest.GetProperty("scenarioReceipts").EnumerateArray())
                {
                    var name = pin.GetProperty("scenario").GetString();
                    var bytes = Bytes(admin, "receipts/" + name + "/handler-db-replay-receipt.json");
                    Assert.AreEqual(pin.GetProperty("receiptSHA256").GetString(), HashBytes(bytes));
                    Assert.AreEqual(0, pin.GetProperty("exitCode").GetInt32());
                    var receipt = JsonDocument.Parse(bytes).RootElement;
                    Assert.IsTrue(receipt.GetProperty("sourceBodyBytesUnchanged").GetBoolean());
                    Assert.IsTrue(receipt.GetProperty("sourcePinsUnchanged").GetBoolean());
                    if (name == scenario) serverCase = receipt.Clone();
                }
                Assert.AreEqual(JsonValueKind.Object, serverCase.ValueKind);
            }
            responses = manifest.GetProperty("responses").EnumerateArray().Where(value => value.GetProperty("scenario").GetString() == scenario)
                .Select(value => value.Clone()).OrderBy(value => value.GetProperty("index").GetInt32()).ToArray();
            Assert.AreEqual(exchanges.Length, responses.Length);
            Assert.AreEqual(historicalMissingItemCount ? serverCase.GetProperty("requests").GetInt32() : serverCase.GetProperty("requests").GetArrayLength(), responses.Length);
            for (var index = 0; index < responses.Length; index++)
            {
                var entry = responses[index]; var original = exchanges[index];
                Assert.AreEqual(index, entry.GetProperty("index").GetInt32());
                Assert.AreEqual(index, original.GetProperty("index").GetInt32());
                Assert.AreEqual(original.GetProperty("route").GetString(), entry.GetProperty("route").GetString());
                Assert.AreEqual(original.GetProperty("requestSha256").GetString(), RequestHash(entry));
                if (historicalMissingItemCount) Assert.AreEqual(original.GetProperty("requestBytes").GetInt32(), entry.GetProperty("requestBytes").GetInt32());
                else
                {
                    var call = serverCase.GetProperty("requests")[index];
                    Assert.AreEqual(index, call.GetProperty("index").GetInt32());
                    Assert.AreEqual(RequestHash(entry), call.GetProperty("bodySha256").GetString());
                    Assert.AreEqual(original.GetProperty("requestBytes").GetInt32(), call.GetProperty("bytes").GetInt32());
                    Assert.AreEqual(ResponseHash(entry), call.GetProperty("responseSha256").GetString());
                    if (!entry.GetProperty("route").GetString()!.EndsWith("/apply", StringComparison.Ordinal))
                        foreach (var field in new[] { "products", "prices", "batches", "stock125", "syncEvents", "auditSuccess", "completeACKs", "productDigest", "priceDigest", "batchDigest", "eventDigest", "auditDigest", "privateAckDigest", "privateACKPriceMaps", "privateACKProductMaps" })
                            Assert.AreEqual(call.GetProperty("beforeEconomic").GetProperty(field).GetRawText(), call.GetProperty("afterEconomic").GetProperty(field).GetRawText(), "Non-apply changed " + field + " at " + index);
                }
                Assert.AreEqual(original.GetProperty("deliberatelyDropped").GetBoolean(), entry.GetProperty("deliberatelyDropped").GetBoolean());
                Assert.AreEqual(200, entry.GetProperty("httpStatus").GetInt32());
                var bytes = Bytes(admin, ResponseFile(entry));
                Assert.AreEqual(ResponseHash(entry), HashBytes(bytes));
                var response = Read<PosCatalogImportRecoveryMultipartResponse>(new UTF8Encoding(false, true).GetString(bytes));
                Assert.AreEqual(entry.GetProperty("status").GetString(), response.Status);
                if (historicalMissingItemCount && !entry.GetProperty("route").GetString()!.EndsWith("/apply", StringComparison.Ordinal))
                    Assert.IsTrue(entry.GetProperty("economicNoEffectsOnNonApplyVerified").GetBoolean());
                if (response.Receipt == null) continue;
                foreach (var map in response.Receipt.RemoteProductIds)
                { if (products.TryGetValue(map.Barcode, out var prior)) Assert.AreEqual(prior, map.RemoteProductId); products[map.Barcode] = map.RemoteProductId; }
                foreach (var map in response.Receipt.RemotePriceIds)
                {
                    var key = map.Barcode + "|" + PriceKind(map.PriceType);
                    if (prices.TryGetValue(key, out var prior)) Assert.AreEqual(prior, map.RemotePriceId);
                    prices[key] = map.RemotePriceId;
                }
            }
            Assert.AreEqual(Count, products.Count); Assert.AreEqual(2 * Count, prices.Count);
            VerifyDatabaseEvidence();
        }
        internal byte[] Response(string route, byte[] request)
        {
            Assert.IsTrue(nextIndex < responses.Length, "Unexpected additional HTTP request.");
            var expected = responses[nextIndex]; var generated = exchanges[nextIndex];
            Assert.AreEqual(expected.GetProperty("route").GetString(), route, "Route differs at " + nextIndex);
            Assert.AreEqual(RequestHash(expected), HashBytes(request), "Request differs at " + nextIndex);
            CollectionAssert.AreEqual(Bytes(source, sourcePrefix + generated.GetProperty("requestFile").GetString()), request);
            nextIndex++;
            return Bytes(admin, ResponseFile(expected));
        }
        internal void VerifyObserved(string route, byte[] request, byte[] response, bool dropped)
        {
            var expected = responses[nextIndex - 1];
            Assert.AreEqual(expected.GetProperty("route").GetString(), route);
            Assert.AreEqual(RequestHash(expected), HashBytes(request));
            Assert.AreEqual(ResponseHash(expected), HashBytes(response));
            Assert.AreEqual(expected.GetProperty("deliberatelyDropped").GetBoolean(), dropped);
        }
        internal string CanonicalHash(PosCatalogImportRecoveryPlanChildRequest child)
        {
            var hash = Hash(Write(child));
            var pin = JsonDocument.Parse(Text(source, sourcePrefix + "canonical-child-" + hash + ".json")).RootElement;
            Assert.AreEqual(hash, pin.GetProperty("childJsonSha256").GetString());
            var canonical = pin.GetProperty("canonicalPayloadHash").GetString()!;
            var matched = responses.Select(value => Read<PosCatalogImportRecoveryMultipartResponse>(Text(admin, ResponseFile(value))))
                .Where(response => response.Status == "planned").SelectMany(response => response.Parts)
                .Where(part => part.ClientImportId == child.Batch.ClientImportId).ToArray();
            Assert.IsTrue(matched.Length > 0);
            Assert.IsTrue(matched.All(part => part.PayloadHash == canonical && part.DeclaredPayloadHash == child.PayloadHash));
            if (!HistoricalMissingItemCount) Assert.IsTrue(matched.All(part => part.ItemCount == child.Items.Length));
            return canonical;
        }
        private void VerifyDatabaseEvidence()
        {
            var postcheck = serverCase.GetProperty(HistoricalMissingItemCount ? "finalPostcheck" : "final"); var zeroChild = scenario.StartsWith("zerochild", StringComparison.Ordinal);
            foreach (var field in new[] { "products", "stock125", "privateACKProductMaps" }) Assert.AreEqual(Count, postcheck.GetProperty(field).GetInt32());
            foreach (var field in new[] { "prices", "privateACKPriceMaps" }) Assert.AreEqual(2 * Count, postcheck.GetProperty(field).GetInt32());
            foreach (var field in new[] { "batches", "auditSuccess", "completeACKs" }) Assert.AreEqual(2, postcheck.GetProperty(field).GetInt32());
            Assert.AreEqual("on", postcheck.GetProperty("readOnly").GetString());
            var plan = postcheck.GetProperty("plans").EnumerateArray().Single(value => value.GetProperty("planId").GetString() == sourceScenario.GetProperty("successorPlanId").GetString());
            Assert.IsTrue(plan.GetProperty("authoritativelyComplete").GetBoolean());
            Assert.AreEqual(Count, plan.GetProperty("coverageCount").GetInt32());
            Assert.AreEqual(zeroChild ? 0 : 1, plan.GetProperty("children").GetInt32());
            Assert.AreEqual(zeroChild ? 0 : 1, plan.GetProperty("acceptedChildren").GetInt32());
            var values = serverCase.GetProperty("values");
            Assert.IsTrue(values.GetProperty("allPurchase900").GetBoolean());
            Assert.AreEqual(zeroChild ? 1000 : 999, values.GetProperty("retail1200").GetInt32());
            Assert.AreEqual(1, values.GetProperty("retail1300").GetInt32());
            Assert.AreEqual(zeroChild ? 0 : 1, values.GetProperty("retail1500").GetInt32());
            foreach (var group in responses.Where(value => value.GetProperty("deliberatelyDropped").GetBoolean()))
            {
                var retries = responses.Where(value => value.GetProperty("index").GetInt32() > group.GetProperty("index").GetInt32() &&
                    value.GetProperty("route").GetString() == group.GetProperty("route").GetString() &&
                    RequestHash(value) == RequestHash(group)).ToArray();
                Assert.IsTrue(retries.Length > 0, "Dropped operation must be retried exactly.");
                if (!group.GetProperty("route").GetString()!.EndsWith("/plan", StringComparison.Ordinal))
                    Assert.IsTrue(retries.All(value => ResponseHash(value) == ResponseHash(group)), "Durable retirement retry response changed.");
                else
                {
                    // A plan's economic identity and children are immutable;
                    // accepted children can advance its current parent status.
                    var before = Read<PosCatalogImportRecoveryMultipartResponse>(Text(admin, ResponseFile(group)));
                    foreach (var retry in retries)
                    {
                        var after = Read<PosCatalogImportRecoveryMultipartResponse>(Text(admin, ResponseFile(retry)));
                        Assert.AreEqual(before.SchemaVersion, after.SchemaVersion); Assert.AreEqual(before.Status, after.Status);
                        Assert.AreEqual(before.ShopId, after.ShopId); Assert.AreEqual(before.ShopDeviceId, after.ShopDeviceId);
                        Assert.AreEqual(before.PlanId, after.PlanId); Assert.AreEqual(before.VerifiedOriginalId, after.VerifiedOriginalId);
                        Assert.AreEqual(before.PlanCanonicalHash, after.PlanCanonicalHash); Assert.AreEqual(before.PartCount, after.PartCount);
                        Assert.AreEqual(before.ItemCount, after.ItemCount); Assert.AreEqual(Write(before.Parts), Write(after.Parts));
                        Assert.IsTrue(before.ParentStatus == after.ParentStatus || before.ParentStatus == "partial" && after.ParentStatus == "complete");
                    }
                }
            }
        }
        internal void VerifyFinal(Fixture fixture)
        {
            Assert.AreEqual(responses.Length, nextIndex, "Every recorded exchange must pass through the real C# client.");
            using var connection = fixture.Factory.Open();
            Assert.AreEqual(Count, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
            foreach (var row in connection.Query("SELECT barcode,remote_product_id FROM products")) Assert.AreEqual(products[(string)row.barcode], (string)row.remote_product_id);
            var mapped = connection.Query("SELECT barcode,type,remote_price_id FROM product_price_history WHERE remote_price_id IS NOT NULL").ToArray();
            Assert.AreEqual(2 * Count, mapped.Length);
            foreach (var row in mapped) Assert.AreEqual(prices[(string)row.barcode + "|" + PriceKind((string)row.type)], (string)row.remote_price_id);
            Assert.AreEqual(2 * Count, mapped.Select(row => (string)row.remote_price_id).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM catalog_import_prepared_plan"));
            Assert.AreEqual(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE resolved_at IS NULL"));
        }
        internal void VerifyRejectedBeforeFirstChildRetirement()
        {
            Assert.IsTrue(HistoricalMissingItemCount);
            var first = Array.FindIndex(exchanges, entry =>
            {
                if (!entry.GetProperty("route").GetString()!.EndsWith("/retire", StringComparison.Ordinal)) return false;
                var request = Read<PosCatalogImportRecoveryHandleRequest>(Text(source, sourcePrefix + entry.GetProperty("requestFile").GetString()));
                return request.PlanId != null;
            });
            Assert.IsTrue(first > 0); Assert.AreEqual(first, nextIndex, "Historical missing count must fail before any child retirement HTTP request.");
            var last = Read<PosCatalogImportRecoveryMultipartResponse>(Text(admin, ResponseFile(responses[nextIndex - 1])));
            Assert.AreEqual("planned", last.Status); Assert.IsTrue(last.Parts.All(part => part.ItemCount == 0));
        }
        private string RequestHash(JsonElement value) => value.GetProperty(HistoricalMissingItemCount ? "requestSHA256" : "bodySha256").GetString()!;
        private string ResponseHash(JsonElement value) => value.GetProperty(HistoricalMissingItemCount ? "responseSHA256" : "responseSha256").GetString()!;
        private string ResponseFile(JsonElement value) => value.GetProperty(HistoricalMissingItemCount ? "responseFile" : "responsePath").GetString()!;
        private static string PriceKind(string value)
        {
            // The wire uses lowercase enum labels and SQLite stores uppercase.
            // Normalize only that enum, never barcode or remote identity text.
            var kind = value.ToLowerInvariant(); Assert.IsTrue(kind is "purchase" or "retail"); return kind;
        }
        private static ZipArchive Open(string path, string hash)
        {
            var bytes = File.ReadAllBytes(path); Assert.AreEqual(hash, HashBytes(bytes), path);
            return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        }
        private static byte[] Bytes(ZipArchive archive, string path)
        {
            var entry = archive.GetEntry(path); Assert.IsNotNull(entry, path);
            using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
        }
        private static string Text(ZipArchive archive, string path) => new UTF8Encoding(false, true).GetString(Bytes(archive, path));
        private static string HashBytes(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        public void Dispose() { source.Dispose(); admin.Dispose(); }
    }

    private sealed class Capture : IDisposable
    {
        private readonly Fixture fixture;
        private readonly bool actualReplay;
        private readonly string? directory;
        private readonly List<object> exchanges = new();
        private readonly List<(string Route, string Body)> bodies = new();
        private readonly List<object> states = new();
        private readonly Dictionary<string, JsonElement> canonical = new();
        private readonly string? adminRoot = Environment.GetEnvironmentVariable("WIN7POS_INTEROP_ADMIN_ROOT");
        private readonly string? dependencyRoot = Environment.GetEnvironmentVariable("WIN7POS_INTEROP_ADMIN_DEPENDENCIES");
        internal bool HasAdminParser => !string.IsNullOrEmpty(adminRoot) && !string.IsNullOrEmpty(dependencyRoot);
        internal string Phase { get; set; } = "initial-local-apply";
        internal Capture(string scenario, Fixture fixture, bool actualReplay)
        {
            this.fixture = fixture; this.actualReplay = actualReplay;
            var output = Environment.GetEnvironmentVariable("WIN7POS_INTEROP_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                directory = Path.Combine(output, (actualReplay ? "admin-db-supersession-" : "supersession-") + scenario);
                Assert.IsFalse(Directory.Exists(directory), "Evidence capture must use a fresh directory.");
                Directory.CreateDirectory(directory);
            }
        }
        internal void Observe(string route, byte[] request, byte[] response, bool dropped)
        {
            Assert.IsTrue(request.Length <= 512 * 1024);
            var index = exchanges.Count;
            var stem = index.ToString("D3") + "." + route.Split('/').Last();
            var responseFile = stem + (actualReplay ? ".actual-response.json" : ".synthetic-response.json");
            Bytes(stem + ".request.json", request); Bytes(responseFile, response);
            var body = new UTF8Encoding(false, true).GetString(request); bodies.Add((route, body));
            exchanges.Add(new { index, route, phase = Phase, requestFile = stem + ".request.json", requestSha256 = Hash(body), requestBytes = request.Length,
                responseFile, responseSha256 = Hash(new UTF8Encoding(false, true).GetString(response)),
                deliberatelyDropped = dropped, responseScope = actualReplay ? "actual Admin handler/isolated PostgreSQL bytes; synthetic outer authentication" : "synthetic generation only; not Admin or database acceptance" });
            if (route.EndsWith("/plan", StringComparison.Ordinal))
            {
                Assert.AreEqual(1, fixture.Number("SELECT COUNT(*) FROM catalog_import_prepared_plan WHERE dispatch_started_at IS NOT NULL AND plan_document_json IS NOT NULL"));
                State("before-delivery-of-plan-response-" + index, fixture.Number("SELECT original_id FROM catalog_import_prepared_plan"));
            }
        }
        internal string CanonicalHash(PosCatalogImportRecoveryPlanChildRequest child)
        {
            var childJson = CatalogImportSupersessionInteropEvidenceTests.Write(child); var childHash = Hash(childJson);
            if (canonical.TryGetValue(childHash, out var cached)) return cached.GetProperty("canonicalPayloadHash").GetString()!;
            var source = FindSourceRoot();
            var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(source, "scripts", "qa", "catalog-import-canonical-hash.mjs"));
            start.ArgumentList.Add(adminRoot!); start.ArgumentList.Add(dependencyRoot!);
            using var process = Process.Start(start)!;
            var session = Trusted();
            process.StandardInput.Write(JsonSerializer.Serialize(new { childJson, trust = new { deviceToken = session.DeviceToken,
                sessionToken = session.SessionToken, posSessionId = session.PosSessionId, shopDeviceId = session.ShopDeviceId, shopCode = session.ShopCode } }));
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, error);
            var parsed = JsonDocument.Parse(output).RootElement.Clone();
            Assert.AreEqual(childHash, parsed.GetProperty("childJsonSha256").GetString());
            canonical.Add(childHash, parsed); Write("canonical-child-" + childHash + ".json", output);
            return parsed.GetProperty("canonicalPayloadHash").GetString()!;
        }
        internal void State(string name, long rootId)
        {
            using var connection = fixture.Factory.Open();
            states.Add(new { name, phase = Phase, rootId, exchangeCount = exchanges.Count,
                originalPayloadHash = Hash(fixture.Saved(rootId)), historyCount = fixture.Number("SELECT COUNT(*) FROM product_price_history"),
                stockUnchangedRows = fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"),
                outbox = connection.Query("SELECT id,client_import_id,idempotency_key,payload_hash,status,attempt_count FROM catalog_import_outbox ORDER BY id").Select(row => (IDictionary<string, object>)row).ToArray(),
                prepared = connection.Query("SELECT * FROM catalog_import_prepared_plan").Select(row => (IDictionary<string, object>)row).ToArray(),
                supersession = connection.Query("SELECT * FROM catalog_import_recovery_supersession").Select(row => (IDictionary<string, object>)row).ToArray() });
        }
        internal void AssertIdenticalRetirementRetry(string planId, int index)
        {
            var repeated = bodies.Where(value => value.Route.EndsWith("/retire", StringComparison.Ordinal)).Select(value => value.Body)
                .Where(body => { var request = Read<PosCatalogImportRecoveryHandleRequest>(body); return request.PlanId == planId && request.PartIndex == index; }).ToArray();
            Assert.AreEqual(2, repeated.Length); Assert.AreEqual(repeated[0], repeated[1]);
        }
        internal void Complete(object scenario)
        {
            if (directory == null) return;
            Write("scenario.json", JsonSerializer.Serialize(scenario));
            Write("exchanges.json", JsonSerializer.Serialize(exchanges));
            Write("local-states.json", JsonSerializer.Serialize(states));
            var files = Directory.GetFiles(directory).OrderBy(path => path, StringComparer.Ordinal).Select(path => new { path = Path.GetFileName(path),
                bytes = new FileInfo(path).Length, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
            Write("manifest.json", JsonSerializer.Serialize(new { schemaVersion = "win7pos-supersession-wire-evidence-v1", scenario,
                requestCount = exchanges.Count, originalUnchanged = true, files,
                canonicalHashSource = actualReplay ? "pinned original Admin parser proof and actual Admin plan responses" : HasAdminParser ? "actual Admin forensic parser; source hashes in canonical-child files" : "synthetic peer",
                scope = actualReplay ? "actual Admin handlers and isolated PostgreSQL responses reenter real C# services/SQLite; synthetic outer credentials; no live/shared TEST" : "real C# builder/SQLite/journal/serializer; synthetic responses and outer credentials; no Admin handler or SQL acceptance",
                externalAcceptedEvents = "real PosAdminWebClient HTTP apply calls with typed request; local ACK deliberately absent",
                initialUncertainty = "explicit synthetic legacy seed; oversize original never dispatched by current sender" }));
        }
        internal void Write(string name, string value) => Bytes(name, new UTF8Encoding(false).GetBytes(value));
        private void Bytes(string name, byte[] bytes) { if (directory != null) File.WriteAllBytes(Path.Combine(directory, name), bytes); }
        internal static string FindSourceRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Win7POS.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new AssertFailedException("Cannot locate Win7POS source root.");
        }
        public void Dispose() { }
    }
}
