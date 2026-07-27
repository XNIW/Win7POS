using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

internal sealed class CatalogRowInvalidAnalyzerParameters
{
    public string DatabasePath { get; set; } = string.Empty;
    public string EvidenceDirectory { get; set; } = string.Empty;
}

internal static class CatalogRowInvalidAnalyzer
{
    private const string StagePrefix = "pos.catalog.full_stage.";
    private const int HmacHexLength = 24;

    public static bool TryParse(string[] args, out CatalogRowInvalidAnalyzerParameters parameters)
    {
        parameters = new CatalogRowInvalidAnalyzerParameters();
        var requested = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (string.Equals(arg, "--catalog-row-invalid-analyzer", StringComparison.OrdinalIgnoreCase))
            {
                requested = true;
                continue;
            }

            if (string.Equals(arg, "--db", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length) return false;
                parameters.DatabasePath = args[++index];
                continue;
            }

            if (string.Equals(arg, "--evidence-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length) return false;
                parameters.EvidenceDirectory = args[++index];
                continue;
            }

            return false;
        }

        return requested &&
            !string.IsNullOrWhiteSpace(parameters.DatabasePath) &&
            !string.IsNullOrWhiteSpace(parameters.EvidenceDirectory);
    }

    public static void Run(CatalogRowInvalidAnalyzerParameters parameters)
    {
        if (parameters == null) throw new ArgumentNullException(nameof(parameters));
        var databasePath = Path.GetFullPath(parameters.DatabasePath);
        var evidenceDirectory = Path.GetFullPath(parameters.EvidenceDirectory);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Offline catalog database was not found.", databasePath);
        }

        Directory.CreateDirectory(evidenceDirectory);
        var reportPath = Path.Combine(evidenceDirectory, "catalog-row-invalid-analysis.json");
        var keyPath = Path.Combine(evidenceDirectory, "catalog-row-invalid-correlation-key.bin");
        if (File.Exists(reportPath) || File.Exists(keyPath))
        {
            throw new InvalidOperationException(
                "Refusing to overwrite existing catalog-row-invalid analyzer evidence.");
        }

        var correlationKey = new byte[32];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(correlationKey);
        }

        var pages = LoadPages(databasePath);
        var pageResults = new List<PageResult>();
        foreach (var page in pages)
        {
            pageResults.Add(AnalyzePage(page, correlationKey));
        }
        var authoritativeStage = LoadAuthoritativeProductEvidence(databasePath);
        var lastFailureCode = LoadSetting(databasePath, "pos.catalog.last_error");
        var finalPersistedPageHasMore = pageResults
            .OrderBy(page => page.PageNumber)
            .LastOrDefault()?.HasMore == true;
        var failingResponsePersisted = !string.Equals(
            lastFailureCode,
            "catalog_product_row_invalid",
            StringComparison.OrdinalIgnoreCase) || !finalPersistedPageHasMore;

        var invalidRows = pageResults
            .SelectMany(page => page.Products)
            .Where(product => !product.MappedRowValid)
            .ToArray();
        var reasonCounts = invalidRows
            .GroupBy(product => product.Reason, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var firstInvalidPhase = invalidRows
            .Select(product => product.FirstInvalidPhase)
            .OrderBy(PhaseOrder)
            .FirstOrDefault() ?? "none";
        var report = new
        {
            analyzer = "Win7POS.Cli catalog-row-invalid offline analyzer",
            analyzedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            source = new
            {
                database = Path.GetFileName(databasePath),
                mode = "sqlite-read-only",
                rawWirePayloadAvailable = false,
                stagedPayloadDescription =
                    "DataContractJsonSerializer JSON persisted by CatalogFullResponseStageRepository after transport deserialization"
            },
            correlation = new
            {
                algorithm = "HMAC-SHA256",
                truncatedHexCharacters = HmacHexLength,
                keyFile = Path.GetFileName(keyPath)
            },
            fullResponseGenerationCount = pageResults
                .Select(page => page.GenerationHmac)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            pageCount = pageResults.Count,
            productPageCount = pageResults.Count(page => page.ProductCount > 0),
            invalidProductCount = invalidRows.Length,
            invalidProductCountScope = "persisted_staged_pages_only",
            invalidReasonCounts = reasonCounts,
            firstInvalidPhase,
            preStageFailure = new
            {
                code = SafeCode(lastFailureCode),
                failingResponsePersisted,
                explanation = failingResponsePersisted
                    ? "No known pre-stage catalog_product_row_invalid boundary is present in the persisted stage evidence."
                    : "The failing response was rejected before full-response staging and is unavailable in the frozen local evidence."
            },
            authoritativeProductStage = authoritativeStage.Select(row => new
            {
                page = row.PageNumber,
                invalidOccurrenceCount = row.InvalidOccurrenceCount,
                productOccurrenceCount = row.ProductOccurrenceCount
            }),
            pages = pageResults.Select(page => new
            {
                page = page.PageNumber,
                fullResponseGenerationHmac = page.GenerationHmac,
                catalogRevisionHmac = page.CatalogRevisionHmac,
                rawJsonValid = page.RawJsonValid,
                rawJsonShape = page.RawJsonShape,
                dtoValid = page.DtoValid,
                mappedRowsValid = page.Products.All(product => product.MappedRowValid),
                hasMore = page.HasMore,
                productCount = page.ProductCount,
                products = page.Products.Select(product => new
                {
                    lane = "products",
                    row = product.Row,
                    reason = product.Reason,
                    firstInvalidPhase = product.FirstInvalidPhase,
                    dtoValid = product.DtoValid,
                    mappedRowValid = product.MappedRowValid,
                    remoteProductIdHmac = product.RemoteProductIdHmac,
                    barcodeHmac = product.BarcodeHmac,
                    fields = product.Fields,
                    fieldLengths = product.FieldLengths,
                    observedJsonTypes = product.ObservedJsonTypes,
                    priceClass = product.PriceClass
                })
            })
        };

        using (var stream = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(correlationKey, 0, correlationKey.Length);
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine("CATALOG_ROW_INVALID_ANALYSIS=PASS");
        Console.WriteLine("PAGE_COUNT=" + pageResults.Count.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("OBSERVED_STAGED_INVALID_PRODUCT_COUNT=" + invalidRows.Length.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("FAILING_RESPONSE_PERSISTED=" + failingResponsePersisted.ToString().ToLowerInvariant());
        Console.WriteLine("FIRST_INVALID_PHASE=" + firstInvalidPhase);
        Console.WriteLine("REDACTED_REPORT=" + reportPath);
    }

    private static IReadOnlyList<StagedPage> LoadPages(string databasePath)
    {
        var pages = new List<StagedPage>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        using (var command = new SqliteCommand(@"
SELECT key, value
FROM app_settings
WHERE key LIKE @prefix
  AND key LIKE '%.page.%'
ORDER BY key;", connection))
        {
            command.Parameters.AddWithValue("@prefix", StagePrefix + "%");
            connection.Open();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var value = ReadPayload(reader, 1);
                    pages.Add(ParseStagedPage(key, value));
                }
            }
        }

        if (pages.Count == 0)
        {
            throw new InvalidOperationException("No persisted full-response catalog pages were found.");
        }

        return pages;
    }

    private static IReadOnlyList<AuthoritativeProductEvidence> LoadAuthoritativeProductEvidence(
        string databasePath)
    {
        var rows = new List<AuthoritativeProductEvidence>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        using (var command = new SqliteCommand(@"
SELECT page_number,
       COALESCE(SUM(occurrence_count), 0) AS product_occurrence_count,
       COALESCE(SUM(CASE WHEN content_fingerprint = 'invalid' THEN occurrence_count ELSE 0 END), 0)
         AS invalid_occurrence_count
FROM catalog_authoritative_id_stage
WHERE entity_kind = 'product'
GROUP BY page_number
ORDER BY page_number;", connection))
        {
            try
            {
                connection.Open();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(new AuthoritativeProductEvidence
                        {
                            InvalidOccurrenceCount = reader.GetInt64(2),
                            PageNumber = reader.GetInt32(0),
                            ProductOccurrenceCount = reader.GetInt64(1)
                        });
                    }
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
            {
                // Older/failing runs may not have entered the authoritative-stage transaction.
                return Array.Empty<AuthoritativeProductEvidence>();
            }
        }

        return rows;
    }

    private static string LoadSetting(string databasePath, string key)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        using (var command = new SqliteCommand(
            "SELECT value FROM app_settings WHERE key = @key LIMIT 1;", connection))
        {
            command.Parameters.AddWithValue("@key", key);
            connection.Open();
            return command.ExecuteScalar() as string ?? string.Empty;
        }
    }

    private static byte[] ReadPayload(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException("A persisted full-response catalog page has no payload.");
        }

        var value = reader.GetValue(ordinal);
        if (value is byte[] bytes)
        {
            return bytes;
        }

        if (value is string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        throw new InvalidOperationException("A persisted full-response catalog page has an unsupported payload type.");
    }

    private static StagedPage ParseStagedPage(string key, byte[] payload)
    {
        var suffixIndex = key.LastIndexOf(".page.", StringComparison.Ordinal);
        if (suffixIndex <= StagePrefix.Length ||
            !int.TryParse(key.Substring(suffixIndex + 6), NumberStyles.None, CultureInfo.InvariantCulture, out var pageNumber))
        {
            throw new InvalidOperationException("A persisted full-response catalog page has an invalid stage key.");
        }

        return new StagedPage
        {
            Generation = key.Substring(StagePrefix.Length, suffixIndex - StagePrefix.Length),
            PageNumber = pageNumber,
            Payload = payload
        };
    }

    private static PageResult AnalyzePage(StagedPage page, byte[] correlationKey)
    {
        JsonDocument? document = null;
        var rawJsonValid = false;
        var rawJsonShape = "invalid";
        try
        {
            document = JsonDocument.Parse(page.Payload);
            rawJsonValid = true;
            rawJsonShape = DescribePageShape(document.RootElement);
        }
        catch (JsonException)
        {
            // The DTO deserialize below reports the bounded failure without rendering payload content.
        }

        try
        {
            var response = Deserialize(page.Payload);
            var dtoValid = string.IsNullOrEmpty(PosOnlineCompatibilityValidator.ValidateCatalogPull(response));
            var batch = RemoteCatalogBatchMapper.BuildRemoteCatalogBatch(response, true, null);
            var dtoProducts = response.Catalog?.Products ?? Array.Empty<PosCatalogProductResponse>();
            var mappedProducts = batch.Products ?? Array.Empty<RemoteCatalogProductWrite>();
            if (dtoProducts.Length != mappedProducts.Count)
            {
                throw new InvalidOperationException("Catalog product mapping cardinality changed.");
            }

            var rawProducts = ExtractRawProducts(document?.RootElement);
            var products = new List<ProductResult>();
            for (var index = 0; index < dtoProducts.Length; index++)
            {
                products.Add(AnalyzeProduct(
                    index + 1,
                    dtoProducts[index],
                    mappedProducts[index],
                    index < rawProducts.Count ? rawProducts[index] : default,
                    index < rawProducts.Count,
                    rawJsonValid,
                    correlationKey));
            }

            return new PageResult
            {
                CatalogRevisionHmac = Hmac(correlationKey, "catalog-version", response.CatalogVersion),
                DtoValid = dtoValid,
                GenerationHmac = Hmac(correlationKey, "generation", page.Generation),
                HasMore = response.HasMore,
                PageNumber = page.PageNumber,
                ProductCount = products.Count,
                Products = products,
                RawJsonShape = rawJsonShape,
                RawJsonValid = rawJsonValid
            };
        }
        catch (Exception exception) when (exception is JsonException || exception is InvalidOperationException || exception is System.Runtime.Serialization.SerializationException)
        {
            return new PageResult
            {
                CatalogRevisionHmac = string.Empty,
                DtoValid = false,
                GenerationHmac = Hmac(correlationKey, "generation", page.Generation),
                HasMore = false,
                PageNumber = page.PageNumber,
                ProductCount = 0,
                Products = new List<ProductResult>(),
                RawJsonShape = rawJsonShape,
                RawJsonValid = rawJsonValid
            };
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static ProductResult AnalyzeProduct(
        int row,
        PosCatalogProductResponse? dto,
        RemoteCatalogProductWrite? mapped,
        JsonElement rawProduct,
        bool rawProductAvailable,
        bool rawJsonValid,
        byte[] correlationKey)
    {
        var productId = dto?.ProductId;
        var barcode = dto?.Barcode;
        var dtoCode = PosOnlineCompatibilityValidator.ValidateCatalogRows(new PosCatalogPayload
        {
            Products = new[] { dto }
        });
        var fingerprint = RemoteCatalogBatchMapper.ProductStageFingerprint(mapped);
        var mappedRowValid = !string.Equals(fingerprint, "invalid", StringComparison.Ordinal);
        var fieldPresence = rawProductAvailable
            ? ProductFieldPresence(rawProduct)
            : new Dictionary<string, bool>(StringComparer.Ordinal);
        var fieldTypes = rawProductAvailable
            ? ProductFieldTypes(rawProduct)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var reason = ClassifyReason(dto, mapped, dtoCode, mappedRowValid, fieldPresence, rawJsonValid);
        var firstInvalidPhase = !rawJsonValid
            ? "raw_json"
            : !string.IsNullOrEmpty(dtoCode)
                ? "dto"
                : !mappedRowValid
                    ? "mapped_row"
                    : "none";
        return new ProductResult
        {
            BarcodeHmac = Hmac(correlationKey, "barcode", barcode),
            DtoValid = string.IsNullOrEmpty(dtoCode),
            FieldLengths = new
            {
                barcode = Length(barcode),
                itemNumber = Length(dto?.ItemNumber),
                productId = Length(productId),
                productName = Length(dto?.ProductName),
                secondProductName = Length(dto?.SecondProductName)
            },
            Fields = fieldPresence,
            FirstInvalidPhase = firstInvalidPhase,
            MappedRowValid = mappedRowValid,
            ObservedJsonTypes = fieldTypes,
            PriceClass = DescribePrice(dto?.RetailPrice),
            Reason = reason,
            RemoteProductIdHmac = Hmac(correlationKey, "remote-product-id", productId),
            Row = row
        };
    }

    private static string ClassifyReason(
        PosCatalogProductResponse? dto,
        RemoteCatalogProductWrite? mapped,
        string dtoCode,
        bool mappedRowValid,
        IReadOnlyDictionary<string, bool> presence,
        bool rawJsonValid)
    {
        if (!rawJsonValid) return "json_shape_invalid";
        if (dto == null) return "null_product";
        if (presence.Count > 0 && RequiredProductFields.Any(field => !presence.TryGetValue(field, out var present) || !present))
            return "dto_property_missing";
        if (string.IsNullOrWhiteSpace(dto.ProductId)) return "missing_remote_product_id";
        if (string.IsNullOrWhiteSpace(dto.Barcode)) return "blank_barcode";
        if (double.IsNaN(dto.RetailPrice.GetValueOrDefault()) || double.IsInfinity(dto.RetailPrice.GetValueOrDefault()))
            return "nonfinite_retail_price";
        if (!dto.RetailPrice.HasValue || dto.RetailPrice.Value <= 0 || RemoteCatalogBatchMapper.ToLong(dto.RetailPrice) <= 0)
            return "nonpositive_unit_price_after_conversion";
        if (dto.RetailPrice.Value >= long.MaxValue) return "conversion_overflow";
        if ((dto.Barcode ?? string.Empty).Trim().StartsWith("DISC:", StringComparison.Ordinal))
            return "reserved_disc_barcode";
        if ((dto.Barcode ?? string.Empty).Trim().StartsWith("MANUAL:", StringComparison.Ordinal))
            return "reserved_manual_barcode";
        if (string.IsNullOrWhiteSpace(mapped?.Name)) return "blank_display_name";
        if (!string.IsNullOrEmpty(dtoCode) || !mappedRowValid) return "unexpected_reason";
        return "valid";
    }

    private static string DescribePrice(double? price)
    {
        if (!price.HasValue) return "missing";
        if (double.IsNaN(price.Value) || double.IsInfinity(price.Value)) return "nonfinite";
        if (price.Value <= 0) return "nonpositive";
        if (price.Value >= long.MaxValue) return "conversion_saturates_long_max";
        if (RemoteCatalogBatchMapper.ToLong(price) <= 0) return "positive_rounds_to_zero";
        return "positive_converts_to_long";
    }

    private static PosCatalogPullResponse Deserialize(byte[] payload)
    {
        using (var stream = new MemoryStream(payload, writable: false))
        {
            var serializer = new DataContractJsonSerializer(typeof(PosCatalogPullResponse));
            return serializer.ReadObject(stream) as PosCatalogPullResponse
                ?? throw new InvalidOperationException("Persisted full-response page did not deserialize to its contract.");
        }
    }

    private static IReadOnlyList<JsonElement> ExtractRawProducts(JsonElement? root)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object ||
            !root.Value.TryGetProperty("catalog", out var catalog) ||
            catalog.ValueKind != JsonValueKind.Object ||
            !catalog.TryGetProperty("products", out var products) ||
            products.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }

        return products.EnumerateArray().ToArray();
    }

    private static string DescribePageShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root.ValueKind.ToString().ToLowerInvariant();
        if (!root.TryGetProperty("catalog", out var catalog)) return "object_missing_catalog";
        if (catalog.ValueKind != JsonValueKind.Object) return "catalog_" + catalog.ValueKind.ToString().ToLowerInvariant();
        if (!catalog.TryGetProperty("products", out var products)) return "catalog_missing_products";
        return products.ValueKind == JsonValueKind.Array ? "catalog_products_array" : "catalog_products_" + products.ValueKind.ToString().ToLowerInvariant();
    }

    private static Dictionary<string, bool> ProductFieldPresence(JsonElement product)
    {
        return RequiredProductFields.ToDictionary(
            field => field,
            field => product.ValueKind == JsonValueKind.Object && product.TryGetProperty(field, out _),
            StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ProductFieldTypes(JsonElement product)
    {
        return RequiredProductFields.ToDictionary(
            field => field,
            field => product.ValueKind == JsonValueKind.Object && product.TryGetProperty(field, out var value)
                ? value.ValueKind.ToString().ToLowerInvariant()
                : "missing",
            StringComparer.Ordinal);
    }

    private static string Hmac(byte[] key, string domain, string? value)
    {
        using (var hmac = new HMACSHA256(key))
        {
            var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes((domain ?? string.Empty) + "\0" + (value ?? string.Empty)));
            return string.Concat(bytes.Take(HmacHexLength / 2).Select(valueByte => valueByte.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    private static int Length(string? value)
    {
        return value?.Length ?? 0;
    }

    private static string SafeCode(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > 120)
        {
            return "unknown";
        }
        return normalized.All(character =>
            (character >= 'a' && character <= 'z') ||
            (character >= '0' && character <= '9') ||
            character == '_' || character == '-' || character == '.')
                ? normalized
                : "invalid_code";
    }

    private static int PhaseOrder(string phase)
    {
        switch (phase)
        {
            case "raw_json": return 0;
            case "dto": return 1;
            case "mapped_row": return 2;
            default: return 3;
        }
    }

    private static readonly string[] RequiredProductFields =
    {
        "productId", "barcode", "productName", "secondProductName", "retailPrice"
    };

    private sealed class StagedPage
    {
        public string Generation { get; set; } = string.Empty;
        public int PageNumber { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    private sealed class PageResult
    {
        public string CatalogRevisionHmac { get; set; } = string.Empty;
        public bool DtoValid { get; set; }
        public string GenerationHmac { get; set; } = string.Empty;
        public bool HasMore { get; set; }
        public int PageNumber { get; set; }
        public int ProductCount { get; set; }
        public List<ProductResult> Products { get; set; } = new List<ProductResult>();
        public string RawJsonShape { get; set; } = string.Empty;
        public bool RawJsonValid { get; set; }
    }

    private sealed class ProductResult
    {
        public string BarcodeHmac { get; set; } = string.Empty;
        public bool DtoValid { get; set; }
        public object FieldLengths { get; set; } = new { };
        public IReadOnlyDictionary<string, bool> Fields { get; set; } =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        public string FirstInvalidPhase { get; set; } = string.Empty;
        public bool MappedRowValid { get; set; }
        public IReadOnlyDictionary<string, string> ObservedJsonTypes { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public string PriceClass { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string RemoteProductIdHmac { get; set; } = string.Empty;
        public int Row { get; set; }
    }

    private sealed class AuthoritativeProductEvidence
    {
        public long InvalidOccurrenceCount { get; set; }
        public int PageNumber { get; set; }
        public long ProductOccurrenceCount { get; set; }
    }
}
