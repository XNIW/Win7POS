using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportOutboxRepository
    {
        private static async Task<PlanAck> BuildRecoveryAggregationAsync(SqliteConnection conn,SqliteTransaction tx,string planId,long originalId)
        {
            var original=await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(CatalogImportRecoveryService.SelectOriginal+" WHERE o.id=@originalId",new { originalId },tx).ConfigureAwait(false);
            if(original.OperationType!="catalog_import" || original.ReceiptStatus=="accepted") return null;
            var lineage=await conn.ExecuteScalarAsync<long>(@"WITH RECURSIVE links(parent,child) AS (
SELECT original_id,replacement_id FROM catalog_import_recovery WHERE replacement_id IS NOT NULL
UNION SELECT p.original_id,m.outbox_id FROM catalog_import_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE p.original_id IS NOT NULL),
a(id,depth) AS (SELECT @originalId,0 UNION ALL SELECT l.parent,a.depth+1 FROM links l JOIN a ON l.child=a.id WHERE a.depth<128 AND l.parent<a.id)
SELECT COUNT(*) FROM a JOIN catalog_import_plan_part m ON m.outbox_id=a.id",new { originalId },tx).ConfigureAwait(false);
            if(lineage==0) return null;
            var state=await ReadAggregationStateAsync(conn,tx,planId,originalId).ConfigureAwait(false);
            var evidence=new RecoveryAggregation { PlanId=planId,OriginalId=originalId,OriginalHash=original.PayloadHash,RowsHash=state.RowsHash,
                Members=state.Members.Select(member=>new AggregationSource { Id=member.Id,PayloadHash=member.PayloadHash,ProofHash=CatalogImportOutboxPayloadBuilder.Sha256Hex(member.AckJson) }).ToArray(),
                Contributors=state.Contributors.Select(contributor=>new AggregationSource { Id=contributor.Id,PayloadHash=contributor.PayloadHash,
                    ProofHash=CatalogImportOutboxPayloadBuilder.Sha256Hex(contributor.ReceiptJson) }).ToArray() };
            var checkedProof=await ReadRecoveryAggregationAsync(conn,tx,evidence).ConfigureAwait(false);
            checkedProof.AggregationJson=CatalogImportRecoveryService.Serialize(evidence);
            checkedProof.AggregationHash=CatalogImportOutboxPayloadBuilder.Sha256Hex(checkedProof.AggregationJson);
            return checkedProof;
        }

        private static async Task ValidateRecoveryAggregationAsync(SqliteConnection conn,SqliteTransaction tx,long expectedId,PlanAck saved)
        {
            if(saved.AggregationJson==null && saved.AggregationHash==null) return;
            if(saved.AggregationJson==null || CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.AggregationJson)!=saved.AggregationHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var evidence=CatalogImportRecoveryService.Deserialize<RecoveryAggregation>(saved.AggregationJson);
            if(evidence.OriginalId!=expectedId && await CatalogImportRecoveryService.IsDescendantAsync(conn,expectedId,evidence.OriginalId,tx).ConfigureAwait(false)==0)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var actual=await ReadRecoveryAggregationAsync(conn,tx,evidence).ConfigureAwait(false);
            if(evidence.OriginalId!=expectedId)
            {
                // Legacy replacement links can propagate a descendant proof to an
                // ancestor. Lineage alone is insufficient: a valid subset proof must
                // never close that ancestor's entire membership.
                var expected=(await ReadIntendedRequestAsync(conn,tx,expectedId).ConfigureAwait(false)).Items;
                var coverage=actual.Intent.ToDictionary(item=>item.Barcode,StringComparer.Ordinal);
                if(expected.Length!=coverage.Count || expected.Select(item=>item.Barcode).Distinct(StringComparer.Ordinal).Count()!=expected.Length ||
                    expected.Any(item=>!coverage.TryGetValue(item.Barcode,out var covered) || covered.RowNumber!=item.RowNumber))
                    throw new CatalogImportRecoveryException("receipt_incomplete");
            }
            if(CatalogImportRecoveryService.Serialize(actual.Products)!=CatalogImportRecoveryService.Serialize(saved.Products) ||
                CatalogImportRecoveryService.Serialize(actual.Prices)!=CatalogImportRecoveryService.Serialize(saved.Prices) ||
                CatalogImportRecoveryService.Serialize(actual.Intent)!=CatalogImportRecoveryService.Serialize(saved.Intent))
                throw new CatalogImportRecoveryException("receipt_conflict");
        }

        private static async Task<PlanAck> ReadRecoveryAggregationAsync(SqliteConnection conn,SqliteTransaction tx,RecoveryAggregation evidence)
        {
            if(evidence?.SchemaVersion!="win7pos-recovery-aggregation-v1") throw new CatalogImportRecoveryException("receipt_conflict");
            var original=await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(CatalogImportRecoveryService.SelectOriginal+" WHERE o.id=@id",new { id=evidence.OriginalId },tx).ConfigureAwait(false);
            if(original.OperationType!="catalog_import" || original.PayloadHash!=evidence.OriginalHash || CatalogImportOutboxPayloadBuilder.Sha256Hex(original.PayloadJson)!=original.PayloadHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(original.PayloadJson);
            var state=await ReadAggregationStateAsync(conn,tx,evidence.PlanId,evidence.OriginalId).ConfigureAwait(false);
            if(state.RowsHash!=evidence.RowsHash || state.RowsJson==null || CatalogImportOutboxPayloadBuilder.Sha256Hex(state.RowsJson)!=state.RowsHash ||
                evidence.Members==null || evidence.Contributors==null || evidence.Members.Length!=state.Members.Length ||
                state.Members.Length!=state.ExpectedParts || evidence.Contributors.Length!=state.Contributors.Length)
                throw new CatalogImportRecoveryException("receipt_incomplete");
            var desired=CatalogImportRecoveryService.Deserialize<SupplierImportEditableRow[]>(state.RowsJson).ToDictionary(row=>row.Barcode,StringComparer.Ordinal);
            if(desired.Count!=request.Items.Length) throw new CatalogImportRecoveryException("receipt_incomplete");
            var sources=new List<AggregationIntent>();
            foreach(var member in state.Members)
            {
                var proof=evidence.Members.SingleOrDefault(source=>source.Id==member.Id);
                if(member.Id<=evidence.OriginalId || proof==null || proof.PayloadHash!=member.PayloadHash || proof.ProofHash!=CatalogImportOutboxPayloadBuilder.Sha256Hex(member.AckJson) ||
                    member.Status!="acked" && member.Status!="recovered" || CatalogImportOutboxPayloadBuilder.Sha256Hex(member.PayloadJson)!=member.PayloadHash)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                await ValidateConvergenceMembershipAsync(conn,tx,member.Id,member.AckJson).ConfigureAwait(false);
                var ack=CatalogImportRecoveryService.Deserialize<PlanAck>(member.AckJson);
                if(ack.ConvergenceJson==null && ack.AggregationJson==null &&
                    CatalogImportRecoveryService.Serialize(ack.Intent)!=CatalogImportRecoveryService.Serialize((await ReadIntendedRequestAsync(conn,tx,member.Id).ConfigureAwait(false)).Items))
                    throw new CatalogImportRecoveryException("receipt_conflict");
                var intent=new PosCatalogImportRequest { Items=ack.Intent };
                var maps=new CatalogImportAckResult { RemoteProductIds=ack.Products,RemotePriceIds=ack.Prices };
                EnsureRecoveryAckComplete(intent,maps,true);
                sources.Add(new AggregationIntent { Items=ack.Intent,Products=ack.Products,Prices=ack.Prices,IsMember=true,
                    HasRecoveredIntent=ack.ConvergenceJson!=null || ack.AggregationJson!=null });
            }
            foreach(var contributor in state.Contributors)
            {
                var proof=evidence.Contributors.SingleOrDefault(source=>source.Id==contributor.Id);
                if(proof==null || proof.PayloadHash!=contributor.PayloadHash || proof.ProofHash!=CatalogImportOutboxPayloadBuilder.Sha256Hex(contributor.ReceiptJson) ||
                    contributor.Status!="acked" || CatalogImportOutboxPayloadBuilder.Sha256Hex(contributor.PayloadJson)!=contributor.PayloadHash ||
                    contributor.OriginShopId!=original.OriginShopId || contributor.OriginShopCode!=original.OriginShopCode)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                var intent=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(contributor.PayloadJson);
                var receipt=CatalogImportRecoveryService.Deserialize<PosCatalogImportReceiptResponse>(contributor.ReceiptJson);
                var owner=new CatalogImportRecoveryOriginal { Id=contributor.Id,PayloadHash=contributor.PayloadHash,PayloadJson=contributor.PayloadJson,
                    ClientImportId=intent.Batch.ClientImportId,IdempotencyKey=intent.Batch.IdempotencyKey,OriginShopId=contributor.OriginShopId,OriginShopCode=contributor.OriginShopCode };
                CatalogImportRecoveryService.ValidateReceipt(new CatalogImportRecoveryDraft { Original=owner,OriginalRequest=intent,ShopDeviceId=receipt.ShopDeviceId },receipt);
                if(receipt.Status!="accepted") throw new CatalogImportRecoveryException("receipt_required");
                var ack=CatalogImportRecoveryService.BuildPersistedAck(owner,intent,receipt.Receipt);
                sources.Add(new AggregationIntent { Items=intent.Items,Products=ack.RemoteProductIds.ToArray(),Prices=ack.RemotePriceIds.ToArray() });
            }
            var due=request.Items.ToDictionary(item=>item.Barcode,StringComparer.Ordinal);
            var fulfilled=new Dictionary<string,Tuple<PosCatalogImportItemRequest,AggregationIntent>>(StringComparer.Ordinal);
            foreach(var source in sources)
                foreach(var item in source.Items)
                {
                    if(!due.TryGetValue(item.Barcode,out var originalItem))
                    { if(source.IsMember) throw new CatalogImportRecoveryException("receipt_conflict");continue; }
                    var row=desired[item.Barcode];
                    var expected=CatalogImportRecoveryService.Deserialize<PosCatalogImportItemRequest>(CatalogImportRecoveryService.Serialize(originalItem));
                    if(row.RowNumber!=expected.RowNumber || row.IsSkipped) throw new CatalogImportRecoveryException("recovery_rows_changed");
                    expected.ProductName=row.ProductName;expected.SecondProductName=row.SecondProductName;expected.ItemNumber=row.ItemNumber;
                    expected.RetailPrice=row.RetailPrice;expected.PurchasePrice=row.PurchasePrice;expected.Quantity=row.Quantity;expected.Supplier=row.Supplier;expected.Category=row.Category;
                    // A validated descendant recovery can intentionally supersede the
                    // values frozen when this ancestor plan was created. Its effective
                    // intent is reconstructed from the actual accepted source receipts.
                    // A plain ACK or external contributor cannot make that substitution.
                    if(!source.HasRecoveredIntent && !CatalogImportOutboxPayloadBuilder.SameIntent(expected,item))
                    { if(source.IsMember) throw new CatalogImportRecoveryException("receipt_conflict");continue; }
                    if(fulfilled.ContainsKey(item.Barcode))
                    { if(source.IsMember) throw new CatalogImportRecoveryException("receipt_conflict");continue; }
                    fulfilled.Add(item.Barcode,Tuple.Create(item,source));
                }
            if(fulfilled.Count!=request.Items.Length) throw new CatalogImportRecoveryException("receipt_incomplete");
            var products=new List<CatalogImportRemoteProductId>();var prices=new List<CatalogImportRemotePriceId>();
            foreach(var item in request.Items)
            {
                var selected=fulfilled[item.Barcode];var sourceItem=selected.Item1;var source=selected.Item2;
                var mapped=source.Products.Where(map=>map.ClientItemId==sourceItem.ClientItemId).First();
                products.Add(new CatalogImportRemoteProductId { ClientItemId=item.ClientItemId,Barcode=item.Barcode,RemoteProductId=mapped.RemoteProductId });
                prices.AddRange(source.Prices.Where(map=>map.ClientItemId==sourceItem.ClientItemId).Select(map=>new CatalogImportRemotePriceId
                    { ClientItemId=item.ClientItemId,Barcode=item.Barcode,PriceType=map.PriceType,RemotePriceId=map.RemotePriceId }));
                item.ProductName=sourceItem.ProductName;item.SecondProductName=sourceItem.SecondProductName;item.ItemNumber=sourceItem.ItemNumber;
                item.PurchasePrice=sourceItem.PurchasePrice;item.RetailPrice=sourceItem.RetailPrice;item.Quantity=sourceItem.Quantity;item.Supplier=sourceItem.Supplier;item.Category=sourceItem.Category;
            }
            EnsureRecoveryAckComplete(request,new CatalogImportAckResult { RemoteProductIds=products,RemotePriceIds=prices },true);
            return new PlanAck { Products=products.ToArray(),Prices=prices.ToArray(),Intent=request.Items };
        }

        private static async Task<AggregationState> ReadAggregationStateAsync(SqliteConnection conn,SqliteTransaction tx,string planId,long originalId)
        {
            var state=await conn.QuerySingleOrDefaultAsync<AggregationState>(@"SELECT recovery_rows_json AS RowsJson,recovery_rows_hash AS RowsHash,total_parts AS ExpectedParts
FROM catalog_import_plan WHERE plan_id=@planId AND original_id=@originalId AND completed_at IS NOT NULL",new { planId,originalId },tx).ConfigureAwait(false);
            if(state==null) throw new CatalogImportRecoveryException("receipt_incomplete");
            state.Members=(await conn.QueryAsync<AggregationMember>(@"SELECT o.id AS Id,o.payload_hash AS PayloadHash,o.payload_json AS PayloadJson,o.status AS Status,m.ack_json AS AckJson
FROM catalog_import_plan_part m JOIN catalog_import_outbox o ON o.id=m.outbox_id WHERE m.plan_id=@planId AND m.payload_hash=o.payload_hash ORDER BY m.ordinal",new { planId },tx).ConfigureAwait(false)).ToArray();
            state.Contributors=(await conn.QueryAsync<AggregationContributor>(@"SELECT o.id AS Id,o.payload_hash AS PayloadHash,o.payload_json AS PayloadJson,o.status AS Status,
o.origin_shop_id AS OriginShopId,o.origin_shop_code AS OriginShopCode,c.receipt_json AS ReceiptJson
FROM catalog_import_recovery_contributions c JOIN catalog_import_outbox o ON o.id=c.contributor_id
WHERE c.original_id=@originalId AND c.payload_hash=o.payload_hash AND o.operation_type='catalog_import' ORDER BY o.id",new { originalId },tx).ConfigureAwait(false)).ToArray();
            return state;
        }
        [DataContract] private sealed class RecoveryAggregation
        {
            [DataMember] public string SchemaVersion { get; set; }="win7pos-recovery-aggregation-v1";
            [DataMember] public long OriginalId { get; set; }
            [DataMember] public string OriginalHash { get; set; }
            [DataMember] public string PlanId { get; set; }
            [DataMember] public string RowsHash { get; set; }
            [DataMember] public AggregationSource[] Members { get; set; }
            [DataMember] public AggregationSource[] Contributors { get; set; }
        }
        [DataContract] private sealed class AggregationSource
        {
            [DataMember] public long Id { get; set; }
            [DataMember] public string PayloadHash { get; set; }
            [DataMember] public string ProofHash { get; set; }
        }
        private sealed class AggregationState { public string RowsJson { get; set; } public string RowsHash { get; set; } public int ExpectedParts { get; set; } public AggregationMember[] Members { get; set; } public AggregationContributor[] Contributors { get; set; } }
        private sealed class AggregationMember { public long Id { get; set; } public string PayloadHash { get; set; } public string PayloadJson { get; set; } public string Status { get; set; } public string AckJson { get; set; } }
        private sealed class AggregationContributor { public long Id { get; set; } public string PayloadHash { get; set; } public string PayloadJson { get; set; } public string Status { get; set; } public string OriginShopId { get; set; } public string OriginShopCode { get; set; } public string ReceiptJson { get; set; } }
        private sealed class AggregationIntent { public PosCatalogImportItemRequest[] Items { get; set; } public CatalogImportRemoteProductId[] Products { get; set; } public CatalogImportRemotePriceId[] Prices { get; set; } public bool IsMember { get; set; } public bool HasRecoveredIntent { get; set; } }
    }
}
