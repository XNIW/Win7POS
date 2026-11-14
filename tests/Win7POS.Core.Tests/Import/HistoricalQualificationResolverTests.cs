using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Import;

[TestClass]
public sealed class HistoricalQualificationResolverTests
{
    private const string Shop="10000000-0000-0000-0000-000000000001";
    private const string OtherShop="10000000-0000-0000-0000-000000000002";
    private const string Merchant="20000000-0000-0000-0000-000000000001";
    private const string OtherMerchant="20000000-0000-0000-0000-000000000002";
    private const string Upload="30000000-0000-0000-0000-000000000001";
    private const string Parser="getnet-ooxml-v1";
    private const string Policy="synthetic-time-v1";
    private static readonly string FileHash=new('a',64),Alias=new('b',64),OtherFile=new('c',64),Document=new('d',64),OtherAlias=new('e',64);
    private static readonly long ScopeFrom=new DateTime(2000,1,1).Ticks,ScopeTo=new DateTime(2000,2,1).Ticks;
    private static readonly long RawFrom=new DateTime(2000,1,2,9,0,0).Ticks,RawTo=new DateTime(2000,1,2,13,0,0).Ticks+1;
    private static readonly long BusinessTo=RawTo-TimeSpan.FromHours(4).Ticks;

    private static HistoricalQualificationSubject Subject(string? file=null,string? alias=null,HistoricalSalesProfile profile=HistoricalSalesProfile.GetnetModern,
        string parser=Parser,long? from=null,long? to=null,bool explicitClp=true,long? businessTo=null,bool analyzed=true,
        HistoricalSalesModernTimeInterpretation interpretation=HistoricalSalesModernTimeInterpretation.IsoUtc,string policy=Policy)
        =>new(file??FileHash,alias??Alias,profile,parser,from??RawFrom,to??RawTo,explicitClp,
            analyzed?(businessTo??(profile==HistoricalSalesProfile.GetnetLegacy?to??RawTo:BusinessTo)):null,
            analyzed?interpretation:HistoricalSalesModernTimeInterpretation.Unresolved,analyzed?policy:"");

    private static HistoricalQualifiedEvidence Proof(HistoricalEvidenceKind kind,int variant=0,string shop=Shop,string merchant=Merchant,
        string? alias=null,IEnumerable<string>? files=null,HistoricalSalesProfile profile=HistoricalSalesProfile.GetnetModern,string parser=Parser,
        long? from=null,long? to=null,HistoricalEvidenceState state=HistoricalEvidenceState.Verified,
        HistoricalEvidenceIssuer issuer=HistoricalEvidenceIssuer.PlatformMaster,string? qualification=null,string reference="synthetic-reviewed-document",
        string policy=Policy,HistoricalSalesModernTimeInterpretation interpretation=HistoricalSalesModernTimeInterpretation.IsoUtc,
        long? start=null,bool includeStart=true,bool? provider=null,string? docSha=null,string? upload=null,bool document=true)
        =>new("40000000-0000-0000-0000-"+(100+(int)kind*10+variant).ToString("D12",CultureInfo.InvariantCulture),shop,kind,state,issuer,
            qualification??HistoricalQualifiedEvidence.CurrentQualificationVersion,merchant,alias??(kind==HistoricalEvidenceKind.MerchantMapping?Alias:""),
            profile,parser,files??new[]{FileHash},from??ScopeFrom,to??ScopeTo,policy,reference,
            kind==HistoricalEvidenceKind.ModernTime?interpretation:HistoricalSalesModernTimeInterpretation.Unresolved,
            kind==HistoricalEvidenceKind.NativeStart&&includeStart?start??ScopeTo:null,provider??kind==HistoricalEvidenceKind.ModernTime,
            document?docSha??Document:"",document?upload??Upload:"");

    private static HistoricalQualifiedEvidence[] Modern() => new[]{Proof(HistoricalEvidenceKind.MerchantMapping),Proof(HistoricalEvidenceKind.ModernTime),Proof(HistoricalEvidenceKind.NativeStart)};
    private static HistoricalQualifiedEvidence[] Legacy() => new[]{Proof(HistoricalEvidenceKind.MerchantMapping,profile:HistoricalSalesProfile.GetnetLegacy),
        Proof(HistoricalEvidenceKind.LegacyCurrency,profile:HistoricalSalesProfile.GetnetLegacy),Proof(HistoricalEvidenceKind.NativeStart,profile:HistoricalSalesProfile.GetnetLegacy)};
    private static HistoricalQualificationResolution Resolve(IEnumerable<HistoricalQualifiedEvidence> proofs,HistoricalQualificationSubject? subject=null,string shop=Shop)
        =>HistoricalQualificationResolver.Resolve(shop,new[]{subject??Subject()},proofs.ToArray());
    private static void Missing(HistoricalQualificationResolution result,HistoricalQualificationNeed need)
    { Assert.IsFalse(result.CanPrepare);CollectionAssert.Contains(result.Missing.ToArray(),need); }

    [TestMethod]
    public void ModernExplicitClpUsesQualifiedProviderTimeWithoutLegacyCheckboxOrCurrencyEvidence()
    {
        var result=Resolve(Modern());
        Assert.IsTrue(result.CanPrepare);Assert.AreEqual(0,result.Missing.Count);Assert.AreEqual(Merchant,result.Merchants[Alias]);
        Assert.AreEqual(3,result.Evidence.Count);Assert.IsFalse(result.Evidence.Any(x=>x.Kind==HistoricalEvidenceKind.LegacyCurrency));
    }

    [TestMethod]
    public void LegacyNeedsItsOwnQualifiedCurrencyAndDoesNotBorrowModernEvidence()
    {
        var subject=Subject(profile:HistoricalSalesProfile.GetnetLegacy,explicitClp:false);
        Assert.IsTrue(Resolve(Legacy(),subject).CanPrepare);
        Missing(Resolve(Legacy().Where(e=>e.Kind!=HistoricalEvidenceKind.LegacyCurrency).Concat(new[]{Proof(HistoricalEvidenceKind.LegacyCurrency)}),subject),HistoricalQualificationNeed.LegacyCurrency);
    }

    [TestMethod]
    [DataRow("shop")]
    [DataRow("alias")]
    [DataRow("file")]
    [DataRow("profile")]
    [DataRow("parser")]
    [DataRow("start")]
    [DataRow("end")]
    public void MappingOutsideAnyObservedScopeDimensionCannotBeReused(string mismatch)
    {
        var mapping=Proof(HistoricalEvidenceKind.MerchantMapping,shop:mismatch=="shop"?OtherShop:Shop,
            alias:mismatch=="alias"?OtherAlias:Alias,files:new[]{mismatch=="file"?OtherFile:FileHash},
            profile:mismatch=="profile"?HistoricalSalesProfile.GetnetLegacy:HistoricalSalesProfile.GetnetModern,
            parser:mismatch=="parser"?"synthetic-other-parser":Parser,from:mismatch=="start"?RawFrom+1:ScopeFrom,to:mismatch=="end"?RawTo-1:ScopeTo);
        Missing(Resolve(Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.MerchantMapping).Append(mapping)),HistoricalQualificationNeed.MerchantMapping);
    }

    [TestMethod]
    [DataRow(HistoricalEvidenceState.Draft)]
    [DataRow(HistoricalEvidenceState.Revoked)]
    public void UnverifiedOrRevokedProviderDecisionCannotPrepare(HistoricalEvidenceState state)
    {
        Missing(Resolve(Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.ModernTime).Append(Proof(HistoricalEvidenceKind.ModernTime,state:state))),HistoricalQualificationNeed.ProviderTime);
    }

    [TestMethod]
    [DataRow(HistoricalEvidenceIssuer.Unqualified)]
    [DataRow(HistoricalEvidenceIssuer.PlatformMaster)]
    public void AReferenceOrNewMasterLabelWithoutDocumentCannotQualify(HistoricalEvidenceIssuer issuer)
    {
        var proof=Proof(HistoricalEvidenceKind.MerchantMapping,issuer:issuer,document:false);
        Assert.IsFalse(proof.IsQualified);Assert.IsFalse(proof.HasDocument);
        Missing(Resolve(Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.MerchantMapping).Append(proof)),HistoricalQualificationNeed.MerchantMapping);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void MissingApprovalReferenceStillBlocksEvenWithAnUploadedDocument(string reference)
    {
        Missing(Resolve(Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.MerchantMapping).Append(Proof(HistoricalEvidenceKind.MerchantMapping,reference:reference))),HistoricalQualificationNeed.MerchantMapping);
    }

    [TestMethod]
    [DataRow(HistoricalEvidenceIssuer.Unqualified,"getnet-qualified-input-v1")]
    [DataRow(HistoricalEvidenceIssuer.PlatformMaster,"legacy_attested")]
    [DataRow(HistoricalEvidenceIssuer.PlatformMaster,"")]
    public void ExistingAttestationsOrDocumentPresenceNeverPromoteThemselves(HistoricalEvidenceIssuer issuer,string version)
    {
        Missing(Resolve(Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.MerchantMapping).Append(Proof(HistoricalEvidenceKind.MerchantMapping,issuer:issuer,qualification:version))),HistoricalQualificationNeed.MerchantMapping);
    }

    [TestMethod]
    public void ExplicitScopedShopDelegateCanSupplyReviewedConfiguration()
    {
        var proofs=new[]{Proof(HistoricalEvidenceKind.MerchantMapping,issuer:HistoricalEvidenceIssuer.ExplicitShopDelegate),
            Proof(HistoricalEvidenceKind.ModernTime,issuer:HistoricalEvidenceIssuer.ExplicitShopDelegate),Proof(HistoricalEvidenceKind.NativeStart,issuer:HistoricalEvidenceIssuer.ExplicitShopDelegate)};
        Assert.IsTrue(Resolve(proofs).CanPrepare);
    }

    [TestMethod]
    [DataRow("provider")]
    [DataRow("sha")]
    [DataRow("upload")]
    [DataRow("decision")]
    public void ProviderTimeNeedsTheVerifiedDocumentAndAResolvedInterpretation(string absent)
    {
        var proof=Proof(HistoricalEvidenceKind.ModernTime,provider:absent!="provider",docSha:absent=="sha"?new string('g',64):Document,
            upload:absent=="upload"?Guid.Empty.ToString("D"):Upload,
            interpretation:absent=="decision"?HistoricalSalesModernTimeInterpretation.Unresolved:HistoricalSalesModernTimeInterpretation.IsoUtc);
        Missing(Resolve(Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.ModernTime).Append(proof)),HistoricalQualificationNeed.ProviderTime);
    }

    [TestMethod]
    public void ProviderAndStartRulesForAnotherMerchantDoNotCompleteTheMappedMerchant()
    {
        var proofs=new[]{Proof(HistoricalEvidenceKind.MerchantMapping),Proof(HistoricalEvidenceKind.ModernTime,merchant:OtherMerchant),Proof(HistoricalEvidenceKind.NativeStart,merchant:OtherMerchant)};
        var result=Resolve(proofs);Missing(result,HistoricalQualificationNeed.ProviderTime);Missing(result,HistoricalQualificationNeed.NativeStart);
    }

    [TestMethod]
    public void ModernCurrencyIsReadFromTheFileAndCannotBeReplacedByLegacyApproval()
    {
        Missing(Resolve(Modern().Append(Proof(HistoricalEvidenceKind.LegacyCurrency)),Subject(explicitClp:false)),HistoricalQualificationNeed.ExplicitCurrency);
    }

    [TestMethod]
    public void QualifiedTimeIsSelectedBeforeReanalysisButDoesNotYetPermitPreparation()
    {
        var result=Resolve(Modern(),Subject(analyzed:false));
        Missing(result,HistoricalQualificationNeed.TimeAnalysis);
        Assert.AreEqual(HistoricalSalesModernTimeInterpretation.IsoUtc,result.Evidence.Single(x=>x.Kind==HistoricalEvidenceKind.ModernTime).Interpretation);
    }

    [TestMethod]
    [DataRow("interpretation")]
    [DataRow("version")]
    [DataRow("unresolved")]
    public void AnalyzedBusinessTicksMustComeFromTheSameQualifiedInterpretationAndVersion(string mismatch)
    {
        var subject=Subject(interpretation:mismatch=="interpretation"?HistoricalSalesModernTimeInterpretation.ExportedLocal:
            mismatch=="unresolved"?HistoricalSalesModernTimeInterpretation.Unresolved:HistoricalSalesModernTimeInterpretation.IsoUtc,
            policy:mismatch=="version"?"synthetic-other-policy":Policy);
        Missing(Resolve(Modern(),subject),HistoricalQualificationNeed.TimeAnalysis);
    }

    [TestMethod]
    public void ExactExclusiveSourceScopeIsAcceptedButOneTickOutsideIsNot()
    {
        var proofs=new[]{Proof(HistoricalEvidenceKind.MerchantMapping,from:RawFrom,to:RawTo),Proof(HistoricalEvidenceKind.ModernTime,from:RawFrom,to:RawTo),Proof(HistoricalEvidenceKind.NativeStart,from:RawFrom,to:RawTo)};
        Assert.IsTrue(Resolve(proofs).CanPrepare);
        Missing(Resolve(proofs,Subject(from:RawFrom-1)),HistoricalQualificationNeed.MerchantMapping);
        Missing(Resolve(proofs,Subject(to:RawTo+1)),HistoricalQualificationNeed.MerchantMapping);
    }

    [TestMethod]
    public void HistoryEndingImmediatelyBeforeNativeStartIsAllowedButTheStartTickIsExcluded()
    {
        var proofs=Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.NativeStart).Append(Proof(HistoricalEvidenceKind.NativeStart,start:BusinessTo)).ToArray();
        Assert.IsTrue(Resolve(proofs,Subject(businessTo:BusinessTo)).CanPrepare);
        Missing(Resolve(proofs,Subject(businessTo:BusinessTo+1)),HistoricalQualificationNeed.NativeOverlap);
    }

    [TestMethod]
    public void LegacyOverlapUsesItsObservedCommercialTicksWithoutPcTimeZone()
    {
        var proofs=Legacy().Where(e=>e.Kind!=HistoricalEvidenceKind.NativeStart).Append(Proof(HistoricalEvidenceKind.NativeStart,profile:HistoricalSalesProfile.GetnetLegacy,start:RawTo));
        Assert.IsTrue(Resolve(proofs,Subject(profile:HistoricalSalesProfile.GetnetLegacy,explicitClp:false)).CanPrepare);
        Missing(Resolve(proofs,Subject(profile:HistoricalSalesProfile.GetnetLegacy,explicitClp:false,to:RawTo+1)),HistoricalQualificationNeed.NativeOverlap);
    }

    [TestMethod]
    public void MissingStartFactDoesNotBecomeAnInventedCutoff()
    {
        Missing(Resolve(Modern().Where(e=>e.Kind!=HistoricalEvidenceKind.NativeStart).Append(Proof(HistoricalEvidenceKind.NativeStart,includeStart:false))),HistoricalQualificationNeed.NativeStart);
    }

    [TestMethod]
    [DataRow("interpretation")]
    [DataRow("version")]
    [DataRow("start")]
    [DataRow("merchant")]
    public void ContradictoryApplicablePoliciesAreRejectedWithoutChoosingOne(string conflict)
    {
        var extra=conflict=="start"?Proof(HistoricalEvidenceKind.NativeStart,variant:1,start:ScopeTo+1):
            conflict=="merchant"?Proof(HistoricalEvidenceKind.MerchantMapping,variant:1,merchant:OtherMerchant):
            Proof(HistoricalEvidenceKind.ModernTime,variant:1,policy:conflict=="version"?"synthetic-v2":Policy,
                interpretation:conflict=="interpretation"?HistoricalSalesModernTimeInterpretation.ExportedLocal:HistoricalSalesModernTimeInterpretation.IsoUtc);
        Missing(Resolve(Modern().Append(extra)),HistoricalQualificationNeed.ConflictingPolicy);
    }

    [TestMethod]
    public void AllSelectedFilesNeedTheirOwnScopeAndAnAliasCannotChangeMerchantBetweenFiles()
    {
        var subjects=new[]{Subject(),Subject(file:OtherFile)};
        Missing(HistoricalQualificationResolver.Resolve(Shop,subjects,Modern()),HistoricalQualificationNeed.MerchantMapping);
        var other=new[]{Proof(HistoricalEvidenceKind.MerchantMapping,variant:1,merchant:OtherMerchant,files:new[]{OtherFile}),
            Proof(HistoricalEvidenceKind.ModernTime,variant:1,merchant:OtherMerchant,files:new[]{OtherFile}),Proof(HistoricalEvidenceKind.NativeStart,variant:1,merchant:OtherMerchant,files:new[]{OtherFile})};
        Missing(HistoricalQualificationResolver.Resolve(Shop,subjects,Modern().Concat(other).ToArray()),HistoricalQualificationNeed.ConflictingPolicy);
    }

    [TestMethod]
    public void AProviderDecisionForAnEarlierExportDoesNotQualifyAnotherFileForTheSameMerchant()
    {
        var subject=Subject(file:OtherFile);
        var proofs=new[]{Proof(HistoricalEvidenceKind.MerchantMapping,files:new[]{FileHash,OtherFile}),
            Proof(HistoricalEvidenceKind.ModernTime,files:new[]{FileHash}),Proof(HistoricalEvidenceKind.NativeStart,files:new[]{FileHash,OtherFile})};
        Missing(Resolve(proofs,subject),HistoricalQualificationNeed.ProviderTime);
        var qualified=proofs.Append(Proof(HistoricalEvidenceKind.ModernTime,variant:1,files:new[]{OtherFile}));
        Assert.IsTrue(Resolve(qualified,subject).CanPrepare);
        var revoked=proofs.Append(Proof(HistoricalEvidenceKind.ModernTime,variant:1,files:new[]{OtherFile},state:HistoricalEvidenceState.Revoked));
        Missing(Resolve(revoked,subject),HistoricalQualificationNeed.ProviderTime);
    }

    [TestMethod]
    public void FingerprintIsStableAcrossInputOrderButChangesForSubjectsProofsAndRevocation()
    {
        var subjects=new[]{Subject(),Subject(file:OtherFile,alias:OtherAlias)};
        var proofs=new[]{Proof(HistoricalEvidenceKind.MerchantMapping,files:new[]{OtherFile,FileHash}),
            Proof(HistoricalEvidenceKind.MerchantMapping,variant:1,alias:OtherAlias,files:new[]{OtherFile,FileHash}),
            Proof(HistoricalEvidenceKind.ModernTime,files:new[]{OtherFile,FileHash}),Proof(HistoricalEvidenceKind.NativeStart,files:new[]{OtherFile,FileHash})};
        var first=HistoricalQualificationResolver.Resolve(Shop,subjects,proofs);
        var reordered=HistoricalQualificationResolver.Resolve(Shop,subjects.Reverse().ToArray(),proofs.Reverse().ToArray());
        Assert.IsTrue(first.CanPrepare);Assert.AreEqual(first.Fingerprint,reordered.Fingerprint);
        Assert.AreNotEqual(first.Fingerprint,HistoricalQualificationResolver.Resolve(Shop,new[]{Subject(),Subject(file:OtherFile,alias:OtherAlias,to:RawTo+1)},proofs).Fingerprint);
        Assert.AreNotEqual(first.Fingerprint,HistoricalQualificationResolver.Resolve(Shop,new[]{Subject(),Subject(file:OtherFile,alias:OtherAlias,interpretation:HistoricalSalesModernTimeInterpretation.ExportedLocal)},proofs).Fingerprint);
        var revoked=proofs.Where(p=>p.Kind!=HistoricalEvidenceKind.ModernTime).Append(Proof(HistoricalEvidenceKind.ModernTime,files:new[]{OtherFile,FileHash},state:HistoricalEvidenceState.Revoked)).ToArray();
        Assert.AreNotEqual(first.Fingerprint,HistoricalQualificationResolver.Resolve(Shop,subjects,revoked).Fingerprint);
        Assert.AreNotEqual(Resolve(Array.Empty<HistoricalQualifiedEvidence>()).Fingerprint,Resolve(Array.Empty<HistoricalQualifiedEvidence>(),shop:OtherShop).Fingerprint);
    }

    [TestMethod]
    public void FingerprintIgnoresUnrelatedEvidenceAndNormalizesFileListOrder()
    {
        var a=new[]{Proof(HistoricalEvidenceKind.MerchantMapping,files:new[]{FileHash,OtherFile}),Proof(HistoricalEvidenceKind.ModernTime,files:new[]{FileHash,OtherFile}),Proof(HistoricalEvidenceKind.NativeStart,files:new[]{FileHash,OtherFile})};
        var b=new[]{Proof(HistoricalEvidenceKind.MerchantMapping,files:new[]{OtherFile,FileHash}),Proof(HistoricalEvidenceKind.ModernTime,files:new[]{OtherFile,FileHash}),Proof(HistoricalEvidenceKind.NativeStart,files:new[]{OtherFile,FileHash})};
        Assert.AreEqual(Resolve(a).Fingerprint,Resolve(b.Append(Proof(HistoricalEvidenceKind.LegacyCurrency))).Fingerprint);
    }

    [TestMethod]
    public void AnEmptySelectionNeverCreatesAPlanAndDuplicateIdentitiesAreRejected()
    {
        Assert.IsFalse(HistoricalQualificationResolver.Resolve(Shop,Array.Empty<HistoricalQualificationSubject>(),Modern()).CanPrepare);
        Assert.ThrowsExactly<ArgumentException>(()=>HistoricalQualificationResolver.Resolve(Shop,new[]{Subject(),Subject(to:RawTo+1)},Modern()));
        Assert.ThrowsExactly<ArgumentException>(()=>Resolve(Modern().Append(Proof(HistoricalEvidenceKind.ModernTime))));
        Assert.ThrowsExactly<ArgumentException>(()=>HistoricalQualificationResolver.Resolve(Shop,new[]{Subject(),Subject(alias:OtherAlias,parser:"other-version")},Modern()));
    }

    [TestMethod]
    public void ScopeAndEvidenceInputsAreImmutableAndMalformedValuesAreRejected()
    {
        var files=new[]{FileHash};var proof=Proof(HistoricalEvidenceKind.MerchantMapping,files:files);files[0]=OtherFile;
        Assert.AreEqual(FileHash,proof.Files[0]);
        Assert.ThrowsExactly<ArgumentException>(()=>Subject(file:new string('A',64)));
        Assert.ThrowsExactly<ArgumentException>(()=>Subject(from:RawTo,to:RawFrom));
        Assert.ThrowsExactly<ArgumentException>(()=>Subject(profile:HistoricalSalesProfile.GetnetLegacy,businessTo:RawTo-1));
        Assert.ThrowsExactly<ArgumentException>(()=>Proof(HistoricalEvidenceKind.MerchantMapping,files:new[]{FileHash,FileHash}));
        Assert.ThrowsExactly<ArgumentException>(()=>Proof(HistoricalEvidenceKind.MerchantMapping,reference:"untrusted\nreference"));
        Assert.ThrowsExactly<ArgumentException>(()=>HistoricalQualificationResolver.Resolve(Guid.Empty.ToString("D"),new[]{Subject()},Modern()));
    }

    [TestMethod]
    public void SubjectsUseRawModernScopeAndQualifiedBusinessEndFromTheActualParser()
    {
        var first=GetnetTestWorkbook.ModernRow("SYN-QUAL-01","Confirmada","Venta","900","900","-15","-3","882");
        var second=GetnetTestWorkbook.ModernRow("SYN-QUAL-02","Confirmada","Venta","1000","1000","-15","-3","982");
        first["FECHA DE VENTA"]="2000-01-02T09:00:00.000Z";second["FECHA DE VENTA"]="2000-01-02T13:00:00.000Z";
        first["ZONA HORARIA (UTC)"]=second["ZONA HORARIA (UTC)"]="-4";
        using var workbook=GetnetTestWorkbook.Modern(new[]{first,second});
        var unresolved=new HistoricalSalesImportAnalyzer().Analyze(new[]{workbook.Path});
        Assert.IsFalse(unresolved.HasErrors);
        var initial=HistoricalQualificationResolver.CreateSubjects(unresolved).Single();
        Assert.AreEqual(RawFrom,initial.FromTicks);Assert.AreEqual(RawTo,initial.ToTicks);Assert.IsNull(initial.ToBusinessTicks);
        Assert.AreEqual(HistoricalSalesModernTimeInterpretation.Unresolved,initial.Interpretation);Assert.IsTrue(initial.ExplicitClp);
        var approved=new HistoricalSalesImportAnalyzer().Analyze(new[]{workbook.Path},new HistoricalSalesImportOptions{
            ModernTimeInterpretation=HistoricalSalesModernTimeInterpretation.IsoUtc,TemporalPolicyVersion=Policy,TemporalEvidenceReference="synthetic-provider-reference"});
        var final=HistoricalQualificationResolver.CreateSubjects(approved).Single();
        Assert.AreEqual(initial.FileSha256,final.FileSha256);Assert.AreEqual(initial.AliasKey,final.AliasKey);Assert.AreEqual(initial.FromTicks,final.FromTicks);
        Assert.AreEqual(initial.ToTicks,final.ToTicks);Assert.AreEqual(BusinessTo,final.ToBusinessTicks);Assert.AreEqual(Policy,final.TemporalPolicyVersion);
        Assert.AreEqual(HistoricalSalesModernTimeInterpretation.IsoUtc,final.Interpretation);
    }

    [TestMethod]
    public void BareManualTimeSelectionStillRequiresQualifiedReanalysisAndDeclinesMakeNoSubject()
    {
        using var workbook=GetnetTestWorkbook.Modern(new[]{GetnetTestWorkbook.ModernRow("SYN-QUAL-03","Confirmada","Venta","900","900","-15","-3","882")});
        var bare=new HistoricalSalesImportAnalyzer().Analyze(new[]{workbook.Path},new HistoricalSalesImportOptions{ModernTimeInterpretation=HistoricalSalesModernTimeInterpretation.IsoUtc,TemporalPolicyVersion=Policy});
        var subject=HistoricalQualificationResolver.CreateSubjects(bare).Single();
        Assert.AreEqual(HistoricalSalesModernTimeInterpretation.IsoUtc,subject.Interpretation);Assert.IsNull(subject.ToBusinessTicks);
        using var declined=GetnetTestWorkbook.Modern(new[]{GetnetTestWorkbook.ModernRow("-","Rechazada","Venta","900","","","","")});
        var rejected=new HistoricalSalesImportAnalyzer().Analyze(new[]{declined.Path});
        Assert.AreEqual(0,HistoricalQualificationResolver.CreateSubjects(rejected).Count);
    }

    [TestMethod]
    public void LegacySubjectUsesTheExactLocalRangeAndUnreadableRowsCannotProduceQualificationSubjects()
    {
        var row=GetnetTestWorkbook.LegacyRow("SYN-QUAL-LEGACY","900","18","882");row["FECHA VENTA"]="02/01/2000 09:00:00";
        using var workbook=GetnetTestWorkbook.Legacy(new[]{row});
        var analysis=new HistoricalSalesImportAnalyzer().Analyze(new[]{workbook.Path});
        var subject=HistoricalQualificationResolver.CreateSubjects(analysis).Single();
        Assert.AreEqual(RawFrom,subject.FromTicks);Assert.AreEqual(RawFrom+1,subject.ToTicks);Assert.AreEqual(subject.ToTicks,subject.ToBusinessTicks);
        Assert.IsFalse(subject.ExplicitClp);Assert.AreEqual(HistoricalSalesModernTimeInterpretation.Unresolved,subject.Interpretation);
        row["VALOR TRANSACCION"]="not-money";
        using var invalid=GetnetTestWorkbook.Legacy(new[]{row});
        var failed=new HistoricalSalesImportAnalyzer().Analyze(new[]{invalid.Path});Assert.IsTrue(failed.HasErrors);
        Assert.ThrowsExactly<ArgumentException>(()=>HistoricalQualificationResolver.CreateSubjects(failed));
    }
}
