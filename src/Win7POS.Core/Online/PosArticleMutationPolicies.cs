using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Win7POS.Core.Online
{
    public static class PosArticleMutationFieldMaskPolicy
    {
        private static readonly string[] Allowed =
        {
            PosArticleMutationFields.Barcode,
            PosArticleMutationFields.CategoryId,
            PosArticleMutationFields.ItemNumber,
            PosArticleMutationFields.PrimaryName,
            PosArticleMutationFields.SecondaryName,
            PosArticleMutationFields.SupplierId
        };

        public static IReadOnlyList<string> Normalize(IEnumerable<string> fieldMask)
        {
            var values = (fieldMask ?? Array.Empty<string>()).ToArray();
            if (values.Any(value => !Allowed.Contains(value, StringComparer.Ordinal)))
                throw new ArgumentException("Article mutation field mask is invalid.");
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new ArgumentException("Article mutation field mask contains duplicates.");
            Array.Sort(values, StringComparer.Ordinal);
            return new ReadOnlyCollection<string>(values);
        }

        public static bool IsUpdateField(string value)
        {
            return Allowed.Contains(value, StringComparer.Ordinal);
        }
    }

    public static class PosArticleMutationIntentPolicy
    {
        private static readonly Regex SafeId = new Regex(
            "^[A-Za-z0-9][A-Za-z0-9._:-]{0,119}$",
            RegexOptions.CultureInvariant);
        private static readonly Regex ProductRevision = new Regex(
            "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{6}Z$",
            RegexOptions.CultureInvariant);
        private static readonly HashSet<string> MutationKinds =
            new HashSet<string>(
                new[]
                {
                    PosArticleMutationKinds.ProductCreate,
                    PosArticleMutationKinds.ProductDuplicate,
                    PosArticleMutationKinds.ProductUpdate,
                    PosArticleMutationKinds.ProductActivate,
                    PosArticleMutationKinds.ProductDeactivate,
                    PosArticleMutationKinds.ProductRetailPriceChange,
                    PosArticleMutationKinds.ProductPurchasePriceChange,
                    PosArticleMutationKinds.ProductManualStockAdjustment
                },
                StringComparer.Ordinal);
        private static readonly HashSet<string> StockReasons =
            new HashSet<string>(
                new[]
                {
                    "count_correction",
                    "damage",
                    "found",
                    "loss",
                    "other",
                    "return_to_stock",
                    "transfer"
                },
                StringComparer.Ordinal);

        public static PosArticleMutationIntent Create(
            string baseRevision,
            IDictionary<string, object> changes,
            string clientProductId,
            DateTimeOffset createdAt,
            IEnumerable<string> fieldMask,
            string idempotencyKey,
            long localSequence,
            string mutationId,
            string mutationKind,
            DateTimeOffset occurredAt,
            string remoteProductId)
        {
            var normalizedMask = PosArticleMutationFieldMaskPolicy.Normalize(fieldMask);
            var normalizedChanges = NormalizeChanges(
                mutationKind,
                changes,
                normalizedMask);
            ValidateIdentity(
                baseRevision,
                clientProductId,
                idempotencyKey,
                localSequence,
                mutationId,
                mutationKind,
                remoteProductId);

            return new PosArticleMutationIntent(
                NormalizeNullable(baseRevision),
                normalizedChanges,
                clientProductId.Trim(),
                FormatTimestamp(createdAt),
                normalizedMask,
                idempotencyKey.Trim(),
                localSequence,
                mutationId.Trim(),
                mutationKind,
                FormatTimestamp(occurredAt),
                NormalizeRemoteProductId(remoteProductId));
        }

        public static PosArticleMutationIntent Rehydrate(
            string baseRevision,
            IDictionary<string, object> changes,
            string clientProductId,
            string createdAt,
            IEnumerable<string> fieldMask,
            string idempotencyKey,
            long localSequence,
            string mutationId,
            string mutationKind,
            string occurredAt,
            string remoteProductId)
        {
            DateTimeOffset created;
            DateTimeOffset occurred;
            if (!TryParseCanonicalTimestamp(createdAt, out created) ||
                !TryParseCanonicalTimestamp(occurredAt, out occurred))
            {
                throw new ArgumentException(
                    "Article mutation timestamp is not canonical UTC.");
            }
            return Create(
                baseRevision,
                changes,
                clientProductId,
                created,
                fieldMask,
                idempotencyKey,
                localSequence,
                mutationId,
                mutationKind,
                occurred,
                remoteProductId);
        }

        public static PosArticleMutationIntent CreateUnresolved(
            IDictionary<string, object> changes,
            string clientProductId,
            DateTimeOffset createdAt,
            IEnumerable<string> fieldMask,
            string idempotencyKey,
            long localSequence,
            string mutationId,
            string mutationKind,
            DateTimeOffset occurredAt)
        {
            if (string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductCreate,
                    StringComparison.Ordinal) ||
                string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductDuplicate,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Create and duplicate mutations cannot be unresolved.");
            }
            if (!IsSafeId(clientProductId) ||
                !IsSafeId(idempotencyKey) ||
                !IsSafeId(mutationId) ||
                localSequence < 1)
            {
                throw new ArgumentException(
                    "Unresolved article mutation identity is invalid.");
            }

            var normalizedMask = PosArticleMutationFieldMaskPolicy.Normalize(fieldMask);
            var normalizedChanges = NormalizeChanges(
                mutationKind,
                changes,
                normalizedMask);
            return new PosArticleMutationIntent(
                null,
                normalizedChanges,
                clientProductId.Trim(),
                FormatTimestamp(createdAt),
                normalizedMask,
                idempotencyKey.Trim(),
                localSequence,
                mutationId.Trim(),
                mutationKind,
                FormatTimestamp(occurredAt),
                null);
        }

        public static bool IsSafeId(string value)
        {
            return value != null && SafeId.IsMatch(value);
        }

        public static bool IsProductRevision(string value)
        {
            if (value == null || !ProductRevision.IsMatch(value)) return false;
            DateTimeOffset parsed;
            return DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
        }

        public static bool TryParseCanonicalTimestamp(
            string value,
            out DateTimeOffset timestamp)
        {
            return DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }

        private static IDictionary<string, object> NormalizeChanges(
            string mutationKind,
            IDictionary<string, object> changes,
            IReadOnlyList<string> fieldMask)
        {
            if (!MutationKinds.Contains(mutationKind ?? string.Empty))
                throw new ArgumentException("Article mutation kind is invalid.");
            var input = new Dictionary<string, object>(
                changes ?? new Dictionary<string, object>(),
                StringComparer.Ordinal);

            if (string.Equals(
                mutationKind,
                PosArticleMutationKinds.ProductCreate,
                StringComparison.Ordinal))
            {
                RequireOnly(
                    input,
                    PosArticleMutationFields.Barcode,
                    PosArticleMutationFields.ItemNumber,
                    PosArticleMutationFields.PrimaryName,
                    PosArticleMutationFields.SecondaryName,
                    PosArticleMutationFields.CategoryId,
                    PosArticleMutationFields.SupplierId,
                    PosArticleMutationFields.PurchasePrice,
                    PosArticleMutationFields.RetailPrice,
                    PosArticleMutationFields.StockQuantity);
                RequireNonEmptyString(input, PosArticleMutationFields.Barcode);
                RequireNonEmptyString(input, PosArticleMutationFields.PrimaryName);
                NormalizeCommon(input);
                NormalizeNonNegativeNumber(input, PosArticleMutationFields.PurchasePrice);
                NormalizeNonNegativeNumber(input, PosArticleMutationFields.RetailPrice);
                NormalizeNonNegativeNumber(input, PosArticleMutationFields.StockQuantity);
                if (fieldMask.Count != 0)
                    throw new ArgumentException("Create field mask must be empty.");
                return input;
            }

            if (string.Equals(
                mutationKind,
                PosArticleMutationKinds.ProductDuplicate,
                StringComparison.Ordinal))
            {
                RequireOnly(
                    input,
                    PosArticleMutationFields.Barcode,
                    PosArticleMutationFields.ItemNumber,
                    PosArticleMutationFields.PrimaryName,
                    PosArticleMutationFields.SecondaryName,
                    PosArticleMutationFields.CategoryId,
                    PosArticleMutationFields.SupplierId);
                RequireNonEmptyString(input, PosArticleMutationFields.Barcode);
                NormalizeCommon(input);
                if (fieldMask.Count != 0)
                    throw new ArgumentException("Duplicate field mask must be empty.");
                return input;
            }

            if (string.Equals(
                mutationKind,
                PosArticleMutationKinds.ProductUpdate,
                StringComparison.Ordinal))
            {
                if (fieldMask.Count == 0 ||
                    input.Count != fieldMask.Count ||
                    input.Keys.Any(key => !fieldMask.Contains(key, StringComparer.Ordinal)))
                {
                    throw new ArgumentException(
                        "Update changes must exactly equal the field mask.");
                }
                NormalizeCommon(input);
                return input;
            }

            if (string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductActivate,
                    StringComparison.Ordinal) ||
                string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductDeactivate,
                    StringComparison.Ordinal))
            {
                if (fieldMask.Count != 0 || input.Count != 0)
                    throw new ArgumentException(
                        "Activation mutations require empty changes and field mask.");
                return input;
            }

            if (string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductRetailPriceChange,
                    StringComparison.Ordinal) ||
                string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductPurchasePriceChange,
                    StringComparison.Ordinal))
            {
                RequireOnly(input, PosArticleMutationFields.Price);
                if (input.Count != 1)
                    throw new ArgumentException("Price mutation requires price.");
                NormalizeNonNegativeNumber(input, PosArticleMutationFields.Price, true);
                if (fieldMask.Count != 0)
                    throw new ArgumentException("Price field mask must be empty.");
                return input;
            }

            RequireOnly(
                input,
                PosArticleMutationFields.QuantityDelta,
                PosArticleMutationFields.Reason);
            if (input.Count != 2)
                throw new ArgumentException(
                    "Manual stock mutation requires quantityDelta and reason.");
            var delta = NormalizeNumber(
                input[PosArticleMutationFields.QuantityDelta],
                PosArticleMutationFields.QuantityDelta);
            if (delta == 0)
                throw new ArgumentException("Manual stock quantityDelta must be non-zero.");
            input[PosArticleMutationFields.QuantityDelta] = delta;
            var reason = input[PosArticleMutationFields.Reason] as string;
            if (!StockReasons.Contains((reason ?? string.Empty).Trim()))
                throw new ArgumentException("Manual stock reason is invalid.");
            input[PosArticleMutationFields.Reason] = reason.Trim();
            if (fieldMask.Count != 0)
                throw new ArgumentException("Manual stock field mask must be empty.");
            return input;
        }

        private static void ValidateIdentity(
            string baseRevision,
            string clientProductId,
            string idempotencyKey,
            long localSequence,
            string mutationId,
            string mutationKind,
            string remoteProductId)
        {
            if (!IsSafeId(clientProductId) ||
                !IsSafeId(idempotencyKey) ||
                !IsSafeId(mutationId))
            {
                throw new ArgumentException("Article mutation identity is invalid.");
            }
            if (localSequence < 1)
                throw new ArgumentException("Article mutation sequence is invalid.");

            var isCreate = string.Equals(
                mutationKind,
                PosArticleMutationKinds.ProductCreate,
                StringComparison.Ordinal);
            if (isCreate)
            {
                if (localSequence != 1 ||
                    !string.IsNullOrWhiteSpace(baseRevision) ||
                    !string.IsNullOrWhiteSpace(remoteProductId))
                {
                    throw new ArgumentException(
                        "Product create requires sequence one and no remote identity.");
                }
                return;
            }

            if (!Guid.TryParse(remoteProductId, out _) ||
                !IsProductRevision(baseRevision))
            {
                throw new ArgumentException(
                    "Non-create article mutation requires remote identity and six-fraction base revision.");
            }
            if (string.Equals(
                    mutationKind,
                    PosArticleMutationKinds.ProductDuplicate,
                    StringComparison.Ordinal) &&
                localSequence != 1)
            {
                throw new ArgumentException(
                    "Product duplicate requires sequence one.");
            }
        }

        private static void NormalizeCommon(IDictionary<string, object> changes)
        {
            foreach (var field in new[]
            {
                PosArticleMutationFields.Barcode,
                PosArticleMutationFields.PrimaryName
            })
            {
                object value;
                if (!changes.TryGetValue(field, out value)) continue;
                var text = value as string;
                if (string.IsNullOrWhiteSpace(text))
                    throw new ArgumentException(field + " must be non-empty.");
                changes[field] = NormalizeBoundedText(
                    text,
                    field,
                    string.Equals(
                        field,
                        PosArticleMutationFields.Barcode,
                        StringComparison.Ordinal)
                        ? 96
                        : 240);
            }

            foreach (var field in new[]
            {
                PosArticleMutationFields.ItemNumber,
                PosArticleMutationFields.SecondaryName
            })
            {
                object value;
                if (!changes.TryGetValue(field, out value)) continue;
                if (value == null)
                {
                    changes[field] = null;
                    continue;
                }
                var text = value as string;
                if (text == null)
                    throw new ArgumentException(field + " must be text or null.");
                changes[field] = string.IsNullOrWhiteSpace(text)
                    ? null
                    : NormalizeBoundedText(
                        text,
                        field,
                        string.Equals(
                            field,
                            PosArticleMutationFields.ItemNumber,
                            StringComparison.Ordinal)
                            ? 120
                            : 240);
            }

            foreach (var field in new[]
            {
                PosArticleMutationFields.CategoryId,
                PosArticleMutationFields.SupplierId
            })
            {
                object value;
                if (!changes.TryGetValue(field, out value)) continue;
                if (value == null)
                {
                    changes[field] = null;
                    continue;
                }
                var text = value as string;
                Guid parsed;
                if (text == null || !Guid.TryParse(text, out parsed))
                    throw new ArgumentException(field + " must be a remote UUID or null.");
                changes[field] = parsed.ToString("D").ToLowerInvariant();
            }
        }

        private static void RequireOnly(
            IDictionary<string, object> values,
            params string[] allowed)
        {
            if (values.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)))
                throw new ArgumentException(
                    "Article mutation changes contain an unsupported field.");
        }

        private static void RequireNonEmptyString(
            IDictionary<string, object> values,
            string field)
        {
            object value;
            if (!values.TryGetValue(field, out value) ||
                string.IsNullOrWhiteSpace(value as string))
            {
                throw new ArgumentException(field + " is required.");
            }
        }

        private static void NormalizeNonNegativeNumber(
            IDictionary<string, object> values,
            string field,
            bool required = false)
        {
            object value;
            if (!values.TryGetValue(field, out value))
            {
                if (required) throw new ArgumentException(field + " is required.");
                return;
            }
            var number = NormalizeNumber(value, field);
            if (number < 0)
                throw new ArgumentException(field + " must be non-negative.");
            values[field] = number;
        }

        private static decimal NormalizeNumber(object value, string field)
        {
            decimal number;
            try
            {
                number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (
                ex is FormatException ||
                ex is InvalidCastException ||
                ex is OverflowException)
            {
                throw new ArgumentException(field + " must be numeric.");
            }
            if (Math.Abs(number) > 1000000000000m ||
                decimal.Round(number, 3, MidpointRounding.AwayFromZero) != number)
            {
                throw new ArgumentException(
                    field + " must be bounded to three fractional digits.");
            }
            return number;
        }

        private static string FormatTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        private static string NormalizeNullable(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeRemoteProductId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            Guid parsed;
            if (!Guid.TryParse(value, out parsed))
                throw new ArgumentException("Remote product ID is invalid.");
            return parsed.ToString("D").ToLowerInvariant();
        }

        private static string NormalizeBoundedText(
            string value,
            string field,
            int maximumLength)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0 || normalized.Length > maximumLength)
                throw new ArgumentException(field + " exceeds the contract limit.");
            for (var index = 0; index < normalized.Length; index++)
            {
                if (char.IsControl(normalized[index]))
                    throw new ArgumentException(field + " contains control text.");
            }
            return normalized;
        }
    }

    public static class PosArticleMutationRequestWriter
    {
        public static byte[] WriteUtf8(PosArticleMutationEnvelope envelope)
        {
            ValidateEnvelope(envelope);
            var builder = new StringBuilder(1024);
            builder.Append('{');
            PosArticleMutationCanonicalWriter.AppendName(builder, "schemaVersion");
            PosArticleMutationCanonicalWriter.AppendString(
                builder,
                PosArticleMutationContract.SchemaVersion);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "appVersion");
            PosArticleMutationCanonicalWriter.AppendString(builder, envelope.AppVersion);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "shopId");
            PosArticleMutationCanonicalWriter.AppendString(builder, envelope.ShopId);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "shopDeviceId");
            PosArticleMutationCanonicalWriter.AppendString(builder, envelope.ShopDeviceId);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "staffId");
            PosArticleMutationCanonicalWriter.AppendString(builder, envelope.StaffId);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(
                builder,
                "staffCredentialVersion");
            builder.Append(
                envelope.StaffCredentialVersion.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "posSessionId");
            PosArticleMutationCanonicalWriter.AppendString(builder, envelope.PosSessionId);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "deviceToken");
            PosArticleMutationCanonicalWriter.AppendString(builder, envelope.DeviceToken);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "sessionToken");
            PosArticleMutationCanonicalWriter.AppendString(builder, envelope.SessionToken);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "mutations");
            builder.Append('[');
            for (var index = 0; index < envelope.Mutations.Count; index++)
            {
                if (index > 0) builder.Append(',');
                AppendMutation(builder, envelope.Mutations[index]);
            }
            builder.Append(']');
            builder.Append('}');

            var result = new UTF8Encoding(false, true).GetBytes(builder.ToString());
            if (result.Length > PosArticleMutationContract.MaximumEncodedRequestBytes)
            {
                throw new ArgumentException(
                    "Article mutation request exceeds the 256 KiB UTF-8 limit.");
            }
            return result;
        }

        private static void AppendMutation(
            StringBuilder builder,
            PosArticleMutationRequest request)
        {
            if (request == null || request.Intent == null)
                throw new ArgumentException("Article mutation request is incomplete.");
            if (!PosArticleMutationIntentPolicy.IsSafeId(request.AttemptToken))
                throw new ArgumentException("Article mutation attempt token is invalid.");

            var calculatedHash = PosArticleMutationPayloadHash.Compute(request.Intent);
            if (!string.Equals(
                request.PayloadHash,
                calculatedHash,
                StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Article mutation payload hash does not match immutable intent.");
            }

            var intent = request.Intent;
            builder.Append('{');
            PosArticleMutationCanonicalWriter.AppendName(builder, "mutationId");
            PosArticleMutationCanonicalWriter.AppendString(builder, intent.MutationId);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "idempotencyKey");
            PosArticleMutationCanonicalWriter.AppendString(builder, intent.IdempotencyKey);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "payloadHash");
            PosArticleMutationCanonicalWriter.AppendString(builder, request.PayloadHash);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "attemptToken");
            PosArticleMutationCanonicalWriter.AppendString(builder, request.AttemptToken);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "mutationKind");
            PosArticleMutationCanonicalWriter.AppendString(builder, intent.MutationKind);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "clientProductId");
            PosArticleMutationCanonicalWriter.AppendString(builder, intent.ClientProductId);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "remoteProductId");
            PosArticleMutationCanonicalWriter.AppendStringOrNull(
                builder,
                intent.RemoteProductId);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "baseRevision");
            PosArticleMutationCanonicalWriter.AppendStringOrNull(
                builder,
                intent.BaseRevision);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "localSequence");
            builder.Append(intent.LocalSequence.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "fieldMask");
            PosArticleMutationCanonicalWriter.AppendStringArray(
                builder,
                intent.FieldMask);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "changes");
            PosArticleMutationCanonicalWriter.AppendChanges(builder, intent.Changes);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "createdAt");
            PosArticleMutationCanonicalWriter.AppendString(builder, intent.CreatedAt);
            builder.Append(',');
            PosArticleMutationCanonicalWriter.AppendName(builder, "occurredAt");
            PosArticleMutationCanonicalWriter.AppendString(builder, intent.OccurredAt);
            builder.Append('}');
        }

        private static void ValidateEnvelope(PosArticleMutationEnvelope envelope)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (envelope.Mutations == null ||
                envelope.Mutations.Count < 1 ||
                envelope.Mutations.Count > PosArticleMutationContract.MaximumBatchCount)
            {
                throw new ArgumentException(
                    "Article mutation batch count must be between 1 and 25.");
            }
            if (string.IsNullOrWhiteSpace(envelope.AppVersion) ||
                envelope.AppVersion.Trim().Length > 80 ||
                !Guid.TryParse(envelope.ShopId, out _) ||
                !Guid.TryParse(envelope.ShopDeviceId, out _) ||
                !Guid.TryParse(envelope.StaffId, out _) ||
                !Guid.TryParse(envelope.PosSessionId, out _) ||
                envelope.StaffCredentialVersion < 1 ||
                string.IsNullOrWhiteSpace(envelope.DeviceToken) ||
                envelope.DeviceToken.Trim().Length >
                    PosArticleMutationContract.MaximumSecretLength ||
                string.IsNullOrWhiteSpace(envelope.SessionToken) ||
                envelope.SessionToken.Trim().Length >
                    PosArticleMutationContract.MaximumSecretLength)
            {
                throw new ArgumentException(
                    "Article mutation trusted-session envelope is invalid.");
            }

            EnsureUnique(
                envelope.Mutations.Select(item => item?.Intent?.MutationId),
                "mutation ID");
            EnsureUnique(
                envelope.Mutations.Select(item => item?.Intent?.IdempotencyKey),
                "idempotency key");
            EnsureUnique(
                envelope.Mutations.Select(item => item?.AttemptToken),
                "attempt token");
            EnsureUnique(
                envelope.Mutations.Select(item =>
                    (item?.Intent?.ClientProductId ?? string.Empty) + ":" +
                    (item?.Intent?.LocalSequence ?? 0).ToString(
                        CultureInfo.InvariantCulture)),
                "product sequence");
        }

        private static void EnsureUnique(IEnumerable<string> values, string field)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value) || !set.Add(value))
                    throw new ArgumentException(
                        "Article mutation batch contains a duplicate " + field + ".");
            }
        }
    }

    public enum PosArticleMutationLocalDisposition
    {
        Completed,
        RetryWait,
        FailedBlocked,
        AuthStop
    }

    public static class PosArticleMutationStatusPolicy
    {
        public const string Applied = "applied";
        public const string DuplicateReplay = "duplicate_replay";
        public const string RetryableUpstream = "retryable_upstream";
        public const string FailedAuth = "failed_auth";
        public const string FailedValidation = "failed_validation";
        public const string FailedConflict = "failed_conflict";
        public const string TargetNotFound = "target_not_found";
        public const string IdentityConflict = "identity_conflict";
        public const string IdempotencyPayloadMismatch =
            "idempotency_payload_mismatch";

        public static PosArticleMutationLocalDisposition Classify(string status)
        {
            if (string.Equals(status, Applied, StringComparison.Ordinal) ||
                string.Equals(status, DuplicateReplay, StringComparison.Ordinal))
            {
                return PosArticleMutationLocalDisposition.Completed;
            }
            if (string.Equals(status, RetryableUpstream, StringComparison.Ordinal))
                return PosArticleMutationLocalDisposition.RetryWait;
            if (string.Equals(status, FailedAuth, StringComparison.Ordinal))
                return PosArticleMutationLocalDisposition.AuthStop;
            if (IsBlocked(status))
                return PosArticleMutationLocalDisposition.FailedBlocked;
            throw new ArgumentException("Unknown article mutation status.", nameof(status));
        }

        public static bool IsBlocked(string status)
        {
            return
                string.Equals(status, FailedValidation, StringComparison.Ordinal) ||
                string.Equals(status, FailedConflict, StringComparison.Ordinal) ||
                string.Equals(status, TargetNotFound, StringComparison.Ordinal) ||
                string.Equals(status, IdentityConflict, StringComparison.Ordinal) ||
                string.Equals(
                    status,
                    IdempotencyPayloadMismatch,
                    StringComparison.Ordinal);
        }
    }
}
