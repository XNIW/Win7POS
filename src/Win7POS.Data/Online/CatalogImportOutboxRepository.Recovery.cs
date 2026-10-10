using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportOutboxRepository
    {
        internal static async Task ReconcileRecoveryContributionAsync(SqliteConnection conn,SqliteTransaction tx,
            CatalogImportRecoveryOriginal original,PosCatalogImportRequest originalRequest,CatalogImportRecoveryContribution contribution,
            CatalogImportAckResult authoritativeAck=null)
        {
            var item=new CatalogImportOutboxItem { Id=contribution.ContributorId,ClientImportId=contribution.Request.Batch.ClientImportId,
                IdempotencyKey=contribution.Request.Batch.IdempotencyKey,PayloadHash=contribution.PayloadHash };
            var ack=authoritativeAck ?? CatalogImportRecoveryService.BuildPersistedAck(new CatalogImportRecoveryOriginal
                { Id=item.Id,ClientImportId=item.ClientImportId,IdempotencyKey=item.IdempotencyKey,PayloadHash=item.PayloadHash },contribution.Request,contribution.Receipt.Receipt);
            EnsureRecoveryAckComplete(contribution.Request,ack,true);
            await ApplyOriginalReceiptMappingsAsync(conn,tx,new CatalogImportRecoveryOriginal
                { Id=contribution.ContributorId,IdempotencyKey=contribution.Request.Batch.IdempotencyKey },contribution.Request,ack,original.Id).ConfigureAwait(false);
            var wasAcked=await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_outbox WHERE id=@id AND status='acked';",
                new { id=contribution.ContributorId },tx).ConfigureAwait(false)==1;
            var rows=await conn.ExecuteAsync(@"UPDATE catalog_import_outbox SET status='acked',server_import_id=@serverImportId,
server_request_id=@serverRequestId,last_error_code=NULL,last_error_at=NULL,claim_generation_id=NULL,claim_token=NULL,updated_at=@now
WHERE id=@id AND payload_hash=@hash AND status IN ('pending','retry','failed_blocked','acked');",
                new { id=contribution.ContributorId,hash=contribution.PayloadHash,serverImportId=NormalizeTechnicalId(ack.ServerImportId,160),
                    serverRequestId=NormalizeTechnicalId(ack.ServerRequestId,160),now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },tx).ConfigureAwait(false);
            if (rows!=1) throw new CatalogImportRecoveryException("recovery_overlap_pending");
            if (!wasAcked)
            {
                var raw=await conn.ExecuteScalarAsync<string>("SELECT value FROM app_settings WHERE key=@key;",
                    new { key=CatalogShopStateRepository.ImportAckGenerationKey },tx).ConfigureAwait(false);
                long generation=0;
                if (!string.IsNullOrEmpty(raw) && (!long.TryParse(raw,System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,out generation) || generation<0) || generation==long.MaxValue)
                    throw new CatalogImportRecoveryException("catalog_import_ack_generation_invalid");
                await conn.ExecuteAsync(@"INSERT INTO app_settings(key,value) VALUES(@key,@value)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",new { key=CatalogShopStateRepository.ImportAckGenerationKey,
                    value=(generation+1).ToString(System.Globalization.CultureInfo.InvariantCulture) },tx).ConfigureAwait(false);
            }
            await CompleteRecoveryAsync(conn,tx,contribution.ContributorId,ack,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
        }

        internal static async Task ApplyOriginalReceiptMappingsAsync(SqliteConnection conn,SqliteTransaction tx,
            CatalogImportRecoveryOriginal original,PosCatalogImportRequest request,CatalogImportAckResult ack,long? contributionRootId=null)
        {
            EnsureRecoveryAckComplete(request,ack,true);
            await ApplyRecoveryProductIdsAsync(conn,tx,ack.RemoteProductIds).ConfigureAwait(false);
            // A replacement may not have changed the local price and therefore
            // owns no local history row. Resolve its immutable ancestors and
            // match the exact value; never attach an old ACK to the latest edit.
            var ancestors=(await conn.QueryAsync<ReceiptAncestor>(@"WITH RECURSIVE links(parent,child) AS (
SELECT original_id,replacement_id FROM catalog_import_recovery WHERE replacement_id IS NOT NULL
UNION SELECT p.original_id,m.outbox_id FROM catalog_import_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE p.original_id IS NOT NULL),
a(id,depth) AS (SELECT @id,0 UNION SELECT @contributionRootId,1 WHERE @contributionRootId IS NOT NULL UNION ALL
SELECT l.parent,a.depth+1 FROM links l JOIN a ON a.id=l.child WHERE a.depth<128 AND l.parent<a.id)
SELECT o.id AS Id,o.idempotency_key AS IdempotencyKey,MIN(a.depth) AS Depth FROM a JOIN catalog_import_outbox o ON o.id=a.id GROUP BY o.id ORDER BY Depth,o.id DESC",new { id=original.Id,contributionRootId },tx).ConfigureAwait(false)).ToArray();
            var keys=CatalogImportRecoveryService.Serialize(ancestors.Select(a=>a.IdempotencyKey).ToArray());
            var histories=(await conn.QueryAsync<RecoveryHistoryProof>(@"SELECT catalog_import_idempotency_key AS IdempotencyKey,
catalog_import_client_item_id AS ClientItemId,barcode AS Barcode,LOWER(type) AS PriceType,new_price AS NewPrice,remote_price_id AS RemotePriceId
FROM product_price_history WHERE catalog_import_idempotency_key IN (SELECT value FROM json_each(@keys))
OR remote_price_id IN (SELECT value FROM json_each(@remoteIds))",new { keys,remoteIds=CatalogImportRecoveryService.Serialize(ack.RemotePriceIds.Select(p=>p.RemotePriceId).ToArray()) },tx).ConfigureAwait(false)).ToArray();
            var slots=histories.GroupBy(h=>HistoryKey(h.IdempotencyKey,h.ClientItemId,h.Barcode,h.PriceType),StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var remote=histories.Where(h=>!string.IsNullOrWhiteSpace(h.RemotePriceId)).GroupBy(h=>h.RemotePriceId,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var byBarcode=request.Items.ToDictionary(i=>i.Barcode,StringComparer.Ordinal);
            var ancestorRows=new System.Collections.Generic.List<System.Tuple<string,System.Collections.Generic.Dictionary<string,PosCatalogImportItemRequest>>>();
            foreach(var ancestor in ancestors)
            {
                var intent=ancestor.Id==original.Id ? request : await ReadIntendedRequestAsync(conn,tx,ancestor.Id).ConfigureAwait(false);
                ancestorRows.Add(System.Tuple.Create(ancestor.IdempotencyKey,intent.Items.ToDictionary(i=>i.Barcode,StringComparer.Ordinal)));
            }
            var assignments=new System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<CatalogImportRemotePriceId>>(StringComparer.Ordinal);
            foreach(var price in ack.RemotePriceIds)
            {
                var expected=price.PriceType=="retail" ? byBarcode[price.Barcode].RetailPrice : byBarcode[price.Barcode].PurchasePrice;
                if(remote.TryGetValue(price.RemotePriceId,out var owned))
                {
                    if(owned.Length!=1 || owned[0].Barcode!=price.Barcode || owned[0].PriceType!=price.PriceType ||
                        !CatalogImportOutboxPayloadBuilder.EqualNumber(expected,owned[0].NewPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                        throw new CatalogImportRecoveryException("receipt_history_conflict");
                    continue;
                }
                var matched=false;
                foreach(var ancestor in ancestorRows)
                {
                    if(!ancestor.Item2.TryGetValue(price.Barcode,out var row) ||
                        !CatalogImportOutboxPayloadBuilder.EqualNumber(expected,price.PriceType=="retail" ? row.RetailPrice : row.PurchasePrice) ||
                        !slots.TryGetValue(HistoryKey(ancestor.Item1,row.ClientItemId,price.Barcode,price.PriceType),out var candidates)) continue;
                    if(candidates.Length!=1 || !CatalogImportOutboxPayloadBuilder.EqualNumber(expected,candidates[0].NewPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                        !string.IsNullOrWhiteSpace(candidates[0].RemotePriceId)) throw new CatalogImportRecoveryException("receipt_history_conflict");
                    if(!assignments.TryGetValue(ancestor.Item1,out var pending)) assignments.Add(ancestor.Item1,pending=new System.Collections.Generic.List<CatalogImportRemotePriceId>());
                    pending.Add(new CatalogImportRemotePriceId { Barcode=price.Barcode,ClientItemId=row.ClientItemId,
                        PriceType=price.PriceType,RemotePriceId=price.RemotePriceId });
                    matched=true;break;
                }
                if(!matched) throw new CatalogImportRecoveryException("receipt_history_unmatched");
            }
            foreach(var assignment in assignments)
                await ApplyRemotePriceIdsAsync(conn,tx,assignment.Value,assignment.Key).ConfigureAwait(false);
        }
        private sealed class ReceiptAncestor { public long Id { get; set; } public string IdempotencyKey { get; set; } public int Depth { get; set; } }

        private static async Task ApplyAckMappingsAsync(SqliteConnection conn, SqliteTransaction tx, long outboxId,
            CatalogImportAckResult ack, string idempotencyKey)
        {
            var recovery = await conn.ExecuteScalarAsync<long>(@"SELECT (SELECT COUNT(*) FROM catalog_import_recovery WHERE replacement_id=@outboxId) +
(SELECT COUNT(*) FROM catalog_import_plan_part m JOIN catalog_import_plan p ON p.plan_id=m.plan_id WHERE m.outbox_id=@outboxId AND p.original_id IS NOT NULL)", new { outboxId }, tx).ConfigureAwait(false) > 0;
            if (!recovery)
            {
                await ApplyRemoteProductIdsAsync(conn, tx, ack.RemoteProductIds).ConfigureAwait(false);
                await ApplyRemotePriceIdsAsync(conn, tx, ack.RemotePriceIds, idempotencyKey).ConfigureAwait(false);
                return;
            }
            var request = await ReadIntendedRequestAsync(conn,tx,outboxId).ConfigureAwait(false);
            EnsureRecoveryAckComplete(request, ack, true);
            await ApplyRecoveryProductIdsAsync(conn, tx, ack.RemoteProductIds).ConfigureAwait(false);
            var slots = new System.Collections.Generic.HashSet<string>((await conn.QueryAsync<RecoveryHistorySlot>(@"
SELECT catalog_import_client_item_id AS ClientItemId,barcode AS Barcode,LOWER(type) AS PriceType
FROM product_price_history WHERE catalog_import_idempotency_key=@idempotencyKey;",new { idempotencyKey },tx).ConfigureAwait(false))
                .Select(price => PriceKey(price.ClientItemId,price.Barcode,price.PriceType)),StringComparer.Ordinal);
            var corrected = ack.RemotePriceIds.Where(price => slots.Contains(PriceKey(price.ClientItemId,price.Barcode,price.PriceType))).ToArray();
            await ApplyRemotePriceIdsAsync(conn, tx, corrected, idempotencyKey).ConfigureAwait(false);
        }

        internal async Task<bool> RecordDispatchAsync(CatalogImportOutboxItem item, int attempt, OnlineSyncAttemptFence fence)
        {
            using (var conn = _factory.Open())
            {
                // Missing legacy evidence stays unknown. Do not promote it using
                // attempt_count, which release/defer paths deliberately decrement.
                return await conn.ExecuteScalarAsync<long>(@"
UPDATE catalog_import_recovery SET dispatch_count=dispatch_count+1,updated_at=@now
WHERE original_id=@id AND EXISTS (
 SELECT 1 FROM catalog_import_outbox o WHERE o.id=@id AND o.payload_hash=@hash
 AND o.status='in_progress' AND o.attempt_count=@attempt
 AND ((@generationId IS NULL AND o.claim_generation_id IS NULL AND o.claim_token IS NULL
       AND NOT EXISTS(SELECT 1 FROM pos_sync_session_generation WHERE singleton_id=1 AND active=1))
 OR (o.claim_generation_id=@generationId AND o.claim_token=@claimToken AND EXISTS(
 SELECT 1 FROM pos_sync_session_generation WHERE singleton_id=1 AND active=1
 AND generation_id=@generationId AND fingerprint=@generationFingerprint))));
SELECT COUNT(1) FROM catalog_import_outbox o WHERE o.id=@id AND o.payload_hash=@hash
 AND o.status='in_progress' AND o.attempt_count=@attempt
 AND ((@generationId IS NULL AND o.claim_generation_id IS NULL AND o.claim_token IS NULL
       AND NOT EXISTS(SELECT 1 FROM pos_sync_session_generation WHERE singleton_id=1 AND active=1))
 OR (o.claim_generation_id=@generationId AND o.claim_token=@claimToken AND EXISTS(
 SELECT 1 FROM pos_sync_session_generation WHERE singleton_id=1 AND active=1
 AND generation_id=@generationId AND fingerprint=@generationFingerprint)));",
                    FenceParameters(new { id=item.Id, hash=item.PayloadHash, attempt, now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, fence)).ConfigureAwait(false) == 1;
            }
        }

        internal static async Task CompleteRecoveryAsync(SqliteConnection conn, SqliteTransaction tx, long replacementId,
            CatalogImportAckResult ack, long now,bool leafAck=true,PosCatalogImportRequest acknowledgedIntent=null,string convergenceJson=null,string aggregationJson=null)
        {
            if (await CompletePlanRecoveryAsync(conn, tx, replacementId, ack, now, acknowledgedIntent,convergenceJson,aggregationJson).ConfigureAwait(false)) return;
            var original = await conn.QuerySingleOrDefaultAsync<CatalogImportRecoveryOriginal>(@"
SELECT o.id AS Id,o.payload_json AS PayloadJson,o.payload_hash AS PayloadHash,o.idempotency_key AS IdempotencyKey,
o.client_import_id AS ClientImportId,o.status AS Status
FROM catalog_import_recovery r JOIN catalog_import_outbox o ON o.id=r.original_id
WHERE r.replacement_id=@replacementId AND r.resolved_at IS NULL;", new { replacementId }, tx).ConfigureAwait(false);
            if (original == null) return;
            if(CatalogImportOutboxPayloadBuilder.Sha256Hex(original.PayloadJson)!=original.PayloadHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var replacement = await ReadIntendedRequestAsync(conn,tx,replacementId).ConfigureAwait(false);
            if(leafAck) EnsureRecoveryAckComplete(replacement, ack, true);
            acknowledgedIntent=acknowledgedIntent ?? replacement;
            var request = await ReadIntendedRequestAsync(conn,tx,original.Id).ConfigureAwait(false);
            var originals = request.Items.ToDictionary(item => item.Barcode,StringComparer.Ordinal);
            var replacements = replacement.Items.ToDictionary(item => item.Barcode,StringComparer.Ordinal);
            var acknowledged=acknowledgedIntent.Items.ToDictionary(item=>item.Barcode,StringComparer.Ordinal);
            var originalPrices = ack.RemotePriceIds.Where(price => originals.TryGetValue(price.Barcode,out var before) &&
                replacements.TryGetValue(price.Barcode,out var after) && acknowledged.TryGetValue(price.Barcode,out var effective) &&
                CatalogImportOutboxPayloadBuilder.EqualNumber(
                price.PriceType=="retail" ? before.RetailPrice : before.PurchasePrice,
                price.PriceType=="retail" ? effective.RetailPrice : effective.PurchasePrice) && CatalogImportOutboxPayloadBuilder.EqualNumber(
                price.PriceType == "retail" ? before.RetailPrice : before.PurchasePrice,
                price.PriceType == "retail" ? after.RetailPrice : after.PurchasePrice))
                .Select(price => new CatalogImportRemotePriceId
            {
                Barcode = price.Barcode, PriceType = price.PriceType, RemotePriceId = price.RemotePriceId,
                ClientItemId = originals[price.Barcode].ClientItemId
            }).ToArray();
            // Unchanged local rows retained their original history correlation.
            // Assign their ACK identities without adding economic history rows.
            await ApplyRemotePriceIdsAsync(conn, tx, originalPrices, original.IdempotencyKey).ConfigureAwait(false);
            var changed = await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox SET status='recovered',updated_at=@now
WHERE id=@id AND status='failed_blocked' AND payload_hash=@hash;
UPDATE catalog_import_recovery SET resolved_at=@now,updated_at=@now
WHERE original_id=@id AND replacement_id=@replacementId AND resolved_at IS NULL;",
                new { id=original.Id, hash=original.PayloadHash, now, replacementId }, tx).ConfigureAwait(false);
            if (changed != 2) throw new CatalogImportRecoveryException("recovery_state_changed");
            // A retired correction can have a replacement of its own. The
            // accepted leaf closes each durable ancestor link in this same TX.
            await CompleteRecoveryAsync(conn,tx,original.Id,ack,now,false,acknowledgedIntent,convergenceJson,aggregationJson).ConfigureAwait(false);
        }

        private static async Task<PosCatalogImportRequest> ReadIntendedRequestAsync(SqliteConnection conn,SqliteTransaction tx,long id)
        {
            var item=await conn.QuerySingleAsync<CatalogImportOutboxItem>("SELECT payload_json AS PayloadJson,operation_type AS OperationType FROM catalog_import_outbox WHERE id=@id",new { id },tx).ConfigureAwait(false);
            if(item.OperationType=="catalog_import_correction") item.SharedProof=await CatalogImportCorrectionSharedProof.LoadAsync(conn,tx,item.PayloadJson).ConfigureAwait(false);
            return item.OperationType=="catalog_import_correction" ? CatalogImportCorrectionTransport.ReadAckIntendedRequest(item.PayloadJson, item.SharedProof) :
                CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(item.PayloadJson);
        }

        internal static void EnsureRecoveryAckComplete(PosCatalogImportRequest request, CatalogImportAckResult ack, bool requirePrices)
        {
            var items = request.Items.ToDictionary(item => item.ClientItemId,StringComparer.Ordinal);
            var products = new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
            var prices = new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
            var productOwners = new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
            var priceOwners = new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var product in ack.RemoteProductIds)
            {
                if (product == null || !items.TryGetValue(product.ClientItemId ?? "",out var item) || product.Barcode != item.Barcode ||
                    string.IsNullOrWhiteSpace(product.RemoteProductId) || NormalizeTechnicalId(product.RemoteProductId,160) != product.RemoteProductId ||
                    products.TryGetValue(item.ClientItemId,out var existing) && existing != product.RemoteProductId)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                products[item.ClientItemId] = product.RemoteProductId;
                if (productOwners.TryGetValue(product.RemoteProductId,out var owner) && owner != item.Barcode)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                productOwners[product.RemoteProductId] = item.Barcode;
            }
            foreach (var price in ack.RemotePriceIds)
            {
                if (price == null || !items.TryGetValue(price.ClientItemId ?? "",out var item) || price.Barcode != item.Barcode ||
                    string.IsNullOrWhiteSpace(price.RemotePriceId) || NormalizeTechnicalId(price.RemotePriceId,160) != price.RemotePriceId ||
                    price.PriceType != "retail" && price.PriceType != "purchase")
                    throw new CatalogImportRecoveryException("receipt_conflict");
                var key = PriceKey(price.ClientItemId,price.Barcode,price.PriceType);
                if (prices.TryGetValue(key,out var existing) && existing != price.RemotePriceId)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                prices[key] = price.RemotePriceId;
                if (priceOwners.TryGetValue(price.RemotePriceId,out var owner) && owner != key)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                priceOwners[price.RemotePriceId] = key;
            }
            foreach (var item in request.Items)
            {
                if (!products.ContainsKey(item.ClientItemId))
                    throw new CatalogImportRecoveryException("receipt_incomplete");
                if (!requirePrices) continue;
                foreach (var type in new[] { "purchase", "retail" })
                {
                    var value = type == "retail" ? item.RetailPrice : item.PurchasePrice;
                    if (!string.IsNullOrWhiteSpace(value) && CatalogImportOutboxPayloadBuilder.IsAdminPrice(value) &&
                        !prices.ContainsKey(PriceKey(item.ClientItemId,item.Barcode,type)))
                        throw new CatalogImportRecoveryException("receipt_incomplete");
                }
            }
        }
        internal static async Task ApplyRecoveryProductIdsAsync(SqliteConnection conn, SqliteTransaction tx,
            System.Collections.Generic.IReadOnlyList<CatalogImportRemoteProductId> mappings)
        {
            await ApplyRemoteProductIdsAsync(conn,tx,mappings).ConfigureAwait(false);
            var actual = (await conn.QueryAsync<RecoveryProductSlot>(@"
SELECT barcode AS Barcode,remote_product_id AS RemoteProductId,COALESCE(is_active,1) AS IsActive
FROM products WHERE barcode IN (SELECT value FROM json_each(@barcodesJson));",new { barcodesJson=CatalogImportRecoveryService.Serialize(mappings.Select(mapping=>mapping.Barcode).Distinct(StringComparer.Ordinal).ToArray()) },tx).ConfigureAwait(false))
                .ToDictionary(product=>product.Barcode,StringComparer.Ordinal);
            foreach (var mapping in mappings)
                if (!actual.TryGetValue(mapping.Barcode,out var product) || product.RemoteProductId != mapping.RemoteProductId || !product.IsActive)
                    throw new CatalogImportRecoveryException("receipt_conflict");
        }
        private static string PriceKey(string clientItemId,string barcode,string type) => clientItemId + "\0" + barcode + "\0" + (type ?? "").ToLowerInvariant();
        private static string HistoryKey(string idempotencyKey,string clientItemId,string barcode,string type) => idempotencyKey + "\0" + PriceKey(clientItemId,barcode,type);
        private sealed class RecoveryHistorySlot
        {
            public string ClientItemId { get; set; }
            public string Barcode { get; set; }
            public string PriceType { get; set; }
        }
        private sealed class RecoveryProductSlot
        {
            public string Barcode { get; set; }
            public string RemoteProductId { get; set; }
            public bool IsActive { get; set; }
        }
        private sealed class RecoveryHistoryProof
        {
            public string IdempotencyKey { get; set; }
            public string ClientItemId { get; set; }
            public string Barcode { get; set; }
            public string PriceType { get; set; }
            public long NewPrice { get; set; }
            public string RemotePriceId { get; set; }
        }
    }

    internal sealed class CatalogImportRecoveryOriginal : CatalogImportRecoveryState
    {
        internal CatalogImportCorrectionSharedProof SharedProof { get; set; }
        public long Id { get; set; }
        public string ClientImportId { get; set; }
        public string IdempotencyKey { get; set; }
        public string PayloadHash { get; set; }
        public string PayloadJson { get; set; }
        public string OriginShopId { get; set; }
        public string OriginShopCode { get; set; }
        public string LastErrorCode { get; set; }
        public string Status { get; set; }
        public string OperationType { get; set; }
    }

    internal class CatalogImportRecoveryState
    {
        public bool DeliveryKnown { get; set; }
        public long DispatchCount { get; set; }
        public string ReceiptStatus { get; set; }
        public string ReceiptJson { get; set; }
        public long? ReplacementId { get; set; }
    }
}
