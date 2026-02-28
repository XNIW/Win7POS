using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Audit;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Infrastructure
{
    public sealed class CashierModeService
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 10000;
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private readonly SettingsRepository _settings;
        private readonly AuditLogRepository _audit = new AuditLogRepository();
        private readonly PosDbOptions _options;

        public CashierModeService()
        {
            _options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(_options);
            _settings = new SettingsRepository(new SqliteConnectionFactory(_options));
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
                var action = enabled ? AuditActions.CashierModeOn : AuditActions.CashierModeOff;
                var details = AuditDetails.Kv(("enabled", enabled ? "true" : "false"));
                await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), action, details).ConfigureAwait(false);
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
                if (!enabled)
                    await ResetPinFailuresNoLockAsync().ConfigureAwait(false);
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
                await ResetPinFailuresNoLockAsync().ConfigureAwait(false);
                await _audit.AppendAsync(
                    _options,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    AuditActions.PinSet,
                    AuditDetails.Kv(("pinEnabled", "true"))).ConfigureAwait(false);
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
                return await VerifyPinNoLockAsync(pin4).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<(bool IsLocked, DateTime? LockUntilUtc, int FailedCount)> GetPinLockStateAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var failedCount = await ReadFailedCountNoLockAsync().ConfigureAwait(false);
                var lockUntil = await ReadLockUntilUtcNoLockAsync().ConfigureAwait(false);
                if (lockUntil.HasValue && DateTime.UtcNow >= lockUntil.Value)
                {
                    await ResetPinFailuresNoLockAsync().ConfigureAwait(false);
                    return (false, null, 0);
                }

                return (lockUntil.HasValue, lockUntil, failedCount);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task ResetPinFailuresAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await ResetPinFailuresNoLockAsync().ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<(bool Ok, bool LockedNow, DateTime? LockUntilUtc, string ErrorMessage)> VerifyPinWithLockoutAsync(string pin4)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var pinEnabled = await _settings.GetBoolAsync(AppSettingKeys.CashierPinEnabled).ConfigureAwait(false) ?? false;
                if (!pinEnabled)
                {
                    return (true, false, null, string.Empty);
                }

                var lockUntil = await ReadLockUntilUtcNoLockAsync().ConfigureAwait(false);
                if (lockUntil.HasValue && DateTime.UtcNow < lockUntil.Value)
                {
                    await AppendPinFailedAttemptNoLockAsync(await ReadFailedCountNoLockAsync().ConfigureAwait(false), true, lockUntil).ConfigureAwait(false);
                    return (false, true, lockUntil, BuildLockedMessage(lockUntil.Value));
                }

                if (lockUntil.HasValue && DateTime.UtcNow >= lockUntil.Value)
                {
                    await ResetPinFailuresNoLockAsync().ConfigureAwait(false);
                }

                var valid = await VerifyPinNoLockAsync(pin4).ConfigureAwait(false);
                if (valid)
                {
                    await ResetPinFailuresNoLockAsync().ConfigureAwait(false);
                    return (true, false, null, string.Empty);
                }

                var failedCount = await ReadFailedCountNoLockAsync().ConfigureAwait(false);
                failedCount += 1;
                if (failedCount >= MaxFailedAttempts)
                {
                    var nextLockUntil = DateTime.UtcNow.Add(LockDuration);
                    await _settings.SetIntAsync(AppSettingKeys.CashierPinFailedCount, failedCount).ConfigureAwait(false);
                    await _settings.SetStringAsync(AppSettingKeys.CashierPinLockUntilUtc, nextLockUntil.ToString("o", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    await AppendPinFailedAttemptNoLockAsync(failedCount, true, nextLockUntil).ConfigureAwait(false);
                    return (false, true, nextLockUntil, BuildLockedMessage(nextLockUntil));
                }

                await _settings.SetIntAsync(AppSettingKeys.CashierPinFailedCount, failedCount).ConfigureAwait(false);
                await _settings.SetStringAsync(AppSettingKeys.CashierPinLockUntilUtc, string.Empty).ConfigureAwait(false);
                var remaining = MaxFailedAttempts - failedCount;
                await AppendPinFailedAttemptNoLockAsync(failedCount, false, null).ConfigureAwait(false);
                return (false, false, null, "PIN errato. Tentativi rimasti: " + remaining);
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
                await ResetPinFailuresNoLockAsync().ConfigureAwait(false);
                await _audit.AppendAsync(
                    _options,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    AuditActions.PinRemove,
                    AuditDetails.Kv(("pinEnabled", "false"))).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private async Task<bool> VerifyPinNoLockAsync(string pin4)
        {
            if (!IsValidPin(pin4)) return false;

            var raw = await _settings.GetStringAsync(AppSettingKeys.CashierPin).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var parts = raw.Split('|');
            if (parts.Length != 4) return false;
            if (!string.Equals(parts[0], "v1", StringComparison.Ordinal)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iter) || iter <= 0) return false;

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

        private async Task<int> ReadFailedCountNoLockAsync()
        {
            return await _settings.GetIntAsync(AppSettingKeys.CashierPinFailedCount).ConfigureAwait(false) ?? 0;
        }

        private async Task<DateTime?> ReadLockUntilUtcNoLockAsync()
        {
            var raw = await _settings.GetStringAsync(AppSettingKeys.CashierPinLockUntilUtc).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
                return null;

            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        private async Task ResetPinFailuresNoLockAsync()
        {
            await _settings.SetIntAsync(AppSettingKeys.CashierPinFailedCount, 0).ConfigureAwait(false);
            await _settings.SetStringAsync(AppSettingKeys.CashierPinLockUntilUtc, string.Empty).ConfigureAwait(false);
        }

        private async Task AppendPinFailedAttemptNoLockAsync(int failedCount, bool lockedNow, DateTime? lockUntilUtc)
        {
            var details = AuditDetails.Kv(
                ("failedCount", failedCount.ToString(CultureInfo.InvariantCulture)),
                ("lockedNow", lockedNow ? "true" : "false"),
                ("lockUntilUtc", lockUntilUtc.HasValue ? lockUntilUtc.Value.ToString("o", CultureInfo.InvariantCulture) : string.Empty));
            await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.PinFailedAttempt, details).ConfigureAwait(false);
        }

        private static string BuildLockedMessage(DateTime lockUntilUtc)
        {
            var local = lockUntilUtc.ToLocalTime();
            return "Troppi tentativi. Riprova alle " + local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ".";
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
