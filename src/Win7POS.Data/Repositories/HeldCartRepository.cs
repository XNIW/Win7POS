using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Repositories
{
    public sealed class HeldCartRepository
    {
        private readonly SqliteConnectionFactory _factory;

        public HeldCartRepository(SqliteConnectionFactory factory) => _factory = factory;

        public async Task CreateHoldAsync(string holdId, long createdAtMs, long totalMinor, IReadOnlyList<HeldCartLineRow> lines)
        {
            if (string.IsNullOrWhiteSpace(holdId)) throw new ArgumentException("holdId is empty");
            if (lines == null || lines.Count == 0) throw new ArgumentException("lines is empty");

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    "INSERT INTO held_carts(holdId, createdAtMs, totalMinor) VALUES(@holdId, @createdAtMs, @totalMinor)",
                    new { holdId, createdAtMs, totalMinor }, tx);

                foreach (var line in lines)
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO held_cart_lines(holdId, barcode, name, unitPrice, qty) VALUES(@holdId, @barcode, @name, @unitPrice, @qty)",
                        new
                        {
                            holdId,
                            barcode = line.Barcode ?? string.Empty,
                            name = line.Name ?? string.Empty,
                            unitPrice = line.UnitPrice,
                            qty = line.Qty
                        }, tx);
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        public async Task<IReadOnlyList<HeldCartSummary>> ListHoldsAsync()
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<HeldCartSummary>(
                "SELECT holdId AS HoldId, createdAtMs AS CreatedAtMs, totalMinor AS TotalMinor FROM held_carts ORDER BY createdAtMs DESC");
            return rows.ToList();
        }

        public async Task<IReadOnlyList<HeldCartLineRow>> LoadHoldLinesAsync(string holdId)
        {
            using var conn = _factory.Open();
            var rows = await conn.QueryAsync<HeldCartLineRow>(
                "SELECT barcode AS Barcode, name AS Name, unitPrice AS UnitPrice, qty AS Qty FROM held_cart_lines WHERE holdId = @holdId ORDER BY id",
                new { holdId });
            return rows.ToList();
        }

        public async Task DeleteHoldAsync(string holdId)
        {
            using var conn = _factory.Open();
            await conn.ExecuteAsync("DELETE FROM held_carts WHERE holdId = @holdId", new { holdId });
        }
    }

    public sealed class HeldCartSummary
    {
        public string HoldId { get; set; } = string.Empty;
        public long CreatedAtMs { get; set; }
        public long TotalMinor { get; set; }
    }

    public sealed class HeldCartLineRow
    {
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public int Qty { get; set; }
    }
}
