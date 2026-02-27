using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;

namespace Win7POS.Core.Reports
{
    public interface IDateRangeSalesQuery
    {
        Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(long fromMs, long toMs);
    }
}
