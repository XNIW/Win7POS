using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
namespace Win7POS.Data.Online
{
    public sealed partial class PosAdminWebClient
    {
        public Task<PosOnlineResult<HistoricalCloudResponse>> HistoricalCloudAsync(HistoricalCloudRequest request,CancellationToken ct)
            =>PostJsonAsync<HistoricalCloudRequest,HistoricalCloudResponse>("/api/pos/payment-history",request,ct,noStore:true);
    }
}
