using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    /// <summary>A fresh, immutable device-scoped configuration; never an offline cache.</summary>
    public sealed class HistoricalQualificationContext
    {
        internal HistoricalQualificationContext(string shopId,string configDigest,string datasetId,long? writerEpoch,
            string writerAuthority,IEnumerable<HistoricalQualifiedEvidence> evidence,IEnumerable<HistoricalLocalScopeBinding> localBindings)
        {
            ShopId=shopId;ConfigDigest=configDigest;RuntimeDatasetId=datasetId;WriterEpoch=writerEpoch;WriterAuthority=writerAuthority;
            Evidence=new ReadOnlyCollection<HistoricalQualifiedEvidence>(evidence.ToArray());
            LocalScopeBindings=new ReadOnlyCollection<HistoricalLocalScopeBinding>(localBindings.ToArray());
        }
        public string ShopId { get; }
        public string ConfigDigest { get; }
        public string RuntimeDatasetId { get; }
        public long? WriterEpoch { get; }
        public string WriterAuthority { get; }
        public IReadOnlyList<HistoricalQualifiedEvidence> Evidence { get; }
        public IReadOnlyList<HistoricalLocalScopeBinding> LocalScopeBindings { get; }
    }
    /// <summary>Persisted baseline relation, separate from Getnet qualification evidence.</summary>
    public sealed class HistoricalLocalScopeBinding
    {
        internal HistoricalLocalScopeBinding(HistoricalLocalScopeBindingDto dto)
        { LocalScopeId=dto.LocalScopeId;MerchantId=dto.MerchantId;SourceRunId=dto.SourceRunId;WitnessId=dto.WitnessId; }
        public string LocalScopeId { get; }
        public string MerchantId { get; }
        public string SourceRunId { get; }
        public string WitnessId { get; }
    }

    public static class HistoricalQualificationResponseMapper
    {
        public const string InvalidCode="history_qualified_config_invalid";
        private static InvalidOperationException Invalid()=>new InvalidOperationException(InvalidCode);
        public static HistoricalQualificationContext RequireContext(HistoricalCloudResult result,string shopId)
        {
            try
            {
                if(result==null||!Uuid(shopId)||result.QualificationShopId!=shopId||
                    result.QualificationSchemaVersion!=HistoricalQualifiedEvidence.CurrentQualificationVersion||
                    !Digest(result.QualificationConfigDigest)||result.QualifiedEvidence==null||result.QualifiedEvidence.Length>200||
                    result.QualificationLocalScopeBindings==null||result.QualificationLocalScopeBindings.Length>200)
                    throw Invalid();
                string dataset=null,authority=null;long? epoch=null;
                if(result.QualificationRuntime!=null)
                {
                    var runtime=result.QualificationRuntime;
                    if(!Uuid(runtime.DatasetId)||!Integer(runtime.WriterEpoch,1,long.MaxValue,out var parsedEpoch)||
                        runtime.WriterAuthority!="local"&&runtime.WriterAuthority!="frozen"&&runtime.WriterAuthority!="web")throw Invalid();
                    dataset=runtime.DatasetId;epoch=parsedEpoch;authority=runtime.WriterAuthority;
                }
                var evidence=result.QualifiedEvidence.Select(dto=>ConvertEvidence(dto,shopId)).ToArray();
                if(evidence.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=evidence.Length)throw Invalid();
                var bindings=result.QualificationLocalScopeBindings.Select(ConvertBinding).ToArray();
                if(bindings.Select(b=>b.LocalScopeId).Distinct(StringComparer.Ordinal).Count()!=bindings.Length)throw Invalid();
                return new HistoricalQualificationContext(shopId,result.QualificationConfigDigest,dataset,epoch,authority,evidence,bindings);
            }
            catch(ArgumentException){throw Invalid();}
            catch(OverflowException){throw Invalid();}
        }
        private static HistoricalLocalScopeBinding ConvertBinding(HistoricalLocalScopeBindingDto dto)
        {
            if(dto==null||dto.LocalScopeId==null||dto.LocalScopeId.Length==0||dto.LocalScopeId.Length>256||dto.LocalScopeId.Any(char.IsControl)||
                !Uuid(dto.MerchantId)||!Uuid(dto.SourceRunId)||!Uuid(dto.WitnessId))throw Invalid();
            return new HistoricalLocalScopeBinding(dto);
        }
        private static HistoricalQualifiedEvidence ConvertEvidence(HistoricalQualifiedEvidenceDto dto,string shopId)
        {
            if(dto==null||!Uuid(dto.Id)||dto.ShopId!=shopId||!Uuid(dto.MerchantId)||
                !Named(dto.Kind,out HistoricalEvidenceKind kind)||!Named(dto.State,out HistoricalEvidenceState state)||
                !Named(dto.Issuer,out HistoricalEvidenceIssuer issuer)||issuer==HistoricalEvidenceIssuer.Unqualified||
                !Named(dto.Profile,out HistoricalSalesProfile profile)||!Named(dto.Interpretation,out HistoricalSalesModernTimeInterpretation interpretation)||
                dto.QualificationVersion!=HistoricalQualifiedEvidence.CurrentQualificationVersion||
                !Text(dto.ParserVersion,160)||!Text(dto.PolicyVersion,160)||!Text(dto.Reference,2048)||
                dto.AliasKey==null||dto.AliasKey.Length>0&&!Digest(dto.AliasKey)||kind==HistoricalEvidenceKind.MerchantMapping&&!Digest(dto.AliasKey)||
                dto.Files==null||dto.Files.Length==0||dto.Files.Length>10||dto.Files.Any(h=>!Digest(h))||
                dto.Files.Distinct(StringComparer.Ordinal).Count()!=dto.Files.Length||
                !Integer(dto.FromTicks,0,DateTime.MaxValue.Ticks,out var from)||!Integer(dto.ToTicks,1,DateTime.MaxValue.Ticks+1,out var to)||from>=to||
                !dto.ProviderVerified.HasValue||!Digest(dto.DocumentSha256)||!Uuid(dto.DocumentUploadId))throw Invalid();
            long? start=null;
            if(kind==HistoricalEvidenceKind.NativeStart)
            {
                if(!Integer(dto.NativeStartTicks,1,DateTime.MaxValue.Ticks,out var native))throw Invalid();start=native;
            }
            else if(dto.NativeStartTicks!=null)throw Invalid();
            if(kind==HistoricalEvidenceKind.ModernTime)
            {
                if(profile!=HistoricalSalesProfile.GetnetModern||!dto.ProviderVerified.Value||
                    interpretation!=HistoricalSalesModernTimeInterpretation.IsoUtc&&interpretation!=HistoricalSalesModernTimeInterpretation.ExportedLocal)throw Invalid();
            }
            else if(interpretation!=HistoricalSalesModernTimeInterpretation.Unresolved||
                kind==HistoricalEvidenceKind.LegacyCurrency&&profile!=HistoricalSalesProfile.GetnetLegacy)throw Invalid();
            return new HistoricalQualifiedEvidence(dto.Id,shopId,kind,state,issuer,dto.QualificationVersion,dto.MerchantId,dto.AliasKey,
                profile,dto.ParserVersion,dto.Files,from,to,dto.PolicyVersion,dto.Reference,interpretation,start,dto.ProviderVerified.Value,
                dto.DocumentSha256,dto.DocumentUploadId);
        }
        private static bool Named<T>(string value,out T parsed) where T:struct
        {
            parsed=default;
            return value!=null&&Enum.GetNames(typeof(T)).Contains(value,StringComparer.Ordinal)&&Enum.TryParse(value,false,out parsed);
        }
        private static bool Integer(string value,long minimum,long maximum,out long parsed)
            =>long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out parsed)&&parsed>=minimum&&parsed<=maximum&&
                parsed.ToString(CultureInfo.InvariantCulture)==value;
        private static bool Uuid(string value)=>Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty&&id.ToString("D")==value;
        private static bool Digest(string value)=>value!=null&&value.Length==64&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
        private static bool Text(string value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=maximum&&!value.Any(char.IsControl);
    }
}
