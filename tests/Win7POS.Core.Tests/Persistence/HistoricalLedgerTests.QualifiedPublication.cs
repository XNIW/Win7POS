using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;

namespace Win7POS.Core.Tests.Persistence;

public sealed partial class HistoricalLedgerTests
{
    [TestMethod]
    public async Task QualifiedSelectionBindsRequestAndPublicationAndSurvivesRestore()
    {
        using var f=new Fixture();var original=await f.Plan();
        var selection=new HistoricalQualifiedInputSelection(new string('a',64),new string('b',64),new[]{"22222222-2222-4222-8222-222222222222","11111111-1111-4111-8111-111111111111"});
        var plan=new HistoricalSalesImportPlan(original.OperationId,original.ShopId,original.DatasetId,original.DataRevision,original.RestoreEpoch,
            original.Analysis,original.Assignments,original.NativeStartBusinessDateTime,original.OverlapEvidenceReference,qualification:selection);
        Assert.AreNotEqual(original.RequestFingerprint,plan.RequestFingerprint);
        const string lineage="33333333-3333-4333-8333-333333333333";
        using var old=HistoricalPublicationDraft.ForApply(original,"device",lineage);
        using var draft=HistoricalPublicationDraft.ForApply(plan,"device",lineage);
        Assert.AreNotEqual(old.ContentDigest,draft.ContentDigest);
        Assert.IsFalse(old.ReadPage(1,default).Json.Contains("qualification_config_digest"));
        Assert.IsFalse(old.ReadPage(1,default).Json.Contains("parser_version"));
        var first=draft.ReadPage(1,default).Json;
        Assert.IsTrue(first.Contains("qualification_config_digest"));
        Assert.IsTrue(first.Contains(selection.ConfigDigest));
        Assert.IsTrue(first.Contains("\"parser_version\":\"getnet-ooxml-v1\""));
        using var restored=new HistoricalPublicationDraft(draft.OperationId,draft.Kind,draft.ShopId,draft.DeviceId,draft.DatasetId,
            draft.RestoreEpoch,draft.BaseRevision,draft.RequestFingerprint,lineage,draft.ContentDigest,draft.PageCount,draft.Pages(default),default);
        Assert.IsNotNull(restored.Qualification);
        Assert.AreEqual(selection.ConfigDigest,restored.Qualification.ConfigDigest);
        Assert.AreEqual(selection.ResolverFingerprint,restored.Qualification.ResolverFingerprint);
        Assert.AreEqual(selection.EvidenceIdsJson,restored.Qualification.EvidenceIdsJson);
        Assert.AreEqual("[\"11111111-1111-4111-8111-111111111111\",\"22222222-2222-4222-8222-222222222222\"]",selection.EvidenceIdsJson);
        var changed=new HistoricalQualifiedInputSelection(new string('c',64),selection.ResolverFingerprint,selection.EvidenceIds);
        var changedPlan=new HistoricalSalesImportPlan(original.OperationId,original.ShopId,original.DatasetId,original.DataRevision,original.RestoreEpoch,
            original.Analysis,original.Assignments,original.NativeStartBusinessDateTime,original.OverlapEvidenceReference,qualification:changed);
        Assert.AreNotEqual(plan.RequestFingerprint,changedPlan.RequestFingerprint);
    }

    [TestMethod]
    public void QualifiedAdmissionEnvelopeIsOptionalAndUsesCanonicalWireNames()
    {
        static string Json(HistoricalCloudPayload payload)
        {
            using var stream=new MemoryStream();new DataContractJsonSerializer(typeof(HistoricalCloudPayload)).WriteObject(stream,payload);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        Assert.IsFalse(Json(new HistoricalCloudPayload()).Contains("qualification"));
        var json=Json(new HistoricalCloudPayload{Qualification=new HistoricalCloudQualification{ConfigDigest=new string('a',64),ResolverFingerprint=new string('b',64),EvidenceIds=new[]{"11111111-1111-4111-8111-111111111111"}}});
        Assert.IsTrue(json.Contains("\"qualification\":{\"config_digest\":"));
        Assert.IsTrue(json.Contains("\"resolver_fingerprint\":"));Assert.IsTrue(json.Contains("\"evidence_ids\":["));
        Assert.ThrowsExactly<ArgumentException>(()=>new HistoricalQualifiedInputSelection(new string('a',64),new string('b',64),Array.Empty<string>()));
        Assert.ThrowsExactly<ArgumentException>(()=>new HistoricalQualifiedInputSelection(new string('a',64),new string('b',64),new[]{Guid.Empty.ToString("D")}));
        Assert.ThrowsExactly<ArgumentException>(()=>new HistoricalQualifiedInputSelection(new string('a',64),new string('b',64),new[]{"11111111-1111-4111-8111-111111111111","11111111-1111-4111-8111-111111111111"}));
    }
}
