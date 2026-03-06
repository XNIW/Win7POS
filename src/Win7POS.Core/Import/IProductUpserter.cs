using System.Threading.Tasks;
using Win7POS.Core.Models;

namespace Win7POS.Core.Import
{
    public interface IProductUpserter
    {
        Task<UpsertOutcome> UpsertAsync(Product product);
        /// <summary>Upsert prodotto + product_meta da riga import (article_code, name2, purchase_price, supplier_name, category_name, stock).</summary>
        Task<UpsertOutcome> UpsertAsync(ImportRow row);
    }
}
