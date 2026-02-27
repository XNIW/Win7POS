using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Models;

namespace Win7POS.Data.Repositories
{
    public sealed class SaleRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public SaleRepository(SqliteConnectionFactory factory) => _factory = factory;

        public async Task<long> InsertSaleAsync(Sale sale, IReadOnlyList<SaleLine> lines)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, total, paidCash, paidCard, change)
VALUES(@Code, @CreatedAt, @Total, @PaidCash, @PaidCard, @Change);
SELECT last_insert_rowid();", sale, tx);

                foreach (var l in lines)
                {
                    l.SaleId = saleId;
                    l.LineTotal = l.Quantity * l.UnitPrice;
                }

                await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@SaleId, @ProductId, @Barcode, @Name, @Quantity, @UnitPrice, @LineTotal);", lines, tx);

                tx.Commit();
                return saleId;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public async Task<IReadOnlyList<Sale>> LastSalesAsync(int take = 5)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Sale>(
                "SELECT id, code, createdAt, total, paidCash, paidCard, change FROM sales ORDER BY id DESC LIMIT @take",
                new { take }
            );
            return rows.ToList();
        }

        public async Task<IReadOnlyList<Sale>> GetSalesBetweenAsync(long fromMs, long toMs)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<Sale>(
                @"SELECT id, code, createdAt, total, paidCash, paidCard, change
                  FROM sales
                  WHERE createdAt >= @fromMs AND createdAt < @toMs
                  ORDER BY createdAt ASC, id ASC",
                new { fromMs, toMs }
            );
            return rows.ToList();
        }

        public async Task<Sale> GetByIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<Sale>(
                "SELECT id, code, createdAt, total, paidCash, paidCard, change FROM sales WHERE id = @saleId",
                new { saleId });
        }

        public async Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync(long saleId)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<SaleLine>(
                @"SELECT id, saleId, productId, barcode, name, quantity, unitPrice, lineTotal
                  FROM sale_lines
                  WHERE saleId = @saleId
                  ORDER BY id ASC",
                new { saleId });
            return rows.ToList();
        }
    }
}
