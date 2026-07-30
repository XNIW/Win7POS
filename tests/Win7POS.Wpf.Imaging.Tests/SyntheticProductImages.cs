using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Images;

namespace Win7POS.Wpf.Imaging.Tests
{
    internal static class SyntheticProductImages
    {
        private const string AccountScope =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        internal static byte[] CreateJpeg(int width, int height, int quality = 82)
        {
            return OnStaThread(() =>
            {
                var bitmap = CreateBitmap(width, height);
                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return ProductImageBinaryPolicy.RemoveForbiddenJpegMetadata(
                        stream.ToArray());
                }
            });
        }

        internal static byte[] CreatePng(int width, int height)
        {
            return OnStaThread(() =>
            {
                var bitmap = CreateBitmap(width, height);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            });
        }

        internal static ProductImageReference CreateReference(
            byte[] bytes,
            ProductImageVariant variant,
            int width,
            int height,
            int ordinal = 1,
            DateTimeOffset? imageUpdatedAt = null)
        {
            var productId = Guid.Parse(
                string.Format(
                    "50000000-0000-4000-8000-{0:D12}",
                    ordinal));
            var versionId = Guid.Parse(
                string.Format(
                    "60000000-0000-4000-8000-{0:D12}",
                    ordinal));
            ProductImageIdentity identity;
            ProductImageValidationResult identityValidation;
            Assert.IsTrue(ProductImageIdentity.TryCreate(
                AccountScope,
                "11111111-1111-4111-8111-111111111111",
                productId.ToString("D"),
                versionId.ToString("D"),
                out identity,
                out identityValidation),
                string.Join(",", identityValidation.Messages));
            ProductImageMetadata metadata;
            ProductImageValidationResult metadataValidation;
            Assert.IsTrue(ProductImageMetadata.TryCreate(
                variant,
                ProductImageContractV1.WireMimeType,
                bytes.Length,
                width,
                height,
                ProductImageHash.Sha256Hex(bytes),
                out metadata,
                out metadataValidation),
                string.Join(",", metadataValidation.Messages));
            return new ProductImageReference(
                identity,
                variant,
                metadata,
                imageUpdatedAt ?? DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
        }

        internal static byte[] CreateStructurallyValidUndecodableJpeg()
        {
            return new byte[]
            {
                0xff, 0xd8,
                0xff, 0xe0, 0x00, 0x10,
                0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,
                0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0xff, 0xc0, 0x00, 0x11, 0x00,
                0x00, 0x01, 0x00, 0x01, 0x00,
                0x01, 0x11, 0x00,
                0x02, 0x11, 0x00,
                0x03, 0x11, 0x00,
                0xff, 0xda, 0x00, 0x0c, 0x03,
                0x01, 0x00,
                0x02, 0x00,
                0x03, 0x00,
                0x00, 0x3f, 0x00,
                0x00,
                0xff, 0xd9
            };
        }

        internal static string CreateTempDirectory()
        {
            var root = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "Win7POS-product-image-wpf-tests",
                Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            return root;
        }

        internal static void DeleteTempDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var allowedRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "Win7POS-product-image-wpf-tests"));
            Assert.IsTrue(fullPath.StartsWith(
                allowedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, true);
            }
        }

        private static BitmapSource CreateBitmap(int width, int height)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(
                    Brushes.White,
                    null,
                    new Rect(0, 0, width, height));
                drawing.DrawRectangle(
                    Brushes.SteelBlue,
                    null,
                    new Rect(0, 0, width / 2.0, height));
                drawing.DrawEllipse(
                    Brushes.Goldenrod,
                    null,
                    new Point(width * 0.7, height * 0.45),
                    Math.Max(1, width * 0.18),
                    Math.Max(1, height * 0.3));
            }

            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static T OnStaThread<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task.GetAwaiter().GetResult();
        }
    }

    internal sealed class SyntheticStreamProvider : IProductImageStreamProvider
    {
        private readonly IReadOnlyDictionary<string, byte[]> _bytes;
        private readonly int _delayMilliseconds;
        private int _openCount;
        private int _active;
        private int _maximumActive;
        private int _openThreadId;

        internal SyntheticStreamProvider(
            IReadOnlyDictionary<string, byte[]> bytes,
            int delayMilliseconds = 0)
        {
            _bytes = bytes;
            _delayMilliseconds = delayMilliseconds;
        }

        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int MaximumActive => Volatile.Read(ref _maximumActive);
        internal int OpenThreadId => Volatile.Read(ref _openThreadId);

        public async Task<Stream> OpenReadAsync(
            ProductImageReference reference,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref _openThreadId, Thread.CurrentThread.ManagedThreadId);
            Interlocked.Increment(ref _openCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                if (_delayMilliseconds > 0)
                {
                    await Task.Delay(_delayMilliseconds, cancellationToken)
                        .ConfigureAwait(false);
                }

                var key = ProductImageCacheKey.FromReference(reference).FileStem;
                byte[] bytes;
                if (!_bytes.TryGetValue(key, out bytes))
                {
                    throw new FileNotFoundException();
                }

                return new MemoryStream(bytes, false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maximumActive);
                if (active <= observed ||
                    Interlocked.CompareExchange(
                        ref _maximumActive,
                        active,
                        observed) == observed)
                {
                    return;
                }
            }
        }
    }
}
