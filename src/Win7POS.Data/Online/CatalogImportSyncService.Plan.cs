using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportSyncService
    {
        private async Task<bool> SyncPlannedAsync(PosAdminWebClient client, PosTrustedDeviceSession session,
            CatalogImportOutboxItem item, CatalogImportSavedRemotePlan saved, int attempt, DrainAccumulator run,
            OnlineSyncAttemptFence fence, OnlineSyncLaneExecutionContext context, CancellationToken token)
        {
            var validation = item.OperationType == "catalog_import_correction" ? CatalogImportCorrectionTransport.ValidateSaved(item) :
                CatalogImportOutboxPayloadValidator.Validate(item).Code;
            if (!string.IsNullOrEmpty(validation))
            {
                await BlockCorrectionAsync(item,attempt,validation,SyncFailureKind.LocalValidation,run,fence).ConfigureAwait(false); return false;
            }
            var part = saved?.Receipt?.Parts?.SingleOrDefault(p => p.Index == saved.PartIndex);
            var projected = saved?.Document?.Parts?.SingleOrDefault(p => p.Index == saved.PartIndex);
            if (part == null || projected == null || saved.Receipt.PlanId != saved.Document.PlanId ||
                saved.Receipt.ShopId != session.ShopId || saved.Receipt.ShopDeviceId != session.ShopDeviceId ||
                part.ClientImportId != item.ClientImportId || part.IdempotencyKey != item.IdempotencyKey || part.DeclaredPayloadHash != item.PayloadHash)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var expected = item.OperationType == "catalog_import_correction"
                ? CatalogImportRecoveryProofTransport.ProjectionForTransport(CatalogImportCorrectionTransport.ReadSavedRequest(item.PayloadJson, item.SharedProof),saved.Document.VerifiedOriginalId)
                : CatalogImportRecoveryProofTransport.ProjectionForTransport(CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(item.PayloadJson),item.PayloadHash);
            if(expected.Correction != null) expected.Correction.PayloadHash=item.PayloadHash;
            if(CatalogImportRecoveryService.Serialize(expected)!=CatalogImportRecoveryService.Serialize(projected.Request))
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            if (!await _outbox.RecordDispatchAsync(item,attempt,fence).ConfigureAwait(false))
            { run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain,"catalog_import_dispatch_fence_lost"); return false; }
            var response = context == null
                ? await SendPlannedAsync(client,saved,session,token).ConfigureAwait(false)
                : await SendPlannedWithFreshCredentialsAsync(client,saved,session,context,token).ConfigureAwait(false);
            var code=FirstNonEmpty(response.Value?.Code,response.Code);
            if(!response.Success || response.Value?.Ok!=true)
                return await MarkRemoteFailureAsync(item,attempt,code,response.Denied,run,fence).ConfigureAwait(false);
            var receipt=response.Value;
            if(receipt.Status!="accepted")
            {
                await BlockCorrectionAsync(item,attempt,SafeDiagnosticCode(FirstNonEmpty(receipt.Reason,receipt.Status)),SyncFailureKind.PermanentRemote,run,fence).ConfigureAwait(false);
                return false;
            }
            if(receipt.SchemaVersion!=PosCatalogImportRecoveryMultipartContract.SchemaVersion || receipt.ShopId!=session.ShopId || receipt.ShopDeviceId!=session.ShopDeviceId ||
                receipt.PlanId!=saved.Document.PlanId || receipt.PartIndex!=saved.PartIndex || receipt.PartCount!=saved.Document.Parts.Length ||
                receipt.ClientImportId!=item.ClientImportId || receipt.IdempotencyKey!=item.IdempotencyKey || receipt.PayloadHash!=item.PayloadHash ||
                receipt.CanonicalPayloadHash!=part.PayloadHash || receipt.AcceptedPartCount<1 || receipt.AcceptedPartCount>receipt.PartCount ||
                !receipt.AcceptedPartCount.HasValue || (receipt.ParentStatus!="partial" && receipt.ParentStatus!="complete") ||
                (receipt.ParentStatus=="complete")!=(receipt.AcceptedPartCount==receipt.PartCount))
                throw new CatalogImportRecoveryException("receipt_conflict");
            CatalogImportAckResult ack;
            if(item.OperationType=="catalog_import_correction")
            {
                var request=CatalogImportCorrectionTransport.BuildTransportRequest(item,session);
                ack=CatalogImportCorrectionTransport.ValidateResponse(item,request,new PosCatalogImportCorrectionResponse
                { Ok=true,Code="success",SchemaVersion=item.SchemaVersion,Status="accepted",ShopId=receipt.ShopId,ShopDeviceId=receipt.ShopDeviceId,
                    ClientImportId=receipt.ClientImportId,IdempotencyKey=receipt.IdempotencyKey,PayloadHash=receipt.PayloadHash,
                    CanonicalPayloadHash=receipt.CanonicalPayloadHash,Receipt=receipt.Receipt });
            }
            else
            {
                var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(item.PayloadJson);
                ack=CatalogImportRecoveryService.BuildPersistedAck(new CatalogImportRecoveryOriginal { Id=item.Id,ClientImportId=item.ClientImportId,
                    IdempotencyKey=item.IdempotencyKey,PayloadHash=item.PayloadHash },request,receipt.Receipt);
                CatalogImportOutboxRepository.EnsureRecoveryAckComplete(request,ack,true);
            }
            ack.ServerRequestId=response.ServerRequestId;
            if(await _outbox.MarkAckedAsync(item.Id,ack,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),attempt,fence).ConfigureAwait(false)) run.Acked++;
            else run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain,"catalog_import_ack_cas_lost");
            return false;
        }

        private static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> SendPlannedAsync(PosAdminWebClient client,
            CatalogImportSavedRemotePlan plan,PosTrustedDeviceSession session,CancellationToken token)
        {
            var request=CatalogImportRecoveryProofTransport.Authenticate(new PosCatalogImportRecoveryApplyRequest
                { PlanId=plan.Document.PlanId,PartIndex=plan.PartIndex },session);
            DemandTransportSize(request);
            var result=await client.CatalogImportRecoveryApplyAsync(request,token).ConfigureAwait(false);
            if(!result.Success || result.Value?.Status!="accepted") return result;
            var expected=plan.Document.Parts.Single(p=>p.Index==plan.PartIndex).Request;
            var count=expected.Correction?.Items?.Length ?? expected.Items?.Length ?? 0;
            if(count<1 || count>CatalogImportPlanBuilder.MaximumRowsPerRequest || result.Value.TotalItemCount!=count ||
                result.Value.Offset!=0 || result.Value.Limit!=count || result.Value.Complete!=true)
                throw new CatalogImportRecoveryException("receipt_incomplete");
            return await CatalogImportRecoveryProofTransport.ReadCompleteReceiptAsync(client,result.Value,()=>session,token).ConfigureAwait(false);
        }

        private static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> SendPlannedWithFreshCredentialsAsync(
            PosAdminWebClient client,CatalogImportSavedRemotePlan plan,PosTrustedDeviceSession session,OnlineSyncLaneExecutionContext context,CancellationToken token)
        {
            for(var attempt=0;attempt<2;attempt++)
            {
                try
                {
                    return await context.ExecuteCredentialedRequestAsync((credentials,ct)=>
                    {
                        var current=CatalogImportRecoveryService.SnapshotTransportSession(session);
                        current.DeviceToken=credentials.DeviceToken;current.SessionToken=credentials.SessionToken;
                        current.PosSessionId=credentials.PosSessionId;current.ShopDeviceId=credentials.ShopDeviceId;
                        return SendPlannedAsync(client,plan,current,ct);
                    },response=>response!=null && (response.Denied || IsAuthDenied(response.Value?.Code) || IsAuthDenied(response.Code))
                        ? "auth_denied" : string.Empty,token).ConfigureAwait(false);
                }
                catch(OnlineSyncCredentialsChangedException) when(attempt==0) { }
            }
            throw new OnlineSyncCredentialsChangedException();
        }
    }
}
