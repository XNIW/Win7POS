using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed class CatalogImportPlanProgress
    {
        public int TotalParts { get; set; }
        public int CompletedParts { get; set; }
        public int TotalRows { get; set; }
        public int CompletedRows { get; set; }
        public int FailedParts { get; set; }
        public string LastErrorCode { get; set; }
    }

    public sealed partial class CatalogImportOutboxRepository
    {
        internal static async Task SavePlanAsync(SqliteConnection conn, SqliteTransaction tx, CatalogImportOutboxPlan plan,
            IReadOnlyList<long> ids, long? originalId)
        {
            if (plan.Entries.Count != ids.Count || plan.TotalRows != plan.Entries.Sum(CatalogImportPlanBuilder.CountRows))
                throw new CatalogImportRecoveryException("recovery_state_changed");
            await conn.ExecuteAsync(@"INSERT OR IGNORE INTO catalog_import_plan(plan_id,original_id,total_rows,total_parts,created_at,remote_plan_json,remote_plan_hash,recovery_rows_json,recovery_rows_hash)
VALUES(@PlanId,@originalId,@TotalRows,@totalParts,@now,@RemotePlanJson,@remoteHash,@RecoveryRowsJson,@rowsHash)", new { plan.PlanId, originalId, plan.TotalRows, plan.RemotePlanJson,plan.RecoveryRowsJson,rowsHash=plan.RecoveryRowsJson==null ? null : CatalogImportOutboxPayloadBuilder.Sha256Hex(plan.RecoveryRowsJson),
                remoteHash=plan.RemotePlanJson==null ? null : CatalogImportOutboxPayloadBuilder.Sha256Hex(plan.RemotePlanJson), totalParts = ids.Count, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
            if (await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_plan WHERE plan_id=@PlanId AND original_id IS @originalId AND total_rows=@TotalRows AND total_parts=@totalParts AND recovery_rows_json IS @RecoveryRowsJson AND remote_plan_json IS @RemotePlanJson",
                new { plan.PlanId, originalId, plan.TotalRows, totalParts = ids.Count,plan.RecoveryRowsJson,plan.RemotePlanJson }, tx).ConfigureAwait(false) != 1)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            for (var index = 0; index < ids.Count; index++)
            {
                var entry = plan.Entries[index]; var rowCount = CatalogImportPlanBuilder.CountRows(entry);
                await conn.ExecuteAsync(@"INSERT OR IGNORE INTO catalog_import_plan_part(plan_id,ordinal,outbox_id,payload_hash,row_count)
VALUES(@PlanId,@index,@id,@PayloadHash,@rowCount)", new { plan.PlanId, index, id = ids[index], entry.PayloadHash, rowCount }, tx).ConfigureAwait(false);
                if (await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_plan_part WHERE plan_id=@PlanId AND ordinal=@index AND outbox_id=@id AND payload_hash=@PayloadHash AND row_count=@rowCount",
                    new { plan.PlanId, index, id = ids[index], entry.PayloadHash, rowCount }, tx).ConfigureAwait(false) != 1)
                    throw new CatalogImportRecoveryException("recovery_state_changed");
            }
        }

        internal async Task<CatalogImportSavedRemotePlan> GetRemotePlanAsync(long outboxId)
        {
            using(var conn=_factory.Open())
            {
                var row=await conn.QuerySingleOrDefaultAsync<RemotePlanRow>(@"SELECT p.remote_plan_json AS Json,p.remote_plan_hash AS Hash,m.ordinal AS Ordinal,
CASE WHEN p.completed_at IS NULL AND NOT EXISTS(SELECT 1 FROM catalog_import_recovery_supersession s
 WHERE s.predecessor_plan_id=json_extract(p.remote_plan_json,'$.Document.planId') AND s.resolved_at IS NOT NULL) THEN 1 ELSE 0 END AS RequiresRetirement
FROM catalog_import_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE m.outbox_id=@outboxId",new { outboxId }).ConfigureAwait(false);
                if(row?.Json==null) return null;
                if(CatalogImportOutboxPayloadBuilder.Sha256Hex(row.Json)!=row.Hash) throw new CatalogImportRecoveryException("payload_hash_mismatch");
                var saved=CatalogImportRecoveryService.Deserialize<CatalogImportSavedRemotePlan>(row.Json);saved.PartIndex=row.Ordinal;saved.RequiresRetirement=row.RequiresRetirement;return saved;
            }
        }
        private sealed class RemotePlanRow { public string Json { get; set; } public string Hash { get; set; } public int Ordinal { get; set; } public bool RequiresRetirement { get; set; } }

        private static async Task<bool> CompletePlanRecoveryAsync(SqliteConnection conn, SqliteTransaction tx, long partId,
            CatalogImportAckResult ack, long now, PosCatalogImportRequest acknowledgedIntent,string convergenceJson=null,string aggregationJson=null)
        {
            var plan = await conn.QuerySingleOrDefaultAsync<PlanParent>(@"SELECT p.plan_id AS PlanId,p.original_id AS OriginalId,
CASE WHEN p.completed_at IS NOT NULL THEN 1 ELSE 0 END AS Completed,m.ack_json AS AckJson FROM catalog_import_plan p
JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE m.outbox_id=@partId", new { partId }, tx).ConfigureAwait(false);
            if (plan == null) return false;
            if(plan.Completed)
            {
                // A later correction is a separate operation. Its new ACK must
                // not rewrite evidence already used to close an earlier plan.
                if(plan.AckJson==null) throw new CatalogImportRecoveryException("receipt_incomplete");
                await ValidateConvergenceMembershipAsync(conn,tx,partId,plan.AckJson).ConfigureAwait(false);
                return true;
            }
            var part = await ReadIntendedRequestAsync(conn, tx, partId).ConfigureAwait(false);
            acknowledgedIntent = acknowledgedIntent ?? part;
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_outbox WHERE id=@partId AND status IN ('acked','recovered')", new { partId }, tx).ConfigureAwait(false) == 1)
                await conn.ExecuteAsync("UPDATE catalog_import_plan_part SET ack_json=@json WHERE outbox_id=@partId", new { partId,
                    json = CatalogImportRecoveryService.Serialize(new PlanAck { Products = ack.RemoteProductIds.ToArray(), Prices = ack.RemotePriceIds.ToArray(), Intent = acknowledgedIntent.Items,
                        ConvergenceJson=convergenceJson,ConvergenceHash=convergenceJson==null ? null : CatalogImportOutboxPayloadBuilder.Sha256Hex(convergenceJson),
                        AggregationJson=aggregationJson,AggregationHash=aggregationJson==null ? null : CatalogImportOutboxPayloadBuilder.Sha256Hex(aggregationJson) }) }, tx).ConfigureAwait(false);
            var complete = await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_plan_part m JOIN catalog_import_outbox o ON o.id=m.outbox_id
WHERE m.plan_id=@PlanId AND (o.status NOT IN ('acked','recovered') OR o.payload_hash<>m.payload_hash OR m.ack_json IS NULL)", new { plan.PlanId }, tx).ConfigureAwait(false) == 0;
            if (complete)
            {
                var members=await conn.QueryAsync<ConvergenceMember>("SELECT outbox_id AS Id,ack_json AS AckJson FROM catalog_import_plan_part WHERE plan_id=@PlanId ORDER BY ordinal",new { plan.PlanId },tx).ConfigureAwait(false);
                foreach(var member in members) await ValidateConvergenceMembershipAsync(conn,tx,member.Id,member.AckJson).ConfigureAwait(false);
                await conn.ExecuteAsync("UPDATE catalog_import_plan SET completed_at=COALESCE(completed_at,@now) WHERE plan_id=@PlanId", new { plan.PlanId, now }, tx).ConfigureAwait(false);
                await CatalogImportRecoveryService.CompleteSupersessionAsync(conn,tx,plan.PlanId,now).ConfigureAwait(false);
            }
            if (!plan.OriginalId.HasValue) return true;
            var original = await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(@"SELECT id AS Id,payload_json AS PayloadJson,payload_hash AS PayloadHash,idempotency_key AS IdempotencyKey
FROM catalog_import_outbox WHERE id=@id", new { id = plan.OriginalId.Value }, tx).ConfigureAwait(false);
            if (CatalogImportOutboxPayloadBuilder.Sha256Hex(original.PayloadJson) != original.PayloadHash) throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var originalIntent = await ReadIntendedRequestAsync(conn, tx, original.Id).ConfigureAwait(false);
            var before = originalIntent.Items.ToDictionary(i => i.Barcode, StringComparer.Ordinal);
            var after = acknowledgedIntent.Items.ToDictionary(i => i.Barcode, StringComparer.Ordinal);
            var mappings = ack.RemotePriceIds.Where(p => before.ContainsKey(p.Barcode) && after.ContainsKey(p.Barcode) &&
                CatalogImportOutboxPayloadBuilder.EqualNumber(p.PriceType == "retail" ? before[p.Barcode].RetailPrice : before[p.Barcode].PurchasePrice,
                    p.PriceType == "retail" ? after[p.Barcode].RetailPrice : after[p.Barcode].PurchasePrice))
                .Select(p => new CatalogImportRemotePriceId { Barcode = p.Barcode, ClientItemId = before[p.Barcode].ClientItemId, PriceType = p.PriceType, RemotePriceId = p.RemotePriceId }).ToArray();
            await ApplyRemotePriceIdsAsync(conn, tx, mappings, original.IdempotencyKey).ConfigureAwait(false);
            if (complete)
            {
                await conn.ExecuteAsync(@"UPDATE catalog_import_outbox SET status='recovered',updated_at=@now WHERE id=@id AND payload_hash=@PayloadHash AND status='failed_blocked';
UPDATE catalog_import_recovery SET resolved_at=COALESCE(resolved_at,@now),updated_at=@now WHERE original_id=@id;",
                    new { id = original.Id, original.PayloadHash, now }, tx).ConfigureAwait(false);
            }
            // Also map unchanged history in an outer plan before its final ACK.
            if (complete)
            {
                var proofJson=(await conn.QueryAsync<string>("SELECT ack_json FROM catalog_import_plan_part WHERE plan_id=@PlanId ORDER BY ordinal", new { plan.PlanId }, tx).ConfigureAwait(false)).ToArray();
                var proofs=proofJson.Select(CatalogImportRecoveryService.Deserialize<PlanAck>).ToArray();
                var aggregate = new CatalogImportAckResult { RemoteProductIds = proofs.SelectMany(p => p.Products).ToArray(), RemotePriceIds = proofs.SelectMany(p => p.Prices).ToArray() };
                var intent = new PosCatalogImportRequest { Items = proofs.SelectMany(p => p.Intent).ToArray() };
                var completeProof=await BuildRecoveryAggregationAsync(conn,tx,plan.PlanId,original.Id).ConfigureAwait(false);
                if(completeProof!=null)
                {
                    aggregate=new CatalogImportAckResult { RemoteProductIds=completeProof.Products,RemotePriceIds=completeProof.Prices };
                    intent=new PosCatalogImportRequest { Items=completeProof.Intent };
                }
                await CompleteRecoveryAsync(conn, tx, original.Id, aggregate, now, false, intent,aggregationJson:completeProof?.AggregationJson).ConfigureAwait(false);
            }
            else await CompletePlanRecoveryAsync(conn, tx, original.Id, ack, now, acknowledgedIntent).ConfigureAwait(false);
            return true;
        }
        private sealed class PlanParent { public string PlanId { get; set; } public long? OriginalId { get; set; } public bool Completed { get; set; } public string AckJson { get; set; } }
        private sealed class ConvergenceMember { public long Id { get; set; } public string AckJson { get; set; } }
        [System.Runtime.Serialization.DataContract]
        private sealed class PlanAck
        {
            [System.Runtime.Serialization.DataMember] public CatalogImportRemoteProductId[] Products { get; set; }
            [System.Runtime.Serialization.DataMember] public CatalogImportRemotePriceId[] Prices { get; set; }
            [System.Runtime.Serialization.DataMember] public PosCatalogImportItemRequest[] Intent { get; set; }
            [System.Runtime.Serialization.DataMember(EmitDefaultValue=false)] public string ConvergenceJson { get; set; }
            [System.Runtime.Serialization.DataMember(EmitDefaultValue=false)] public string ConvergenceHash { get; set; }
            [System.Runtime.Serialization.DataMember(EmitDefaultValue=false)] public string AggregationJson { get; set; }
            [System.Runtime.Serialization.DataMember(EmitDefaultValue=false)] public string AggregationHash { get; set; }
        }
    }

    public sealed partial class CatalogImportRecoveryService
    {
        public Task<CatalogImportPlanProgress> GetPlanProgressAsync(long originalId, CancellationToken token) => Task.Run(async () =>
        {
            using (var conn = _factory.Open())
                return await conn.QuerySingleAsync<CatalogImportPlanProgress>(@"WITH active_plan AS (
SELECT p.* FROM catalog_import_plan p
WHERE (p.original_id=@originalId OR (p.original_id IS NULL AND p.plan_id=(SELECT plan_id FROM catalog_import_plan_part WHERE outbox_id=@originalId)))
AND NOT EXISTS(SELECT 1 FROM catalog_import_recovery_supersession superseded
 WHERE superseded.predecessor_plan_id=json_extract(p.remote_plan_json,'$.Document.planId') AND superseded.successor_plan_id IS NOT NULL)
ORDER BY p.created_at DESC,p.rowid DESC LIMIT 1),
coverage AS (
SELECT c.value AS value,json_extract(p.remote_plan_json,'$.Document.supersedes.planId') AS predecessor
FROM active_plan p,json_each(p.remote_plan_json,'$.Document.coverage') c),
carry AS (
SELECT COUNT(DISTINCT json_extract(value,'$.clientItemId')) AS rows,
COUNT(DISTINCT CASE json_extract(value,'$.kind')
 WHEN 'original_accepted' THEN 'original'
 WHEN 'accepted_contributor' THEN 'contributor:'||json_extract(value,'$.verifiedContributorId')
 WHEN 'accepted_plan_part' THEN 'plan:'||COALESCE(json_extract(value,'$.contributorPlanId'),predecessor)||':'||json_extract(value,'$.contributorPartIndex')
 END) AS parts FROM coverage WHERE json_extract(value,'$.kind')<>'child')
SELECT COUNT(m.outbox_id)+(SELECT parts FROM carry) AS TotalParts,
CASE WHEN (SELECT remote_plan_json FROM active_plan) IS NULL THEN COALESCE(SUM(m.row_count),0)
 ELSE (SELECT COUNT(DISTINCT json_extract(value,'$.clientItemId')) FROM coverage) END AS TotalRows,
COALESCE(SUM(CASE WHEN o.status IN ('acked','recovered') AND m.ack_json IS NOT NULL THEN 1 ELSE 0 END),0)+(SELECT parts FROM carry) AS CompletedParts,
COALESCE(SUM(CASE WHEN o.status IN ('acked','recovered') AND m.ack_json IS NOT NULL THEN m.row_count ELSE 0 END),0)+(SELECT rows FROM carry) AS CompletedRows,
COALESCE(SUM(CASE WHEN o.status='failed_blocked' THEN 1 ELSE 0 END),0) AS FailedParts,
MAX(CASE WHEN o.status='failed_blocked' THEN o.last_error_code ELSE NULL END) AS LastErrorCode
FROM active_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id JOIN catalog_import_outbox o ON o.id=m.outbox_id", new { originalId }).ConfigureAwait(false);
        }, token);
    }
}
