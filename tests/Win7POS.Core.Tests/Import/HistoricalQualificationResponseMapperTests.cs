using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class HistoricalQualificationResponseMapperTests
{
    private const string Shop="10000000-0000-0000-0000-000000000001";
    private const string Merchant="20000000-0000-0000-0000-000000000001";
    private static readonly string Hash=new('a',64),Alias=new('b',64);
    private static readonly long From=new DateTime(2000,1,2).Ticks,To=new DateTime(2000,1,3).Ticks;
    private static HistoricalCloudResult Result()=>new()
    {
        QualificationSchemaVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,QualificationShopId=Shop,QualificationConfigDigest=new string('c',64),
        QualificationRuntime=new(){DatasetId="30000000-0000-0000-0000-000000000001",WriterEpoch="1",WriterAuthority="local"},
        QualificationLocalScopeBindings=Array.Empty<HistoricalLocalScopeBindingDto>(),
        QualifiedEvidence=Enum.GetValues<HistoricalEvidenceKind>().Where(k=>k!=HistoricalEvidenceKind.LegacyCurrency).Select(Proof).ToArray()
    };
    private static HistoricalQualifiedEvidenceDto Proof(HistoricalEvidenceKind kind)=>new()
    {
        Id="40000000-0000-0000-0000-"+(10+(int)kind).ToString("D12",CultureInfo.InvariantCulture),ShopId=Shop,Kind=kind.ToString(),State="Verified",
        Issuer="PlatformMaster",QualificationVersion=HistoricalQualifiedEvidence.CurrentQualificationVersion,MerchantId=Merchant,
        AliasKey=kind==HistoricalEvidenceKind.MerchantMapping?Alias:"",Profile="GetnetModern",ParserVersion="getnet-ooxml-v1",Files=new[]{Hash},
        FromTicks=From.ToString(CultureInfo.InvariantCulture),ToTicks=To.ToString(CultureInfo.InvariantCulture),PolicyVersion="synthetic-time-v1",Reference="synthetic-reviewed-document",
        Interpretation=kind==HistoricalEvidenceKind.ModernTime?"ExportedLocal":"Unresolved",NativeStartTicks=kind==HistoricalEvidenceKind.NativeStart?new DateTime(2001,1,1).Ticks.ToString(CultureInfo.InvariantCulture):null,
        ProviderVerified=kind==HistoricalEvidenceKind.ModernTime,DocumentSha256=new string('d',64),DocumentUploadId="50000000-0000-0000-0000-000000000001"
    };
    [TestMethod]
    public void ValidDeviceResponseMapsToImmutableQualifiedResolverInputs()
    {
        var dto=Result();var context=HistoricalQualificationResponseMapper.RequireContext(dto,Shop);
        Assert.AreEqual(Shop,context.ShopId);Assert.AreEqual(dto.QualificationConfigDigest,context.ConfigDigest);
        Assert.AreEqual(dto.QualificationRuntime.DatasetId,context.RuntimeDatasetId);Assert.AreEqual(1L,context.WriterEpoch!.Value);Assert.AreEqual("local",context.WriterAuthority);
        dto.QualifiedEvidence[0].Files[0]=new string('f',64);dto.QualifiedEvidence[0].State="Revoked";
        Assert.AreEqual(Hash,context.Evidence[0].Files[0]);Assert.AreEqual(HistoricalEvidenceState.Verified,context.Evidence[0].State);
        var subject=new HistoricalQualificationSubject(Hash,Alias,HistoricalSalesProfile.GetnetModern,"getnet-ooxml-v1",From,To,true,To,
            HistoricalSalesModernTimeInterpretation.ExportedLocal,"synthetic-time-v1");
        Assert.IsTrue(HistoricalQualificationResolver.Resolve(Shop,new[]{subject},context.Evidence).CanPrepare);
    }
    [TestMethod]
    public void NoRuntimeAndNoProofsStayEmptyRatherThanGrantingAuthority()
    {
        var dto=Result();dto.QualificationRuntime=null;dto.QualifiedEvidence=Array.Empty<HistoricalQualifiedEvidenceDto>();
        var context=HistoricalQualificationResponseMapper.RequireContext(dto,Shop);
        Assert.IsNull(context.RuntimeDatasetId);Assert.IsNull(context.WriterEpoch);Assert.IsNull(context.WriterAuthority);Assert.AreEqual(0,context.Evidence.Count);
    }
    [TestMethod]
    [DataRow("schema")][DataRow("shop")][DataRow("digest")][DataRow("null_evidence")][DataRow("too_many")][DataRow("duplicate_id")]
    [DataRow("runtime_dataset")][DataRow("runtime_epoch")][DataRow("runtime_authority")]
    public void MalformedContextIsRejectedWithClosedCode(string fault)
    {
        var dto=Result();
        switch(fault)
        {
            case "schema":dto.QualificationSchemaVersion="future";break;
            case "shop":dto.QualificationShopId="10000000-0000-0000-0000-000000000002";break;
            case "digest":dto.QualificationConfigDigest=new string('C',64);break;
            case "null_evidence":dto.QualifiedEvidence=null!;break;
            case "too_many":dto.QualifiedEvidence=Enumerable.Range(0,201).Select(_=>Proof(HistoricalEvidenceKind.MerchantMapping)).ToArray();break;
            case "duplicate_id":dto.QualifiedEvidence[1].Id=dto.QualifiedEvidence[0].Id;break;
            case "runtime_dataset":dto.QualificationRuntime.DatasetId=Guid.Empty.ToString("D");break;
            case "runtime_epoch":dto.QualificationRuntime.WriterEpoch="01";break;
            case "runtime_authority":dto.QualificationRuntime.WriterAuthority="LOCAL";break;
        }
        Invalid(dto);
    }
    [TestMethod]
    [DataRow("null")][DataRow("id")][DataRow("shop")][DataRow("kind")][DataRow("state")][DataRow("issuer")]
    [DataRow("version")][DataRow("merchant")][DataRow("alias")][DataRow("profile")][DataRow("parser")][DataRow("files")]
    [DataRow("duplicate_file")][DataRow("from")][DataRow("to")][DataRow("range")][DataRow("policy")][DataRow("reference")]
    [DataRow("interpretation")][DataRow("unexpected_start")][DataRow("provider_missing")][DataRow("document_hash")][DataRow("document_id")]
    public void MalformedProofCannotEnterResolver(string fault)
    {
        var dto=Result();var proof=dto.QualifiedEvidence[0];
        switch(fault)
        {
            case "null":dto.QualifiedEvidence[0]=null!;break;
            case "id":proof.Id=Guid.Empty.ToString("D");break;
            case "shop":proof.ShopId="10000000-0000-0000-0000-000000000002";break;
            case "kind":proof.Kind="0";break;
            case "state":proof.State="verified";break;
            case "issuer":proof.Issuer="Unqualified";break;
            case "version":proof.QualificationVersion="future";break;
            case "merchant":proof.MerchantId="alias-only";break;
            case "alias":proof.AliasKey="";break;
            case "profile":proof.Profile="Other";break;
            case "parser":proof.ParserVersion="parser\nversion";break;
            case "files":proof.Files=Array.Empty<string>();break;
            case "duplicate_file":proof.Files=new[]{Hash,Hash};break;
            case "from":proof.FromTicks="0"+proof.FromTicks;break;
            case "to":proof.ToTicks=(DateTime.MaxValue.Ticks+2).ToString(CultureInfo.InvariantCulture);break;
            case "range":proof.ToTicks=proof.FromTicks;break;
            case "policy":proof.PolicyVersion="";break;
            case "reference":proof.Reference="free\ntext";break;
            case "interpretation":proof.Interpretation="IsoUtc";break;
            case "unexpected_start":proof.NativeStartTicks=To.ToString(CultureInfo.InvariantCulture);break;
            case "provider_missing":proof.ProviderVerified=null;break;
            case "document_hash":proof.DocumentSha256="";break;
            case "document_id":proof.DocumentUploadId=Guid.Empty.ToString("D");break;
        }
        Invalid(dto);
    }
    [TestMethod]
    [DataRow("provider")][DataRow("interpretation")][DataRow("start")]
    public void TimeAndNativeStartRequireTheirSpecificQualifiedFields(string fault)
    {
        var dto=Result();var time=dto.QualifiedEvidence.Single(p=>p.Kind=="ModernTime");
        if(fault=="provider")time.ProviderVerified=false;
        if(fault=="interpretation")time.Interpretation="Unresolved";
        if(fault=="start")dto.QualifiedEvidence.Single(p=>p.Kind=="NativeStart").NativeStartTicks="0";
        Invalid(dto);
    }
    [TestMethod]
    public void RevokedEvidenceIsRetainedForResolverToDenyRatherThanDroppedOrPromoted()
    {
        var dto=Result();dto.QualifiedEvidence.Single(p=>p.Kind=="ModernTime").State="Revoked";
        var mapped=HistoricalQualificationResponseMapper.RequireContext(dto,Shop);
        var subject=new HistoricalQualificationSubject(Hash,Alias,HistoricalSalesProfile.GetnetModern,"getnet-ooxml-v1",From,To,true,To,
            HistoricalSalesModernTimeInterpretation.ExportedLocal,"synthetic-time-v1");
        var resolution=HistoricalQualificationResolver.Resolve(Shop,new[]{subject},mapped.Evidence);
        Assert.IsFalse(resolution.CanPrepare);CollectionAssert.Contains(resolution.Missing.ToArray(),HistoricalQualificationNeed.ProviderTime);
    }
    [TestMethod]
    public void BaselineScopeRelationIsPreservedExactlyAndCannotBeMutatedThroughDto()
    {
        var dto=Result();var opaque=Guid.NewGuid().ToString("N");
        dto.QualificationLocalScopeBindings=new[]{Binding(opaque)};
        var context=HistoricalQualificationResponseMapper.RequireContext(dto,Shop);
        dto.QualificationLocalScopeBindings[0].LocalScopeId="changed";
        Assert.AreEqual(opaque,context.LocalScopeBindings.Single().LocalScopeId);
        Assert.AreEqual(Merchant,context.LocalScopeBindings.Single().MerchantId);
        Assert.AreEqual(3,context.Evidence.Count);
    }
    [TestMethod]
    [DataRow("missing")][DataRow("null")][DataRow("duplicate")][DataRow("too_many")][DataRow("empty")]
    [DataRow("control")][DataRow("long")][DataRow("merchant")][DataRow("run")][DataRow("witness")]
    public void MalformedOrAmbiguousBaselineRelationIsRejected(string fault)
    {
        var dto=Result();var binding=Binding("opaque-local-scope");dto.QualificationLocalScopeBindings=new[]{binding};
        switch(fault)
        {
            case "missing":dto.QualificationLocalScopeBindings=null!;break;
            case "null":dto.QualificationLocalScopeBindings[0]=null!;break;
            case "duplicate":dto.QualificationLocalScopeBindings=new[]{binding,Binding(binding.LocalScopeId)};break;
            case "too_many":dto.QualificationLocalScopeBindings=Enumerable.Range(0,201).Select(i=>Binding("scope-"+i)).ToArray();break;
            case "empty":binding.LocalScopeId="";break;
            case "control":binding.LocalScopeId="opaque\nscope";break;
            case "long":binding.LocalScopeId=new string('x',257);break;
            case "merchant":binding.MerchantId="alias-only";break;
            case "run":binding.SourceRunId=Guid.Empty.ToString("D");break;
            case "witness":binding.WitnessId="free-text";break;
        }
        Invalid(dto);
    }
    private static HistoricalLocalScopeBindingDto Binding(string scope)=>new()
    {
        LocalScopeId=scope,MerchantId=Merchant,SourceRunId="60000000-0000-0000-0000-000000000001",WitnessId="70000000-0000-0000-0000-000000000001"
    };
    private static void Invalid(HistoricalCloudResult dto)
    {
        var error=Assert.ThrowsExactly<InvalidOperationException>(()=>HistoricalQualificationResponseMapper.RequireContext(dto,Shop));
        Assert.AreEqual(HistoricalQualificationResponseMapper.InvalidCode,error.Message);
    }
}
