using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Images;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    /// <summary>
    /// Persists only an opaque hash of the last trusted server cache scope.
    /// This lets offline reads use the last authorized partition without
    /// storing signed URLs, tokens, or the raw server scope.
    /// </summary>
    public sealed class ProductImageCacheScopeStore
    {
        private const string KeyPrefix = "pos.product_image.cache_scope.v1.";
        private const string ActiveBindingKey = KeyPrefix + "active.binding";
        private const string ActiveAccountScopeKey = KeyPrefix + "active.account";
        private const string PendingPurgeKey = KeyPrefix + "active.purge_pending";
        private readonly SqliteConnectionFactory _factory;

        public ProductImageCacheScopeStore(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<string> ResolveAsync(
            string staffId,
            string shopId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            {
                var stored = await connection.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = @key LIMIT 1;",
                    new { key = Key(staffId, shopId) }).ConfigureAwait(false);
                return IsLowerHex64(stored)
                    ? stored
                    : null;
            }
        }

        public async Task<string> ResolveActiveAsync(
            string staffId,
            string shopId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            {
                var stored = await connection.ExecuteScalarAsync<string>(@"
SELECT scope.value
FROM app_settings scope
JOIN app_settings active_binding
  ON active_binding.key = @activeBindingKey
 AND active_binding.value = @binding
JOIN app_settings active_account
  ON active_account.key = @activeAccountScopeKey
 AND active_account.value = scope.value
WHERE scope.key = @scopeKey
  AND NOT EXISTS (
    SELECT 1 FROM app_settings pending WHERE pending.key = @pendingPurgeKey
  )
LIMIT 1;",
                    new
                    {
                        activeBindingKey = ActiveBindingKey,
                        binding = Binding(staffId, shopId),
                        activeAccountScopeKey = ActiveAccountScopeKey,
                        scopeKey = Key(staffId, shopId),
                        pendingPurgeKey = PendingPurgeKey
                    }).ConfigureAwait(false);
                return IsLowerHex64(stored) ? stored : null;
            }
        }

        public async Task<string> BindAsync(
            string staffId,
            string shopId,
            string cacheScope,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var transition = await BindWithTransitionAsync(
                staffId,
                shopId,
                cacheScope,
                cancellationToken).ConfigureAwait(false);
            return transition.AccountScope;
        }

        public async Task<ProductImageCacheScopeBinding> BindWithTransitionAsync(
            string staffId,
            string shopId,
            string cacheScope,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!PosProductImageContractV1.IsCacheScope(cacheScope))
                throw new ArgumentException("product_image_cache_scope_invalid", nameof(cacheScope));
            cancellationToken.ThrowIfCancellationRequested();
            var accountScope = DeriveAccountScope(cacheScope);
            var binding = Binding(staffId, shopId);
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                var activeBinding = await connection.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = @key LIMIT 1;",
                    new { key = ActiveBindingKey },
                    transaction).ConfigureAwait(false);
                var activeAccountScope = await connection.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = @key LIMIT 1;",
                    new { key = ActiveAccountScopeKey },
                    transaction).ConfigureAwait(false);
                var purgeToken = await connection.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = @key LIMIT 1;",
                    new { key = PendingPurgeKey },
                    transaction).ConfigureAwait(false);
                var transitionRequired = !IsLowerHex64(activeBinding) ||
                    !IsLowerHex64(activeAccountScope) ||
                    !string.Equals(activeBinding, binding, StringComparison.Ordinal) ||
                    !string.Equals(activeAccountScope, accountScope, StringComparison.Ordinal);
                if (transitionRequired)
                {
                    purgeToken = PurgeToken(binding, accountScope);
                }
                else if (!IsLowerHex64(purgeToken))
                {
                    purgeToken = null;
                }
                await connection.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@scopeKey, @accountScope)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
INSERT INTO app_settings(key, value) VALUES(@activeBindingKey, @binding)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
INSERT INTO app_settings(key, value) VALUES(@activeAccountScopeKey, @accountScope)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;" +
                    (transitionRequired ? @"
INSERT INTO app_settings(key, value) VALUES(@pendingPurgeKey, @purgeToken)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;" : string.Empty),
                    new
                    {
                        scopeKey = Key(staffId, shopId),
                        accountScope,
                        activeBindingKey = ActiveBindingKey,
                        binding,
                        activeAccountScopeKey = ActiveAccountScopeKey,
                        pendingPurgeKey = PendingPurgeKey,
                        purgeToken
                    },
                    transaction).ConfigureAwait(false);
                transaction.Commit();
                return new ProductImageCacheScopeBinding(
                    accountScope,
                    purgeToken);
            }
        }

        public async Task<string> ObserveTrustedIdentityAsync(
            string staffId,
            string shopId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = Binding(staffId, shopId);
            using (var connection = _factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                var activeBinding = await connection.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = @key LIMIT 1;",
                    new { key = ActiveBindingKey },
                    transaction).ConfigureAwait(false);
                var purgeToken = await connection.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = @key LIMIT 1;",
                    new { key = PendingPurgeKey },
                    transaction).ConfigureAwait(false);
                if (!IsLowerHex64(activeBinding) ||
                    !string.Equals(activeBinding, binding, StringComparison.Ordinal))
                {
                    purgeToken = PurgeToken(binding, string.Empty);
                    await connection.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@activeBindingKey, @binding)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
DELETE FROM app_settings WHERE key = @activeAccountScopeKey;
INSERT INTO app_settings(key, value) VALUES(@pendingPurgeKey, @purgeToken)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                        new
                        {
                            activeBindingKey = ActiveBindingKey,
                            binding,
                            activeAccountScopeKey = ActiveAccountScopeKey,
                            pendingPurgeKey = PendingPurgeKey,
                            purgeToken
                        },
                        transaction).ConfigureAwait(false);
                }
                else if (!IsLowerHex64(purgeToken))
                {
                    purgeToken = null;
                }
                transaction.Commit();
                return purgeToken;
            }
        }

        public async Task<bool> AcknowledgePurgeAsync(
            string staffId,
            string shopId,
            string accountScope,
            string purgeToken,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!IsLowerHex64(purgeToken)) return false;
            if (accountScope != null && !IsLowerHex64(accountScope)) return false;
            cancellationToken.ThrowIfCancellationRequested();
            using (var connection = _factory.Open())
            {
                var deleted = await connection.ExecuteAsync(@"
DELETE FROM app_settings
WHERE key = @pendingPurgeKey
  AND value = @purgeToken
  AND EXISTS (
    SELECT 1 FROM app_settings
    WHERE key = @activeBindingKey AND value = @binding
  )
  AND (
    @accountScope IS NULL OR EXISTS (
      SELECT 1 FROM app_settings
      WHERE key = @activeAccountScopeKey AND value = @accountScope
    )
  );",
                    new
                    {
                        pendingPurgeKey = PendingPurgeKey,
                        purgeToken,
                        activeBindingKey = ActiveBindingKey,
                        binding = Binding(staffId, shopId),
                        accountScope,
                        activeAccountScopeKey = ActiveAccountScopeKey
                    }).ConfigureAwait(false);
                return deleted == 1;
            }
        }

        private static string Key(string staffId, string shopId)
        {
            return KeyPrefix + Binding(staffId, shopId);
        }

        public static string DeriveAccountScope(string cacheScope)
        {
            if (!PosProductImageContractV1.IsCacheScope(cacheScope))
                throw new ArgumentException(
                    "product_image_cache_scope_invalid",
                    nameof(cacheScope));
            return ProductImageHash.Sha256Hex(Encoding.UTF8.GetBytes(
                "win7pos-server-cache-scope-v1\n" + cacheScope));
        }

        private static string Binding(string staffId, string shopId)
        {
            return ProductImageHash.Sha256Hex(Encoding.UTF8.GetBytes(
                "win7pos-cache-binding-v1\n" +
                (staffId ?? string.Empty).ToLowerInvariant() + "\n" +
                (shopId ?? string.Empty).ToLowerInvariant()));
        }

        private static string PurgeToken(string binding, string accountScope)
        {
            return ProductImageHash.Sha256Hex(Encoding.UTF8.GetBytes(
                "win7pos-cache-purge-v1\n" + binding + "\n" +
                (accountScope ?? string.Empty)));
        }

        private static bool IsLowerHex64(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                    return false;
            }
            return true;
        }
    }

    public sealed class ProductImageCacheScopeBinding
    {
        public ProductImageCacheScopeBinding(
            string accountScope,
            string purgeToken)
        {
            AccountScope = accountScope;
            PurgeToken = purgeToken;
        }

        public string AccountScope { get; }

        public string PurgeToken { get; }
    }
}
