using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportRecoveryService
    {
        private static async Task LoadSettledBaselineAsync(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx, CatalogImportRecoveryDraft draft)
        {
            draft.InitialIntentRequest = Deserialize<PosCatalogImportRequest>(Serialize(draft.OriginalRequest));
            var rows = draft.InitialIntentRequest.Items.ToDictionary(row => row.ClientItemId, StringComparer.Ordinal);
            var corrections = await conn.QueryAsync<CatalogImportOutboxItem>(@"WITH RECURSIVE links(parent,child) AS (
SELECT original_id,replacement_id FROM catalog_import_recovery WHERE replacement_id IS NOT NULL
UNION SELECT p.original_id,m.outbox_id FROM catalog_import_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE p.original_id IS NOT NULL),
descendants(id,depth) AS (SELECT child,1 FROM links WHERE parent=@root UNION ALL
SELECT l.child,d.depth+1 FROM descendants d JOIN links l ON l.parent=d.id WHERE d.depth<128 AND l.child>d.id)
SELECT DISTINCT o.id AS Id,o.payload_json AS PayloadJson,o.payload_hash AS PayloadHash FROM descendants d JOIN catalog_import_outbox o ON o.id=d.id
WHERE o.operation_type='catalog_import_correction' AND o.status='acked'
UNION SELECT o.id AS Id,o.payload_json AS PayloadJson,o.payload_hash AS PayloadHash FROM catalog_import_recovery_contributions c JOIN catalog_import_outbox o ON o.id=c.contributor_id WHERE c.original_id=@root AND o.operation_type='catalog_import_correction' AND o.status='acked' ORDER BY Id", new { root = draft.Original.Id }, tx).ConfigureAwait(false);
            var proofs=new Dictionary<string,CatalogImportCorrectionSharedProof>();
            foreach (var saved in corrections)
            {
                if (CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.PayloadJson) != saved.PayloadHash) throw new CatalogImportRecoveryException("payload_hash_mismatch");
                var proofHash=CatalogImportCorrectionTransport.SharedProofHash(saved.PayloadJson);
                if(proofHash!=null && !proofs.ContainsKey(proofHash)) proofs.Clear();
                saved.SharedProof=await CatalogImportCorrectionSharedProof.LoadAsync(conn,tx,saved.PayloadJson,proofs).ConfigureAwait(false);
                var request = CatalogImportCorrectionTransport.ReadSavedRequest(saved.PayloadJson, saved.SharedProof);
                foreach (var correction in request.Correction.Items)
                {
                    if (!rows.TryGetValue(correction.ClientItemId, out var row)) throw new CatalogImportRecoveryException("recovery_state_changed");
                    if (correction.Changes.RetailPrice.HasValue) row.RetailPrice = correction.Changes.RetailPrice.Value.ToString(CultureInfo.InvariantCulture);
                    if (correction.Changes.PurchasePrice.HasValue) row.PurchasePrice = correction.Changes.PurchasePrice.Value.ToString(CultureInfo.InvariantCulture);
                    if (correction.Changes.QuantityDelta.HasValue) row.Quantity = (CatalogImportOutboxPayloadBuilder.QuantityOrZero(row.Quantity) + correction.Changes.QuantityDelta.Value).ToString(CultureInfo.InvariantCulture);
                }
                saved.SharedProof=null;
            }
            draft.Rows = draft.InitialIntentRequest.Items.Select(ToEditable).ToArray();
        }

        public Task SaveDraftAsync(CatalogImportRecoveryDraft draft, IReadOnlyList<SupplierImportEditableRow> rows, CancellationToken token)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            DemandRows(draft, rows);
            var json = Serialize(new SavedDraftContent { Rows=SnapshotRows(rows),OperationCreatedAtUtc=draft.OperationCreatedAtUtc }); // Capture before UI can edit again.
            var hash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json);
            return Task.Run(async () =>
            {
                using (var conn = _factory.Open()) using (var tx = conn.BeginTransaction())
                {
                    var original = await LoadOriginalAsync(conn, tx, draft.Original.Id).ConfigureAwait(false);
                    if (original.PayloadHash != draft.Original.PayloadHash || original.PayloadJson != draft.Original.PayloadJson)
                        throw new CatalogImportRecoveryException("recovery_state_changed");
                    await conn.ExecuteAsync(@"INSERT INTO catalog_import_recovery_draft(original_id,payload_hash,rows_json,rows_hash,origin_shop_id,origin_shop_code,generation_fingerprint,transition_epoch,revision_fingerprint,updated_at)
VALUES(@id,@PayloadHash,@json,@hash,@OriginShopId,@OriginShopCode,@generation,@TransitionEpoch,@RevisionFingerprint,@now)
ON CONFLICT(original_id) DO UPDATE SET payload_hash=excluded.payload_hash,rows_json=excluded.rows_json,rows_hash=excluded.rows_hash,
origin_shop_id=excluded.origin_shop_id,origin_shop_code=excluded.origin_shop_code,generation_fingerprint=excluded.generation_fingerprint,
transition_epoch=excluded.transition_epoch,revision_fingerprint=excluded.revision_fingerprint,updated_at=excluded.updated_at;",
                        new { id = original.Id, original.PayloadHash, json, hash, original.OriginShopId, original.OriginShopCode,
                            generation = draft.Generation?.Fingerprint, draft.TransitionEpoch, draft.RevisionFingerprint,
                            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); tx.Commit();
                }
                draft.HasSavedDraft = true;
            }, token);
        }

        public Task DiscardDraftAsync(long originalId, CancellationToken token) => Task.Run(async () =>
        {
            using (var conn = _factory.Open())
                await conn.ExecuteAsync("DELETE FROM catalog_import_recovery_draft WHERE original_id=@originalId; DELETE FROM catalog_import_prepared_plan WHERE original_id=@originalId AND dispatch_started_at IS NULL", new { originalId }).ConfigureAwait(false);
        }, token);

        public async Task<CatalogImportRecoveryDraft> PrepareLocalAsync(long originalId, PosTrustedDeviceSession session,
            OnlineSyncGeneration generation, CancellationToken token)
        {
            long? plannedCorrection=null;
            // Show the same ordinary blocked leaf offline that verification will
            // recover online; edits and their saved draft remain scoped to it.
            using(var conn=_factory.Open())
            {
                var archiving=await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE original_id=@originalId AND resolved_at IS NULL",new { originalId }).ConfigureAwait(false)>0;
                var blocked=archiving ? null : await FindBlockedReplacementAsync(conn,originalId,null).ConfigureAwait(false);
                if(blocked.HasValue)
                {
                    if(await conn.ExecuteScalarAsync<string>("SELECT operation_type FROM catalog_import_outbox WHERE id=@id",new { id=blocked.Value }).ConfigureAwait(false)=="catalog_import")
                        originalId=blocked.Value;
                    else plannedCorrection=blocked.Value;
                }
            }
            var draft = await LoadLocalAsync(originalId, session, generation, token).ConfigureAwait(false);
            if(plannedCorrection.HasValue)
            {
                using(var conn=_factory.Open())
                {
                    draft.TargetOriginal=await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(SelectOriginal+" WHERE o.id=@id",new { id=plannedCorrection.Value }).ConfigureAwait(false);
                    draft.TargetOriginal.SharedProof=await CatalogImportCorrectionSharedProof.LoadAsync(conn,null,draft.TargetOriginal.PayloadJson).ConfigureAwait(false);
                    await InitializeReplacementDraftAsync(draft,draft.TargetOriginal).ConfigureAwait(false);
                    if(draft.TargetOriginal.ReceiptJson!=null)
                    {
                        var receipt=Deserialize<PosCatalogImportReceiptResponse>(draft.TargetOriginal.ReceiptJson);
                        ValidateTargetReceipt(draft,receipt);draft.TargetReceipt=receipt;
                        UpdateRevisionSummary(draft,draft.Receipt,receipt);
                    }
                    var frozen=await conn.QuerySingleOrDefaultAsync<LocalPlanRows>(@"SELECT p.recovery_rows_json AS Json,p.recovery_rows_hash AS Hash FROM catalog_import_plan p
JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE m.outbox_id=@id AND p.remote_plan_json IS NOT NULL",new { id=plannedCorrection.Value }).ConfigureAwait(false);
                    if(frozen!=null)
                    {
                        if(frozen.Json==null || CatalogImportOutboxPayloadBuilder.Sha256Hex(frozen.Json)!=frozen.Hash)
                            throw new CatalogImportRecoveryException("payload_hash_mismatch");
                        var rows=Deserialize<SupplierImportEditableRow[]>(frozen.Json);DemandRows(draft,rows);
                        draft.Rows=rows;draft.RequiresPlanRetirement=true;
                    }
                }
            }
            await LoadSupersessionAsync(draft,token).ConfigureAwait(false);
            draft.CanCommit = false;
            await RestoreDraftAsync(draft, token).ConfigureAwait(false);
            return draft;
        }

        private sealed class LocalPlanRows { public string Json { get; set; } public string Hash { get; set; } }

        private Task RestoreDraftAsync(CatalogImportRecoveryDraft draft, CancellationToken token) => Task.Run(async () =>
        {
            using (var conn = _factory.Open())
            {
                var saved = await conn.QuerySingleOrDefaultAsync<SavedOperatorDraft>(@"SELECT payload_hash AS PayloadHash,rows_json AS RowsJson,rows_hash AS RowsHash,
origin_shop_id AS OriginShopId,origin_shop_code AS OriginShopCode,generation_fingerprint AS GenerationFingerprint,
transition_epoch AS TransitionEpoch,revision_fingerprint AS RevisionFingerprint FROM catalog_import_recovery_draft WHERE original_id=@id", new { id = draft.Original.Id }).ConfigureAwait(false);
                var prepared=await conn.QuerySingleOrDefaultAsync<PreparedRows>(@"SELECT rows_json AS Json,rows_hash AS Hash,
original_hash AS OriginalHash,operation_created_at AS CreatedAt FROM catalog_import_prepared_plan WHERE original_id=@id",new { id=draft.Original.Id }).ConfigureAwait(false);
                draft.HasPreparedPlan=prepared!=null || draft.Supersession!=null && !draft.Supersession.IsSettled;
                if (saved == null)
                {
                    if(prepared==null) return;
                    if(prepared.OriginalHash!=draft.Original.PayloadHash || CatalogImportOutboxPayloadBuilder.Sha256Hex(prepared.Json)!=prepared.Hash)
                        throw new CatalogImportRecoveryException("payload_hash_mismatch");
                    var preparedRows=Deserialize<SupplierImportEditableRow[]>(prepared.Json);DemandRows(draft,preparedRows);
                    draft.Rows=preparedRows;draft.OperationCreatedAtUtc=prepared.CreatedAt;return;
                }
                if (saved.PayloadHash != draft.Original.PayloadHash || saved.RowsHash != CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.RowsJson))
                    throw new CatalogImportRecoveryException("draft_hash_mismatch");
                var content = Deserialize<SavedDraftContent>(saved.RowsJson);
                var rows = content.Rows;
                DemandRows(draft, rows);
                draft.OperationCreatedAtUtc=content.OperationCreatedAtUtc;
                draft.Rows = rows; draft.HasSavedDraft = true;
                draft.IsSavedDraftStale = saved.OriginShopId != draft.Original.OriginShopId || saved.OriginShopCode != draft.Original.OriginShopCode ||
                    saved.GenerationFingerprint != draft.Generation?.Fingerprint || saved.TransitionEpoch != draft.TransitionEpoch ||
                    saved.RevisionFingerprint != draft.RevisionFingerprint;
            }
        }, token);

        private sealed class PreparedRows { public string Json { get; set; } public string Hash { get; set; } public string OriginalHash { get; set; } public string CreatedAt { get; set; } }

        private sealed class SavedOperatorDraft
        {
            public string PayloadHash { get; set; } public string RowsJson { get; set; } public string RowsHash { get; set; }
            public string OriginShopId { get; set; } public string OriginShopCode { get; set; } public string GenerationFingerprint { get; set; }
            public long TransitionEpoch { get; set; } public string RevisionFingerprint { get; set; }
        }
        [System.Runtime.Serialization.DataContract]
        private sealed class SavedDraftContent
        {
            [System.Runtime.Serialization.DataMember] public string OperationCreatedAtUtc { get; set; }
            [System.Runtime.Serialization.DataMember] public SupplierImportEditableRow[] Rows { get; set; }
        }
    }
}
