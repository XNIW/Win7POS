using System.Threading.Tasks;
using Win7POS.Core.Models;

namespace Win7POS.Core.Import
{
    public interface IProductUpserter
    {
        Task<UpsertOutcome> UpsertAsync(Product product);
    }
}
