using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Import;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos
{
    public sealed partial class PosWorkflowService
    {
        internal static string QualifiedHistoryReference(HistoricalQualificationContext context,HistoricalQualificationResolution resolution)
            =>"qualified:"+context.ConfigDigest+":"+resolution.Fingerprint;
        internal static string QualifiedHistoryTimeReference(HistoricalQualificationContext context,HistoricalQualificationResolution resolution)
        {
            var ids=string.Join("\n",resolution.Evidence.Where(e=>e.Kind==HistoricalEvidenceKind.ModernTime).Select(e=>e.Id).OrderBy(x=>x,StringComparer.Ordinal));
            using var sha=SHA256.Create();
            return "qualified-time:"+context.ConfigDigest+":"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(ids))).Replace("-","").ToLowerInvariant();
        }
        internal static bool QualifiedScopeMatches(HistoricalQualificationContext context,string localScope,string merchant)
            =>Guid.TryParse(localScope,out var scope)&&Guid.TryParse(merchant,out var target)&&scope==target||
              context.LocalScopeBindings.Any(b=>b.LocalScopeId==localScope&&b.MerchantId==merchant);

        public async Task<HistoricalSalesOperationReceipt> ApplyQualifiedHistoricalSalesAsync(HistoricalSalesImportPlan plan,string configDigest,
            string qualificationFingerprint,OperatorSession session,CancellationToken ct)
        {
            if(plan==null)throw new ArgumentNullException(nameof(plan));
            var fresh=await ReadHistoricalQualificationAsync(session,ct).ConfigureAwait(false);
            var resolved=await Task.Run(()=>HistoricalQualificationResolver.Resolve(plan.ShopId,HistoricalQualificationResolver.CreateSubjects(plan.Analysis),fresh.Evidence),ct).ConfigureAwait(false);
            if(fresh.ShopId!=plan.ShopId||fresh.ConfigDigest!=configDigest||!resolved.CanPrepare||resolved.Fingerprint!=qualificationFingerprint||
                plan.Qualification==null||plan.Qualification.ConfigDigest!=configDigest||plan.Qualification.ResolverFingerprint!=qualificationFingerprint||
                !plan.Qualification.EvidenceIds.SequenceEqual(resolved.Evidence.Select(e=>e.Id).OrderBy(x=>x,StringComparer.Ordinal)))
                throw new InvalidOperationException("history_review_changed");
            var reference=QualifiedHistoryReference(fresh,resolved);var timeReference=QualifiedHistoryTimeReference(fresh,resolved);
            var nativeStart=resolved.Evidence.Where(e=>e.Kind==HistoricalEvidenceKind.NativeStart).Select(e=>e.NativeStartTicks.Value).Distinct().Single();
            if(plan.NativeStartBusinessDateTime.Ticks!=nativeStart||plan.OverlapEvidenceReference!=reference||plan.Assignments.Count!=resolved.Merchants.Count||
                plan.Analysis.AcceptedTransactions.Any(r=>r.Profile==HistoricalSalesProfile.GetnetModern&&r.Timestamp.EvidenceReference!=timeReference))
                throw new InvalidOperationException("history_review_changed");
            var bindings=await GetHistoricalMerchantBindingsAsync(session,ct).ConfigureAwait(false);
            foreach(var assignment in plan.Assignments)
            {
                if(!resolved.Merchants.TryGetValue(assignment.AliasKey,out var merchant)||assignment.EvidenceReference!=reference)
                    throw new InvalidOperationException("history_review_changed");
                var existing=bindings.SingleOrDefault(b=>b.AliasKey==assignment.AliasKey);
                if(existing==null?assignment.ScopeId!=merchant:
                    assignment.BindingId!=existing.BindingId||assignment.ScopeId!=existing.ScopeId||!QualifiedScopeMatches(fresh,existing.ScopeId,merchant))
                    throw new InvalidOperationException("history_review_changed");
            }
            ct.ThrowIfCancellationRequested();
            return await ApplyHistoricalSalesAsync(plan,session,ct).ConfigureAwait(false);
        }
    }
}
