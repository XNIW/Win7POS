using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    public sealed class HistoricalWebReviewSummary
    {
        internal HistoricalWebReviewSummary(HistoricalWebSummaryDto s)
        { Total=s.Total.Value;NewCount=s.NewCount.Value;DuplicateCount=s.DuplicateCount.Value;Conflicts=s.Conflicts.Value;Blocked=s.Blocked.Value;
            NewGrossClp=long.Parse(s.NewGrossClp,CultureInfo.InvariantCulture);KnownNewNetClp=long.Parse(s.KnownNewNetClp,CultureInfo.InvariantCulture);KnownNewNetCount=s.KnownNewNetCount.Value; }
        public int Total { get; } public int NewCount { get; } public int DuplicateCount { get; } public int Conflicts { get; } public int Blocked { get; }
        public long NewGrossClp { get; } public long KnownNewNetClp { get; } public int KnownNewNetCount { get; }
    }
    public sealed class HistoricalWebParserSummary
    {
        internal HistoricalWebParserSummary(HistoricalWebSummaryDto s)
        { SourceRows=s.SourceRows.Value;Accepted=s.Accepted.Value;Sales=s.Sales.Value;Cancellations=s.Cancellations.Value;Rejected=s.Rejected.Value;
            SignedGrossClp=Optional(s.SignedGrossClp);KnownNetClp=Optional(s.KnownNetClp);KnownNetCount=s.KnownNetCount.Value;NeedsTimeDecision=s.NeedsTimeDecision.Value;
            PositiveGrossClp=Optional(s.PositiveGrossClp);CancellationGrossClp=Optional(s.CancellationGrossClp);RejectedRequestedClp=Optional(s.RejectedRequestedClp);KnownRejectedRequestedCount=s.KnownRejectedRequestedCount.Value;
            FromBusinessDateTime=s.FromBusinessDateTime;ToBusinessDateTime=s.ToBusinessDateTime;FromIsoCandidate=s.FromIsoCandidateDateTime;ToIsoCandidate=s.ToIsoCandidateDateTime;
            FromExportedLocalCandidate=s.FromExportedLocalCandidateDateTime;ToExportedLocalCandidate=s.ToExportedLocalCandidateDateTime; }
        private static long? Optional(string value)=>value==null?(long?)null:long.Parse(value,CultureInfo.InvariantCulture);
        public int SourceRows { get; } public int Accepted { get; } public int Sales { get; } public int Cancellations { get; } public int Rejected { get; }
        public long? SignedGrossClp { get; } public long? KnownNetClp { get; } public int KnownNetCount { get; } public bool NeedsTimeDecision { get; }
        public long? PositiveGrossClp { get; } public long? CancellationGrossClp { get; } public long? RejectedRequestedClp { get; } public int KnownRejectedRequestedCount { get; }
        public string FromBusinessDateTime { get; } public string ToBusinessDateTime { get; } public string FromIsoCandidate { get; } public string ToIsoCandidate { get; }
        public string FromExportedLocalCandidate { get; } public string ToExportedLocalCandidate { get; }
    }
    public sealed class HistoricalWebReviewPlan
    {
        public HistoricalWebReviewPlan(string intentKey,string jobId,string commitRequestKey,string shopId,string datasetId,long revision,long writerEpoch,
            string reviewDigest,string configDigest,string resolverFingerprint,IEnumerable<string> evidenceIds,HistoricalWebReviewSummary summary,bool canCommit,bool writerReady)
        {
            if(!HistoricalWebImportResponseMapper.Hash(intentKey)||!HistoricalWebImportResponseMapper.Id(jobId)||!HistoricalWebImportResponseMapper.Id(commitRequestKey)||
                !HistoricalWebImportResponseMapper.Id(shopId)||!HistoricalWebImportResponseMapper.Id(datasetId)||revision<0||writerEpoch<1||
                !HistoricalWebImportResponseMapper.Hash(reviewDigest)||!HistoricalWebImportResponseMapper.Hash(configDigest)||!HistoricalWebImportResponseMapper.Hash(resolverFingerprint)||summary==null)
                throw new ArgumentException("history_web_review_invalid");
            var ids=(evidenceIds??throw new ArgumentNullException(nameof(evidenceIds))).ToArray();
            if(ids.Length>200||ids.Any(x=>!HistoricalWebImportResponseMapper.Id(x))||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)throw new ArgumentException("history_web_review_invalid");
            IntentKey=intentKey;JobId=jobId;CommitRequestKey=commitRequestKey;ShopId=shopId;DatasetId=datasetId;Revision=revision;WriterEpoch=writerEpoch;
            ReviewDigest=reviewDigest;ConfigDigest=configDigest;ResolverFingerprint=resolverFingerprint;EvidenceIds=new ReadOnlyCollection<string>(ids);Summary=summary;CanCommit=canCommit;WriterReady=writerReady;
        }
        public string IntentKey { get; } public string JobId { get; } public string CommitRequestKey { get; } public string ShopId { get; } public string DatasetId { get; }
        public long Revision { get; } public long WriterEpoch { get; } public string ReviewDigest { get; } public string ConfigDigest { get; } public string ResolverFingerprint { get; }
        public IReadOnlyList<string> EvidenceIds { get; } public HistoricalWebReviewSummary Summary { get; }
        public bool CanCommit { get; } public bool WriterReady { get; }
    }
    public sealed class HistoricalWebImportReceipt
    {
        internal HistoricalWebImportReceipt(string operation,string batch,string dataset,long revision,long epoch,HistoricalWebReviewSummary summary)
        {OperationId=operation;BatchId=batch;DatasetId=dataset;Revision=revision;WriterEpoch=epoch;Summary=summary;}
        public string OperationId { get; } public string BatchId { get; } public string DatasetId { get; } public long Revision { get; } public long WriterEpoch { get; }
        public HistoricalWebReviewSummary Summary { get; }
    }
    public sealed class HistoricalWebImportProgress
    {
        public HistoricalWebImportProgress(string intentKey,string jobId,string state,int processedRows=0,HistoricalWebParserSummary parserSummary=null,
            HistoricalWebReviewPlan review=null,HistoricalWebImportReceipt receipt=null,string errorCode=null,IEnumerable<string> reasons=null)
        {IntentKey=intentKey;JobId=jobId;State=state;ProcessedRows=processedRows;ParserSummary=parserSummary;Review=review;Receipt=receipt;ErrorCode=errorCode;
            Reasons=new ReadOnlyCollection<string>((reasons??Array.Empty<string>()).ToArray());}
        public string IntentKey { get; } public string JobId { get; } public string State { get; } public int ProcessedRows { get; }
        public HistoricalWebParserSummary ParserSummary { get; } public HistoricalWebReviewPlan Review { get; } public HistoricalWebImportReceipt Receipt { get; } public string ErrorCode { get; }
        public IReadOnlyList<string> Reasons { get; }
    }
    public static class HistoricalWebImportResponseMapper
    {
        public const string InvalidCode="history_web_response_invalid";
        private static InvalidOperationException Invalid()=>new InvalidOperationException(InvalidCode);
        public static HistoricalWebResultDto RequireResult(PosOnlineResult<HistoricalWebCloudResponse> response,string shop,string device,string session)
        {
            if(response?.Success!=true)throw new HistoricalCloudRequestException(response?.Code,response?.Denied==true,response?.Retryable==true);
            var r=response.Value;
            if(r?.Ok!=true||r.ShopId!=shop||r.DeviceId!=device||r.SessionId!=session||r.Result==null)throw Invalid();return r.Result;
        }
        public static void Upload(HistoricalWebResultDto r)
        {
            if(r==null||!Id(r.UploadId)||!Text(r.ObjectKey,100)||!r.UploadRequired.HasValue||
                (r.UploadRequired.Value?!Text(r.UploadUrl,8192):r.UploadUrl!=null))throw Invalid();
        }
        public static string SourceState(HistoricalWebResultDto r,string upload,string name,string hash,long length)
        {
            if(r==null||r.UploadId!=upload||r.FileName!=name||r.Sha256!=hash||!Decimal(r.ByteLength,1,20971520,out var size)||size!=length||
                !new[]{"pending","uploaded","verifying","verified","rejected","expired"}.Contains(r.State,StringComparer.Ordinal))throw Invalid();return r.State;
        }
        public static void Job(HistoricalWebResultDto r,string job=null)
        {if(r==null||!Id(r.JobId)||job!=null&&r.JobId!=job||!States.Contains(r.State,StringComparer.Ordinal))throw Invalid();}
        private static readonly string[] States={"queued","analysing","review_ready","committing","committed","cancelled","failed"};
        public static HistoricalWebParserSummary Status(HistoricalWebResultDto r,string job)
        {
            Job(r,job);
            if(!Count(r.ProcessedRows)||r.Diagnostics==null||r.Diagnostics.Length>500||r.Merchants==null||r.Merchants.Length>201||
                r.ErrorCode!=null&&r.ErrorCode.Length>0&&!Regex.IsMatch(r.ErrorCode,"^[a-z][a-z0-9_]{0,119}$"))throw Invalid();
            foreach(var d in r.Diagnostics)
            {
                if(d==null||!Text(d.Code,120)||!Text(d.Field,256,true)||d.Severity!="Error"&&d.Severity!="Warning"||d.Column.HasValue&&!Count(d.Column,256))throw Invalid();
                if(d.Source!=null&&(!Text(d.Source.FileId,30)||!Hash(d.Source.FileSha256)||!Text(d.Source.SheetName,256,true)||!Count(d.Source.RowNumber,1000000)))throw Invalid();
            }
            foreach(var m in r.Merchants)
            {
                var s=m?.SourceIdentity;
                if(m==null||!Hash(m.AliasKey)||s==null||!s.HasEvidence.HasValue||new[]{s.Local,s.Seller,s.SellerId,s.LocalNumber,s.Rut}.Any(x=>!Text(x,256,true)))throw Invalid();
            }
            if(r.Summary==null)return null;var p=r.Summary;
            if(p.Total.HasValue||p.NewCount.HasValue||p.DuplicateCount.HasValue||p.Conflicts.HasValue||p.Blocked.HasValue||p.NewGrossClp!=null||p.KnownNewNetClp!=null||p.KnownNewNetCount.HasValue||
                new[]{p.SourceRows,p.Accepted,p.Sales,p.Cancellations,p.Rejected,p.KnownNetCount,p.KnownRejectedRequestedCount,p.SourceAccepted}.Any(n=>!Count(n))||
                !p.NeedsTimeDecision.HasValue||p.Candidates==null||
                new[]{p.SignedGrossClp,p.PositiveGrossClp,p.CancellationGrossClp,p.KnownNetClp,p.RejectedRequestedClp}.Any(v=>v!=null&&!Decimal(v,long.MinValue,long.MaxValue,out _))||
                new[]{p.FromBusinessDateTime,p.ToBusinessDateTime,p.FromIsoCandidateDateTime,p.ToIsoCandidateDateTime,p.FromExportedLocalCandidateDateTime,p.ToExportedLocalCandidateDateTime,
                    p.Candidates.IsoFrom,p.Candidates.IsoTo,p.Candidates.LocalFrom,p.Candidates.LocalTo}.Any(x=>x!=null&&!Text(x,40)))throw Invalid();
            return new HistoricalWebParserSummary(p);
        }
        public static HistoricalWebReviewSummary Review(HistoricalWebResultDto r,string shop)
        {
            if(r==null||r.ShopId!=shop||!Id(r.DatasetId)||!Decimal(r.Revision,0,long.MaxValue,out _)||!Decimal(r.WriterEpoch,1,long.MaxValue,out _)||
                !r.WriterReady.HasValue||!r.CanCommit.HasValue||!Hash(r.ReviewDigest)||r.Reasons==null||r.Reasons.Length>20||
                r.Reasons.Any(v=>v==null||!Regex.IsMatch(v,"^history_[a-z_]+$")))throw Invalid();
            return ReviewSummary(r.Summary);
        }
        public static HistoricalWebReviewSummary ReviewSummary(HistoricalWebSummaryDto p)
        {
            if(p==null||new[]{p.Total,p.NewCount,p.DuplicateCount,p.Conflicts,p.Blocked,p.KnownNewNetCount}.Any(n=>!Count(n))||
                !Decimal(p.NewGrossClp,long.MinValue,long.MaxValue,out _)||!Decimal(p.KnownNewNetClp,long.MinValue,long.MaxValue,out _)||
                p.KnownNewNetCount>p.NewCount||p.SourceRows.HasValue||p.Accepted.HasValue||p.Sales.HasValue||p.Cancellations.HasValue||p.Rejected.HasValue||
                p.SignedGrossClp!=null||p.PositiveGrossClp!=null||p.CancellationGrossClp!=null||p.KnownNetClp!=null||p.KnownNetCount.HasValue||p.RejectedRequestedClp!=null||
                p.KnownRejectedRequestedCount.HasValue||p.SourceAccepted.HasValue||p.Candidates!=null||p.NeedsTimeDecision.HasValue||
                new[]{p.FromBusinessDateTime,p.ToBusinessDateTime,p.FromIsoCandidateDateTime,p.ToIsoCandidateDateTime,p.FromExportedLocalCandidateDateTime,p.ToExportedLocalCandidateDateTime}.Any(x=>x!=null))throw Invalid();
            return new HistoricalWebReviewSummary(p);
        }
        public static HistoricalWebImportReceipt Receipt(HistoricalWebReceiptDto r,string dataset,long epoch)
        {
            if(r==null)return null;
            if(!Id(r.OperationId)||!Id(r.BatchId)||r.Kind!="apply"||r.DatasetId!=dataset||!Decimal(r.Revision,0,long.MaxValue,out var revision)||
                !Decimal(r.WriterEpoch,1,long.MaxValue,out var writer)||writer!=epoch)throw Invalid();
            return new HistoricalWebImportReceipt(r.OperationId,r.BatchId,r.DatasetId,revision,writer,ReviewSummary(r.Summary));
        }
        public static HistoricalWebImportReceipt Commit(HistoricalWebResultDto r,string dataset,long epoch)
            =>Receipt(new HistoricalWebReceiptDto{OperationId=r?.OperationId,BatchId=r?.BatchId,Kind=r?.Kind,DatasetId=r?.DatasetId,Revision=r?.Revision,WriterEpoch=r?.WriterEpoch,Summary=r?.Summary},dataset,epoch);
        internal static bool Id(string v)=>Guid.TryParseExact(v,"D",out var id)&&id!=Guid.Empty&&id.ToString("D")==v;
        internal static bool Hash(string v)=>v!=null&&v.Length==64&&v.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
        internal static bool Text(string v,int max,bool empty=false)=>v!=null&&v.Length<=max&&(empty||v.Length>0)&&!v.Any(char.IsControl);
        private static bool Count(int? n,int max=100000)=>n.HasValue&&n>=0&&n<=max;
        internal static bool Decimal(string v,long min,long max,out long n)=>long.TryParse(v,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out n)&&n>=min&&n<=max&&n.ToString(CultureInfo.InvariantCulture)==v;
    }
}
