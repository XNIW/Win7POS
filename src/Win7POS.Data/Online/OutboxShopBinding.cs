using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Online
{
    public sealed class OutboxShopBinding
    {
        public const string OfficialShopCodeKey = "pos.official_shop.shop_code";
        public const string OfficialShopIdKey = "pos.official_shop.shop_id";

        public string ShopCode { get; set; } = string.Empty;
        public string ShopId { get; set; } = string.Empty;

        public static async Task<OutboxShopBinding> ResolveRequiredAsync(
            SqliteConnection conn,
            SqliteTransaction tx)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            if (tx == null) throw new ArgumentNullException(nameof(tx));

            var rows = await conn.QueryAsync<OutboxShopSetting>(@"
SELECT key AS Key, TRIM(value) AS Value
FROM app_settings
WHERE key IN (@ShopCodeKey, @ShopIdKey);",
                new
                {
                    ShopCodeKey = OfficialShopCodeKey,
                    ShopIdKey = OfficialShopIdKey
                },
                tx).ConfigureAwait(false);

            var binding = new OutboxShopBinding();
            var shopCodeCount = 0;
            var shopIdCount = 0;
            foreach (var row in rows)
            {
                if (string.Equals(row.Key, OfficialShopCodeKey, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(row.Value))
                {
                    shopCodeCount++;
                    binding.ShopCode = NormalizeCode(row.Value);
                }
                else if (string.Equals(row.Key, OfficialShopIdKey, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(row.Value))
                {
                    shopIdCount++;
                    binding.ShopId = row.Value.Trim();
                }
            }

            if (shopCodeCount != 1 || shopIdCount > 1 || string.IsNullOrWhiteSpace(binding.ShopCode))
            {
                throw new InvalidOperationException("Official shop binding is required before enqueue.");
            }

            return binding;
        }

        public static string GetMismatchCode(
            string originShopId,
            string originShopCode,
            string trustedShopId,
            string trustedShopCode)
        {
            var originCode = NormalizeCode(originShopCode);
            var trustedCode = NormalizeCode(trustedShopCode);
            var originId = NormalizeId(originShopId);
            var trustedId = NormalizeId(trustedShopId);

            if (originCode.Length == 0)
            {
                return "origin_shop_unbound";
            }

            if (trustedCode.Length == 0)
            {
                return "trusted_shop_unbound";
            }

            if (!string.Equals(originCode, trustedCode, StringComparison.Ordinal) ||
                (originId.Length > 0 &&
                 (trustedId.Length == 0 || !string.Equals(originId, trustedId, StringComparison.OrdinalIgnoreCase))))
            {
                return "origin_shop_mismatch";
            }

            return string.Empty;
        }

        public static string NormalizeCode(string value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string NormalizeId(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class OutboxShopSetting
        {
            public string Key { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }
    }
}
