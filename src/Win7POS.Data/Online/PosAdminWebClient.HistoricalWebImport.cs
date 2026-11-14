using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
namespace Win7POS.Data.Online
{
    public sealed partial class PosAdminWebClient
    {
        public Task<PosOnlineResult<HistoricalWebCloudResponse>> HistoricalWebImportAsync(HistoricalCloudRequest request,CancellationToken ct)
            =>PostJsonAsync<HistoricalCloudRequest,HistoricalWebCloudResponse>("/api/pos/payment-history",request,ct,noStore:true);
    }
}
