using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;

namespace Win7POS.Core.Import
{
    public sealed class ImportApplier
    {
        private readonly IProductUpserter _upserter;

        public ImportApplier(IProductUpserter upserter)
        {
            _upserter = upserter ?? throw new ArgumentNullException(nameof(upserter));
        }

        public async Task<ImportApplyResult> ApplyAsync(IEnumerable<ImportRow> rows)
        {
            var result = new ImportApplyResult();
            if (rows == null) return result;

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
                    var outcome = await _upserter.UpsertAsync(new Product
                    {
                        Barcode = barcode,
                        Name = row.Name ?? string.Empty,
                        UnitPrice = row.UnitPrice
                    });

                    if (outcome == UpsertOutcome.Inserted) result.Inserted += 1;
                    else result.Updated += 1;
                    result.AppliedBarcodes.Add(barcode);
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
