using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win7POS.Core.Images;

namespace Win7POS.Wpf.Products.Images
{
    public sealed class ProductImagePreprocessService
    {
        private static readonly double[] SideFactors =
        {
            1.0, 0.85, 0.72, 0.61, 0.52, 0.44, 0.4
        };

        private static readonly int[] MainQualities = { 82, 76, 70 };
        private static readonly int[] ThumbQualities = { 75, 68, 60, 52 };
        private readonly ProductImagePreprocessOptions _options;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public ProductImagePreprocessService(
            ProductImagePreprocessOptions options = null)
        {
            _options = options ?? new ProductImagePreprocessOptions();
        }

        public async Task<ProductImagePreprocessResult> PreprocessFileAsync(
            string filePath,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return Failure("image_input_missing", "No source image was selected.");
            }

            try
            {
                using (var stream = new FileStream(
                           filePath,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           81920,
                           FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    return await PreprocessAsync(
                            stream,
                            Path.GetFileName(filePath),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return Failure(
                    "image_operation_cancelled",
                    "Image processing was cancelled.");
            }
            catch (Exception)
            {
                return Failure(
                    "image_decode_failed",
                    "The selected image could not be read.");
            }
        }

        public async Task<ProductImagePreprocessResult> PreprocessAsync(
            Stream source,
            string sourceName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null || !source.CanRead)
            {
                return Failure("image_input_missing", "No readable source image was provided.");
            }

            var entered = false;
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                return await Task.Run(
                        async () =>
                        {
                            var bytes = await ReadBoundedAsync(
                                    source,
                                    _options.MaximumSourceBytes,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();

                            var inspection = ProductImageBinaryPolicy.Inspect(
                                bytes,
                                _options.MaximumSourceBytes,
                                _options.MaximumSourcePixels,
                                out var header);
                            if (!inspection.IsValid)
                            {
                                return Failure(
                                    inspection.Messages.FirstOrDefault() ??
                                    "image_decode_failed",
                                    "The selected image is unsupported, corrupt, or outside the configured limits.");
                            }

                            return Process(
                                bytes,
                                sourceName,
                                header,
                                cancellationToken);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure(
                    "image_operation_cancelled",
                    "Image processing was cancelled.");
            }
            catch (InvalidDataException error)
            {
                return Failure(
                    string.IsNullOrWhiteSpace(error.Message)
                        ? "image_decode_failed"
                        : error.Message,
                    "The selected image could not be processed safely.");
            }
            catch (Exception)
            {
                return Failure(
                    "image_decode_failed",
                    "The selected image could not be processed safely.");
            }
            finally
            {
                if (entered)
                {
                    _gate.Release();
                }
            }
        }

        private static ProductImagePreprocessResult Process(
            byte[] bytes,
            string sourceName,
            ProductImageHeader header,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = new ProductImageOriginalMetadata(
                bytes.Length,
                header.Width,
                header.Height,
                ProductImageInputPolicy.MimeType(header.Format),
                header.Orientation,
                Path.GetExtension(sourceName ?? string.Empty));
            var warnings = ExtensionWarnings(sourceName, header.Format).ToList();

            var source = DecodeBoundedSource(
                bytes,
                header,
                ProductImageContractV1.MainMaximumSide);
            cancellationToken.ThrowIfCancellationRequested();
            var oriented = ApplyOrientation(source, header.Orientation);
            var normalizedMain = RenderOpaque(
                oriented,
                ProductImageContractV1.MainMaximumSide);
            cancellationToken.ThrowIfCancellationRequested();

            var main = EncodeWithinBudget(
                normalizedMain,
                ProductImageVariant.Main,
                ProductImageContractV1.MainMinimumSide,
                MainQualities,
                ProductImageContractV1.MainTargetBytes,
                ProductImageContractV1.MainMaximumBytes,
                cancellationToken);
            var thumb = EncodeWithinBudget(
                normalizedMain,
                ProductImageVariant.Thumb,
                ProductImageContractV1.ThumbMinimumSide,
                ThumbQualities,
                ProductImageContractV1.ThumbTargetBytes,
                ProductImageContractV1.ThumbMaximumBytes,
                cancellationToken);
            return ProductImagePreprocessResult.Success(
                original,
                main,
                thumb,
                warnings);
        }

        private static BitmapSource DecodeBoundedSource(
            byte[] bytes,
            ProductImageHeader header,
            int maximumSide)
        {
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // Keep WIC color management enabled so embedded ICC profiles are
                // converted before the canonical metadata-free JPEG is encoded.
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                if (header.Width >= header.Height)
                {
                    bitmap.DecodePixelWidth = Math.Min(header.Width, maximumSide);
                }
                else
                {
                    bitmap.DecodePixelHeight = Math.Min(header.Height, maximumSide);
                }

                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private static BitmapSource ApplyOrientation(
            BitmapSource source,
            int orientation)
        {
            Transform transform;
            switch (orientation)
            {
                case 2:
                    transform = new ScaleTransform(-1, 1);
                    break;
                case 3:
                    transform = new RotateTransform(180);
                    break;
                case 4:
                    transform = new ScaleTransform(1, -1);
                    break;
                case 5:
                    transform = CreateTransformGroup(
                        new RotateTransform(90),
                        new ScaleTransform(-1, 1));
                    break;
                case 6:
                    transform = new RotateTransform(90);
                    break;
                case 7:
                    transform = CreateTransformGroup(
                        new RotateTransform(270),
                        new ScaleTransform(-1, 1));
                    break;
                case 8:
                    transform = new RotateTransform(270);
                    break;
                default:
                    return source;
            }

            var transformed = new TransformedBitmap(source, transform);
            transformed.Freeze();
            return transformed;
        }

        private static Transform CreateTransformGroup(
            params Transform[] transforms)
        {
            var group = new TransformGroup();
            foreach (var transform in transforms)
            {
                group.Children.Add(transform);
            }

            group.Freeze();
            return group;
        }

        private static BitmapSource RenderOpaque(
            BitmapSource source,
            int maximumSide)
        {
            var scale = Math.Min(
                1.0,
                maximumSide / (double)Math.Max(source.PixelWidth, source.PixelHeight));
            var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                drawing.DrawImage(source, new Rect(0, 0, width, height));
            }

            var target = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }

        private static ProductImageProcessedVariant EncodeWithinBudget(
            BitmapSource source,
            ProductImageVariant variant,
            int minimumSide,
            int[] qualities,
            int targetBytes,
            int hardMaximumBytes,
            CancellationToken cancellationToken)
        {
            ProductImageProcessedVariant fallback = null;
            var initialMaximum = ProductImageContractV1.MaximumSide(variant);
            foreach (var side in OutputSideSchedule(
                         Math.Max(source.PixelWidth, source.PixelHeight),
                         initialMaximum,
                         minimumSide))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bitmap = RenderOpaque(source, side);
                foreach (var quality in qualities)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = EncodeCanonicalJpeg(bitmap, quality);
                    if (!ProductImageMetadata.TryCreate(
                            variant,
                            ProductImageContractV1.WireMimeType,
                            bytes.Length,
                            bitmap.PixelWidth,
                            bitmap.PixelHeight,
                            ProductImageHash.Sha256Hex(bytes),
                            out var metadata,
                            out _))
                    {
                        continue;
                    }

                    var candidate = new ProductImageProcessedVariant(
                        variant,
                        bytes,
                        metadata);
                    if (candidate.ByteSize <= hardMaximumBytes &&
                        (fallback == null || candidate.ByteSize < fallback.ByteSize))
                    {
                        fallback = candidate;
                    }

                    if (candidate.ByteSize <= targetBytes)
                    {
                        return candidate;
                    }
                }
            }

            return fallback ??
                   throw new InvalidDataException("image_output_budget_exceeded");
        }

        private static byte[] EncodeCanonicalJpeg(
            BitmapSource source,
            int quality)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                return ProductImageBinaryPolicy.RemoveForbiddenJpegMetadata(
                    output.ToArray());
            }
        }

        private static IReadOnlyList<int> OutputSideSchedule(
            int sourceLongestSide,
            int initialMaximum,
            int minimum)
        {
            var maximum = Math.Min(sourceLongestSide, initialMaximum);
            if (maximum <= minimum || sourceLongestSide < minimum)
            {
                return new[] { maximum };
            }

            return SideFactors
                .Select(factor => Math.Max(minimum, (int)Math.Floor(maximum * factor)))
                .Concat(new[] { minimum })
                .Where(side => side <= maximum)
                .Distinct()
                .ToArray();
        }

        private static IEnumerable<ProductImagePreprocessIssue> ExtensionWarnings(
            string sourceName,
            ProductImageInputFormat format)
        {
            var extension = Path.GetExtension(sourceName ?? string.Empty)
                .ToLowerInvariant();
            var matches = format == ProductImageInputFormat.Jpeg
                ? extension == ".jpg" || extension == ".jpeg"
                : format == ProductImageInputFormat.Png && extension == ".png";
            if (!matches)
            {
                yield return new ProductImagePreprocessIssue(
                    "image_extension_mismatch",
                    "The file extension did not match the decoded image format; the decoded format was used.",
                    isError: false);
            }
        }

        private static async Task<byte[]> ReadBoundedAsync(
            Stream source,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            using (var output = new MemoryStream(Math.Min(maximumBytes, 1024 * 1024)))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await source
                        .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (output.Length + read > maximumBytes)
                    {
                        throw new InvalidDataException("image_input_size_invalid");
                    }

                    await output
                        .WriteAsync(buffer, 0, read, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (output.Length == 0)
                {
                    throw new InvalidDataException("image_input_size_invalid");
                }

                return output.ToArray();
            }
        }

        private static ProductImagePreprocessResult Failure(
            string code,
            string message)
        {
            return ProductImagePreprocessResult.Failure(
                null,
                new ProductImagePreprocessIssue(code, message, isError: true));
        }
    }
}
