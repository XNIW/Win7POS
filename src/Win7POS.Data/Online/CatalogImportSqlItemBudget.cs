using System;
using System.Linq;
using System.Text;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    // Admin 15ee1886 retains the apply/correction RPC's pg_column_size(items)
    // bound in addition to its HTTP bound. This is an upper bound for the
    // normalized shapes, not a PostgreSQL binary encoder or a new threshold.
    internal static class CatalogImportSqlItemBudget
    {
        internal const int MaximumBytes = CatalogImportPlanBuilder.MaximumTransportBytes;
        private const int ArrayRootBytes = 8; // varlena + array container header
        private const int EntryBytes = 4;
        private const int ContainerHeaderBytes = 4;
        private const int AlignmentBytes = 3; // at most three bytes before an aligned value
        // Admin rounds ordinary values to three decimal places in 0..999999999.
        // Four base-10000 digits (8), long Numeric header + varlena (8), alignment
        // (3) fit in 20. Positive Int32 rowNumber fits the same reserve.
        private const int RoundedNumericBytes = 20;
        // A CLR decimal snapshot has <=29 significant digits. Splitting its
        // integer/fractional groups takes <=9 base-10000 digits (18); headers (8)
        // and alignment (3) fit in a 32-byte reserve, including null snapshots.
        private const int SnapshotNumericBytes = 32;
        // All 13 normalized ordinary keys are present, including absent nulls.
        // Their UTF-8 key lengths sum to 135; every key/value has its own JEntry.
        private const int OrdinaryObjectBytes = ContainerHeaderBytes + 26 * EntryBytes + 135 + 4 * RoundedNumericBytes;

        internal static long MeasureOrdinary(PosCatalogImportItemRequest[] items) =>
            ArrayRootBytes + (items ?? Array.Empty<PosCatalogImportItemRequest>()).Sum(OrdinaryArrayItemBytes);

        internal static long OrdinaryArrayItemBytes(PosCatalogImportItemRequest item)
        {
            var itemNumber = Utf8(item.ItemNumber);
            var secondName = DisplayBytes(item.SecondProductName, 240);
            // Admin fills an empty name from secondProductName or itemNumber.
            // Reserve the largest possible selected value without changing it.
            var productName = Math.Max(DisplayBytes(item.ProductName, 240), Math.Max(secondName, itemNumber));
            return EntryBytes + AlignmentBytes + OrdinaryObjectBytes + Utf8(item.Barcode) +
                DisplayBytes(item.Category, 160) + 9 /* longest normalized changeKind: no_change */ +
                Utf8(item.ClientItemId) + Utf8(item.DiffSummary) + itemNumber + productName + secondName +
                DisplayBytes(item.Supplier, 160);
        }

        private static int Utf8(string text) => Encoding.UTF8.GetByteCount(text ?? string.Empty);

        private static int DisplayBytes(string text, int maximumUtf16Length)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // ASCII normalization can only remove/replace bytes. NFC can expand
            // non-ASCII input; any accepted result is capped by Admin's UTF-16
            // limit and needs at most three UTF-8 bytes per code unit. This also
            // covers supplementary characters without relying on the OS/V8
            // Unicode table versions or altering the immutable raw text.
            return text.Any(character => character > 127) ? maximumUtf16Length * 3 : Utf8(text);
        }

        internal static long MeasureCorrection(PosCatalogImportCorrectionItem[] items) =>
            ArrayRootBytes + (items ?? Array.Empty<PosCatalogImportCorrectionItem>()).Sum(CorrectionArrayItemBytes);

        private static long CorrectionArrayItemBytes(PosCatalogImportCorrectionItem item)
        {
            // Six outer keys total 67 UTF-8 bytes; normalized nested maps retain
            // exactly the selected keys, and fieldMask is an array of strings.
            var mask = item.FieldMask ?? Array.Empty<string>();
            var snapshots = item.BaseSnapshot;
            var snapshotBytes = ContainerHeaderBytes + AlignmentBytes + (snapshots == null ? 0 :
                snapshots.Sum(pair => 2 * EntryBytes + Utf8(pair.Key) + SnapshotNumericBytes));
            var changesBytes = ContainerHeaderBytes + AlignmentBytes +
                mask.Sum(field => 2 * EntryBytes + Utf8(field) + RoundedNumericBytes);
            var maskBytes = ContainerHeaderBytes + AlignmentBytes + mask.Sum(field => EntryBytes + Utf8(field));
            return EntryBytes + AlignmentBytes + ContainerHeaderBytes + 12 * EntryBytes + 67 +
                Utf8(item.ClientItemId) + Utf8(item.RemoteProductId) + Utf8(item.BaseRevision) +
                snapshotBytes + changesBytes + maskBytes;
        }
    }
}
