using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    /// <summary>The same scoped transport result for foreground and background history callers.</summary>
    public static class HistoricalCloudResponseMapper
    {
        public static HistoricalCloudResult RequireResult(PosOnlineResult<HistoricalCloudResponse> response,
            string shopId,string deviceId,string sessionId)
        {
            if(response?.Success!=true)
            {
                if(response?.Code=="history_resnapshot_required")throw new HistoricalCloudResnapshotRequiredException();
                throw new HistoricalCloudRequestException(response?.Code,response?.Denied==true,response?.Retryable==true);
            }
            var body=response.Value;
            if(body?.Ok!=true||body.ShopId!=shopId||body.DeviceId!=deviceId||body.SessionId!=sessionId||body.Result==null)
                throw new HistoricalCloudRequestException("history_response_invalid",false,false);
            return body.Result;
        }
    }
}
