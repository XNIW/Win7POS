using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogImportRecoveryService
    {
        internal static CatalogImportOutboxPlan CreateConvergencePlan(CatalogImportRecoveryDraft draft,
            IReadOnlyList<SupplierImportEditableRow> rows)
        {
            if (draft.Supersession == null || !draft.Supersession.IsSettled)
                throw new CatalogImportRecoveryException("receipt_required");
            DemandRows(draft, rows);
            return EmptyConvergencePlan("catalog-plan-" + CatalogImportOutboxPayloadBuilder.Sha256Hex(
                "coverage-only/v1\n" + draft.Original.PayloadHash + "\n" + draft.Supersession.PlanId + "\n" +
                draft.OperationCreatedAtUtc + "\n" + Serialize(SnapshotRows(rows))));
        }

        private static CatalogImportOutboxPlan EmptyConvergencePlan(string planId)
        {
            if (planId == null || !planId.StartsWith("catalog-plan-", StringComparison.Ordinal) ||
                !CatalogImportRecoveryProofTransport.IsHash("sha256:" + planId.Substring("catalog-plan-".Length)))
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            return new CatalogImportOutboxPlan { PlanId = planId, Entries = Array.Empty<CatalogImportOutboxEntry>(), TotalRows = 0 };
        }

        internal static async Task<CatalogImportSavedRemotePlan> CompleteConvergenceSupersessionAsync(SqliteConnection conn, SqliteTransaction tx,
            CatalogImportRecoveryDraft draft, long now)
        {
            var source = await ReadConvergenceSourceAsync(conn, tx, draft.Original.Id).ConfigureAwait(false);
            if (source == null) throw new CatalogImportRecoveryException("receipt_required");
            ValidatePreparedSource(draft, source);
            var record = Deserialize<PreparedPlan>(source.PlanJson);
            var expected = CreateConvergencePlan(draft, Deserialize<SupplierImportEditableRow[]>(source.RowsJson));
            if (record.PlanId != expected.PlanId || record.Entries.Length != 0 || record.TotalRows != 0 ||
                source.CreatedAt != draft.OperationCreatedAtUtc)
                throw new CatalogImportRecoveryException("payload_hash_mismatch");
            await BindSupersessionAsync(conn, tx, draft, expected.PlanId).ConfigureAwait(false);
            await CompleteSupersessionAsync(conn, tx, expected.PlanId, now).ConfigureAwait(false);
            if (await conn.ExecuteAsync("DELETE FROM catalog_import_prepared_plan WHERE original_id=@id AND plan_hash=@hash AND remote_plan_json=@remote",
                new { id = draft.Original.Id, hash = source.PlanHash, remote = source.RemoteJson }, tx).ConfigureAwait(false) != 1)
                throw new CatalogImportRecoveryException("recovery_state_changed");
            var saved = Deserialize<CatalogImportSavedRemotePlan>(source.RemoteJson);
            saved.LocalRecoveryRowsJson = source.RowsJson;
            return saved;
        }

        private static Task<PreparedSupersessionSource> ReadConvergenceSourceAsync(SqliteConnection conn, SqliteTransaction tx, long originalId) =>
            conn.QuerySingleOrDefaultAsync<PreparedSupersessionSource>(@"SELECT original_hash AS OriginalHash,target_id AS TargetId,
target_hash AS TargetHash,rows_json AS RowsJson,rows_hash AS RowsHash,plan_json AS PlanJson,plan_hash AS PlanHash,
plan_document_json AS DocumentJson,plan_document_hash AS DocumentHash,remote_plan_json AS RemoteJson,remote_plan_hash AS RemoteHash,
operation_created_at AS CreatedAt,dispatch_started_at AS DispatchStarted FROM catalog_import_prepared_plan WHERE original_id=@originalId",
                new { originalId }, tx);

        private static async Task ValidateConvergenceCompletionAsync(SqliteConnection conn, SqliteTransaction tx,
            CatalogImportRecoveryOriginal root, CatalogImportRecoverySupersession predecessor, string localPlanId)
        {
            var source = await ReadConvergenceSourceAsync(conn, tx, root.Id).ConfigureAwait(false);
            if (source == null || source.OriginalHash != root.PayloadHash || source.TargetId != root.Id || source.TargetHash != root.PayloadHash ||
                !source.DispatchStarted.HasValue || source.RemoteJson == null || source.DocumentJson == null ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(source.PlanJson) != source.PlanHash ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(source.RowsJson) != source.RowsHash ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(source.RemoteJson) != source.RemoteHash ||
                CatalogImportOutboxPayloadBuilder.Sha256Hex(source.DocumentJson) != source.DocumentHash)
                throw new CatalogImportRecoveryException("receipt_required");
            var saved = Deserialize<CatalogImportSavedRemotePlan>(source.RemoteJson);
            var record = Deserialize<PreparedPlan>(source.PlanJson);
            var draft = new CatalogImportRecoveryDraft { Original = root, OriginalRequest = ReadOriginal(root),
                Supersession = predecessor, OperationCreatedAtUtc = source.CreatedAt };
            var plan = CreateConvergencePlan(draft, Deserialize<SupplierImportEditableRow[]>(source.RowsJson));
            if (localPlanId != plan.PlanId || record.PlanId != plan.PlanId || record.Entries.Length != 0 || record.TotalRows != 0 ||
                saved.Document.Supersedes?.PlanId != predecessor.PlanId ||
                saved.Document.VerifiedOriginalId != predecessor.SavedPlan.Document.VerifiedOriginalId ||
                saved.Document.Mode != predecessor.SavedPlan.Document.Mode || saved.Receipt.ParentStatus != "complete" ||
                saved.Receipt.ItemCount != 0 || saved.Document.Parts.Length != 0 ||
                Serialize(saved.Document) != source.DocumentJson)
                throw new CatalogImportRecoveryException("receipt_conflict");
            ValidateRemotePlan(plan, saved.Document, saved.Receipt, new Win7POS.Core.Online.PosTrustedDeviceSession
                { ShopId = predecessor.SavedPlan.Receipt.ShopId, ShopDeviceId = predecessor.SavedPlan.Receipt.ShopDeviceId });
        }
    }
}
