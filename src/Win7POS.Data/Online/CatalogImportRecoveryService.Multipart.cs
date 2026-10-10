using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    [DataContract]
    internal sealed class CatalogImportSavedRemotePlan
    {
        [DataMember] public PosCatalogImportRecoveryPlanDocument Document { get; set; }
        [DataMember] public PosCatalogImportRecoveryMultipartResponse Receipt { get; set; }
        [IgnoreDataMember] public int PartIndex { get; set; }
        [IgnoreDataMember] public bool RequiresRetirement { get; set; }
        [IgnoreDataMember] internal string LocalRecoveryRowsJson { get; set; }
    }

    public sealed partial class CatalogImportRecoveryService
    {
        internal static string RecoveryTransportFailure<T>(PosOnlineResult<T> result,string fallback="receipt_unavailable") where T : class
        {
            return result.Denied ? "authentication_required" : result.Code=="quota_exceeded" ? "quota_exceeded" : fallback;
        }

        private PosTrustedDeviceSession ReadFreshSession(CatalogImportRecoveryDraft draft, PosTrustedDeviceSession fallback, Func<bool> authorize = null)
        {
            if (authorize != null) DemandPermission(authorize);
            if (draft.Generation != null)
                using (var conn = _factory.Open())
                    if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM pos_sync_session_generation WHERE singleton_id=1 AND active=1 AND generation_id=@id AND fingerprint=@fingerprint",
                        new { id=draft.Generation.GenerationId,fingerprint=draft.Generation.Fingerprint }) != 1)
                        throw new CatalogImportRecoveryException("trusted_generation_changed");
            var current = _freshSession?.Invoke() ?? fallback;
            if(current==null || current.ShopDeviceId!=draft.ShopDeviceId || OutboxShopBinding.GetMismatchCode(draft.Original.OriginShopId,
                draft.Original.OriginShopCode,current.ShopId,current.ShopCode).Length>0) throw new CatalogImportRecoveryException("authentication_required");
            return SnapshotTransportSession(current);
        }
        internal static bool RequiresMultipartProof(PosCatalogImportRequest original)
        {
            if(original.Items.Length>1000) return true;
            var session=CatalogImportPlanBuilder.MaximumSession();
            // Retirement has the longer schema name. Measure the full actual
            // shape, including maximum permitted escaped credentials and IDs.
            var copy=CatalogImportCorrectionTransport.CloneOriginalEnvelope(original);
            copy.PayloadHash=new string('a',64);
            var request=new PosCatalogImportReceiptRequest { SchemaVersion=PosCatalogImportReceiptContract.RetirementSchemaVersion,
                ClientImportId=original.Batch.ClientImportId,IdempotencyKey=original.Batch.IdempotencyKey,
                PayloadHash=new string('a',64),OriginalRequest=copy,
                DeviceToken=session.DeviceToken,SessionToken=session.SessionToken,PosSessionId=session.PosSessionId,
                ShopDeviceId=session.ShopDeviceId,ShopCode=session.ShopCode };
            return Encoding.UTF8.GetByteCount(Serialize(request))>CatalogImportPlanBuilder.MaximumTransportBytes;
        }

        private async Task<PosOnlineResult<PosCatalogImportReceiptResponse>> QueryTargetReceiptAsync(CatalogImportRecoveryDraft draft,
            PosAdminWebClient client,PosTrustedDeviceSession session,bool retire,CancellationToken token,Func<bool> authorize=null)
        {
            var planned=await new CatalogImportOutboxRepository(_factory).GetRemotePlanAsync(draft.TargetOriginal.Id).ConfigureAwait(false);
            if(planned!=null)
            {
                draft.RequiresPlanRetirement=planned.RequiresRetirement;
                return await CatalogImportRecoveryProofTransport.QueryPlannedReceiptAsync(client,planned,ToOutbox(draft.TargetOriginal),retire,
                    ()=>ReadFreshSession(draft,session,authorize),token).ConfigureAwait(false);
            }
            var request=BuildReplacementReceiptRequest(draft,ReadFreshSession(draft,session,authorize));
            if(retire) request.SchemaVersion=PosCatalogImportReceiptContract.RetirementSchemaVersion;
            if(RequiresMultipartProof(draft.OriginalRequest) || Encoding.UTF8.GetByteCount(Serialize(request))>CatalogImportPlanBuilder.MaximumTransportBytes)
            {
                var verified=await CatalogImportRecoveryProofTransport.EnsureProofAsync(client,ToOutbox(draft.TargetOriginal),
                    ()=>ReadFreshSession(draft,session,authorize),token).ConfigureAwait(false);
                if(!verified.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(verified,"receipt_unavailable"));
                return await CatalogImportRecoveryProofTransport.QueryReceiptAsync(client,verified.Value,retire,
                    ()=>ReadFreshSession(draft,session,authorize),token).ConfigureAwait(false);
            }
            if(retire) request.SchemaVersion=PosCatalogImportReceiptContract.RetirementSchemaVersion;
            CatalogImportSyncService.DemandTransportSize(request);
            return retire ? await client.CatalogImportRetireAsync(request,token).ConfigureAwait(false) :
                await client.CatalogImportReceiptAsync(request,token).ConfigureAwait(false);
        }

        private async Task<PosOnlineResult<PosCatalogImportReceiptResponse>> QueryRootReceiptAsync(CatalogImportRecoveryDraft draft,
            PosAdminWebClient client, PosTrustedDeviceSession session, bool retire, CancellationToken token, Func<bool> authorize=null)
        {
            var planned=await new CatalogImportOutboxRepository(_factory).GetRemotePlanAsync(draft.Original.Id).ConfigureAwait(false);
            if(planned!=null)
            {
                draft.RequiresPlanRetirement=planned.RequiresRetirement;
                return await CatalogImportRecoveryProofTransport.QueryPlannedReceiptAsync(client,planned,ToOutbox(draft.Original),retire,
                    ()=>ReadFreshSession(draft,session,authorize),token).ConfigureAwait(false);
            }
            var request = BuildReceiptRequest(draft, ReadFreshSession(draft,session,authorize));
            if (retire) request.SchemaVersion = PosCatalogImportReceiptContract.RetirementSchemaVersion;
            if (draft.OriginalRequest.Items.Length>1000 || Encoding.UTF8.GetByteCount(Serialize(request))>CatalogImportPlanBuilder.MaximumTransportBytes)
            {
                var proof = await CatalogImportRecoveryProofTransport.EnsureProofAsync(client, ToOutbox(draft.Original), () => ReadFreshSession(draft,session,authorize), token).ConfigureAwait(false);
                if (!proof.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(proof,"receipt_unavailable"));
                draft.VerifiedOriginal = proof.Value;
                return await CatalogImportRecoveryProofTransport.QueryReceiptAsync(client, proof.Value, retire, () => ReadFreshSession(draft,session,authorize), token).ConfigureAwait(false);
            }
            CatalogImportSyncService.DemandTransportSize(request);
            return retire ? await client.CatalogImportRetireAsync(request, token).ConfigureAwait(false)
                : await client.CatalogImportReceiptAsync(request, token).ConfigureAwait(false);
        }

        private async Task PrepareRemotePlanAsync(CatalogImportRecoveryDraft draft, SupplierImportSyncPreview preview,
            CatalogImportOutboxPlan plan, Func<bool> authorize, CancellationToken token)
        {
            if (draft.TransportOptions == null || draft.TransportSession == null) throw new CatalogImportRecoveryException("authentication_required");
            DemandPermission(authorize);
            using (var client = new PosAdminWebClient(draft.TransportOptions))
            {
                var proof = draft.VerifiedOriginal;
                if (proof == null)
                {
                    var verified = await CatalogImportRecoveryProofTransport.EnsureProofAsync(client, ToOutbox(draft.Original), () => ReadFreshSession(draft,draft.TransportSession,authorize), token).ConfigureAwait(false);
                    if (!verified.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(verified,"receipt_unavailable"));
                    proof = verified.Value;
                }
                PosCatalogImportRecoveryPlanDocument document;
                if(plan.PreparedDocumentJson!=null) document=Deserialize<PosCatalogImportRecoveryPlanDocument>(plan.PreparedDocumentJson);
                else {
                var parts = plan.Entries.Select((entry, index) => new PosCatalogImportRecoveryPlanPart
                {
                    Index = index,
                    Request = entry.OperationType == "catalog_import_correction"
                        ? CatalogImportRecoveryProofTransport.ProjectionForTransport(CatalogImportCorrectionTransport.ReadSavedRequest(entry.PayloadJson, entry.SharedProof), proof.VerifiedOriginalId)
                        : CatalogImportRecoveryProofTransport.ProjectionForTransport(Deserialize<PosCatalogImportRequest>(entry.PayloadJson), entry.PayloadHash)
                }).ToArray();
                for (var index = 0; index < parts.Length; index++)
                    if (parts[index].Request.Correction != null) parts[index].Request.Correction.PayloadHash = plan.Entries[index].PayloadHash;
                var carried=new HashSet<string>(draft.Supersession?.CarryCoverage.Select(c=>c.ClientItemId) ?? Array.Empty<string>(),StringComparer.Ordinal);
                var byBarcode = draft.OriginalRequest.Items.ToDictionary(i => i.Barcode, StringComparer.Ordinal);
                var byId = draft.OriginalRequest.Items.ToDictionary(i => i.ClientItemId, StringComparer.Ordinal);
                var coverage = new Dictionary<string, PosCatalogImportRecoveryCoverage>(StringComparer.Ordinal);
                foreach (var part in parts)
                {
                    if (part.Request.Correction != null)
                        foreach (var item in part.Request.Correction.Items)
                        {
                            if (!byId.ContainsKey(item.ClientItemId) || coverage.ContainsKey(item.ClientItemId)) throw new CatalogImportRecoveryException("recovery_rows_changed");
                            coverage.Add(item.ClientItemId, new PosCatalogImportRecoveryCoverage { ClientItemId = item.ClientItemId, Kind = "child", PartIndex = part.Index, ChildClientItemId = item.ClientItemId });
                        }
                    else
                        foreach (var item in part.Request.Items)
                        {
                            var original = byBarcode[item.Barcode];
                            if (coverage.ContainsKey(original.ClientItemId)) throw new CatalogImportRecoveryException("recovery_rows_changed");
                            coverage.Add(original.ClientItemId, new PosCatalogImportRecoveryCoverage { ClientItemId = original.ClientItemId, Kind = "child", PartIndex = part.Index, ChildClientItemId = item.ClientItemId });
                        }
                }
                Dictionary<string,PosCatalogImportItemRequest> allDesired = null;
                var contributorProofs = new Dictionary<long,string>();
                foreach (var original in draft.OriginalRequest.Items)
                {
                    if (coverage.ContainsKey(original.ClientItemId)) continue;
                    if(carried.Contains(original.ClientItemId))
                    {
                        // Exact predecessor identity and ACK binding is supplied
                        // by the succession hook before this document is saved.
                        coverage.Add(original.ClientItemId,new PosCatalogImportRecoveryCoverage { ClientItemId=original.ClientItemId,Kind="original_accepted" });
                        continue;
                    }
                    if (draft.ReceiptStatus == "accepted")
                    {
                        coverage.Add(original.ClientItemId, new PosCatalogImportRecoveryCoverage { ClientItemId = original.ClientItemId, Kind = "original_accepted" });
                        continue;
                    }
                    if (allDesired == null)
                    {
                        var complete = CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(preview,draft.OriginalRequest,draft.Original.PayloadHash,false,
                            Array.Empty<CatalogImportRecoveryContribution>(),null);
                        allDesired = complete.Entries.SelectMany(e => Deserialize<PosCatalogImportRequest>(e.PayloadJson).Items).ToDictionary(i => i.Barcode,StringComparer.Ordinal);
                    }
                    var desired = allDesired[original.Barcode];
                    var contribution = draft.Contributions.FirstOrDefault(c => c.Request.Items.Any(item => CatalogImportOutboxPayloadBuilder.SameIntent(item,desired)));
                    if (contribution == null) throw new CatalogImportRecoveryException("receipt_incomplete");
                    var contributed = contribution.Request.Items.Single(item => item.Barcode == original.Barcode);
                    if (!contributorProofs.TryGetValue(contribution.ContributorId,out var contributorId))
                    {
                        CatalogImportRecoveryOriginal saved;
                        using (var conn = _factory.Open())
                            saved = await Dapper.SqlMapper.QuerySingleAsync<CatalogImportRecoveryOriginal>(conn,SelectOriginal+" WHERE o.id=@id",new { id=contribution.ContributorId }).ConfigureAwait(false);
                        var verified = await CatalogImportRecoveryProofTransport.EnsureProofAsync(client,ToOutbox(saved),()=>ReadFreshSession(draft,draft.TransportSession,authorize),token).ConfigureAwait(false);
                        if (!verified.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(verified));
                        contributorId=verified.Value.VerifiedOriginalId;contributorProofs.Add(contribution.ContributorId,contributorId);
                    }
                    coverage.Add(original.ClientItemId,new PosCatalogImportRecoveryCoverage { ClientItemId=original.ClientItemId,Kind="accepted_contributor",
                        VerifiedContributorId=contributorId,ContributorClientItemId=contributed.ClientItemId,DesiredItem=desired });
                }
                document = new PosCatalogImportRecoveryPlanDocument
                {
                    PlanId = CatalogImportRecoveryProofTransport.CreateUploadId(draft.TransportSession.ShopId,draft.TransportSession.ShopDeviceId,
                        "sha256:"+CatalogImportOutboxPayloadBuilder.Sha256Hex(plan.PlanId),"plan"),
                    VerifiedOriginalId = proof.VerifiedOriginalId, Mode = draft.ReceiptStatus == "accepted" ? "correction" : "replacement",
                    Parts = parts, Coverage = draft.OriginalRequest.Items.Select(i => coverage[i.ClientItemId]).ToArray()
                };
                }
                if(plan.PreparedDocumentJson==null) ApplySupersessionToPlan(draft,document);
                if(document.VerifiedOriginalId!=proof.VerifiedOriginalId) throw new CatalogImportRecoveryException("receipt_conflict");
                var json=plan.PreparedDocumentJson??Serialize(document);
                await SavePreparedDocumentAsync(draft,json,authorize,token).ConfigureAwait(false);
                plan.PreparedDocumentJson=json;
                var uploadId=CatalogImportRecoveryProofTransport.CreateUploadId(draft.TransportSession.ShopId,draft.TransportSession.ShopDeviceId,
                    "sha256:"+CatalogImportOutboxPayloadBuilder.Sha256Hex(json),"plan");
                var response=await CatalogImportRecoveryProofTransport.RegisterPlanAsync(client,json,uploadId,document.VerifiedOriginalId,
                    ()=>ReadFreshSession(draft,draft.TransportSession,authorize),token,document).ConfigureAwait(false);
                if(!response.Success) throw new CatalogImportRecoveryException(RecoveryTransportFailure(response,"receipt_unavailable"));
                ValidateRemotePlan(plan,document,response.Value,draft.TransportSession);
                DemandPermission(authorize);
                plan.RemotePlanJson=Serialize(new CatalogImportSavedRemotePlan { Document=document,Receipt=response.Value });
            }
        }

        private static void ValidateRemotePlan(CatalogImportOutboxPlan plan,PosCatalogImportRecoveryPlanDocument document,
            PosCatalogImportRecoveryMultipartResponse response,PosTrustedDeviceSession session)
        {
            if(response?.Ok!=true || response.SchemaVersion!=PosCatalogImportRecoveryMultipartContract.SchemaVersion || response.Status!="planned" ||
                response.ShopId!=session.ShopId || response.ShopDeviceId!=session.ShopDeviceId || response.PlanId!=document.PlanId ||
                response.VerifiedOriginalId!=document.VerifiedOriginalId || response.PartCount!=plan.Entries.Count ||
                !CatalogImportRecoveryProofTransport.IsHash(response.PlanCanonicalHash) || response.Parts?.Length!=plan.Entries.Count)
                throw new CatalogImportRecoveryException("receipt_conflict");
            if(plan.Entries.Count==0 && (document.Parts.Length!=0 || response.ItemCount!=0 || response.ParentStatus!="complete"))
                throw new CatalogImportRecoveryException("receipt_conflict");
            for(var index=0;index<plan.Entries.Count;index++)
            {
                var returned=response.Parts.SingleOrDefault(p=>p.Index==index);var expected=plan.Entries[index];
                if(returned==null || returned.ClientImportId!=expected.ClientImportId || returned.IdempotencyKey!=expected.IdempotencyKey ||
                    returned.DeclaredPayloadHash!=expected.PayloadHash || !CatalogImportRecoveryProofTransport.IsHash(returned.PayloadHash))
                    throw new CatalogImportRecoveryException("receipt_conflict");
            }
        }
    }
}
