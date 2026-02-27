using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;

namespace Win7POS.Core.Pos
{
    public interface ISalesStore
    {
        Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines);
        Task<IReadOnlyList<Sale>> LastSalesAsync(int take = 5);
    }
}
