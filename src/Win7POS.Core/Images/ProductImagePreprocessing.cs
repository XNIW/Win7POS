using System;
using System.Collections.Generic;
using System.Linq;

namespace Win7POS.Core.Images
{
    public sealed class ProductImagePreprocessOptions
    {
        // The shared wire contract accepts up to 64 MP. Win7POS x86 uses a
        // lower default because legacy WIC codecs may allocate the source
        // surface before honoring decode scaling.
        public const long Win7DefaultMaximumSourcePixels = 16_000_000L;

        public ProductImagePreprocessOptions(
            int maximumSourceBytes = ProductImageContractV1.InputMaximumBytes,
            long maximumSourcePixels = Win7DefaultMaximumSourcePixels)
        {
            if (maximumSourceBytes < ProductImageContractV1.MainMaximumBytes ||
                maximumSourceBytes > ProductImageContractV1.InputMaximumBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));
            }

            if (maximumSourcePixels < ProductImageContractV1.MainMaximumSide ||
                maximumSourcePixels > ProductImageContractV1.InputMaximumPixels)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumSourcePixels));
            }

            MaximumSourceBytes = maximumSourceBytes;
            MaximumSourcePixels = maximumSourcePixels;
        }

        public int MaximumSourceBytes { get; }
        public long MaximumSourcePixels { get; }
    }

    public sealed class ProductImageOriginalMetadata
    {
        public ProductImageOriginalMetadata(
            int byteSize,
            int width,
            int height,
            string mimeType,
            int orientation,
            string sourceExtension)
        {
            ByteSize = byteSize;
            Width = width;
            Height = height;
            MimeType = mimeType ?? string.Empty;
            Orientation = orientation;
            SourceExtension = sourceExtension ?? string.Empty;
        }

        public int ByteSize { get; }
        public int Width { get; }
        public int Height { get; }
        public string MimeType { get; }
        public int Orientation { get; }
        public string SourceExtension { get; }
    }

    public sealed class ProductImageProcessedVariant
    {
        private readonly byte[] _bytes;

        public ProductImageProcessedVariant(
            ProductImageVariant variant,
            byte[] bytes,
            ProductImageMetadata metadata)
        {
            Variant = variant;
            _bytes = bytes == null
                ? throw new ArgumentNullException(nameof(bytes))
                : (byte[])bytes.Clone();
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        public ProductImageVariant Variant { get; }
        public ProductImageMetadata Metadata { get; }
        public int ByteSize => _bytes.Length;

        public byte[] CopyBytes()
        {
            return (byte[])_bytes.Clone();
        }
    }

    public sealed class ProductImagePreprocessIssue
    {
        public ProductImagePreprocessIssue(
            string code,
            string message,
            bool isError)
        {
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            IsError = isError;
        }

        public string Code { get; }
        public string Message { get; }
        public bool IsError { get; }
    }

    public sealed class ProductImagePreprocessResult
    {
        private ProductImagePreprocessResult(
            ProductImageOriginalMetadata original,
            ProductImageProcessedVariant main,
            ProductImageProcessedVariant thumb,
            IReadOnlyList<ProductImagePreprocessIssue> issues)
        {
            Original = original;
            Main = main;
            Thumb = thumb;
            Issues = issues ?? new ProductImagePreprocessIssue[0];
        }

        public ProductImageOriginalMetadata Original { get; }
        public ProductImageProcessedVariant Main { get; }
        public ProductImageProcessedVariant Thumb { get; }
        public IReadOnlyList<ProductImagePreprocessIssue> Issues { get; }
        public bool IsSuccess =>
            Main != null &&
            Thumb != null &&
            !Issues.Any(issue => issue.IsError);

        public static ProductImagePreprocessResult Success(
            ProductImageOriginalMetadata original,
            ProductImageProcessedVariant main,
            ProductImageProcessedVariant thumb,
            IEnumerable<ProductImagePreprocessIssue> warnings = null)
        {
            return new ProductImagePreprocessResult(
                original,
                main,
                thumb,
                (warnings ?? Enumerable.Empty<ProductImagePreprocessIssue>()).ToArray());
        }

        public static ProductImagePreprocessResult Failure(
            ProductImageOriginalMetadata original,
            params ProductImagePreprocessIssue[] issues)
        {
            return new ProductImagePreprocessResult(
                original,
                null,
                null,
                (issues ?? new ProductImagePreprocessIssue[0]).ToArray());
        }
    }
}
