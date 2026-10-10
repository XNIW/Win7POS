using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    internal sealed class CatalogImportSupersessionAcceptedEntry
    {
        public CatalogImportOutboxEntry Entry { get; set; }
        public PosCatalogImportReceiptResponse Receipt { get; set; }
        public PosCatalogImportRequest Intent { get; set; }
        public CatalogImportAckResult Ack { get; set; }
        public int PartIndex { get; set; }
    }

    internal sealed class CatalogImportRecoverySupersession
    {
        public string PlanId { get; set; }
        public CatalogImportSavedRemotePlan SavedPlan { get; set; }
        public PosCatalogImportReceiptResponse[] PartReceipts { get; set; }
        public IReadOnlyList<CatalogImportSupersessionAcceptedEntry> AcceptedEntries { get; set; }
        public IReadOnlyList<CatalogImportSupersessionAcceptedEntry> AcceptedEntriesToPublish { get; set; }
        public SupplierImportEditableRow[] FrozenRows { get; set; }
        public SupplierImportEditableRow[] AppliedRows { get; set; }
        public IReadOnlyList<PosCatalogImportRecoveryCoverage> CarryCoverage { get; set; }
        public IReadOnlyList<CatalogImportOutboxEntry> Entries { get; set; }
        public bool DeferredRows { get; set; }
        public SupplierImportEditableRow[] DeferredDesiredRows { get; set; }
        public bool IsSettled => PartReceipts.All(receipt => receipt != null &&
            (receipt.Status == "accepted" || receipt.Status == "retired"));
        public bool LocalAlreadyApplied { get; set; }
    }

    public sealed partial class CatalogImportRecoveryService
    {
        public async Task<CatalogImportRecoveryDraft> RetireCommittedPlanAsync(CatalogImportRecoveryDraft childDraft,
            PosAdminWebOptions options, PosTrustedDeviceSession session, OnlineSyncGeneration generation,
            Func<bool> authorize, CancellationToken token)
        {
            DemandPermission(authorize);
            CommittedSupersessionSource source;
            using (var conn = _factory.Open())
                source = await conn.QuerySingleOrDefaultAsync<CommittedSupersessionSource>(@"SELECT p.plan_id AS LocalPlanId,
p.original_id AS OriginalId,p.remote_plan_json AS RemoteJson,p.remote_plan_hash AS RemoteHash,
p.recovery_rows_json AS RowsJson,p.recovery_rows_hash AS RowsHash FROM catalog_import_plan p
JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE m.outbox_id=@id AND p.original_id IS NOT NULL",
                    new { id = (childDraft?.TargetOriginal ?? childDraft?.Original)?.Id }).ConfigureAwait(false);
            if (source == null || source.RemoteJson == null || CatalogImportOutboxPayloadBuilder.Sha256Hex(source.RemoteJson) != source.RemoteHash)
                throw new CatalogImportRecoveryException("prepared_plan_required");
            var draft = await LoadLocalAsync(source.OriginalId, session, generation, token).ConfigureAwait(false);
            draft.TransportOptions = options;
            ReadFreshSession(draft, session, authorize);
            var existing = await ReadSupersessionRowAsync(source.OriginalId, token).ConfigureAwait(false);
            var remote = Deserialize<CatalogImportSavedRemotePlan>(source.RemoteJson);
            if (existing != null && existing.PlanId == remote.Document.PlanId)
                return await RetirePreparedPlanAsync(draft, options, session, generation, authorize, token).ConfigureAwait(false);
            CatalogImportOutboxEntry[] entries;
            using (var conn = _factory.Open())
            {
                entries = (await conn.QueryAsync<CatalogImportOutboxEntry>(@"SELECT o.client_import_id AS ClientImportId,
o.idempotency_key AS IdempotencyKey,o.created_at AS CreatedAt,o.operation_type AS OperationType,o.schema_version AS SchemaVersion,
o.source AS Source,o.payload_json AS PayloadJson,o.payload_hash AS PayloadHash FROM catalog_import_plan_part m
JOIN catalog_import_outbox o ON o.id=m.outbox_id WHERE m.plan_id=@id ORDER BY m.ordinal", new { id = source.LocalPlanId }).ConfigureAwait(false)).ToArray();
                var proofs = new Dictionary<string, CatalogImportCorrectionSharedProof>();
                foreach (var entry in entries)
                    if (entry.OperationType == "catalog_import_correction")
                        entry.SharedProof = await CatalogImportCorrectionSharedProof.LoadAsync(conn, null, entry.PayloadJson, proofs).ConfigureAwait(false);
            }
            var plan = CatalogImportPlanBuilder.Plan(entries);
            if (plan.PlanId != source.LocalPlanId || entries.Any(entry => CatalogImportOutboxPayloadBuilder.Sha256Hex(entry.PayloadJson) != entry.PayloadHash))
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            ValidateRemotePlan(plan, remote.Document, remote.Receipt, ReadFreshSession(draft, session, authorize));
            SupplierImportEditableRow[] frozen;
            if (source.RowsJson != null)
            {
                if (CatalogImportOutboxPayloadBuilder.Sha256Hex(source.RowsJson) != source.RowsHash)
                    throw new CatalogImportRecoveryException("payload_hash_mismatch");
                frozen = Deserialize<SupplierImportEditableRow[]>(source.RowsJson);
            }
            else
            {
                // Transitional ordinary plans have complete immutable row
                // intent in each child. Corrections require the saved baseline.
                if (remote.Document.Mode != "replacement") throw new CatalogImportRecoveryException("prepared_plan_required");
                var byRootId = draft.OriginalRequest.Items.ToDictionary(item => item.ClientItemId, StringComparer.Ordinal);
                var rows = draft.OriginalRequest.Items.ToDictionary(item => item.ClientItemId, ToEditable, StringComparer.Ordinal);
                foreach (var coverage in remote.Document.Coverage.Where(row => row.Kind == "child"))
                {
                    var projected = remote.Document.Parts.Single(part => part.Index == coverage.PartIndex).Request;
                    var item = projected.Items.Single(row => row.ClientItemId == coverage.ChildClientItemId);
                    if (item.Barcode != byRootId[coverage.ClientItemId].Barcode) throw new CatalogImportRecoveryException("recovery_rows_changed");
                    rows[coverage.ClientItemId] = ToEditable(item);
                }
                frozen = draft.OriginalRequest.Items.Select(item => rows[item.ClientItemId]).ToArray();
            }
            DemandRows(draft, frozen);
            var proofsUsed = entries.Where(entry => entry.SharedProof != null).Select(entry => entry.SharedProof).GroupBy(proof => proof.Hash).ToArray();
            if (proofsUsed.Length > 1) throw new CatalogImportRecoveryException("receipt_conflict");
            var shared = proofsUsed.SingleOrDefault()?.First();
            var prepared = new PreparedPlan { PlanId = plan.PlanId, TotalRows = plan.TotalRows, SharedProofJson = shared?.Json,
                SharedProofHash = shared?.Hash, Entries = entries.Select(entry => new PreparedEntry { ClientImportId = entry.ClientImportId,
                    IdempotencyKey = entry.IdempotencyKey, CreatedAt = entry.CreatedAt, OperationType = entry.OperationType,
                    SchemaVersion = entry.SchemaVersion, Source = entry.Source, PayloadJson = entry.PayloadJson,
                    PayloadHash = entry.PayloadHash, SharedProofHash = entry.SharedProof?.Hash }).ToArray() };
            var archive = new SupersessionArchive { OriginalId = draft.Original.Id, OriginalHash = draft.Original.PayloadHash,
                TargetId = draft.Original.Id, TargetHash = draft.Original.PayloadHash, PlanJson = Serialize(prepared),
                RowsJson = Serialize(frozen), AppliedRowsJson = Serialize(frozen), DocumentJson = Serialize(remote.Document), RemoteJson = source.RemoteJson, LocalAlreadyApplied = true };
            // Merge the operator's edited leaf into the saved full-root draft.
            // Frozen local intent and unapplied work remain separate records.
            var desired = SnapshotRows(frozen).ToDictionary(row => row.Barcode, StringComparer.Ordinal);
            foreach (var edited in childDraft.Rows)
                if (desired.ContainsKey(edited.Barcode)) desired[edited.Barcode] = SnapshotRows(new[] { edited })[0];
            draft.Rows = frozen.Select(row => desired[row.Barcode]).ToArray();
            await SaveDraftAsync(draft, draft.Rows, token).ConfigureAwait(false);
            var json = Serialize(archive); var settled = Serialize(new PosCatalogImportReceiptResponse[entries.Length]);
            using (var conn = _factory.Open()) using (var tx = conn.BeginTransaction())
            {
                await new CatalogImportRecoveryCommit(draft, generation, authorize).ValidateAsync(conn, tx, false).ConfigureAwait(false);
                await LinkPredecessorArchiveAsync(conn, tx, draft, remote.Document, source.LocalPlanId).ConfigureAwait(false);
                await conn.ExecuteAsync(@"INSERT OR IGNORE INTO catalog_import_recovery_supersession
(predecessor_plan_id,original_id,original_hash,archive_json,archive_hash,settlement_json,settlement_hash,created_at)
VALUES(@planId,@id,@originalHash,@json,@hash,@settled,@settledHash,@now)",
                    new { planId = remote.Document.PlanId, id = draft.Original.Id, originalHash = draft.Original.PayloadHash,
                        json, hash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json), settled,
                        settledHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(settled), now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
                if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE predecessor_plan_id=@planId AND archive_json=@json AND resolved_at IS NULL",
                    new { planId = remote.Document.PlanId, json }, tx).ConfigureAwait(false) != 1)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                token.ThrowIfCancellationRequested(); DemandPermission(authorize); tx.Commit();
            }
            return await RetirePreparedPlanAsync(draft, options, session, generation, authorize, token).ConfigureAwait(false);
        }

        // Explicit review action. Registration/retirement cannot apply products.
        // The immutable archive survives a crash between any two child replies.
        public async Task<CatalogImportRecoveryDraft> RetirePreparedPlanAsync(CatalogImportRecoveryDraft draft,
            PosAdminWebOptions options, PosTrustedDeviceSession session, OnlineSyncGeneration generation,
            Func<bool> authorize, CancellationToken token)
        {
            DemandPermission(authorize);
            if (draft == null || options == null || draft.Generation?.Fingerprint != generation?.Fingerprint)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            draft.TransportOptions = options;
            ReadFreshSession(draft, session, authorize);
            var archived = await ReadSupersessionRowAsync(draft.Original.Id, token).ConfigureAwait(false);
            PreparedSupersessionSource source;
            using (var conn = _factory.Open())
                source = await conn.QuerySingleOrDefaultAsync<PreparedSupersessionSource>(@"SELECT original_hash AS OriginalHash,
target_id AS TargetId,target_hash AS TargetHash,rows_json AS RowsJson,rows_hash AS RowsHash,
plan_json AS PlanJson,plan_hash AS PlanHash,plan_document_json AS DocumentJson,plan_document_hash AS DocumentHash,
remote_plan_json AS RemoteJson,remote_plan_hash AS RemoteHash,dispatch_started_at AS DispatchStarted
FROM catalog_import_prepared_plan WHERE original_id=@id", new { id = draft.Original.Id }).ConfigureAwait(false);
            if (archived != null && source?.DocumentJson != null &&
                Deserialize<PosCatalogImportRecoveryPlanDocument>(source.DocumentJson).PlanId != archived.PlanId)
            {
                ValidatePreparedSource(draft, source);
                var next = Deserialize<PosCatalogImportRecoveryPlanDocument>(source.DocumentJson);
                if (!archived.FinalizedAt.HasValue || next.Supersedes?.PlanId != archived.PlanId)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                archived = null; // Retire this generation, preserving its ancestor.
            }
            if (archived == null)
            {
                if (source == null) throw new CatalogImportRecoveryException("prepared_plan_required");
                ValidatePreparedSource(draft, source);
                if (!source.DispatchStarted.HasValue)
                {
                    // No request could have left this journal: discard only the
                    // unstarted preparation, retaining the operator's draft.
                    using (var conn = _factory.Open()) using (var tx = conn.BeginTransaction())
                    {
                        await new CatalogImportRecoveryCommit(draft, generation, authorize).ValidateAsync(conn, tx, false).ConfigureAwait(false);
                        if (await conn.ExecuteAsync("DELETE FROM catalog_import_prepared_plan WHERE original_id=@id AND plan_hash=@hash AND dispatch_started_at IS NULL",
                            new { id = draft.Original.Id, hash = source.PlanHash }, tx).ConfigureAwait(false) != 1)
                            throw new CatalogImportRecoveryException("recovery_state_changed");
                        token.ThrowIfCancellationRequested(); DemandPermission(authorize); tx.Commit();
                    }
                    return await PrepareAsync(draft.Original.Id, options, session, generation, token).ConfigureAwait(false);
                }
                if (source.DocumentJson == null) throw new CatalogImportRecoveryException("payload_hash_mismatch");
                var plan = RestoreArchivedPlan(source.PlanJson);
                plan.PreparedDocumentJson = source.DocumentJson;
                plan.RemotePlanJson = source.RemoteJson;
                if (plan.RemotePlanJson == null)
                {
                    // Lost registration response: replay exactly the saved
                    // document and upload IDs, never rebuild from fresh rows.
                    await PrepareRemotePlanAsync(draft, null, plan, authorize, token).ConfigureAwait(false);
                    await SavePreparedRemoteReceiptAsync(draft, plan, authorize, token).ConfigureAwait(false);
                    source.RemoteJson = plan.RemotePlanJson;
                }
                var archive = new SupersessionArchive { OriginalId = draft.Original.Id, OriginalHash = draft.Original.PayloadHash,
                    TargetId = source.TargetId, TargetHash = source.TargetHash, PlanJson = source.PlanJson,
                    RowsJson = source.RowsJson,
                    AppliedRowsJson = Serialize((draft.InitialIntentRequest ?? draft.OriginalRequest).Items.Select(ToEditable).ToArray()),
                    DocumentJson = source.DocumentJson, RemoteJson = source.RemoteJson };
                var saved = Deserialize<CatalogImportSavedRemotePlan>(archive.RemoteJson);
                ValidateRemotePlan(plan, saved.Document, saved.Receipt, ReadFreshSession(draft, session, authorize));
                if (Serialize(saved.Document) != Serialize(Deserialize<PosCatalogImportRecoveryPlanDocument>(archive.DocumentJson)))
                    throw new CatalogImportRecoveryException("payload_hash_mismatch");
                var json = Serialize(archive);
                var settled = Serialize(new PosCatalogImportReceiptResponse[plan.Entries.Count]);
                using (var conn = _factory.Open()) using (var tx = conn.BeginTransaction())
                {
                    await new CatalogImportRecoveryCommit(draft, generation, authorize).ValidateAsync(conn, tx, false).ConfigureAwait(false);
                    await LinkPredecessorArchiveAsync(conn, tx, draft, saved.Document, plan.PlanId).ConfigureAwait(false);
                    await conn.ExecuteAsync(@"INSERT OR IGNORE INTO catalog_import_recovery_supersession
(predecessor_plan_id,original_id,original_hash,archive_json,archive_hash,settlement_json,settlement_hash,created_at)
VALUES(@planId,@id,@originalHash,@json,@hash,@settled,@settledHash,@now)",
                        new { planId = saved.Document.PlanId, id = draft.Original.Id, originalHash = draft.Original.PayloadHash,
                            json, hash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json), settled,
                            settledHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(settled), now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
                    if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE predecessor_plan_id=@planId AND archive_json=@json AND resolved_at IS NULL",
                        new { planId = saved.Document.PlanId, json }, tx).ConfigureAwait(false) != 1)
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    token.ThrowIfCancellationRequested(); DemandPermission(authorize); tx.Commit();
                }
                archived = await ReadSupersessionRowAsync(draft.Original.Id, token).ConfigureAwait(false);
            }
            var state = ParseSupersession(draft, archived, session);
            using (var client = new PosAdminWebClient(options))
            {
                for (var index = 0; index < state.PartReceipts.Length; index++)
                {
                    token.ThrowIfCancellationRequested(); ReadFreshSession(draft, session, authorize);
                    if (state.PartReceipts[index] != null) continue;
                    var entry = state.Entries[index];
                    var child = OutboxForArchivedEntry(draft, entry);
                    var saved = new CatalogImportSavedRemotePlan { Document = state.SavedPlan.Document,
                        Receipt = state.SavedPlan.Receipt, PartIndex = index };
                    var response = await CatalogImportRecoveryProofTransport.QueryPlannedReceiptAsync(client, saved, child, true,
                        () => ReadFreshSession(draft, session, authorize), token).ConfigureAwait(false);
                    if (!response.Success) throw new CatalogImportRecoveryException(response.Denied ? "authentication_required" : "receipt_retirement_unavailable");
                    if (response.Value.Status != "accepted" && response.Value.Status != "retired")
                        throw new CatalogImportRecoveryException("receipt_retirement_unavailable");
                    state.PartReceipts[index] = response.Value;
                    var settled = Serialize(state.PartReceipts);
                    using (var conn = _factory.Open()) using (var tx = conn.BeginTransaction())
                    {
                        await new CatalogImportRecoveryCommit(draft, generation, authorize).ValidateAsync(conn, tx, false).ConfigureAwait(false);
                        if (response.Value.Status == "retired")
                            await FenceRetiredEntryAsync(conn, tx, draft, entry).ConfigureAwait(false);
                        if (await conn.ExecuteAsync(@"UPDATE catalog_import_recovery_supersession SET settlement_json=@json,settlement_hash=@hash
WHERE predecessor_plan_id=@planId AND settlement_hash=@before AND resolved_at IS NULL",
                            new { json = settled, hash = CatalogImportOutboxPayloadBuilder.Sha256Hex(settled), planId = state.PlanId,
                                before = archived.SettlementHash }, tx).ConfigureAwait(false) != 1)
                            throw new CatalogImportRecoveryException("recovery_state_changed");
                        token.ThrowIfCancellationRequested(); DemandPermission(authorize); tx.Commit();
                    }
                    archived.SettlementJson = settled; archived.SettlementHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(settled);
                }
            }
            // Delete the active preparation only after every fence/ACK is durable
            // in its immutable archive. The archive itself remains a shop barrier.
            ParseSupersession(draft, archived, session);
            using (var conn = _factory.Open()) using (var tx = conn.BeginTransaction())
            {
                await new CatalogImportRecoveryCommit(draft, generation, authorize).ValidateAsync(conn, tx, false).ConfigureAwait(false);
                await conn.ExecuteAsync(@"UPDATE catalog_import_recovery_supersession SET finalized_at=COALESCE(finalized_at,@now)
WHERE predecessor_plan_id=@planId AND settlement_hash=@hash AND resolved_at IS NULL;
DELETE FROM catalog_import_prepared_plan WHERE original_id=@id AND plan_document_json=@document;",
                    new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), planId = state.PlanId, hash = archived.SettlementHash,
                        id = draft.Original.Id, document = Deserialize<SupersessionArchive>(archived.ArchiveJson).DocumentJson }, tx).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); DemandPermission(authorize); tx.Commit();
            }
            return await PrepareAsync(draft.Original.Id, options, session, generation, token).ConfigureAwait(false);
        }

        private async Task<SupersessionRow> ReadSupersessionRowAsync(long originalId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var conn = _factory.Open())
            {
                var headers = (await conn.QueryAsync<SupersessionHeader>(@"SELECT predecessor_plan_id AS PlanId,
successor_plan_id AS SuccessorPlanId,json_extract(json_extract(archive_json,'$.PlanJson'),'$.PlanId') AS LocalPlanId
FROM catalog_import_recovery_supersession WHERE original_id=@originalId AND resolved_at IS NULL ORDER BY created_at,rowid",
                    new { originalId }).ConfigureAwait(false)).ToArray();
                if (headers.Length == 0) return null;
                if (headers.Length > 128 || headers.Any(header => string.IsNullOrWhiteSpace(header.LocalPlanId)))
                    throw new CatalogImportRecoveryException("receipt_conflict");
                for (var index = 1; index < headers.Length; index++)
                    if (headers[index - 1].SuccessorPlanId != headers[index].LocalPlanId)
                        throw new CatalogImportRecoveryException("receipt_conflict");
                return await conn.QuerySingleAsync<SupersessionRow>(@"SELECT predecessor_plan_id AS PlanId,original_hash AS OriginalHash,
archive_json AS ArchiveJson,archive_hash AS ArchiveHash,settlement_json AS SettlementJson,settlement_hash AS SettlementHash,
finalized_at AS FinalizedAt,successor_plan_id AS SuccessorPlanId FROM catalog_import_recovery_supersession WHERE predecessor_plan_id=@planId",
                    new { planId = headers[headers.Length - 1].PlanId }).ConfigureAwait(false);
            }
        }

        private static async Task LinkPredecessorArchiveAsync(SqliteConnection conn, SqliteTransaction tx,
            CatalogImportRecoveryDraft draft, PosCatalogImportRecoveryPlanDocument document, string localPlanId)
        {
            if (document.Supersedes == null) return;
            if (await conn.ExecuteAsync(@"UPDATE catalog_import_recovery_supersession SET successor_plan_id=@localPlanId
WHERE predecessor_plan_id=@predecessor AND original_id=@id AND original_hash=@hash AND finalized_at IS NOT NULL
AND resolved_at IS NULL AND (successor_plan_id IS NULL OR successor_plan_id=@localPlanId)",
                new { localPlanId, predecessor = document.Supersedes.PlanId, id = draft.Original.Id,
                    hash = draft.Original.PayloadHash }, tx).ConfigureAwait(false) != 1)
                throw new CatalogImportRecoveryException("receipt_conflict");
        }

        private async Task LoadSupersessionAsync(CatalogImportRecoveryDraft draft, CancellationToken token)
        {
            var saved = await ReadSupersessionRowAsync(draft.Original.Id, token).ConfigureAwait(false);
            if (saved == null) return;
            var state = ParseSupersession(draft, saved, draft.TransportSession);
            await LoadCarryAcceptedEntriesAsync(draft, state, token).ConfigureAwait(false);
            draft.Supersession = state;
            if (!saved.FinalizedAt.HasValue || state.PartReceipts.Any(receipt => receipt == null))
            {
                draft.HasPreparedPlan = true;
                draft.CanCommit = false;
                return;
            }
            if (state.AppliedRows != null)
            {
                var frozen = state.AppliedRows.ToDictionary(row => row.Barcode, StringComparer.Ordinal);
                var baseline = Deserialize<PosCatalogImportRequest>(Serialize(draft.OriginalRequest));
                foreach (var item in baseline.Items)
                {
                    var row = frozen[item.Barcode];
                    item.RetailPrice = row.HasRetailPriceSource ? row.RetailPrice : null;
                    item.PurchasePrice = row.HasPurchasePriceSource ? row.PurchasePrice : null;
                    item.Quantity = row.HasQuantitySource ? row.Quantity : null;
                    item.ProductName = row.HasProductNameSource ? row.ProductName : null;
                    item.SecondProductName = row.HasSecondProductNameSource ? row.SecondProductName : null;
                    item.ItemNumber = row.HasItemNumberSource ? row.ItemNumber : null;
                    item.Supplier = row.HasSupplierSource ? row.Supplier : null;
                    item.Category = row.HasCategorySource ? row.Category : null;
                }
                draft.InitialIntentRequest = baseline;
            }
        }

        private static async Task FenceRetiredEntryAsync(SqliteConnection conn, SqliteTransaction tx,
            CatalogImportRecoveryDraft draft, CatalogImportOutboxEntry entry)
        {
            var current = await conn.ExecuteScalarAsync<string>(@"SELECT status FROM catalog_import_outbox
WHERE client_import_id=@ClientImportId AND idempotency_key=@IdempotencyKey AND payload_hash=@PayloadHash AND payload_json=@PayloadJson
AND origin_shop_id=@shopId AND origin_shop_code=@shopCode", new { entry.ClientImportId, entry.IdempotencyKey,
                entry.PayloadHash, entry.PayloadJson, shopId = draft.Original.OriginShopId, shopCode = draft.Original.OriginShopCode }, tx).ConfigureAwait(false);
            if (current != null && current != "pending" && current != "retry" && current != "in_progress" &&
                current != "failed_blocked" && current != "recovered")
                throw new CatalogImportRecoveryException("receipt_conflict");
            await conn.ExecuteAsync(@"UPDATE catalog_import_outbox SET status='failed_blocked',claim_generation_id=NULL,claim_token=NULL,
last_error_code='recovery_plan_retired',last_error_at=@now,updated_at=@now
WHERE client_import_id=@ClientImportId AND idempotency_key=@IdempotencyKey AND payload_hash=@PayloadHash AND payload_json=@PayloadJson
AND origin_shop_id=@shopId AND origin_shop_code=@shopCode AND status IN ('pending','retry','in_progress','failed_blocked')",
                new { entry.ClientImportId, entry.IdempotencyKey, entry.PayloadHash, entry.PayloadJson,
                    shopId = draft.Original.OriginShopId, shopCode = draft.Original.OriginShopCode,
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
        }

        private async Task LoadCarryAcceptedEntriesAsync(CatalogImportRecoveryDraft draft,
            CatalogImportRecoverySupersession state, CancellationToken token)
        {
            var publish = state.AcceptedEntries.ToList();
            using (var conn = _factory.Open())
                foreach (var group in state.CarryCoverage.Where(row => row.Kind == "accepted_plan_part" &&
                    row.ContributorPlanId != null && row.ContributorPlanId != state.PlanId).GroupBy(row => row.ContributorPlanId, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var saved = await conn.QuerySingleOrDefaultAsync<SupersessionRow>(@"SELECT predecessor_plan_id AS PlanId,
original_hash AS OriginalHash,archive_json AS ArchiveJson,archive_hash AS ArchiveHash,settlement_json AS SettlementJson,
settlement_hash AS SettlementHash,finalized_at AS FinalizedAt,successor_plan_id AS SuccessorPlanId
FROM catalog_import_recovery_supersession WHERE predecessor_plan_id=@planId AND original_id=@id",
                        new { planId = group.Key, id = draft.Original.Id }).ConfigureAwait(false);
                    var ancestor = ParseSupersession(draft, saved, draft.TransportSession);
                    if (!saved.FinalizedAt.HasValue || !ancestor.IsSettled) throw new CatalogImportRecoveryException("receipt_required");
                    foreach (var partIndex in group.Select(row => row.ContributorPartIndex).Distinct())
                    {
                        var accepted = ancestor.AcceptedEntries.SingleOrDefault(row => row.PartIndex == partIndex);
                        if (accepted == null) throw new CatalogImportRecoveryException("receipt_required");
                        var entry = accepted.Entry;
                        var acked = await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_outbox
WHERE client_import_id=@ClientImportId AND idempotency_key=@IdempotencyKey AND payload_hash=@PayloadHash AND payload_json=@PayloadJson
AND origin_shop_id=@shopId AND origin_shop_code=@shopCode AND status='acked'",
                            new { entry.ClientImportId, entry.IdempotencyKey, entry.PayloadHash, entry.PayloadJson,
                                shopId = draft.Original.OriginShopId, shopCode = draft.Original.OriginShopCode }).ConfigureAwait(false);
                        if (acked == 0) publish.Add(accepted);
                    }
                }
            state.AcceptedEntriesToPublish = publish.GroupBy(row => row.Entry.ClientImportId + "\n" + row.Entry.IdempotencyKey, StringComparer.Ordinal)
                .Select(group => group.Select(row => row.Entry.PayloadHash + "\n" + row.Entry.PayloadJson).Distinct(StringComparer.Ordinal).Count() == 1 ?
                    group.First() : throw new CatalogImportRecoveryException("receipt_conflict")).ToArray();
        }

        private static SupplierImportEditableRow[] GetSupersessionActiveRows(CatalogImportRecoveryDraft draft,
            IReadOnlyList<SupplierImportEditableRow> desired)
        {
            var result = SnapshotRows(desired);
            if (draft.Supersession == null) return result;
            var carried = new HashSet<string>(draft.Supersession.CarryCoverage.Select(row => row.ClientItemId), StringComparer.Ordinal);
            var original = draft.OriginalRequest.Items.ToDictionary(row => row.Barcode, StringComparer.Ordinal);
            var frozen = draft.Supersession.FrozenRows.ToDictionary(row => row.Barcode, StringComparer.Ordinal);
            for (var index = 0; index < result.Length; index++)
                if (carried.Contains(original[result[index].Barcode].ClientItemId))
                    result[index] = SnapshotRows(new[] { frozen[result[index].Barcode] })[0];
            draft.Supersession.DeferredRows = result.Where((row, index) => !SameEditableIntent(row, desired[index])).Any();
            draft.Supersession.DeferredDesiredRows = SnapshotRows(desired);
            return result;
        }

        internal static async Task SaveDeferredSupersessionDraftsAsync(SqliteConnection conn, SqliteTransaction tx,
            CatalogImportRecoveryDraft draft)
        {
            var state = draft.Supersession;
            if (state?.DeferredRows != true || state.SavedPlan.Document.Mode != "replacement") return;
            var desired = state.DeferredDesiredRows.ToDictionary(row => row.Barcode, StringComparer.Ordinal);
            var frozen = state.FrozenRows.ToDictionary(row => row.Barcode, StringComparer.Ordinal);
            var original = draft.OriginalRequest.Items.ToDictionary(row => row.ClientItemId, StringComparer.Ordinal);
            var ancestors = new Dictionary<string, CatalogImportRecoverySupersession>(StringComparer.Ordinal)
                { [state.PlanId] = state };
            var owners = new Dictionary<long, DeferredDraftOwner>();
            foreach (var coverage in state.CarryCoverage)
            {
                var barcode = original[coverage.ClientItemId].Barcode;
                if (SameEditableIntent(frozen[barcode], desired[barcode])) continue;
                CatalogImportRecoveryOriginal owner;
                PosCatalogImportRequest intent;
                PosCatalogImportReceiptResponse receipt;
                if (coverage.Kind == "accepted_plan_part")
                {
                    var planId = coverage.ContributorPlanId ?? state.PlanId;
                    if (!ancestors.TryGetValue(planId, out var ancestor))
                    {
                        var saved = await conn.QuerySingleOrDefaultAsync<SupersessionRow>(@"SELECT predecessor_plan_id AS PlanId,
original_hash AS OriginalHash,archive_json AS ArchiveJson,archive_hash AS ArchiveHash,settlement_json AS SettlementJson,
settlement_hash AS SettlementHash,finalized_at AS FinalizedAt,successor_plan_id AS SuccessorPlanId
FROM catalog_import_recovery_supersession WHERE predecessor_plan_id=@planId AND original_id=@id",
                            new { planId, id = draft.Original.Id }, tx).ConfigureAwait(false);
                        ancestor = ParseSupersession(draft, saved, draft.TransportSession);
                        if (!ancestor.IsSettled) throw new CatalogImportRecoveryException("receipt_required");
                        ancestors.Add(planId, ancestor);
                    }
                    var accepted = ancestor.AcceptedEntries.SingleOrDefault(part => part.PartIndex == coverage.ContributorPartIndex);
                    if (accepted == null || accepted.Entry.OperationType != "catalog_import")
                        throw new CatalogImportRecoveryException("receipt_required");
                    intent = accepted.Intent; receipt = accepted.Receipt;
                    owner = await conn.QuerySingleOrDefaultAsync<CatalogImportRecoveryOriginal>(SelectOriginal + @" WHERE
o.client_import_id=@ClientImportId AND o.idempotency_key=@IdempotencyKey AND o.payload_hash=@PayloadHash AND o.payload_json=@PayloadJson",
                        new { accepted.Entry.ClientImportId, accepted.Entry.IdempotencyKey, accepted.Entry.PayloadHash,
                            accepted.Entry.PayloadJson }, tx).ConfigureAwait(false);
                }
                else if (coverage.Kind == "accepted_contributor")
                {
                    owner = null; intent = null; receipt = null;
                    var candidates = await conn.QueryAsync<CatalogImportRecoveryOriginal>(SelectOriginal + @" JOIN
catalog_import_recovery_contributions c ON c.contributor_id=o.id WHERE c.original_id=@id AND o.operation_type='catalog_import'",
                        new { id = draft.Original.Id }, tx).ConfigureAwait(false);
                    foreach (var candidate in candidates)
                    {
                        if (CatalogImportRecoveryProofTransport.CreateUploadId(draft.TransportSession.ShopId,
                            draft.TransportSession.ShopDeviceId, "sha256:" + candidate.PayloadHash, "original") != coverage.VerifiedContributorId) continue;
                        if (owner != null) throw new CatalogImportRecoveryException("receipt_conflict");
                        owner = candidate; intent = ReadOriginal(candidate);
                        var json = await conn.ExecuteScalarAsync<string>("SELECT receipt_json FROM catalog_import_recovery_contributions WHERE original_id=@id AND contributor_id=@contributorId AND payload_hash=@hash",
                            new { id = draft.Original.Id, contributorId = candidate.Id, hash = candidate.PayloadHash }, tx).ConfigureAwait(false);
                        receipt = Deserialize<PosCatalogImportReceiptResponse>(json);
                    }
                }
                else throw new CatalogImportRecoveryException("receipt_conflict");
                if (owner == null || owner.Status != "acked" || owner.OriginShopId != draft.Original.OriginShopId ||
                    owner.OriginShopCode != draft.Original.OriginShopCode || receipt?.Status != "accepted" ||
                    intent.Items.SingleOrDefault(item => item.ClientItemId == coverage.ContributorClientItemId)?.Barcode != barcode)
                    throw new CatalogImportRecoveryException("receipt_required");
                ValidateReceipt(new CatalogImportRecoveryDraft { Original = owner, OriginalRequest = intent, ShopDeviceId = draft.ShopDeviceId }, receipt);
                if (!owners.TryGetValue(owner.Id, out var work))
                {
                    var prior = await conn.QuerySingleOrDefaultAsync<SavedOperatorDraft>("SELECT payload_hash AS PayloadHash,rows_json AS RowsJson,rows_hash AS RowsHash FROM catalog_import_recovery_draft WHERE original_id=@id",
                        new { id = owner.Id }, tx).ConfigureAwait(false);
                    if (prior != null && (prior.PayloadHash != owner.PayloadHash || CatalogImportOutboxPayloadBuilder.Sha256Hex(prior.RowsJson) != prior.RowsHash))
                        throw new CatalogImportRecoveryException("draft_hash_mismatch");
                    var rows = prior == null ? intent.Items.Select(ToEditable).ToArray() : Deserialize<SavedDraftContent>(prior.RowsJson).Rows;
                    DemandRows(new CatalogImportRecoveryDraft { OriginalRequest = intent }, rows);
                    work = new DeferredDraftOwner { Owner = owner, Rows = rows.ToDictionary(row => row.Barcode, StringComparer.Ordinal) };
                    owners.Add(owner.Id, work);
                }
                var copy = SnapshotRows(new[] { desired[barcode] })[0];
                copy.RowNumber = work.Rows[barcode].RowNumber;
                work.Rows[barcode] = copy;
            }
            foreach (var work in owners.Values)
            {
                var rows = work.Rows.Values.OrderBy(row => row.RowNumber).ToArray();
                var json = Serialize(new SavedDraftContent { Rows = rows, OperationCreatedAtUtc = draft.OperationCreatedAtUtc });
                // This is unapplied operator work, rooted at its actual accepted
                // child. A retired legacy root cannot authorize a correction.
                await conn.ExecuteAsync(@"INSERT INTO catalog_import_recovery_draft(original_id,payload_hash,rows_json,rows_hash,
origin_shop_id,origin_shop_code,generation_fingerprint,transition_epoch,revision_fingerprint,updated_at)
VALUES(@id,@hash,@json,@rowsHash,@shopId,@shopCode,@generation,@epoch,@revision,@now)
ON CONFLICT(original_id) DO UPDATE SET rows_json=excluded.rows_json,rows_hash=excluded.rows_hash,
generation_fingerprint=excluded.generation_fingerprint,transition_epoch=excluded.transition_epoch,
revision_fingerprint=excluded.revision_fingerprint,updated_at=excluded.updated_at WHERE payload_hash=excluded.payload_hash;",
                    new { id = work.Owner.Id, hash = work.Owner.PayloadHash, json, rowsHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json),
                        shopId = draft.Original.OriginShopId, shopCode = draft.Original.OriginShopCode,
                        generation = draft.Generation?.Fingerprint, epoch = draft.TransitionEpoch,
                        revision = draft.RevisionFingerprint, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
                if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_recovery_draft WHERE original_id=@id AND rows_json=@json",
                    new { id = work.Owner.Id, json }, tx).ConfigureAwait(false) != 1)
                    throw new CatalogImportRecoveryException("draft_hash_mismatch");
            }
        }

        private sealed class DeferredDraftOwner
        {
            public CatalogImportRecoveryOriginal Owner { get; set; }
            public Dictionary<string, SupplierImportEditableRow> Rows { get; set; }
        }

        private static bool SameEditableIntent(SupplierImportEditableRow first, SupplierImportEditableRow second) =>
            first.Barcode == second.Barcode && first.ProductName == second.ProductName && first.SecondProductName == second.SecondProductName &&
            first.ItemNumber == second.ItemNumber && first.PurchasePrice == second.PurchasePrice && first.RetailPrice == second.RetailPrice &&
            first.Quantity == second.Quantity && first.Supplier == second.Supplier && first.Category == second.Category &&
            first.IsSkipped == second.IsSkipped && first.HasItemNumberSource == second.HasItemNumberSource &&
            first.HasProductNameSource == second.HasProductNameSource && first.HasSecondProductNameSource == second.HasSecondProductNameSource &&
            first.HasPurchasePriceSource == second.HasPurchasePriceSource && first.HasRetailPriceSource == second.HasRetailPriceSource &&
            first.HasQuantitySource == second.HasQuantitySource && first.HasSupplierSource == second.HasSupplierSource && first.HasCategorySource == second.HasCategorySource;

        private static void ApplySupersessionToPlan(CatalogImportRecoveryDraft draft, PosCatalogImportRecoveryPlanDocument document)
        {
            var state = draft.Supersession;
            if (state == null) return;
            if (state.PartReceipts.Any(receipt => receipt == null) || document.VerifiedOriginalId != state.SavedPlan.Document.VerifiedOriginalId ||
                document.Mode != state.SavedPlan.Document.Mode || document.PlanId == state.PlanId)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var carried = state.CarryCoverage.ToDictionary(row => row.ClientItemId, StringComparer.Ordinal);
            foreach (var row in document.Coverage)
                if (carried.ContainsKey(row.ClientItemId) && row.Kind == "child")
                    throw new CatalogImportRecoveryException("prepared_plan_carry_required");
            document.Coverage = document.Coverage.Select(row => carried.TryGetValue(row.ClientItemId, out var carry) ?
                Deserialize<PosCatalogImportRecoveryCoverage>(Serialize(carry)) : row).ToArray();
            if (carried.Keys.Except(document.Coverage.Select(row => row.ClientItemId), StringComparer.Ordinal).Any())
                throw new CatalogImportRecoveryException("receipt_incomplete");
            document.Supersedes = new PosCatalogImportRecoverySupersedes { PlanId = state.PlanId,
                RetiredChildren = state.PartReceipts.Select((receipt, index) => new { receipt, index })
                    .Where(row => row.receipt.Status == "retired").Select(row => new PosCatalogImportRecoveryRetiredChild
                    { PartIndex = row.index, CanonicalPayloadHash = state.SavedPlan.Receipt.Parts.Single(part => part.Index == row.index).PayloadHash }).ToArray() };
        }

        internal static async Task BindSupersessionAsync(SqliteConnection conn, SqliteTransaction tx,
            CatalogImportRecoveryDraft draft, string localPlanId)
        {
            if (draft.Supersession == null) return;
            if (string.IsNullOrWhiteSpace(localPlanId) || await conn.ExecuteAsync(@"UPDATE catalog_import_recovery_supersession SET successor_plan_id=@localPlanId
WHERE predecessor_plan_id=@planId AND original_id=@id AND original_hash=@hash AND finalized_at IS NOT NULL AND resolved_at IS NULL
AND (successor_plan_id IS NULL OR successor_plan_id=@localPlanId)",
                new { localPlanId, planId = draft.Supersession.PlanId, id = draft.Original.Id, hash = draft.Original.PayloadHash }, tx).ConfigureAwait(false) != 1)
                throw new CatalogImportRecoveryException("recovery_state_changed");
        }

        internal static async Task CompleteSupersessionAsync(SqliteConnection conn, SqliteTransaction tx, string localPlanId, long now)
        {
            var head = await conn.QuerySingleOrDefaultAsync<SupersessionRow>(@"SELECT predecessor_plan_id AS PlanId,original_hash AS OriginalHash,
archive_json AS ArchiveJson,archive_hash AS ArchiveHash,settlement_json AS SettlementJson,settlement_hash AS SettlementHash,
finalized_at AS FinalizedAt,successor_plan_id AS SuccessorPlanId FROM catalog_import_recovery_supersession
WHERE successor_plan_id=@localPlanId AND resolved_at IS NULL", new { localPlanId }, tx).ConfigureAwait(false);
            if (head == null) return;
            var archive = Deserialize<SupersessionArchive>(head.ArchiveJson);
            var root = await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(SelectOriginal + " WHERE o.id=@id",
                new { id = archive.OriginalId }, tx).ConfigureAwait(false);
            var remote = Deserialize<CatalogImportSavedRemotePlan>(archive.RemoteJson);
            var state = ParseSupersession(new CatalogImportRecoveryDraft { Original = root, OriginalRequest = ReadOriginal(root) }, head,
                new PosTrustedDeviceSession { ShopId = remote.Receipt.ShopId, ShopDeviceId = remote.Receipt.ShopDeviceId,
                    ShopCode = root.OriginShopCode, DeviceToken = "local-proof-validation", SessionToken = "local-proof-validation",
                    PosSessionId = "10000000-0000-4000-8000-000000000001" });
            if (!head.FinalizedAt.HasValue || !state.IsSettled) throw new CatalogImportRecoveryException("receipt_required");
            var hasPlan = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_plan WHERE plan_id=@localPlanId",
                new { localPlanId }, tx).ConfigureAwait(false) == 1;
            if (hasPlan)
            {
                if (await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_plan_part m JOIN catalog_import_outbox o ON o.id=m.outbox_id
WHERE m.plan_id=@localPlanId AND (o.status NOT IN ('acked','recovered') OR m.ack_json IS NULL OR o.payload_hash<>m.payload_hash)",
                    new { localPlanId }, tx).ConfigureAwait(false) != 0)
                    throw new CatalogImportRecoveryException("receipt_required");
            }
            else if (localPlanId != "settled-supersession-" + state.PlanId)
                await ValidateConvergenceCompletionAsync(conn, tx, root, state, localPlanId).ConfigureAwait(false);
            else if (state.PartReceipts.Any(receipt => receipt.Status != "accepted"))
                throw new CatalogImportRecoveryException("receipt_required");
            // Resolve the complete linear ancestry. Retired operations are
            // recovered by the successor; they never receive a fabricated ACK.
            for (var depth = 0; head != null && depth < 128; depth++)
            {
                archive = Deserialize<SupersessionArchive>(head.ArchiveJson);
                if (CatalogImportOutboxPayloadBuilder.Sha256Hex(head.ArchiveJson) != head.ArchiveHash ||
                    CatalogImportOutboxPayloadBuilder.Sha256Hex(head.SettlementJson) != head.SettlementHash)
                    throw new CatalogImportRecoveryException("payload_hash_mismatch");
                var entries = RestoreArchivedPlan(archive.PlanJson).Entries;
                var receipts = Deserialize<PosCatalogImportReceiptResponse[]>(head.SettlementJson);
                if (!head.FinalizedAt.HasValue || receipts.Length != entries.Count || receipts.Any(receipt => receipt == null))
                    throw new CatalogImportRecoveryException("receipt_required");
                for (var index = 0; index < receipts.Length; index++)
                    if (receipts[index].Status == "retired")
                    {
                        var entry = entries[index];
                        var current = await conn.ExecuteScalarAsync<string>(@"SELECT status FROM catalog_import_outbox
WHERE client_import_id=@ClientImportId AND idempotency_key=@IdempotencyKey AND payload_hash=@PayloadHash AND payload_json=@PayloadJson
AND origin_shop_id=@shopId AND origin_shop_code=@shopCode", new { entry.ClientImportId, entry.IdempotencyKey,
                            entry.PayloadHash, entry.PayloadJson, shopId = root.OriginShopId, shopCode = root.OriginShopCode }, tx).ConfigureAwait(false);
                        if (current != null && current != "pending" && current != "retry" && current != "in_progress" &&
                            current != "failed_blocked" && current != "recovered")
                            throw new CatalogImportRecoveryException("receipt_conflict");
                        await conn.ExecuteAsync(@"UPDATE catalog_import_outbox SET status='recovered',updated_at=@now,
claim_generation_id=NULL,claim_token=NULL,last_error_code=NULL,last_error_at=NULL
WHERE client_import_id=@ClientImportId AND idempotency_key=@IdempotencyKey AND payload_hash=@PayloadHash AND payload_json=@PayloadJson
AND origin_shop_id=@shopId AND origin_shop_code=@shopCode AND status IN ('pending','retry','in_progress','failed_blocked','recovered');
UPDATE catalog_import_recovery SET resolved_at=COALESCE(resolved_at,@now),updated_at=@now WHERE original_id IN
(SELECT id FROM catalog_import_outbox WHERE client_import_id=@ClientImportId AND idempotency_key=@IdempotencyKey
AND payload_hash=@PayloadHash AND payload_json=@PayloadJson AND status='recovered' AND origin_shop_id=@shopId AND origin_shop_code=@shopCode)",
                            new { now, entry.ClientImportId, entry.IdempotencyKey, entry.PayloadHash, entry.PayloadJson,
                                shopId = root.OriginShopId, shopCode = root.OriginShopCode }, tx).ConfigureAwait(false);
                    }
                await conn.ExecuteAsync("UPDATE catalog_import_recovery_supersession SET resolved_at=COALESCE(resolved_at,@now) WHERE predecessor_plan_id=@planId",
                    new { now, planId = head.PlanId }, tx).ConfigureAwait(false);
                var previousLocalId = Deserialize<PreparedPlan>(archive.PlanJson).PlanId;
                head = await conn.QuerySingleOrDefaultAsync<SupersessionRow>(@"SELECT predecessor_plan_id AS PlanId,original_hash AS OriginalHash,
archive_json AS ArchiveJson,archive_hash AS ArchiveHash,settlement_json AS SettlementJson,settlement_hash AS SettlementHash,
finalized_at AS FinalizedAt,successor_plan_id AS SuccessorPlanId FROM catalog_import_recovery_supersession
WHERE successor_plan_id=@previousLocalId AND original_id=@id AND resolved_at IS NULL",
                    new { previousLocalId, id = root.Id }, tx).ConfigureAwait(false);
            }
            if (head != null) throw new CatalogImportRecoveryException("receipt_conflict");
        }

        private static CatalogImportRecoverySupersession ParseSupersession(CatalogImportRecoveryDraft draft, SupersessionRow saved,
            PosTrustedDeviceSession session)
        {
            if (saved == null || saved.OriginalHash != draft.Original.PayloadHash ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.ArchiveJson) != saved.ArchiveHash ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.SettlementJson) != saved.SettlementHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var archive = Deserialize<SupersessionArchive>(saved.ArchiveJson);
            if (archive.OriginalId != draft.Original.Id || archive.OriginalHash != draft.Original.PayloadHash)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            var plan = RestoreArchivedPlan(archive.PlanJson);
            var remote = Deserialize<CatalogImportSavedRemotePlan>(archive.RemoteJson);
            if (remote.Document.PlanId != saved.PlanId || Serialize(remote.Document) != Serialize(Deserialize<PosCatalogImportRecoveryPlanDocument>(archive.DocumentJson)))
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            ValidateRemotePlan(plan, remote.Document, remote.Receipt, session);
            var receipts = Deserialize<PosCatalogImportReceiptResponse[]>(saved.SettlementJson);
            if (receipts.Length != plan.Entries.Count) throw new CatalogImportRecoveryException("receipt_incomplete");
            var accepted = new List<CatalogImportSupersessionAcceptedEntry>();
            var carry = remote.Document.Coverage.Where(row => row.Kind == "accepted_plan_part" || row.Kind == "accepted_contributor")
                .Select(row => Deserialize<PosCatalogImportRecoveryCoverage>(Serialize(row))).ToList();
            for (var index = 0; index < receipts.Length; index++)
            {
                var receipt = receipts[index]; if (receipt == null) continue;
                var entry = plan.Entries[index];
                var part = remote.Receipt.Parts.Single(row => row.Index == index);
                if (!receipt.Ok || receipt.ShopId != session.ShopId || receipt.ShopDeviceId != session.ShopDeviceId ||
                    receipt.OriginalSchemaVersion != entry.SchemaVersion || receipt.ClientImportId != entry.ClientImportId ||
                    receipt.IdempotencyKey != entry.IdempotencyKey || receipt.PayloadHash != entry.PayloadHash ||
                    receipt.CanonicalPayloadHash != part.PayloadHash || receipt.Status != "accepted" && receipt.Status != "retired")
                    throw new CatalogImportRecoveryException("receipt_conflict");
                if (receipt.Status == "retired")
                {
                    if (!receipt.OldIdentityBlocked || string.IsNullOrWhiteSpace(receipt.RetiredAt) || receipt.Receipt != null)
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    continue;
                }
                PosCatalogImportRequest intent; CatalogImportAckResult ack;
                if (entry.OperationType == "catalog_import_correction")
                {
                    var child = OutboxForArchivedEntry(draft, entry);
                    var transport = CatalogImportCorrectionTransport.BuildTransportRequest(child, session);
                    intent = CatalogImportCorrectionTransport.ReadAckIntendedRequest(entry.PayloadJson, entry.SharedProof);
                    ack = CatalogImportCorrectionTransport.ValidateResponse(child, transport, new PosCatalogImportCorrectionResponse
                    { Ok = true, Code = "success", SchemaVersion = entry.SchemaVersion, Status = "accepted", ShopId = receipt.ShopId,
                        ShopDeviceId = receipt.ShopDeviceId, ClientImportId = entry.ClientImportId, IdempotencyKey = entry.IdempotencyKey,
                        PayloadHash = entry.PayloadHash, CanonicalPayloadHash = receipt.CanonicalPayloadHash, Receipt = receipt.Receipt });
                }
                else
                {
                    intent = Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
                    ack = BuildPersistedAck(new CatalogImportRecoveryOriginal { ClientImportId = entry.ClientImportId,
                        IdempotencyKey = entry.IdempotencyKey, PayloadHash = entry.PayloadHash }, intent, receipt.Receipt);
                    CatalogImportOutboxRepository.EnsureRecoveryAckComplete(intent, ack, true);
                }
                accepted.Add(new CatalogImportSupersessionAcceptedEntry { Entry = entry, Receipt = receipt, Intent = intent, Ack = ack, PartIndex = index });
                foreach (var coverage in remote.Document.Coverage.Where(row => row.Kind == "child" && row.PartIndex == index))
                    carry.Add(new PosCatalogImportRecoveryCoverage { ClientItemId = coverage.ClientItemId, Kind = "accepted_plan_part",
                        ContributorPartIndex = index, ContributorClientItemId = coverage.ChildClientItemId, ContributorPlanId = saved.PlanId });
            }
            if (carry.Select(row => row.ClientItemId).Distinct(StringComparer.Ordinal).Count() != carry.Count)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var frozenRows = Deserialize<SupplierImportEditableRow[]>(archive.RowsJson); DemandRows(draft, frozenRows);
            var appliedRows = archive.AppliedRowsJson == null ? (archive.LocalAlreadyApplied ? frozenRows : null) :
                Deserialize<SupplierImportEditableRow[]>(archive.AppliedRowsJson);
            if (appliedRows != null) DemandRows(draft, appliedRows);
            return new CatalogImportRecoverySupersession { PlanId = saved.PlanId, SavedPlan = remote, PartReceipts = receipts,
                AcceptedEntries = accepted, AcceptedEntriesToPublish = accepted, FrozenRows = frozenRows, CarryCoverage = carry, Entries = plan.Entries,
                AppliedRows = appliedRows, LocalAlreadyApplied = archive.LocalAlreadyApplied };
        }

        private static CatalogImportOutboxPlan RestoreArchivedPlan(string json)
        {
            var record = Deserialize<PreparedPlan>(json);
            var proof = record.SharedProofJson == null ? null : CatalogImportCorrectionSharedProof.Parse(record.SharedProofJson, record.SharedProofHash);
            var entries = record.Entries.Select(entry => new CatalogImportOutboxEntry { ClientImportId = entry.ClientImportId,
                IdempotencyKey = entry.IdempotencyKey, CreatedAt = entry.CreatedAt, OperationType = entry.OperationType,
                SchemaVersion = entry.SchemaVersion, Source = entry.Source, PayloadJson = entry.PayloadJson, PayloadHash = entry.PayloadHash,
                SharedProof = entry.SharedProofHash == null ? null : proof?.Hash == entry.SharedProofHash ? proof : throw new CatalogImportRecoveryException("receipt_required") }).ToArray();
            if (entries.Any(entry => CatalogImportOutboxPayloadBuilder.Sha256Hex(entry.PayloadJson) != entry.PayloadHash))
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var plan = entries.Length == 0 ? EmptyConvergencePlan(record.PlanId) : CatalogImportPlanBuilder.Plan(entries);
            if (plan.PlanId != record.PlanId || plan.TotalRows != record.TotalRows) throw new CatalogImportRecoveryException("payload_hash_mismatch");
            return plan;
        }

        private static CatalogImportOutboxItem OutboxForArchivedEntry(CatalogImportRecoveryDraft draft, CatalogImportOutboxEntry entry) =>
            new CatalogImportOutboxItem { ClientImportId = entry.ClientImportId, IdempotencyKey = entry.IdempotencyKey,
                OperationType = entry.OperationType, SchemaVersion = entry.SchemaVersion, Source = entry.Source,
                PayloadJson = entry.PayloadJson, PayloadHash = entry.PayloadHash, SharedProof = entry.SharedProof,
                OriginShopId = draft.Original.OriginShopId, OriginShopCode = draft.Original.OriginShopCode };

        private static void ValidatePreparedSource(CatalogImportRecoveryDraft draft, PreparedSupersessionSource source)
        {
            if (source.OriginalHash != draft.Original.PayloadHash || source.TargetId != (draft.TargetOriginal ?? draft.Original).Id ||
                source.TargetHash != (draft.TargetOriginal ?? draft.Original).PayloadHash ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(source.RowsJson) != source.RowsHash ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(source.PlanJson) != source.PlanHash ||
                source.DocumentJson != null && CatalogImportOutboxPayloadBuilder.Sha256Hex(source.DocumentJson) != source.DocumentHash ||
                source.RemoteJson != null && CatalogImportOutboxPayloadBuilder.Sha256Hex(source.RemoteJson) != source.RemoteHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
        }

        private sealed class PreparedSupersessionSource
        {
            public string OriginalHash { get; set; } public long TargetId { get; set; } public string TargetHash { get; set; }
            public string RowsJson { get; set; } public string RowsHash { get; set; } public string PlanJson { get; set; }
            public string PlanHash { get; set; } public string DocumentJson { get; set; } public string DocumentHash { get; set; }
            public string RemoteJson { get; set; } public string RemoteHash { get; set; } public long? DispatchStarted { get; set; }
            public string CreatedAt { get; set; }
        }
        private sealed class CommittedSupersessionSource
        {
            public string LocalPlanId { get; set; } public long OriginalId { get; set; }
            public string RemoteJson { get; set; } public string RemoteHash { get; set; }
            public string RowsJson { get; set; } public string RowsHash { get; set; }
        }
        private sealed class SupersessionRow
        {
            public string PlanId { get; set; } public string OriginalHash { get; set; } public string ArchiveJson { get; set; }
            public string ArchiveHash { get; set; } public string SettlementJson { get; set; } public string SettlementHash { get; set; }
            public long? FinalizedAt { get; set; } public string SuccessorPlanId { get; set; }
        }
        private sealed class SupersessionHeader
        {
            public string PlanId { get; set; } public string LocalPlanId { get; set; } public string SuccessorPlanId { get; set; }
        }
        [DataContract] private sealed class SupersessionArchive
        {
            [DataMember] public long OriginalId { get; set; } [DataMember] public string OriginalHash { get; set; }
            [DataMember] public long TargetId { get; set; } [DataMember] public string TargetHash { get; set; }
            [DataMember] public string PlanJson { get; set; } [DataMember] public string RowsJson { get; set; }
            [DataMember(EmitDefaultValue=false)] public string AppliedRowsJson { get; set; }
            [DataMember] public string DocumentJson { get; set; } [DataMember] public string RemoteJson { get; set; }
            [DataMember] public bool LocalAlreadyApplied { get; set; }
        }
    }
}
