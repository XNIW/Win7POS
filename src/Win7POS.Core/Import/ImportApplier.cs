using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;

namespace Win7POS.Core.Import
{
    public sealed class ImportApplier
    {
        private readonly IProductUpserter _upserter;
        private readonly IProductSnapshotLookup _lookup;

        public ImportApplier(IProductUpserter upserter, IProductSnapshotLookup lookup)
        {
            _upserter = upserter ?? throw new ArgumentNullException(nameof(upserter));
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        }

        public async Task<ImportApplyResult> ApplyAsync(IEnumerable<ImportRow> rows, ImportApplyOptions options)
        {
            var result = new ImportApplyResult();
            if (rows == null) return result;
            options = options ?? new ImportApplyOptions();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null)
                {
                    result.Skipped += 1;
                    continue;
                }

                var barcode = (row.Barcode ?? string.Empty).Trim();
                if (barcode.Length == 0 || row.UnitPrice < 0)
                {
                    result.Skipped += 1;
                    continue;
                }

                if (!seen.Add(barcode))
                {
                    result.Skipped += 1;
                    continue;
                }

                try
                {
                    var existing = await _lookup.GetByBarcodeAsync(barcode);
                    var kind = ImportDiffer.ResolveKind(existing, row);
                    if (existing == null)
                    {
                        if (!options.InsertNew)
                        {
                            result.Skipped += 1;
                            continue;
                        }

                        if (options.DryRun)
                        {
                            result.AppliedInserted += 1;
                            result.ChangedBarcodes.Add(barcode);
                            continue;
                        }

                        var insertedOutcome = await _upserter.UpsertAsync(new Product
                        {
                            Barcode = barcode,
                            Name = row.Name ?? string.Empty,
                            UnitPrice = row.UnitPrice
                        });
                        if (insertedOutcome == UpsertOutcome.Inserted) result.AppliedInserted += 1;
                        else result.AppliedUpdated += 1;
                        result.ChangedBarcodes.Add(barcode);
                        continue;
                    }

                    var nextName = options.UpdateName ? (row.Name ?? string.Empty) : (existing.Name ?? string.Empty);
                    var nextPrice = options.UpdatePrice ? row.UnitPrice : existing.UnitPrice;
                    if (nextName == (existing.Name ?? string.Empty) && nextPrice == existing.UnitPrice)
                    {
                        result.NoChange += 1;
                        continue;
                    }

                    if (kind == ImportDiffKind.UpdateName && !options.UpdateName) { result.NoChange += 1; continue; }
                    if (kind == ImportDiffKind.UpdatePrice && !options.UpdatePrice) { result.NoChange += 1; continue; }
                    if (kind == ImportDiffKind.UpdateBoth && !options.UpdateName && !options.UpdatePrice) { result.NoChange += 1; continue; }

                    if (options.DryRun)
                    {
                        result.AppliedUpdated += 1;
                        result.ChangedBarcodes.Add(barcode);
                        continue;
                    }

                    await _upserter.UpsertAsync(new Product
                    {
                        Barcode = barcode,
                        Name = nextName,
                        UnitPrice = nextPrice
                    });
                    result.AppliedUpdated += 1;
                    result.ChangedBarcodes.Add(barcode);
                }
                catch
                {
                    result.ErrorsCount += 1;
                }
            }

            return result;
        }
    }
}
