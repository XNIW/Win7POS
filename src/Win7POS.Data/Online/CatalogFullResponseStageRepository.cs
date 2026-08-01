using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    /// <summary>
    /// Bounded, crash-safe scratch storage for a full catalog response chain. The
    /// stage lives in app_settings so older databases need no schema mutation; it
    /// is never authoritative and is deleted before a new generation starts.
    /// </summary>
    public sealed class CatalogFullResponseStageRepository
    {
        public const int MaximumPageBytes = 8 * 1024 * 1024;
        public const long MinimumFreeDiskReserveBytes = 256L * 1024L * 1024L;
        private const string Prefix = "pos.catalog.full_stage.";

        private readonly SqliteConnectionFactory _factory;

        public CatalogFullResponseStageRepository(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task BeginAsync(string generationId)
        {
            ValidateGeneration(generationId);
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                await conn.ExecuteAsync(
                    "DELETE FROM app_settings WHERE key GLOB @pattern;",
                    new { pattern = Prefix + "*" },
                    tx).ConfigureAwait(false);
                await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value);",
                    new
                    {
                        key = RunKey(generationId),
                        value = DateTimeOffset.UtcNow.ToString("O")
                    },
                    tx).ConfigureAwait(false);
                tx.Commit();
            }
        }

        public bool TryCreateResourceBudget(
            out CatalogFullResponseStageResourceBudget budget,
            out long availableBytes,
            out long requiredBytes)
        {
            requiredBytes = checked(
                MinimumFreeDiskReserveBytes + (2L * MaximumPageBytes));
            try
            {
                if (!TryReadAvailableBytes(out availableBytes))
                {
                    budget = null;
                    return false;
                }

                if (!TryCalculateMaximumStagedBytes(
                    availableBytes,
                    out var maximumStagedBytes))
                {
                    budget = null;
                    return false;
                }

                budget = new CatalogFullResponseStageResourceBudget(
                    availableBytes,
                    MinimumFreeDiskReserveBytes,
                    maximumStagedBytes);
                return true;
            }
            catch
            {
                budget = null;
                availableBytes = 0;
                return false;
            }
        }

        public static bool TryCalculateMaximumStagedBytes(
            long availableBytes,
            out long maximumStagedBytes)
        {
            var required = checked(
                MinimumFreeDiskReserveBytes + (2L * MaximumPageBytes));
            if (availableBytes < required)
            {
                maximumStagedBytes = 0;
                return false;
            }

            // The scratch blob and SQLite rollback journal can coexist. Deriving
            // half of post-reserve free space at run start avoids a historical
            // catalog-size constant while preserving the disk reserve.
            maximumStagedBytes =
                (availableBytes - MinimumFreeDiskReserveBytes) / 2L;
            return maximumStagedBytes >= MaximumPageBytes;
        }

        public async Task ClearAllAsync()
        {
            using (var conn = _factory.Open())
            {
                await conn.ExecuteAsync(
                    "DELETE FROM app_settings WHERE key GLOB @pattern;",
                    new { pattern = Prefix + "*" }).ConfigureAwait(false);
            }
        }

        public async Task<long> AppendAsync(
            string generationId,
            long pageNumber,
            string cursorFingerprint,
            PosCatalogPullResponse response,
            long currentRunBytes,
            CatalogFullResponseStageResourceBudget budget)
        {
            ValidateGeneration(generationId);
            if (pageNumber <= 0) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            if (string.IsNullOrWhiteSpace(cursorFingerprint) ||
                cursorFingerprint.Length != 64)
            {
                throw new ArgumentException(
                    "A canonical cursor fingerprint is required.",
                    nameof(cursorFingerprint));
            }
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (currentRunBytes < 0) throw new ArgumentOutOfRangeException(nameof(currentRunBytes));
            if (budget == null) throw new ArgumentNullException(nameof(budget));

            var payload = Serialize(response);
            if (payload.LongLength > MaximumPageBytes)
            {
                throw new InvalidOperationException("catalog_full_stage_page_too_large");
            }

            long nextRunBytes;
            try
            {
                nextRunBytes = checked(currentRunBytes + payload.LongLength);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException(
                    CatalogAuthoritativeDrainBudgetPolicy.NumericOverflowCode);
            }

            if (nextRunBytes > budget.MaximumStagedBytes)
            {
                throw new InvalidOperationException(
                    CatalogAuthoritativeDrainBudgetPolicy.StageByteBudgetExceededCode);
            }

            if (!TryReadAvailableBytes(out var availableBytes) ||
                availableBytes <
                checked(budget.MinimumFreeDiskReserveBytes + (2L * payload.LongLength)))
            {
                throw new InvalidOperationException(
                    CatalogAuthoritativeDrainBudgetPolicy.InsufficientDiskCode);
            }

            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var cursorRows = await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @value)
ON CONFLICT(key) DO NOTHING;",
                    new
                    {
                        key = CursorKey(generationId, cursorFingerprint),
                        value = pageNumber.ToString("D20")
                    },
                    tx).ConfigureAwait(false);
                if (cursorRows != 1)
                {
                    tx.Rollback();
                    throw new InvalidOperationException(
                        CatalogAuthoritativeDrainBudgetPolicy.CursorRepeatedCode);
                }

                var pageRows = await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @payload)
ON CONFLICT(key) DO NOTHING;",
                    new
                    {
                        key = PageKey(generationId, pageNumber),
                        payload
                    },
                    tx).ConfigureAwait(false);
                if (pageRows != 1)
                {
                    tx.Rollback();
                    throw new InvalidOperationException("catalog_full_stage_page_duplicate");
                }

                tx.Commit();
            }

            return nextRunBytes;
        }

        public async Task<PosCatalogPullResponse> LoadPageAsync(
            string generationId,
            long pageNumber)
        {
            ValidateGeneration(generationId);
            if (pageNumber <= 0) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            using (var conn = _factory.Open())
            {
                var payload = await conn.ExecuteScalarAsync<byte[]>(
                    "SELECT value FROM app_settings WHERE key = @key;",
                    new { key = PageKey(generationId, pageNumber) }).ConfigureAwait(false);
                if (payload == null || payload.Length == 0 || payload.Length > MaximumPageBytes)
                {
                    throw new InvalidOperationException("catalog_full_stage_page_missing");
                }

                return Deserialize(payload);
            }
        }

        public async Task ClearAsync(string generationId)
        {
            ValidateGeneration(generationId);
            using (var conn = _factory.Open())
            {
                await conn.ExecuteAsync(
                    "DELETE FROM app_settings WHERE key GLOB @pattern;",
                    new { pattern = Prefix + generationId + ".*" }).ConfigureAwait(false);
            }
        }

        private static byte[] Serialize(PosCatalogPullResponse response)
        {
            var products = response?.Catalog?.Products ??
                Array.Empty<PosCatalogProductResponse>();
            var presence = new char[products.Length];
            for (var index = 0; index < products.Length; index++)
            {
                var product = products[index];
                var bits = product == null
                    ? 0
                    : (product.PrimaryImageVersionIdPresent ? 1 : 0) |
                      (product.PrimaryImageUpdatedAtPresent ? 2 : 0);
                presence[index] = (char)('0' + bits);
            }
            var payload = new StagedPagePayload
            {
                ImagePresence = new string(presence),
                Response = response
            };
            var serializer = new DataContractJsonSerializer(typeof(StagedPagePayload));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, payload);
                return stream.ToArray();
            }
        }

        private static PosCatalogPullResponse Deserialize(byte[] payload)
        {
            var serializer = new DataContractJsonSerializer(typeof(StagedPagePayload));
            using (var stream = new MemoryStream(payload, writable: false))
            {
                var staged = serializer.ReadObject(stream) as StagedPagePayload;
                var response = staged?.Response;
                var products = response?.Catalog?.Products ??
                    Array.Empty<PosCatalogProductResponse>();
                if (response == null || staged.ImagePresence == null ||
                    staged.ImagePresence.Length != products.Length)
                {
                    throw new InvalidOperationException("catalog_full_stage_page_invalid");
                }
                for (var index = 0; index < products.Length; index++)
                {
                    var bits = staged.ImagePresence[index] - '0';
                    if (products[index] == null || bits < 0 || bits > 3)
                        throw new InvalidOperationException("catalog_full_stage_page_invalid");
                    products[index] = RestoreProductImageFieldPresence(
                        products[index], bits);
                }
                return response;
            }
        }

        private static PosCatalogProductResponse RestoreProductImageFieldPresence(
            PosCatalogProductResponse source,
            int bits)
        {
            var restored = new PosCatalogProductResponse
            {
                Barcode = source.Barcode,
                CategoryId = source.CategoryId,
                ItemNumber = source.ItemNumber,
                ProductId = source.ProductId,
                ProductName = source.ProductName,
                PurchasePrice = source.PurchasePrice,
                RetailPrice = source.RetailPrice,
                SecondProductName = source.SecondProductName,
                StockQuantity = source.StockQuantity,
                SupplierId = source.SupplierId,
                UpdatedAt = source.UpdatedAt
            };
            if ((bits & 1) != 0)
                restored.PrimaryImageVersionId = source.PrimaryImageVersionId;
            if ((bits & 2) != 0)
                restored.PrimaryImageUpdatedAt = source.PrimaryImageUpdatedAt;
            return restored;
        }

        [DataContract]
        private sealed class StagedPagePayload
        {
            [DataMember(Name = "response", Order = 1)]
            public PosCatalogPullResponse Response { get; set; }

            [DataMember(Name = "imagePresence", Order = 2)]
            public string ImagePresence { get; set; }
        }

        private static string RunKey(string generationId)
        {
            return Prefix + generationId + ".run";
        }

        internal static async Task<PosCatalogPullResponse> LoadPageAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string generationId,
            long pageNumber)
        {
            if (conn == null) throw new ArgumentNullException(nameof(conn));
            ValidateGeneration(generationId);
            if (pageNumber <= 0) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            var payload = await conn.ExecuteScalarAsync<byte[]>(
                "SELECT value FROM app_settings WHERE key = @key;",
                new { key = PageKey(generationId, pageNumber) },
                tx).ConfigureAwait(false);
            if (payload == null || payload.Length == 0 || payload.Length > MaximumPageBytes)
            {
                throw new InvalidOperationException("catalog_full_stage_page_missing");
            }

            return Deserialize(payload);
        }

        private bool TryReadAvailableBytes(out long availableBytes)
        {
            try
            {
                var fullPath = Path.GetFullPath(_factory.DbPath);
                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    availableBytes = 0;
                    return false;
                }

                availableBytes = new DriveInfo(root).AvailableFreeSpace;
                return availableBytes >= 0;
            }
            catch
            {
                availableBytes = 0;
                return false;
            }
        }

        private static string CursorKey(
            string generationId,
            string cursorFingerprint)
        {
            return Prefix + generationId + ".cursor." + cursorFingerprint;
        }

        private static string PageKey(string generationId, long pageNumber)
        {
            return Prefix + generationId + ".page." + pageNumber.ToString("D20");
        }

        private static void ValidateGeneration(string generationId)
        {
            if (string.IsNullOrWhiteSpace(generationId) ||
                generationId.Length != 32 ||
                !Guid.TryParseExact(generationId, "N", out _))
            {
                throw new ArgumentException("A canonical full-stage generation is required.", nameof(generationId));
            }
        }
    }

    public sealed class CatalogFullResponseStageResourceBudget
    {
        internal CatalogFullResponseStageResourceBudget(
            long availableBytesAtStart,
            long minimumFreeDiskReserveBytes,
            long maximumStagedBytes)
        {
            AvailableBytesAtStart = availableBytesAtStart;
            MinimumFreeDiskReserveBytes = minimumFreeDiskReserveBytes;
            MaximumStagedBytes = maximumStagedBytes;
        }

        public long AvailableBytesAtStart { get; }
        public long MaximumStagedBytes { get; }
        public long MinimumFreeDiskReserveBytes { get; }
    }
}
