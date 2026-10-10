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
        internal static async Task FinalizeRecoveredConvergenceMembershipAsync(SqliteConnection conn,SqliteTransaction tx,
            CatalogImportRecoveryDraft draft,CatalogImportSavedRemotePlan convergence,long now)
        {
            if(await conn.ExecuteScalarAsync<long>(@"SELECT (SELECT COUNT(*) FROM catalog_import_plan_part WHERE outbox_id=@id)+
(SELECT COUNT(*) FROM catalog_import_recovery WHERE replacement_id=@id AND resolved_at IS NULL)",new { id=draft.Original.Id },tx).ConfigureAwait(false)==0) return;
            var evidence=await CatalogImportRecoveryService.BuildConvergenceEvidenceAsync(conn,tx,draft,convergence).ConfigureAwait(false);
            var composite=await ReadConvergenceEvidenceAsync(conn,tx,evidence).ConfigureAwait(false);
            await CompleteRecoveryAsync(conn,tx,draft.Original.Id,composite.Ack,now,false,composite.Intent,
                CatalogImportRecoveryService.Serialize(evidence)).ConfigureAwait(false);
        }

        internal static async Task ValidateConvergenceMembershipAsync(SqliteConnection conn,SqliteTransaction tx,long expectedOutboxId,string ackJson)
        {
            if(ackJson==null) return;
            var saved=CatalogImportRecoveryService.Deserialize<PlanAck>(ackJson);
            if(saved.AggregationJson==null && (saved.ConvergenceJson==null ||
                CatalogImportRecoveryService.Deserialize<CatalogImportConvergenceEvidence>(saved.ConvergenceJson).OriginalId!=expectedOutboxId) &&
                await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_plan p
JOIN catalog_import_outbox o ON o.id=p.original_id LEFT JOIN catalog_import_recovery r ON r.original_id=o.id
WHERE p.original_id=@expectedOutboxId AND p.completed_at IS NOT NULL AND o.status='recovered' AND o.operation_type='catalog_import'
AND COALESCE(r.receipt_status,'')<>'accepted'",new { expectedOutboxId },tx).ConfigureAwait(false)>0)
                throw new CatalogImportRecoveryException("receipt_incomplete");
            await ValidateRecoveryAggregationAsync(conn,tx,expectedOutboxId,saved).ConfigureAwait(false);
            if(saved.ConvergenceJson==null && saved.ConvergenceHash==null) return;
            if(saved.ConvergenceJson==null || CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.ConvergenceJson)!=saved.ConvergenceHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var evidence=CatalogImportRecoveryService.Deserialize<CatalogImportConvergenceEvidence>(saved.ConvergenceJson);
            if(evidence.OriginalId!=expectedOutboxId &&
                await CatalogImportRecoveryService.IsDescendantAsync(conn,expectedOutboxId,evidence.OriginalId,tx).ConfigureAwait(false)==0)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var checkedProof=await ReadConvergenceEvidenceAsync(conn,tx,evidence).ConfigureAwait(false);
            if(CatalogImportRecoveryService.Serialize(saved.Products)!=CatalogImportRecoveryService.Serialize(checkedProof.Ack.RemoteProductIds.ToArray()) ||
                CatalogImportRecoveryService.Serialize(saved.Prices)!=CatalogImportRecoveryService.Serialize(checkedProof.Ack.RemotePriceIds.ToArray()) ||
                CatalogImportRecoveryService.Serialize(saved.Intent)!=CatalogImportRecoveryService.Serialize(checkedProof.Intent.Items))
                throw new CatalogImportRecoveryException("receipt_conflict");
        }

        private static async Task<ConvergenceMappings> ReadConvergenceEvidenceAsync(SqliteConnection conn,SqliteTransaction tx,CatalogImportConvergenceEvidence evidence)
        {
            if(evidence?.SchemaVersion!="win7pos-local-convergence-proof-v1" || evidence.Convergence?.Document?.Mode!="replacement")
                throw new CatalogImportRecoveryException("receipt_conflict");
            var original=await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(CatalogImportRecoveryService.SelectOriginal+" WHERE o.id=@id",new { id=evidence.OriginalId },tx).ConfigureAwait(false);
            if(original.PayloadHash!=evidence.OriginalHash || CatalogImportOutboxPayloadBuilder.Sha256Hex(original.PayloadJson)!=original.PayloadHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var root=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(original.PayloadJson);
            var doc=evidence.Convergence.Document;var response=evidence.Convergence.Receipt;
            if(response?.Ok!=true || response.SchemaVersion!=PosCatalogImportRecoveryMultipartContract.SchemaVersion ||
                response.Status!="planned" || response.ParentStatus!="complete" || response.ItemCount!=0 || response.PartCount!=0 ||
                response.Parts?.Length!=0 || doc.Parts?.Length!=0 || response.PlanId!=doc.PlanId || response.VerifiedOriginalId!=doc.VerifiedOriginalId ||
                !CatalogImportRecoveryProofTransport.IsHash(response.PlanCanonicalHash) || response.ShopId!=original.OriginShopId ||
                doc.Supersedes==null || string.IsNullOrWhiteSpace(response.ShopDeviceId) ||
                root.Items.Length!=doc.Coverage?.Length || doc.Coverage.Select(row=>row.ClientItemId).Distinct(StringComparer.Ordinal).Count()!=root.Items.Length)
                throw new CatalogImportRecoveryException("receipt_incomplete");
            evidence.Convergence.LocalRecoveryRowsJson=evidence.RowsJson;
            var authoritative=await CatalogImportRecoveryService.BuildConvergenceEvidenceAsync(conn,tx,new CatalogImportRecoveryDraft
            { Original=original,OriginalRequest=root,ShopDeviceId=response.ShopDeviceId,TransportSession=new PosTrustedDeviceSession
                { ShopId=response.ShopId,ShopCode=original.OriginShopCode,ShopDeviceId=response.ShopDeviceId } },evidence.Convergence,false).ConfigureAwait(false);
            var authority=authoritative.Sources.ToDictionary(source=>source.Key,StringComparer.Ordinal);
            if(authority.Count!=evidence.Sources.Length) throw new CatalogImportRecoveryException("receipt_conflict");
            foreach(var source in evidence.Sources)
                if(!authority.TryGetValue(source.Key,out var match) || source.OutboxId!=match.OutboxId || source.PayloadHash!=match.PayloadHash ||
                    source.Receipt?.CanonicalPayloadHash!=match.Receipt.CanonicalPayloadHash ||
                    CatalogImportRecoveryService.Serialize(source.Receipt?.Receipt)!=CatalogImportRecoveryService.Serialize(match.Receipt.Receipt))
                    throw new CatalogImportRecoveryException("receipt_conflict");
            var active=CatalogImportRecoveryService.Deserialize<SupplierImportEditableRow[]>(evidence.RowsJson).ToDictionary(row=>row.Barcode,StringComparer.Ordinal);
            if(active.Count!=root.Items.Length) throw new CatalogImportRecoveryException("recovery_rows_changed");
            var sources=new Dictionary<string,ConvergenceSourceMappings>(StringComparer.Ordinal);
            foreach(var source in evidence.Sources)
            {
                var saved=await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(CatalogImportRecoveryService.SelectOriginal+" WHERE o.id=@id",new { id=source.OutboxId },tx).ConfigureAwait(false);
                if(saved.OperationType!="catalog_import" || saved.PayloadHash!=source.PayloadHash ||
                    CatalogImportOutboxPayloadBuilder.Sha256Hex(saved.PayloadJson)!=saved.PayloadHash || saved.OriginShopId!=original.OriginShopId ||
                    saved.OriginShopCode!=original.OriginShopCode || source.Receipt?.Status!="accepted")
                    throw new CatalogImportRecoveryException("receipt_conflict");
                var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(saved.PayloadJson);
                CatalogImportRecoveryService.ValidateReceipt(new CatalogImportRecoveryDraft { Original=saved,OriginalRequest=request,
                    ShopDeviceId=response.ShopDeviceId },source.Receipt);
                var ack=CatalogImportRecoveryService.BuildPersistedAck(saved,request,source.Receipt.Receipt);
                sources.Add(source.Key,new ConvergenceSourceMappings { Source=source,Request=request,Ack=ack,
                    Items=request.Items.ToDictionary(row=>row.ClientItemId,StringComparer.Ordinal),
                    // The compatibility ACK includes the same validated product
                    // mapping in both the explicit map and the item receipt.
                    Products=ack.RemoteProductIds.GroupBy(row=>row.ClientItemId,StringComparer.Ordinal)
                        .ToDictionary(group=>group.Key,group=>group.First(),StringComparer.Ordinal),
                    Prices=ack.RemotePriceIds.GroupBy(row=>row.ClientItemId,StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.Ordinal) });
            }
            var coverage=doc.Coverage.ToDictionary(row=>row.ClientItemId,StringComparer.Ordinal);
            var products=new List<CatalogImportRemoteProductId>();var prices=new List<CatalogImportRemotePriceId>();
            var intent=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(CatalogImportRecoveryService.Serialize(root));
            foreach(var item in intent.Items)
            {
                if(!coverage.TryGetValue(item.ClientItemId,out var proof) || !active.TryGetValue(item.Barcode,out var row) || row.RowNumber!=item.RowNumber || row.IsSkipped)
                    throw new CatalogImportRecoveryException("receipt_incomplete");
                var key=ConvergenceSourceKey(proof,doc);
                if(!sources.TryGetValue(key,out var source) || !source.Items.TryGetValue(proof.ContributorClientItemId??"",out var sourceItem) || sourceItem.Barcode!=item.Barcode)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                if(proof.Kind=="accepted_contributor" && CatalogImportRecoveryProofTransport.CreateUploadId(response.ShopId,response.ShopDeviceId,
                    "sha256:"+source.Source.PayloadHash,"original")!=proof.VerifiedContributorId)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                item.ProductName=row.ProductName;item.SecondProductName=row.SecondProductName;item.ItemNumber=row.ItemNumber;
                item.PurchasePrice=row.PurchasePrice;item.RetailPrice=row.RetailPrice;item.Quantity=row.Quantity;item.Supplier=row.Supplier;item.Category=row.Category;
                if(!CatalogImportOutboxPayloadBuilder.SameIntent(item,sourceItem)) throw new CatalogImportRecoveryException("receipt_conflict");
                var product=source.Products[sourceItem.ClientItemId];
                products.Add(new CatalogImportRemoteProductId { ClientItemId=item.ClientItemId,Barcode=item.Barcode,RemoteProductId=product.RemoteProductId });
                if(source.Prices.TryGetValue(sourceItem.ClientItemId,out var itemPrices))
                    prices.AddRange(itemPrices.Select(price=>new CatalogImportRemotePriceId { ClientItemId=item.ClientItemId,Barcode=item.Barcode,
                        PriceType=price.PriceType,RemotePriceId=price.RemotePriceId }));
            }
            var aggregate=new CatalogImportAckResult { RemoteProductIds=products.ToArray(),RemotePriceIds=prices.ToArray() };
            EnsureRecoveryAckComplete(intent,aggregate,true);
            return new ConvergenceMappings { Intent=intent,Ack=aggregate };
        }

        internal static string ConvergenceSourceKey(PosCatalogImportRecoveryCoverage coverage,PosCatalogImportRecoveryPlanDocument document)
        {
            if(coverage.Kind=="accepted_plan_part" && coverage.ContributorPartIndex.HasValue)
                return "plan:"+(coverage.ContributorPlanId??document.Supersedes?.PlanId)+":"+coverage.ContributorPartIndex.Value;
            if(coverage.Kind=="accepted_contributor" && coverage.VerifiedContributorId!=null) return "contributor:"+coverage.VerifiedContributorId;
            throw new CatalogImportRecoveryException("receipt_conflict");
        }
        private sealed class ConvergenceMappings { public PosCatalogImportRequest Intent { get; set; } public CatalogImportAckResult Ack { get; set; } }
        private sealed class ConvergenceSourceMappings
        {
            public CatalogImportConvergenceSource Source { get; set; } public PosCatalogImportRequest Request { get; set; } public CatalogImportAckResult Ack { get; set; }
            public Dictionary<string,PosCatalogImportItemRequest> Items { get; set; }
            public Dictionary<string,CatalogImportRemoteProductId> Products { get; set; }
            public Dictionary<string,CatalogImportRemotePriceId[]> Prices { get; set; }
        }
    }

    [DataContract] internal sealed class CatalogImportConvergenceEvidence
    {
        [DataMember] public string SchemaVersion { get; set; }="win7pos-local-convergence-proof-v1";
        [DataMember] public long OriginalId { get; set; }
        [DataMember] public string OriginalHash { get; set; }
        [DataMember] public string RowsJson { get; set; }
        [DataMember] public CatalogImportSavedRemotePlan Convergence { get; set; }
        [DataMember] public CatalogImportConvergenceSource[] Sources { get; set; }
    }
    [DataContract] internal sealed class CatalogImportConvergenceSource
    {
        [DataMember] public string Key { get; set; }
        [DataMember] public long OutboxId { get; set; }
        [DataMember] public string PayloadHash { get; set; }
        [DataMember] public PosCatalogImportReceiptResponse Receipt { get; set; }
    }

    public sealed partial class CatalogImportRecoveryService
    {
        internal static async Task<CatalogImportConvergenceEvidence> BuildConvergenceEvidenceAsync(SqliteConnection conn,SqliteTransaction tx,
            CatalogImportRecoveryDraft draft,CatalogImportSavedRemotePlan convergence,bool requireLocallyAcknowledged=true)
        {
            if(convergence?.LocalRecoveryRowsJson==null || convergence.Document.Mode!="replacement") throw new CatalogImportRecoveryException("receipt_required");
            var sources=new List<CatalogImportConvergenceSource>();
            var plans=new Dictionary<string,CatalogImportRecoverySupersession>(StringComparer.Ordinal);
            foreach(var group in convergence.Document.Coverage.GroupBy(row=>CatalogImportOutboxRepository.ConvergenceSourceKey(row,convergence.Document)))
            {
                var coverage=group.First();CatalogImportRecoveryOriginal owner;PosCatalogImportReceiptResponse receipt;
                if(coverage.Kind=="accepted_plan_part")
                {
                    var id=coverage.ContributorPlanId??convergence.Document.Supersedes.PlanId;
                    if(!plans.TryGetValue(id,out var state))
                    {
                        var saved=await conn.QuerySingleAsync<SupersessionRow>(@"SELECT predecessor_plan_id AS PlanId,original_hash AS OriginalHash,
archive_json AS ArchiveJson,archive_hash AS ArchiveHash,settlement_json AS SettlementJson,settlement_hash AS SettlementHash,
finalized_at AS FinalizedAt,successor_plan_id AS SuccessorPlanId FROM catalog_import_recovery_supersession WHERE predecessor_plan_id=@id AND original_id=@root",
                            new { id,root=draft.Original.Id },tx).ConfigureAwait(false);
                        state=ParseSupersession(draft,saved,draft.TransportSession);plans.Add(id,state);
                    }
                    var accepted=state.AcceptedEntries.SingleOrDefault(part=>part.PartIndex==coverage.ContributorPartIndex);
                    if(accepted==null || accepted.Entry.OperationType!="catalog_import") throw new CatalogImportRecoveryException("receipt_required");
                    owner=await conn.QuerySingleAsync<CatalogImportRecoveryOriginal>(SelectOriginal+@" WHERE o.client_import_id=@ClientImportId AND o.idempotency_key=@IdempotencyKey
AND o.payload_hash=@PayloadHash AND o.payload_json=@PayloadJson",new { accepted.Entry.ClientImportId,accepted.Entry.IdempotencyKey,accepted.Entry.PayloadHash,accepted.Entry.PayloadJson },tx).ConfigureAwait(false);
                    receipt=accepted.Receipt;
                }
                else
                {
                    owner=null;receipt=null;
                    var candidates=await conn.QueryAsync<CatalogImportRecoveryOriginal>(SelectOriginal+@" JOIN catalog_import_recovery_contributions c ON c.contributor_id=o.id
WHERE c.original_id=@root AND o.operation_type='catalog_import'",new { root=draft.Original.Id },tx).ConfigureAwait(false);
                    foreach(var candidate in candidates)
                        if(CatalogImportRecoveryProofTransport.CreateUploadId(draft.TransportSession.ShopId,draft.ShopDeviceId,"sha256:"+candidate.PayloadHash,"original")==coverage.VerifiedContributorId)
                        {
                            if(owner!=null) throw new CatalogImportRecoveryException("receipt_conflict");owner=candidate;
                            receipt=Deserialize<PosCatalogImportReceiptResponse>(await conn.ExecuteScalarAsync<string>("SELECT receipt_json FROM catalog_import_recovery_contributions WHERE original_id=@root AND contributor_id=@id AND payload_hash=@hash",
                                new { root=draft.Original.Id,id=candidate.Id,hash=candidate.PayloadHash },tx).ConfigureAwait(false));
                        }
                }
                if(owner==null || requireLocallyAcknowledged && owner.Status!="acked") throw new CatalogImportRecoveryException("receipt_required");
                sources.Add(new CatalogImportConvergenceSource { Key=group.Key,OutboxId=owner.Id,PayloadHash=owner.PayloadHash,Receipt=receipt });
            }
            return new CatalogImportConvergenceEvidence { OriginalId=draft.Original.Id,OriginalHash=draft.Original.PayloadHash,RowsJson=convergence.LocalRecoveryRowsJson,
                Convergence=convergence,Sources=sources.ToArray() };
        }
    }
}
