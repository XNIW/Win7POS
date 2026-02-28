using System;
using System.Threading.Tasks;
using Dapper;

namespace Win7POS.Data.Repositories
{
    public sealed class SettingsRepository
    {
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
                new { key });
        }

        public async Task SetStringAsync(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is empty");
            using var conn = _factory.Open();
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty });
        }

        public async Task<bool?> GetBoolAsync(string key)
        {
            var raw = await GetStringAsync(key);
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
    }
}
