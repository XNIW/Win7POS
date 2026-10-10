using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportRecoveryService
    {
        public Task<Win7POS.Data.Import.SupplierExcelImportApplyResult> ReconcileAcceptedReplacementAsync(CatalogImportRecoveryDraft draft,
            Func<bool> authorizeCommit,OnlineSyncGeneration generation,CancellationToken cancellationToken)
        {
            if(draft==null || !draft.RequiresAcceptedReconciliation || draft.InitialIntentRequest==null)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            // Preserve editable draft rows; acknowledging an existing accepted
            // operation never means publishing a newly entered correction.
            return CommitAsync(draft,draft.InitialIntentRequest.Items.Select(ToEditable).ToArray(),authorizeCommit,generation,cancellationToken);
        }

        private async Task<CatalogImportRecoveryDraft> PrepareReplacementAsync(CatalogImportRecoveryDraft draft,
            PosAdminWebOptions options,PosTrustedDeviceSession session,CancellationToken token)
        {
            var target=await Task.Run(async()=>
            {
                using(var conn=_factory.Open())
                {
                    var blocked = await FindBlockedReplacementAsync(conn,draft.Original.Id,null).ConfigureAwait(false);
                    var id=blocked ?? draft.Original.ReplacementId.Value;
                    for(var depth=0;depth<128;depth++)
                    {
                        var row=await conn.QuerySingleOrDefaultAsync<CatalogImportRecoveryOriginal>(SelectOriginal+" WHERE o.id=@id",new { id }).ConfigureAwait(false);
                        if(row==null || (row.OperationType!="catalog_import_correction" && row.OperationType!="catalog_import") || row.Status!="failed_blocked") return null;
                        if(!row.ReplacementId.HasValue) {
                            if(row.OperationType=="catalog_import_correction") row.SharedProof=await CatalogImportCorrectionSharedProof.LoadAsync(conn,null,row.PayloadJson).ConfigureAwait(false);
                            return row;
                        }
                        if(row.ReplacementId.Value<=row.Id) throw new CatalogImportRecoveryException("recovery_state_changed");
                        id=row.ReplacementId.Value;
                    }
                    throw new CatalogImportRecoveryException("recovery_state_changed");
                }
            },token).ConfigureAwait(false);
            if(target==null) return draft; // Pending/retry children continue through the normal drain.
            if(target.OperationType=="catalog_import")
                return await PrepareAsync(target.Id,options,session,draft.Generation,token).ConfigureAwait(false);
            if(options==null || session==null) throw new CatalogImportRecoveryException("authentication_required");
            await InitializeReplacementDraftAsync(draft,target).ConfigureAwait(false);
            using(var client=new PosAdminWebClient(options))
            {
                var response=await QueryTargetReceiptAsync(draft,client,session,false,token).ConfigureAwait(false);
                if(!response.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(response,"receipt_unavailable"));
                ValidateTargetReceipt(draft,response.Value);
                await StoreTargetReceiptAsync(draft,response.Value,token).ConfigureAwait(false);
                if(response.Value.Status=="retired")
                    await RefreshRootReceiptAsync(draft,client,session,token).ConfigureAwait(false);
            }
            draft.Batch.CanRecoverReplacement=true;
            draft.CanCommit=draft.TargetReceipt.Status=="accepted" || draft.TargetReceipt.Status=="retired" && draft.ReceiptStatus=="accepted";
            await Task.Run(()=>LoadContributionsAsync(draft,options,session,token),token).ConfigureAwait(false);
            return draft;
        }

        private async Task InitializeReplacementDraftAsync(CatalogImportRecoveryDraft draft,CatalogImportRecoveryOriginal target)
        {
            var operation=CatalogImportCorrectionTransport.ReadSavedRequest(target.PayloadJson, target.SharedProof);
            if(CatalogImportOutboxPayloadBuilder.Sha256Hex(target.PayloadJson)!=target.PayloadHash ||
                operation.RecoveryOf.ClientImportId!=draft.Original.ClientImportId || operation.RecoveryOf.IdempotencyKey!=draft.Original.IdempotencyKey ||
                operation.RecoveryOf.PayloadHash!=draft.Original.PayloadHash || operation.Correction.ClientImportId!=target.ClientImportId ||
                operation.Correction.IdempotencyKey!=target.IdempotencyKey)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            draft.TargetOriginal=target;
            draft.TargetAckRequest=CatalogImportCorrectionTransport.ReadAckIntendedRequest(target.PayloadJson, target.SharedProof);
            using(var conn=_factory.Open())
            {
                await LoadSettledBaselineAsync(conn,null,draft).ConfigureAwait(false);
                draft.TargetPartOfGroup=await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_plan p
JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE m.outbox_id=@id AND p.total_parts>1",new { id=target.Id }).ConfigureAwait(false)>0;
            }
            draft.RemoteIntentBaseline=Deserialize<PosCatalogImportRequest>(Serialize(draft.InitialIntentRequest));
            var byId=draft.InitialIntentRequest.Items.ToDictionary(item=>item.ClientItemId,StringComparer.Ordinal);
            foreach(var item in operation.Correction.Items)
            {
                var row=byId[item.ClientItemId];
                if(item.Changes.RetailPrice.HasValue) row.RetailPrice=item.Changes.RetailPrice.Value.ToString(CultureInfo.InvariantCulture);
                if(item.Changes.PurchasePrice.HasValue) row.PurchasePrice=item.Changes.PurchasePrice.Value.ToString(CultureInfo.InvariantCulture);
                if(item.Changes.QuantityDelta.HasValue)
                {
                    decimal quantity;
                    if(!decimal.TryParse(row.Quantity,NumberStyles.Number,CultureInfo.InvariantCulture,out quantity)) quantity=0;
                    row.Quantity=(quantity+item.Changes.QuantityDelta.Value).ToString(CultureInfo.InvariantCulture);
                }
            }
            draft.Rows=draft.InitialIntentRequest.Items.Select(ToEditable).ToArray();
        }

        internal static Task<long?> FindBlockedReplacementAsync(Microsoft.Data.Sqlite.SqliteConnection conn,long root,Microsoft.Data.Sqlite.SqliteTransaction tx)
            => conn.ExecuteScalarAsync<long?>(@"WITH RECURSIVE links(parent,child) AS (
SELECT original_id,replacement_id FROM catalog_import_recovery WHERE replacement_id IS NOT NULL
UNION SELECT p.original_id,m.outbox_id FROM catalog_import_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE p.original_id IS NOT NULL),
descendants(id,depth) AS (SELECT child,1 FROM links WHERE parent=@root UNION ALL
SELECT l.child,d.depth+1 FROM descendants d JOIN links l ON l.parent=d.id WHERE d.depth<128 AND l.child>d.id)
SELECT o.id FROM descendants d JOIN catalog_import_outbox o ON o.id=d.id WHERE o.operation_type IN ('catalog_import','catalog_import_correction') AND o.status='failed_blocked'
AND NOT EXISTS(SELECT 1 FROM links WHERE parent=o.id)
AND NOT EXISTS(SELECT 1 FROM catalog_import_plan_part member JOIN catalog_import_plan plan ON plan.plan_id=member.plan_id
 JOIN catalog_import_recovery_supersession superseded ON superseded.predecessor_plan_id=json_extract(plan.remote_plan_json,'$.Document.planId')
 WHERE member.outbox_id=o.id AND superseded.successor_plan_id IS NOT NULL)
ORDER BY d.depth DESC,o.id LIMIT 1",new { root },tx);

        internal static Task<long> IsDescendantAsync(Microsoft.Data.Sqlite.SqliteConnection conn,long root,long target,Microsoft.Data.Sqlite.SqliteTransaction tx)
            => conn.ExecuteScalarAsync<long>(@"WITH RECURSIVE links(parent,child) AS (
SELECT original_id,replacement_id FROM catalog_import_recovery WHERE replacement_id IS NOT NULL
UNION SELECT p.original_id,m.outbox_id FROM catalog_import_plan p JOIN catalog_import_plan_part m ON m.plan_id=p.plan_id WHERE p.original_id IS NOT NULL),
descendants(id,depth) AS (SELECT child,1 FROM links WHERE parent=@root UNION ALL
SELECT l.child,d.depth+1 FROM descendants d JOIN links l ON l.parent=d.id WHERE d.depth<128 AND l.child>d.id)
SELECT COUNT(*) FROM descendants WHERE id=@target",new { root,target },tx);

        private async Task<CatalogImportRecoveryDraft> RetireReplacementAsync(CatalogImportRecoveryDraft draft,
            PosAdminWebOptions options,PosTrustedDeviceSession session,OnlineSyncGeneration generation,Func<bool> authorize,CancellationToken token)
        {
            DemandPermission(authorize);
            if(!draft.CanRetire || draft.Generation?.Fingerprint!=generation?.Fingerprint || draft.ShopDeviceId!=session?.ShopDeviceId)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            await Task.Run(async()=>
            {
                using(var conn=_factory.Open()) using(var tx=conn.BeginTransaction())
                    await new CatalogImportRecoveryCommit(draft,generation,authorize).ValidateAsync(conn,tx).ConfigureAwait(false);
            },token);
            DemandPermission(authorize);
            using(var client=new PosAdminWebClient(options))
            {
                var response=await QueryTargetReceiptAsync(draft,client,session,true,token,authorize);
                DemandPermission(authorize);
                if(!response.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(response,"receipt_retirement_unavailable"));
                ValidateTargetReceipt(draft,response.Value);
                if(response.Value.Status!="accepted" && response.Value.Status!="retired")
                    throw new CatalogImportRecoveryException("receipt_retirement_unavailable");
                await StoreTargetReceiptAsync(draft,response.Value,token);
                if(response.Value.Status=="retired") await RefreshRootReceiptAsync(draft,client,session,token);
                DemandPermission(authorize);
            }
            draft.TransportOptions=options;
            draft.CanCommit=draft.TargetReceipt.Status=="accepted" || draft.TargetReceipt.Status=="retired" && draft.ReceiptStatus=="accepted";
            return draft;
        }

        private async Task RefreshRootReceiptAsync(CatalogImportRecoveryDraft draft,PosAdminWebClient client,
            PosTrustedDeviceSession session,CancellationToken token)
        {
            var response=await QueryRootReceiptAsync(draft,client,session,false,token).ConfigureAwait(false);
            if(!response.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(response,"receipt_unavailable"));
            ValidateReceipt(draft,response.Value);
            if(response.Value.Status!="accepted") throw new CatalogImportRecoveryException("receipt_required");
            await StoreReceiptAsync(draft,response.Value,token).ConfigureAwait(false);
        }

        private async Task StoreTargetReceiptAsync(CatalogImportRecoveryDraft draft,PosCatalogImportReceiptResponse receipt,CancellationToken token)
        {
            await Task.Run(async()=>
            {
                using(var conn=_factory.Open()) using(var tx=conn.BeginTransaction())
                {
                    await new CatalogImportRecoveryCommit(draft,draft.Generation).ValidateAsync(conn,tx).ConfigureAwait(false);
                    await conn.ExecuteAsync(@"INSERT INTO catalog_import_recovery(original_id,delivery_known,dispatch_count,receipt_status,receipt_json,created_at,updated_at)
VALUES(@id,0,0,@status,@json,@now,@now) ON CONFLICT(original_id) DO UPDATE SET receipt_status=@status,receipt_json=@json,updated_at=@now;",
                        new { id=draft.TargetOriginal.Id,status=receipt.Status,json=Serialize(receipt),now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },tx).ConfigureAwait(false);
                    tx.Commit();
                    UpdateRevisionSummary(draft,draft.Receipt,receipt);
                }
            },token).ConfigureAwait(false);
            draft.TargetReceipt=receipt;
        }

        private static void ValidateTargetReceipt(CatalogImportRecoveryDraft draft,PosCatalogImportReceiptResponse receipt)
        {
            ValidateReceiptIdentity(new CatalogImportRecoveryDraft { Original=draft.TargetOriginal,OriginalRequest=draft.TargetAckRequest,
                ShopDeviceId=draft.ShopDeviceId },receipt);
            if(receipt.Status=="accepted")
            {
                var saved=draft.TargetOriginal;
                var item=new CatalogImportOutboxItem { SharedProof=saved.SharedProof,Id=saved.Id,OperationType=saved.OperationType,
                    SchemaVersion=PosCatalogImportCorrectionContract.SchemaVersion,PayloadHash=saved.PayloadHash,PayloadJson=saved.PayloadJson,
                    ClientImportId=saved.ClientImportId,IdempotencyKey=saved.IdempotencyKey,
                    OriginShopId=saved.OriginShopId,OriginShopCode=saved.OriginShopCode };
                var request=CatalogImportCorrectionTransport.ReadSavedRequest(saved.PayloadJson, saved.SharedProof);
                request.ShopDeviceId=draft.ShopDeviceId;
                request.Correction.PayloadHash=saved.PayloadHash;
                request.RecoveryOf.OriginalRequest.PayloadHash=request.RecoveryOf.PayloadHash;
                draft.TargetAck=CatalogImportCorrectionTransport.ValidateResponse(item,request,new PosCatalogImportCorrectionResponse
                { Ok=receipt.Ok,Code="success",SchemaVersion=PosCatalogImportCorrectionContract.SchemaVersion,Status="accepted",
                    ShopId=receipt.ShopId,ShopDeviceId=receipt.ShopDeviceId,ClientImportId=receipt.ClientImportId,IdempotencyKey=receipt.IdempotencyKey,
                    PayloadHash=receipt.PayloadHash,CanonicalPayloadHash=receipt.CanonicalPayloadHash,Receipt=receipt.Receipt });
            }
        }

        private static PosCatalogImportCorrectionReceiptRequest BuildReplacementReceiptRequest(CatalogImportRecoveryDraft draft,PosTrustedDeviceSession session)
        {
            var operation=CatalogImportCorrectionTransport.ReadSavedRequest(draft.TargetOriginal.PayloadJson, draft.TargetOriginal.SharedProof);
            operation.Correction.PayloadHash=draft.TargetOriginal.PayloadHash;
            operation.RecoveryOf.OriginalRequest.PayloadHash=operation.RecoveryOf.PayloadHash;
            return new PosCatalogImportCorrectionReceiptRequest { ClientImportId=draft.TargetOriginal.ClientImportId,
                IdempotencyKey=draft.TargetOriginal.IdempotencyKey,PayloadHash=draft.TargetOriginal.PayloadHash,OriginalRequest=operation,
                DeviceToken=session.DeviceToken,SessionToken=session.SessionToken,PosSessionId=session.PosSessionId,
                ShopDeviceId=session.ShopDeviceId,ShopCode=draft.Original.OriginShopCode };
        }

        private static CatalogImportOutboxPlan BuildCommitPlan(CatalogImportRecoveryDraft draft,Win7POS.Core.Import.SupplierImportSyncPreview preview)
        {
            if(draft.TargetReceipt?.Status=="accepted")
            {
                var originals=draft.InitialIntentRequest.Items.ToDictionary(item=>item.Barcode,StringComparer.Ordinal);
                foreach(var row in preview.ValidatedRows)
                {
                    var before=originals[row.Barcode];
                    if(!CatalogImportOutboxPayloadBuilder.EqualNumber(row.RetailPrice,before.RetailPrice) ||
                        !CatalogImportOutboxPayloadBuilder.EqualNumber(row.PurchasePrice,before.PurchasePrice) ||
                        !CatalogImportOutboxPayloadBuilder.EqualNumber(row.Quantity,before.Quantity))
                        throw new CatalogImportRecoveryException("recovery_state_changed");
                }
                return null;
            }
            if(draft.TargetOriginal!=null && draft.TargetPartOfGroup)
            {
                // Other parts may already be accepted or still own a pending
                // operation. A replacement edits only the retired child's rows.
                var owned=new System.Collections.Generic.HashSet<string>(draft.TargetAckRequest.Items.Select(i=>i.Barcode),StringComparer.Ordinal);
                var before=draft.InitialIntentRequest.Items.ToDictionary(i=>i.Barcode,StringComparer.Ordinal);
                foreach(var row in preview.ValidatedRows.Where(r=>!owned.Contains(r.Barcode)))
                {
                    var original=before[row.Barcode];
                    if(!CatalogImportOutboxPayloadBuilder.EqualNumber(row.RetailPrice,original.RetailPrice) ||
                        !CatalogImportOutboxPayloadBuilder.EqualNumber(row.PurchasePrice,original.PurchasePrice) ||
                        !CatalogImportOutboxPayloadBuilder.EqualNumber(row.Quantity,original.Quantity))
                        throw new CatalogImportRecoveryException("recovery_rows_changed");
                }
            }
            var carried=new System.Collections.Generic.HashSet<string>(draft.Supersession?.CarryCoverage.Select(c=>c.ClientItemId) ?? Array.Empty<string>(),StringComparer.Ordinal);
            return CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(preview,draft.OriginalRequest,draft.Original.PayloadHash,
                draft.ReceiptStatus=="accepted",draft.Contributions,draft.Receipt,draft.RemoteIntentBaseline ?? (draft.IsResumedSettledDraft ? draft.InitialIntentRequest : null),
                draft.Supersession==null ? null : draft.OriginalRequest.Items.Where(item=>carried.Contains(item.ClientItemId)).Select(item=>item.Barcode).ToArray(),draft.Supersession?.PlanId);
        }
    }
}
