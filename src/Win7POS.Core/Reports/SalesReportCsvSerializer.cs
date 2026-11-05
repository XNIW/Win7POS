using System;
using System.Globalization;

namespace Win7POS.Core.Reports
{
    /// <summary>Pure typed serializer. It neither opens files nor resolves authorization or report data.</summary>
    public static class SalesReportCsvSerializer
    {
        public const string SchemaVersion = "win7pos-sales-report-v1";
        public const string Header = "record_type;schema_version;report_token;coverage;ownership_policy;native_time_policy;origin;internal_id;business_datetime;movement_kind;signed_amount_clp;cash_tendered_clp;change_clp;cash_clp;card_clp;original_cashier_id;reference;provider;external_namespace;external_id;batch_id;source_sheet;source_row;native_positive_sales_clp;native_positive_receipts;native_average_receipt_clp";

        public static string Serialize(SalesReportCsvRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            var fields = new string[26];
            for (var i = 0; i < fields.Length; i++) fields[i] = string.Empty;
            fields[0] = row.IsMetadata ? "REPORT" : "MOVEMENT";
            fields[1] = Text(SchemaVersion); fields[2] = Text(row.Token.Fingerprint); fields[3] = Text(row.Coverage.Status.ToString());
            fields[4] = Text(row.Token.OwnershipPolicyHash); fields[5] = Text(row.Token.NativeTimePolicyHash);
            if (row.IsMetadata)
            {
                fields[23] = Integer(row.Summary.NativePositiveSalesClp); fields[24] = Integer(row.Summary.NativePositiveReceiptCount);
                fields[25] = row.Summary.NativeAverageReceiptClp.HasValue ? Integer(row.Summary.NativeAverageReceiptClp.Value) : string.Empty;
            }
            else
            {
                var entry = row.Entry;
                fields[6] = Text(entry.Key.Origin.ToString()); fields[7] = Integer(entry.Key.InternalId);
                fields[8] = Text(entry.BusinessDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                fields[9] = Text(entry.MovementKind.ToString()); fields[10] = Integer(entry.SignedAmountClp);
                fields[11] = Integer(entry.CashTenderedClp); fields[12] = Integer(entry.ChangeClp); fields[13] = Integer(entry.CashClp); fields[14] = Integer(entry.CardClp);
                fields[15] = entry.OriginalCashierId.HasValue ? Integer(entry.OriginalCashierId.Value) : string.Empty;
                fields[16] = Text(entry.Reference); fields[17] = Text(entry.Provider); fields[18] = Text(entry.ExternalNamespace);
                fields[19] = Text(entry.ExternalId); fields[20] = Text(entry.BatchId); fields[21] = Text(entry.SourceSheet);
                fields[22] = entry.SourceRow.HasValue ? Integer(entry.SourceRow.Value) : string.Empty;
            }
            return string.Join(";", fields);
        }
        public static string Text(string value)
        {
            var text = value ?? string.Empty;
            var first = 0;
            while (first < text.Length && (char.IsWhiteSpace(text[first]) || char.IsControl(text[first]) || char.GetUnicodeCategory(text[first]) == UnicodeCategory.Format)) first++;
            if (first < text.Length && (text[first] == '=' || text[first] == '+' || text[first] == '-' || text[first] == '@')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        private static string Integer(long value) { return value.ToString(CultureInfo.InvariantCulture); }
    }
}
