using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportRecoveryService
    {
        private async Task<CatalogImportOutboxPlan> LoadPreparedPlanAsync(CatalogImportRecoveryDraft draft,
            SupplierImportEditableRow[] rows, CancellationToken token)
        {
            using(var conn=_factory.Open())
            {
                token.ThrowIfCancellationRequested();
                var saved=await conn.QuerySingleOrDefaultAsync<PreparedPlanRow>(@"SELECT target_id AS TargetId,original_hash AS OriginalHash,
target_hash AS TargetHash,rows_hash AS RowsHash,plan_json AS PlanJson,plan_hash AS PlanHash,remote_plan_json AS RemoteJson,
remote_plan_hash AS RemoteHash,plan_document_json AS DocumentJson,plan_document_hash AS DocumentHash FROM catalog_import_prepared_plan WHERE original_id=@id",new { id=draft.Original.Id }).ConfigureAwait(false);
                if(saved==null) return null;
                if(saved.OriginalHash!=draft.Original.PayloadHash || saved.TargetId!=(draft.TargetOriginal??draft.Original).Id ||
                    saved.TargetHash!=(draft.TargetOriginal??draft.Original).PayloadHash || saved.RowsHash!=CatalogImportOutboxPayloadBuilder.Sha256Hex(Serialize(rows)))
                    throw new CatalogImportRecoveryException("prepared_plan_intent_changed");
                if(CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.PlanJson)!=saved.PlanHash ||
                    saved.RemoteJson!=null && CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.RemoteJson)!=saved.RemoteHash ||
                    saved.DocumentJson!=null && CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.DocumentJson)!=saved.DocumentHash)
                    throw new CatalogImportRecoveryException("payload_hash_mismatch");
                var record=Deserialize<PreparedPlan>(saved.PlanJson);
                var proof=record.SharedProofJson==null ? null : CatalogImportCorrectionSharedProof.Parse(record.SharedProofJson,record.SharedProofHash);
                var entries=record.Entries.Select(e=>new CatalogImportOutboxEntry { ClientImportId=e.ClientImportId,IdempotencyKey=e.IdempotencyKey,
                    CreatedAt=e.CreatedAt,OperationType=e.OperationType,SchemaVersion=e.SchemaVersion,Source=e.Source,PayloadJson=e.PayloadJson,PayloadHash=e.PayloadHash,
                    SharedProof=e.SharedProofHash==null ? null : proof?.Hash==e.SharedProofHash ? proof : throw new CatalogImportRecoveryException("receipt_required") }).ToArray();
                foreach(var entry in entries)
                {
                    if(CatalogImportOutboxPayloadBuilder.Sha256Hex(entry.PayloadJson)!=entry.PayloadHash) throw new CatalogImportRecoveryException("payload_hash_mismatch");
                    if(entry.OperationType!="catalog_import_correction") continue;
                    var correction=CatalogImportCorrectionTransport.ReadSavedRequest(entry.PayloadJson,entry.SharedProof);
                    var snapshots=(draft.Receipt?.CurrentProductSnapshots ?? Array.Empty<Win7POS.Core.Online.PosCatalogImportProductSnapshot>()).ToDictionary(r=>r.ClientItemId,StringComparer.Ordinal);
                    foreach(var item in correction.Correction.Items)
                        if(!snapshots.TryGetValue(item.ClientItemId,out var current) || current.SnapshotStatus!="available" ||
                            current.RemoteProductId!=item.RemoteProductId || current.BaseRevision!=item.BaseRevision)
                            throw new CatalogImportRecoveryException("prepared_plan_stale");
                }
                var plan=entries.Length==0 ? CreateConvergencePlan(draft,rows) : CatalogImportPlanBuilder.Plan(entries);
                if(plan.PlanId!=record.PlanId || plan.TotalRows!=record.TotalRows) throw new CatalogImportRecoveryException("payload_hash_mismatch");
                plan.RemotePlanJson=saved.RemoteJson;plan.PreparedDocumentJson=saved.DocumentJson;return plan;
            }
        }

        private async Task SavePreparedPlanAsync(CatalogImportRecoveryDraft draft,SupplierImportEditableRow[] rows,CatalogImportOutboxPlan plan,
            Func<bool> authorize,CancellationToken token)
        {
            var proofs=plan.Entries.Where(e=>e.SharedProof!=null).Select(e=>e.SharedProof).Distinct().ToArray();
            if(proofs.Length>1) throw new CatalogImportRecoveryException("receipt_conflict");
            var record=new PreparedPlan { PlanId=plan.PlanId,TotalRows=plan.TotalRows,SharedProofJson=proofs.FirstOrDefault()?.Json,
                SharedProofHash=proofs.FirstOrDefault()?.Hash,Entries=plan.Entries.Select(e=>new PreparedEntry { ClientImportId=e.ClientImportId,
                    IdempotencyKey=e.IdempotencyKey,CreatedAt=e.CreatedAt,OperationType=e.OperationType,SchemaVersion=e.SchemaVersion,Source=e.Source,
                    PayloadJson=e.PayloadJson,PayloadHash=e.PayloadHash,SharedProofHash=e.SharedProof?.Hash }).ToArray() };
            var json=Serialize(record);var hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(json);
            using(var conn=_factory.Open()) using(var tx=conn.BeginTransaction())
            {
                await new CatalogImportRecoveryCommit(draft,draft.Generation,authorize).ValidateAsync(conn,tx).ConfigureAwait(false);
                var target=draft.TargetOriginal??draft.Original;
                await conn.ExecuteAsync(@"INSERT OR IGNORE INTO catalog_import_prepared_plan(original_id,target_id,original_hash,target_hash,rows_hash,rows_json,operation_created_at,plan_json,plan_hash,created_at)
VALUES(@id,@target,@originalHash,@targetHash,@rowsHash,@rowsJson,@createdAt,@json,@hash,@now)",new { id=draft.Original.Id,target=target.Id,originalHash=draft.Original.PayloadHash,
                    targetHash=target.PayloadHash,rowsHash=CatalogImportOutboxPayloadBuilder.Sha256Hex(Serialize(rows)),rowsJson=Serialize(rows),createdAt=draft.OperationCreatedAtUtc,json,hash,now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },tx).ConfigureAwait(false);
                if(await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_prepared_plan WHERE original_id=@id AND plan_hash=@hash",new { id=draft.Original.Id,hash },tx).ConfigureAwait(false)!=1)
                    throw new CatalogImportRecoveryException("prepared_plan_intent_changed");
                token.ThrowIfCancellationRequested();DemandPermission(authorize);tx.Commit();
            }
        }

        private async Task SavePreparedDocumentAsync(CatalogImportRecoveryDraft draft,string json,Func<bool> authorize,CancellationToken token)
        {
            using(var conn=_factory.Open()) using(var tx=conn.BeginTransaction())
            {
                await new CatalogImportRecoveryCommit(draft,draft.Generation,authorize).ValidateAsync(conn,tx).ConfigureAwait(false);
                var changed=await conn.ExecuteAsync(@"UPDATE catalog_import_prepared_plan SET plan_document_json=@json,plan_document_hash=@hash,dispatch_started_at=COALESCE(dispatch_started_at,@now)
WHERE original_id=@id AND (plan_document_json IS NULL OR plan_document_json=@json)",new { id=draft.Original.Id,json,now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(json) },tx).ConfigureAwait(false);
                if(changed!=1) throw new CatalogImportRecoveryException("prepared_plan_intent_changed");
                token.ThrowIfCancellationRequested();DemandPermission(authorize);tx.Commit();
            }
        }

        private async Task SavePreparedRemoteReceiptAsync(CatalogImportRecoveryDraft draft,CatalogImportOutboxPlan plan,Func<bool> authorize,CancellationToken token)
        {
            using(var conn=_factory.Open()) using(var tx=conn.BeginTransaction())
            {
                await new CatalogImportRecoveryCommit(draft,draft.Generation,authorize).ValidateAsync(conn,tx).ConfigureAwait(false);
                var changed=await conn.ExecuteAsync(@"UPDATE catalog_import_prepared_plan SET remote_plan_json=@json,remote_plan_hash=@hash
WHERE original_id=@id AND (remote_plan_json IS NULL OR remote_plan_json=@json)",new { id=draft.Original.Id,json=plan.RemotePlanJson,
                    hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(plan.RemotePlanJson) },tx).ConfigureAwait(false);
                if(changed!=1) throw new CatalogImportRecoveryException("receipt_conflict");
                token.ThrowIfCancellationRequested();DemandPermission(authorize);tx.Commit();
            }
        }
        private sealed class PreparedPlanRow
        {
            public long TargetId { get; set; } public string OriginalHash { get; set; } public string TargetHash { get; set; }
            public string RowsHash { get; set; } public string PlanJson { get; set; } public string PlanHash { get; set; }
            public string RemoteJson { get; set; } public string RemoteHash { get; set; }
            public string DocumentJson { get; set; } public string DocumentHash { get; set; }
        }
        [DataContract] private sealed class PreparedPlan
        {
            [DataMember] public string PlanId { get; set; } [DataMember] public int TotalRows { get; set; }
            [DataMember] public string SharedProofJson { get; set; } [DataMember] public string SharedProofHash { get; set; }
            [DataMember] public PreparedEntry[] Entries { get; set; }
        }
        [DataContract] private sealed class PreparedEntry
        {
            [DataMember] public string ClientImportId { get; set; } [DataMember] public string IdempotencyKey { get; set; }
            [DataMember] public long CreatedAt { get; set; } [DataMember] public string OperationType { get; set; }
            [DataMember] public string SchemaVersion { get; set; } [DataMember] public string Source { get; set; }
            [DataMember] public string PayloadJson { get; set; } [DataMember] public string PayloadHash { get; set; }
            [DataMember] public string SharedProofHash { get; set; }
        }
    }
}
