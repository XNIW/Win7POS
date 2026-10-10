using System;
using System.Globalization;
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
            if (batch.PlanTotalParts > 0)
                State += Environment.NewLine + PosLocalization.F("importRecovery.planProgress", batch.PlanCompletedRows,
                    batch.PlanTotalRows, batch.PlanCompletedParts, batch.PlanTotalParts);
            if (batch.PlanFailedParts > 0)
                State += Environment.NewLine + PosLocalization.F("importRecovery.planFailed", batch.PlanFailedParts);
            if (batch.HasPreparedPlan)
                State += Environment.NewLine + PosLocalization.T("importRecovery.preparedPlan");
            else if (batch.HasSavedDraft)
                State += Environment.NewLine + PosLocalization.T("importRecovery.draftSaved");
            AffectedFields = string.Join(Environment.NewLine, batch.Issues.Take(20).Select(issue =>
                PosLocalization.F("importRecovery.affectedField", issue.Barcode,
                    PosLocalization.T("importRecovery." + FieldKey(issue.Field)), issue.Value)));
        }

        public CatalogImportRecoveryBatch Batch { get; }
        public string Title { get; }
        public string Cause { get; }
        public string State { get; }
        public string AffectedFields { get; }
        public bool CanPrepare => !Batch.ReplacementOutboxId.HasValue || Batch.CanRecoverReplacement || Batch.HasSavedDraft || Batch.HasPreparedPlan || Batch.PlanTotalParts > 1;

        internal static string FieldKey(string field)
        {
            if (string.Equals(field, "retailPrice", StringComparison.OrdinalIgnoreCase)) return "retail";
            if (string.Equals(field, "purchasePrice", StringComparison.OrdinalIgnoreCase)) return "purchase";
            if (string.Equals(field, "quantity", StringComparison.OrdinalIgnoreCase)) return "quantity";
            return "data";
        }

        internal static string FriendlyCause(string code)
        {
            var rowError = DescribePlanRowError(code);
            if (rowError != null) return rowError;
            var safe = (code ?? string.Empty).ToLowerInvariant();
            var key = safe == "quota_exceeded" ? "recoveryCapacity" : safe.Contains("prepared_plan_") ? "preparedPlanConflict" : safe.Contains("quantity_conflict") || safe.Contains("stock_conflict") ? "quantityConflict" : safe.Contains("payload_too_large") || safe.Contains("row_too_large") || safe.Contains("item_limit") ? "batchLimit" :
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

        internal static string DescribePlanRowError(string code)
        {
            var invalidUnicode = (code ?? string.Empty).StartsWith("supplier_import_field_invalid_unicode|", StringComparison.Ordinal);
            var parts = (code ?? string.Empty).Split(new[] { '|' }, invalidUnicode ? 4 : 5);
            var maximum = 0;
            if ((invalidUnicode ? parts.Length != 4 : parts.Length != 5 || parts[0] != "supplier_import_field_too_long") ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1 ||
                !invalidUnicode && (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out maximum) || maximum < 1))
                return null;
            string field;
            switch (parts[2])
            {
                case "barcode": field = "Barcode"; break;
                case "productName": field = "ProductName"; break;
                case "secondProductName": field = "SecondProductName"; break;
                case "itemNumber": field = "ItemNumber"; break;
                case "supplier": field = "Supplier"; break;
                case "category": field = "Category"; break;
                case "purchasePrice": field = "PurchasePrice"; break;
                case "retailPrice": field = "RetailPrice"; break;
                case "quantity": field = "Quantity"; break;
                default: return null;
            }
            var barcode = new string(parts[invalidUnicode ? 3 : 4].Where(character => !char.IsControl(character)).Take(80).ToArray());
            return PosLocalization.F("importRecovery.rowError", row, barcode,
                invalidUnicode ? PosLocalization.F("importRecovery.fieldInvalidUnicode", PosLocalization.T("supplierExcelImport.field" + field)) :
                    PosLocalization.F("importRecovery.fieldTooLong", PosLocalization.T("supplierExcelImport.field" + field), maximum));
        }
    }
}
