using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    /// <summary>Access to an existing generation's bounded network gate, never a scheduler.</summary>
    public interface IOnlineSyncCredentialedRequestExecutor
    {
        Task<T> ExecuteCredentialedRequestAsync<T>(OnlineSyncGeneration expected,OnlineSyncLane lane,
            Func<OnlineSyncRequestCredentials,CancellationToken,Task<T>> request,
            Func<T,string> authenticationDenialCode,CancellationToken cancellationToken);
    }
}
