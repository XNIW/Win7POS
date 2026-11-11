using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Reports;
using Win7POS.Data.Reports;
using Win7POS.Data.Backup;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalSalesOperationIdentity
    {
        public HistoricalSalesOperationIdentity(HistoricalImportOperationKind kind, string operationId, string requestFingerprint, string shopId, string datasetId)
        {
            if (!Enum.IsDefined(typeof(HistoricalImportOperationKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Kind = kind; OperationId = HistoricalMerchantAssignment.Required(operationId); RequestFingerprint = HistoricalMerchantAssignment.Required(requestFingerprint);
            ShopId = HistoricalMerchantAssignment.Required(shopId); DatasetId = HistoricalMerchantAssignment.Required(datasetId);
        }
        public HistoricalImportOperationKind Kind { get; }
        public string OperationId { get; }
        public string RequestFingerprint { get; }
        public string ShopId { get; }
        public string DatasetId { get; }
    }

    public sealed class HistoricalMerchantAssignment
    {
        public HistoricalMerchantAssignment(HistoricalSalesMerchant merchant, string scopeId, string bindingId, string evidenceReference)
        {
            AliasKey = MerchantKey(merchant); ScopeId = Required(scopeId); BindingId = Required(bindingId); EvidenceReference = Required(evidenceReference);
        }
        public string AliasKey { get; }
        public string ScopeId { get; }
        public string BindingId { get; }
        public string EvidenceReference { get; }
        internal static string Required(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 ? value : throw new ArgumentException("historical_evidence_required");
        public static string MerchantKey(HistoricalSalesMerchant m)
        {
            if (m == null || !m.HasEvidence) throw new ArgumentException("historical_merchant_missing");
            return HistoricalSalesImportPlan.Hash(new[] { "getnet-merchant-alias-v1", m.Local, m.Seller, m.SellerId, m.LocalNumber, m.Rut });
        }
    }

    public sealed class HistoricalSalesImportPlan
    {
        public const int MaximumMerchantAssignments = 200;
        public HistoricalSalesImportPlan(string operationId, string shopId, string datasetId, long dataRevision, long restoreEpoch,
            HistoricalSalesImportAnalysis analysis, IEnumerable<HistoricalMerchantAssignment> assignments,
            DateTime nativeStartBusinessDateTime, string overlapEvidenceReference,
            IEnumerable<HistoricalSalesCorrectionApproval> correctionApprovals = null)
        {
            OperationId = HistoricalMerchantAssignment.Required(operationId); ShopId = HistoricalMerchantAssignment.Required(shopId);
            DatasetId = HistoricalMerchantAssignment.Required(datasetId); DataRevision = dataRevision; RestoreEpoch = restoreEpoch;
            Analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
            Assignments = new ReadOnlyCollection<HistoricalMerchantAssignment>((assignments ?? throw new ArgumentNullException(nameof(assignments))).ToArray());
            if (Assignments.Count > MaximumMerchantAssignments) throw new InvalidOperationException("historical_merchant_assignment_limit");
            NativeStartBusinessDateTime = DateTime.SpecifyKind(nativeStartBusinessDateTime, DateTimeKind.Unspecified);
            OverlapEvidenceReference = HistoricalMerchantAssignment.Required(overlapEvidenceReference);
            CorrectionApprovals = new ReadOnlyCollection<HistoricalSalesCorrectionApproval>((correctionApprovals ?? Array.Empty<HistoricalSalesCorrectionApproval>()).ToArray());
            if (CorrectionApprovals.Count > HistoricalSalesApplyPreview.MaxDisplayedCorrections || CorrectionApprovals.Any(a => a == null) ||
                CorrectionApprovals.GroupBy(a => a.SourceFileId + "\n" + a.SourceSheet + "\n" + a.SourceRow).Any(g => g.Count() != 1)) throw new InvalidOperationException("historical_correction_approval_invalid");
            if (dataRevision < 0 || restoreEpoch < 0 || !analysis.CanApply) throw new InvalidOperationException("historical_analysis_not_ready");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Assignments) if (!keys.Add(item.AliasKey)) throw new InvalidOperationException("historical_ambiguous_merchant_binding");
            foreach (var row in analysis.AcceptedTransactions)
                if (!row.BusinessDateTime.HasValue || row.BusinessDateTime.Value >= NativeStartBusinessDateTime ||
                    !keys.Contains(HistoricalMerchantAssignment.MerchantKey(row.Merchant))) throw new InvalidOperationException("historical_overlap_or_binding_unresolved");
            RequestFingerprint = Hash(FingerprintFields());
        }
        private IEnumerable<string> FingerprintFields()
        {
            foreach (var value in new[] { "historical-apply-request-v1", OperationId, ShopId, DatasetId,
                NativeStartBusinessDateTime.Ticks.ToString(CultureInfo.InvariantCulture), OverlapEvidenceReference }) yield return value;
            foreach (var f in Analysis.Files.OrderBy(x => x.FileId, StringComparer.Ordinal)) { yield return f.FileId; yield return f.Sha256; }
            foreach (var a in Assignments.OrderBy(x => x.AliasKey, StringComparer.Ordinal))
            { yield return a.AliasKey; yield return a.ScopeId; yield return a.BindingId; yield return a.EvidenceReference; }
            foreach (var r in Analysis.AcceptedTransactions) foreach (var value in new[] { r.Source.FileId, r.Source.RowNumber.ToString(CultureInfo.InvariantCulture),
                r.ExternalNamespace, r.ExternalId, r.BusinessDateTime.Value.Ticks.ToString(CultureInfo.InvariantCulture), r.Timestamp.PolicyVersion, r.Timestamp.Interpretation.ToString(),
                r.Profile == HistoricalSalesProfile.GetnetLegacy ? "LegacyLocal;CLP=explicitly-approved" : "Modern;CLP=exported",
                r.Timestamp.EvidenceReference, r.Timestamp.RawTimestamp, r.Timestamp.RawOffset,
                r.Timestamp.OccurredAtUtc?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? "null",
                HistoricalMerchantAssignment.MerchantKey(r.Merchant), r.MovementKind.ToString(), r.TransactionStatus, r.SettlementStatus,
                r.CardSubtype.ToString(), r.PaymentId, r.AuthorizationCode, r.GrossClp.ToString(CultureInfo.InvariantCulture),
                r.CommissionClp?.ToString(CultureInfo.InvariantCulture) ?? "null", r.CommissionVatClp?.ToString(CultureInfo.InvariantCulture) ?? "null",
                r.NetClp?.ToString(CultureInfo.InvariantCulture) ?? "null" }) yield return value;
            foreach (var r in Analysis.RejectedRows) foreach (var value in new[] { "rejected", r.Source.FileId, r.Source.SheetName,
                r.Source.RowNumber.ToString(CultureInfo.InvariantCulture), r.TransactionStatus, r.RequestedClp?.ToString(CultureInfo.InvariantCulture) ?? "null" }) yield return value;
            foreach (var approval in CorrectionApprovals.OrderBy(a => a.SourceFileId, StringComparer.Ordinal).ThenBy(a => a.SourceSheet, StringComparer.Ordinal).ThenBy(a => a.SourceRow))
            { yield return "approved-inactive-correction-v1"; yield return approval.Fingerprint; }
        }
        public string OperationId { get; }
        public string ShopId { get; }
        public string DatasetId { get; }
        public long DataRevision { get; }
        public long RestoreEpoch { get; }
        public HistoricalSalesImportAnalysis Analysis { get; }
        public IReadOnlyList<HistoricalMerchantAssignment> Assignments { get; }
        public IReadOnlyList<HistoricalSalesCorrectionApproval> CorrectionApprovals { get; }
        public DateTime NativeStartBusinessDateTime { get; }
        public string OverlapEvidenceReference { get; }
        public string RequestFingerprint { get; }
        internal static string Hash(IEnumerable<string> fields)
        {
            using (var sha = SHA256.Create())
            using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            {
                // Keep the existing UTF-16 length + ':' + value protocol byte for
                // byte, without retaining a concatenated request or UTF-8 array.
                // A small buffer also bounds per-merchant hash allocations.
                using (var writer = new StreamWriter(crypto, new UTF8Encoding(false), 256, true))
                    foreach (var field in fields)
                    { var value = field ?? string.Empty; writer.Write(value.Length.ToString(CultureInfo.InvariantCulture)); writer.Write(':'); writer.Write(value); }
                crypto.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        public static string UndoFingerprint(string operationId, string shopId, string datasetId, string batchId, long batchRevision, long restoreEpoch)
            => Hash(new[] { "historical-undo-request-v1", operationId, shopId, datasetId, batchId, batchRevision.ToString(CultureInfo.InvariantCulture), restoreEpoch.ToString(CultureInfo.InvariantCulture) });
    }

    [DataContract]
    public sealed class HistoricalSalesOperationReceipt
    {
        [DataMember] public string OperationId { get; internal set; }
        [DataMember] public string OperationKind { get; internal set; }
        [DataMember] public string BatchId { get; internal set; }
        [DataMember] public int InsertedCount { get; internal set; }
        [DataMember] public int DuplicateCount { get; internal set; }
        [DataMember] public int RejectedCount { get; internal set; }
        [DataMember] public long SignedAmountClp { get; internal set; }
        [DataMember] public int CorrectionCount { get; internal set; }
        [DataMember] public HistoricalSalesCorrectionApproval[] CorrectionApprovals { get; internal set; } = Array.Empty<HistoricalSalesCorrectionApproval>();
        // A replay always returns the original receipt. Current state is a separate observation.
        public string CurrentBatchState { get; internal set; }
        public long CurrentBatchRevision { get; internal set; }
        public bool IsReplay { get; internal set; }
    }

    public sealed class HistoricalSalesBatch
    {
        public string Id { get; set; }
        public string State { get; set; }
        public string ApplyOperationId { get; set; }
        public long Revision { get; set; }
        public long CreatedAt { get; set; }
        public long AmountClp { get; set; }
        public long Rows { get; set; }
    }

    public sealed class HistoricalMerchantBindingInfo
    {
        public string AliasKey { get; set; }
        public string ScopeId { get; set; }
        public string BindingId { get; set; }
    }

    public sealed class HistoricalSalesDatasetState
    {
        public string DatasetId { get; set; }
        public long DataRevision { get; set; }
        public string RestoreLineageId { get; set; }
    }

    /// <summary>Isolated historical ledger; never calls the native sale, stock, fiscal or outbox writers.</summary>
    public sealed class HistoricalSalesImportService
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly HistoricalSalesImportTestHooks _testHooks;
        private readonly Func<long> _restoreEpoch;
        private readonly Func<SalesReportAuthorityStamp> _authority;
        private readonly SalesReportSnapshotCoordinator _coordinator;
        public HistoricalSalesImportService(SqliteConnectionFactory factory, Func<SalesReportAuthorityStamp> authority)
            : this(factory, authority, null) { }
        internal HistoricalSalesImportService(SqliteConnectionFactory factory, Func<SalesReportAuthorityStamp> authority, HistoricalSalesImportTestHooks testHooks)
        {
            _testHooks = testHooks;
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _restoreEpoch = () => _authority().RestoreEpoch;
            _coordinator = SalesReportSnapshotCoordinator.ForDatabase(factory.DbPath);
        }
        public async Task<HistoricalSalesDatasetState> ReadDatasetAsync(CancellationToken ct)
        {
            using var c = await _factory.OpenAsync(ct).ConfigureAwait(false);
            return await c.QuerySingleAsync<HistoricalSalesDatasetState>(new CommandDefinition("SELECT dataset_id AS DatasetId,data_revision AS DataRevision,(SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id') AS RestoreLineageId FROM historical_import_dataset WHERE singleton_id=1", cancellationToken: ct)).ConfigureAwait(false);
        }
        public Task<HistoricalPublicationDraft> PreparePublicationDraftAsync(HistoricalSalesImportPlan plan,string device,CancellationToken ct)
            =>Task.Run(()=>
            {
                if(plan==null)throw new ArgumentNullException(nameof(plan));
                ct.ThrowIfCancellationRequested();var stamp=_authority();
                if(stamp.RestoreEpoch!=plan.RestoreEpoch||plan.CorrectionApprovals.Count>HistoricalSalesApplyPreview.MaxDisplayedCorrections)
                    throw new InvalidOperationException("historical_plan_stale");
                var owners=new Dictionary<long,HistoricalPublicationCorrectionReference>();string lineage;
                using(var c=_factory.Open())
                using(var tx=c.BeginTransaction(deferred:true))
                {
                    var state=c.QuerySingle<HistoricalSalesDatasetState>("SELECT dataset_id AS DatasetId,data_revision AS DataRevision,(SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id') AS RestoreLineageId FROM historical_import_dataset WHERE singleton_id=1",transaction:tx);
                    if(state.DatasetId!=plan.DatasetId||state.DataRevision!=plan.DataRevision)throw new InvalidOperationException("historical_plan_stale");
                    lineage=state.RestoreLineageId;
                    foreach(var id in plan.CorrectionApprovals.Select(x=>x.PreviousTransactionId).Distinct())
                    {
                        ct.ThrowIfCancellationRequested();
                        var owner=c.QuerySingleOrDefault<PublicationOwnerRow>(@"SELECT t.batch_id AS BatchId,b.apply_operation_id AS ApplyOperationId
FROM historical_transactions t JOIN historical_import_batch b ON b.id=t.batch_id AND b.shop_id=t.shop_id
JOIN historical_import_operation o ON o.operation_id=b.apply_operation_id AND o.batch_id=b.id AND o.shop_id=b.shop_id AND o.dataset_id=b.dataset_id AND o.operation_kind='Apply'
WHERE t.id=@id AND t.shop_id=@ShopId AND b.dataset_id=@DatasetId AND t.is_active=0",new{id,plan.ShopId,plan.DatasetId},tx);
                        if(owner==null)throw new InvalidOperationException("history_publication_correction_owner_required");
                        owners.Add(id,new HistoricalPublicationCorrectionReference(plan,lineage,id,owner.BatchId,owner.ApplyOperationId));
                    }
                    tx.Commit();
                }
                var current=_authority();ct.ThrowIfCancellationRequested();
                if(current.RestoreEpoch!=stamp.RestoreEpoch||current.AuthorityInstanceEpoch!=stamp.AuthorityInstanceEpoch)throw new InvalidOperationException("historical_plan_stale");
                // The SQLite snapshot is closed before content spooling. No
                // database lock spans serialization or later HTTP preparation.
                return HistoricalPublicationDraft.ForApply(plan,device,lineage,owners,ct);
            },ct);
        public Task<HistoricalPublicationDraft> PrepareUndoPublicationDraftAsync(string operation,string shop,string device,string dataset,long restore,
            string batchId,long batchRevision,CancellationToken ct)
            =>Task.Run(()=>
            {
                ct.ThrowIfCancellationRequested();var stamp=_authority();
                if(stamp.RestoreEpoch!=restore)throw new InvalidOperationException("historical_plan_stale");
                HistoricalSalesDatasetState state;PublicationOwnerRow batch;
                using(var c=_factory.Open())
                using(var tx=c.BeginTransaction(deferred:true))
                {
                    state=c.QuerySingle<HistoricalSalesDatasetState>("SELECT dataset_id AS DatasetId,data_revision AS DataRevision,(SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id') AS RestoreLineageId FROM historical_import_dataset WHERE singleton_id=1",transaction:tx);
                    batch=c.QuerySingleOrDefault<PublicationOwnerRow>(@"SELECT b.id AS BatchId,b.apply_operation_id AS ApplyOperationId
FROM historical_import_batch b JOIN historical_import_operation o ON o.operation_id=b.apply_operation_id AND o.batch_id=b.id AND o.shop_id=b.shop_id AND o.dataset_id=b.dataset_id AND o.operation_kind='Apply'
WHERE b.id=@batchId AND b.shop_id=@shop AND b.dataset_id=@dataset AND b.revision=@batchRevision AND b.state='Active'",new{batchId,shop,dataset,batchRevision},tx);
                    if(state.DatasetId!=dataset||batch==null)throw new InvalidOperationException("historical_batch_stale");
                    tx.Commit();
                }
                var current=_authority();ct.ThrowIfCancellationRequested();
                if(current.RestoreEpoch!=stamp.RestoreEpoch||current.AuthorityInstanceEpoch!=stamp.AuthorityInstanceEpoch)throw new InvalidOperationException("historical_plan_stale");
                return HistoricalPublicationDraft.ForUndo(operation,shop,device,dataset,restore,state.DataRevision,batchId,batchRevision,batch.ApplyOperationId,state.RestoreLineageId);
            },ct);
        private sealed class PublicationOwnerRow{public string BatchId{get;set;} public string ApplyOperationId{get;set;}}
        public async Task<IReadOnlyList<HistoricalMerchantBindingInfo>> GetMerchantBindingsAsync(string shopId, CancellationToken ct)
        {
            HistoricalMerchantAssignment.Required(shopId);
            using var c = await _factory.OpenAsync(ct).ConfigureAwait(false);
            return (await c.QueryAsync<HistoricalMerchantBindingInfo>(new CommandDefinition("SELECT alias_key AS AliasKey,scope_id AS ScopeId,id AS BindingId FROM historical_merchant_binding WHERE shop_id=@shopId AND provider='getnet' AND is_active=1", new { shopId }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        }
        public async Task<IReadOnlyList<HistoricalSalesBatch>> GetBatchesAsync(string shopId, CancellationToken ct)
        {
            HistoricalMerchantAssignment.Required(shopId);
            using var c = await _factory.OpenAsync(ct).ConfigureAwait(false);
            return (await c.QueryAsync<HistoricalSalesBatch>(new CommandDefinition(@"SELECT b.id,b.state,b.apply_operation_id AS ApplyOperationId,b.revision,b.created_at AS CreatedAt,COALESCE(SUM(t.signed_amount_clp),0) AS AmountClp,COUNT(t.id) AS Rows
FROM historical_import_batch b LEFT JOIN historical_transactions t ON t.batch_id=b.id AND t.is_active=1 WHERE b.shop_id=@shopId GROUP BY b.id ORDER BY b.created_at DESC,b.id LIMIT 200", new { shopId }, cancellationToken: ct)).ConfigureAwait(false)).ToArray();
        }
        public async Task<IReadOnlyList<HistoricalSalesBatch>> GetBatchesAsync(string shopId, HistoricalImportAuthorizationCommitGuard guard, CancellationToken ct)
        {
            guard.DemandStillValid();
            if (!string.Equals(shopId, guard.ShopId, StringComparison.Ordinal)) throw new InvalidOperationException("historical_shop_scope_mismatch");
            using var c = await _factory.OpenAsync(ct).ConfigureAwait(false);
            var rows = await c.QueryAsync<HistoricalSalesBatch>(new CommandDefinition(@"SELECT b.id,b.state,b.apply_operation_id AS ApplyOperationId,b.revision,b.created_at AS CreatedAt,COALESCE(SUM(t.signed_amount_clp),0) AS AmountClp,COUNT(t.id) AS Rows
FROM historical_import_batch b LEFT JOIN historical_transactions t ON t.batch_id=b.id AND t.is_active=1 WHERE b.shop_id=@shopId GROUP BY b.id ORDER BY b.created_at DESC,b.id LIMIT 200", new { shopId }, cancellationToken: ct)).ConfigureAwait(false);
            guard.DemandStillValid(); return rows.ToArray();
        }
        public async Task<HistoricalSalesOperationReceipt> GetReceiptAsync(string operationId, string shopId, CancellationToken ct)
        {
            HistoricalMerchantAssignment.Required(operationId); HistoricalMerchantAssignment.Required(shopId);
            using var c = await _factory.OpenExistingForRecoveryAsync(ct).ConfigureAwait(false);
            using var tx = c.BeginTransaction(deferred: true);
            var json = await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT receipt_json FROM historical_import_operation WHERE operation_id=@operationId AND shop_id=@shopId", new { operationId, shopId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (json == null) return null;
            var receipt = Deserialize(json);
            var batch = c.QuerySingle<HistoricalSalesBatch>("SELECT state,revision FROM historical_import_batch WHERE id=@BatchId AND shop_id=@shopId", new { receipt.BatchId, shopId }, tx);
            receipt.CurrentBatchState = batch.State; receipt.CurrentBatchRevision = batch.Revision;
            tx.Commit(); return receipt;
        }
        public async Task<HistoricalSalesOperationReceipt> TryReplayAsync(HistoricalImportAuthorizationCommitGuard guard, CancellationToken ct)
        {
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            guard.DemandStillValid();
            if (_restoreEpoch() != guard.RestoreEpoch) throw new InvalidOperationException("historical_plan_stale");
            var receipt = await TryReplayAsync(new HistoricalSalesOperationIdentity(guard.Kind, guard.OperationId, guard.RequestFingerprint, guard.ShopId, guard.DatasetId), ct).ConfigureAwait(false);
            guard.DemandStillValid();
            if (_restoreEpoch() != guard.RestoreEpoch) throw new InvalidOperationException("historical_plan_stale");
            return receipt;
        }
        public async Task<HistoricalSalesOperationReceipt> TryReplayAsync(HistoricalSalesOperationIdentity identity, CancellationToken ct)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            using var c = await _factory.OpenExistingForRecoveryAsync(ct).ConfigureAwait(false);
            using var tx = c.BeginTransaction(deferred: true);
            var dataset = await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT dataset_id FROM historical_import_dataset WHERE singleton_id=1", transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
            if (dataset != identity.DatasetId) throw new InvalidOperationException("historical_plan_stale");
            ct.ThrowIfCancellationRequested();
            var receipt = ReadTerminalOperation(c, tx, identity);
            ct.ThrowIfCancellationRequested(); tx.Commit(); return receipt;
        }
        public async Task<HistoricalSalesApplyPreview> PreviewAsync(HistoricalSalesImportPlan plan, CancellationToken ct)
        {
            using var c = await _factory.OpenAsync(ct).ConfigureAwait(false);
            using var tx = c.BeginTransaction(deferred: true);
            var state = c.QuerySingle<HistoricalSalesDatasetState>("SELECT dataset_id AS DatasetId,data_revision AS DataRevision FROM historical_import_dataset WHERE singleton_id=1", transaction: tx);
            if (state.DatasetId != plan.DatasetId || state.DataRevision != plan.DataRevision || _restoreEpoch() != plan.RestoreEpoch) throw new InvalidOperationException("historical_plan_stale");
            var preview = new HistoricalSalesApplyPreview();
            var pending = new Dictionary<string, HistoricalSalesTransaction>(StringComparer.Ordinal);
            var matchedApprovals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in plan.Analysis.AcceptedTransactions)
            {
                ct.ThrowIfCancellationRequested();
                var mapping = plan.Assignments.Single(x => x.AliasKey == HistoricalMerchantAssignment.MerchantKey(row.Merchant));
                var key = HistoricalSalesImportPlan.Hash(new[] { mapping.ScopeId, row.ExternalNamespace, row.ExternalId });
                var existing = ReadMovement(c, tx, plan.ShopId, mapping.ScopeId, row, true);
                if (existing == null && pending.TryGetValue(key, out var previous)) existing = ExistingMovement.From(previous);
                if (existing != null)
                {
                    if (existing.Value.ConflictsWith(EconomicValue(row))) preview.ConflictCount++;
                    else preview.DuplicateCount++;
                    continue;
                }
                var inactive = ReadMovement(c, tx, plan.ShopId, mapping.ScopeId, row, false);
                if (inactive != null && inactive.Value.ConflictsWith(EconomicValue(row)))
                {
                    var candidate = CorrectionCandidate(plan, row, inactive); preview.AddCorrection(candidate);
                    var approval = plan.CorrectionApprovals.SingleOrDefault(candidate.Matches);
                    if (approval == null) preview.UnapprovedCorrectionCount++; else matchedApprovals.Add(approval.Fingerprint);
                }
                pending.Add(key, row); preview.NewCount++; preview.NewGrossClp = checked(preview.NewGrossClp + row.GrossClp);
                if (row.NetClp.HasValue) { preview.KnownNewNetCount++; preview.KnownNewNetClp = checked(preview.KnownNewNetClp + row.NetClp.Value); }
            }
            if (matchedApprovals.Count != plan.CorrectionApprovals.Count) preview.ConflictCount++;
            tx.Commit(); return preview;
        }
        public async Task<HistoricalSalesOperationReceipt> ApplyAsync(HistoricalSalesImportPlan plan,
            HistoricalImportAuthorizationCommitGuard guard, string backupPath, CancellationToken ct,
            HistoricalSalesVerifiedSources verifiedSources = null, CancellationToken admissionToken = default, Action maintenanceAdmitted = null,
            HistoricalPublicationDraft publicationDraft = null, HistoricalPublicationProof publicationProof = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            ValidateGuard(guard, HistoricalImportOperationKind.Apply, plan.OperationId, plan.RequestFingerprint, plan.ShopId, plan.DatasetId, plan.RestoreEpoch);
            using var admission = admissionToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, admissionToken) : CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!admissionToken.CanBeCanceled) admission.CancelAfter(TimeSpan.FromSeconds(60));
            var replay = await TryReplayAsync(guard, admission.Token).ConfigureAwait(false);
            if (replay != null) return replay; // No source access or backup for an authorized terminal replay.
            using var ownedDraft=publicationDraft==null?await PreparePublicationDraftAsync(plan,guard.DeviceId,admission.Token).ConfigureAwait(false):null;
            publicationDraft ??= ownedDraft;
            publicationDraft.DemandMatches(guard);
            using var ownedSources = verifiedSources == null ? await VerifySourcesAsync(plan, ct).ConfigureAwait(false) : null;
            using var sourceUse = (verifiedSources ?? ownedSources).AcquireUse(plan.RequestFingerprint);
            HistoricalSalesOperationReceipt result = null;
            await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
            {
                admission.CancelAfter(Timeout.InfiniteTimeSpan);
                maintenanceAdmitted?.Invoke();
                guard.DemandStillValid();
                _testHooks?.AtPhase?.Invoke("before_backup", null, null);
                await new SqliteOnlineBackup(_factory).CreateVerifiedAsync(backupPath, ct).ConfigureAwait(false);
                _testHooks?.AtPhase?.Invoke("after_backup", null, null);
                _testHooks?.AtPhase?.Invoke("apply_database_started", null, null);
                using var c = await _factory.OpenAsync(ct).ConfigureAwait(false);
                using var fence = SqliteConnectionFactory.AcquireExclusiveCommitFence(c);
                using var tx = c.BeginTransaction();
                result = Replay(c, tx, guard);
                if (result != null) { tx.Rollback(); return; }
                DemandDataset(c, tx, guard, plan.DataRevision);
                _testHooks?.AtPhase?.Invoke("mutation_started", c, tx);
                var batch = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                c.Execute("INSERT INTO historical_import_batch(id,shop_id,dataset_id,apply_operation_id,state,created_at) VALUES(@batch,@ShopId,@DatasetId,@OperationId,'Active',@now)", new { batch, plan.ShopId, plan.DatasetId, plan.OperationId, now }, tx);
                foreach (var a in plan.Assignments) Bind(c, tx, plan.ShopId, a, now);
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var f in plan.Analysis.Files)
                {
                    var id = Guid.NewGuid().ToString("N"); files.Add(f.FileId, id);
                    var row = plan.Analysis.AcceptedTransactions.FirstOrDefault(x => x.Source.FileId == f.FileId);
                    c.Execute(@"INSERT INTO historical_import_file(id,batch_id,source_sha256,profile,parser_version,temporal_policy,evidence_reference,source_rows)
VALUES(@id,@batch,@Sha256,@profile,'getnet-ooxml-v1',@policy,@evidence,@SourceRows)", new { id, batch, f.Sha256, profile = f.Profile.ToString(), policy = row == null ? "rejections-only" : TemporalPolicyDescriptor(row), evidence = row?.Timestamp.EvidenceReference, f.SourceRows }, tx);
                }
                var inserted = 0; var duplicates = 0; long amount = 0;
                var approvedCorrections = new List<HistoricalSalesCorrectionApproval>();
                var assignments = new MerchantAssignmentLookup(plan.Assignments);
                var usedBindings = new HashSet<string>(StringComparer.Ordinal);
                using var commands = new ApplyCommands(c, tx, plan.ShopId, batch, now);
                _testHooks?.AtPhase?.Invoke("before_insert", c, tx);
                foreach (var r in plan.Analysis.AcceptedTransactions)
                {
                    ct.ThrowIfCancellationRequested();
                    var a = assignments.Resolve(r.Merchant);
                    var old = commands.ReadMovement(a.ScopeId, r, true);
                    if (old != null)
                    {
                        if (old.Value.ConflictsWith(EconomicValue(r)))
                            throw new InvalidOperationException("historical_economic_identity_conflict");
                        if (old.SettlementState != r.SettlementStatus || old.CardSubtype != r.CardSubtype.ToString() || old.Fee != r.CommissionClp || old.FeeVat != r.CommissionVatClp || old.Net != r.NetClp)
                            commands.Observe(files[r.Source.FileId], r.Source.RowNumber, "Metadata", SerializeMetadata(r), old.Id);
                        commands.Observe(files[r.Source.FileId], r.Source.RowNumber, "Duplicate", "same_economic_identity", old.Id); duplicates++; continue;
                    }
                    var inactive = commands.ReadMovement(a.ScopeId, r, false);
                    long? supersedes = inactive?.Id;
                    if (inactive != null && inactive.Value.ConflictsWith(EconomicValue(r)))
                    {
                        var candidate = CorrectionCandidate(plan, r, inactive);
                        var approval = plan.CorrectionApprovals.SingleOrDefault(candidate.Matches);
                        if (approval == null) throw new InvalidOperationException("historical_inactive_correction_approval_required");
                        approvedCorrections.Add(approval);
                        commands.Observe(files[r.Source.FileId], r.Source.RowNumber, "Correction", "inactive_economic_correction_approved", inactive.Id);
                    }
                    commands.Insert(files[r.Source.FileId], a, r, supersedes);
                    // All rows in this apply share now. The first inserted row
                    // freezes its binding in this same transaction; subsequent
                    // identical COALESCE updates cannot change that evidence.
                    if (usedBindings.Add(a.BindingId)) commands.MarkBindingUsed(a.BindingId);
                    inserted++; amount = checked(amount + r.GrossClp);
                }
                if (approvedCorrections.Count != plan.CorrectionApprovals.Count) throw new InvalidOperationException("historical_correction_approval_stale");
                foreach (var r in plan.Analysis.RejectedRows)
                {
                    ct.ThrowIfCancellationRequested();
                    commands.Observe(files[r.Source.FileId], r.Source.RowNumber, "Rejected", "provider_rejected", null);
                }
                result = new HistoricalSalesOperationReceipt { OperationId = plan.OperationId, OperationKind = "Apply", BatchId = batch, InsertedCount = inserted, DuplicateCount = duplicates, RejectedCount = plan.Analysis.RejectedRows.Count, SignedAmountClp = amount, CorrectionCount = approvedCorrections.Count, CorrectionApprovals = approvedCorrections.ToArray(), CurrentBatchState = "Active", CurrentBatchRevision = 1 };
                InsertReceipt(c, tx, guard, result, now);
                HistoricalPublicationOutbox.Enqueue(c,tx,guard,publicationDraft,publicationProof,Serialize(result),null,now,ct);
                _testHooks?.AtPhase?.Invoke("before_commit", c, tx);
                ct.ThrowIfCancellationRequested();
                guard.DemandStillValid();
                using (await _coordinator.EnterPublicationAsync(ct).ConfigureAwait(false))
                    _coordinator.CommitAndPublishReportRevision(c, tx, () => guard.CommitIfStillValid(SqliteConnectionFactory.DurableCommitSafetyBudget, tx.Commit), _authority());
                _testHooks?.AtPhase?.Invoke("after_commit", c, tx);
            }, TimeSpan.FromSeconds(30), admission.Token).ConfigureAwait(false);
            return result;
        }
        public async Task<HistoricalSalesOperationReceipt> UndoAsync(string batchId, long expectedBatchRevision,
            HistoricalImportAuthorizationCommitGuard guard, string backupPath, CancellationToken ct,
            CancellationToken admissionToken = default, Action maintenanceAdmitted = null,
            HistoricalPublicationDraft publicationDraft = null, HistoricalPublicationProof publicationProof = null)
        {
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            var fingerprint = HistoricalSalesImportPlan.UndoFingerprint(guard.OperationId, guard.ShopId, guard.DatasetId, batchId, expectedBatchRevision, guard.RestoreEpoch);
            ValidateGuard(guard, HistoricalImportOperationKind.Undo, guard.OperationId, fingerprint, guard.ShopId, guard.DatasetId, guard.RestoreEpoch);
            using var admission = admissionToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, admissionToken) : CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!admissionToken.CanBeCanceled) admission.CancelAfter(TimeSpan.FromSeconds(60));
            var replay = await TryReplayAsync(guard, admission.Token).ConfigureAwait(false); if (replay != null) return replay;
            HistoricalSalesOperationReceipt result = null;
            await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
            {
                admission.CancelAfter(Timeout.InfiniteTimeSpan);
                maintenanceAdmitted?.Invoke();
                guard.DemandStillValid();
                _testHooks?.AtPhase?.Invoke("before_backup", null, null);
                await new SqliteOnlineBackup(_factory).CreateVerifiedAsync(backupPath, ct).ConfigureAwait(false);
                _testHooks?.AtPhase?.Invoke("after_backup", null, null);
                using var c = await _factory.OpenAsync(ct).ConfigureAwait(false);
                using var fence = SqliteConnectionFactory.AcquireExclusiveCommitFence(c);
                using var tx = c.BeginTransaction();
                result = Replay(c, tx, guard); if (result != null) { tx.Rollback(); return; }
                DemandDataset(c, tx, guard, null);
                var baseRevision=c.ExecuteScalar<long>("SELECT data_revision FROM historical_import_dataset WHERE singleton_id=1",transaction:tx);
                if(publicationDraft!=null&&publicationDraft.BaseRevision!=baseRevision)throw new InvalidOperationException("historical_plan_stale");
                var batch = c.QuerySingleOrDefault<HistoricalSalesBatch>("SELECT id,state,revision FROM historical_import_batch WHERE id=@batchId AND shop_id=@ShopId", new { batchId, guard.ShopId }, tx);
                if (batch == null || batch.Revision != expectedBatchRevision || batch.State != "Active") throw new InvalidOperationException("historical_batch_stale");
                var amount = c.ExecuteScalar<long>("SELECT COALESCE(SUM(signed_amount_clp),0) FROM historical_transactions WHERE batch_id=@batchId AND is_active=1", new { batchId }, tx);
                var count = c.Execute("UPDATE historical_transactions SET is_active=0 WHERE batch_id=@batchId AND is_active=1", new { batchId }, tx);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                c.Execute("UPDATE historical_import_batch SET state='Undone',revision=revision+1,undone_at=@now,undo_operation_id=@OperationId WHERE id=@batchId", new { now, guard.OperationId, batchId }, tx);
                result = new HistoricalSalesOperationReceipt { OperationId = guard.OperationId, OperationKind = "Undo", BatchId = batchId, InsertedCount = count, SignedAmountClp = amount, CurrentBatchState = "Undone", CurrentBatchRevision = expectedBatchRevision + 1 };
                InsertReceipt(c, tx, guard, result, now);
                var applyOperation=c.ExecuteScalar<string>("SELECT apply_operation_id FROM historical_import_batch WHERE id=@batchId",new{batchId},tx);
                using var ownedDraft=publicationDraft==null?HistoricalPublicationDraft.ForUndo(guard.OperationId,guard.ShopId,guard.DeviceId,guard.DatasetId,guard.RestoreEpoch,
                    baseRevision,batchId,expectedBatchRevision,applyOperation,c.ExecuteScalar<string>("SELECT value FROM app_settings WHERE key='pos.history.restore_lineage_id'",transaction:tx)):null;
                HistoricalPublicationOutbox.Enqueue(c,tx,guard,publicationDraft??ownedDraft,publicationProof,Serialize(result),applyOperation,now,ct);
                _testHooks?.AtPhase?.Invoke("before_undo_commit", c, tx); ct.ThrowIfCancellationRequested(); guard.DemandStillValid();
                using (await _coordinator.EnterPublicationAsync(ct).ConfigureAwait(false))
                    _coordinator.CommitAndPublishReportRevision(c, tx, () => guard.CommitIfStillValid(SqliteConnectionFactory.DurableCommitSafetyBudget, tx.Commit), _authority());
                _testHooks?.AtPhase?.Invoke("after_commit", c, tx);
            }, TimeSpan.FromSeconds(30), admission.Token).ConfigureAwait(false);
            return result;
        }
        private void ValidateGuard(HistoricalImportAuthorizationCommitGuard g, HistoricalImportOperationKind kind, string operation, string fingerprint, string shop, string dataset, long epoch)
        {
            if (g == null) throw new ArgumentNullException(nameof(g)); g.DemandStillValid();
            if (g.Kind != kind || g.OperationId != operation || g.RequestFingerprint != fingerprint || g.ShopId != shop || g.DatasetId != dataset || g.RestoreEpoch != epoch || _restoreEpoch() != epoch)
                throw new InvalidOperationException("historical_authorization_binding_mismatch");
        }
        private void DemandDataset(SqliteConnection c, SqliteTransaction tx, HistoricalImportAuthorizationCommitGuard g, long? expectedRevision)
        {
            var state = c.QuerySingle<HistoricalSalesDatasetState>("SELECT dataset_id AS DatasetId,data_revision AS DataRevision FROM historical_import_dataset WHERE singleton_id=1", transaction: tx);
            if (state.DatasetId != g.DatasetId || _restoreEpoch() != g.RestoreEpoch || (expectedRevision.HasValue && state.DataRevision != expectedRevision.Value)) throw new InvalidOperationException("historical_plan_stale");
        }
        private HistoricalSalesOperationReceipt Replay(SqliteConnection c, SqliteTransaction tx, HistoricalImportAuthorizationCommitGuard g)
        {
            DemandDataset(c, tx, g, null);
            return ReadTerminalOperation(c, tx, new HistoricalSalesOperationIdentity(g.Kind, g.OperationId, g.RequestFingerprint, g.ShopId, g.DatasetId));
        }
        private static HistoricalSalesOperationReceipt ReadTerminalOperation(SqliteConnection c, SqliteTransaction tx, HistoricalSalesOperationIdentity identity)
        {
            var op = c.QuerySingleOrDefault<StoredOperation>("SELECT operation_kind AS Kind,shop_id AS Shop,dataset_id AS Dataset,request_fingerprint AS Fingerprint,receipt_json AS Json FROM historical_import_operation WHERE operation_id=@OperationId", new { identity.OperationId }, tx);
            if (op == null) return null;
            if (op.Kind != identity.Kind.ToString() || op.Shop != identity.ShopId || op.Dataset != identity.DatasetId || op.Fingerprint != identity.RequestFingerprint) throw new InvalidOperationException("historical_operation_identity_conflict");
            var receipt = Deserialize(op.Json);
            var batch = c.QuerySingle<HistoricalSalesBatch>("SELECT state,revision FROM historical_import_batch WHERE id=@BatchId AND shop_id=@ShopId AND dataset_id=@DatasetId", new { receipt.BatchId, identity.ShopId, identity.DatasetId }, tx);
            receipt.CurrentBatchState = batch.State; receipt.CurrentBatchRevision = batch.Revision; receipt.IsReplay = true; return receipt;
        }
        private static void Bind(SqliteConnection c, SqliteTransaction tx, string shop, HistoricalMerchantAssignment a, long now)
        {
            var scope = c.QuerySingleOrDefault<ScopeRow>("SELECT shop_id AS Shop,provider AS Provider FROM historical_merchant_scope WHERE id=@ScopeId", new { a.ScopeId }, tx);
            if (scope == null) c.Execute("INSERT INTO historical_merchant_scope(id,shop_id,provider,evidence_reference,created_at) VALUES(@ScopeId,@shop,'getnet',@EvidenceReference,@now)", new { a.ScopeId, shop, a.EvidenceReference, now }, tx);
            else if (scope.Shop != shop || scope.Provider != "getnet") throw new InvalidOperationException("historical_merchant_scope_conflict");
            var binding = c.QuerySingleOrDefault<BindingRow>("SELECT id,scope_id AS Scope,shop_id AS Shop,provider AS Provider,alias_key AS Alias FROM historical_merchant_binding WHERE id=@BindingId OR (shop_id=@shop AND provider='getnet' AND alias_version='getnet-merchant-alias-v1' AND alias_key=@AliasKey AND is_active=1)", new { a.BindingId, shop, a.AliasKey }, tx);
            if (binding == null) c.Execute("INSERT INTO historical_merchant_binding(id,scope_id,shop_id,provider,alias_version,alias_key,evidence_reference,is_active) VALUES(@BindingId,@ScopeId,@shop,'getnet','getnet-merchant-alias-v1',@AliasKey,@EvidenceReference,1)", new { a.BindingId, a.ScopeId, shop, a.AliasKey, a.EvidenceReference }, tx);
            else if (binding.Id != a.BindingId || binding.Scope != a.ScopeId || binding.Shop != shop || binding.Provider != "getnet" || binding.Alias != a.AliasKey) throw new InvalidOperationException("historical_merchant_binding_conflict");
        }
        private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, HistoricalImportAuthorizationCommitGuard guard, HistoricalSalesOperationReceipt receipt, long now)
            => c.Execute(@"INSERT INTO historical_import_operation(operation_id,operation_kind,shop_id,dataset_id,request_fingerprint,batch_id,operator_id,created_at,receipt_json,inserted_count,duplicate_count,signed_amount_clp)
VALUES(@OperationId,@kind,@ShopId,@DatasetId,@RequestFingerprint,@BatchId,@OperatorId,@now,@json,@InsertedCount,@DuplicateCount,@SignedAmountClp)", new { guard.OperationId, kind = guard.Kind.ToString(), guard.ShopId, guard.DatasetId, guard.RequestFingerprint, receipt.BatchId, guard.OperatorId, now, json = Serialize(receipt), receipt.InsertedCount, receipt.DuplicateCount, receipt.SignedAmountClp }, tx);
        private static string Serialize(HistoricalSalesOperationReceipt receipt) { using var m = new MemoryStream(); new DataContractJsonSerializer(typeof(HistoricalSalesOperationReceipt)).WriteObject(m, receipt); return Encoding.UTF8.GetString(m.ToArray()); }
        private static HistoricalSalesOperationReceipt Deserialize(string json) { using var m = new MemoryStream(Encoding.UTF8.GetBytes(json)); return (HistoricalSalesOperationReceipt)new DataContractJsonSerializer(typeof(HistoricalSalesOperationReceipt)).ReadObject(m); }
        public async Task<HistoricalSalesVerifiedSources> VerifySourcesAsync(HistoricalSalesImportPlan plan, CancellationToken ct)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var handles = new List<FileStream>();
            try
            {
            foreach (var file in plan.Analysis.Files)
            {
                ct.ThrowIfCancellationRequested();
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                handles.Add(stream);
                if (stream.Length != file.Bytes) throw new InvalidOperationException("historical_source_changed");
                using var sha = SHA256.Create(); var buffer = new byte[65536]; int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0) sha.TransformBlock(buffer, 0, count, buffer, 0);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                if (hash != file.Sha256) throw new InvalidOperationException("historical_source_changed");
            }
            return new HistoricalSalesVerifiedSources(plan.RequestFingerprint, handles);
            }
            catch { foreach (var handle in handles) handle.Dispose(); throw; }
        }
        private static ExistingMovement ReadMovement(SqliteConnection c, SqliteTransaction tx, string shop, string scope, HistoricalSalesTransaction row, bool active)
            => c.QuerySingleOrDefault<ExistingMovement>(@"SELECT id,business_ticks AS BusinessTicks,signed_amount_clp AS Gross,signed_fee_clp AS Fee,signed_fee_vat_clp AS FeeVat,
signed_net_clp AS Net,movement_kind AS Kind,state AS TransactionState,settlement_state AS SettlementState,card_subtype AS CardSubtype
FROM historical_transactions WHERE shop_id=@shop AND provider=@Provider AND merchant_scope_id=@scope AND external_namespace=@ExternalNamespace AND external_id=@ExternalId AND is_active=@active ORDER BY id DESC LIMIT 1",
                new { shop, scope, row.Provider, row.ExternalNamespace, row.ExternalId, active = active ? 1 : 0 }, tx);
        private static HistoricalSalesEconomicValue EconomicValue(HistoricalSalesTransaction r)
            => new HistoricalSalesEconomicValue(r.BusinessDateTime.Value.Ticks, r.GrossClp, r.CommissionClp, r.CommissionVatClp, r.NetClp, r.MovementKind.ToString(), r.TransactionStatus);
        private static string TemporalPolicyDescriptor(HistoricalSalesTransaction r)
            => r.Timestamp.PolicyVersion + ";interpretation=" + (r.Profile == HistoricalSalesProfile.GetnetLegacy ? "LegacyLocal;CLP=explicitly-approved" : r.Timestamp.Interpretation.ToString() + ";CLP=exported");
        private static HistoricalSalesCorrectionCandidate CorrectionCandidate(HistoricalSalesImportPlan plan, HistoricalSalesTransaction row, ExistingMovement previous)
            => new HistoricalSalesCorrectionCandidate(previous.Id, previous.Value, EconomicValue(row), row.Source.FileId, row.Source.SheetName, row.Source.RowNumber,
                HistoricalSalesImportPlan.Hash(new[] { "correction-review-context-v1", plan.ShopId, plan.DatasetId, plan.DataRevision.ToString(CultureInfo.InvariantCulture), plan.RestoreEpoch.ToString(CultureInfo.InvariantCulture) }));
        private static string SerializeMetadata(HistoricalSalesTransaction row)
        {
            using var stream = new MemoryStream();
            new DataContractJsonSerializer(typeof(MetadataObservation)).WriteObject(stream, new MetadataObservation { SettlementState = row.SettlementStatus, CardSubtype = row.CardSubtype.ToString(), FeeClp = row.CommissionClp, FeeVatClp = row.CommissionVatClp, NetClp = row.NetClp });
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        [DataContract] private sealed class MetadataObservation
        {
            [DataMember] public string Version = "historical-metadata-observation-v1";
            [DataMember] public string SettlementState;
            [DataMember] public string CardSubtype;
            [DataMember] public long? FeeClp;
            [DataMember] public long? FeeVatClp;
            [DataMember] public long? NetClp;
        }
        /// <summary>
        /// Operation-local resolution of the already approved immutable merchant
        /// evidence. At most the plan's existing 200-merchant bound is retained;
        /// neither customer data nor a hash/proof is cached across operations.
        /// </summary>
        private sealed class MerchantAssignmentLookup
        {
            private readonly Dictionary<string, HistoricalMerchantAssignment> _aliases;
            private readonly Dictionary<HistoricalSalesMerchant, HistoricalMerchantAssignment> _merchants =
                new Dictionary<HistoricalSalesMerchant, HistoricalMerchantAssignment>(MerchantEvidenceComparer.Instance);
            internal MerchantAssignmentLookup(IReadOnlyList<HistoricalMerchantAssignment> assignments)
                => _aliases = assignments.ToDictionary(a => a.AliasKey, StringComparer.Ordinal);
            internal HistoricalMerchantAssignment Resolve(HistoricalSalesMerchant merchant)
            {
                if (_merchants.TryGetValue(merchant, out var assignment)) return assignment;
                if (!_aliases.TryGetValue(HistoricalMerchantAssignment.MerchantKey(merchant), out assignment))
                    throw new InvalidOperationException("historical_overlap_or_binding_unresolved");
                if (_merchants.Count < HistoricalSalesImportPlan.MaximumMerchantAssignments) _merchants.Add(merchant, assignment);
                return assignment;
            }
        }
        private sealed class MerchantEvidenceComparer : IEqualityComparer<HistoricalSalesMerchant>
        {
            internal static readonly MerchantEvidenceComparer Instance = new MerchantEvidenceComparer();
            public bool Equals(HistoricalSalesMerchant x, HistoricalSalesMerchant y)
                => ReferenceEquals(x, y) || (x != null && y != null && x.Local == y.Local && x.Seller == y.Seller &&
                    x.SellerId == y.SellerId && x.LocalNumber == y.LocalNumber && x.Rut == y.Rut);
            public int GetHashCode(HistoricalSalesMerchant merchant)
            {
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(merchant.Local);
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(merchant.Seller);
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(merchant.SellerId);
                    hash = hash * 31 + StringComparer.Ordinal.GetHashCode(merchant.LocalNumber);
                    return hash * 31 + StringComparer.Ordinal.GetHashCode(merchant.Rut);
                }
            }
        }
        /// <summary>
        /// Reuses statements only within one live connection and transaction.
        /// Both identity lookups still run against SQLite for every source row,
        /// observing earlier inserts in this batch and the latest inactive ID.
        /// </summary>
        private sealed class ApplyCommands : IDisposable
        {
            private readonly PreparedStatement _active, _inactive, _insert, _observation, _used;
            private const string LookupSql = @"SELECT id,business_ticks,signed_amount_clp,signed_fee_clp,signed_fee_vat_clp,
signed_net_clp,movement_kind,state,settlement_state,card_subtype
FROM historical_transactions WHERE shop_id=$shop AND provider=$provider AND merchant_scope_id=$scope AND external_namespace=$namespace AND external_id=$external AND is_active=";
            internal ApplyCommands(SqliteConnection connection, SqliteTransaction transaction, string shop, string batch, long now)
            {
                try
                {
                    var identity = new[] { "shop", "provider", "scope", "namespace", "external" };
                    _active = new PreparedStatement(connection, transaction, LookupSql + "1 ORDER BY id DESC LIMIT 1", identity, Array.Empty<string>());
                    _inactive = new PreparedStatement(connection, transaction, LookupSql + "0 ORDER BY id DESC LIMIT 1", identity, Array.Empty<string>());
                    _active.Set("shop", shop); _inactive.Set("shop", shop);
                    _insert = new PreparedStatement(connection, transaction, @"INSERT INTO historical_transactions(batch_id,file_id,shop_id,provider,merchant_scope_id,binding_id,external_namespace,external_id,business_ticks,timestamp_raw,offset_minutes,occurred_at_utc_ms,movement_kind,state,settlement_state,signed_amount_clp,signed_fee_clp,signed_fee_vat_clp,signed_net_clp,original_cashier_id,card_subtype,source_sheet,source_row,is_active,supersedes_id)
VALUES($batch,$file,$shop,$provider,$scope,$binding,$namespace,$external,$ticks,$raw,$offset,$utc,$kind,$state,$settlement,$gross,$fee,$vat,$net,NULL,$card,$sheet,$row,1,$supersedes)",
                        new[] { "batch", "file", "shop", "provider", "scope", "binding", "namespace", "external", "raw", "kind", "state", "settlement", "card", "sheet" },
                        new[] { "ticks", "offset", "utc", "gross", "fee", "vat", "net", "row", "supersedes" });
                    _insert.Set("batch", batch); _insert.Set("shop", shop);
                    _observation = new PreparedStatement(connection, transaction,
                        "INSERT INTO historical_import_observation(batch_id,file_id,source_row,observation_kind,code,existing_transaction_id) VALUES($batch,$file,$row,$kind,$code,$existing)",
                        new[] { "batch", "file", "kind", "code" }, new[] { "row", "existing" });
                    _observation.Set("batch", batch);
                    _used = new PreparedStatement(connection, transaction,
                        "UPDATE historical_merchant_binding SET used_at=COALESCE(used_at,$now) WHERE id=$binding", new[] { "binding" }, new[] { "now" });
                    _used.Set("now", now);
                }
                catch { Dispose(); throw; }
            }
            internal ExistingMovement ReadMovement(string scope, HistoricalSalesTransaction row, bool active)
            {
                var statement = active ? _active : _inactive;
                statement.Set("provider", row.Provider); statement.Set("scope", scope);
                statement.Set("namespace", row.ExternalNamespace); statement.Set("external", row.ExternalId);
                using var reader = statement.Command.ExecuteReader();
                if (!reader.Read()) return null;
                return new ExistingMovement
                {
                    Id = reader.GetInt64(0), BusinessTicks = reader.GetInt64(1), Gross = reader.GetInt64(2),
                    Fee = NullableInt64(reader, 3), FeeVat = NullableInt64(reader, 4), Net = NullableInt64(reader, 5),
                    Kind = reader.GetString(6), TransactionState = reader.GetString(7),
                    SettlementState = reader.IsDBNull(8) ? null : reader.GetString(8), CardSubtype = reader.GetString(9)
                };
            }
            private static long? NullableInt64(SqliteDataReader reader, int ordinal)
                => reader.IsDBNull(ordinal) ? (long?)null : reader.GetInt64(ordinal);
            internal void Insert(string file, HistoricalMerchantAssignment assignment, HistoricalSalesTransaction row, long? supersedes)
            {
                _insert.Set("file", file); _insert.Set("provider", row.Provider); _insert.Set("scope", assignment.ScopeId);
                _insert.Set("binding", assignment.BindingId); _insert.Set("namespace", row.ExternalNamespace); _insert.Set("external", row.ExternalId);
                _insert.Set("ticks", row.BusinessDateTime.Value.Ticks); _insert.Set("raw", row.Timestamp.RawTimestamp);
                _insert.Set("offset", row.Timestamp.OffsetMinutes); _insert.Set("utc", row.Timestamp.OccurredAtUtc?.ToUnixTimeMilliseconds());
                _insert.Set("kind", row.MovementKind.ToString()); _insert.Set("state", row.TransactionStatus); _insert.Set("settlement", row.SettlementStatus);
                _insert.Set("gross", row.GrossClp); _insert.Set("fee", row.CommissionClp); _insert.Set("vat", row.CommissionVatClp); _insert.Set("net", row.NetClp);
                _insert.Set("card", row.CardSubtype.ToString()); _insert.Set("sheet", row.Source.SheetName); _insert.Set("row", row.Source.RowNumber);
                _insert.Set("supersedes", supersedes); _insert.Command.ExecuteNonQuery();
            }
            internal void Observe(string file, int row, string kind, string code, long? existing)
            {
                _observation.Set("file", file); _observation.Set("row", row); _observation.Set("kind", kind);
                _observation.Set("code", code); _observation.Set("existing", existing); _observation.Command.ExecuteNonQuery();
            }
            internal void MarkBindingUsed(string binding) { _used.Set("binding", binding); _used.Command.ExecuteNonQuery(); }
            public void Dispose()
            {
                _active?.Dispose(); _inactive?.Dispose(); _insert?.Dispose(); _observation?.Dispose(); _used?.Dispose();
            }
        }
        private sealed class PreparedStatement : IDisposable
        {
            private readonly Dictionary<string, SqliteParameter> _parameters = new Dictionary<string, SqliteParameter>(StringComparer.Ordinal);
            internal PreparedStatement(SqliteConnection connection, SqliteTransaction transaction, string sql, string[] text, string[] integers)
            {
                Command = connection.CreateCommand(); Command.Transaction = transaction; Command.CommandText = sql;
                try
                {
                    foreach (var name in text) _parameters.Add(name, Command.Parameters.Add("$" + name, SqliteType.Text));
                    foreach (var name in integers) _parameters.Add(name, Command.Parameters.Add("$" + name, SqliteType.Integer));
                    foreach (var parameter in _parameters.Values) parameter.Value = DBNull.Value;
                    Command.Prepare();
                }
                catch { Command.Dispose(); throw; }
            }
            internal SqliteCommand Command { get; }
            internal void Set(string name, object value) => _parameters[name].Value = value ?? DBNull.Value;
            public void Dispose() => Command.Dispose();
        }
        private sealed class ExistingMovement
        {
            public long Id { get; set; } public long BusinessTicks { get; set; } public long Gross { get; set; }
            public long? Fee { get; set; } public long? FeeVat { get; set; } public long? Net { get; set; }
            public string Kind { get; set; } public string TransactionState { get; set; } public string SettlementState { get; set; } public string CardSubtype { get; set; }
            internal HistoricalSalesEconomicValue Value => new HistoricalSalesEconomicValue(BusinessTicks, Gross, Fee, FeeVat, Net, Kind, TransactionState);
            internal static ExistingMovement From(HistoricalSalesTransaction r) => new ExistingMovement { BusinessTicks = r.BusinessDateTime.Value.Ticks, Gross = r.GrossClp,
                Fee = r.CommissionClp, FeeVat = r.CommissionVatClp, Net = r.NetClp, Kind = r.MovementKind.ToString(), TransactionState = r.TransactionStatus, SettlementState = r.SettlementStatus, CardSubtype = r.CardSubtype.ToString() };
        }
        private sealed class StoredOperation { public string Kind { get; set; } public string Shop { get; set; } public string Dataset { get; set; } public string Fingerprint { get; set; } public string Json { get; set; } }
        private sealed class ScopeRow { public string Shop { get; set; } public string Provider { get; set; } }
        private sealed class BindingRow { public string Id { get; set; } public string Scope { get; set; } public string Shop { get; set; } public string Provider { get; set; } public string Alias { get; set; } }
    }
}
