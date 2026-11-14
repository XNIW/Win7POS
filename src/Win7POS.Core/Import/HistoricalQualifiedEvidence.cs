using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Win7POS.Core.Import
{
    public sealed class HistoricalQualifiedInputSelection
    {
        public HistoricalQualifiedInputSelection(string configDigest,string resolverFingerprint,IEnumerable<string> evidenceIds)
        {
            if(!HistoricalQualifiedEvidence.Digest(configDigest)||!HistoricalQualifiedEvidence.Digest(resolverFingerprint)||evidenceIds==null)
                throw new ArgumentException("historical_qualification_selection_invalid");
            var ids=evidenceIds.Select(value=>Guid.TryParse(value,out var id)&&id!=Guid.Empty?id.ToString("D"):throw new ArgumentException("historical_qualification_selection_invalid")).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            if(ids.Length==0||ids.Length>200||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)throw new ArgumentException("historical_qualification_selection_invalid");
            ConfigDigest=configDigest;ResolverFingerprint=resolverFingerprint;EvidenceIds=new ReadOnlyCollection<string>(ids);
        }
        public string ConfigDigest{get;}
        public string ResolverFingerprint{get;}
        public IReadOnlyList<string> EvidenceIds{get;}
        // UUIDs contain only ASCII hex and hyphens. This is the canonical compact
        // JSON array used by both the operation header and admission envelope.
        public string EvidenceIdsJson=>"["+string.Join(",",EvidenceIds.Select(x=>"\""+x+"\""))+"]";
    }
    public enum HistoricalEvidenceKind { MerchantMapping, LegacyCurrency, ModernTime, NativeStart }
    public enum HistoricalEvidenceState { Draft, Verified, Revoked }
    public enum HistoricalEvidenceIssuer { Unqualified, PlatformMaster, ExplicitShopDelegate }
    public enum HistoricalQualificationNeed { MerchantMapping, LegacyCurrency, ProviderTime, NativeStart, ConflictingPolicy, ExplicitCurrency, TimeAnalysis, NativeOverlap }

    /// <summary>One server-qualified, revocable scope. A reference alone grants nothing.</summary>
    public sealed class HistoricalQualifiedEvidence
    {
        public const string CurrentQualificationVersion="getnet-qualified-input-v1";
        public HistoricalQualifiedEvidence(string id,string shopId,HistoricalEvidenceKind kind,HistoricalEvidenceState state,
            HistoricalEvidenceIssuer issuer,string qualificationVersion,string merchantId,string aliasKey,HistoricalSalesProfile profile,
            string parserVersion,IEnumerable<string> files,long fromTicks,long toTicks,string policyVersion,string reference,
            HistoricalSalesModernTimeInterpretation interpretation=HistoricalSalesModernTimeInterpretation.Unresolved,
            long? nativeStartTicks=null,bool providerVerified=false,string documentSha256=null,string documentUploadId=null)
        {
            Id=IdValue(id);ShopId=IdValue(shopId);MerchantId=IdValue(merchantId);
            if(!Enum.IsDefined(typeof(HistoricalEvidenceKind),kind)||!Enum.IsDefined(typeof(HistoricalEvidenceState),state)||
               !Enum.IsDefined(typeof(HistoricalEvidenceIssuer),issuer)||!Enum.IsDefined(typeof(HistoricalSalesProfile),profile)||
               !Enum.IsDefined(typeof(HistoricalSalesModernTimeInterpretation),interpretation)||fromTicks<0||toTicks<=fromTicks||toTicks>DateTime.MaxValue.Ticks+1)
                throw new ArgumentException("historical_qualification_invalid");
            var hashes=(files??throw new ArgumentNullException(nameof(files))).ToArray();
            if(hashes.Length==0||hashes.Length>10||hashes.Any(h=>!Digest(h))||hashes.Distinct(StringComparer.Ordinal).Count()!=hashes.Length)
                throw new ArgumentException("historical_qualification_invalid");
            if(nativeStartTicks.HasValue&&(nativeStartTicks<=0||nativeStartTicks>DateTime.MaxValue.Ticks))throw new ArgumentException("historical_qualification_invalid");
            Kind=kind;State=state;Issuer=issuer;QualificationVersion=Bound(qualificationVersion,160);AliasKey=Bound(aliasKey,64);
            Profile=profile;ParserVersion=Bound(parserVersion,160);Files=new ReadOnlyCollection<string>(hashes);
            FromTicks=fromTicks;ToTicks=toTicks;PolicyVersion=Bound(policyVersion,160);Reference=Bound(reference,2048);
            Interpretation=interpretation;NativeStartTicks=nativeStartTicks;ProviderVerified=providerVerified;
            DocumentSha256=Bound(documentSha256??"",64);
            var upload=Bound(documentUploadId??"",36);
            DocumentUploadId=Guid.TryParse(upload,out var uploadId)?uploadId.ToString("D"):upload;
        }
        private static string IdValue(string value)=>Guid.TryParse(value,out var id)&&id!=Guid.Empty?id.ToString("D"):throw new ArgumentException("historical_qualification_invalid");
        private static string Bound(string value,int max)=>value!=null&&value.Length<=max&&!value.Any(char.IsControl)?value:throw new ArgumentException("historical_qualification_invalid");
        internal static bool Digest(string value)=>value!=null&&value.Length==64&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
        public string Id { get; }
        public string ShopId { get; }
        public HistoricalEvidenceKind Kind { get; }
        public HistoricalEvidenceState State { get; }
        public HistoricalEvidenceIssuer Issuer { get; }
        public string QualificationVersion { get; }
        public string MerchantId { get; }
        public string AliasKey { get; }
        public HistoricalSalesProfile Profile { get; }
        public string ParserVersion { get; }
        public IReadOnlyList<string> Files { get; }
        public long FromTicks { get; }
        public long ToTicks { get; }
        public string PolicyVersion { get; }
        public string Reference { get; }
        public HistoricalSalesModernTimeInterpretation Interpretation { get; }
        public long? NativeStartTicks { get; }
        public bool ProviderVerified { get; }
        public string DocumentSha256 { get; }
        public string DocumentUploadId { get; }
        public bool IsQualified=>State==HistoricalEvidenceState.Verified&&Issuer!=HistoricalEvidenceIssuer.Unqualified&&
            QualificationVersion==CurrentQualificationVersion&&!string.IsNullOrWhiteSpace(Reference)&&!string.IsNullOrWhiteSpace(PolicyVersion)&&HasDocument;
        public bool HasDocument=>Digest(DocumentSha256)&&Guid.TryParse(DocumentUploadId,out var id)&&id!=Guid.Empty;
        public bool HasProviderDocument=>ProviderVerified&&HasDocument;
    }

    /// <summary>Observed source scope, using raw commercial timestamp candidates, never the PC zone.</summary>
    public sealed class HistoricalQualificationSubject
    {
        public HistoricalQualificationSubject(string fileSha256,string aliasKey,HistoricalSalesProfile profile,string parserVersion,
            long fromTicks,long toTicks,bool explicitClp,long? toBusinessTicks=null,
            HistoricalSalesModernTimeInterpretation interpretation=HistoricalSalesModernTimeInterpretation.Unresolved,string temporalPolicyVersion="")
        {
            if(!HistoricalQualifiedEvidence.Digest(fileSha256)||!HistoricalQualifiedEvidence.Digest(aliasKey)||
               !Enum.IsDefined(typeof(HistoricalSalesProfile),profile)||string.IsNullOrWhiteSpace(parserVersion)||parserVersion.Length>160||
               !Enum.IsDefined(typeof(HistoricalSalesModernTimeInterpretation),interpretation)||temporalPolicyVersion==null||temporalPolicyVersion.Length>160||temporalPolicyVersion.Any(char.IsControl)||
               parserVersion.Any(char.IsControl)||profile==HistoricalSalesProfile.GetnetLegacy&&toBusinessTicks.HasValue&&toBusinessTicks.Value!=toTicks||
               fromTicks<0||toTicks<=fromTicks||toTicks>DateTime.MaxValue.Ticks+1||toBusinessTicks.HasValue&&(toBusinessTicks<=0||toBusinessTicks>DateTime.MaxValue.Ticks+1))throw new ArgumentException("historical_qualification_subject_invalid");
            FileSha256=fileSha256;AliasKey=aliasKey;Profile=profile;ParserVersion=parserVersion;FromTicks=fromTicks;ToTicks=toTicks;ExplicitClp=explicitClp;
            ToBusinessTicks=toBusinessTicks??(profile==HistoricalSalesProfile.GetnetLegacy?(long?)toTicks:null);
            Interpretation=interpretation;TemporalPolicyVersion=temporalPolicyVersion;
        }
        public string FileSha256 { get; }
        public string AliasKey { get; }
        public HistoricalSalesProfile Profile { get; }
        public string ParserVersion { get; }
        public long FromTicks { get; }
        public long ToTicks { get; }
        public bool ExplicitClp { get; }
        public long? ToBusinessTicks { get; }
        public HistoricalSalesModernTimeInterpretation Interpretation { get; }
        public string TemporalPolicyVersion { get; }
    }
}
