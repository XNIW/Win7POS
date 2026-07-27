using System;
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

        private readonly SqliteConnectionFactory _factory;

        public CatalogDisplayWarningRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<CatalogDisplayWarningSnapshot> LoadAsync()
        {
            var settings = new SettingsRepository(_factory);
            return new CatalogDisplayWarningSnapshot
            {
                AcknowledgedRevision = await settings.GetStringAsync(AcknowledgedRevisionKey).ConfigureAwait(false),
                CategoriesAffected = Parse(await settings.GetStringAsync(CategoriesAffectedKey).ConfigureAwait(false)),
                DisplayedRevision = await settings.GetStringAsync(DisplayedRevisionKey).ConfigureAwait(false),
                FallbackCount = Parse(await settings.GetStringAsync(FallbackCountKey).ConfigureAwait(false)),
                FirstWarningAt = await settings.GetStringAsync(FirstWarningAtKey).ConfigureAwait(false),
                LastWarningAt = await settings.GetStringAsync(LastWarningAtKey).ConfigureAwait(false),
                NormalizedCount = Parse(await settings.GetStringAsync(NormalizedCountKey).ConfigureAwait(false)),
                ProductsAffected = Parse(await settings.GetStringAsync(ProductsAffectedKey).ConfigureAwait(false)),
                RemovedControlCount = Parse(await settings.GetStringAsync(RemovedControlCountKey).ConfigureAwait(false)),
                ReplacementCharacterCount = Parse(await settings.GetStringAsync(ReplacementCharacterCountKey).ConfigureAwait(false)),
                Revision = await settings.GetStringAsync(RevisionKey).ConfigureAwait(false),
                SuppliersAffected = Parse(await settings.GetStringAsync(SuppliersAffectedKey).ConfigureAwait(false)),
                WarningCount = Parse(await settings.GetStringAsync(ActiveWarningCountKey).ConfigureAwait(false))
            };
        }

        public async Task StoreSuccessfulSyncAsync(
            CatalogWarningSummary summary,
            string catalogRevision,
            OnlineSyncGeneration generation)
        {
            var settings = new SettingsRepository(_factory);
            var normalizedRevision = (catalogRevision ?? string.Empty).Trim();
            var current = await LoadAsync().ConfigureAwait(false);
            var value = summary ?? new CatalogWarningSummary();
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var sameRevision = value.HasWarnings &&
                string.Equals(current.Revision, normalizedRevision, StringComparison.Ordinal);
            var firstWarningAt = value.HasWarnings && sameRevision &&
                !string.IsNullOrWhiteSpace(current.FirstWarningAt)
                ? current.FirstWarningAt
                : value.HasWarnings ? now : string.Empty;

            if (current.WarningCount > 0 && !string.IsNullOrWhiteSpace(current.Revision))
            {
                await SetAsync(settings, LastWarningRevisionKey, current.Revision, generation).ConfigureAwait(false);
            }

            await SetAsync(settings, ActiveWarningCountKey, value.WarningCount, generation).ConfigureAwait(false);
            await SetAsync(settings, ProductsAffectedKey, value.ProductsAffected, generation).ConfigureAwait(false);
            await SetAsync(settings, CategoriesAffectedKey, value.CategoriesAffected, generation).ConfigureAwait(false);
            await SetAsync(settings, SuppliersAffectedKey, value.SuppliersAffected, generation).ConfigureAwait(false);
            await SetAsync(settings, NormalizedCountKey, value.NormalizedCount, generation).ConfigureAwait(false);
            await SetAsync(settings, RemovedControlCountKey, value.RemovedControlCount, generation).ConfigureAwait(false);
            await SetAsync(settings, ReplacementCharacterCountKey, value.ReplacementCharacterCount, generation).ConfigureAwait(false);
            await SetAsync(settings, FallbackCountKey, value.FallbackCount, generation).ConfigureAwait(false);
            await SetAsync(settings, RevisionKey, normalizedRevision, generation).ConfigureAwait(false);
            await SetAsync(settings, FirstWarningAtKey, firstWarningAt, generation).ConfigureAwait(false);
            await SetAsync(settings, LastWarningAtKey, value.HasWarnings ? now : string.Empty, generation).ConfigureAwait(false);

            if (!value.HasWarnings || !sameRevision)
            {
                await SetAsync(settings, DisplayedRevisionKey, string.Empty, generation).ConfigureAwait(false);
                await SetAsync(settings, AcknowledgedRevisionKey, string.Empty, generation).ConfigureAwait(false);
            }
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

        private static Task SetAsync(
            SettingsRepository settings,
            string key,
            int value,
            OnlineSyncGeneration generation)
        {
            return settings.SetIntIfGenerationCurrentAsync(key, Math.Max(0, value), generation);
        }

        private static Task SetAsync(
            SettingsRepository settings,
            string key,
            string value,
            OnlineSyncGeneration generation)
        {
            return settings.SetStringIfGenerationCurrentAsync(key, value ?? string.Empty, generation);
        }

        private static int Parse(string value)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                ? Math.Max(0, result)
                : 0;
        }
    }

    public sealed class CatalogDisplayWarningSnapshot
    {
        public string AcknowledgedRevision { get; set; } = string.Empty;
        public int CategoriesAffected { get; set; }
        public string DisplayedRevision { get; set; } = string.Empty;
        public int FallbackCount { get; set; }
        public string FirstWarningAt { get; set; } = string.Empty;
        public string LastWarningAt { get; set; } = string.Empty;
        public int NormalizedCount { get; set; }
        public int ProductsAffected { get; set; }
        public int RemovedControlCount { get; set; }
        public int ReplacementCharacterCount { get; set; }
        public string Revision { get; set; } = string.Empty;
        public int SuppliersAffected { get; set; }
        public int WarningCount { get; set; }
    }
}
