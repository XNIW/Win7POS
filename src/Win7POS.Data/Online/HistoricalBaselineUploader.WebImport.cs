using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    public sealed partial class HistoricalBaselineUploader
    {
        public async Task<string> UploadGetnetAsync(string url,string uploadId,HistoricalSalesImportFile source,
            OnlineSyncRequestCredentials credentials,Action demand,CancellationToken ct)
        {
            var target=DemandGetnetUri(url,_admin);
            if(source==null||source.Bytes<1||source.Bytes>20971520||!HistoricalWebImportResponseMapper.Hash(source.Sha256)||
                !HistoricalWebImportResponseMapper.Id(uploadId)||credentials==null||demand==null||
                new[]{credentials.DeviceToken,credentials.SessionToken}.Any(v=>v==null||v.Length<20||v.Length>256||v.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c))))
                throw new HistoricalCloudRequestException("history_web_source_invalid",false,false);
            demand();ct.ThrowIfCancellationRequested();
            try
            {
                using var file=new FileStream(source.Path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
                if(file.Length!=source.Bytes)throw new IOException();
                var digest=await Task.Run(()=>{using var sha=SHA256.Create();return HistoricalPublicationOutbox.Hex(sha.ComputeHash(file));},ct).ConfigureAwait(false);
                if(digest!=source.Sha256)throw new IOException();file.Position=0;demand();ct.ThrowIfCancellationRequested();
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var request=new HttpRequestMessage(HttpMethod.Post,target);
                request.Headers.CacheControl=new CacheControlHeaderValue{NoStore=true,MaxAge=TimeSpan.Zero};
                request.Headers.Add("x-history-device-id",credentials.ShopDeviceId);request.Headers.Add("x-history-session-id",credentials.PosSessionId);
                request.Headers.Add("x-history-device-token",credentials.DeviceToken);request.Headers.Add("x-history-session-token",credentials.SessionToken);
                request.Headers.Add("x-history-upload-id",uploadId);
                request.Content=new StreamContent(file);
                request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");request.Content.Headers.ContentLength=file.Length;
                using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);
                demand();ct.ThrowIfCancellationRequested();var raw=await ReadResponseAsync(response,deadline.Token).ConfigureAwait(false);
                if(response.StatusCode==HttpStatusCode.Unauthorized||response.StatusCode==HttpStatusCode.Forbidden)
                {
                    var code=ReadAuthenticationCode(raw);if(code!=null)return code;
                    throw new HistoricalCloudRequestException("history_web_upload_denied",true,false);
                }
                if((response.StatusCode!=HttpStatusCode.OK&&response.StatusCode!=HttpStatusCode.Conflict)||
                    !ReadAcknowledgement(raw,uploadId,response.StatusCode==HttpStatusCode.Conflict))
                    throw new HistoricalCloudRequestException("history_web_upload_failed",false,(int)response.StatusCode>=500||response.StatusCode==HttpStatusCode.RequestTimeout||(int)response.StatusCode==429);
                return string.Empty;
            }
            catch(HttpRequestException){throw new HistoricalCloudRequestException("history_web_upload_unavailable",false,true);}
            catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new HistoricalCloudRequestException("history_timeout",false,true);}
            catch(IOException){throw new HistoricalCloudRequestException("history_web_source_changed",false,false);}
            catch(UnauthorizedAccessException){throw new HistoricalCloudRequestException("history_web_source_unavailable",false,false);}
        }
        internal static Uri DemandGetnetUri(string value,Uri admin)
        {
            var expected=new Uri(admin,"/api/pos/payment-history/source?source=getnet");
            if(value==null||value.Length>8192||!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||
                uri.AbsoluteUri!=expected.AbsoluteUri)throw new HistoricalCloudRequestException("history_web_upload_target_invalid",false,false);
            return uri;
        }
    }
}
