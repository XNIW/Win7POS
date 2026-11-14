using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Synthetic authoritative transport inputs. The product DTO mapper, scope
    // resolver, parser reanalysis and service authorization all still execute.
    internal sealed class HistoricalQualifiedConfigFixture
    {
        private readonly string _shop;
        private readonly string _dataset;
        private readonly HistoricalQualifiedEvidenceDto[] _proofs;
        private int _documentRevision;
        internal int Reads { get; private set; }
        internal HistoricalQualifiedConfigFixture(string shop,HistoricalSalesImportAnalysis analysis=null,
            string datasetId="90000000-0000-4000-8000-000000003019")
        {
            _shop=shop;_dataset=datasetId;var proofs=new List<HistoricalQualifiedEvidenceDto>();
            var subjects=analysis==null?Array.Empty<HistoricalQualificationSubject>():HistoricalQualificationResolver.CreateSubjects(analysis).ToArray();
            foreach(var group in subjects.GroupBy(s=>s.Profile+"|"+s.AliasKey,StringComparer.Ordinal))
            {
                var first=group.First();var merchant=Id("merchant:"+first.AliasKey);
                foreach(var kind in new[]{HistoricalEvidenceKind.MerchantMapping,
                    first.Profile==HistoricalSalesProfile.GetnetModern?HistoricalEvidenceKind.ModernTime:HistoricalEvidenceKind.LegacyCurrency,
                    HistoricalEvidenceKind.NativeStart})
                {
                    proofs.Add(new HistoricalQualifiedEvidenceDto
                    {
                        Id=Id("proof:"+group.Key+":"+kind),ShopId=shop,Kind=kind.ToString(),State="Verified",Issuer="PlatformMaster",
                        QualificationVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,MerchantId=merchant,
                        AliasKey=kind==HistoricalEvidenceKind.MerchantMapping?first.AliasKey:"",Profile=first.Profile.ToString(),ParserVersion=first.ParserVersion,
                        Files=group.Select(s=>s.FileSha256).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
                        FromTicks=group.Min(s=>s.FromTicks).ToString(CultureInfo.InvariantCulture),ToTicks=group.Max(s=>s.ToTicks).ToString(CultureInfo.InvariantCulture),
                        PolicyVersion=first.Profile==HistoricalSalesProfile.GetnetModern?"synthetic-qualified-time-v1":"synthetic-qualified-local-v1",
                        Reference="synthetic-reviewed-document-"+kind,Interpretation=kind==HistoricalEvidenceKind.ModernTime?"IsoUtc":"Unresolved",
                        NativeStartTicks=kind==HistoricalEvidenceKind.NativeStart?new DateTime(2040,1,1).Ticks.ToString(CultureInfo.InvariantCulture):null,
                        ProviderVerified=kind==HistoricalEvidenceKind.ModernTime,DocumentSha256=Hash("synthetic-document:"+kind),DocumentUploadId=Id("document:"+kind)
                    });
                }
            }
            _proofs=proofs.OrderBy(p=>p.Id,StringComparer.Ordinal).ToArray();
        }
        internal HistoricalCloudResult Read()
        {
            Reads++;
            return new HistoricalCloudResult
            {
                QualificationSchemaVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,QualificationShopId=_shop,
                QualificationConfigDigest=Hash(Json(_proofs)),QualifiedEvidence=_proofs,
                QualificationLocalScopeBindings=Array.Empty<HistoricalLocalScopeBindingDto>(),
                QualificationRuntime=new HistoricalQualificationRuntimeDto
                {DatasetId=_dataset,WriterEpoch="1",WriterAuthority="local"}
            };
        }
        internal void Revoke(HistoricalEvidenceKind kind)
        { foreach(var proof in _proofs.Where(p=>p.Kind==kind.ToString()))proof.State="Revoked"; }
        internal void AdvanceDocumentRevision()
        {
            _documentRevision++;
            foreach(var proof in _proofs)
            {
                proof.Reference="synthetic-reviewed-revision-"+_documentRevision.ToString(CultureInfo.InvariantCulture);
                proof.DocumentSha256=Hash("synthetic-document-revision:"+_documentRevision+":"+proof.Kind);
                proof.DocumentUploadId=Id("document-revision:"+_documentRevision+":"+proof.Kind);
            }
        }
        private static string Id(string value)
        { using var hash=SHA256.Create();var digest=hash.ComputeHash(Encoding.UTF8.GetBytes(value));var bytes=new byte[16];Array.Copy(digest,bytes,bytes.Length);return new Guid(bytes).ToString("D"); }
        private static string Hash(string value)
        { using var hash=SHA256.Create();return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant(); }
        private static string Json<T>(T value)
        { using var memory=new MemoryStream();new DataContractJsonSerializer(typeof(T)).WriteObject(memory,value);return Encoding.UTF8.GetString(memory.ToArray()); }
    }
}
