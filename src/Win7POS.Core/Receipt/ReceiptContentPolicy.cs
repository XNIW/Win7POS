using System;
using System.Collections.Generic;
using System.Text;
using Win7POS.Core.Models;
using Win7POS.Core.Online;

namespace Win7POS.Core.Receipt
{
    public sealed class ReceiptContentValidationException : InvalidOperationException
    {
        public ReceiptContentValidationException(
            string code,
            string field,
            int characters,
            int utf8Bytes)
            : base((code ?? "receipt_content_invalid") +
                ": field=" + (field ?? string.Empty) +
                ", characters=" + characters +
                ", utf8Bytes=" + utf8Bytes)
        {
            Code = code ?? "receipt_content_invalid";
            Field = field ?? string.Empty;
            Characters = characters;
            Utf8Bytes = utf8Bytes;
        }

        public string Code { get; }
        public string Field { get; }
        public int Characters { get; }
        public int Utf8Bytes { get; }
    }

    public sealed class ReceiptShopMetadata
    {
        public string BusinessAddress { get; set; }
        public string BusinessCity { get; set; }
        public string BusinessGiro { get; set; }
        public string BusinessPhone { get; set; }
        public string CompanyRut { get; set; }
        public string Footer { get; set; }
        public string LegalRepresentativeRut { get; set; }
        public string ShopCode { get; set; }
        public string ShopId { get; set; }
        public string ShopName { get; set; }
        public string ShopStatus { get; set; }
        public string Source { get; set; }
        public string SyncedAtUtc { get; set; }
        public string UpdatedAt { get; set; }
    }

    /// <summary>
    /// Semantic limits for remotely supplied shop identity and receipt metadata.
    /// Values are rejected in full; fiscal identity is never silently truncated.
    /// </summary>
    public static class ReceiptShopMetadataPolicy
    {
        public const int MaxShopNameCharacters = 128;
        public const int MaxAddressCharacters = 256;
        public const int MaxCityCharacters = 128;
        public const int MaxRutCharacters = 32;
        public const int MaxGiroCharacters = 256;
        public const int MaxPhoneCharacters = 64;
        public const int MaxFooterCharacters = 256;
        public const int MaxShopIdCharacters = 160;
        public const int MaxShopCodeCharacters = 80;
        public const int MaxShopStatusCharacters = 32;
        public const int MaxSourceCharacters = 120;
        public const int MaxTimestampCharacters = 64;
        public const int MaxVisibleCharacters = 1024;
        public const int MaxVisibleUtf8Bytes = 4096;
        public const int MaxSnapshotCharacters = 1600;
        public const int MaxSnapshotUtf8Bytes = 6144;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static ReceiptShopMetadata FromRemoteShop(PosShopResponse shop)
        {
            if (shop == null) return null;
            return new ReceiptShopMetadata
            {
                BusinessAddress = shop.BusinessAddress,
                BusinessCity = shop.BusinessCity,
                BusinessGiro = shop.BusinessGiro,
                CompanyRut = shop.CompanyRut,
                LegalRepresentativeRut = shop.LegalRepresentativeRut,
                ShopCode = shop.ShopCode,
                ShopId = shop.ShopId,
                ShopName = shop.ShopName,
                ShopStatus = shop.ShopStatus,
                Source = shop.Source,
                UpdatedAt = shop.UpdatedAt
            };
        }

        public static ReceiptShopMetadata FromReceiptShop(ReceiptShopInfo shop)
        {
            if (shop == null) return null;
            return new ReceiptShopMetadata
            {
                BusinessAddress = shop.Address,
                BusinessCity = shop.City,
                BusinessGiro = shop.BusinessGiro,
                BusinessPhone = shop.Phone,
                CompanyRut = shop.Rut,
                Footer = shop.Footer,
                LegalRepresentativeRut = shop.LegalRepresentativeRut,
                ShopCode = shop.ShopCode,
                ShopName = shop.Name,
                ShopStatus = shop.ShopStatus,
                Source = shop.Source,
                SyncedAtUtc = shop.SyncedAtUtc
            };
        }

        public static void EnsureValidRemoteShop(PosShopResponse shop)
        {
            if (shop == null) return;
            EnsureValidSnapshot(FromRemoteShop(shop));
        }

        public static void EnsureValidReceiptShop(ReceiptShopInfo shop)
        {
            if (shop == null) return;
            EnsureValid(FromReceiptShop(shop), visibleOnly: true);
        }

        public static void EnsureValidSnapshot(ReceiptShopMetadata metadata)
        {
            if (metadata == null) return;
            EnsureValid(metadata, visibleOnly: false);
            // Persisted/remote data must also fit the downstream receipt-visible
            // budget; otherwise ingress would accept a shop that blocks checkout.
            EnsureValid(metadata, visibleOnly: true);
        }

        private static void EnsureValid(ReceiptShopMetadata metadata, bool visibleOnly)
        {
            var fields = new[]
            {
                Field("shopName", metadata.ShopName, MaxShopNameCharacters, visible: true),
                Field("businessAddress", metadata.BusinessAddress, MaxAddressCharacters, visible: true),
                Field("businessCity", metadata.BusinessCity, MaxCityCharacters, visible: true),
                Field("companyRut", metadata.CompanyRut, MaxRutCharacters, visible: true),
                Field("businessGiro", metadata.BusinessGiro, MaxGiroCharacters, visible: true),
                Field("legalRepresentativeRut", metadata.LegalRepresentativeRut, MaxRutCharacters, visible: true),
                Field("businessPhone", metadata.BusinessPhone, MaxPhoneCharacters, visible: true),
                Field("footer", metadata.Footer, MaxFooterCharacters, visible: true),
                Field("shopId", metadata.ShopId, MaxShopIdCharacters, visible: false),
                Field("shopCode", metadata.ShopCode, MaxShopCodeCharacters, visible: true),
                Field("shopStatus", metadata.ShopStatus, MaxShopStatusCharacters, visible: true),
                Field("source", metadata.Source, MaxSourceCharacters, visible: true),
                Field("syncedAtUtc", metadata.SyncedAtUtc, MaxTimestampCharacters, visible: true),
                Field("updatedAt", metadata.UpdatedAt, MaxTimestampCharacters, visible: false)
            };

            var totalCharacters = 0;
            var totalBytes = 0;
            foreach (var field in fields)
            {
                if (visibleOnly && !field.Visible) continue;
                var value = field.Value ?? string.Empty;
                if (value.Length > field.MaximumCharacters)
                {
                    throw Invalid("shop_metadata_field_too_large", field.Name, value.Length, -1);
                }
                EnsureTextIsSafe(value, field.Name);
                var bytes = StrictUtf8.GetByteCount(value);
                totalCharacters = checked(totalCharacters + value.Length);
                totalBytes = checked(totalBytes + bytes);
            }

            var maxCharacters = visibleOnly ? MaxVisibleCharacters : MaxSnapshotCharacters;
            var maxBytes = visibleOnly ? MaxVisibleUtf8Bytes : MaxSnapshotUtf8Bytes;
            if (totalCharacters > maxCharacters || totalBytes > maxBytes)
            {
                throw Invalid(
                    visibleOnly
                        ? "receipt_shop_metadata_budget_exceeded"
                        : "shop_metadata_budget_exceeded",
                    "aggregate",
                    totalCharacters,
                    totalBytes);
            }
        }

        internal static void EnsureTextIsSafe(string value, string field)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character))
                {
                    throw Invalid("shop_metadata_control_character", field, value.Length, -1);
                }
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    {
                        throw Invalid("shop_metadata_invalid_unicode", field, value.Length, -1);
                    }
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    throw Invalid("shop_metadata_invalid_unicode", field, value.Length, -1);
                }
            }
        }

        private static ReceiptShopField Field(
            string name,
            string value,
            int maximumCharacters,
            bool visible)
        {
            return new ReceiptShopField(name, value, maximumCharacters, visible);
        }

        private static ReceiptContentValidationException Invalid(
            string code,
            string field,
            int characters,
            int bytes)
        {
            return new ReceiptContentValidationException(code, field, characters, bytes);
        }

        private sealed class ReceiptShopField
        {
            public ReceiptShopField(
                string name,
                string value,
                int maximumCharacters,
                bool visible)
            {
                Name = name;
                Value = value;
                MaximumCharacters = maximumCharacters;
                Visible = visible;
            }

            public string Name { get; }
            public string Value { get; }
            public int MaximumCharacters { get; }
            public bool Visible { get; }
        }
    }

    /// <summary>
    /// Bounds persisted and rendered sale text before it can fan out into wrapped
    /// receipt rows, sync payloads, or database writes.
    /// </summary>
    public static class SalesReceiptContentPolicy
    {
        public const int MaxSaleLines = 512;
        public const int MaxSaleLineNameCharacters = 512;
        public const int MaxSaleLineBarcodeCharacters = 256;
        public const int MaxSaleCodeCharacters = 128;
        public const int MaxSaleClientIdCharacters = 256;
        public const int MaxSaleReasonCharacters = 512;
        public const int MaxAggregateLineNameCharacters = 8192;
        public const int MaxAggregateLineNameUtf8Bytes = 16384;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static bool IsValidProductName(string value)
        {
            try
            {
                EnsureField(
                    value,
                    MaxSaleLineNameCharacters,
                    "productName",
                    "receipt_sale_line_field_too_large");
                return true;
            }
            catch (ReceiptContentValidationException)
            {
                return false;
            }
        }

        public static bool IsValidBarcode(string value)
        {
            try
            {
                EnsureField(
                    value,
                    MaxSaleLineBarcodeCharacters,
                    "productBarcode",
                    "receipt_sale_line_field_too_large");
                return true;
            }
            catch (ReceiptContentValidationException)
            {
                return false;
            }
        }

        public static void EnsureValidProductIdentity(string barcode, string name)
        {
            EnsureField(
                barcode,
                MaxSaleLineBarcodeCharacters,
                "product.barcode",
                "receipt_sale_line_field_too_large");
            EnsureField(
                name,
                MaxSaleLineNameCharacters,
                "product.name",
                "receipt_sale_line_field_too_large");
        }

        public static void EnsureValidSaleReason(string reason)
        {
            EnsureField(
                reason,
                MaxSaleReasonCharacters,
                "sale.reason",
                "receipt_sale_field_too_large");
        }

        public static void EnsureValid(Sale sale, IReadOnlyList<SaleLine> lines)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            EnsureField(
                sale.Code,
                MaxSaleCodeCharacters,
                "sale.code",
                "receipt_sale_field_too_large");
            EnsureField(
                sale.ClientSaleId,
                MaxSaleClientIdCharacters,
                "sale.clientSaleId",
                "receipt_sale_field_too_large");
            EnsureField(
                sale.Reason,
                MaxSaleReasonCharacters,
                "sale.reason",
                "receipt_sale_field_too_large");
            EnsureValidLines(lines);
        }

        public static void EnsureValidLines(IReadOnlyList<SaleLine> lines)
        {
            var values = lines ?? Array.Empty<SaleLine>();
            if (values.Count > MaxSaleLines)
            {
                throw new ReceiptContentValidationException(
                    "receipt_sale_line_count_exceeded",
                    "sale.lines",
                    values.Count,
                    -1);
            }

            var aggregateCharacters = 0;
            var aggregateBytes = 0;
            for (var index = 0; index < values.Count; index++)
            {
                var line = values[index];
                if (line == null)
                {
                    throw new ReceiptContentValidationException(
                        "receipt_sale_line_missing",
                        "sale.lines[" + index + "]",
                        0,
                        0);
                }

                var name = line.Name ?? string.Empty;
                EnsureField(
                    name,
                    MaxSaleLineNameCharacters,
                    "sale.lines[" + index + "].name",
                    "receipt_sale_line_field_too_large");
                EnsureField(
                    line.Barcode,
                    MaxSaleLineBarcodeCharacters,
                    "sale.lines[" + index + "].barcode",
                    "receipt_sale_line_field_too_large");

                aggregateCharacters = checked(aggregateCharacters + name.Length);
                aggregateBytes = checked(aggregateBytes + StrictUtf8.GetByteCount(name));
                if (aggregateCharacters > MaxAggregateLineNameCharacters ||
                    aggregateBytes > MaxAggregateLineNameUtf8Bytes)
                {
                    throw new ReceiptContentValidationException(
                        "receipt_sale_line_budget_exceeded",
                        "sale.lines.name.aggregate",
                        aggregateCharacters,
                        aggregateBytes);
                }
            }
        }

        public static void EnsureCumulativeLineBudget(
            long existingCount,
            long existingNameCharacters,
            long existingNameUtf8Bytes,
            IReadOnlyList<SaleLine> appendedLines)
        {
            EnsureValidLines(appendedLines);
            var values = appendedLines ?? Array.Empty<SaleLine>();
            long appendedCharacters = 0;
            long appendedBytes = 0;
            foreach (var line in values)
            {
                var name = line?.Name ?? string.Empty;
                appendedCharacters = checked(appendedCharacters + name.Length);
                appendedBytes = checked(appendedBytes + StrictUtf8.GetByteCount(name));
            }

            EnsureStoredLineBudget(
                checked(existingCount + values.Count),
                maximumNameCharacters: 0,
                maximumBarcodeCharacters: 0,
                checked(existingNameCharacters + appendedCharacters),
                checked(existingNameUtf8Bytes + appendedBytes));
        }

        public static void EnsureStoredLineBudget(
            long count,
            long maximumNameCharacters,
            long maximumBarcodeCharacters,
            long aggregateNameCharacters,
            long aggregateNameUtf8Bytes)
        {
            if (count < 0 || count > MaxSaleLines)
            {
                throw new ReceiptContentValidationException(
                    "receipt_sale_line_count_exceeded",
                    "sale.lines",
                    ToDiagnosticInt(count),
                    -1);
            }
            if (maximumNameCharacters > MaxSaleLineNameCharacters ||
                maximumBarcodeCharacters > MaxSaleLineBarcodeCharacters)
            {
                throw new ReceiptContentValidationException(
                    "receipt_sale_line_field_too_large",
                    "sale.lines",
                    ToDiagnosticInt(Math.Max(maximumNameCharacters, maximumBarcodeCharacters)),
                    -1);
            }
            if (aggregateNameCharacters < 0 || aggregateNameUtf8Bytes < 0 ||
                aggregateNameCharacters > MaxAggregateLineNameCharacters ||
                aggregateNameUtf8Bytes > MaxAggregateLineNameUtf8Bytes)
            {
                throw new ReceiptContentValidationException(
                    "receipt_sale_line_budget_exceeded",
                    "sale.lines.name.aggregate",
                    ToDiagnosticInt(aggregateNameCharacters),
                    ToDiagnosticInt(aggregateNameUtf8Bytes));
            }
        }

        private static int ToDiagnosticInt(long value)
        {
            if (value <= int.MinValue) return int.MinValue;
            if (value >= int.MaxValue) return int.MaxValue;
            return (int)value;
        }

        private static void EnsureField(
            string value,
            int maximumCharacters,
            string field,
            string sizeCode)
        {
            var text = value ?? string.Empty;
            if (text.Length > maximumCharacters)
            {
                throw new ReceiptContentValidationException(
                    sizeCode,
                    field,
                    text.Length,
                    -1);
            }

            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (char.IsControl(character))
                {
                    throw new ReceiptContentValidationException(
                        "receipt_content_control_character",
                        field,
                        text.Length,
                        -1);
                }
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    {
                        throw new ReceiptContentValidationException(
                            "receipt_content_invalid_unicode",
                            field,
                            text.Length,
                            -1);
                    }
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    throw new ReceiptContentValidationException(
                        "receipt_content_invalid_unicode",
                        field,
                        text.Length,
                        -1);
                }
            }
        }
    }

    public static class ReceiptDocumentPolicy
    {
        public const int MaxSnapshotJsonCharacters = 16384;
        public const int MaxSnapshotJsonUtf8Bytes = 16384;
        public const int MaxDocumentCharacters = 131072;
        public const int MaxDocumentUtf8Bytes = 262144;
        public const int MaxLogicalLines = 2048;
        public const int MaxCharactersPerLine = 512;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public static void EnsureValidSnapshotJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            if (json.Length > MaxSnapshotJsonCharacters)
            {
                throw new ReceiptContentValidationException(
                    "receipt_shop_snapshot_too_large",
                    "receiptShopSnapshotJson",
                    json.Length,
                    -1);
            }
            EnsureUnicode(json, "receiptShopSnapshotJson", allowLineControls: false);
            var bytes = StrictUtf8.GetByteCount(json);
            if (bytes > MaxSnapshotJsonUtf8Bytes)
            {
                throw new ReceiptContentValidationException(
                    "receipt_shop_snapshot_too_large",
                    "receiptShopSnapshotJson",
                    json.Length,
                    bytes);
            }
        }

        public static void EnsureValidDocument(string receiptText)
        {
            var text = receiptText ?? string.Empty;
            if (text.Length > MaxDocumentCharacters)
            {
                throw new ReceiptContentValidationException(
                    "receipt_document_too_large",
                    "receiptText",
                    text.Length,
                    -1);
            }
            EnsureUnicode(text, "receiptText", allowLineControls: true);

            var lineCount = 1;
            var lineLength = 0;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (character == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                    lineCount++;
                    lineLength = 0;
                }
                else if (character == '\n')
                {
                    lineCount++;
                    lineLength = 0;
                }
                else
                {
                    lineLength++;
                    if (lineLength > MaxCharactersPerLine)
                    {
                        throw new ReceiptContentValidationException(
                            "receipt_document_line_too_large",
                            "receiptText",
                            text.Length,
                            -1);
                    }
                }

                if (lineCount > MaxLogicalLines)
                {
                    throw new ReceiptContentValidationException(
                        "receipt_document_too_many_lines",
                        "receiptText",
                        text.Length,
                        -1);
                }
            }

            var bytes = StrictUtf8.GetByteCount(text);
            if (bytes > MaxDocumentUtf8Bytes)
            {
                throw new ReceiptContentValidationException(
                    "receipt_document_too_large",
                    "receiptText",
                    text.Length,
                    bytes);
            }
        }

        private static void EnsureUnicode(
            string value,
            string field,
            bool allowLineControls)
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character) &&
                    !(allowLineControls &&
                      (character == '\r' || character == '\n' || character == '\t')))
                {
                    throw new ReceiptContentValidationException(
                        "receipt_content_control_character",
                        field,
                        value.Length,
                        -1);
                }
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    {
                        throw new ReceiptContentValidationException(
                            "receipt_content_invalid_unicode",
                            field,
                            value.Length,
                            -1);
                    }
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    throw new ReceiptContentValidationException(
                        "receipt_content_invalid_unicode",
                        field,
                        value.Length,
                        -1);
                }
            }
        }
    }
}
