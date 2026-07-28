using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Win7POS.Core.Online
{
    public static class PosArticleMutationCanonicalWriter
    {
        private static readonly string[] ChangePropertyOrder =
        {
            PosArticleMutationFields.Barcode,
            PosArticleMutationFields.ItemNumber,
            PosArticleMutationFields.PrimaryName,
            PosArticleMutationFields.SecondaryName,
            PosArticleMutationFields.CategoryId,
            PosArticleMutationFields.SupplierId,
            PosArticleMutationFields.PurchasePrice,
            PosArticleMutationFields.RetailPrice,
            PosArticleMutationFields.StockQuantity,
            PosArticleMutationFields.Price,
            PosArticleMutationFields.QuantityDelta,
            PosArticleMutationFields.Reason
        };

        public static byte[] WriteUtf8(PosArticleMutationIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            var builder = new StringBuilder(512);
            AppendCanonical(builder, intent);
            return new UTF8Encoding(false, true).GetBytes(builder.ToString());
        }

        public static string Write(PosArticleMutationIntent intent)
        {
            return new UTF8Encoding(false, true).GetString(WriteUtf8(intent));
        }

        internal static void AppendCanonical(
            StringBuilder builder,
            PosArticleMutationIntent intent)
        {
            builder.Append('{');
            AppendName(builder, "baseRevision");
            AppendStringOrNull(builder, intent.BaseRevision);
            builder.Append(',');
            AppendName(builder, "changes");
            AppendChanges(builder, intent.Changes);
            builder.Append(',');
            AppendName(builder, "clientProductId");
            AppendString(builder, intent.ClientProductId);
            builder.Append(',');
            AppendName(builder, "createdAt");
            AppendString(builder, intent.CreatedAt);
            builder.Append(',');
            AppendName(builder, "fieldMask");
            AppendStringArray(builder, intent.FieldMask);
            builder.Append(',');
            AppendName(builder, "idempotencyKey");
            AppendString(builder, intent.IdempotencyKey);
            builder.Append(',');
            AppendName(builder, "localSequence");
            builder.Append(intent.LocalSequence.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            AppendName(builder, "mutationId");
            AppendString(builder, intent.MutationId);
            builder.Append(',');
            AppendName(builder, "mutationKind");
            AppendString(builder, intent.MutationKind);
            builder.Append(',');
            AppendName(builder, "occurredAt");
            AppendString(builder, intent.OccurredAt);
            builder.Append(',');
            AppendName(builder, "remoteProductId");
            AppendStringOrNull(builder, intent.RemoteProductId);
            builder.Append('}');
        }

        internal static void AppendChanges(
            StringBuilder builder,
            IReadOnlyDictionary<string, object> changes)
        {
            builder.Append('{');
            var first = true;
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in ChangePropertyOrder)
            {
                object value;
                if (changes == null || !changes.TryGetValue(name, out value))
                {
                    continue;
                }

                if (!first) builder.Append(',');
                first = false;
                emitted.Add(name);
                AppendName(builder, name);
                AppendValue(builder, value);
            }

            if (changes != null && emitted.Count != changes.Count)
            {
                throw new ArgumentException(
                    "Article mutation changes contain an unsupported property.",
                    nameof(changes));
            }
            builder.Append('}');
        }

        internal static void AppendName(StringBuilder builder, string name)
        {
            AppendString(builder, name);
            builder.Append(':');
        }

        internal static void AppendStringArray(
            StringBuilder builder,
            IReadOnlyList<string> values)
        {
            builder.Append('[');
            for (var index = 0; index < (values?.Count ?? 0); index++)
            {
                if (index > 0) builder.Append(',');
                AppendString(builder, values[index]);
            }
            builder.Append(']');
        }

        internal static void AppendStringOrNull(StringBuilder builder, string value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }
            AppendString(builder, value);
        }

        internal static void AppendString(StringBuilder builder, string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            builder.Append('"');
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(character))
                        {
                            if (index + 1 >= value.Length ||
                                !char.IsLowSurrogate(value[index + 1]))
                            {
                                throw new ArgumentException(
                                    "Article mutation text contains an unpaired surrogate.");
                            }
                            builder.Append(character);
                            builder.Append(value[++index]);
                        }
                        else if (char.IsLowSurrogate(character))
                        {
                            throw new ArgumentException(
                                "Article mutation text contains an unpaired surrogate.");
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
        }

        private static void AppendValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            var text = value as string;
            if (text != null)
            {
                AppendString(builder, text);
                return;
            }

            if (value is int)
            {
                builder.Append(((int)value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is long)
            {
                builder.Append(((long)value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (value is decimal)
            {
                builder.Append(((decimal)value).ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }
            if (value is double)
            {
                var number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new ArgumentException("Article mutation number is not finite.");
                builder.Append(number.ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float)
            {
                var number = (float)value;
                if (float.IsNaN(number) || float.IsInfinity(number))
                    throw new ArgumentException("Article mutation number is not finite.");
                builder.Append(number.ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }

            throw new ArgumentException(
                "Article mutation changes contain an unsupported value type.");
        }
    }

    public static class PosArticleMutationPayloadHash
    {
        public static string Compute(PosArticleMutationIntent intent)
        {
            return Compute(PosArticleMutationCanonicalWriter.WriteUtf8(intent));
        }

        public static string Compute(byte[] canonicalPayload)
        {
            if (canonicalPayload == null)
                throw new ArgumentNullException(nameof(canonicalPayload));
            using (var algorithm = SHA256.Create())
            {
                var digest = algorithm.ComputeHash(canonicalPayload);
                var builder = new StringBuilder("sha256:", 71);
                foreach (var value in digest)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }
    }
}
