using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class DataSalesStore : ISalesStore
    {
        private readonly SaleRepository _sales;

        public DataSalesStore(SaleRepository sales) => _sales = sales;

        public Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines) => _sales.InsertSaleAsync(sale, lines);
        public Task<IReadOnlyList<Sale>> LastSalesAsync(int take = 5) => _sales.LastSalesAsync(take);
    }
}
