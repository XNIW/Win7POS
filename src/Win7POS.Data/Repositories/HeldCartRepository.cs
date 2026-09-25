using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Data.Online;
using Win7POS.Core.Models;
using System.IO;
using System.Text;
using System.Runtime.Serialization.Json;

namespace Win7POS.Data.Repositories
{
    public sealed class HeldCartRepository
    {
        private readonly SqliteConnectionFactory _factory;
        private const string ScopePrefix = "pos.held.scope.";
        private const string SalePrefix = "pos.held.sale.";
        private const string ProductsPrefix = "pos.held.products.";

        public HeldCartRepository(SqliteConnectionFactory factory) => _factory = factory;

        public static string NewHoldId(DateTimeOffset now) => "H-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

        public async Task CreateHoldAsync(string holdId, long createdAtMs, long totalMinor, IReadOnlyList<HeldCartLineRow> lines, bool replaceRecovered = false)
        {
            if (string.IsNullOrWhiteSpace(holdId)) throw new ArgumentException("holdId is empty");
            if (lines == null || lines.Count == 0) throw new ArgumentException("lines is empty");

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            try
            {
                if (replaceRecovered)
                {
                    await RequireScopeAsync(conn, tx, holdId).ConfigureAwait(false);
                    var changed = await conn.ExecuteAsync("UPDATE held_carts SET totalMinor=@totalMinor WHERE holdId=@holdId", new { holdId, totalMinor }, tx).ConfigureAwait(false);
                    if (changed != 1) throw new InvalidOperationException("Held cart was already consumed or deleted.");
                    await conn.ExecuteAsync("DELETE FROM held_cart_lines WHERE holdId=@holdId", new { holdId }, tx).ConfigureAwait(false);
                }
                else
                {
                    await conn.ExecuteAsync(
                    "INSERT INTO held_carts(holdId, createdAtMs, totalMinor) VALUES(@holdId, @createdAtMs, @totalMinor)",
                    new { holdId, createdAtMs, totalMinor }, tx).ConfigureAwait(false);
                    await conn.ExecuteAsync("INSERT INTO app_settings(key,value) VALUES(@key,@value)",
                        new { key = ScopePrefix + holdId, value = await CurrentScopeAsync(conn, tx).ConfigureAwait(false) }, tx).ConfigureAwait(false);
                }

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
                        }, tx).ConfigureAwait(false);
                }
                await conn.ExecuteAsync(@"INSERT INTO app_settings(key,value) VALUES(@key,@value)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new { key = ProductsPrefix + holdId, value = SerializeProducts(lines) }, tx).ConfigureAwait(false);
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
            using var tx = conn.BeginTransaction(deferred: true);
            var scope = await CurrentScopeAsync(conn, tx).ConfigureAwait(false);
            var rows = await conn.QueryAsync<HeldCartSummary>(
                @"SELECT h.holdId AS HoldId, h.createdAtMs AS CreatedAtMs, h.totalMinor AS TotalMinor FROM held_carts h
LEFT JOIN app_settings s ON s.key=@prefix || h.holdId
WHERE s.value=@scope OR (s.value IS NULL AND @scope='0::0::1:0:0:')
ORDER BY h.createdAtMs DESC", new { prefix = ScopePrefix, scope }, tx).ConfigureAwait(false);
            tx.Commit();
            return rows.ToList();
        }

        public async Task<IReadOnlyList<HeldCartLineRow>> LoadHoldLinesAsync(string holdId)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: true);
            await RequireScopeAsync(conn, tx, holdId).ConfigureAwait(false);
            var rows = await conn.QueryAsync<HeldCartLineRow>(
                "SELECT barcode AS Barcode, name AS Name, unitPrice AS UnitPrice, qty AS Qty FROM held_cart_lines WHERE holdId = @holdId ORDER BY id",
                new { holdId }, tx).ConfigureAwait(false);
            var metadata = await conn.ExecuteScalarAsync<string>("SELECT value FROM app_settings WHERE key=@key", new { key = ProductsPrefix + holdId }, tx).ConfigureAwait(false);
            var products = string.IsNullOrEmpty(metadata) ? null : DeserializeProducts(metadata);
            var list = rows.ToList();
            foreach (var line in list)
                if (products != null && products.TryGetValue(line.Barcode, out var productId)) line.ProductId = productId;
            tx.Commit();
            return list;
        }

        public async Task DeleteHoldAsync(string holdId)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            await RequireScopeAsync(conn, tx, holdId).ConfigureAwait(false);
            await DeleteWithinTransactionAsync(conn, tx, holdId).ConfigureAwait(false);
            tx.Commit();
        }

        /// <summary>Recovery keeps the original rows durable. All retries share one sale identity.</summary>
        public async Task<string> ClaimAsync(string holdId)
        {
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction();
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM held_carts WHERE holdId=@holdId", new { holdId }, tx).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Held cart was already consumed or deleted.");
            await RequireScopeAsync(conn, tx, holdId).ConfigureAwait(false);
            var key = SalePrefix + holdId;
            await conn.ExecuteAsync("INSERT OR IGNORE INTO app_settings(key,value) VALUES(@key,@value)",
                new { key, value = "V-H-" + Guid.NewGuid().ToString("N") }, tx).ConfigureAwait(false);
            var code = await conn.ExecuteScalarAsync<string>("SELECT value FROM app_settings WHERE key=@key", new { key }, tx).ConfigureAwait(false);
            tx.Commit();
            return code;
        }

        internal static async Task ConsumeWithinTransactionAsync(SqliteConnection conn, SqliteTransaction tx, Sale sale)
        {
            if (string.IsNullOrEmpty(sale.HeldCartId)) return;
            if (sale.Kind != (int)SaleKind.Sale) throw new InvalidOperationException("Only an ordinary sale may consume a held cart.");
            await RequireScopeAsync(conn, tx, sale.HeldCartId).ConfigureAwait(false);
            var code = await conn.ExecuteScalarAsync<string>("SELECT value FROM app_settings WHERE key=@key",
                new { key = SalePrefix + sale.HeldCartId }, tx).ConfigureAwait(false);
            if (!string.Equals(code, sale.Code, StringComparison.Ordinal))
                throw new InvalidOperationException("Held cart claim does not match the sale identity.");
            if (await DeleteWithinTransactionAsync(conn, tx, sale.HeldCartId).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Held cart was already consumed or deleted.");
        }

        private static async Task<int> DeleteWithinTransactionAsync(SqliteConnection conn, SqliteTransaction tx, string holdId)
        {
            var changed = await conn.ExecuteAsync("DELETE FROM held_carts WHERE holdId=@holdId", new { holdId }, tx).ConfigureAwait(false);
            await conn.ExecuteAsync("DELETE FROM app_settings WHERE key IN (@scopeKey,@saleKey,@productsKey)",
                new { scopeKey = ScopePrefix + holdId, saleKey = SalePrefix + holdId, productsKey = ProductsPrefix + holdId }, tx).ConfigureAwait(false);
            return changed;
        }

        private static async Task RequireScopeAsync(SqliteConnection conn, SqliteTransaction tx, string holdId)
        {
            var scope = await conn.ExecuteScalarAsync<string>("SELECT value FROM app_settings WHERE key=@key",
                new { key = ScopePrefix + holdId }, tx).ConfigureAwait(false);
            var current = await CurrentScopeAsync(conn, tx).ConfigureAwait(false);
            // Legacy holds without a binding may only be resumed in an unbound local
            // database that has never transitioned shops. Keep other legacy rows for review.
            if (scope == null && current == "0::0::1:0:0:") return;
            if (!string.Equals(scope, current, StringComparison.Ordinal))
                throw new InvalidOperationException("Held cart belongs to another shop or database generation; review it before recovery.");
        }

        private static async Task<string> CurrentScopeAsync(SqliteConnection conn, SqliteTransaction tx)
        {
            var values = new List<string>();
            foreach (var key in new[] { CatalogShopStateRepository.BoundShopIdKey, CatalogShopStateRepository.BoundShopCodeKey, CatalogShopStateRepository.TransitionEpochKey, RestoreShopSafetyRepository.RestoreCompletedAtKey })
            {
                var value = await conn.ExecuteScalarAsync<string>("SELECT value FROM app_settings WHERE key=@key", new { key }, tx).ConfigureAwait(false);
                value = value ?? (key == CatalogShopStateRepository.TransitionEpochKey ? "0" : string.Empty);
                values.Add(value.Length + ":" + value);
            }
            return string.Join(":", values);
        }

        private static string SerializeProducts(IReadOnlyList<HeldCartLineRow> lines)
        {
            using var stream = new MemoryStream();
            new DataContractJsonSerializer(typeof(Dictionary<string, long?>)).WriteObject(stream,
                lines.ToDictionary(x => x.Barcode, x => x.ProductId, StringComparer.Ordinal));
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static Dictionary<string, long?> DeserializeProducts(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return (Dictionary<string, long?>)new DataContractJsonSerializer(typeof(Dictionary<string, long?>)).ReadObject(stream);
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
        public long? ProductId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public int Qty { get; set; }
    }
}
