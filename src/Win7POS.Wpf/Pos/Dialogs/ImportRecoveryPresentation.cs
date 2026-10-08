using System;
using System.Linq;
using Win7POS.Data.Online;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class ImportRecoveryPresentation
    {
        public ImportRecoveryPresentation(CatalogImportRecoveryBatch batch)
        {
            Batch = batch ?? throw new ArgumentNullException(nameof(batch));
            Title = PosLocalization.F("importRecovery.batch", batch.OutboxId, batch.ItemCount);
            Cause = FriendlyCause(string.IsNullOrWhiteSpace(batch.ReplacementErrorCode) ? batch.LastErrorCode : batch.ReplacementErrorCode);
            State = PosLocalization.T(batch.CanRecoverReplacement ? "importRecovery.replacementBlocked" : batch.ReplacementOutboxId.HasValue
                ? "importRecovery.awaitingAck" : batch.NeverSent
                    ? "importRecovery.neverSent" : "importRecovery.receipt.unknown");
            AffectedFields = string.Join(Environment.NewLine, batch.Issues.Take(20).Select(issue =>
                PosLocalization.F("importRecovery.affectedField", issue.Barcode,
                    PosLocalization.T("importRecovery." + FieldKey(issue.Field)), issue.Value)));
        }

        public CatalogImportRecoveryBatch Batch { get; }
        public string Title { get; }
        public string Cause { get; }
        public string State { get; }
        public string AffectedFields { get; }
        public bool CanPrepare => !Batch.ReplacementOutboxId.HasValue || Batch.CanRecoverReplacement;

        internal static string FieldKey(string field)
        {
            if (string.Equals(field, "retailPrice", StringComparison.OrdinalIgnoreCase)) return "retail";
            if (string.Equals(field, "purchasePrice", StringComparison.OrdinalIgnoreCase)) return "purchase";
            if (string.Equals(field, "quantity", StringComparison.OrdinalIgnoreCase)) return "quantity";
            return "data";
        }

        internal static string FriendlyCause(string code)
        {
            var safe = (code ?? string.Empty).ToLowerInvariant();
            var key = safe.Contains("quantity_conflict") || safe.Contains("stock_conflict") ? "quantityConflict" : safe.Contains("payload_too_large") || safe.Contains("item_limit") ? "batchLimit" :
                safe.Contains("overlap_pending") ? "overlapPending" : safe.Contains("history_conflict") ? "historyConflict" :
                safe.Contains("receipt_price_owner") || safe.Contains("receipt_incomplete") || safe.Contains("receipt_conflict")
                    ? "receiptConflict" : safe.Contains("snapshot_unavailable") || safe.Contains("metadata_changed") ? "productChanged" :
                safe.Contains("local_save_failed") ? "saveFailed" : safe.Contains("backup") ? "backupFailed" : safe.Contains("conflict") || safe.Contains("stale") || safe.Contains("shop") ||
                safe.Contains("changed") || safe.Contains("epoch") || safe.Contains("hash_mismatch")
                    ? "conflict" : safe.Contains("permission") || safe.Contains("auth") || safe.Contains("trust") || safe.Contains("session")
                        ? "authentication" : safe.Contains("network") || safe.Contains("timeout") || safe.Contains("http") || safe.Contains("unavailable")
                        ? "network" : safe.Contains("price") || safe.Contains("validation") || safe.Contains("payload")
                            ? "invalidData" : "unknownBlocked";
            return PosLocalization.T("importRecovery." + key);
        }
    }
}
