using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

// Test-state setup for known historical ordinary plans only. These tuples come
// from immutable source ZIPs and their verified inner manifests/upload chunks.
// Current planning must acquire new replies; this helper never fabricates one.
internal static class CatalogImportArchivedPreparedJournalFixture
{
    private const string MultipartSource = "2d348b460ca98ac957e1afdbd4c148d2cac54128fb72aea5da24446b950c5f7e";
    private const string SupersessionSource = "fc77839f807f3870f34db4f99447c3cda2c82b7827ae46560bfee06caae97d44";
    private static readonly HashSet<string> Pins = new(StringComparer.Ordinal)
    {
        MultipartSource + "|d4708f17c092948716df60a3fa065b2d249be9c828389e1c161af2fff1c26a3b|11e9af9d7d7486177ec4d9df893e798c08ee1c2680456ead3a999bb75b4f22e7",
        SupersessionSource + "|6553e996c9ae10920e6db5d9e278aaf2fc5364aa578d77767ab0cacb516d4ded|fcf5ca5a7aa0cad6f44c6ba56081c9518e6b81d6c0aad0da9807bde3082931d9",
        SupersessionSource + "|61205c1015e8cb4e119738d6cc27c7597c1dd30aa2557c798d99e42955e7f477|6bc85d667df25de33e08c0309ebfb9cf944ca37bb8f15746c114c39a158a103c",
        SupersessionSource + "|fc63ce2a3d295d999ac5b88349253fc27386e22a5e44ebb5a0da2ade1d94940d|f78c716ea4b0adf42aa09097be540dacb21f396ed7ae0d27e53c8f6148609f03",
        SupersessionSource + "|31e692056f14090a659e78a45dba8bef42cbed9e3fef072bb66d009e0f7a35b5|183b853e277ef9bcd7893f66d714d381835ebff5841e37a753ea5508fc576f39"
    };

    internal static async Task<CatalogImportOutboxEntry[]> StageAsync(SqliteConnectionFactory factory,
        CatalogImportRecoveryService service, CatalogImportRecoveryDraft draft, PosTrustedDeviceSession session,
        string sourceArchiveSha256, string originalJson, string documentJson)
    {
        var originalHash = Hash(originalJson); var documentHash = Hash(documentJson);
        Assert.IsTrue(Pins.Contains(sourceArchiveSha256 + "|" + originalHash + "|" + documentHash), "Only the exact pinned historical archive may seed prepared work.");
        Assert.AreEqual(originalHash, draft.Original.PayloadHash);
        Assert.AreEqual("retired", draft.ReceiptStatus); Assert.IsTrue(draft.CanCommit);
        var document = CatalogImportRecoveryService.Deserialize<PosCatalogImportRecoveryPlanDocument>(documentJson);
        Assert.AreEqual("replacement", document.Mode);
        Assert.AreEqual(CatalogImportRecoveryProofTransport.CreateUploadId(session.ShopId, session.ShopDeviceId,
            "sha256:" + originalHash, "original"), document.VerifiedOriginalId);
        var entries = document.Parts.OrderBy(part => part.Index).Select(part =>
        {
            Assert.IsNull(part.Request.Correction);
            var request = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(CatalogImportRecoveryService.Serialize(part.Request));
            request.PayloadHash = null; request.Batch.AttemptCount = 0;
            var json = CatalogImportRecoveryService.Serialize(request);
            Assert.AreEqual(part.Request.PayloadHash, Hash(json), "Restore the saved operation byte for byte, removing only transient transport metadata.");
            return new CatalogImportOutboxEntry { ClientImportId = request.Batch.ClientImportId, IdempotencyKey = request.Batch.IdempotencyKey,
                CreatedAt = DateTimeOffset.Parse(request.Batch.CreatedAt, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
                SchemaVersion = request.SchemaVersion, Source = request.Source, PayloadJson = json, PayloadHash = Hash(json) };
        }).ToArray();
        var plan = CatalogImportPlanBuilder.Plan(entries);
        Assert.AreEqual(draft.Rows.Count, plan.TotalRows);
        Assert.AreEqual(document.PlanId, CatalogImportRecoveryProofTransport.CreateUploadId(session.ShopId, session.ShopDeviceId,
            "sha256:" + Hash(plan.PlanId), "plan"));
        var before = EconomicFingerprint(factory);
        long outboxBefore;
        using (var connection = factory.Open()) outboxBefore = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox");
        var snapshot = typeof(CatalogImportRecoveryService).GetMethod("SnapshotRows", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(snapshot);
        var rows = (SupplierImportEditableRow[])snapshot.Invoke(null, new object[] { draft.Rows })!;
        async Task InvokeWriter(string name, params object[] arguments)
        {
            var writer = typeof(CatalogImportRecoveryService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(writer, name); await (Task)writer.Invoke(service, arguments)!;
        }
        // Existing production writers retain their authority/intent/epoch checks.
        // Do this after the recorded retirement and immediately before Commit.
        await InvokeWriter("SavePreparedPlanAsync", draft, rows, plan, (Func<bool>)(() => true), CancellationToken.None);
        await InvokeWriter("SavePreparedDocumentAsync", draft, documentJson, (Func<bool>)(() => true), CancellationToken.None);
        using (var connection = factory.Open())
        {
            Assert.AreEqual(documentJson, connection.ExecuteScalar<string>("SELECT plan_document_json FROM catalog_import_prepared_plan WHERE original_id=@id", new { id = draft.Original.Id }));
            Assert.IsNull(connection.ExecuteScalar<string?>("SELECT remote_plan_json FROM catalog_import_prepared_plan WHERE original_id=@id", new { id = draft.Original.Id }), "Journal setup cannot manufacture remote acceptance.");
            Assert.AreEqual(outboxBefore, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"), "No child or ACK is published by the seed.");
        }
        Assert.AreEqual(before, EconomicFingerprint(factory), "Historical journal setup cannot change any local product, metadata or price-history field.");
        return entries;
    }

    internal static string EconomicFingerprint(SqliteConnectionFactory factory, bool allowAckMetadata = false)
    {
        using var connection = factory.Open();
        object[] Rows(string table, string order) => connection.Query("SELECT * FROM " + table + " ORDER BY " + order).Select(row =>
        {
            var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in (IDictionary<string, object>)row)
            {
                if (allowAckMetadata && (field.Key.StartsWith("remote_", StringComparison.Ordinal) ||
                    field.Key is "catalog_import_client_item_id" or "catalog_import_idempotency_key")) continue;
                result[field.Key] = allowAckMetadata && table == "product_price_history" && field.Key == "type"
                    ? ((string)field.Value).Trim().ToLowerInvariant() : field.Value;
            }
            return (object)result;
        }).ToArray();
        return Hash(JsonSerializer.Serialize(new { products = Rows("products", "id"), metadata = Rows("product_meta", "barcode"), prices = Rows("product_price_history", "id") }));
    }
    private static string Hash(string value) => CatalogImportOutboxPayloadBuilder.Sha256Hex(value);
}
