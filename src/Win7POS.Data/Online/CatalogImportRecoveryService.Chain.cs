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
                    var id=draft.Original.ReplacementId.Value;
                    for(var depth=0;depth<128;depth++)
                    {
                        var row=await conn.QuerySingleOrDefaultAsync<CatalogImportRecoveryOriginal>(SelectOriginal+" WHERE o.id=@id",new { id }).ConfigureAwait(false);
                        if(row==null || row.OperationType!="catalog_import_correction" || row.Status!="failed_blocked") return null;
                        if(!row.ReplacementId.HasValue) return row;
                        if(row.ReplacementId.Value<=row.Id) throw new CatalogImportRecoveryException("recovery_state_changed");
                        id=row.ReplacementId.Value;
                    }
                    throw new CatalogImportRecoveryException("recovery_state_changed");
                }
            },token).ConfigureAwait(false);
            if(target==null) return draft; // Ordinary pending/retry child continues through the normal drain.
            if(options==null || session==null) throw new CatalogImportRecoveryException("authentication_required");
            var operation=CatalogImportCorrectionTransport.ReadSavedRequest(target.PayloadJson);
            if(CatalogImportOutboxPayloadBuilder.Sha256Hex(target.PayloadJson)!=target.PayloadHash ||
                operation.RecoveryOf.ClientImportId!=draft.Original.ClientImportId || operation.RecoveryOf.IdempotencyKey!=draft.Original.IdempotencyKey ||
                operation.RecoveryOf.PayloadHash!=draft.Original.PayloadHash || operation.Correction.ClientImportId!=target.ClientImportId ||
                operation.Correction.IdempotencyKey!=target.IdempotencyKey)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            draft.TargetOriginal=target;
            draft.TargetAckRequest=CatalogImportCorrectionTransport.ReadAckIntendedRequest(target.PayloadJson);
            draft.InitialIntentRequest=Deserialize<PosCatalogImportRequest>(Serialize(draft.OriginalRequest));
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
            using(var client=new PosAdminWebClient(options))
            {
                var request=BuildReplacementReceiptRequest(draft,session);
                CatalogImportSyncService.DemandTransportSize(request);
                var response=await client.CatalogImportReceiptAsync(request,token).ConfigureAwait(false);
                if(!response.Success) throw new CatalogImportRecoveryException(response.Denied ? "authentication_required" : "receipt_unavailable");
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
                var request=BuildReplacementReceiptRequest(draft,session);
                request.SchemaVersion=PosCatalogImportReceiptContract.RetirementSchemaVersion;
                CatalogImportSyncService.DemandTransportSize(request);
                var response=await client.CatalogImportRetireAsync(request,token);
                DemandPermission(authorize);
                if(!response.Success) throw new CatalogImportRecoveryException(response.Denied ? "authentication_required" : "receipt_retirement_unavailable");
                ValidateTargetReceipt(draft,response.Value);
                if(response.Value.Status!="accepted" && response.Value.Status!="retired")
                    throw new CatalogImportRecoveryException("receipt_retirement_unavailable");
                await StoreTargetReceiptAsync(draft,response.Value,token);
                if(response.Value.Status=="retired") await RefreshRootReceiptAsync(draft,client,session,token);
                DemandPermission(authorize);
            }
            draft.CanCommit=draft.TargetReceipt.Status=="accepted" || draft.TargetReceipt.Status=="retired" && draft.ReceiptStatus=="accepted";
            return draft;
        }

        private async Task RefreshRootReceiptAsync(CatalogImportRecoveryDraft draft,PosAdminWebClient client,
            PosTrustedDeviceSession session,CancellationToken token)
        {
            var request=BuildReceiptRequest(draft,session);
            CatalogImportSyncService.DemandTransportSize(request);
            var response=await client.CatalogImportReceiptAsync(request,token).ConfigureAwait(false);
            if(!response.Success) throw new CatalogImportRecoveryException(response.Denied ? "authentication_required" : "receipt_unavailable");
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
                var item=new CatalogImportOutboxItem { Id=saved.Id,OperationType=saved.OperationType,
                    SchemaVersion=PosCatalogImportCorrectionContract.SchemaVersion,PayloadHash=saved.PayloadHash,PayloadJson=saved.PayloadJson,
                    ClientImportId=saved.ClientImportId,IdempotencyKey=saved.IdempotencyKey,
                    OriginShopId=saved.OriginShopId,OriginShopCode=saved.OriginShopCode };
                var request=CatalogImportCorrectionTransport.ReadSavedRequest(saved.PayloadJson);
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
            var operation=CatalogImportCorrectionTransport.ReadSavedRequest(draft.TargetOriginal.PayloadJson);
            operation.Correction.PayloadHash=draft.TargetOriginal.PayloadHash;
            operation.RecoveryOf.OriginalRequest.PayloadHash=operation.RecoveryOf.PayloadHash;
            return new PosCatalogImportCorrectionReceiptRequest { ClientImportId=draft.TargetOriginal.ClientImportId,
                IdempotencyKey=draft.TargetOriginal.IdempotencyKey,PayloadHash=draft.TargetOriginal.PayloadHash,OriginalRequest=operation,
                DeviceToken=session.DeviceToken,SessionToken=session.SessionToken,PosSessionId=session.PosSessionId,
                ShopDeviceId=session.ShopDeviceId,ShopCode=draft.Original.OriginShopCode };
        }

        private static CatalogImportOutboxEntry BuildCommitEntry(CatalogImportRecoveryDraft draft,Win7POS.Core.Import.SupplierImportSyncPreview preview)
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
            var entry=CatalogImportOutboxPayloadBuilder.BuildRecoveryEntry(preview,draft.OriginalRequest,draft.Original.PayloadHash,
                draft.ReceiptStatus=="accepted",draft.Contributions,draft.Receipt);
            if(entry!=null)
            {
                string payload;
                if(entry.OperationType=="catalog_import_correction")
                    payload=Serialize(CatalogImportCorrectionTransport.BuildTransportRequest(new CatalogImportOutboxItem
                    { OperationType=entry.OperationType,SchemaVersion=entry.SchemaVersion,ClientImportId=entry.ClientImportId,
                        IdempotencyKey=entry.IdempotencyKey,PayloadJson=entry.PayloadJson,PayloadHash=entry.PayloadHash,
                        OriginShopId=draft.Original.OriginShopId,OriginShopCode=draft.Original.OriginShopCode },draft.TransportSession));
                else
                {
                    var request=Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
                    request.PayloadHash=entry.PayloadHash;
                    request.Batch.AttemptCount=int.MaxValue; // Reserve the full typed counter width, including later attempts.
                    request.DeviceToken=draft.TransportSession?.DeviceToken;request.SessionToken=draft.TransportSession?.SessionToken;
                    request.PosSessionId=draft.TransportSession?.PosSessionId;request.ShopDeviceId=draft.TransportSession?.ShopDeviceId;
                    request.ShopCode=draft.Original.OriginShopCode;
                    payload=Serialize(request);
                }
                if(System.Text.Encoding.UTF8.GetByteCount(payload)>512*1024)
                    throw new CatalogImportRecoveryException("recovery_payload_too_large");
            }
            return entry;
        }
    }
}
