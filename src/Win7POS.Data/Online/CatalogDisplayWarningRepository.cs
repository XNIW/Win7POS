using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    /// <summary>
    /// Stores only bounded aggregate display-recovery telemetry. Catalog names,
    /// identifiers, barcodes and payload values never cross this boundary.
    /// </summary>
    public sealed class CatalogDisplayWarningRepository
    {
        public const string ActiveWarningCountKey = "pos.catalog.display_warning.count";
        public const string AcknowledgedRevisionKey = "pos.catalog.display_warning.acknowledged_revision";
        public const string CategoriesAffectedKey = "pos.catalog.display_warning.categories_affected";
        public const string DisplayedRevisionKey = "pos.catalog.display_warning.displayed_revision";
        public const string FallbackCountKey = "pos.catalog.display_warning.fallback_count";
        public const string FirstWarningAtKey = "pos.catalog.display_warning.first_at";
        public const string LastWarningAtKey = "pos.catalog.display_warning.last_at";
        public const string LastWarningRevisionKey = "pos.catalog.display_warning.last_revision";
        public const string NormalizedCountKey = "pos.catalog.display_warning.normalized_count";
        public const string ProductsAffectedKey = "pos.catalog.display_warning.products_affected";
        public const string RemovedControlCountKey = "pos.catalog.display_warning.removed_control_count";
        public const string ReplacementCharacterCountKey = "pos.catalog.display_warning.replacement_character_count";
        public const string RevisionKey = "pos.catalog.display_warning.revision";
        public const string SuppliersAffectedKey = "pos.catalog.display_warning.suppliers_affected";

        private static readonly string[] WarningKeys =
        {
            AcknowledgedRevisionKey,
            ActiveWarningCountKey,
            CategoriesAffectedKey,
            DisplayedRevisionKey,
            FallbackCountKey,
            FirstWarningAtKey,
            LastWarningAtKey,
            LastWarningRevisionKey,
            NormalizedCountKey,
            ProductsAffectedKey,
            RemovedControlCountKey,
            ReplacementCharacterCountKey,
            RevisionKey,
            SuppliersAffectedKey
        };

        private readonly SqliteConnectionFactory _factory;

        public CatalogDisplayWarningRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<CatalogDisplayWarningSnapshot> LoadAsync()
        {
            using var connection = _factory.Open();
            return await LoadAsync(connection, transaction: null).ConfigureAwait(false);
        }

        public async Task StoreSuccessfulSyncAsync(
            CatalogWarningSummary summary,
            string catalogRevision,
            OnlineSyncGeneration generation)
        {
            var normalizedRevision = (catalogRevision ?? string.Empty).Trim();
            var value = summary ?? new CatalogWarningSummary();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            using var connection = _factory.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!await IsWritePermittedAsync(connection, transaction, generation).ConfigureAwait(false))
            {
                transaction.Rollback();
                return;
            }

            var current = await LoadAsync(connection, transaction).ConfigureAwait(false);
            var sameRevision = string.Equals(
                current.Revision,
                normalizedRevision,
                StringComparison.Ordinal);
            // A delta can be clean because it does not resend the earlier recovered
            // row. Keep the advisory for that revision until a later revision proves
            // the catalog clean, otherwise the banner can disappear and reappear.
            var retainExistingWarning = sameRevision &&
                current.WarningCount > 0 &&
                !value.HasWarnings;
            var warningCount = retainExistingWarning ? current.WarningCount : value.WarningCount;
            var productsAffected = retainExistingWarning ? current.ProductsAffected : value.ProductsAffected;
            var categoriesAffected = retainExistingWarning ? current.CategoriesAffected : value.CategoriesAffected;
            var suppliersAffected = retainExistingWarning ? current.SuppliersAffected : value.SuppliersAffected;
            var normalizedCount = retainExistingWarning ? current.NormalizedCount : value.NormalizedCount;
            var removedControlCount = retainExistingWarning ? current.RemovedControlCount : value.RemovedControlCount;
            var replacementCharacterCount = retainExistingWarning
                ? current.ReplacementCharacterCount
                : value.ReplacementCharacterCount;
            var fallbackCount = retainExistingWarning ? current.FallbackCount : value.FallbackCount;
            var hasWarnings = warningCount > 0;
            var firstWarningAt = hasWarnings && sameRevision &&
                !string.IsNullOrWhiteSpace(current.FirstWarningAt)
                ? current.FirstWarningAt
                : hasWarnings ? now : string.Empty;

            if (current.WarningCount > 0 && !string.IsNullOrWhiteSpace(current.Revision) &&
                !sameRevision)
            {
                await UpsertAsync(
                    connection,
                    transaction,
                    LastWarningRevisionKey,
                    current.Revision).ConfigureAwait(false);
            }

            await UpsertAsync(connection, transaction, ActiveWarningCountKey, warningCount.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, ProductsAffectedKey, productsAffected.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, CategoriesAffectedKey, categoriesAffected.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, SuppliersAffectedKey, suppliersAffected.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, NormalizedCountKey, normalizedCount.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, RemovedControlCountKey, removedControlCount.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, ReplacementCharacterCountKey, replacementCharacterCount.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, FallbackCountKey, fallbackCount.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, RevisionKey, normalizedRevision).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, FirstWarningAtKey, firstWarningAt).ConfigureAwait(false);
            await UpsertAsync(connection, transaction, LastWarningAtKey, hasWarnings ? now : string.Empty).ConfigureAwait(false);

            if (!sameRevision)
            {
                await UpsertAsync(connection, transaction, DisplayedRevisionKey, string.Empty).ConfigureAwait(false);
                await UpsertAsync(connection, transaction, AcknowledgedRevisionKey, string.Empty).ConfigureAwait(false);
            }

            transaction.Commit();
        }

        public async Task<bool> TryMarkDisplayedAsync(string revision)
        {
            var normalized = (revision ?? string.Empty).Trim();
            if (normalized.Length == 0) return false;
            using var conn = _factory.Open();
            using var tx = conn.BeginTransaction(deferred: false);
            var warningCount = Parse(await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key;",
                new { key = ActiveWarningCountKey }, tx).ConfigureAwait(false));
            var currentRevision = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key;",
                new { key = RevisionKey }, tx).ConfigureAwait(false);
            var displayedRevision = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM app_settings WHERE key = @key;",
                new { key = DisplayedRevisionKey }, tx).ConfigureAwait(false);
            if (warningCount <= 0 ||
                !string.Equals(currentRevision, normalized, StringComparison.Ordinal) ||
                string.Equals(displayedRevision, normalized, StringComparison.Ordinal))
            {
                tx.Rollback();
                return false;
            }

            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key = DisplayedRevisionKey, value = normalized }, tx).ConfigureAwait(false);
            tx.Commit();
            return true;
        }

        public Task AcknowledgeAsync(string revision)
        {
            return new SettingsRepository(_factory).SetStringAsync(
                AcknowledgedRevisionKey,
                (revision ?? string.Empty).Trim());
        }

        private static async Task<CatalogDisplayWarningSnapshot> LoadAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction)
        {
            var rows = await connection.QueryAsync<SettingRow>(@"
SELECT key AS Key, value AS Value
FROM app_settings
WHERE key IN @keys;",
                new { keys = WarningKeys }, transaction).ConfigureAwait(false);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                values[row.Key ?? string.Empty] = row.Value ?? string.Empty;
            }

            string Get(string key)
            {
                return values.TryGetValue(key, out var value) ? value : string.Empty;
            }

            return new CatalogDisplayWarningSnapshot
            {
                AcknowledgedRevision = Get(AcknowledgedRevisionKey),
                CategoriesAffected = Parse(Get(CategoriesAffectedKey)),
                DisplayedRevision = Get(DisplayedRevisionKey),
                FallbackCount = Parse(Get(FallbackCountKey)),
                FirstWarningAt = Get(FirstWarningAtKey),
                LastWarningAt = Get(LastWarningAtKey),
                NormalizedCount = Parse(Get(NormalizedCountKey)),
                ProductsAffected = Parse(Get(ProductsAffectedKey)),
                RemovedControlCount = Parse(Get(RemovedControlCountKey)),
                ReplacementCharacterCount = Parse(Get(ReplacementCharacterCountKey)),
                Revision = Get(RevisionKey),
                SuppliersAffected = Parse(Get(SuppliersAffectedKey)),
                WarningCount = Parse(Get(ActiveWarningCountKey))
            };
        }

        private static async Task<bool> IsWritePermittedAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            OnlineSyncGeneration generation)
        {
            if (generation != null)
            {
                return await OnlineSyncGenerationRepository.IsCurrentAndActiveAsync(
                    connection,
                    transaction,
                    generation).ConfigureAwait(false);
            }

            return await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM pos_sync_session_generation
WHERE singleton_id = 1 AND active = 1;",
                transaction: transaction).ConfigureAwait(false) == 0;
        }

        private static Task UpsertAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            string key,
            string value)
        {
            return connection.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key, value = value ?? string.Empty }, transaction);
        }

        private static long Parse(string value)
        {
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                ? Math.Max(0, result)
                : 0;
        }

        private sealed class SettingRow
        {
            public string Key { get; set; }
            public string Value { get; set; }
        }
    }

    public sealed class CatalogDisplayWarningSnapshot
    {
        public string AcknowledgedRevision { get; set; } = string.Empty;
        public long CategoriesAffected { get; set; }
        public string DisplayedRevision { get; set; } = string.Empty;
        public long FallbackCount { get; set; }
        public string FirstWarningAt { get; set; } = string.Empty;
        public string LastWarningAt { get; set; } = string.Empty;
        public long NormalizedCount { get; set; }
        public long ProductsAffected { get; set; }
        public long RemovedControlCount { get; set; }
        public long ReplacementCharacterCount { get; set; }
        public string Revision { get; set; } = string.Empty;
        public long SuppliersAffected { get; set; }
        public long WarningCount { get; set; }
    }
}
