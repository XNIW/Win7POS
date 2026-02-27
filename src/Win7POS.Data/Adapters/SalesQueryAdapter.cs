using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Reports;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class SalesQueryAdapter : IDateRangeSalesQuery
    {
        private readonly SaleRepository _sales;

        public SalesQueryAdapter(SaleRepository sales)
        {
            _sales = sales;
        }

        public Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(long fromMs, long toMs)
            => _sales.GetSalesBetweenAsync(fromMs, toMs);
    }
}
