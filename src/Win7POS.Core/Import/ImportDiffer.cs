using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Win7POS.Core.Import
{
    public sealed class ImportDiffer
    {
        private readonly IProductSnapshotLookup _lookup;

        public ImportDiffer(IProductSnapshotLookup lookup)
        {
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        }

        public async Task<ImportDiffResult> DiffAsync(IEnumerable<ImportRow> rows, int previewTake = 20)
        {
            var result = new ImportDiffResult();
            if (rows == null) return result;
            if (previewTake < 0) previewTake = 0;

            foreach (var row in rows)
            {
                var item = new ImportDiffItem();
                if (row != null)
                {
                    item.Barcode = row.Barcode ?? string.Empty;
                    item.IncomingName = row.Name ?? string.Empty;
                    item.IncomingPrice = row.UnitPrice;
                }

                if (row == null || string.IsNullOrWhiteSpace(row.Barcode) || row.UnitPrice < 0)
                {
                    item.Kind = ImportDiffKind.InvalidRow;
                    Count(result.Summary, item.Kind);
                    TryAddItem(result.Items, item, previewTake);
                    continue;
                }

                var barcode = row.Barcode.Trim();
                item.Barcode = barcode;
                var existing = await _lookup.GetByBarcodeAsync(barcode);
                if (existing == null)
                {
                    item.Kind = ImportDiffKind.NewProduct;
                    Count(result.Summary, item.Kind);
                    TryAddItem(result.Items, item, previewTake);
                    continue;
                }

                item.ExistingName = existing.Name;
                item.ExistingPrice = existing.UnitPrice;
                item.Kind = ResolveKind(existing, row);
                Count(result.Summary, item.Kind);
                TryAddItem(result.Items, item, previewTake);
            }

            return result;
        }

        public static ImportDiffKind ResolveKind(ProductSnapshot existing, ImportRow incoming)
        {
            if (existing == null) return ImportDiffKind.NewProduct;
            var sameName = string.Equals(existing.Name ?? string.Empty, incoming.Name ?? string.Empty, StringComparison.Ordinal);
            var samePrice = existing.UnitPrice == incoming.UnitPrice;
            if (sameName && samePrice) return ImportDiffKind.NoChange;
            if (!sameName && !samePrice) return ImportDiffKind.UpdateBoth;
            if (!sameName) return ImportDiffKind.UpdateName;
            return ImportDiffKind.UpdatePrice;
        }

        private static void TryAddItem(List<ImportDiffItem> items, ImportDiffItem item, int previewTake)
        {
            if (items.Count >= previewTake) return;
            items.Add(item);
        }

        private static void Count(ImportDiffSummary summary, ImportDiffKind kind)
        {
            switch (kind)
            {
                case ImportDiffKind.NewProduct: summary.NewProduct += 1; break;
                case ImportDiffKind.UpdatePrice: summary.UpdatePrice += 1; break;
                case ImportDiffKind.UpdateName: summary.UpdateName += 1; break;
                case ImportDiffKind.UpdateBoth: summary.UpdateBoth += 1; break;
                case ImportDiffKind.NoChange: summary.NoChange += 1; break;
                case ImportDiffKind.InvalidRow: summary.InvalidRow += 1; break;
            }
        }
    }
}
