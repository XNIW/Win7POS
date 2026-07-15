using System;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public sealed class PosShopTransitionGuard
    {
        private static readonly string[] ShopScopedStatusKeys =
        {
            "pos.catalog.last_sync_at",
            "pos.catalog.last_sync_cursor",
            CatalogShopStateRepository.LastSyncModeKey,
            "pos.catalog.last_error",
            "pos.catalog.last_updated_products",
            "pos.catalog.last_tombstones_received",
            "pos.catalog.last_tombstones_applied",
            "pos.catalog.last_has_more",
            "pos.catalog.last_catalog_version",
            CatalogShopStateRepository.BoundShopIdKey,
            CatalogShopStateRepository.BoundShopCodeKey,
            CatalogShopStateRepository.DeltaChainActiveKey,
            CatalogShopStateRepository.DeltaChainCatalogVersionKey,
            CatalogShopStateRepository.DeltaChainCursorFingerprintsKey,
            CatalogShopStateRepository.DeltaChainModeKey,
            CatalogShopStateRepository.DeltaChainSummaryFingerprintKey,
            CatalogShopStateRepository.DeltaChainSummaryPinnedKey,
            "pos.catalog.bootstrap_status",
            "pos.catalog.initial_completed_at",
            "pos.catalog.sale_safe_at",
            "pos.sales_sync.last_error",
            "pos.sales_sync.in_progress",
            "pos.sales_sync.last_success_at",
            SettingsRepository.PosLoginLastShopCodeKey
        };

        private readonly SqliteConnectionFactory _factory;

        public PosShopTransitionGuard(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<PosShopTransitionDecision> EvaluateAsync(
            string trustedShopId,
            string trustedShopCode,
            string destinationShopId,
            string destinationShopCode)
        {
            var destination = ShopIdentity.Create(destinationShopId, destinationShopCode);
            if (!destination.HasId || !destination.HasCode)
            {
                return PosShopTransitionDecision.Block(
                    "invalid_destination_shop",
                    destination.Id,
                    destination.Code,
                    false);
            }

            var snapshot = await new ShopOfficialSnapshotRepository(_factory).GetAsync().ConfigureAwait(false);
            var trusted = ShopIdentity.Create(trustedShopId, trustedShopCode);
            var persisted = ShopIdentity.Create(snapshot?.ShopId, snapshot?.ShopCode);

            if (trusted.HasAny && persisted.HasAny && !AreCompatible(trusted, persisted))
            {
                return PosShopTransitionDecision.Block(
                    "local_shop_identity_conflict",
                    destination.Id,
                    destination.Code,
                    await HasUnresolvedOutboxAsync().ConfigureAwait(false));
            }

            var current = ShopIdentity.Merge(trusted, persisted);
            var hasUnresolvedOutbox = await HasUnresolvedOutboxAsync().ConfigureAwait(false);

            if (!current.HasAny)
            {
                if (hasUnresolvedOutbox)
                {
                    return PosShopTransitionDecision.Block(
                        "shop_switch_blocked_ambiguous_outbox",
                        destination.Id,
                        destination.Code,
                        true);
                }

                return PosShopTransitionDecision.AllowReset(destination.Id, destination.Code);
            }

            if (Matches(current, destination))
            {
                return PosShopTransitionDecision.AllowSameShop(
                    destination.Id,
                    destination.Code,
                    hasUnresolvedOutbox);
            }

            if (hasUnresolvedOutbox)
            {
                return PosShopTransitionDecision.Block(
                    "shop_switch_blocked_unresolved_outbox",
                    destination.Id,
                    destination.Code,
                    true);
            }

            return PosShopTransitionDecision.AllowReset(destination.Id, destination.Code);
        }

        public async Task<bool> IsOfflineShopAuthorizedAsync(
            string trustedShopId,
            string trustedShopCode,
            string requestedShopCode)
        {
            var requestedCode = Normalize(requestedShopCode);
            if (requestedCode.Length == 0)
            {
                return false;
            }

            var snapshot = await new ShopOfficialSnapshotRepository(_factory).GetAsync().ConfigureAwait(false);
            var trusted = ShopIdentity.Create(trustedShopId, trustedShopCode);
            var persisted = ShopIdentity.Create(snapshot?.ShopId, snapshot?.ShopCode);

            if (trusted.HasAny && persisted.HasAny && !AreCompatible(trusted, persisted))
            {
                return false;
            }

            var current = ShopIdentity.Merge(trusted, persisted);
            return current.HasCode && Same(current.Code, requestedCode);
        }

        public async Task ApplyAuthorizedTransitionAsync(PosShopTransitionDecision decision)
        {
            using (await ApplyAuthorizedTransitionAndHoldAsync(decision).ConfigureAwait(false))
            {
            }
        }

        public async Task<IDisposable> ApplyAuthorizedTransitionAndHoldAsync(PosShopTransitionDecision decision)
        {
            if (decision == null) throw new ArgumentNullException(nameof(decision));
            if (!decision.Allowed || !decision.RequiresCatalogReset)
            {
                throw new InvalidOperationException("An authorized shop transition with catalog reset is required.");
            }

            var transitionLease = await new CatalogShopTransitionBarrier(_factory)
                .EnterAsync()
                .ConfigureAwait(false);
            try
            {
                using (var conn = _factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    var unresolved = await conn.ExecuteScalarAsync<long>(@"
SELECT
  (SELECT COUNT(1)
   FROM sales_sync_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'))
  +
  (SELECT COUNT(1)
   FROM catalog_import_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'));",
                        transaction: tx).ConfigureAwait(false);
                    if (unresolved > 0)
                    {
                        throw new InvalidOperationException("Shop transition blocked by unresolved outbox rows.");
                    }

                    var transitionAt = DateTimeOffset.UtcNow.ToString("O");
                    await conn.ExecuteAsync(@"
UPDATE products
SET is_active = 0,
    remote_deleted_at = @TransitionAt
WHERE COALESCE(is_active, 1) = 1;

UPDATE categories
SET is_active = 0,
    remote_deleted_at = @TransitionAt
WHERE COALESCE(is_active, 1) = 1;

UPDATE suppliers
SET is_active = 0,
    remote_deleted_at = @TransitionAt
WHERE COALESCE(is_active, 1) = 1;

DELETE FROM remote_catalog_pending_prices;
DELETE FROM remote_catalog_product_references;

INSERT INTO app_settings(key, value)
VALUES(@TransitionEpochKey, '1')
ON CONFLICT(key) DO UPDATE SET value =
  CAST(CASE
    WHEN CAST(app_settings.value AS INTEGER) < 0 THEN 0
    ELSE CAST(app_settings.value AS INTEGER)
  END + 1 AS TEXT);

UPDATE users
SET is_active = 0,
    updated_at = @UpdatedAt
WHERE remote_staff_id IS NOT NULL
  AND (
    (TRIM(COALESCE(remote_shop_id, '')) = '' AND TRIM(COALESCE(remote_shop_code, '')) = '')
    OR (TRIM(COALESCE(remote_shop_id, '')) <> '' AND UPPER(TRIM(remote_shop_id)) <> UPPER(@DestinationShopId))
    OR (TRIM(COALESCE(remote_shop_code, '')) <> '' AND UPPER(TRIM(remote_shop_code)) <> UPPER(@DestinationShopCode))
  );

DELETE FROM app_settings
WHERE key IN @ShopScopedStatusKeys;",
                    new
                    {
                        DestinationShopId = decision.DestinationShopId,
                        DestinationShopCode = decision.DestinationShopCode,
                        ShopScopedStatusKeys,
                        TransitionEpochKey = CatalogShopStateRepository.TransitionEpochKey,
                        TransitionAt = transitionAt,
                        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    },
                        tx).ConfigureAwait(false);

                    tx.Commit();
                }

                return transitionLease;
            }
            catch
            {
                transitionLease.Dispose();
                throw;
            }
        }

        private async Task<bool> HasUnresolvedOutboxAsync()
        {
            using (var conn = _factory.Open())
            {
                var count = await conn.ExecuteScalarAsync<long>(@"
SELECT
  (SELECT COUNT(1)
   FROM sales_sync_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'))
  +
  (SELECT COUNT(1)
   FROM catalog_import_outbox
   WHERE status IN ('pending', 'retry', 'in_progress', 'failed_blocked'));"
                ).ConfigureAwait(false);
                return count > 0;
            }
        }

        private static bool AreCompatible(ShopIdentity left, ShopIdentity right)
        {
            var compared = false;
            if (left.HasId && right.HasId)
            {
                compared = true;
                if (!Same(left.Id, right.Id)) return false;
            }

            if (left.HasCode && right.HasCode)
            {
                compared = true;
                if (!Same(left.Code, right.Code)) return false;
            }

            return compared;
        }

        private static bool Matches(ShopIdentity current, ShopIdentity destination)
        {
            var compared = false;
            if (current.HasId)
            {
                compared = true;
                if (!Same(current.Id, destination.Id)) return false;
            }

            if (current.HasCode)
            {
                compared = true;
                if (!Same(current.Code, destination.Code)) return false;
            }

            return compared;
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class ShopIdentity
        {
            private ShopIdentity(string id, string code)
            {
                Id = Normalize(id);
                Code = Normalize(code);
            }

            public string Code { get; }
            public bool HasAny => HasId || HasCode;
            public bool HasCode => Code.Length > 0;
            public bool HasId => Id.Length > 0;
            public string Id { get; }

            public static ShopIdentity Create(string id, string code)
            {
                return new ShopIdentity(id, code);
            }

            public static ShopIdentity Merge(ShopIdentity primary, ShopIdentity fallback)
            {
                return new ShopIdentity(
                    primary.HasId ? primary.Id : fallback.Id,
                    primary.HasCode ? primary.Code : fallback.Code);
            }
        }
    }

    public sealed class PosShopTransitionDecision
    {
        private PosShopTransitionDecision(
            bool allowed,
            string code,
            ShopIdentityValue destination,
            bool hasUnresolvedOutbox,
            bool requiresCatalogReset)
        {
            Allowed = allowed;
            Code = code ?? string.Empty;
            DestinationShopCode = destination?.Code ?? string.Empty;
            DestinationShopId = destination?.Id ?? string.Empty;
            HasUnresolvedOutbox = hasUnresolvedOutbox;
            RequiresCatalogReset = requiresCatalogReset;
        }

        public bool Allowed { get; }
        public string Code { get; }
        public string DestinationShopCode { get; }
        public string DestinationShopId { get; }
        public bool HasUnresolvedOutbox { get; }
        public bool RequiresCatalogReset { get; }

        internal static PosShopTransitionDecision AllowReset(string destinationShopId, string destinationShopCode)
        {
            return new PosShopTransitionDecision(
                true,
                "shop_transition_authorized",
                ShopIdentityValue.Create(destinationShopId, destinationShopCode),
                false,
                true);
        }

        internal static PosShopTransitionDecision AllowSameShop(
            string destinationShopId,
            string destinationShopCode,
            bool hasUnresolvedOutbox)
        {
            return new PosShopTransitionDecision(
                true,
                "same_shop",
                ShopIdentityValue.Create(destinationShopId, destinationShopCode),
                hasUnresolvedOutbox,
                false);
        }

        internal static PosShopTransitionDecision Block(
            string code,
            string destinationShopId,
            string destinationShopCode,
            bool hasUnresolvedOutbox)
        {
            return new PosShopTransitionDecision(
                false,
                code,
                ShopIdentityValue.Create(destinationShopId, destinationShopCode),
                hasUnresolvedOutbox,
                false);
        }

        private sealed class ShopIdentityValue
        {
            public string Code { get; private set; }
            public string Id { get; private set; }

            public static ShopIdentityValue Create(string id, string code)
            {
                return new ShopIdentityValue
                {
                    Code = (code ?? string.Empty).Trim(),
                    Id = (id ?? string.Empty).Trim()
                };
            }
        }
    }
}
