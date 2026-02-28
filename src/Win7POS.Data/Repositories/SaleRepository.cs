using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
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

        public async Task<DailySalesSummary> GetDailySummaryAsync(DateTime date)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            using var conn = _factory.Open();
            var row = await conn.QuerySingleAsync<DailySalesSummary>(@"
SELECT
  COUNT(1) AS SalesCount,
  COALESCE(SUM(total), 0) AS TotalAmount,
  COALESCE(SUM(paidCash), 0) AS CashAmount,
  COALESCE(SUM(paidCard), 0) AS CardAmount
FROM sales
WHERE createdAt >= @from AND createdAt < @to",
                new { from, to });
            row.Date = date.Date;
            return row;
        }

        public Task<IReadOnlyList<Sale>> GetSalesForDateAsync(DateTime date)
        {
            var from = new DateTimeOffset(date.Date).ToUnixTimeMilliseconds();
            var to = new DateTimeOffset(date.Date.AddDays(1)).ToUnixTimeMilliseconds();
            return GetSalesBetweenAsync(from, to);
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

    public sealed class DailySalesSummary
    {
        public DateTime Date { get; set; }
        public int SalesCount { get; set; }
        public int TotalAmount { get; set; }
        public int CashAmount { get; set; }
        public int CardAmount { get; set; }
    }
}
