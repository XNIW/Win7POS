using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Infrastructure
{
    public sealed class CashierModeService
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 10000;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private readonly SettingsRepository _settings;

        public CashierModeService()
        {
            var opt = PosDbOptions.Default();
            DbInitializer.EnsureCreated(opt);
            _settings = new SettingsRepository(new SqliteConnectionFactory(opt));
        }

        public async Task<bool> GetCashierModeAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _settings.GetBoolAsync(AppSettingKeys.CashierMode).ConfigureAwait(false) ?? false;
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task SetCashierModeAsync(bool enabled)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settings.SetBoolAsync(AppSettingKeys.CashierMode, enabled).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<bool> IsPinEnabledAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _settings.GetBoolAsync(AppSettingKeys.CashierPinEnabled).ConfigureAwait(false) ?? false;
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task SetPinEnabledAsync(bool enabled)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settings.SetBoolAsync(AppSettingKeys.CashierPinEnabled, enabled).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<bool> HasPinAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var raw = await _settings.GetStringAsync(AppSettingKeys.CashierPin).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(raw);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task SetPinAsync(string pin4)
        {
            ValidatePin(pin4);

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var salt = new byte[SaltSize];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(salt);
                }

                var hash = Derive(pin4, salt, Iterations, HashSize);
                var payload = "v1|" + Iterations + "|" + Convert.ToBase64String(salt) + "|" + Convert.ToBase64String(hash);
                await _settings.SetStringAsync(AppSettingKeys.CashierPin, payload).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<bool> VerifyPinAsync(string pin4)
        {
            if (!IsValidPin(pin4)) return false;

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var raw = await _settings.GetStringAsync(AppSettingKeys.CashierPin).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw)) return false;

                var parts = raw.Split('|');
                if (parts.Length != 4) return false;
                if (!string.Equals(parts[0], "v1", StringComparison.Ordinal)) return false;
                if (!int.TryParse(parts[1], out var iter) || iter <= 0) return false;

                byte[] salt;
                byte[] expected;
                try
                {
                    salt = Convert.FromBase64String(parts[2]);
                    expected = Convert.FromBase64String(parts[3]);
                }
                catch
                {
                    return false;
                }

                var actual = Derive(pin4, salt, iter, expected.Length);
                return FixedTimeEquals(expected, actual);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task ClearPinAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settings.SetStringAsync(AppSettingKeys.CashierPin, string.Empty).ConfigureAwait(false);
                await _settings.SetBoolAsync(AppSettingKeys.CashierPinEnabled, false).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static byte[] Derive(string pin, byte[] salt, int iterations, int length)
        {
            using (var kdf = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256))
            {
                return kdf.GetBytes(length);
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static void ValidatePin(string pin4)
        {
            if (!IsValidPin(pin4))
                throw new ArgumentException("PIN must be exactly 4 digits.");
        }

        private static bool IsValidPin(string pin4)
        {
            if (string.IsNullOrWhiteSpace(pin4) || pin4.Length != 4) return false;
            for (var i = 0; i < pin4.Length; i++)
            {
                if (!char.IsDigit(pin4[i])) return false;
            }
            return true;
        }
    }
}
