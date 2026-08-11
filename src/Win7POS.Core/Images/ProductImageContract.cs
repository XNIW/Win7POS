using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Win7POS.Core.Images
{
    public enum ProductImageVariant
    {
        Main = 0,
        Thumb = 1
    }

    public enum ProductImageDisplayState
    {
        FeatureDisabled = 0,
        NoImage = 1,
        Loading = 2,
        Loaded = 3,
        Unavailable = 4,
        Corrupt = 5,
        Error = 6
    }

    public enum ProductImageInputFormat
    {
        Unknown = 0,
        Jpeg = 1,
        Png = 2
    }

    public enum ProductImageValidationCode
    {
        Valid = 0,
        MissingValue = 1,
        InvalidIdentity = 2,
        InvalidScope = 3,
        InvalidObjectPath = 4,
        UnsupportedMimeType = 5,
        InvalidByteSize = 6,
        InvalidDimensions = 7,
        InvalidChecksum = 8,
        InvalidTimestamp = 9,
        CorruptImage = 10,
        ForbiddenMetadata = 11,
        UnsupportedVariant = 12
    }

    public sealed class ProductImageValidationResult
    {
        private static readonly IReadOnlyList<string> NoMessages = new string[0];

        private ProductImageValidationResult(
            ProductImageValidationCode code,
            IReadOnlyList<string> messages)
        {
            Code = code;
            Messages = messages ?? NoMessages;
        }

        public ProductImageValidationCode Code { get; }
        public IReadOnlyList<string> Messages { get; }
        public bool IsValid => Code == ProductImageValidationCode.Valid;

        public static ProductImageValidationResult Success()
        {
            return new ProductImageValidationResult(
                ProductImageValidationCode.Valid,
                NoMessages);
        }

        public static ProductImageValidationResult Failure(
            ProductImageValidationCode code,
            params string[] messages)
        {
            if (code == ProductImageValidationCode.Valid)
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            return new ProductImageValidationResult(
                code,
                (messages ?? new string[0])
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray());
        }
    }

    public static class ProductImageContractV1
    {
        public const int InputMaximumBytes = 25 * 1024 * 1024;
        public const long InputMaximumPixels = 64_000_000L;
        public const int MainMaximumSide = 1600;
        public const int MainMinimumSide = 640;
        public const int MainTargetBytes = 750 * 1024;
        public const int MainMaximumBytes = 1024 * 1024;
        public const int ThumbMaximumSide = 384;
        public const int ThumbMinimumSide = 128;
        public const int ThumbTargetBytes = 90 * 1024;
        public const int ThumbMaximumBytes = 90 * 1024;
        public const int ReadBatchMaximum = 16;
        public const int ReadRequestConcurrency = 2;
        public const int DownloadConcurrency = 4;
        public const string WireMimeType = "image/jpeg";
        public const string BucketName = "product-images";

        public static int MaximumBytes(ProductImageVariant variant)
        {
            switch (variant)
            {
                case ProductImageVariant.Main:
                    return MainMaximumBytes;
                case ProductImageVariant.Thumb:
                    return ThumbMaximumBytes;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public static int MaximumSide(ProductImageVariant variant)
        {
            switch (variant)
            {
                case ProductImageVariant.Main:
                    return MainMaximumSide;
                case ProductImageVariant.Thumb:
                    return ThumbMaximumSide;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public static string VariantName(ProductImageVariant variant)
        {
            switch (variant)
            {
                case ProductImageVariant.Main:
                    return "main";
                case ProductImageVariant.Thumb:
                    return "thumb";
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public static bool IsSupportedVariant(ProductImageVariant variant)
        {
            return variant == ProductImageVariant.Main ||
                   variant == ProductImageVariant.Thumb;
        }
    }

    public sealed class ProductImageIdentity : IEquatable<ProductImageIdentity>
    {
        private ProductImageIdentity(
            string accountScope,
            Guid shopId,
            Guid productId,
            Guid versionId)
        {
            AccountScope = accountScope;
            ShopId = shopId;
            ProductId = productId;
            VersionId = versionId;
        }

        public string AccountScope { get; }
        public Guid ShopId { get; }
        public Guid ProductId { get; }
        public Guid VersionId { get; }

        public static bool TryCreate(
            string accountScope,
            string shopId,
            string productId,
            string versionId,
            out ProductImageIdentity identity,
            out ProductImageValidationResult validation)
        {
            identity = null;

            if (!ProductImageTextPolicy.IsLowerHex(accountScope, 64))
            {
                validation = ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidScope,
                    "account_scope_invalid");
                return false;
            }

            if (!Guid.TryParseExact(shopId, "D", out var parsedShopId) ||
                !Guid.TryParseExact(productId, "D", out var parsedProductId) ||
                !Guid.TryParseExact(versionId, "D", out var parsedVersionId) ||
                parsedShopId == Guid.Empty ||
                parsedProductId == Guid.Empty ||
                parsedVersionId == Guid.Empty)
            {
                validation = ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidIdentity,
                    "image_identity_invalid");
                return false;
            }

            identity = new ProductImageIdentity(
                accountScope,
                parsedShopId,
                parsedProductId,
                parsedVersionId);
            validation = ProductImageValidationResult.Success();
            return true;
        }

        public bool Equals(ProductImageIdentity other)
        {
            return other != null &&
                   string.Equals(AccountScope, other.AccountScope, StringComparison.Ordinal) &&
                   ShopId.Equals(other.ShopId) &&
                   ProductId.Equals(other.ProductId) &&
                   VersionId.Equals(other.VersionId);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ProductImageIdentity);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(AccountScope);
                hash = (hash * 397) ^ ShopId.GetHashCode();
                hash = (hash * 397) ^ ProductId.GetHashCode();
                hash = (hash * 397) ^ VersionId.GetHashCode();
                return hash;
            }
        }
    }

    public sealed class ProductImageMetadata : IEquatable<ProductImageMetadata>
    {
        private ProductImageMetadata(
            string mimeType,
            int byteSize,
            int width,
            int height,
            string sha256)
        {
            MimeType = mimeType;
            ByteSize = byteSize;
            Width = width;
            Height = height;
            Sha256 = sha256;
        }

        public string MimeType { get; }
        public int ByteSize { get; }
        public int Width { get; }
        public int Height { get; }
        public string Sha256 { get; }

        public static bool TryCreate(
            ProductImageVariant variant,
            string mimeType,
            long byteSize,
            int width,
            int height,
            string sha256,
            out ProductImageMetadata metadata,
            out ProductImageValidationResult validation)
        {
            metadata = null;
            if (!ProductImageContractV1.IsSupportedVariant(variant))
            {
                validation = ProductImageValidationResult.Failure(
                    ProductImageValidationCode.UnsupportedVariant,
                    "image_variant_unsupported");
                return false;
            }

            if (!string.Equals(
                    mimeType,
                    ProductImageContractV1.WireMimeType,
                    StringComparison.Ordinal))
            {
                validation = ProductImageValidationResult.Failure(
                    ProductImageValidationCode.UnsupportedMimeType,
                    "image_mime_unsupported");
                return false;
            }

            if (byteSize < 1 || byteSize > ProductImageContractV1.MaximumBytes(variant))
            {
                validation = ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidByteSize,
                    "image_byte_size_invalid");
                return false;
            }

            var maximumSide = ProductImageContractV1.MaximumSide(variant);
            if (width < 1 ||
                height < 1 ||
                width > maximumSide ||
                height > maximumSide ||
                (long)width * height > ProductImageContractV1.InputMaximumPixels)
            {
                validation = ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidDimensions,
                    "image_dimensions_invalid");
                return false;
            }

            if (!ProductImageTextPolicy.IsLowerHex(sha256, 64))
            {
                validation = ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidChecksum,
                    "image_sha256_invalid");
                return false;
            }

            metadata = new ProductImageMetadata(
                ProductImageContractV1.WireMimeType,
                checked((int)byteSize),
                width,
                height,
                sha256);
            validation = ProductImageValidationResult.Success();
            return true;
        }

        public bool Equals(ProductImageMetadata other)
        {
            return other != null &&
                   ByteSize == other.ByteSize &&
                   Width == other.Width &&
                   Height == other.Height &&
                   string.Equals(MimeType, other.MimeType, StringComparison.Ordinal) &&
                   string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ProductImageMetadata);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = ByteSize;
                hash = (hash * 397) ^ Width;
                hash = (hash * 397) ^ Height;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(MimeType);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Sha256);
                return hash;
            }
        }
    }

    public sealed class ProductImageReference : IEquatable<ProductImageReference>
    {
        public ProductImageReference(
            ProductImageIdentity identity,
            ProductImageVariant variant,
            ProductImageMetadata metadata,
            DateTimeOffset? imageUpdatedAt = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            if (!ProductImageContractV1.IsSupportedVariant(variant))
            {
                throw new ArgumentOutOfRangeException(nameof(variant));
            }

            Variant = variant;
            ImageUpdatedAt = imageUpdatedAt?.ToUniversalTime();
        }

        public ProductImageIdentity Identity { get; }
        public ProductImageVariant Variant { get; }
        public ProductImageMetadata Metadata { get; }
        public DateTimeOffset? ImageUpdatedAt { get; }

        public bool Equals(ProductImageReference other)
        {
            return other != null &&
                   Identity.Equals(other.Identity) &&
                   Variant == other.Variant &&
                   Metadata.Equals(other.Metadata) &&
                   Nullable.Equals(ImageUpdatedAt, other.ImageUpdatedAt);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ProductImageReference);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Identity.GetHashCode();
                hash = (hash * 397) ^ (int)Variant;
                hash = (hash * 397) ^ Metadata.GetHashCode();
                hash = (hash * 397) ^ (ImageUpdatedAt?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }

    public sealed class ProductImageCacheKey : IEquatable<ProductImageCacheKey>
    {
        private ProductImageCacheKey(string canonicalValue, string fileStem)
        {
            CanonicalValue = canonicalValue;
            FileStem = fileStem;
        }

        public string CanonicalValue { get; }
        public string FileStem { get; }

        public static ProductImageCacheKey FromReference(ProductImageReference reference)
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            var identity = reference.Identity;
            var canonical = string.Join(
                "\n",
                "product-image-cache-v1",
                identity.AccountScope,
                identity.ShopId.ToString("D").ToLowerInvariant(),
                identity.ProductId.ToString("D").ToLowerInvariant(),
                identity.VersionId.ToString("D").ToLowerInvariant(),
                ProductImageContractV1.VariantName(reference.Variant));

            return new ProductImageCacheKey(
                canonical,
                ProductImageHash.Sha256Hex(Encoding.UTF8.GetBytes(canonical)));
        }

        public bool Equals(ProductImageCacheKey other)
        {
            return other != null &&
                   string.Equals(CanonicalValue, other.CanonicalValue, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ProductImageCacheKey);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(CanonicalValue);
        }

        public override string ToString()
        {
            return CanonicalValue;
        }
    }

    public static class ProductImageObjectPathPolicy
    {
        public static ProductImageValidationResult Validate(
            string objectPath,
            ProductImageIdentity identity,
            ProductImageVariant variant)
        {
            if (identity == null || string.IsNullOrWhiteSpace(objectPath))
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.MissingValue,
                    "image_object_path_missing");
            }

            if (!ProductImageContractV1.IsSupportedVariant(variant))
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.UnsupportedVariant,
                    "image_variant_unsupported");
            }

            if (objectPath.IndexOf('\\') >= 0 ||
                objectPath.IndexOf('\0') >= 0 ||
                objectPath.StartsWith("/", StringComparison.Ordinal) ||
                objectPath.EndsWith("/", StringComparison.Ordinal) ||
                objectPath.Split('/').Any(part =>
                    part.Length == 0 ||
                    part == "." ||
                    part == ".."))
            {
                return ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidObjectPath,
                    "image_object_path_invalid");
            }

            var expected = string.Format(
                CultureInfo.InvariantCulture,
                "shops/{0}/products/{1}/primary/{2}/{3}.jpg",
                identity.ShopId.ToString("D").ToLowerInvariant(),
                identity.ProductId.ToString("D").ToLowerInvariant(),
                identity.VersionId.ToString("D").ToLowerInvariant(),
                ProductImageContractV1.VariantName(variant));

            return string.Equals(objectPath, expected, StringComparison.Ordinal)
                ? ProductImageValidationResult.Success()
                : ProductImageValidationResult.Failure(
                    ProductImageValidationCode.InvalidObjectPath,
                    "image_object_path_noncanonical");
        }
    }

    public static class ProductImageInputPolicy
    {
        public static ProductImageInputFormat DetectFormat(byte[] bytes)
        {
            if (bytes == null)
            {
                return ProductImageInputFormat.Unknown;
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xff &&
                bytes[1] == 0xd8 &&
                bytes[2] == 0xff)
            {
                return ProductImageInputFormat.Jpeg;
            }

            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4e &&
                bytes[3] == 0x47 &&
                bytes[4] == 0x0d &&
                bytes[5] == 0x0a &&
                bytes[6] == 0x1a &&
                bytes[7] == 0x0a)
            {
                return ProductImageInputFormat.Png;
            }

            return ProductImageInputFormat.Unknown;
        }

        public static string MimeType(ProductImageInputFormat format)
        {
            switch (format)
            {
                case ProductImageInputFormat.Jpeg:
                    return "image/jpeg";
                case ProductImageInputFormat.Png:
                    return "image/png";
                default:
                    return "application/octet-stream";
            }
        }
    }

    public interface IProductImageStreamProvider
    {
        Task<Stream> OpenReadAsync(
            ProductImageReference reference,
            CancellationToken cancellationToken);
    }

    internal static class ProductImageTextPolicy
    {
        internal static bool IsLowerHex(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length != length)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static class ProductImageHash
    {
        public static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            using (var algorithm = SHA256.Create())
            {
                var digest = algorithm.ComputeHash(bytes);
                var builder = new StringBuilder(digest.Length * 2);
                foreach (var value in digest)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }
    }
}
