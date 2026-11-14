using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // HTTP component fixture, not a substitute for the independent backend/SQL
    // qualification. All documents, identity, source bytes and economics are synthetic.
    internal sealed class HistoricalWebHttpFixture:IDisposable
    {
        private readonly HistoricalSalesFoundationRunner.Fixture _fixture;
        private readonly HistoricalSalesImportFile _source;
        private readonly TcpListener _listener=new TcpListener(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new CancellationTokenSource();
        private readonly Task _server;
        private readonly PosAdminWebClient _client;
        private readonly HistoricalBaselineUploader _uploader;
        private readonly HistoricalQualifiedEvidenceDto[] _proofs;
        private readonly Dictionary<string,string> _jobs=new Dictionary<string,string>(StringComparer.Ordinal);
        private readonly Dictionary<string,HistoricalWebReceiptDto> _receipts=new Dictionary<string,HistoricalWebReceiptDto>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingReported=new HashSet<string>(StringComparer.Ordinal);
        private readonly string _upload=Guid.NewGuid().ToString("D");
        private string _deviceToken,_sessionToken;
        internal readonly Uri BaseUri;
        internal readonly List<string> Operations=new List<string>();
        internal bool DenyConfig,Noop,MissingWebProof,FailFirstCommit=true;
        internal int SourcePosts,CommitCalls,FinancialWrites;
        internal string ReceiptOperationId=>_receipts.Count==1?_receipts.Values.Single().OperationId:"";
        internal string ConfigDigest=>Hash(MissingWebProof?"missing-web":"verified-web");
        internal string[] ConfiguredEvidence=Array.Empty<string>();
        internal readonly HashSet<string> ReceiptSessions=new HashSet<string>(StringComparer.Ordinal);
        internal string FailureType="";
        private bool _uploaded;
        internal void RefreshExpectedActor()
        {var current=_fixture.Operator.CaptureHistoricalCloudActor(_fixture.ShopId).Trusted;_deviceToken=current.DeviceToken;_sessionToken=current.SessionToken;}
        internal HistoricalWebHttpFixture(HistoricalSalesFoundationRunner.Fixture fixture,HistoricalSalesImportAnalysis source)
        {
            _fixture=fixture;_source=source.Files.Single();_listener.Start();BaseUri=new Uri("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
            var trusted=fixture.Operator.CaptureHistoricalCloudActor(fixture.ShopId).Trusted;_deviceToken=trusted.DeviceToken;_sessionToken=trusted.SessionToken;
            var options=new PosAdminWebOptions(BaseUri);_client=new PosAdminWebClient(options);_uploader=new HistoricalBaselineUploader(options);
            var merchant=Guid.NewGuid().ToString("D");
            _proofs=new[]{HistoricalEvidenceKind.MerchantMapping,HistoricalEvidenceKind.ModernTime,HistoricalEvidenceKind.NativeStart}.Select(kind=>new HistoricalQualifiedEvidenceDto
            {
                Id=Guid.NewGuid().ToString("D"),ShopId=fixture.ShopId,Kind=kind.ToString(),State="Verified",Issuer="PlatformMaster",QualificationVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,
                MerchantId=merchant,AliasKey=kind==HistoricalEvidenceKind.MerchantMapping?Hash("independent-web-fixture-alias"):"",Profile="GetnetModern",ParserVersion=HistoricalWebImportPolicy.WebParserVersion,
                Files=new[]{_source.Sha256},FromTicks=new DateTime(2000,1,1).Ticks.ToString(CultureInfo.InvariantCulture),ToTicks=new DateTime(2000,2,1).Ticks.ToString(CultureInfo.InvariantCulture),
                PolicyVersion="synthetic-web-provider-v1",Reference="synthetic-web-provider-document",Interpretation=kind==HistoricalEvidenceKind.ModernTime?"IsoUtc":"Unresolved",
                NativeStartTicks=kind==HistoricalEvidenceKind.NativeStart?new DateTime(2040,1,1).Ticks.ToString(CultureInfo.InvariantCulture):null,ProviderVerified=kind==HistoricalEvidenceKind.ModernTime,
                DocumentSha256=Hash("synthetic-web-document"),DocumentUploadId=Guid.NewGuid().ToString("D")
            }).ToArray();
            _server=RunAsync();
        }
        internal async Task<PosWorkflowService> WorkflowAsync()
        {
            var workflow=_fixture.CreateWorkflow();
            await new HistoricalReplicaStore(_fixture.Factory,()=>{}).ObserveAuthorityAsync(_fixture.ShopId,new HistoricalCloudResult
            {DatasetId=Dataset,Revision="0",WriterEpoch="1",WriterAuthority="web",ProtocolVersion=1},CancellationToken.None);
            workflow.SetHistoricalCloudRequestForTesting((actor,operation,payload,ct)=>_client.HistoricalCloudAsync(Request(actor,operation,payload),ct));
            workflow.SetHistoricalWebRequestForTesting((actor,operation,payload,ct)=>_client.HistoricalWebImportAsync(Request(actor,operation,payload),ct),
                async(url,upload,file,actor,ct)=>
                {
                    var trusted=actor.Trusted;var credentials=new OnlineSyncRequestCredentials(_fixture.Generation,trusted.DeviceToken,trusted.SessionToken,"synthetic-http-component");
                    var code=await _uploader.UploadGetnetAsync(url,upload,file,credentials,actor.DemandCurrent,ct).ConfigureAwait(false);
                    if(code.Length>0)throw new HistoricalCloudRequestException(code,true,false);
                });
            return workflow;
        }
        private static HistoricalCloudRequest Request(HistoricalCloudActor actor,string operation,HistoricalCloudPayload payload)
        {actor.DemandCurrent();var t=actor.Trusted;return new HistoricalCloudRequest{DeviceId=t.ShopDeviceId,SessionId=t.PosSessionId,DeviceToken=t.DeviceToken,SessionToken=t.SessionToken,Operation=operation,Payload=payload};}
        private const string Dataset="90000000-0000-4000-8000-000000003019";
        private HistoricalCloudResult Config()=>new HistoricalCloudResult
        {
            QualificationSchemaVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,QualificationShopId=_fixture.ShopId,QualificationConfigDigest=ConfigDigest,
            QualifiedEvidence=MissingWebProof?Array.Empty<HistoricalQualifiedEvidenceDto>():_proofs,QualificationLocalScopeBindings=Array.Empty<HistoricalLocalScopeBindingDto>(),
            QualificationRuntime=new HistoricalQualificationRuntimeDto{DatasetId=Dataset,WriterEpoch="1",WriterAuthority="web"}
        };
        private HistoricalWebSummaryDto ReviewSummary()=>new HistoricalWebSummaryDto{Total=1,NewCount=Noop?0:1,DuplicateCount=Noop?1:0,Conflicts=0,Blocked=MissingWebProof?1:0,
            NewGrossClp=Noop?"0":"1000",KnownNewNetClp=Noop?"0":"1000",KnownNewNetCount=Noop?0:1};
        private string ReviewDigest(string job)=>Hash(job+":"+Noop+":"+MissingWebProof);
        private HistoricalWebResultDto Web(HistoricalCloudRequest request)
        {
            var p=request.Payload;
            switch(request.Operation)
            {
                case "web_upload_create":
                    if(p.Sha256!=_source.Sha256||p.ByteLength!=_source.Bytes.ToString(CultureInfo.InvariantCulture)||p.FileName!=Path.GetFileName(_source.Path))throw new InvalidOperationException();
                    return new HistoricalWebResultDto{UploadId=_upload,ObjectKey="synthetic-getnet-source",UploadRequired=!_uploaded,UploadUrl=_uploaded?null:new Uri(BaseUri,"/api/pos/payment-history/source?source=getnet").AbsoluteUri};
                case "web_upload_ready":case "web_upload_status":
                    if(p.UploadId!=_upload)throw new InvalidOperationException();return new HistoricalWebResultDto{UploadId=_upload,State=_uploaded?"verified":"pending",FileName=Path.GetFileName(_source.Path),Sha256=_source.Sha256,ByteLength=_source.Bytes.ToString(CultureInfo.InvariantCulture)};
                case "web_start":
                    if(!_uploaded||p.WebUploadIds?.Single()!=_upload||p.WebConfiguration==null||!Guid.TryParse(p.RequestKey,out _))throw new InvalidOperationException();
                    if(!_jobs.TryGetValue(p.RequestKey,out var job)){job=Guid.NewGuid().ToString("D");_jobs.Add(p.RequestKey,job);}return new HistoricalWebResultDto{JobId=job,State="queued"};
                case "web_status":
                    if(!_jobs.ContainsValue(p.WebJobId))throw new InvalidOperationException();
                    if(_pendingReported.Add(p.WebJobId))return new HistoricalWebResultDto{JobId=p.WebJobId,State="queued",ProcessedRows=0,Summary=null,Diagnostics=Array.Empty<HistoricalWebDiagnosticDto>(),Merchants=Array.Empty<HistoricalWebMerchantDto>(),ErrorCode=null};
                    return new HistoricalWebResultDto{JobId=p.WebJobId,State="review_ready",ProcessedRows=1,Diagnostics=Array.Empty<HistoricalWebDiagnosticDto>(),Merchants=Array.Empty<HistoricalWebMerchantDto>(),ErrorCode=null,
                        Summary=new HistoricalWebSummaryDto{SourceRows=1,Accepted=1,Sales=1,Cancellations=0,Rejected=0,SignedGrossClp="1000",PositiveGrossClp="1000",CancellationGrossClp="0",KnownNetClp="1000",KnownNetCount=1,
                            RejectedRequestedClp="0",KnownRejectedRequestedCount=0,SourceAccepted=1,NeedsTimeDecision=MissingWebProof,
                            FromBusinessDateTime=MissingWebProof?null:"2000-01-02T08:00:00.0000000",ToBusinessDateTime=MissingWebProof?null:"2000-01-02T08:00:00.0000000",
                            Candidates=new HistoricalWebTimeCandidatesDto{IsoFrom="2000-01-02T08:00:00.0000000",IsoTo="2000-01-02T08:00:00.0000000",LocalFrom="2000-01-02T08:00:00.0000000",LocalTo="2000-01-02T08:00:00.0000000"}}};
                case "web_configure":
                    if(p.WebCorrections==null||p.WebCorrections.Length!=0||p.WebEvidenceIds==null||p.WebEvidenceIds.Any(id=>!_proofs.Any(e=>e.Id==id&&e.ParserVersion==HistoricalWebImportPolicy.WebParserVersion)))throw new InvalidOperationException();
                    ConfiguredEvidence=p.WebEvidenceIds.ToArray();return new HistoricalWebResultDto{JobId=p.WebJobId,State="review_ready"};
                case "web_review":return new HistoricalWebResultDto{ShopId=_fixture.ShopId,DatasetId=Dataset,Revision="0",WriterEpoch="1",WriterReady=true,CanCommit=!MissingWebProof,
                    ReviewDigest=ReviewDigest(p.WebJobId),Summary=ReviewSummary(),Reasons=MissingWebProof?new[]{"history_merchant_unverified"}:Array.Empty<string>()};
                case "web_receipt":ReceiptSessions.Add(request.SessionId);return new HistoricalWebResultDto{Receipt=_receipts.TryGetValue(p.RequestKey,out var receipt)?receipt:null};
                case "web_commit":
                    CommitCalls++;if(MissingWebProof||Noop||p.ReviewDigest!=ReviewDigest(p.WebJobId))throw new InvalidOperationException();
                    if(!_receipts.TryGetValue(p.RequestKey,out var existing))
                    {existing=new HistoricalWebReceiptDto{OperationId=Guid.NewGuid().ToString("D"),BatchId=Guid.NewGuid().ToString("D"),Kind="apply",DatasetId=Dataset,Revision="1",WriterEpoch="1",Summary=ReviewSummary()};_receipts.Add(p.RequestKey,existing);FinancialWrites++;}
                    return new HistoricalWebResultDto{OperationId=existing.OperationId,BatchId=existing.BatchId,Kind=existing.Kind,DatasetId=existing.DatasetId,Revision=existing.Revision,WriterEpoch=existing.WriterEpoch,Summary=existing.Summary};
                default:throw new InvalidOperationException();
            }
        }
        private async Task RunAsync()
        {
            try{while(!_stop.IsCancellationRequested){using var socket=await _listener.AcceptTcpClientAsync().ConfigureAwait(false);await ServeAsync(socket).ConfigureAwait(false);}}
            catch(Exception e) when(_stop.IsCancellationRequested&&(e is SocketException||e is ObjectDisposedException||e is OperationCanceledException)){ }
            catch(Exception e){FailureType=e.GetType().Name;}
        }
        private async Task ServeAsync(TcpClient socket)
        {
            using var stream=socket.GetStream();var header=new MemoryStream();var one=new byte[1];var tail="";
            while(tail!="\r\n\r\n")
            {var n=await stream.ReadAsync(one,0,1,_stop.Token).ConfigureAwait(false);if(n==0)return;header.WriteByte(one[0]);if(header.Length>16384)throw new InvalidOperationException();tail=(tail+(char)one[0]);if(tail.Length>4)tail=tail.Substring(tail.Length-4);}
            var text=Encoding.ASCII.GetString(header.ToArray());var lines=text.Split(new[]{"\r\n"},StringSplitOptions.None);var path=lines[0].Split(' ')[1];
            var headers=lines.Skip(1).Where(x=>x.Contains(":")).ToDictionary(x=>x.Substring(0,x.IndexOf(':')),x=>x.Substring(x.IndexOf(':')+1).Trim(),StringComparer.OrdinalIgnoreCase);
            if(!headers.TryGetValue("Content-Length",out var lengthText)||!int.TryParse(lengthText,out var length)||length<1||length>20971520)throw new InvalidOperationException();
            if(headers.TryGetValue("Expect",out var expect)&&expect=="100-continue"){var ack=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");await stream.WriteAsync(ack,0,ack.Length,_stop.Token).ConfigureAwait(false);}
            var bytes=new byte[length];var offset=0;while(offset<length){var n=await stream.ReadAsync(bytes,offset,length-offset,_stop.Token).ConfigureAwait(false);if(n==0)throw new IOException();offset+=n;}
            object body;var status=200;
            if(path=="/api/pos/payment-history/source?source=getnet")
            {
                if(!headers.TryGetValue("x-history-upload-id",out var upload)||upload!=_upload||headers["x-history-device-id"]!=_fixture.Generation.ShopDeviceId||headers["x-history-session-id"]!=_fixture.Generation.PosSessionId||
                    headers["x-history-device-token"]!=_deviceToken||headers["x-history-session-token"]!=_sessionToken||headers["Content-Type"]!="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"||Hash(bytes)!=_source.Sha256)throw new InvalidOperationException();
                SourcePosts++;var already=_uploaded;_uploaded=true;status=already?409:200;body=new SourceAck{Ok=true,UploadId=_upload,AlreadyPresent=already};
            }
            else
            {
                if(path!="/api/pos/payment-history")throw new InvalidOperationException();using var input=new MemoryStream(bytes);var request=(HistoricalCloudRequest)new DataContractJsonSerializer(typeof(HistoricalCloudRequest)).ReadObject(input);
                if(request.DeviceId!=_fixture.Generation.ShopDeviceId||request.SessionId!=_fixture.Generation.PosSessionId||request.DeviceToken!=_deviceToken||request.SessionToken!=_sessionToken)throw new InvalidOperationException();
                Operations.Add(request.Operation);
                if(request.Operation=="qualified_config"&&DenyConfig){status=403;body=new ErrorReply{Ok=false,Code="history_cloud_access_denied"};}
                else if(request.Operation=="qualified_config")body=new HistoricalCloudResponse{Ok=true,ShopId=_fixture.ShopId,DeviceId=request.DeviceId,SessionId=request.SessionId,Result=Config()};
                else if(request.Operation=="authority")body=new HistoricalCloudResponse{Ok=true,ShopId=_fixture.ShopId,DeviceId=request.DeviceId,SessionId=request.SessionId,Result=new HistoricalCloudResult{DatasetId=Dataset,Revision="1",WriterEpoch="1",WriterAuthority="web",ProtocolVersion=1}};
                else
                {
                    var result=Web(request);
                    if(request.Operation=="web_commit"&&FailFirstCommit){FailFirstCommit=false;status=503;body=new ErrorReply{Ok=false,Code="history_cloud_unavailable"};}
                    else body=new HistoricalWebCloudResponse{Ok=true,ShopId=_fixture.ShopId,DeviceId=request.DeviceId,SessionId=request.SessionId,Result=result};
                }
            }
            using var output=new MemoryStream();new DataContractJsonSerializer(body.GetType()).WriteObject(output,body);var payload=output.ToArray();
            var response=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" "+(status==200?"OK":status==409?"Conflict":status==403?"Forbidden":"Unavailable")+"\r\nContent-Type: application/json\r\nContent-Length: "+payload.Length+"\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response,0,response.Length,_stop.Token).ConfigureAwait(false);await stream.WriteAsync(payload,0,payload.Length,_stop.Token).ConfigureAwait(false);
        }
        private static string Hash(string value)=>Hash(Encoding.UTF8.GetBytes(value));
        private static string Hash(byte[] bytes){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        public void Dispose(){_stop.Cancel();_listener.Stop();_client.Dispose();_uploader.Dispose();try{_server.Wait(1000);}catch(AggregateException){} _stop.Dispose();}
        [System.Runtime.Serialization.DataContract]private sealed class SourceAck
        {[System.Runtime.Serialization.DataMember(Name="ok")]public bool Ok;[System.Runtime.Serialization.DataMember(Name="upload_id")]public string UploadId;[System.Runtime.Serialization.DataMember(Name="already_present")]public bool AlreadyPresent;}
        [System.Runtime.Serialization.DataContract]private sealed class ErrorReply
        {[System.Runtime.Serialization.DataMember(Name="ok")]public bool Ok;[System.Runtime.Serialization.DataMember(Name="code")]public string Code;}
    }
}
