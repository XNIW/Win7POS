using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosCatalogImportRecoveryContractTests
{
    [TestMethod]
    public void MacFixtures_AreByteIdenticalToCoordinatedContract()
    {
        var expected = new Dictionary<string, string>
        {
            ["lookup.request.json"] = "ec509e041b211ac881db593c0e6acb95efe291960571833cf1fbb826402ca3b3",
            ["retire.request.json"] = "84a67c7c98c5f763b1169bc14caeb746550c2d2165fdec081648c8469a06ec6e",
            ["accepted.response.json"] = "ca2111b7032db923c28228977c1ffb6382f547ecbf70d16e69f20227d126789b",
            ["not-found.response.json"] = "44cc7ba0ff25b8bcfaa1d54c2c2ee80ff80ed837571f03aa5194d8d79cf0eddc",
            ["retired.response.json"] = "a6bc4b3a4973f26fba2923836f8426fc73f5bb8f221b8a4c5ee1048dcf9e6a60",
            ["retire.accepted.response.json"] = "c7424784823815c22863274dea73268ce951e449a5a0d1228a4cf283953dba93",
            ["correction.request.json"] = "33a9f1325e8c03d8dd684eff9293704e7523a1cb2697b9c148c196e1fe7f8ec5",
            ["correction.response.json"] = "f9e4e280b6a609834e4cd09a4648a2d0d02432f5812a1ce141819e5c223c9924",
            ["correction-target.lookup.request.json"] = "bc0530b9e337ae38e80c9c5e3f2c3333ebea21593dd50fe710ad66240f248086",
            ["correction-target.retire.request.json"] = "0e54ae7c5ae5bf34bee4058817ad6f70e718969780a31c3b2fdf4453e7f413cf",
            ["correction-target.accepted.response.json"] = "161829c925ae3fb9208690d97cfe1adba97ee40861e98409faef1dd72cd23233",
            ["correction.no-effect.request.json"] = "6557c20b689eafefe8765ae9d721b41c538b95448adc0584fd3dbc40eac255cd",
            ["correction.no-effect.response.json"] = "5483aedee84827ee085859316a71817fbd7be74249d410a1fdb2a22b91e41015"
        };
        foreach (var fixture in expected)
            Assert.AreEqual(fixture.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePath(fixture.Key)))).ToLowerInvariant(), fixture.Key);
    }

    [TestMethod]
    public void PersistedReceipt_DecodesRpcAckAndCurrentSnapshotWithoutInventingHttpMetadata()
    {
        var value = Read<PosCatalogImportReceiptResponse>(FixtureText("accepted.response.json"));
        Assert.IsTrue(value.Ok);
        Assert.AreEqual(PosCatalogImportReceiptContract.SchemaVersion, value.SchemaVersion);
        Assert.AreEqual(PosOnlineContract.CatalogImportSchemaVersion, value.OriginalSchemaVersion);
        Assert.AreEqual("accepted", value.Status);
        Assert.AreEqual("10000000-0000-4000-8000-000000000094", value.ShopId);
        Assert.AreEqual("30000000-0000-4000-8000-000000000094", value.ShopDeviceId);
        Assert.AreEqual("receipt-fixture-import-1", value.ClientImportId);
        Assert.AreEqual("receipt-fixture-idem-1", value.IdempotencyKey);
        Assert.AreEqual("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", value.PayloadHash);
        Assert.AreEqual("sha256:4fb7dfe81c98ab859a1a64992475b114c524233a83962ba548f1414110a39655", value.CanonicalPayloadHash);
        Assert.IsNotNull(value.Receipt);
        Assert.AreEqual("70000000-0000-4000-8000-000000000094", value.Receipt.BatchId);
        Assert.AreEqual("accepted", value.Receipt.Items.Single().Status);
        Assert.AreEqual(2, value.Receipt.RemotePriceIds.Length);
        Assert.AreEqual(value.Receipt.RemoteProductIds.Single().RemoteProductId, value.Receipt.RemotePriceIds[0].RemoteProductId);
        Assert.AreEqual(1, value.Receipt.Summary.AcceptedItemCount);
        Assert.AreEqual(0, value.Receipt.Summary.DuplicateItemCount);
        Assert.AreEqual(1, value.Receipt.Summary.ProductCount);
        var snapshot = value.CurrentProductSnapshots.Single();
        Assert.AreEqual("available", snapshot.SnapshotStatus);
        Assert.AreEqual("2026-10-08T19:55:00.000001Z", snapshot.BaseRevision);
        Assert.AreEqual(1200m, snapshot.RetailPrice);
        Assert.AreEqual(900m, snapshot.PurchasePrice);
        Assert.AreEqual(1.25m, snapshot.StockQuantity);
        using var encoded = JsonDocument.Parse(Write(value));
        Assert.IsFalse(encoded.RootElement.TryGetProperty("shop", out _));
        Assert.IsFalse(encoded.RootElement.TryGetProperty("batch", out _));
        Assert.IsFalse(encoded.RootElement.GetProperty("receipt").TryGetProperty("serverTime", out _));
    }

    [TestMethod]
    public void AbsenceSnapshotAndPersistentRetirement_RemainDistinct()
    {
        var missing = Read<PosCatalogImportReceiptResponse>(FixtureText("not-found.response.json"));
        Assert.AreEqual("not_found", missing.Status);
        Assert.IsTrue(missing.SnapshotOnly);
        Assert.IsFalse(missing.ReplacementAllowed);
        Assert.IsFalse(missing.OldIdentityBlocked);
        Assert.IsNull(missing.Receipt);
        var retired = Read<PosCatalogImportReceiptResponse>(FixtureText("retired.response.json"));
        Assert.AreEqual(PosCatalogImportReceiptContract.RetirementSchemaVersion, retired.SchemaVersion);
        Assert.AreEqual("retired", retired.Status);
        Assert.IsTrue(retired.OldIdentityBlocked);
        Assert.IsFalse(retired.SnapshotOnly);
        Assert.AreEqual("2026-10-08T20:00:00.000Z", retired.RetiredAt);
        Assert.IsNull(retired.Receipt);
        var alreadyAccepted = Read<PosCatalogImportReceiptResponse>(FixtureText("retire.accepted.response.json"));
        Assert.AreEqual(PosCatalogImportReceiptContract.RetirementSchemaVersion, alreadyAccepted.SchemaVersion);
        Assert.AreEqual(PosOnlineContract.CatalogImportSchemaVersion, alreadyAccepted.OriginalSchemaVersion);
        Assert.AreEqual("accepted", alreadyAccepted.Status);
        Assert.IsNotNull(alreadyAccepted.Receipt);
        Assert.IsFalse(alreadyAccepted.OldIdentityBlocked, "Retirement must retain an already accepted operation rather than claiming a new fence.");
    }

    [TestMethod]
    public void Correction_UsesOriginalIdentityAndExplicitNumericMaskWithoutMetadata()
    {
        var value = Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
        Assert.AreEqual(PosCatalogImportCorrectionContract.SchemaVersion, value.SchemaVersion);
        Assert.AreEqual(value.RecoveryOf.ClientImportId, value.RecoveryOf.OriginalRequest.Batch.ClientImportId);
        Assert.AreEqual(value.RecoveryOf.IdempotencyKey, value.RecoveryOf.OriginalRequest.Batch.IdempotencyKey);
        Assert.AreEqual(value.RecoveryOf.PayloadHash, value.RecoveryOf.OriginalRequest.PayloadHash);
        Assert.AreNotEqual(value.RecoveryOf.ClientImportId, value.Correction.ClientImportId);
        Assert.AreNotEqual(value.RecoveryOf.IdempotencyKey, value.Correction.IdempotencyKey);
        var item = value.Correction.Items.Single();
        Assert.AreEqual("receipt-fixture-row-1", item.ClientItemId);
        Assert.AreEqual("2026-10-08T19:55:00.000001Z", item.BaseRevision);
        CollectionAssert.AreEqual(new[] { "purchasePrice", "retailPrice", "quantityDelta" }, item.FieldMask);
        Assert.AreEqual(950m, item.Changes.PurchasePrice);
        Assert.AreEqual(1300m, item.Changes.RetailPrice);
        Assert.AreEqual(0.25m, item.Changes.QuantityDelta);
        using var encoded = JsonDocument.Parse(Write(value));
        var wireItem = encoded.RootElement.GetProperty("correction").GetProperty("items")[0];
        CollectionAssert.AreEquivalent(new[] { "clientItemId", "remoteProductId", "baseRevision", "baseSnapshot", "fieldMask", "changes" },
            wireItem.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(JsonValueKind.Number, wireItem.GetProperty("changes").GetProperty("retailPrice").ValueKind);
    }

    [TestMethod]
    public void Correction_OmittedFieldsStayOmittedAndZeroRemainsAnExplicitNumber()
    {
        var item = new PosCatalogImportCorrectionItem
        {
            ClientItemId = "row", RemoteProductId = "80000000-0000-4000-8000-000000000094",
            BaseRevision = "2026-10-08T19:55:00.000001Z", FieldMask = new[] { "retailPrice" },
            Changes = new PosCatalogImportCorrectionChanges { RetailPrice = 0m }
        };
        using var encoded = JsonDocument.Parse(Write(item));
        var changes = encoded.RootElement.GetProperty("changes");
        Assert.AreEqual(1, changes.EnumerateObject().Count());
        Assert.AreEqual(0m, changes.GetProperty("retailPrice").GetDecimal());
        Assert.IsFalse(changes.TryGetProperty("purchasePrice", out _));
        Assert.IsFalse(changes.TryGetProperty("quantityDelta", out _));
    }

    [TestMethod]
    public async Task BaseSnapshot_PreservesMaskedNullAndOmitsUnmaskedFieldsWithoutLimitingLegacyValues()
    {
        var correction = Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
        var row = correction.Correction.Items[0];
        row.FieldMask = new[] { "retailPrice" };
        row.Changes = new PosCatalogImportCorrectionChanges { RetailPrice = 0m };
        row.BaseSnapshot = new PosCatalogImportCorrectionBaseSnapshot { RetailPrice = null };
        var proof = Read<PosCatalogImportReceiptResponse>(FixtureText("accepted.response.json"));
        proof.OriginalSchemaVersion = PosOnlineContract.CatalogImportSchemaVersion;
        proof.CurrentProductSnapshots[0].RetailPrice = null;
        proof.CurrentProductSnapshots[0].PurchasePrice = null;
        proof.CurrentProductSnapshots[0].StockQuantity = decimal.MaxValue;
        var item = SavedItem(correction, proof);
        using var handler = new CaptureHandler();
        using var client = new PosAdminWebClient(new PosAdminWebOptions(new Uri("https://recovery.example.invalid/")), handler);
        await client.CatalogImportCorrectionAsync(CatalogImportCorrectionTransport.BuildTransportRequest(item, Trusted()), CancellationToken.None);
        using (var body = JsonDocument.Parse(handler.Body))
        {
            var snapshot = body.RootElement.GetProperty("correction").GetProperty("items")[0].GetProperty("baseSnapshot");
            Assert.AreEqual(1, snapshot.EnumerateObject().Count());
            Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("retailPrice").ValueKind);
            Assert.IsFalse(snapshot.TryGetProperty("stockQuantity", out _));
            Assert.IsFalse(snapshot.TryGetProperty("purchasePrice", out _));
        }
        row.BaseSnapshot.PurchasePrice = null;
        Assert.AreEqual("payload_invalid", Assert.ThrowsExactly<CatalogImportRecoveryException>(() =>
            CatalogImportCorrectionTransport.SerializeSaved(correction, proof)).Code);
    }

    [TestMethod]
    public void MacGoldenCorrectionTargetLookup_RetainsNestedCorrectionAndExactOriginalSchemaBinding()
    {
        foreach (var fixture in new[] { "correction-target.lookup.request.json", "correction-target.retire.request.json" })
        {
            var envelope = Read<PosCatalogImportCorrectionReceiptRequest>(FixtureText(fixture));
            Assert.AreEqual(envelope.ClientImportId, envelope.OriginalRequest.Correction.ClientImportId);
            Assert.AreEqual(envelope.IdempotencyKey, envelope.OriginalRequest.Correction.IdempotencyKey);
            Assert.AreEqual(envelope.PayloadHash, envelope.OriginalRequest.Correction.PayloadHash);
            Assert.AreEqual(PosCatalogImportCorrectionContract.SchemaVersion, envelope.OriginalRequest.SchemaVersion);
            CollectionAssert.AreEquivalent(new[] { "retailPrice", "purchasePrice", "stockQuantity" },
                envelope.OriginalRequest.Correction.Items[0].BaseSnapshot.Keys.ToArray());
        }
        var receipt = Read<PosCatalogImportReceiptResponse>(FixtureText("correction-target.accepted.response.json"));
        Assert.AreEqual(PosCatalogImportCorrectionContract.SchemaVersion, receipt.OriginalSchemaVersion);
        Assert.AreEqual("2026-10-08T20:00:00.000001Z", receipt.Receipt.Items[0].AuthoritativeRevision);
        Assert.AreEqual(receipt.Receipt.Items[0].AuthoritativeRevision, receipt.Receipt.RemoteProductIds[0].AuthoritativeRevision);
    }

    [TestMethod]
    [DataRow("correction.request.json", "correction.response.json", 2)]
    [DataRow("correction.no-effect.request.json", "correction.no-effect.response.json", 0)]
    public void MacGoldenCorrectionAck_ValidatesPersistedRevisionsOwnersAndUnchangedFields(string requestFixture, string responseFixture, int priceCount)
    {
        var correction = Read<PosCatalogImportCorrectionRequest>(FixtureText(requestFixture));
        var item = SavedItem(correction);
        var request = CatalogImportCorrectionTransport.BuildTransportRequest(item, Trusted());
        var response = Read<PosCatalogImportCorrectionResponse>(FixtureText(responseFixture));
        // The fixture pins canonical intent; the outer client raw hash echoes
        // this locally persisted request-and-proof envelope at dispatch.
        response.PayloadHash = item.PayloadHash;
        var ack = CatalogImportCorrectionTransport.ValidateResponse(item, request, response);
        Assert.AreEqual(priceCount, ack.RemotePriceIds.Count);
        Assert.AreEqual("RECEIPT-FIXTURE-1", ack.RemoteProductIds.Single().Barcode);
        if (priceCount == 0)
        {
            response.Receipt.Items[0].UnchangedFields = Array.Empty<string>();
            Assert.AreEqual("receipt_incomplete", Assert.ThrowsExactly<CatalogImportRecoveryException>(() =>
                CatalogImportCorrectionTransport.ValidateResponse(item, request, response)).Code);
        }
    }

    [TestMethod]
    public async Task ClientRoutes_UsePostNoStoreAndKeepLookupRetirementCorrectionSeparate()
    {
        using var handler = new CaptureHandler();
        using var client = new PosAdminWebClient(new PosAdminWebOptions(new Uri("https://recovery.example.invalid/")), handler);
        handler.Response = FixtureText("accepted.response.json");
        var lookup = await client.CatalogImportReceiptAsync(Read<PosCatalogImportReceiptRequest>(FixtureText("lookup.request.json")), CancellationToken.None);
        Assert.IsTrue(lookup.Success);
        Assert.AreEqual("/api/pos/catalog/import-receipt", handler.Path);
        Assert.AreEqual("accepted", lookup.Value.Status);
        handler.Response = FixtureText("retired.response.json");
        var retirement = await client.CatalogImportRetireAsync(Read<PosCatalogImportReceiptRequest>(FixtureText("retire.request.json")), CancellationToken.None);
        Assert.IsTrue(retirement.Success);
        Assert.AreEqual("/api/pos/catalog/import-retire", handler.Path);
        Assert.IsTrue(retirement.Value.OldIdentityBlocked);
        // Synthetic transport response only; business acceptance is tested against
        // the coordinated correction ACK fixture once that fixture is published.
        handler.Response = "{\"ok\":false,\"code\":\"synthetic_transport_probe\"}";
        var correction = await client.CatalogImportCorrectionAsync(Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json")), CancellationToken.None);
        Assert.IsTrue(correction.Success);
        Assert.AreEqual("/api/pos/catalog/import-correction", handler.Path);
        Assert.AreEqual("synthetic_transport_probe", correction.Value.Code);
        Assert.AreEqual(3, handler.Calls);
        Assert.IsTrue(handler.AllPostAndNoStore);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.AreEqual(PosCatalogImportCorrectionContract.SchemaVersion, body.RootElement.GetProperty("schemaVersion").GetString());
        Assert.IsTrue(body.RootElement.TryGetProperty("recoveryOf", out _));
        Assert.IsTrue(body.RootElement.TryGetProperty("correction", out _));
        Assert.IsFalse(body.RootElement.TryGetProperty("batch", out _));
    }

    [TestMethod]
    public async Task CorrectionIdentityLookupAndRetirement_KeepTypedNestedSchemaAndUseSameNoStoreRoutes()
    {
        var item = SavedItem();
        var nested = CatalogImportCorrectionTransport.ReadSavedRequest(item.PayloadJson);
        nested.Correction.PayloadHash = item.PayloadHash;
        nested.RecoveryOf.OriginalRequest.PayloadHash = nested.RecoveryOf.PayloadHash;
        var envelope = new PosCatalogImportCorrectionReceiptRequest
        {
            ClientImportId = item.ClientImportId, IdempotencyKey = item.IdempotencyKey, PayloadHash = item.PayloadHash,
            OriginalRequest = nested, DeviceToken = Trusted().DeviceToken, SessionToken = Trusted().SessionToken,
            PosSessionId = Trusted().PosSessionId, ShopDeviceId = Trusted().ShopDeviceId, ShopCode = Trusted().ShopCode
        };
        using var handler = new CaptureHandler { Response = "{\"ok\":false,\"code\":\"synthetic_chain_transport_probe\"}" };
        using var client = new PosAdminWebClient(new PosAdminWebOptions(new Uri("https://recovery.example.invalid/")), handler);
        await client.CatalogImportReceiptAsync(envelope, CancellationToken.None);
        Assert.AreEqual("/api/pos/catalog/import-receipt", handler.Path);
        Assert.AreEqual(1, handler.Calls, "Lookup must not dispatch retirement implicitly.");
        using (var body = JsonDocument.Parse(handler.Body))
        {
            Assert.AreEqual(PosCatalogImportReceiptContract.SchemaVersion, body.RootElement.GetProperty("schemaVersion").GetString());
            var original = body.RootElement.GetProperty("originalRequest");
            Assert.AreEqual(PosCatalogImportCorrectionContract.SchemaVersion, original.GetProperty("schemaVersion").GetString());
            Assert.AreEqual(item.PayloadHash, original.GetProperty("correction").GetProperty("payloadHash").GetString());
            Assert.AreEqual(nested.RecoveryOf.PayloadHash, original.GetProperty("recoveryOf").GetProperty("originalRequest").GetProperty("payloadHash").GetString());
            Assert.IsFalse(original.TryGetProperty("originalReceipt", out _));
        }
        envelope.SchemaVersion = PosCatalogImportReceiptContract.RetirementSchemaVersion;
        await client.CatalogImportRetireAsync(envelope, CancellationToken.None);
        Assert.AreEqual("/api/pos/catalog/import-retire", handler.Path);
        Assert.AreEqual(2, handler.Calls);
        Assert.IsTrue(handler.AllPostAndNoStore);
        Assert.IsNull(CatalogImportCorrectionTransport.ReadSavedRequest(item.PayloadJson).Correction.PayloadHash,
            "Attaching child transport hash must not mutate the saved envelope.");
    }

    [TestMethod]
    public void SavedCorrection_BindsProofAndTransportHashesWithoutPersistingCredentials()
    {
        var item = SavedItem();
        var json = item.PayloadJson;
        var hash = item.PayloadHash;
        Assert.AreEqual("", CatalogImportCorrectionTransport.ValidateSaved(item));
        Assert.IsFalse(json.Contains("fixture-device-token-not-a-secret", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("fixture-session-token-not-a-secret", StringComparison.Ordinal));
        var request = CatalogImportCorrectionTransport.BuildTransportRequest(item, Trusted());
        Assert.AreEqual(hash, request.Correction.PayloadHash);
        Assert.AreEqual(request.RecoveryOf.PayloadHash, request.RecoveryOf.OriginalRequest.PayloadHash);
        Assert.AreEqual(Trusted().DeviceToken, request.DeviceToken);
        using var transport = JsonDocument.Parse(Write(request));
        Assert.IsFalse(transport.RootElement.TryGetProperty("originalReceipt", out _));
        Assert.IsTrue(transport.RootElement.TryGetProperty("recoveryOf", out _));
        Assert.AreEqual(json, item.PayloadJson);
        Assert.AreEqual(hash, CatalogImportOutboxPayloadBuilder.Sha256Hex(item.PayloadJson));
        var intended = CatalogImportCorrectionTransport.ReadIntendedRequest(json);
        Assert.AreEqual("RECEIPT-FIXTURE-1", intended.Items.Single().Barcode);
        Assert.AreEqual(1, intended.Items.Single().RowNumber);
        Assert.AreEqual("1300", intended.Items.Single().RetailPrice);
        Assert.AreEqual("0.25", intended.Items.Single().Quantity);
    }

    [TestMethod]
    [DataRow("missing_price")]
    [DataRow("wrong_price_owner")]
    [DataRow("wrong_revision")]
    [DataRow("wrong_product")]
    [DataRow("duplicate_price")]
    [DataRow("wrong_shop")]
    public void CorrectionAck_RejectsIncompleteOrConflictingOwnership(string fault)
    {
        var item = SavedItem();
        var request = CatalogImportCorrectionTransport.BuildTransportRequest(item, Trusted());
        var response = CorrectionAck(item, request);
        if (fault == "missing_price") response.Receipt.RemotePriceIds = response.Receipt.RemotePriceIds.Take(1).ToArray();
        if (fault == "wrong_price_owner") response.Receipt.RemotePriceIds[0].RemoteProductId = "b0000000-0000-4000-8000-000000000094";
        if (fault == "wrong_revision") response.Receipt.Items[0].AuthoritativeRevision = "2026-10-08T19:55:00.000003Z";
        if (fault == "wrong_product") response.Receipt.RemoteProductIds[0].RemoteProductId = "b0000000-0000-4000-8000-000000000094";
        if (fault == "duplicate_price") response.Receipt.RemotePriceIds[1].RemotePriceId = response.Receipt.RemotePriceIds[0].RemotePriceId;
        if (fault == "wrong_shop") response.ShopId = "b0000000-0000-4000-8000-000000000094";
        Assert.ThrowsExactly<CatalogImportRecoveryException>(() => CatalogImportCorrectionTransport.ValidateResponse(item, request, response));
    }

    [TestMethod]
    public void CorrectionAck_CurrentRemoteBarcodeDoesNotRewriteOriginalHistoryIdentity()
    {
        var item = SavedItem();
        var request = CatalogImportCorrectionTransport.BuildTransportRequest(item, Trusted());
        var response = CorrectionAck(item, request);
        response.Receipt.Items[0].Barcode = "RENAMED-REMOTE";
        response.Receipt.RemoteProductIds[0].Barcode = "RENAMED-REMOTE";
        foreach (var price in response.Receipt.RemotePriceIds) price.Barcode = "RENAMED-REMOTE";
        var ack = CatalogImportCorrectionTransport.ValidateResponse(item, request, response);
        Assert.AreEqual("RECEIPT-FIXTURE-1", ack.RemoteProductIds.Single().Barcode);
        Assert.IsTrue(ack.RemotePriceIds.All(price => price.Barcode == "RECEIPT-FIXTURE-1"));
    }

    [TestMethod]
    public void ProvenNoEffectPrice_RequiresMatchingDurableSnapshotAndDoesNotInventPriceIdentity()
    {
        var correction = Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
        correction.Correction.Items[0].Changes.RetailPrice = 1200m;
        var proof = Read<PosCatalogImportReceiptResponse>(FixtureText("accepted.response.json"));
        var item = SavedItem(correction, proof);
        var request = CatalogImportCorrectionTransport.BuildTransportRequest(item, Trusted());
        var response = CorrectionAck(item, request);
        response.Receipt.RemotePriceIds = response.Receipt.RemotePriceIds.Where(price => price.PriceType != "retail").ToArray();
        response.Receipt.Items[0].UnchangedFields = new[] { "retailPrice" };
        var ack = CatalogImportCorrectionTransport.ValidateResponse(item, request, response);
        Assert.AreEqual(1, ack.RemotePriceIds.Count);
        Assert.AreEqual("purchase", ack.RemotePriceIds.Single().PriceType);
        Assert.AreEqual("1200", CatalogImportCorrectionTransport.ReadIntendedRequest(item.PayloadJson).Items[0].RetailPrice);
        var ackIntent = CatalogImportCorrectionTransport.ReadAckIntendedRequest(item.PayloadJson).Items[0];
        Assert.IsNull(ackIntent.RetailPrice);
        Assert.AreEqual("950", ackIntent.PurchasePrice);
        Assert.AreEqual("0.25", ackIntent.Quantity, "Quantity delta remains an effect even when current stock happens to match.");

        proof.CurrentProductSnapshots[0].RetailPrice = 1199m;
        correction.Correction.Items[0].BaseSnapshot.RetailPrice = 1199m;
        var unproven = SavedItem(correction, proof);
        var unprovenRequest = CatalogImportCorrectionTransport.BuildTransportRequest(unproven, Trusted());
        var incomplete = CorrectionAck(unproven, unprovenRequest);
        incomplete.Receipt.RemotePriceIds = incomplete.Receipt.RemotePriceIds.Where(price => price.PriceType != "retail").ToArray();
        Assert.AreEqual("receipt_incomplete", Assert.ThrowsExactly<CatalogImportRecoveryException>(() =>
            CatalogImportCorrectionTransport.ValidateResponse(unproven, unprovenRequest, incomplete)).Code);
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("code")]
    [DataRow("rpc_status")]
    public void SavedProof_RejectsWrongEnvelopeSchemaCodeAndPersistedStatus(string fault)
    {
        var correction = Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
        var proof = Read<PosCatalogImportReceiptResponse>(FixtureText("accepted.response.json"));
        if (fault == "schema") proof.SchemaVersion = "unrelated-v1";
        if (fault == "code") proof.Code = "conflict";
        if (fault == "rpc_status") proof.Receipt.Status = "retired";
        Assert.AreEqual("receipt_required", Assert.ThrowsExactly<CatalogImportRecoveryException>(() =>
            CatalogImportCorrectionTransport.SerializeSaved(correction, proof)).Code);
    }

    [TestMethod]
    public void SavedProof_AcceptsExistingOrdinaryAckReturnedByRetirement()
    {
        var correction = Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
        var proof = Read<PosCatalogImportReceiptResponse>(FixtureText("retire.accepted.response.json"));
        proof.Receipt.Status = "duplicate";
        Assert.IsFalse(string.IsNullOrWhiteSpace(CatalogImportCorrectionTransport.SerializeSaved(correction, proof)));
    }

    [TestMethod]
    public void SavedCorrection_RejectsPrecisionAndProofTampering()
    {
        var correction = Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
        var proof = Read<PosCatalogImportReceiptResponse>(FixtureText("accepted.response.json"));
        proof.OriginalSchemaVersion = PosOnlineContract.CatalogImportSchemaVersion;
        correction.Correction.Items[0].Changes.RetailPrice = 1.0001m;
        Assert.AreEqual("payload_invalid", Assert.ThrowsExactly<CatalogImportRecoveryException>(() =>
            CatalogImportCorrectionTransport.SerializeSaved(correction, proof)).Code);
        correction.Correction.Items[0].Changes.RetailPrice = 1.001m;
        correction.Correction.Items[0].Changes.QuantityDelta = decimal.MinValue;
        Assert.AreEqual("payload_invalid", Assert.ThrowsExactly<CatalogImportRecoveryException>(() =>
            CatalogImportCorrectionTransport.SerializeSaved(correction, proof)).Code);
        correction.Correction.Items[0].Changes.QuantityDelta = 0.25m;
        proof.CurrentProductSnapshots[0].BaseRevision = "2026-10-08T19:55:00.000002Z";
        Assert.AreEqual("receipt_required", Assert.ThrowsExactly<CatalogImportRecoveryException>(() =>
            CatalogImportCorrectionTransport.SerializeSaved(correction, proof)).Code);
        var item = SavedItem();
        item.PayloadJson += " ";
        Assert.AreEqual("payload_hash_mismatch", CatalogImportCorrectionTransport.ValidateSaved(item));
    }

    private static CatalogImportOutboxItem SavedItem(PosCatalogImportCorrectionRequest? request = null, PosCatalogImportReceiptResponse? proof = null)
    {
        request ??= Read<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
        proof ??= Read<PosCatalogImportReceiptResponse>(FixtureText("accepted.response.json"));
        proof.OriginalSchemaVersion = PosOnlineContract.CatalogImportSchemaVersion;
        var json = CatalogImportCorrectionTransport.SerializeSaved(request, proof);
        return new CatalogImportOutboxItem { Id = 2, ClientImportId = request.Correction.ClientImportId,
            IdempotencyKey = request.Correction.IdempotencyKey, SchemaVersion = PosCatalogImportCorrectionContract.SchemaVersion,
            OperationType = CatalogImportCorrectionTransport.OperationType, OriginShopId = proof.ShopId, OriginShopCode = "FIXTURE",
            PayloadJson = json, PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json) };
    }

    private static PosTrustedDeviceSession Trusted() => new() { DeviceToken = "current-device", SessionToken = "current-session",
        ShopDeviceId = "30000000-0000-4000-8000-000000000094", PosSessionId = "40000000-0000-4000-8000-000000000094",
        ShopId = "10000000-0000-4000-8000-000000000094", ShopCode = "FIXTURE" };

    private static PosCatalogImportCorrectionResponse CorrectionAck(CatalogImportOutboxItem item, PosCatalogImportCorrectionRequest request)
    {
        const string revision = "2026-10-08T19:55:00.000002Z";
        var row = request.Correction.Items.Single();
        return new PosCatalogImportCorrectionResponse { Ok = true, Code = "success", SchemaVersion = PosCatalogImportCorrectionContract.SchemaVersion,
            Status = "accepted", ShopId = item.OriginShopId, ShopDeviceId = request.ShopDeviceId, ClientImportId = item.ClientImportId,
            IdempotencyKey = item.IdempotencyKey, PayloadHash = item.PayloadHash, CanonicalPayloadHash = "sha256:" + new string('a', 64),
            Receipt = new PosCatalogImportPersistedAck { Ok = true, BatchId = "70000000-0000-4000-8000-000000000094", Status = "accepted",
                Items = new[] { new PosCatalogImportPersistedItemAck { ClientItemId = row.ClientItemId, RemoteProductId = row.RemoteProductId,
                    AuthoritativeRevision = revision, Status = "accepted" } },
                RemoteProductIds = new[] { new PosCatalogImportPersistedProductAck { ClientItemId = row.ClientItemId,
                    RemoteProductId = row.RemoteProductId, AuthoritativeRevision = revision } },
                RemotePriceIds = new[] {
                    new PosCatalogImportPersistedPriceAck { ClientItemId = row.ClientItemId, RemoteProductId = row.RemoteProductId, PriceType = "purchase", RemotePriceId = "90000000-0000-4000-8000-000000000094" },
                    new PosCatalogImportPersistedPriceAck { ClientItemId = row.ClientItemId, RemoteProductId = row.RemoteProductId, PriceType = "retail", RemotePriceId = "a0000000-0000-4000-8000-000000000094" } },
                Summary = new PosCatalogImportPersistedSummary { AcceptedItemCount = 1, ProductCount = 1 } } };
    }

    private static T Read<T>(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(stream)!;
    }

    private static string Write<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string FixtureText(string name) => File.ReadAllText(FixturePath(name));
    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Win7POS.slnx")))
                return Path.Combine(directory.FullName, "tests", "fixtures", "pos-catalog-import-receipt-v1", name);
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Win7POS repository root not found.");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Response { get; set; } = "{}";
        public string Path { get; private set; } = "";
        public string Body { get; private set; } = "";
        public int Calls { get; private set; }
        public bool AllPostAndNoStore { get; private set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AllPostAndNoStore &= request.Method == HttpMethod.Post && request.Headers.CacheControl?.NoStore == true &&
                request.Headers.Pragma.Any(value => value.Name == "no-cache");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
}
