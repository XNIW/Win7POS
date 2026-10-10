using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Fixture = Win7POS.Core.Tests.Data.CatalogImportSupersessionTests.Fixture;
using MultipartPeer = Win7POS.Core.Tests.Data.CatalogImportSupersessionTests.SyntheticPeer;

namespace Win7POS.Core.Tests.Data;

/// <summary>
/// Client/SQLite qualification only. Both loopback peers are explicitly synthetic:
/// lookup after retirement returns conflict/identity_retired without a fence, and
/// only replaying the authenticated retirement returns the already saved fence.
/// These tests do not certify the Admin handler or its database implementation.
/// </summary>
[TestClass]
public sealed class CatalogImportOriginalRetirementRetryTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [TestMethod]
    public async Task SmallOriginal_LostRetirement_RestartKeepsExplicitRetryAndCommitsOnce()
    {
        using var fixture = new Fixture();
        var root = await SeedUncertainAsync(fixture, 3);
        using var peer = new SyntheticOriginalPeer();
        var service = fixture.Service();
        var draft = await service.PrepareAsync(root.Id, peer.Options, Trusted(), null!, Token);
        await SaveEditAsync(service, draft);
        peer.DropNextRetirement = true;
        await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.RetireAsync(draft, peer.Options, Trusted(), null!, () => true, Token));
        Assert.AreEqual(1, peer.FenceCount);
        AssertUnapplied(fixture, root, 3);

        service = fixture.Restart();
        var local = await AssertRestartCanRetryAsync(fixture, service, root.Id, peer.Options);
        await service.RetireAsync(local, peer.Options, Trusted(), null!, () => true, Token);
        Assert.IsTrue(local.CanCommit);
        Assert.AreEqual("retired", local.ReceiptStatus);
        Assert.AreEqual(1, peer.FenceCount, "Idempotent retirement must keep one fence.");
        Assert.AreEqual(2, peer.RetirementBodies.Count);
        Assert.AreEqual(peer.RetirementBodies[0], peer.RetirementBodies[1], "Replay retains the exact identity, hash and original.");
        await AssertUniqueCommitAsync(fixture, service, local, root, 3);
    }

    [TestMethod]
    public async Task MultipartOriginal_LostRetirement_RestartKeepsExplicitRetryAndCommitsOnce()
    {
        const int count = 1001;
        using var fixture = new Fixture();
        var root = await SeedUncertainAsync(fixture, count);
        using var peer = new MultipartPeer(count);
        var retireBodies = new List<string>();
        peer.ResponseBytesOverride = (route, request, response) =>
        {
            if (route.EndsWith("/retire", StringComparison.Ordinal)) retireBodies.Add(Encoding.UTF8.GetString(request));
            if (!route.EndsWith("/receipt", StringComparison.Ordinal)) return response;
            var value = Read<PosCatalogImportRecoveryMultipartResponse>(Encoding.UTF8.GetString(response));
            if (value.Status != "retired") return response;
            value.Status = "conflict"; value.Reason = "identity_retired";
            value.OldIdentityBlocked = false; value.RetiredAt = null!;
            value.SnapshotOnly = false; value.ReplacementAllowed = false;
            return Encoding.UTF8.GetBytes(Write(value));
        };
        var service = fixture.Service();
        var draft = await service.PrepareAsync(root.Id, peer.Options, Trusted(), null!, Token);
        await SaveEditAsync(service, draft);
        peer.DropNextRetirement = (null!, -1); // Root selector has no planId/partIndex.
        await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.RetireAsync(draft, peer.Options, Trusted(), null!, () => true, Token));
        AssertUnapplied(fixture, root, count);

        service = fixture.Restart();
        var local = await AssertRestartCanRetryAsync(fixture, service, root.Id, peer.Options);
        await service.RetireAsync(local, peer.Options, Trusted(), null!, () => true, Token);
        Assert.IsTrue(local.CanCommit);
        Assert.AreEqual("retired", local.ReceiptStatus);
        Assert.AreEqual(2, retireBodies.Count);
        Assert.AreEqual(retireBodies[0], retireBodies[1]);
        // Use the returned draft just as the dialog does: no hidden PrepareAsync
        // or manual transport context repairs between explicit retirement/commit.
        await AssertUniqueCommitAsync(fixture, service, local, root, count);
        Assert.AreEqual(1, peer.Plans.Count);
        Assert.AreEqual(2, peer.Plans.Single().Value.Parts.Length);
    }

    [TestMethod]
    public async Task CorrectionTarget_LostRetirement_RestartKeepsExplicitRetryAndCommitsOnce()
    {
        using var fixture = new Fixture();
        var root = await SeedUncertainAsync(fixture, 3);
        using var peer = new SyntheticOriginalPeer { AcceptOrdinaryRoot = true };
        var service = fixture.Service();
        var initial = await service.PrepareAsync(root.Id, peer.Options, Trusted(), null!, Token);
        initial.Rows[0].RetailPrice = "1300";
        var first = await service.CommitAsync(initial, initial.Rows, () => true, null!, Token);
        Assert.AreEqual(0, first.Errors, string.Join(";", first.ErrorMessages));
        var childJson = fixture.Saved(first.CatalogImportOutboxId);
        Assert.AreEqual("catalog_import_correction", fixture.Text("SELECT operation_type FROM catalog_import_outbox WHERE id=@id", new { id = first.CatalogImportOutboxId }));
        var sent = await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options, Trusted(), Token);
        Assert.AreEqual(1, sent.Blocked, sent.DiagnosticCode);
        Assert.AreEqual("revision_conflict", sent.DiagnosticCode);
        var draft = await service.PrepareAsync(root.Id, peer.Options, Trusted(), null!, Token);
        Assert.IsTrue(draft.CanRetire);
        Assert.AreEqual("not_found", draft.TargetReceiptStatus);
        Assert.AreEqual(first.CatalogImportOutboxId, draft.TargetOriginal.Id);
        await service.SaveDraftAsync(draft, draft.Rows, Token);
        peer.DropNextRetirement = true;
        await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.RetireAsync(draft, peer.Options, Trusted(), null!, () => true, Token));
        Assert.AreEqual("not_found", ReceiptStatus(fixture, first.CatalogImportOutboxId));

        service = fixture.Restart();
        var local = await service.PrepareLocalAsync(root.Id, Trusted(), null!, Token);
        Assert.IsFalse(local.CanCommit);
        Assert.IsTrue(local.CanRetire, "The persisted child not_found must keep the explicit retirement action reachable after restart.");
        Assert.AreEqual(first.CatalogImportOutboxId, local.TargetOriginal.Id);
        Assert.AreEqual("not_found", local.TargetReceiptStatus);
        var conflict = await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.PrepareAsync(root.Id, peer.Options, Trusted(), null!, Token));
        Assert.AreEqual("receipt_conflict", conflict.Code);
        Assert.IsTrue(local.CanRetire); Assert.IsFalse(local.CanCommit);
        await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.CommitAsync(local, local.Rows, () => true, null!, Token));
        await service.RetireAsync(local, peer.Options, Trusted(), null!, () => true, Token);
        Assert.IsTrue(local.CanCommit);
        Assert.AreEqual("accepted", local.ReceiptStatus); Assert.AreEqual("retired", local.TargetReceiptStatus);
        var retry = await service.CommitAsync(local, local.Rows, () => true, null!, Token);
        Assert.AreEqual(0, retry.Errors, string.Join(";", retry.ErrorMessages));
        Assert.AreNotEqual(first.CatalogImportOutboxId, retry.CatalogImportOutboxId);
        Assert.AreEqual(3, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(7, fixture.Number("SELECT COUNT(*) FROM product_price_history"), "Retiring/replacing a failed correction must not apply its local price history twice.");
        Assert.AreEqual(3, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
        Assert.AreEqual(root.Json, fixture.Saved(root.Id)); Assert.AreEqual(childJson, fixture.Saved(first.CatalogImportOutboxId));
        Assert.AreEqual(1, peer.FenceCount);
        Assert.AreEqual(peer.RetirementBodies[0], peer.RetirementBodies[1]);
        var repeated = await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.CommitAsync(local, local.Rows, () => true, null!, Token));
        Assert.AreEqual("receipt_required", repeated.Code);
    }

    [TestMethod]
    [DataRow(false, "identity")]
    [DataRow(false, "hash")]
    [DataRow(false, "shop")]
    [DataRow(true, "identity")]
    [DataRow(true, "hash")]
    [DataRow(true, "shop")]
    public async Task RetirementRetry_RejectsMismatchedAuthorityWithoutReplacingSnapshot(bool multipart, string mismatch)
    {
        var count = multipart ? 1001 : 3;
        using var fixture = new Fixture();
        var root = await SeedUncertainAsync(fixture, count);
        using var ordinary = multipart ? null : new SyntheticOriginalPeer();
        using var large = multipart ? new MultipartPeer(count) : null;
        var options = multipart ? large!.Options : ordinary!.Options;
        var service = fixture.Service();
        var draft = await service.PrepareAsync(root.Id, options, Trusted(), null!, Token);
        var snapshot = fixture.Text("SELECT receipt_json FROM catalog_import_recovery WHERE original_id=@id", new { id = root.Id });
        if (ordinary != null) ordinary.MutateRetirement = value => ChangeIdentity(value, mismatch);
        else large!.ResponseBytesOverride = (route, request, response) =>
        {
            if (!route.EndsWith("/retire", StringComparison.Ordinal)) return response;
            var value = Read<PosCatalogImportRecoveryMultipartResponse>(Encoding.UTF8.GetString(response));
            if (mismatch == "identity") value.ClientImportId += "-wrong";
            else if (mismatch == "hash") value.PayloadHash = new string('f', 64);
            else value.ShopId = "10000000-0000-4000-8000-000000000095";
            return Encoding.UTF8.GetBytes(Write(value));
        };
        await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.RetireAsync(draft, options, Trusted(), null!, () => true, Token));
        Assert.IsFalse(draft.CanCommit); Assert.IsTrue(draft.CanRetire);
        Assert.AreEqual(snapshot, fixture.Text("SELECT receipt_json FROM catalog_import_recovery WHERE original_id=@id", new { id = root.Id }));
        AssertUnapplied(fixture, root, count);
    }

    private static async Task<CatalogImportRecoveryDraft> AssertRestartCanRetryAsync(Fixture fixture, CatalogImportRecoveryService service, long root, PosAdminWebOptions options)
    {
        var local = await service.PrepareLocalAsync(root, Trusted(), null!, Token);
        Assert.IsTrue(local.HasSavedDraft);
        Assert.AreEqual("1300", local.Rows[0].RetailPrice);
        Assert.IsTrue(local.CanRetire);
        Assert.IsFalse(local.CanCommit);
        var conflict = await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.PrepareAsync(root, options, Trusted(), null!, Token));
        Assert.AreEqual("receipt_conflict", conflict.Code);
        Assert.AreEqual("not_found", ReceiptStatus(fixture, root), "A conflict lookup carries no retirement fence and must not replace the last validated snapshot.");
        Assert.IsTrue(local.CanRetire); Assert.IsFalse(local.CanCommit);
        var denied = await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.CommitAsync(local, local.Rows, () => true, null!, Token));
        Assert.AreEqual("receipt_required", denied.Code);
        return local;
    }

    private static async Task AssertUniqueCommitAsync(Fixture fixture, CatalogImportRecoveryService service, CatalogImportRecoveryDraft draft, Original root, int count)
    {
        var result = await service.CommitAsync(draft, draft.Rows, () => true, null!, Token);
        Assert.AreEqual(0, result.Errors, string.Join(";", result.ErrorMessages));
        Assert.IsTrue(result.CatalogImportOutboxId > root.Id);
        Assert.IsTrue(File.Exists(result.BackupPath));
        Assert.AreEqual(count * 2 + 1, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
        Assert.AreEqual(1, fixture.Number("SELECT COUNT(*) FROM products WHERE unitPrice=1300"));
        Assert.AreEqual(root.Json, fixture.Saved(root.Id));
        Assert.AreEqual(root.Hash, fixture.Text("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id", new { id = root.Id }));
        Assert.AreEqual("failed_blocked", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = root.Id }), "A local replacement is not a remote ACK.");
        var outboxes = fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox");
        var repeated = await Assert.ThrowsExactlyAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, Token));
        Assert.AreEqual("receipt_required", repeated.Code);
        Assert.AreEqual(outboxes, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(count * 2 + 1, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
    }

    private static async Task SaveEditAsync(CatalogImportRecoveryService service, CatalogImportRecoveryDraft draft)
    {
        Assert.IsTrue(draft.CanRetire); Assert.IsFalse(draft.CanCommit);
        draft.Rows[0].RetailPrice = "1300";
        await service.SaveDraftAsync(draft, draft.Rows, Token);
    }
    private static async Task<Original> SeedUncertainAsync(Fixture fixture, int count)
    {
        var applier = new SupplierExcelImportApplier(fixture.Factory);
        var preview = await applier.BuildPreviewAsync(Enumerable.Range(0, count).Select(CatalogImportInteropEvidenceTests.Row).ToArray());
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "retirement-retry.xlsx", "synthetic-client-test");
        var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
        Assert.AreEqual(0, result.Errors, string.Join(";", result.ErrorMessages));
        using var conn = fixture.Factory.Open();
        // Historical persisted originals may predate request splitting. The real
        // builder/applier create the bytes/economics; only delivery uncertainty
        // is explicitly seeded for this client recovery qualification.
        conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1,last_error_code='synthetic_lost_response' WHERE id=@id", new { id = result.CatalogImportOutboxId });
        conn.Execute("UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id", new { id = result.CatalogImportOutboxId });
        return new Original(result.CatalogImportOutboxId, entry.PayloadJson, entry.PayloadHash);
    }
    private static void AssertUnapplied(Fixture fixture, Original root, int count)
    {
        Assert.AreEqual(root.Json, fixture.Saved(root.Id));
        Assert.AreEqual("not_found", ReceiptStatus(fixture, root.Id));
        Assert.AreEqual(1, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(count * 2, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
    }
    private static string ReceiptStatus(Fixture fixture, long id) => fixture.Text("SELECT receipt_status FROM catalog_import_recovery WHERE original_id=@id", new { id });
    private static void ChangeIdentity(PosCatalogImportReceiptResponse value, string mismatch)
    {
        if (mismatch == "identity") value.ClientImportId += "-wrong";
        else if (mismatch == "hash") value.PayloadHash = new string('f', 64);
        else value.ShopId = "10000000-0000-4000-8000-000000000095";
    }
    private sealed record Original(long Id, string Json, string Hash);
    private static PosTrustedDeviceSession Trusted() => CatalogImportSupersessionTests.Trusted();
    private static string Write<T>(T value) => CatalogImportRecoveryService.Serialize(value);
    private static T Read<T>(string value) => CatalogImportRecoveryService.Deserialize<T>(value);

    // Synthetic endpoint; models client-observable semantics, never Admin proof.
    private sealed class SyntheticOriginalPeer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task serve;
        private readonly HashSet<string> fences = new(StringComparer.Ordinal);
        private volatile bool stopping;
        private Exception? failure;
        internal PosAdminWebOptions Options { get; }
        internal bool AcceptOrdinaryRoot { get; init; }
        internal bool DropNextRetirement { get; set; }
        internal int FenceCount => fences.Count;
        internal List<string> RetirementBodies { get; } = new();
        internal Action<PosCatalogImportReceiptResponse>? MutateRetirement { get; set; }
        internal SyntheticOriginalPeer()
        {
            listener.Start();
            Options = new PosAdminWebOptions(new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port));
            serve = Task.Run(async () =>
            {
                try
                {
                    while (!stopping)
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                        using var stream = client.GetStream();
                        var body = await CatalogImportRecoveryTests.ReadBodyAsync(stream);
                        Assert.IsTrue(Encoding.UTF8.GetByteCount(body) <= 512 * 1024);
                        var reply = Respond(body);
                        if (reply == null) continue;
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(bytes);
                    }
                }
                catch (Exception ex) when (stopping && ex is SocketException or ObjectDisposedException) { }
                catch (Exception ex) { failure = ex; }
            });
        }
        private string? Respond(string body)
        {
            using var document = JsonDocument.Parse(body);
            var schema = document.RootElement.GetProperty("schemaVersion").GetString();
            if (schema == PosCatalogImportCorrectionContract.SchemaVersion)
            {
                var correction = Read<PosCatalogImportCorrectionRequest>(body);
                return Write(new PosCatalogImportCorrectionResponse { Ok = true, Code = "success", Status = "conflict", Reason = "revision_conflict",
                    SchemaVersion = correction.SchemaVersion, ShopId = Trusted().ShopId, ShopDeviceId = Trusted().ShopDeviceId,
                    ClientImportId = correction.Correction.ClientImportId, IdempotencyKey = correction.Correction.IdempotencyKey,
                    PayloadHash = correction.Correction.PayloadHash, CanonicalPayloadHash = Canonical(correction.Correction.PayloadHash) });
            }
            Assert.IsTrue(schema == PosCatalogImportReceiptContract.SchemaVersion || schema == PosCatalogImportReceiptContract.RetirementSchemaVersion);
            var isCorrection = document.RootElement.GetProperty("originalRequest").GetProperty("schemaVersion").GetString() == PosCatalogImportCorrectionContract.SchemaVersion;
            var envelope = Read<PosCatalogImportReceiptRequest>(body);
            Assert.AreEqual(Trusted().ShopDeviceId, envelope.ShopDeviceId);
            Assert.AreEqual(Trusted().DeviceToken, envelope.DeviceToken); Assert.AreEqual(Trusted().SessionToken, envelope.SessionToken);
            var retirement = schema == PosCatalogImportReceiptContract.RetirementSchemaVersion;
            var key = envelope.ShopDeviceId + "|" + envelope.ClientImportId + "|" + envelope.IdempotencyKey + "|" + envelope.PayloadHash;
            if (retirement) { fences.Add(key); RetirementBodies.Add(body); }
            var status = !isCorrection && AcceptOrdinaryRoot ? "accepted" : retirement ? "retired" : fences.Contains(key) ? "conflict" : "not_found";
            var response = new PosCatalogImportReceiptResponse { Ok = true, Code = "success", Status = status, SchemaVersion = schema!,
                OriginalSchemaVersion = isCorrection ? PosCatalogImportCorrectionContract.SchemaVersion : PosOnlineContract.CatalogImportSchemaVersion,
                ShopId = Trusted().ShopId, ShopDeviceId = envelope.ShopDeviceId, ClientImportId = envelope.ClientImportId,
                IdempotencyKey = envelope.IdempotencyKey, PayloadHash = envelope.PayloadHash, CanonicalPayloadHash = Canonical(envelope.PayloadHash),
                Reason = status == "conflict" ? "identity_retired" : null!, SnapshotOnly = status == "not_found",
                OldIdentityBlocked = status == "retired", RetiredAt = status == "retired" ? "2026-10-09T00:00:01.000000Z" : null! };
            if (status == "accepted")
            {
                var accepted = new PosCatalogImportRecoveryMultipartResponse();
                MultipartPeer.FillAck(accepted, CatalogImportRecoveryProofTransport.ProjectionForTransport(envelope.OriginalRequest, envelope.PayloadHash));
                response.Receipt = accepted.Receipt; response.CurrentProductSnapshots = accepted.CurrentProductSnapshots;
            }
            if (retirement)
            {
                MutateRetirement?.Invoke(response);
                if (DropNextRetirement) { DropNextRetirement = false; return null; }
            }
            return Write(response);
        }
        private static string Canonical(string hash) => "sha256:" + CatalogImportOutboxPayloadBuilder.Sha256Hex("synthetic-canonical|" + hash);
        public void Dispose()
        {
            stopping = true; listener.Stop(); serve.GetAwaiter().GetResult();
            if (failure != null) throw new AssertFailedException("Synthetic original peer failed: " + failure);
        }
    }
}
