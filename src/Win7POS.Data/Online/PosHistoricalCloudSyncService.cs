using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Import;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    /// <summary>One history-domain turn under the existing supervisor/request gate.</summary>
    public sealed class PosHistoricalCloudSyncService
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly Func<HistoricalBaselineContext,HistoricalSalesDatasetState,CancellationToken,Task<HistoricalBaselinePreparation>> _prepareBaseline;
        public PosHistoricalCloudSyncService(SqliteConnectionFactory factory,
            Func<HistoricalBaselineContext,HistoricalSalesDatasetState,CancellationToken,Task<HistoricalBaselinePreparation>> prepareBaseline=null)
        {_factory=factory??throw new ArgumentNullException(nameof(factory));_prepareBaseline=prepareBaseline;}

        public async Task<OnlineSyncLaneOutcome> SynchronizeAsync(PosAdminWebOptions options,
            OnlineSyncLaneExecutionContext context,string buildSha,Func<bool> isCurrent,CancellationToken ct)
        {
            var stamp = _factory.AuthorityStamp;
            Action demand = () =>
            {
                ct.ThrowIfCancellationRequested();
                var current = _factory.AuthorityStamp;
                if (!isCurrent() || current.RestoreEpoch != stamp.RestoreEpoch || current.AuthorityInstanceEpoch != stamp.AuthorityInstanceEpoch)
                    throw new OnlineSyncGenerationChangedException();
            };
            async Task<HistoricalCloudResult> Request(string operation,HistoricalCloudPayload payload,CancellationToken token)
            {
                demand();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var client = new PosAdminWebClient(options);
                var response = await context.ExecuteCredentialedRequestAsync((credentials, requestToken) =>
                    client.HistoricalCloudAsync(new HistoricalCloudRequest
                    {
                        DeviceId=credentials.ShopDeviceId,SessionId=credentials.PosSessionId,
                        DeviceToken=credentials.DeviceToken,SessionToken=credentials.SessionToken,
                        Operation=operation,Payload=payload
                    },requestToken),
                    // Domain permission denial must not log the operator out.
                    // Genuine shared lease denials stop this generation too.
                    result => result!=null && SharedAuthStopPolicy.IsAuthenticationDenied(result.Code) ? result.Code : string.Empty,
                    deadline.Token).ConfigureAwait(false);
                demand();
                return HistoricalCloudResponseMapper.RequireResult(response,context.Generation.ShopId,
                    context.Generation.ShopDeviceId,context.Generation.PosSessionId);
            }
            try
            {
                var outboundMore=false;HistoricalCloudRequestException outboundFailure=null;
                try
                {
                    outboundMore=await new HistoricalPublicationSender(new HistoricalPublicationOutbox(_factory),context.Generation,demand,Request).RunAsync(ct).ConfigureAwait(false);
                    outboundMore=await new HistoricalPublicationOutcomeSender(new HistoricalPublicationJournal(_factory),context.Generation,demand,Request).RunAsync(ct).ConfigureAwait(false)||outboundMore;
                    if(_prepareBaseline!=null)
                    {
                        async Task Upload(string url,HistoricalBaselineFlow flow,CancellationToken token)
                        {
                            demand();using var uploader=new HistoricalBaselineUploader(options);
                            var code=await context.ExecuteCredentialedRequestAsync((credentials,requestToken)=>uploader.UploadAsync(url,flow,credentials,demand,requestToken),
                                value=>SharedAuthStopPolicy.IsAuthenticationDenied(value)?value:string.Empty,token).ConfigureAwait(false);demand();
                            if(!string.IsNullOrEmpty(code))throw new HistoricalCloudRequestException(code,true,false);
                        }
                        outboundMore=await new HistoricalBaselineSynchronizer(_factory,context.Generation,demand,Request,_prepareBaseline,Upload).RunAsync(ct).ConfigureAwait(false)||outboundMore;
                    }
                }
                catch(HistoricalCloudRequestException error) { demand();outboundFailure=error; }
                var store = new HistoricalReplicaStore(_factory,demand,context.Generation);
                var more = await new HistoricalReplicaSynchronizer(store,demand,Request).RunAsync(context.Generation.ShopId,buildSha,ct).ConfigureAwait(false);
                if(outboundFailure!=null)return new OnlineSyncLaneOutcome(false,outboundFailure.Code,offline:outboundFailure.Retryable,
                    nextRetryAt:outboundFailure.Denied?DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds():(long?)null);
                return new OnlineSyncLaneOutcome(true,"history_replica_turn_complete",hasImmediateMore:more||outboundMore);
            }
            catch(HistoricalCloudRequestException e)
            {
                // Preserve the durable pending state. A permission change may be
                // discovered on the normal bounded cadence without affecting sales.
                return new OnlineSyncLaneOutcome(false,e.Code,offline:e.Retryable,
                    nextRetryAt:e.Denied?DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds():(long?)null);
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            { return new OnlineSyncLaneOutcome(false,"history_timeout",offline:true); }
        }
    }
}
