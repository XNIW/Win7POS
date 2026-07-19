using System;
using System.IO;
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
        public const long MaximumRunBytes = 512L * 1024L * 1024L;
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
            int pageNumber,
            PosCatalogPullResponse response,
            long currentRunBytes)
        {
            ValidateGeneration(generationId);
            if (pageNumber <= 0) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (currentRunBytes < 0) throw new ArgumentOutOfRangeException(nameof(currentRunBytes));

            var payload = Serialize(response);
            if (payload.LongLength > MaximumPageBytes)
            {
                throw new InvalidOperationException("catalog_full_stage_page_too_large");
            }

            var nextRunBytes = checked(currentRunBytes + payload.LongLength);
            if (nextRunBytes > MaximumRunBytes)
            {
                throw new InvalidOperationException("catalog_full_stage_run_too_large");
            }

            using (var conn = _factory.Open())
            {
                var rows = await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value)
VALUES(@key, @payload)
ON CONFLICT(key) DO NOTHING;",
                    new
                    {
                        key = PageKey(generationId, pageNumber),
                        payload
                    }).ConfigureAwait(false);
                if (rows != 1)
                {
                    throw new InvalidOperationException("catalog_full_stage_page_duplicate");
                }
            }

            return nextRunBytes;
        }

        public async Task<PosCatalogPullResponse> LoadPageAsync(
            string generationId,
            int pageNumber)
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
            var serializer = new DataContractJsonSerializer(typeof(PosCatalogPullResponse));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, response);
                return stream.ToArray();
            }
        }

        private static PosCatalogPullResponse Deserialize(byte[] payload)
        {
            var serializer = new DataContractJsonSerializer(typeof(PosCatalogPullResponse));
            using (var stream = new MemoryStream(payload, writable: false))
            {
                var response = serializer.ReadObject(stream) as PosCatalogPullResponse;
                if (response == null)
                {
                    throw new InvalidOperationException("catalog_full_stage_page_invalid");
                }

                return response;
            }
        }

        private static string RunKey(string generationId)
        {
            return Prefix + generationId + ".run";
        }

        private static string PageKey(string generationId, int pageNumber)
        {
            return Prefix + generationId + ".page." + pageNumber.ToString("D6");
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
}
