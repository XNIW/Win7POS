using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    /// <summary>One bounded source POST to the configured Admin origin. No redirects, retries or backup upload.</summary>
    public sealed class HistoricalBaselineUploader:IDisposable
    {
        private readonly HttpClient _http;private readonly Uri _admin;
        public HistoricalBaselineUploader(PosAdminWebOptions options,HttpMessageHandler handler=null)
        {
            if(options==null)throw new ArgumentNullException(nameof(options));_admin=options.BaseUri;
            _http=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false},true)
                {Timeout=Timeout.InfiniteTimeSpan};
        }
        public async Task<string> UploadAsync(string uploadUrl,HistoricalBaselineFlow flow,OnlineSyncRequestCredentials credentials,Action demand,CancellationToken ct)
        {
            if(flow==null||credentials==null||demand==null)throw new ArgumentNullException();var uri=DemandUri(uploadUrl,_admin);
            if(credentials.ShopDeviceId!=flow.DeviceId||new[]{credentials.DeviceToken,credentials.SessionToken}.Any(v=>v.Length<20||v.Length>256||v.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c))))
                throw new HistoricalCloudRequestException("history_baseline_upload_target_invalid",false,false);
            demand();ct.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(()=>HistoricalBaselineStore.VerifyFiles(flow,ct),ct).ConfigureAwait(false);demand();
                using var file=new FileStream(flow.ArchivePath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
                if(file.Length<1||file.Length>HistoricalCloudExporter.MaximumArchiveBytes)throw new IOException("history_baseline_source_changed");
                var hash=await Task.Run(()=>{using var sha=SHA256.Create();return HistoricalPublicationOutbox.Hex(sha.ComputeHash(file));},ct).ConfigureAwait(false);
                if(hash!=flow.ArchiveSha256)throw new IOException("history_baseline_source_changed");file.Position=0;demand();ct.ThrowIfCancellationRequested();
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var request=new HttpRequestMessage(HttpMethod.Post,uri);
                request.Headers.CacheControl=new CacheControlHeaderValue{NoStore=true,MaxAge=TimeSpan.Zero};
                request.Headers.Add("x-history-device-id",credentials.ShopDeviceId);request.Headers.Add("x-history-session-id",credentials.PosSessionId);
                request.Headers.Add("x-history-device-token",credentials.DeviceToken);request.Headers.Add("x-history-session-token",credentials.SessionToken);
                request.Headers.Add("x-history-upload-id",flow.UploadId);
                request.Content=new StreamContent(file);request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/zip");request.Content.Headers.ContentLength=file.Length;
                using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
                demand();ct.ThrowIfCancellationRequested();
                var raw=await ReadResponseAsync(response,deadline.Token).ConfigureAwait(false);
                if(response.StatusCode==HttpStatusCode.Forbidden||response.StatusCode==HttpStatusCode.Unauthorized)
                {
                    var code=ReadAuthenticationCode(raw);if(code!=null)return code;
                    throw new HistoricalCloudRequestException("history_baseline_upload_denied",true,false);
                }
                // A transport ACK never verifies the archive or applies money.
                // The original worker must still perform its full hash check.
                if((response.StatusCode!=HttpStatusCode.OK&&response.StatusCode!=HttpStatusCode.Conflict)||!ReadAcknowledgement(raw,flow.UploadId,response.StatusCode==HttpStatusCode.Conflict))
                    throw new HistoricalCloudRequestException("history_baseline_upload_failed",false,(int)response.StatusCode>=500||response.StatusCode==HttpStatusCode.RequestTimeout||(int)response.StatusCode==429);
                return string.Empty;
            }
            catch(HttpRequestException){throw new HistoricalCloudRequestException("history_baseline_upload_unavailable",false,true);}
            catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new HistoricalCloudRequestException("history_timeout",false,true);}
            catch(IOException){throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
            catch(UnauthorizedAccessException){throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
            catch(InvalidOperationException e) when(e.Message=="history_baseline_source_changed")
            {throw new HistoricalCloudRequestException("history_baseline_source_unavailable",false,false);}
        }
        internal static Uri DemandUri(string value,Uri admin)
        {
            var expected=new Uri(admin,"/api/pos/payment-history/source");
            if(value==null||value.Length>8192||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||
                uri.Query.Length!=0||uri.AbsoluteUri!=expected.AbsoluteUri)
                throw new HistoricalCloudRequestException("history_baseline_upload_target_invalid",false,false);
            return uri;
        }
        private static async Task<string> ReadResponseAsync(HttpResponseMessage response,CancellationToken ct)
        {
            if(response.Content==null||response.Content.Headers.ContentType?.MediaType!="application/json"||response.Content.Headers.ContentLength>16384)return string.Empty;
            using var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false);using var bytes=new MemoryStream();var buffer=new byte[4096];
            while(true){var n=await input.ReadAsync(buffer,0,buffer.Length,ct).ConfigureAwait(false);if(n==0)break;if(bytes.Length+n>16384)return string.Empty;bytes.Write(buffer,0,n);}
            try{return new System.Text.UTF8Encoding(false,true).GetString(bytes.ToArray());}
            catch(System.Text.DecoderFallbackException){return string.Empty;}
        }
        private static bool ReadAcknowledgement(string raw,string upload,bool existing)
        {
            try
            {
                var p=HistoricalBaselineReceipt.Closed(HistoricalBaselineReceipt.ReadRoot(raw,16384),"ok","upload_id","already_present");
                return (string)p["ok"].Attribute("type")=="boolean"&&p["ok"].Value=="true"&&(string)p["upload_id"].Attribute("type")=="string"&&p["upload_id"].Value==upload&&
                    (string)p["already_present"].Attribute("type")=="boolean"&&p["already_present"].Value==(existing?"true":"false");
            }
            catch(Exception e) when(e is InvalidOperationException||e is System.Xml.XmlException||e is ArgumentException){return false;}
        }
        private static string ReadAuthenticationCode(string raw)
        {
            try
            {
                var root=HistoricalBaselineReceipt.ReadRoot(raw,16384);
                var p=HistoricalBaselineReceipt.Closed(root,root.Elements().Any(e=>e.Name.LocalName=="message")?new[]{"ok","code","message"}:new[]{"ok","code"});
                if((string)p["ok"].Attribute("type")=="boolean"&&p["ok"].Value=="false"&&(string)p["code"].Attribute("type")=="string"&&p["code"].Value=="auth_denied")return "auth_denied";
            }
            catch(Exception e) when(e is InvalidOperationException||e is System.Xml.XmlException||e is ArgumentException){ }
            return null;
        }
        public void Dispose()=>_http.Dispose();
    }
}
