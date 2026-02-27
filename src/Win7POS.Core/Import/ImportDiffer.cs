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
            var all = new List<ImportDiffItem>();

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
                    all.Add(item);
                    continue;
                }

                var barcode = row.Barcode.Trim();
                item.Barcode = barcode;
                var existing = await _lookup.GetByBarcodeAsync(barcode);
                if (existing == null)
                {
                    item.Kind = ImportDiffKind.NewProduct;
                    Count(result.Summary, item.Kind);
                    all.Add(item);
                    continue;
                }

                item.ExistingName = existing.Name;
                item.ExistingPrice = existing.UnitPrice;
                item.Kind = ResolveKind(existing, row);
                Count(result.Summary, item.Kind);
                all.Add(item);
            }

            all.Sort((a, b) =>
            {
                var k = KindPriority(a.Kind).CompareTo(KindPriority(b.Kind));
                if (k != 0) return k;
                return string.CompareOrdinal(a.Barcode ?? string.Empty, b.Barcode ?? string.Empty);
            });
            for (var i = 0; i < all.Count && i < previewTake; i++)
                result.Items.Add(all[i]);

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

        private static int KindPriority(ImportDiffKind kind)
        {
            switch (kind)
            {
                case ImportDiffKind.NewProduct: return 1;
                case ImportDiffKind.UpdatePrice: return 2;
                case ImportDiffKind.UpdateName: return 3;
                case ImportDiffKind.UpdateBoth: return 4;
                case ImportDiffKind.NoChange: return 5;
                case ImportDiffKind.InvalidRow: return 6;
                default: return 99;
            }
        }
    }
}
