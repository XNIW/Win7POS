using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos
{
    public sealed partial class PosWorkflowService
    {
        private Func<HistoricalCloudActor,string,HistoricalCloudPayload,CancellationToken,Task<PosOnlineResult<HistoricalWebCloudResponse>>> _webHistoryRequestForTesting;
        private Func<string,string,HistoricalSalesImportFile,HistoricalCloudActor,CancellationToken,Task> _webHistoryUploadForTesting;
        internal void SetHistoricalWebRequestForTesting(Func<HistoricalCloudActor,string,HistoricalCloudPayload,CancellationToken,Task<PosOnlineResult<HistoricalWebCloudResponse>>> request,
            Func<string,string,HistoricalSalesImportFile,HistoricalCloudActor,CancellationToken,Task> upload=null)
        {_webHistoryRequestForTesting=request;_webHistoryUploadForTesting=upload;}

        /// <summary>One resumable preparation turn. It never commits, polls indefinitely or writes the local ledger.</summary>
        public async Task<HistoricalWebImportProgress> PrepareHistoricalWebImportAsync(HistoricalSalesImportAnalysis analysis,string configDigest,OperatorSession session,CancellationToken ct)
        {
            var scope=await CaptureWebImportScopeAsync(session,ct).ConfigureAwait(false);scope.Demand();
            if(analysis==null||analysis.HasErrors||analysis.AcceptedTransactions.Count==0||analysis.Files.Count<1||analysis.Files.Count>10)
                throw new InvalidOperationException("history_web_analysis_required");
            // The local parser supplies observed identity facts here, not web
            // authorization. Only the independent server parser/review can
            // qualify getnet-ooxml-web-v1. Never require/promote C# approval.
            var resolverFingerprint=await Task.Run(()=>HistoricalWebImportPolicy.ObservedFingerprint(scope.Config.ShopId,analysis),ct).ConfigureAwait(false);
            scope.Demand();
            if(scope.Config.ConfigDigest!=configDigest)
                throw new InvalidOperationException("history_review_changed");
            var files=analysis.Files.OrderBy(f=>f.Sha256,StringComparer.Ordinal).ToArray();
            if(files.Any(f=>f.Bytes<1||f.Bytes>20971520||Path.GetExtension(f.Path).ToLowerInvariant()!=".xlsx"||Path.GetFileName(f.Path).Length>256)||
                files.Select(f=>f.Sha256).Distinct(StringComparer.Ordinal).Count()!=files.Length)throw new InvalidOperationException("history_web_source_invalid");
            var key=WebHash(new[]{"history-web-intent-v1",scope.ActorFingerprint,scope.Config.RuntimeDatasetId,scope.Config.WriterEpoch.Value.ToString(CultureInfo.InvariantCulture),
                _factory.AuthorityStamp.RestoreEpoch.ToString(CultureInfo.InvariantCulture)}.Concat(files.SelectMany(f=>new[]{f.Sha256,f.Bytes.ToString(CultureInfo.InvariantCulture)})).ToArray());
            var store=new HistoricalWebImportIntentStore(_factory);
            var intent=await store.GetOrCreateAsync(key,scope.ActorFingerprint,scope.Config.RuntimeDatasetId,scope.Config.WriterEpoch.Value,
                files.Select(f=>f.Sha256).ToArray(),configDigest,resolverFingerprint,ct).ConfigureAwait(false);scope.Demand();
            if(intent.CommitAttempted)
            {
                var outcome=await WebRequestAsync(scope,"web_receipt",new HistoricalCloudPayload{RequestKey=intent.CommitRequestKey},ct).ConfigureAwait(false);
                var receipt=HistoricalWebImportResponseMapper.Receipt(outcome.Receipt,intent.DatasetId,intent.WriterEpoch);
                if(receipt!=null){await store.CompleteAsync(key,intent.CommitRequestKey,receipt.OperationId,ct).ConfigureAwait(false);scope.Demand();SignalHistoricalPublication();return new HistoricalWebImportProgress(key,intent.JobId,"committed",receipt:receipt);}
                var pendingReview=await WebRequestAsync(scope,"web_review",new HistoricalCloudPayload{WebJobId=intent.JobId},ct).ConfigureAwait(false);
                var pendingSummary=HistoricalWebImportResponseMapper.Review(pendingReview,scope.Config.ShopId);
                var unchanged=scope.Config.ConfigDigest==intent.ConfigDigest&&resolverFingerprint==intent.ResolverFingerprint&&pendingReview.ReviewDigest==intent.ReviewDigest&&
                    pendingReview.DatasetId==intent.DatasetId&&long.Parse(pendingReview.WriterEpoch,CultureInfo.InvariantCulture)==intent.WriterEpoch;
                var pending=new HistoricalWebReviewPlan(key,intent.JobId,intent.CommitRequestKey,scope.Config.ShopId,intent.DatasetId,
                    long.Parse(pendingReview.Revision,CultureInfo.InvariantCulture),intent.WriterEpoch,intent.ReviewDigest,intent.ConfigDigest,intent.ResolverFingerprint,
                    intent.EvidenceIds,pendingSummary,unchanged&&pendingReview.CanCommit==true,pendingReview.WriterReady==true);
                return new HistoricalWebImportProgress(key,intent.JobId,"commit_outcome_unknown",review:pending,
                    errorCode:"history_web_commit_recovery_required",reasons:pendingReview.Reasons);
            }
            var verifying=false;
            if(intent.JobId==null)
            {
                for(var i=0;i<files.Length;i++)
                {
                    var file=files[i];var upload=intent.UploadIds[i];
                    if(upload==null)
                    {
                        await Task.Run(()=>VerifyWebSources(new[]{file},ct),ct).ConfigureAwait(false);scope.Demand();
                        var created=await WebRequestAsync(scope,"web_upload_create",new HistoricalCloudPayload{FileName=Path.GetFileName(file.Path),Sha256=file.Sha256,ByteLength=file.Bytes.ToString(CultureInfo.InvariantCulture)},ct).ConfigureAwait(false);
                        HistoricalWebImportResponseMapper.Upload(created);upload=created.UploadId;
                        intent=await store.SetUploadAsync(key,i,upload,ct).ConfigureAwait(false);scope.Demand();
                        if(created.UploadRequired==true)await UploadWebSourceAsync(scope,created.UploadUrl,upload,file,ct).ConfigureAwait(false);
                        var ready=await WebRequestAsync(scope,"web_upload_ready",new HistoricalCloudPayload{UploadId=upload},ct).ConfigureAwait(false);
                        var readyState=HistoricalWebImportResponseMapper.SourceState(ready,upload,Path.GetFileName(file.Path),file.Sha256,file.Bytes);
                        if(readyState=="rejected"||readyState=="expired")throw new InvalidOperationException("history_web_source_rejected");
                    }
                    var status=await WebRequestAsync(scope,"web_upload_status",new HistoricalCloudPayload{UploadId=upload},ct).ConfigureAwait(false);
                    var state=HistoricalWebImportResponseMapper.SourceState(status,upload,Path.GetFileName(file.Path),file.Sha256,file.Bytes);
                    if(state=="pending")
                    {
                        // Upload identity was persisted before POST. A lost ACK
                        // resumes the same non-overwriting source, never a new one.
                        if(!PosAdminWebOptions.TryLoad(out var options,out _))throw new InvalidOperationException("history_cloud_connection_required");
                        await UploadWebSourceAsync(scope,new Uri(options.BaseUri,"/api/pos/payment-history/source?source=getnet").AbsoluteUri,upload,file,ct).ConfigureAwait(false);
                        var ready=await WebRequestAsync(scope,"web_upload_ready",new HistoricalCloudPayload{UploadId=upload},ct).ConfigureAwait(false);
                        state=HistoricalWebImportResponseMapper.SourceState(ready,upload,Path.GetFileName(file.Path),file.Sha256,file.Bytes);
                    }
                    if(state=="rejected"||state=="expired")throw new InvalidOperationException("history_web_source_rejected");
                    verifying|=state!="verified";
                }
                if(verifying)return new HistoricalWebImportProgress(key,null,"sources_verifying");
                var webProofs=ApplicableWebProofs(scope.Config,files.Select(f=>f.Sha256).ToArray());
                var started=await WebRequestAsync(scope,"web_start",new HistoricalCloudPayload{RequestKey=intent.AnalysisRequestKey,WebUploadIds=intent.UploadIds.ToArray(),WebConfiguration=WebParserOptions(webProofs,files)},ct).ConfigureAwait(false);
                HistoricalWebImportResponseMapper.Job(started);
                intent=await store.SetJobAsync(key,started.JobId,ct).ConfigureAwait(false);scope.Demand();
            }
            var job=await WebRequestAsync(scope,"web_status",new HistoricalCloudPayload{WebJobId=intent.JobId},ct).ConfigureAwait(false);
            var parser=HistoricalWebImportResponseMapper.Status(job,intent.JobId);
            if(parser!=null)HistoricalWebImportPolicy.DemandParserParity(analysis,parser);
            if(job.State!="review_ready")return new HistoricalWebImportProgress(key,intent.JobId,job.State,job.ProcessedRows.Value,parser,errorCode:job.ErrorCode);
            if(parser==null)throw new InvalidOperationException("history_web_parser_mismatch");
            var evidence=ApplicableWebProofs(scope.Config,intent.FileSha256s.ToArray()).Select(p=>p.Id).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var configured=await WebRequestAsync(scope,"web_configure",new HistoricalCloudPayload{WebJobId=intent.JobId,WebEvidenceIds=evidence,WebCorrections=Array.Empty<string>()},ct).ConfigureAwait(false);
            HistoricalWebImportResponseMapper.Job(configured,intent.JobId);
            var review=await WebRequestAsync(scope,"web_review",new HistoricalCloudPayload{WebJobId=intent.JobId},ct).ConfigureAwait(false);
            var summary=HistoricalWebImportResponseMapper.Review(review,scope.Config.ShopId);
            if(review.CanCommit==true&&summary.NewCount>0&&evidence.Length==0)throw new InvalidOperationException(HistoricalWebImportResponseMapper.InvalidCode);
            if(review.DatasetId!=intent.DatasetId||long.Parse(review.WriterEpoch,CultureInfo.InvariantCulture)!=intent.WriterEpoch)throw new InvalidOperationException("history_review_changed");
            intent=await store.SetReviewAsync(key,review.ReviewDigest,evidence,ct).ConfigureAwait(false);scope.Demand();
            var plan=new HistoricalWebReviewPlan(key,intent.JobId,intent.CommitRequestKey,scope.Config.ShopId,review.DatasetId,long.Parse(review.Revision,CultureInfo.InvariantCulture),
                intent.WriterEpoch,review.ReviewDigest,configDigest,resolverFingerprint,evidence,summary,review.CanCommit.Value,review.WriterReady.Value);
            var next=summary.NewCount==0&&summary.DuplicateCount>0&&summary.Conflicts==0&&summary.Blocked==0?"already_imported":
                summary.Total==0&&parser?.Rejected>0?"declined_only":review.CanCommit==true&&review.WriterReady==true?"review_ready":"blocked";
            return new HistoricalWebImportProgress(key,intent.JobId,next,job.ProcessedRows.Value,parser,plan,reasons:review.Reasons);
        }

        /// <summary>Called only by the operator's explicit Confirm. Lost ACKs retain this same durable request key.</summary>
        public async Task<HistoricalWebImportReceipt> CommitHistoricalWebImportAsync(HistoricalWebReviewPlan review,OperatorSession session,CancellationToken ct)
        {
            if(review==null||!review.CanCommit||!review.WriterReady||review.EvidenceIds.Count==0||review.Summary.NewCount<1||review.Summary.Conflicts!=0||review.Summary.Blocked!=0)
                throw new InvalidOperationException("history_web_commit_blocked");
            var scope=await CaptureWebImportScopeAsync(session,ct).ConfigureAwait(false);var store=new HistoricalWebImportIntentStore(_factory);
            var intent=await store.ReadAsync(review.IntentKey,ct).ConfigureAwait(false);scope.Demand();DemandWebIntent(scope,intent,review);
            var previous=await WebRequestAsync(scope,"web_receipt",new HistoricalCloudPayload{RequestKey=intent.CommitRequestKey},ct).ConfigureAwait(false);
            var replay=HistoricalWebImportResponseMapper.Receipt(previous.Receipt,intent.DatasetId,intent.WriterEpoch);
            if(replay!=null)
            {if(!SameWebSummary(replay.Summary,review.Summary))throw new InvalidOperationException("history_web_receipt_mismatch");await store.CompleteAsync(intent.Key,intent.CommitRequestKey,replay.OperationId,ct).ConfigureAwait(false);scope.Demand();return replay;}
            if(scope.Config.ConfigDigest!=review.ConfigDigest||ApplicableWebProofs(scope.Config,intent.FileSha256s.ToArray()).Select(p=>p.Id).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(review.EvidenceIds)!=true)
                throw new InvalidOperationException("history_review_changed");
            await VerifyWebPreCommitBackupAsync(scope,ct).ConfigureAwait(false);
            var fresh=await WebRequestAsync(scope,"web_review",new HistoricalCloudPayload{WebJobId=intent.JobId},ct).ConfigureAwait(false);
            var summary=HistoricalWebImportResponseMapper.Review(fresh,scope.Config.ShopId);
            if(fresh.ReviewDigest!=review.ReviewDigest||fresh.DatasetId!=review.DatasetId||long.Parse(fresh.WriterEpoch,CultureInfo.InvariantCulture)!=review.WriterEpoch||
                fresh.CanCommit!=true||fresh.WriterReady!=true||summary.NewCount<1||summary.Conflicts!=0||summary.Blocked!=0)
                throw new InvalidOperationException("history_review_changed");
            if(!SameWebSummary(summary,review.Summary))throw new InvalidOperationException("history_review_changed");
            await store.BeginCommitAsync(intent.Key,intent.JobId,intent.ReviewDigest,intent.CommitRequestKey,ct).ConfigureAwait(false);scope.Demand();
            var committed=await WebRequestAsync(scope,"web_commit",new HistoricalCloudPayload{WebJobId=intent.JobId,RequestKey=intent.CommitRequestKey,ReviewDigest=intent.ReviewDigest},ct).ConfigureAwait(false);
            var receipt=HistoricalWebImportResponseMapper.Commit(committed,intent.DatasetId,intent.WriterEpoch);
            if(!SameWebSummary(receipt.Summary,review.Summary))throw new InvalidOperationException("history_web_receipt_mismatch");
            await store.CompleteAsync(intent.Key,intent.CommitRequestKey,receipt.OperationId,ct).ConfigureAwait(false);scope.Demand();
            SignalHistoricalPublication();return receipt;
        }
        public async Task<HistoricalWebImportReceipt> ReadHistoricalWebReceiptAsync(HistoricalWebReviewPlan review,OperatorSession session,CancellationToken ct)
        {
            if(review==null)throw new ArgumentNullException(nameof(review));
            var scope=await CaptureWebImportScopeAsync(session,ct,requireWeb:false).ConfigureAwait(false);var store=new HistoricalWebImportIntentStore(_factory);
            var intent=await store.ReadAsync(review.IntentKey,ct).ConfigureAwait(false);scope.Demand();
            if(scope.ActorFingerprint!=intent.ActorFingerprint||intent.CommitRequestKey!=review.CommitRequestKey||intent.JobId!=review.JobId)throw new InvalidOperationException("history_review_changed");
            var result=await WebRequestAsync(scope,"web_receipt",new HistoricalCloudPayload{RequestKey=intent.CommitRequestKey},ct).ConfigureAwait(false);
            return HistoricalWebImportResponseMapper.Receipt(result.Receipt,intent.DatasetId,intent.WriterEpoch);
        }
        public async Task<HistoricalWebImportProgress> SetHistoricalWebJobPausedAsync(HistoricalWebImportProgress progress,bool paused,OperatorSession session,CancellationToken ct)
        {
            if(progress==null||progress.JobId==null)throw new InvalidOperationException("history_web_job_required");
            var scope=await CaptureWebImportScopeAsync(session,ct).ConfigureAwait(false);
            var intent=await new HistoricalWebImportIntentStore(_factory).ReadAsync(progress.IntentKey,ct).ConfigureAwait(false);scope.Demand();
            if(intent.ActorFingerprint!=scope.ActorFingerprint||intent.JobId!=progress.JobId||intent.CommitAttempted)throw new InvalidOperationException("history_web_commit_recovery_required");
            var result=await WebRequestAsync(scope,paused?"web_cancel_job":"web_resume_job",new HistoricalCloudPayload{WebJobId=intent.JobId},ct).ConfigureAwait(false);
            HistoricalWebImportResponseMapper.Job(result,intent.JobId);return new HistoricalWebImportProgress(intent.Key,intent.JobId,result.State);
        }

        private sealed class WebImportScope
        {internal HistoricalCloudActor Actor;internal HistoricalQualificationContext Config;internal string ActorFingerprint;internal Action Demand;}
        private async Task VerifyWebPreCommitBackupAsync(WebImportScope scope,CancellationToken ct)
        {
            using var admission=CancellationTokenSource.CreateLinkedTokenSource(ct);admission.CancelAfter(TimeSpan.FromSeconds(60));
            if(Interlocked.CompareExchange(ref _historicalMaintenance,1,0)!=0)throw new InvalidOperationException("historical_maintenance_active");
            var gate=false;
            try
            {
                using var backup=await _backupAutomation.EnterMaintenanceAsync(admission.Token).ConfigureAwait(false);
                await _gate.WaitAsync(admission.Token).ConfigureAwait(false);gate=true;scope.Demand();
                using var catalog=await new CatalogShopTransitionBarrier(_factory).EnterAsync(admission.Token).ConfigureAwait(false);
                var path=AllocateDbBackupPath("pos_pre_history_web_");
                await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async()=>
                {
                    scope.Demand();var validated=await new SqliteOnlineBackup(_factory).CreateVerifiedAsync(path,admission.Token).ConfigureAwait(false);
                    if(!validated.IsValid)throw new InvalidOperationException("history_web_backup_failed");scope.Demand();
                },TimeSpan.FromSeconds(30),admission.Token).ConfigureAwait(false);
            }
            finally{if(gate)_gate.Release();Interlocked.Exchange(ref _historicalMaintenance,0);}
            // Every lease has been released before the fresh server review and
            // explicit commit. This is a local recovery copy, not a server backup.
            scope.Demand();
        }
        private async Task<WebImportScope> CaptureWebImportScopeAsync(OperatorSession session,CancellationToken ct,bool requireWeb=true)
        {
            var config=await ReadHistoricalQualificationAsync(session,ct).ConfigureAwait(false);
            if(config.RuntimeDatasetId==null||!config.WriterEpoch.HasValue||requireWeb&&config.WriterAuthority!="web")throw new InvalidOperationException("history_web_writer_required");
            var actor=session.CaptureHistoricalCloudActor(config.ShopId);var stamp=_factory.AuthorityStamp;var version=session.CurrentOperatorAuthorityVersion;
            Action demand=()=>
            {
                actor.DemandCurrent();ct.ThrowIfCancellationRequested();new PermissionService(session).Demand(PermissionCodes.RegisterImportHistory,PermissionCodes.RegisterImportHistory);
                var after=_factory.AuthorityStamp;if(session.CurrentOperatorAuthorityVersion!=version||after.RestoreEpoch!=stamp.RestoreEpoch||after.AuthorityInstanceEpoch!=stamp.AuthorityInstanceEpoch)
                    throw new InvalidOperationException("historical_read_scope_changed");
            };
            demand();
            return new WebImportScope{Actor=actor,Config=config,Demand=demand,ActorFingerprint=HistoricalWebImportPolicy.PrincipalFingerprint(actor.Trusted)};
        }
        private static void DemandWebIntent(WebImportScope scope,HistoricalWebImportIntent intent,HistoricalWebReviewPlan review)
        {
            if(intent.ActorFingerprint!=scope.ActorFingerprint||intent.DatasetId!=scope.Config.RuntimeDatasetId||intent.WriterEpoch!=scope.Config.WriterEpoch||
                intent.JobId!=review.JobId||intent.CommitRequestKey!=review.CommitRequestKey||intent.ReviewDigest!=review.ReviewDigest||intent.ConfigDigest!=review.ConfigDigest||
                intent.ResolverFingerprint!=review.ResolverFingerprint||!intent.EvidenceIds.SequenceEqual(review.EvidenceIds)||review.ShopId!=scope.Config.ShopId||review.DatasetId!=intent.DatasetId||review.WriterEpoch!=intent.WriterEpoch)
                throw new InvalidOperationException("history_review_changed");
        }
        private static bool SameWebSummary(HistoricalWebReviewSummary a,HistoricalWebReviewSummary b)
            =>a!=null&&b!=null&&a.Total==b.Total&&a.NewCount==b.NewCount&&a.DuplicateCount==b.DuplicateCount&&a.Conflicts==b.Conflicts&&a.Blocked==b.Blocked&&
                a.NewGrossClp==b.NewGrossClp&&a.KnownNewNetClp==b.KnownNewNetClp&&a.KnownNewNetCount==b.KnownNewNetCount;
        private async Task<HistoricalWebResultDto> WebRequestAsync(WebImportScope scope,string operation,HistoricalCloudPayload payload,CancellationToken ct)
        {
            scope.Demand();var actor=scope.Actor;var trusted=actor.Trusted;PosOnlineResult<HistoricalWebCloudResponse> response;
            if(_webHistoryRequestForTesting!=null)response=await _webHistoryRequestForTesting(actor,operation,payload,ct).ConfigureAwait(false);
            else
            {
                if(!PosAdminWebOptions.TryLoad(out var options,out _))throw new InvalidOperationException("history_cloud_connection_required");
                if(!PosOnlineSyncSupervisorHost.TryCreateGeneration(trusted,out var generation))throw new OnlineSyncGenerationChangedException();
                using var client=new PosAdminWebClient(options);
                response=await PosOnlineSyncSignalBus.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,
                    (credentials,token)=>client.HistoricalWebImportAsync(new HistoricalCloudRequest{DeviceId=credentials.ShopDeviceId,SessionId=credentials.PosSessionId,
                        DeviceToken=credentials.DeviceToken,SessionToken=credentials.SessionToken,Operation=operation,Payload=payload},token),
                    result=>result!=null&&SharedAuthStopPolicy.IsAuthenticationDenied(result.Code)?result.Code:string.Empty,ct).ConfigureAwait(false);
            }
            scope.Demand();return HistoricalWebImportResponseMapper.RequireResult(response,trusted.ShopId,trusted.ShopDeviceId,trusted.PosSessionId);
        }
        private async Task UploadWebSourceAsync(WebImportScope scope,string url,string upload,HistoricalSalesImportFile source,CancellationToken ct)
        {
            scope.Demand();
            if(_webHistoryUploadForTesting!=null)await _webHistoryUploadForTesting(url,upload,source,scope.Actor,ct).ConfigureAwait(false);
            else
            {
                if(!PosAdminWebOptions.TryLoad(out var options,out _)||!PosOnlineSyncSupervisorHost.TryCreateGeneration(scope.Actor.Trusted,out var generation))throw new InvalidOperationException("history_cloud_connection_required");
                using var uploader=new HistoricalBaselineUploader(options);
                var code=await PosOnlineSyncSignalBus.ExecuteCredentialedRequestAsync(generation,OnlineSyncLane.HistoricalPayments,
                    (credentials,token)=>uploader.UploadGetnetAsync(url,upload,source,credentials,scope.Demand,token),
                    value=>SharedAuthStopPolicy.IsAuthenticationDenied(value)?value:string.Empty,ct).ConfigureAwait(false);
                if(!string.IsNullOrEmpty(code))throw new HistoricalCloudRequestException(code,true,false);
            }
            scope.Demand();
        }
        private static HistoricalQualifiedEvidence[] ApplicableWebProofs(HistoricalQualificationContext context,string[] files)
            =>HistoricalWebImportPolicy.ApplicableEvidence(context,files).ToArray();
        private static HistoricalWebParserConfiguration WebParserOptions(HistoricalQualifiedEvidence[] proofs,HistoricalSalesImportFile[] files)
            =>HistoricalWebImportPolicy.ParserConfiguration(proofs,files);
        private static void VerifyWebSources(HistoricalSalesImportFile[] files,CancellationToken ct)
        {
            foreach(var source in files)
            {
                ct.ThrowIfCancellationRequested();using var file=new FileStream(source.Path,FileMode.Open,FileAccess.Read,FileShare.Read);using var sha=SHA256.Create();
                if(file.Length!=source.Bytes)throw new InvalidOperationException("history_web_source_changed");
                var buffer=new byte[81920];int n;while((n=file.Read(buffer,0,buffer.Length))>0){ct.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,n,null,0);}sha.TransformFinalBlock(Array.Empty<byte>(),0,0);
                if(BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant()!=source.Sha256)throw new InvalidOperationException("history_web_source_changed");
            }
        }
        private static string WebHash(string[] fields)
        {using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n",fields)))).Replace("-","").ToLowerInvariant();}
    }
}
