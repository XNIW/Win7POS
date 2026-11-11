using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    /// <summary>One signed source PUT. No POS credentials, redirects, retries or backup upload.</summary>
    public sealed class HistoricalBaselineUploader:IDisposable
    {
        private readonly HttpClient _http;private readonly bool _loopback;
        public HistoricalBaselineUploader(PosAdminWebOptions options,HttpMessageHandler handler=null)
        {
            if(options==null)throw new ArgumentNullException(nameof(options));_loopback=options.BaseUri.IsLoopback;
            _http=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false},true)
                {Timeout=Timeout.InfiniteTimeSpan};
        }
        public async Task UploadAsync(string signedUrl,HistoricalBaselineFlow flow,Action demand,CancellationToken ct)
        {
            if(flow==null||demand==null)throw new ArgumentNullException();var uri=DemandUri(signedUrl,flow,_loopback);
            demand();ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(()=>HistoricalBaselineStore.VerifyFiles(flow,ct),ct).ConfigureAwait(false);demand();
                using var file=new FileStream(flow.ArchivePath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
                if(file.Length<1||file.Length>HistoricalCloudExporter.MaximumArchiveBytes)throw new IOException("history_baseline_source_changed");
                var hash=await Task.Run(()=>{using var sha=SHA256.Create();return HistoricalPublicationOutbox.Hex(sha.ComputeHash(file));},ct).ConfigureAwait(false);
                if(hash!=flow.ArchiveSha256)throw new IOException("history_baseline_source_changed");file.Position=0;demand();ct.ThrowIfCancellationRequested();
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var request=new HttpRequestMessage(HttpMethod.Put,uri);
                request.Headers.CacheControl=new CacheControlHeaderValue{NoStore=true,MaxAge=TimeSpan.Zero};request.Headers.TryAddWithoutValidation("x-upsert","false");
                request.Content=new StreamContent(file);request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/zip");request.Content.Headers.ContentLength=file.Length;
                using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
                demand();ct.ThrowIfCancellationRequested();
                // Existing object is never assumed valid: the next ordinary
                // step still requires the original worker's full hash check.
                if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.Conflict)
                    throw new HistoricalCloudRequestException("history_baseline_upload_failed",false,(int)response.StatusCode>=500||response.StatusCode==HttpStatusCode.RequestTimeout||(int)response.StatusCode==429);
            }
            catch(HttpRequestException){throw new HistoricalCloudRequestException("history_baseline_upload_unavailable",false,true);}
            catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new HistoricalCloudRequestException("history_timeout",false,true);}
            catch(IOException){throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
            catch(UnauthorizedAccessException){throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
            catch(InvalidOperationException e) when(e.Message=="history_baseline_source_changed")
            {throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
        }
        internal static Uri DemandUri(string value,HistoricalBaselineFlow f,bool allowLoopback)
        {
            var path="/storage/v1/object/upload/sign/payment-history-sources/history/"+f.ShopId+"/"+f.UploadId;
            if(value==null||value.Length>8192||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||
                uri.AbsolutePath!=path||!uri.Query.StartsWith("?token=",StringComparison.Ordinal)||uri.Query.Length<=7||uri.Query.IndexOf('&')>=0||
                !((uri.Scheme==Uri.UriSchemeHttps&&uri.IsDefaultPort&&uri.IdnHost.EndsWith(".supabase.co",StringComparison.OrdinalIgnoreCase))||
                  (allowLoopback&&uri.IsLoopback&&uri.Scheme==Uri.UriSchemeHttp)))
                throw new HistoricalCloudRequestException("history_baseline_upload_target_invalid",false,false);
            return uri;
        }
        public void Dispose()=>_http.Dispose();
    }
}
