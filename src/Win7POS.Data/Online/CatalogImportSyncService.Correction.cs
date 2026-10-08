using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportSyncService
    {
        private async Task<bool> SyncCorrectionAsync(PosAdminWebClient client,PosTrustedDeviceSession session,
            CatalogImportOutboxItem item,int attempt,DrainAccumulator run,OnlineSyncAttemptFence fence,
            OnlineSyncLaneExecutionContext executionContext,CancellationToken token)
        {
            var validation=CatalogImportCorrectionTransport.ValidateSaved(item);
            if (!string.IsNullOrEmpty(validation))
            {
                await BlockCorrectionAsync(item,attempt,validation,SyncFailureKind.LocalValidation,run,fence).ConfigureAwait(false);
                return false;
            }
            PosCatalogImportCorrectionRequest request;
            try { request=CatalogImportCorrectionTransport.BuildTransportRequest(item,session); }
            catch(CatalogImportRecoveryException ex)
            {
                if(ex.Code=="authentication_required" || IsAuthDenied(ex.Code))
                    return await MarkRemoteFailureAsync(item,attempt,ex.Code,true,run,fence).ConfigureAwait(false);
                await BlockCorrectionAsync(item,attempt,ex.Code,SyncFailureKind.LocalValidation,run,fence).ConfigureAwait(false);
                return false;
            }
            DemandTransportSize(request);
            if (!await _outbox.RecordDispatchAsync(item,attempt,fence).ConfigureAwait(false))
            {
                run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain,"catalog_import_dispatch_fence_lost");
                return false;
            }
            var response=executionContext==null
                ? await client.CatalogImportCorrectionAsync(request,token).ConfigureAwait(false)
                : await SendCorrectionWithFreshCredentialsAsync(client,request,executionContext,token).ConfigureAwait(false);
            var code=FirstNonEmpty(response.Value?.Code,response.Code);
            if (response.Denied || IsAuthDenied(code))
                return await MarkRemoteFailureAsync(item,attempt,code,true,run,fence).ConfigureAwait(false);
            if (!response.Success || response.Value==null || !response.Value.Ok)
            {
                if (IsCorrectionConflict(code) || IsCorrectionConflict(response.Value?.Status))
                {
                    await BlockCorrectionAsync(item,attempt,SafeDiagnosticCode(FirstNonEmpty(code,response.Value?.Status)),
                        SyncFailureKind.PermanentRemote,run,fence).ConfigureAwait(false);
                    return false;
                }
                return await MarkRemoteFailureAsync(item,attempt,code,false,run,fence).ConfigureAwait(false);
            }
            CatalogImportAckResult ack;
            try { ack=CatalogImportCorrectionTransport.ValidateResponse(item,request,response.Value); }
            catch(CatalogImportRecoveryException ex)
            {
                await BlockCorrectionAsync(item,attempt,ex.Code,SyncFailureKind.PermanentRemote,run,fence).ConfigureAwait(false);
                return false;
            }
            ack.ServerRequestId=response.ServerRequestId;
            if (await _outbox.MarkAckedAsync(item.Id,ack,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),attempt,fence).ConfigureAwait(false))
                run.Acked++;
            else run.SetFailureIfNone(SyncFailureKind.ConcurrentDrain,"catalog_import_ack_cas_lost");
            return false;
        }

        private async Task BlockCorrectionAsync(CatalogImportOutboxItem item,int attempt,string code,SyncFailureKind kind,
            DrainAccumulator run,OnlineSyncAttemptFence fence)
        {
            if (await _outbox.MarkBlockedAsync(item.Id,code,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),attempt,fence).ConfigureAwait(false))
            { run.Blocked++;run.SetFailure(kind,code); }
        }

        private static bool IsCorrectionConflict(string code) => code=="conflict" || code=="revision_conflict" ||
            code=="recovery_conflict" || code=="retired" || code=="payload_hash_mismatch";

        private static async Task<PosOnlineResult<PosCatalogImportCorrectionResponse>> SendCorrectionWithFreshCredentialsAsync(
            PosAdminWebClient client,PosCatalogImportCorrectionRequest request,OnlineSyncLaneExecutionContext context,CancellationToken token)
        {
            for(var attempt=0;attempt<2;attempt++)
            {
                try
                {
                    return await context.ExecuteCredentialedRequestAsync((credentials,ct)=>
                    {
                        request.DeviceToken=credentials.DeviceToken;request.SessionToken=credentials.SessionToken;
                        request.PosSessionId=credentials.PosSessionId;request.ShopDeviceId=credentials.ShopDeviceId;
                        DemandTransportSize(request);
                        return client.CatalogImportCorrectionAsync(request,ct);
                    },response=>response!=null && (response.Denied || IsAuthDenied(response.Value?.Code) || IsAuthDenied(response.Code))
                        ? "auth_denied" : string.Empty,token).ConfigureAwait(false);
                }
                catch(OnlineSyncCredentialsChangedException) when(attempt==0) { }
            }
            throw new OnlineSyncCredentialsChangedException();
        }

        internal static void DemandTransportSize<T>(T request)
        {
            if(System.Text.Encoding.UTF8.GetByteCount(CatalogImportRecoveryService.Serialize(request))>512*1024)
                throw new CatalogImportRecoveryException("recovery_payload_too_large");
        }
    }
}
