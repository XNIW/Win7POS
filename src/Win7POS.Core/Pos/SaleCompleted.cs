using System.Collections.Generic;
using Win7POS.Core.Models;

namespace Win7POS.Core.Pos
{
    public sealed class SaleCompleted
    {
        public SaleCompleted(Sale sale, IReadOnlyList<SaleLine> lines)
        {
            Sale = sale;
            Lines = lines;
        }

        public Sale Sale { get; }
        public IReadOnlyList<SaleLine> Lines { get; }
    }
}
