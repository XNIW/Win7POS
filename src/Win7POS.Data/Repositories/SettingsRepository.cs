using System;
using System.Globalization;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    public sealed class SettingsRepository
    {
        public const string PosLoginLastShopCodeKey = "pos.login.last_shop_code";

        private readonly SqliteConnectionFactory _factory;

        public SettingsRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<string> GetStringAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            using var conn = _factory.Open();
            return await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key",
                new { key }).ConfigureAwait(false);
        }

        public async Task SetStringAsync(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty }).ConfigureAwait(false);
        }

        public async Task<bool> SetStringIfGenerationCurrentAsync(
            string key,
            string value,
            OnlineSyncGeneration generation)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: false);
            var permitted = generation != null
                ? await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                    conn,
                    tx,
                    generation).ConfigureAwait(false)
                : await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1 AND active = 1;",
                    transaction: tx).ConfigureAwait(false) == 0;
            if (!permitted)
            {
                tx.Rollback();
                return false;
            }

            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty },
                tx).ConfigureAwait(false);
            tx.Commit();
            return true;
        }

        public async Task<bool?> GetBoolAsync(string key)
        {
            var raw = await GetStringAsync(key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase))
                return false;

            return null;
        }

        public Task SetBoolAsync(string key, bool value)
        {
            return SetStringAsync(key, value ? "1" : "0");
        }

        public Task<bool> SetBoolIfGenerationCurrentAsync(
            string key,
            bool value,
            OnlineSyncGeneration generation)
        {
            return SetStringIfGenerationCurrentAsync(key, value ? "1" : "0", generation);
        }

        public async Task<int?> GetIntAsync(string key)
        {
            var raw = await GetStringAsync(key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                return null;
            return value;
        }

        public Task SetIntAsync(string key, int value)
        {
            return SetStringAsync(key, value.ToString(CultureInfo.InvariantCulture));
        }

        public Task<bool> SetIntIfGenerationCurrentAsync(
            string key,
            int value,
            OnlineSyncGeneration generation)
        {
            return SetStringIfGenerationCurrentAsync(
                key,
                value.ToString(CultureInfo.InvariantCulture),
                generation);
        }

        /// <summary>
        /// Atomically reserves a positive integer that never moves backwards.
        /// If <paramref name="requested"/> is not greater than the stored value,
        /// the next integer is reserved instead. Corrupt or exhausted state fails
        /// closed so callers cannot accidentally reuse an identifier.
        /// </summary>
        public async Task<int> ReserveMonotonicIntAsync(string key, int requested)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            if (requested <= 0) throw new ArgumentOutOfRangeException(nameof(requested));

            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: false);
            try
            {
                var raw = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT value FROM app_settings WHERE key = @key",
                    new { key },
                    tx).ConfigureAwait(false);

                var current = 0;
                if (raw != null &&
                    (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out current) ||
                     current < 0))
                {
                    throw new InvalidOperationException(
                        "The monotonic integer setting is invalid and cannot be reserved safely.");
                }

                int reserved;
                if (requested > current)
                {
                    reserved = requested;
                }
                else
                {
                    if (current == int.MaxValue)
                        throw new InvalidOperationException(
                            "The monotonic integer setting is exhausted and cannot be reserved safely.");
                    reserved = checked(current + 1);
                }

                await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                    new
                    {
                        key,
                        value = reserved.ToString(CultureInfo.InvariantCulture)
                    },
                    tx).ConfigureAwait(false);
                tx.Commit();
                return reserved;
            }
            catch
            {
                try { tx.Rollback(); }
                catch { }
                throw;
            }
        }

        public Task<string> GetLastPosLoginShopCodeAsync()
        {
            return GetStringAsync(PosLoginLastShopCodeKey);
        }

        public Task SetLastPosLoginShopCodeAsync(string shopCode)
        {
            return SetStringAsync(PosLoginLastShopCodeKey, NormalizeShopCode(shopCode));
        }

        private static string NormalizeShopCode(string shopCode)
        {
            return (shopCode ?? string.Empty).Trim().ToUpperInvariant();
        }
    }
}
