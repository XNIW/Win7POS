using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Backup;
using Win7POS.Data.Import;

namespace Win7POS.Data.Online
{
    public sealed class CatalogImportRecoveryException : InvalidOperationException
    {
        public CatalogImportRecoveryException(string code) : base(code) { Code = code; }
        public string Code { get; }
    }

    public sealed class CatalogImportRecoveryIssue
    {
        public string Barcode { get; internal set; }
        public string Field { get; internal set; }
        public string Value { get; internal set; }
    }

    public sealed class CatalogImportRecoveryBatch
    {
        public long OutboxId { get; internal set; }
        public string ClientImportId { get; internal set; }
        public string PayloadHash { get; internal set; }
        public string OriginShopId { get; internal set; }
        public string OriginShopCode { get; internal set; }
        public string LastErrorCode { get; internal set; }
        public string ReplacementErrorCode { get; internal set; }
        public bool NeverSent { get; internal set; }
        public string RecoveryState { get; internal set; }
        public bool CanRecoverReplacement { get; internal set; }
        public long? ReplacementOutboxId { get; internal set; }
        public int ItemCount { get; internal set; }
        public IReadOnlyList<CatalogImportRecoveryIssue> Issues { get; internal set; }
    }

    public sealed class CatalogImportRecoveryDraft
    {
        public CatalogImportRecoveryBatch Batch { get; internal set; }
        public IReadOnlyList<SupplierImportEditableRow> Rows { get; internal set; }
        public long TransitionEpoch { get; internal set; }
        public string ReceiptStatus { get; internal set; }
        public bool CanCommit { get; internal set; }
        public string TargetReceiptStatus => TargetReceipt?.Status ?? ReceiptStatus;
        public bool CanRetire => !CanCommit && TargetReceiptStatus=="not_found";
        public bool RequiresAcceptedReconciliation => TargetOriginal!=null && TargetReceiptStatus=="accepted";
        public int RevisionCount { get; internal set; }
        public string RevisionFingerprint { get; internal set; }=string.Empty;
        internal CatalogImportRecoveryOriginal Original { get; set; }
        internal PosCatalogImportRequest OriginalRequest { get; set; }
        internal PosCatalogImportReceiptResponse Receipt { get; set; }
        internal OnlineSyncGeneration Generation { get; set; }
        internal string ShopDeviceId { get; set; }
        internal List<CatalogImportRecoveryContribution> Contributions { get; } = new List<CatalogImportRecoveryContribution>();
        internal bool ContributionSetCaptured { get; set; }
        internal CatalogImportRecoveryOriginal TargetOriginal { get; set; }
        internal PosCatalogImportReceiptResponse TargetReceipt { get; set; }
        internal PosCatalogImportRequest InitialIntentRequest { get; set; }
        internal PosCatalogImportRequest TargetAckRequest { get; set; }
        internal CatalogImportAckResult TargetAck { get; set; }
        internal PosTrustedDeviceSession TransportSession { get; set; }
    }

    internal sealed class CatalogImportRecoveryContribution
    {
        public long ContributorId { get; set; }
        public string PayloadHash { get; set; }
        public PosCatalogImportRequest Request { get; set; }
        public PosCatalogImportReceiptResponse Receipt { get; set; }
    }

    public sealed partial class CatalogImportRecoveryService
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly string _backupDirectory;
        private readonly SqliteOnlineBackup _backup;
        private readonly SupplierExcelImportTestHooks _applyHooks;

        public CatalogImportRecoveryService(SqliteConnectionFactory factory, string backupDirectory = null)
            : this(factory, backupDirectory, null) { }

        internal CatalogImportRecoveryService(SqliteConnectionFactory factory, string backupDirectory, SqliteOnlineBackup backup,
            SupplierExcelImportTestHooks applyHooks = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _backupDirectory = backupDirectory ?? Path.Combine(Path.GetDirectoryName(factory.DbPath), "backups");
            _backup = backup ?? new SqliteOnlineBackup(factory);
            _applyHooks = applyHooks;
        }

        public Task<IReadOnlyList<CatalogImportRecoveryBatch>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                using (var conn = _factory.Open())
                {
                    var rows = await conn.QueryAsync<CatalogImportRecoveryOriginal>(SelectOriginal + @"
WHERE o.status='failed_blocked' AND o.operation_type='catalog_import' ORDER BY o.id LIMIT 50;").ConfigureAwait(false);
                    var result = new List<CatalogImportRecoveryBatch>();
                    foreach (var row in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var batch=BuildBatch(row, ReadOriginal(row));
                            result.Add(batch);
                        }
                        catch { result.Add(BuildBatch(row, null)); }
                    }
                    if(result.Count>0)
                    {
                        var leaves=(await conn.QueryAsync<ReplacementCause>(@"WITH RECURSIVE chain(root_id,id,depth) AS (
SELECT original_id,replacement_id,1 FROM catalog_import_recovery WHERE original_id IN @ids AND replacement_id IS NOT NULL
UNION ALL SELECT c.root_id,r.replacement_id,c.depth+1 FROM chain c JOIN catalog_import_recovery r ON r.original_id=c.id
WHERE c.depth<128 AND r.replacement_id>c.id)
SELECT c.root_id AS RootId,o.operation_type AS OperationType,o.status AS Status,o.last_error_code AS LastErrorCode,c.depth AS Depth
FROM chain c JOIN catalog_import_outbox o ON o.id=c.id ORDER BY c.root_id,c.depth DESC;",
                            new { ids=result.Select(batch=>batch.OutboxId).ToArray() }).ConfigureAwait(false))
                            .GroupBy(cause=>cause.RootId).ToDictionary(group=>group.Key,group=>group.First());
                        foreach(var batch in result)
                            if(leaves.TryGetValue(batch.OutboxId,out var leaf))
                            {
                                batch.CanRecoverReplacement=leaf.OperationType=="catalog_import_correction" && leaf.Status=="failed_blocked";
                                batch.ReplacementErrorCode=leaf.LastErrorCode;
                            }
                    }
                    return (IReadOnlyList<CatalogImportRecoveryBatch>)result;
                }
            }, cancellationToken);
        }

        private sealed class ReplacementCause
        {
            public long RootId { get; set; }
            public int Depth { get; set; }
            public string OperationType { get; set; }
            public string Status { get; set; }
            public string LastErrorCode { get; set; }
        }

        public async Task<CatalogImportRecoveryDraft> PrepareAsync(long originalId, PosAdminWebOptions options,
            PosTrustedDeviceSession session, OnlineSyncGeneration generation, CancellationToken cancellationToken)
        {
            var transportSession=SnapshotTransportSession(session);
            var draft = await Task.Run(async () =>
            {
                using (var conn = _factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    var original = await LoadOriginalAsync(conn, tx, originalId).ConfigureAwait(false);
                    var request = ReadOriginal(original);
                    var value = new CatalogImportRecoveryDraft { Original = original, OriginalRequest = request,
                        Batch = BuildBatch(original, request), Rows = request.Items.Select(ToEditable).ToArray(),
                        Generation = generation,ShopDeviceId=session?.ShopDeviceId,TransportSession=transportSession,
                        TransitionEpoch = await ReadEpochAsync(conn, tx).ConfigureAwait(false) };
                    await new CatalogImportRecoveryCommit(value, generation).ValidateAsync(conn, tx, false).ConfigureAwait(false);
                    if (OutboxShopBinding.GetMismatchCode(original.OriginShopId, original.OriginShopCode, session?.ShopId, session?.ShopCode).Length > 0)
                        throw new CatalogImportRecoveryException("origin_shop_mismatch");
                    if (!string.IsNullOrWhiteSpace(original.ReceiptJson))
                    {
                        value.Receipt = Deserialize<PosCatalogImportReceiptResponse>(original.ReceiptJson);
                        ValidateReceipt(value,value.Receipt);
                    }
                    value.ReceiptStatus = value.Batch.NeverSent ? "never_sent" : original.ReceiptStatus ?? "unverified";
                    value.CanCommit = original.ReplacementId == null && (value.Batch.NeverSent || IsAuthoritative(value.Receipt));
                    UpdateRevisionSummary(value,value.Receipt,value.TargetReceipt);
                    tx.Commit();
                    return value;
                }
            }, cancellationToken).ConfigureAwait(false);
            if(draft.Original.ReplacementId.HasValue)
                return await PrepareReplacementAsync(draft,options,session,cancellationToken).ConfigureAwait(false);
            if (!draft.Batch.NeverSent && draft.Original.ReplacementId == null &&
                (!IsAuthoritative(draft.Receipt) || draft.Receipt?.Status=="accepted"))
            {
                if (options == null || session == null) throw new CatalogImportRecoveryException("authentication_required");
                using (var client = new PosAdminWebClient(options))
                {
                    var request=BuildReceiptRequest(draft,session);
                    CatalogImportSyncService.DemandTransportSize(request);
                    var result = await client.CatalogImportReceiptAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!result.Success) throw new CatalogImportRecoveryException(result.Denied ? "authentication_required" : "receipt_unavailable");
                    ValidateReceipt(draft, result.Value);
                    await StoreReceiptAsync(draft, result.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            if (draft.Original.ReplacementId == null)
                await Task.Run(() => LoadContributionsAsync(draft,options,session,cancellationToken),cancellationToken).ConfigureAwait(false);
            return draft;
        }

        private async Task LoadContributionsAsync(CatalogImportRecoveryDraft draft,PosAdminWebOptions options,
            PosTrustedDeviceSession session,CancellationToken token)
        {
            CatalogImportRecoveryOriginal[] candidates;
            Dictionary<long,ContributionProof> proofs;
            using (var conn=_factory.Open())
            {
                candidates=(await conn.QueryAsync<CatalogImportRecoveryOriginal>(SelectOriginal+ContributionCandidatesWhere,
                    ContributionParameters(draft)).ConfigureAwait(false)).ToArray();
                proofs=(await conn.QueryAsync<ContributionProof>(@"SELECT contributor_id AS ContributorId,payload_hash AS PayloadHash,receipt_json AS ReceiptJson
FROM catalog_import_recovery_contributions WHERE original_id=@id;",new { id=draft.Original.Id }).ConfigureAwait(false))
                    .ToDictionary(proof=>proof.ContributorId);
            }
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (candidate.Status=="in_progress") throw new CatalogImportRecoveryException("recovery_overlap_pending");
                var request=ReadOriginal(candidate);
                var contributionDraft=new CatalogImportRecoveryDraft { Original=candidate,OriginalRequest=request,
                    Generation=draft.Generation,ShopDeviceId=draft.ShopDeviceId,TransitionEpoch=draft.TransitionEpoch };
                PosCatalogImportReceiptResponse receipt;
                if (proofs.TryGetValue(candidate.Id,out var proof))
                {
                    if (proof.PayloadHash != candidate.PayloadHash) throw new CatalogImportRecoveryException("receipt_conflict");
                    receipt=Deserialize<PosCatalogImportReceiptResponse>(proof.ReceiptJson);
                }
                else
                {
                    if (options==null || session==null) throw new CatalogImportRecoveryException("recovery_overlap_pending");
                    using (var client=new PosAdminWebClient(options))
                    {
                        var lookupRequest=BuildReceiptRequest(contributionDraft,session);
                        CatalogImportSyncService.DemandTransportSize(lookupRequest);
                        var response=await client.CatalogImportReceiptAsync(lookupRequest,token).ConfigureAwait(false);
                        if (!response.Success) throw new CatalogImportRecoveryException(response.Denied ? "authentication_required" : "receipt_unavailable");
                        receipt=response.Value;
                    }
                    ValidateReceipt(contributionDraft,receipt);
                    if (receipt.Status!="accepted") throw new CatalogImportRecoveryException("recovery_overlap_pending");
                    await StoreContributionAsync(draft,candidate,receipt,token).ConfigureAwait(false);
                }
                ValidateReceipt(contributionDraft,receipt);
                if (receipt.Status!="accepted") throw new CatalogImportRecoveryException("recovery_overlap_pending");
                draft.Contributions.Add(new CatalogImportRecoveryContribution { ContributorId=candidate.Id,PayloadHash=candidate.PayloadHash,
                    Request=request,Receipt=receipt });
            }
            draft.ContributionSetCaptured=true;
        }

        internal const string ContributionCandidatesWhere=@"
WHERE o.id>@id AND o.id<>@excludeId AND o.origin_shop_id=@shopId AND o.origin_shop_code=@shopCode
AND o.operation_type='catalog_import' AND o.status IN ('pending','retry','in_progress','failed_blocked','acked')
AND NOT EXISTS(SELECT 1 FROM catalog_import_recovery parent WHERE parent.replacement_id=o.id)
AND EXISTS(SELECT 1 FROM json_each(CASE WHEN json_valid(o.payload_json) THEN o.payload_json ELSE '{}' END,'$.items') item
 WHERE json_extract(item.value,'$.barcode') IN @barcodes)
ORDER BY o.id;";

        internal static object ContributionParameters(CatalogImportRecoveryDraft draft,long excludeId=0) =>
            new { id=draft.Original.Id,excludeId,shopId=draft.Original.OriginShopId,shopCode=draft.Original.OriginShopCode,
                barcodes=draft.OriginalRequest.Items.Select(item=>item.Barcode).ToArray() };

        private async Task StoreContributionAsync(CatalogImportRecoveryDraft draft,CatalogImportRecoveryOriginal candidate,
            PosCatalogImportReceiptResponse receipt,CancellationToken token)
        {
            await Task.Run(async()=>
            {
                token.ThrowIfCancellationRequested();
                using (var conn=_factory.Open())
                using (var tx=conn.BeginTransaction())
                {
                    await new CatalogImportRecoveryCommit(draft,draft.Generation).ValidateAsync(conn,tx).ConfigureAwait(false);
                    if (await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_outbox
WHERE id=@id AND payload_hash=@hash AND payload_json=@json AND origin_shop_id=@shopId AND origin_shop_code=@shopCode
AND status IN ('pending','retry','failed_blocked','acked');",new { id=candidate.Id,hash=candidate.PayloadHash,json=candidate.PayloadJson,
                        shopId=draft.Original.OriginShopId,shopCode=draft.Original.OriginShopCode },tx).ConfigureAwait(false)!=1)
                        throw new CatalogImportRecoveryException("recovery_overlap_pending");
                    await conn.ExecuteAsync(@"INSERT OR IGNORE INTO catalog_import_recovery_contributions(original_id,contributor_id,payload_hash,receipt_json,created_at)
VALUES(@originalId,@contributorId,@hash,@json,@now);",new { originalId=draft.Original.Id,contributorId=candidate.Id,
                        hash=candidate.PayloadHash,json=Serialize(receipt),now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },tx).ConfigureAwait(false);
                    tx.Commit();
                }
            },token).ConfigureAwait(false);
        }

        private sealed class ContributionProof
        {
            public long ContributorId { get; set; }
            public string PayloadHash { get; set; }
            public string ReceiptJson { get; set; }
        }

        public Task<SupplierImportSyncPreview> BuildPreviewAsync(CatalogImportRecoveryDraft draft,
            IReadOnlyList<SupplierImportEditableRow> rows, CancellationToken cancellationToken)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            DemandRows(draft, rows);
            var capturedRows = SnapshotRows(rows);
            return Task.Run(async () =>
            {
                var preview = await new SupplierExcelImportApplier(_factory).BuildPreviewAsync(capturedRows, cancellationToken).ConfigureAwait(false);
                var originals=draft.OriginalRequest.Items.ToDictionary(item=>item.Barcode,StringComparer.Ordinal);
                foreach(var row in capturedRows)
                {
                    var original=originals[row.Barcode];
                    foreach(var field in new[] { "retailPrice","purchasePrice","quantity" })
                    {
                        var before=field=="retailPrice" ? original.RetailPrice : field=="purchasePrice" ? original.PurchasePrice : original.Quantity;
                        var desired=field=="retailPrice" ? row.RetailPrice : field=="purchasePrice" ? row.PurchasePrice : row.Quantity;
                        if(!string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(desired))
                            preview.Errors.Add(new SupplierImportError("supplier_import_invalid_price|"+field+"|required",row.RowNumber,row.Barcode));
                    }
                }
                // Include NoChange rows: their original intent may still be wholly unsent.
                foreach (var row in capturedRows)
                    foreach (var field in new[] { "retailPrice", "purchasePrice" })
                        if (!CatalogImportOutboxPayloadBuilder.IsAdminPrice(field == "retailPrice" ? row.RetailPrice : row.PurchasePrice))
                            preview.Errors.Add(new SupplierImportError("supplier_import_invalid_price|" + field + "|999999999", row.RowNumber, row.Barcode));
                preview.Summary.ErrorCount = preview.Errors.Count;
                if(preview.CanApply && draft.CanCommit) BuildCommitEntry(draft,preview);
                return preview;
            }, cancellationToken);
        }

        public async Task<CatalogImportRecoveryDraft> RetireAsync(CatalogImportRecoveryDraft draft,
            PosAdminWebOptions options, PosTrustedDeviceSession session, OnlineSyncGeneration generation,
            Func<bool> authorizeCommit, CancellationToken cancellationToken)
        {
            if(draft?.TargetOriginal!=null)
                return await RetireReplacementAsync(draft,options,session,generation,authorizeCommit,cancellationToken);
            DemandPermission(authorizeCommit);
            cancellationToken.ThrowIfCancellationRequested();
            if (draft==null || draft.ReceiptStatus!="not_found" || draft.CanCommit ||
                draft.Generation?.Fingerprint!=generation?.Fingerprint || session?.ShopDeviceId!=draft.ShopDeviceId)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            await Task.Run(async()=>
            {
                using (var conn=_factory.Open())
                using (var tx=conn.BeginTransaction())
                    await new CatalogImportRecoveryCommit(draft,generation,authorizeCommit).ValidateAsync(conn,tx).ConfigureAwait(false);
            },cancellationToken);
            DemandPermission(authorizeCommit);
            var request=BuildReceiptRequest(draft,session);
            request.SchemaVersion=PosCatalogImportReceiptContract.RetirementSchemaVersion;
            CatalogImportSyncService.DemandTransportSize(request);
            using (var client=new PosAdminWebClient(options))
            {
                var result=await client.CatalogImportRetireAsync(request,cancellationToken);
                DemandPermission(authorizeCommit);
                if (!result.Success) throw new CatalogImportRecoveryException(result.Denied ? "authentication_required" : "receipt_retirement_unavailable");
                ValidateReceipt(draft,result.Value);
                if (result.Value.Status!="retired" && result.Value.Status!="accepted")
                    throw new CatalogImportRecoveryException("receipt_retirement_unavailable");
                await StoreReceiptAsync(draft,result.Value,cancellationToken);
                DemandPermission(authorizeCommit);
                return draft;
            }
        }

        public async Task<SupplierExcelImportApplyResult> CommitAsync(CatalogImportRecoveryDraft draft,
            IReadOnlyList<SupplierImportEditableRow> rows, Func<bool> authorizeCommit,
            OnlineSyncGeneration generation, CancellationToken cancellationToken)
        {
            DemandPermission(authorizeCommit);
            if (draft == null || !draft.CanCommit) throw new CatalogImportRecoveryException("receipt_required");
            DemandRows(draft, rows);
            var capturedRows = SnapshotRows(rows);
            var preview = await BuildPreviewAsync(draft, capturedRows, cancellationToken);
            DemandPermission(authorizeCommit);
            if (!preview.CanApply)
            {
                var invalid = new SupplierExcelImportApplyResult { Errors = preview.Errors.Count };
                invalid.ErrorMessages.AddRange(preview.Errors.Select(error => error.Message));
                return invalid;
            }
            var localPreview=await BuildLocalPreviewAsync(draft,capturedRows,preview,cancellationToken);
            DemandPermission(authorizeCommit);
            var entry = await Task.Run(()=>BuildCommitEntry(draft,preview),cancellationToken);
            DemandPermission(authorizeCommit);
            var backupPath = Path.Combine(_backupDirectory, "before-catalog-recovery-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                await Task.Run(() => _backup.CreateVerifiedAsync(backupPath, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch { throw new CatalogImportRecoveryException("backup_failed"); }
            DemandPermission(authorizeCommit);
            var result = await Task.Run(() => new SupplierExcelImportApplier(_factory).ApplyAsync(localPreview,
                new SupplierExcelImportApplyOptions { InsertNew = true, CatalogImportOutboxEntry = entry,
                    RecoveryCommit = new CatalogImportRecoveryCommit(draft, generation, authorizeCommit), TestHooks=_applyHooks }, cancellationToken), cancellationToken);
            if (result.Errors>0)
                throw new CatalogImportRecoveryException(result.ErrorMessages.Any(message=>message!=null &&
                    message.IndexOf("SUPPLIER_IMPORT_STALE_PREVIEW",StringComparison.OrdinalIgnoreCase)>=0)
                    ? "recovery_state_changed" : "local_save_failed");
            result.BackupPath = backupPath;
            if (result.Errors == 0) draft.CanCommit = false;
            return result;
        }

        private Task<SupplierImportSyncPreview> BuildLocalPreviewAsync(CatalogImportRecoveryDraft draft,
            SupplierImportEditableRow[] desiredRows,SupplierImportSyncPreview intent,CancellationToken token)
        {
            return Task.Run(async()=>
            {
                var originals=(draft.InitialIntentRequest ?? draft.OriginalRequest).Items.ToDictionary(item=>item.Barcode,StringComparer.Ordinal);
                var current=intent.ApplyExpectations.ToDictionary(item=>item.Barcode,StringComparer.Ordinal);
                var localRows=SnapshotRows(desiredRows);
                foreach(var row in localRows)
                {
                    if(!current.TryGetValue(row.Barcode,out var existing) || !existing.Exists)
                        throw new CatalogImportRecoveryException("recovery_state_changed");
                    var original=originals[row.Barcode];
                    // The outgoing original intent can still be due remotely;
                    // locally, only fields edited by this recovery may be changed.
                    if(CatalogImportOutboxPayloadBuilder.EqualNumber(row.Quantity,original.Quantity)) row.Quantity=existing.Quantity.ToString(CultureInfo.InvariantCulture);
                    else
                    {
                        decimal desiredQuantity,baselineQuantity;
                        baselineQuantity=CatalogImportOutboxPayloadBuilder.QuantityOrZero(original.Quantity);
                        if(!decimal.TryParse(row.Quantity,NumberStyles.Number,CultureInfo.InvariantCulture,out desiredQuantity))
                            throw new CatalogImportRecoveryException("recovery_quantity_conflict");
                        var quantity=existing.Quantity+desiredQuantity-baselineQuantity;
                        if(quantity<0 || quantity>999999999m || decimal.Round(quantity,3)!=quantity)
                            throw new CatalogImportRecoveryException("recovery_quantity_conflict");
                        row.Quantity=quantity.ToString(CultureInfo.InvariantCulture);
                    }
                    if(CatalogImportOutboxPayloadBuilder.EqualNumber(row.PurchasePrice,original.PurchasePrice)) row.PurchasePrice=existing.PurchasePrice.ToString(CultureInfo.InvariantCulture);
                    if(CatalogImportOutboxPayloadBuilder.EqualNumber(row.RetailPrice,original.RetailPrice)) row.RetailPrice=existing.RetailPrice.ToString(CultureInfo.InvariantCulture);
                    if(TextSame(row.ProductName,original.ProductName)) row.ProductName=existing.ProductName;
                    if(TextSame(row.SecondProductName,original.SecondProductName)) row.SecondProductName=existing.SecondProductName;
                    if(TextSame(row.ItemNumber,original.ItemNumber)) row.ItemNumber=existing.ItemNumber;
                    if(TextSame(row.Supplier,original.Supplier)) row.Supplier=existing.Supplier;
                    if(TextSame(row.Category,original.Category)) row.Category=existing.Category;
                }
                var local=await new SupplierExcelImportApplier(_factory).BuildPreviewAsync(localRows,token).ConfigureAwait(false);
                if(!local.CanApply || Serialize(intent.ApplyExpectations)!=Serialize(local.ApplyExpectations))
                    throw new CatalogImportRecoveryException("recovery_state_changed");
                return local;
            },token);
        }

        private static bool TextSame(string first,string second) => (first ?? "").Trim()==(second ?? "").Trim();
        internal static PosTrustedDeviceSession SnapshotTransportSession(PosTrustedDeviceSession session) => session==null ? null :
            new PosTrustedDeviceSession { DeviceToken=session.DeviceToken,SessionToken=session.SessionToken,PosSessionId=session.PosSessionId,
                ShopDeviceId=session.ShopDeviceId,ShopId=session.ShopId,ShopCode=session.ShopCode };

        private async Task StoreReceiptAsync(CatalogImportRecoveryDraft draft, PosCatalogImportReceiptResponse receipt, CancellationToken token)
        {
            await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                using (var conn = _factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    await new CatalogImportRecoveryCommit(draft, draft.Generation).ValidateAsync(conn, tx).ConfigureAwait(false);
                    await conn.ExecuteAsync(@"
INSERT INTO catalog_import_recovery(original_id,delivery_known,dispatch_count,receipt_status,receipt_json,created_at,updated_at)
VALUES(@id,0,0,@status,@json,@now,@now)
ON CONFLICT(original_id) DO UPDATE SET receipt_status=@status,receipt_json=@json,updated_at=@now;",
                        new { id=draft.Original.Id, status=receipt.Status, json=Serialize(receipt), now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
                    tx.Commit();
                    UpdateRevisionSummary(draft,receipt,draft.TargetReceipt);
                }
            }, token).ConfigureAwait(false);
            draft.Receipt = receipt;
            draft.ReceiptStatus = receipt.Status;
            draft.CanCommit = IsAuthoritative(receipt);
        }

        internal static void ValidateReceipt(CatalogImportRecoveryDraft draft, PosCatalogImportReceiptResponse value)
        {
            ValidateReceiptIdentity(draft,value);
            if (value.Status == "accepted")
            {
                if (value.Receipt == null || !value.Receipt.Ok || value.Receipt.Status!="accepted" && value.Receipt.Status!="duplicate" ||
                    string.IsNullOrWhiteSpace(value.Receipt.BatchId))
                    throw new CatalogImportRecoveryException("receipt_incomplete");
                var ack = BuildPersistedAck(draft.Original,draft.OriginalRequest,value.Receipt);
                CatalogImportOutboxRepository.EnsureRecoveryAckComplete(draft.OriginalRequest, ack, true);
            }
        }

        internal static void ValidateReceiptIdentity(CatalogImportRecoveryDraft draft, PosCatalogImportReceiptResponse value)
        {
            if (value == null || !value.Ok || value.Status != "accepted" && value.Status != "not_found" && value.Status != "retired")
                throw new CatalogImportRecoveryException(value?.Status == "conflict" ? "receipt_conflict" : "receipt_unavailable");
            if (!string.Equals(draft.Original.OriginShopId,value.ShopId,StringComparison.OrdinalIgnoreCase) ||
                value.ShopDeviceId!=draft.ShopDeviceId)
                throw new CatalogImportRecoveryException("response_shop_mismatch");
            if (value.SchemaVersion!=PosCatalogImportReceiptContract.SchemaVersion && value.SchemaVersion!=PosCatalogImportReceiptContract.RetirementSchemaVersion ||
                value.OriginalSchemaVersion!= (draft.Original.OperationType=="catalog_import_correction"
                    ? PosCatalogImportCorrectionContract.SchemaVersion : PosOnlineContract.CatalogImportSchemaVersion) ||
                value.ClientImportId != draft.Original.ClientImportId || value.IdempotencyKey != draft.Original.IdempotencyKey ||
                value.PayloadHash != draft.Original.PayloadHash || value.CanonicalPayloadHash==null ||
                value.CanonicalPayloadHash.Length!=71 || !value.CanonicalPayloadHash.StartsWith("sha256:",StringComparison.Ordinal) ||
                value.CanonicalPayloadHash.Skip(7).Any(character=>!(character>='0' && character<='9' || character>='a' && character<='f')))
                throw new CatalogImportRecoveryException("receipt_conflict");
            if (value.Status=="not_found" && (!value.SnapshotOnly || value.ReplacementAllowed) ||
                value.Status=="retired" && (!value.OldIdentityBlocked || string.IsNullOrWhiteSpace(value.RetiredAt)))
                throw new CatalogImportRecoveryException("receipt_conflict");
        }

        private static bool IsAuthoritative(PosCatalogImportReceiptResponse receipt) => receipt != null &&
            (receipt.Status == "accepted" || receipt.Status == "retired" && receipt.OldIdentityBlocked && !string.IsNullOrWhiteSpace(receipt.RetiredAt));

        internal static CatalogImportAckResult BuildPersistedAck(CatalogImportRecoveryOriginal original,PosCatalogImportRequest request,PosCatalogImportPersistedAck persisted)
        {
            if(persisted==null || (persisted.RemoteProductIds ?? Array.Empty<PosCatalogImportPersistedProductAck>())
                .Any(product=>product==null || string.IsNullOrWhiteSpace(product.ClientItemId)))
                throw new CatalogImportRecoveryException("receipt_incomplete");
            // RPC price receipts include ownership; do not discard it before
            // translating to the older ordinary ACK representation.
            var products=(persisted.RemoteProductIds ?? Array.Empty<PosCatalogImportPersistedProductAck>())
                .GroupBy(product=>product.ClientItemId,StringComparer.Ordinal)
                .ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.Ordinal);
            foreach(var price in persisted.RemotePriceIds ?? Array.Empty<PosCatalogImportPersistedPriceAck>())
                if (price==null || string.IsNullOrWhiteSpace(price.ClientItemId) ||
                    !products.TryGetValue(price.ClientItemId,out var owners) || owners.Length!=1 ||
                    string.IsNullOrWhiteSpace(price.RemoteProductId) || price.RemoteProductId!=owners[0].RemoteProductId ||
                    !string.IsNullOrEmpty(price.Barcode) && !string.IsNullOrEmpty(owners[0].Barcode) && price.Barcode!=owners[0].Barcode)
                    throw new CatalogImportRecoveryException("receipt_price_owner_mismatch");
            var adapted=new PosCatalogImportResponse { Ok=persisted.Ok,Code=persisted.Status,ServerImportId=persisted.BatchId,
                Batch=new PosCatalogImportBatchResponse { ClientImportId=original.ClientImportId,IdempotencyKey=original.IdempotencyKey,
                    PayloadHash=original.PayloadHash,Status=persisted.Status,ServerImportId=persisted.BatchId },
                Items=(persisted.Items ?? Array.Empty<PosCatalogImportPersistedItemAck>()).Select(item=>new PosCatalogImportItemAck
                { Barcode=item.Barcode,ClientItemId=item.ClientItemId,RemoteProductId=item.RemoteProductId,RemotePriceId=item.RemotePriceId,
                    PriceType=item.PriceType,Status=item.Status,Code=item.Code }).ToArray(),
                RemoteProductIds=(persisted.RemoteProductIds ?? Array.Empty<PosCatalogImportPersistedProductAck>()).Select(product=>new PosCatalogImportRemoteProductIdAck
                    { ClientItemId=product.ClientItemId,Barcode=product.Barcode,RemoteProductId=product.RemoteProductId }).ToArray(),
                RemotePriceIds=(persisted.RemotePriceIds ?? Array.Empty<PosCatalogImportPersistedPriceAck>()).Select(price=>new PosCatalogImportRemotePriceIdAck
                { Barcode=price.Barcode,ClientItemId=price.ClientItemId,RemotePriceId=price.RemotePriceId,PriceType=price.PriceType }).ToArray() };
            return CatalogImportSyncService.BuildAckResult(ToOutbox(original),request,adapted,null);
        }

        private static PosCatalogImportReceiptRequest BuildReceiptRequest(CatalogImportRecoveryDraft draft, PosTrustedDeviceSession session) =>
            new PosCatalogImportReceiptRequest { ClientImportId=draft.Original.ClientImportId, IdempotencyKey=draft.Original.IdempotencyKey,
                PayloadHash=draft.Original.PayloadHash, OriginalRequest=CloneForTransport(draft.OriginalRequest,draft.Original.PayloadHash), DeviceToken=session.DeviceToken, SessionToken=session.SessionToken,
                PosSessionId=session.PosSessionId, ShopDeviceId=session.ShopDeviceId, ShopCode=draft.Original.OriginShopCode };

        internal static PosCatalogImportRequest CloneForTransport(PosCatalogImportRequest original,string hash)
        {
            var copy=Deserialize<PosCatalogImportRequest>(Serialize(original));
            copy.PayloadHash=hash;
            return copy;
        }

        private static void DemandPermission(Func<bool> authorize) { if (authorize == null || !authorize()) throw new CatalogImportRecoveryException("permission_denied"); }
        private static void DemandRows(CatalogImportRecoveryDraft draft, IReadOnlyList<SupplierImportEditableRow> rows)
        {
            var originals = draft.OriginalRequest.Items.ToDictionary(item => item.Barcode, StringComparer.Ordinal);
            if (rows == null || rows.Count != draft.OriginalRequest.Items.Length || rows.Any(row => row == null || row.IsSkipped) ||
                rows.Select(row => row.RowNumber + "|" + row.Barcode).Distinct(StringComparer.Ordinal).Count() != rows.Count ||
                rows.Any(row => !originals.TryGetValue(row.Barcode, out var original) || original.RowNumber != row.RowNumber))
                throw new CatalogImportRecoveryException("recovery_rows_changed");
        }

        internal const string SelectOriginal = @"
SELECT o.id AS Id,o.client_import_id AS ClientImportId,o.idempotency_key AS IdempotencyKey,
 o.payload_hash AS PayloadHash,o.payload_json AS PayloadJson,o.origin_shop_id AS OriginShopId,
 o.origin_shop_code AS OriginShopCode,o.last_error_code AS LastErrorCode,o.status AS Status,
 o.operation_type AS OperationType,
 COALESCE(r.delivery_known,0) AS DeliveryKnown,COALESCE(r.dispatch_count,0) AS DispatchCount,
 r.receipt_status AS ReceiptStatus,r.receipt_json AS ReceiptJson,r.replacement_id AS ReplacementId
FROM catalog_import_outbox o LEFT JOIN catalog_import_recovery r ON r.original_id=o.id ";

        internal static async Task<CatalogImportRecoveryOriginal> LoadOriginalAsync(SqliteConnection conn, SqliteTransaction tx, long id)
        {
            var row = await conn.QuerySingleOrDefaultAsync<CatalogImportRecoveryOriginal>(SelectOriginal + " WHERE o.id=@id", new { id }, tx).ConfigureAwait(false);
            if (row == null || row.Status != "failed_blocked") throw new CatalogImportRecoveryException("recovery_state_changed");
            return row;
        }

        private static PosCatalogImportRequest ReadOriginal(CatalogImportRecoveryOriginal row)
        {
            if (CatalogImportOutboxPayloadBuilder.Sha256Hex(row.PayloadJson) != row.PayloadHash)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            PosCatalogImportRequest request;
            try { request=Deserialize<PosCatalogImportRequest>(row.PayloadJson); }
            catch { throw new CatalogImportRecoveryException("payload_invalid"); }
            if (request?.Batch == null || request.Batch.ClientImportId != row.ClientImportId || request.Batch.IdempotencyKey != row.IdempotencyKey ||
                request.Items == null || request.Items.Length == 0 || request.Items.Length > 5000 ||
                request.Items.Any(item => item == null || string.IsNullOrWhiteSpace(item.Barcode)) ||
                request.Items.Select(item => item.Barcode).Distinct(StringComparer.Ordinal).Count() != request.Items.Length ||
                request.Items.Any(item => string.IsNullOrWhiteSpace(item.ClientItemId)) ||
                request.Items.Select(item => item.ClientItemId).Distinct(StringComparer.Ordinal).Count() != request.Items.Length)
                throw new CatalogImportRecoveryException("payload_invalid");
            return request;
        }

        private static CatalogImportRecoveryBatch BuildBatch(CatalogImportRecoveryOriginal row, PosCatalogImportRequest request)
        {
            var issues = new List<CatalogImportRecoveryIssue>();
            foreach (var item in request?.Items ?? Array.Empty<PosCatalogImportItemRequest>())
                foreach (var field in new[] { "retailPrice", "purchasePrice" })
                {
                    var value = field == "retailPrice" ? item.RetailPrice : item.PurchasePrice;
                    if (!CatalogImportOutboxPayloadBuilder.IsAdminPrice(value) && issues.Count < 20)
                        issues.Add(new CatalogImportRecoveryIssue { Barcode=item.Barcode, Field=field, Value=value });
                }
            return new CatalogImportRecoveryBatch { OutboxId=row.Id, ClientImportId=row.ClientImportId, PayloadHash=row.PayloadHash,
                OriginShopId=row.OriginShopId, OriginShopCode=row.OriginShopCode, LastErrorCode=row.LastErrorCode,
                NeverSent=row.DeliveryKnown && row.DispatchCount == 0, RecoveryState=row.ReplacementId.HasValue ? "awaiting_ack" : row.ReceiptStatus ?? "unverified",
                ReplacementOutboxId=row.ReplacementId, ItemCount=request?.Items?.Length ?? 0, Issues=issues };
        }

        private static SupplierImportEditableRow ToEditable(PosCatalogImportItemRequest item) => new SupplierImportEditableRow
        {
            RowNumber=item.RowNumber, Barcode=item.Barcode, ProductName=item.ProductName, SecondProductName=item.SecondProductName,
            ItemNumber=item.ItemNumber, PurchasePrice=item.PurchasePrice, RetailPrice=item.RetailPrice, Quantity=item.Quantity,
            Supplier=item.Supplier, Category=item.Category,
            HasItemNumberSource=item.ItemNumber != null,HasProductNameSource=item.ProductName != null,
            HasSecondProductNameSource=item.SecondProductName != null,HasPurchasePriceSource=item.PurchasePrice != null,
            HasRetailPriceSource=item.RetailPrice != null,HasQuantitySource=item.Quantity != null,
            HasSupplierSource=item.Supplier != null,HasCategorySource=item.Category != null
        };
        private static SupplierImportEditableRow[] SnapshotRows(IReadOnlyList<SupplierImportEditableRow> rows) => rows.Select(row =>
            new SupplierImportEditableRow { RowNumber=row.RowNumber,Barcode=row.Barcode,ProductName=row.ProductName,
                SecondProductName=row.SecondProductName,ItemNumber=row.ItemNumber,PurchasePrice=row.PurchasePrice,
                RetailPrice=row.RetailPrice,Quantity=row.Quantity,Supplier=row.Supplier,Category=row.Category,IsSkipped=row.IsSkipped,
                HasItemNumberSource=row.HasItemNumberSource,HasProductNameSource=row.HasProductNameSource,
                HasSecondProductNameSource=row.HasSecondProductNameSource,HasPurchasePriceSource=row.HasPurchasePriceSource,
                HasRetailPriceSource=row.HasRetailPriceSource,HasQuantitySource=row.HasQuantitySource,HasSupplierSource=row.HasSupplierSource,
                HasCategorySource=row.HasCategorySource,RetailPriceMissingButPurchasePresent=row.RetailPriceMissingButPurchasePresent,Exists=row.Exists }).ToArray();
        internal static CatalogImportOutboxItem ToOutbox(CatalogImportRecoveryOriginal row) => new CatalogImportOutboxItem
        { Id=row.Id,ClientImportId=row.ClientImportId,IdempotencyKey=row.IdempotencyKey,PayloadHash=row.PayloadHash };
        internal static async Task<long> ReadEpochAsync(SqliteConnection conn, SqliteTransaction tx)
        {
            var raw = await conn.ExecuteScalarAsync<string>("SELECT value FROM app_settings WHERE key=@key", new { key=CatalogShopStateRepository.TransitionEpochKey }, tx).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw)) return 0;
            long epoch;
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out epoch) || epoch < 0)
                throw new CatalogImportRecoveryException("transition_epoch_invalid");
            return epoch;
        }
        internal static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings
                { UseSimpleDictionaryFormat=true }).WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); }
        }
        internal static void UpdateRevisionSummary(CatalogImportRecoveryDraft draft,PosCatalogImportReceiptResponse root,PosCatalogImportReceiptResponse target)
        {
            var revisions=(root?.CurrentProductSnapshots ?? Array.Empty<PosCatalogImportProductSnapshot>())
                .Concat(target?.CurrentProductSnapshots ?? Array.Empty<PosCatalogImportProductSnapshot>())
                .Where(snapshot=>snapshot!=null && !string.IsNullOrEmpty(snapshot.BaseRevision)).Take(5000)
                .Select(snapshot=>snapshot.BaseRevision).OrderBy(revision=>revision,StringComparer.Ordinal).ToArray();
            draft.RevisionCount=revisions.Length;
            draft.RevisionFingerprint=revisions.Length==0 ? string.Empty : CatalogImportOutboxPayloadBuilder.Sha256Hex(string.Join("|",revisions));
        }
        internal static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (T)new DataContractJsonSerializer(typeof(T),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat=true }).ReadObject(stream);
        }
    }

    internal sealed class CatalogImportRecoveryCommit
    {
        private readonly CatalogImportRecoveryDraft _draft;
        private readonly OnlineSyncGeneration _generation;
        private readonly Func<bool> _authorize;
        internal CatalogImportRecoveryCommit(CatalogImportRecoveryDraft draft, OnlineSyncGeneration generation, Func<bool> authorize = null)
        { _draft=draft; _generation=generation; _authorize=authorize; }
        internal async Task ValidateAsync(SqliteConnection conn, SqliteTransaction tx, bool requireUnlinked = true,long excludeCandidateId=0)
        {
            DemandAuthorization();
            var current = await CatalogImportRecoveryService.LoadOriginalAsync(conn, tx, _draft.Original.Id).ConfigureAwait(false);
            if (current.PayloadHash != _draft.Original.PayloadHash || current.PayloadJson != _draft.Original.PayloadJson ||
                requireUnlinked && _draft.TargetOriginal==null && current.ReplacementId != null ||
                _draft.TargetOriginal!=null && current.ReplacementId!=_draft.Original.ReplacementId)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            if(_draft.TargetOriginal!=null)
            {
                var target=await CatalogImportRecoveryService.LoadOriginalAsync(conn,tx,_draft.TargetOriginal.Id).ConfigureAwait(false);
                if(target.PayloadHash!=_draft.TargetOriginal.PayloadHash || target.PayloadJson!=_draft.TargetOriginal.PayloadJson ||
                    target.OperationType!="catalog_import_correction" || target.ReplacementId.HasValue ||
                    _draft.TargetReceipt!=null && target.ReceiptJson!=CatalogImportRecoveryService.Serialize(_draft.TargetReceipt))
                    throw new CatalogImportRecoveryException("recovery_state_changed");
                var linked=current.ReplacementId;
                var depth=0;
                while(linked.HasValue && linked.Value!=target.Id && depth++<128)
                {
                    var next=await conn.ExecuteScalarAsync<long?>("SELECT replacement_id FROM catalog_import_recovery WHERE original_id=@id",new { id=linked.Value },tx).ConfigureAwait(false);
                    if(next.HasValue && next.Value<=linked.Value) throw new CatalogImportRecoveryException("recovery_state_changed");
                    linked=next;
                }
                if(linked!=target.Id) throw new CatalogImportRecoveryException("recovery_state_changed");
            }
            var shop = await OutboxShopBinding.ResolveRequiredAsync(conn, tx).ConfigureAwait(false);
            if (OutboxShopBinding.GetMismatchCode(current.OriginShopId, current.OriginShopCode, shop.ShopId, shop.ShopCode).Length > 0 ||
                await CatalogImportRecoveryService.ReadEpochAsync(conn, tx).ConfigureAwait(false) != _draft.TransitionEpoch)
                throw new CatalogImportRecoveryException("origin_shop_mismatch");
            if (_draft.Generation?.Fingerprint != _generation?.Fingerprint ||
                (_generation != null ? !await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(conn, tx, _generation).ConfigureAwait(false) :
                 await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM pos_sync_session_generation WHERE singleton_id=1 AND active=1", transaction:tx).ConfigureAwait(false) != 0))
                throw new CatalogImportRecoveryException("trusted_generation_changed");
            if (_draft.ContributionSetCaptured)
            {
                var candidates=(await conn.QueryAsync<CatalogImportRecoveryOriginal>(CatalogImportRecoveryService.SelectOriginal+
                    CatalogImportRecoveryService.ContributionCandidatesWhere,
                    CatalogImportRecoveryService.ContributionParameters(_draft,excludeCandidateId),tx).ConfigureAwait(false)).Select(row=>row.Id).ToArray();
                if (!candidates.SequenceEqual(_draft.Contributions.Select(row=>row.ContributorId).OrderBy(id=>id)))
                    throw new CatalogImportRecoveryException("recovery_overlap_pending");
            }
            foreach (var contribution in _draft.Contributions)
                if (await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(*) FROM catalog_import_outbox o
JOIN catalog_import_recovery_contributions c ON c.contributor_id=o.id AND c.original_id=@originalId
WHERE o.id=@id AND o.payload_hash=@hash AND c.payload_hash=@hash AND c.receipt_json=@receipt
AND o.origin_shop_id=@shopId AND o.origin_shop_code=@shopCode AND o.status IN ('pending','retry','failed_blocked','acked');",
                    new { originalId=_draft.Original.Id,id=contribution.ContributorId,hash=contribution.PayloadHash,
                        receipt=CatalogImportRecoveryService.Serialize(contribution.Receipt),shopId=current.OriginShopId,shopCode=current.OriginShopCode },tx).ConfigureAwait(false)!=1)
                    throw new CatalogImportRecoveryException("recovery_overlap_pending");
        }
        internal void DemandAuthorization()
        {
            if (_authorize != null && !_authorize()) throw new CatalogImportRecoveryException("permission_denied");
        }

        internal async Task AttachAsync(SqliteConnection conn, SqliteTransaction tx, long replacementId)
        {
            await ValidateAsync(conn, tx,true,replacementId).ConfigureAwait(false);
            if (!_draft.CanCommit) throw new CatalogImportRecoveryException("receipt_required");
            await ReconcileReceiptsAsync(conn,tx).ConfigureAwait(false);
            var rows = await conn.ExecuteAsync(@"
INSERT INTO catalog_import_recovery(original_id,delivery_known,dispatch_count,receipt_status,replacement_id,created_at,updated_at)
VALUES(@id,0,0,@status,@replacementId,@now,@now)
ON CONFLICT(original_id) DO UPDATE SET replacement_id=@replacementId,updated_at=@now WHERE replacement_id IS NULL;",
                new { id=(_draft.TargetOriginal ?? _draft.Original).Id,status=_draft.TargetReceipt?.Status ?? (_draft.ReceiptStatus == "never_sent" ? "unverified" : _draft.ReceiptStatus),
                    replacementId,now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx).ConfigureAwait(false);
            if (rows != 1) throw new CatalogImportRecoveryException("recovery_state_changed");
        }

        private async Task ReconcileReceiptsAsync(SqliteConnection conn,SqliteTransaction tx)
        {
            if (_draft.ReceiptStatus == "accepted")
            {
                var ack = CatalogImportRecoveryService.BuildPersistedAck(_draft.Original,_draft.OriginalRequest,_draft.Receipt.Receipt);
                await CatalogImportOutboxRepository.ApplyRecoveryProductIdsAsync(conn, tx, ack.RemoteProductIds).ConfigureAwait(false);
                await CatalogImportOutboxRepository.ApplyRemotePriceIdsAsync(conn, tx, ack.RemotePriceIds, _draft.Original.IdempotencyKey).ConfigureAwait(false);
            }
            foreach (var contribution in _draft.Contributions)
                await CatalogImportOutboxRepository.ReconcileRecoveryContributionAsync(conn,tx,_draft.Original,_draft.OriginalRequest,contribution).ConfigureAwait(false);
            if(_draft.TargetReceipt?.Status=="accepted")
                await CatalogImportOutboxRepository.ReconcileRecoveryContributionAsync(conn,tx,_draft.Original,_draft.OriginalRequest,
                    new CatalogImportRecoveryContribution { ContributorId=_draft.TargetOriginal.Id,PayloadHash=_draft.TargetOriginal.PayloadHash,
                        Request=_draft.TargetAckRequest,Receipt=_draft.TargetReceipt },_draft.TargetAck).ConfigureAwait(false);
        }

        internal async Task FinalizeAsync(SqliteConnection conn, SqliteTransaction tx)
        {
            await ValidateAsync(conn,tx).ConfigureAwait(false);
            if (!_draft.CanCommit || _draft.ReceiptStatus != "accepted" && _draft.Contributions.Count == 0)
                throw new CatalogImportRecoveryException("receipt_required");
            await ReconcileReceiptsAsync(conn,tx).ConfigureAwait(false);
            if(_draft.TargetReceipt?.Status=="accepted")
            {
                var targetNow=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await conn.ExecuteAsync("UPDATE catalog_import_recovery SET resolved_at=@targetNow,updated_at=@targetNow WHERE original_id=@id;",
                    new { id=_draft.TargetOriginal.Id,targetNow },tx).ConfigureAwait(false);
                return;
            }
            // This path is entered only when every intended row is covered by
            // complete authoritative receipts, with no new remote effect due.
            var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await conn.ExecuteAsync(@"UPDATE catalog_import_outbox SET status='recovered',updated_at=@now WHERE id=@id AND payload_hash=@hash AND status='failed_blocked';
UPDATE catalog_import_recovery SET resolved_at=@now,updated_at=@now WHERE original_id=@id;",
                new { id=_draft.Original.Id,hash=_draft.Original.PayloadHash,now },tx).ConfigureAwait(false);
        }
    }
}
