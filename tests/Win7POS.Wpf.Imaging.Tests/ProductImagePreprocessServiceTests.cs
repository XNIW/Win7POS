using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Images;
using Win7POS.Wpf.Products.Images;

namespace Win7POS.Wpf.Imaging.Tests
{
    [TestClass]
    public sealed class ProductImagePreprocessServiceTests
    {
        [TestMethod]
        public async Task JpegInput_ProducesBoundedCanonicalVariantsWithoutUpscale()
        {
            var source = SyntheticProductImages.CreateJpeg(320, 180);
            var service = new ProductImagePreprocessService();

            var result = await service.PreprocessAsync(
                new MemoryStream(source, false),
                "synthetic.jpg");

            Assert.IsTrue(result.IsSuccess, Issues(result));
            Assert.AreEqual("image/jpeg", result.Original.MimeType);
            Assert.AreEqual(320, result.Original.Width);
            Assert.AreEqual(180, result.Original.Height);
            Assert.AreEqual(320, result.Main.Metadata.Width);
            Assert.AreEqual(180, result.Main.Metadata.Height);
            Assert.IsTrue(result.Thumb.Metadata.Width <= 320);
            Assert.IsTrue(result.Thumb.Metadata.Height <= 180);
            Assert.IsTrue(result.Main.ByteSize <= ProductImageContractV1.MainMaximumBytes);
            Assert.IsTrue(result.Thumb.ByteSize <= ProductImageContractV1.ThumbMaximumBytes);
            Assert.IsFalse(ProductImageBinaryPolicy.HasForbiddenJpegMetadata(
                result.Main.CopyBytes()));
            Assert.IsFalse(ProductImageBinaryPolicy.HasForbiddenJpegMetadata(
                result.Thumb.CopyBytes()));
        }

        [TestMethod]
        public async Task PngMagicOverridesWrongExtensionAndFlattensToJpeg()
        {
            var source = SyntheticProductImages.CreatePng(480, 270);
            var service = new ProductImagePreprocessService();

            var result = await service.PreprocessAsync(
                new MemoryStream(source, false),
                "synthetic.jpg");

            Assert.IsTrue(result.IsSuccess, Issues(result));
            Assert.AreEqual("image/png", result.Original.MimeType);
            Assert.IsTrue(result.Issues.Any(issue =>
                issue.Code == "image_extension_mismatch" &&
                !issue.IsError));
            Assert.AreEqual("image/jpeg", result.Main.Metadata.MimeType);
            Assert.AreEqual(ProductImageInputFormat.Jpeg,
                ProductImageInputPolicy.DetectFormat(result.Main.CopyBytes()));
        }

        [TestMethod]
        public async Task FileInput_DoesNotOverwriteOrLockOriginal()
        {
            var root = SyntheticProductImages.CreateTempDirectory();
            try
            {
                var path = Path.Combine(root, "synthetic.png");
                var source = SyntheticProductImages.CreatePng(256, 144);
                File.WriteAllBytes(path, source);
                var before = ProductImageHash.Sha256Hex(source);
                var service = new ProductImagePreprocessService();

                var result = await service.PreprocessFileAsync(path);
                using (new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                }

                Assert.IsTrue(result.IsSuccess, Issues(result));
                Assert.AreEqual(
                    before,
                    ProductImageHash.Sha256Hex(File.ReadAllBytes(path)));
            }
            finally
            {
                SyntheticProductImages.DeleteTempDirectory(root);
            }
        }

        [TestMethod]
        public async Task LargeSyntheticInput_IsDecodedAndOutputWithinHardBounds()
        {
            var source = SyntheticProductImages.CreateJpeg(2400, 1800);
            var service = new ProductImagePreprocessService();
            var timer = Stopwatch.StartNew();

            var result = await service.PreprocessAsync(
                new MemoryStream(source, false),
                "large-synthetic.jpeg");
            timer.Stop();

            Assert.IsTrue(result.IsSuccess, Issues(result));
            Assert.AreEqual(2400, result.Original.Width);
            Assert.AreEqual(1800, result.Original.Height);
            Assert.IsTrue(result.Main.Metadata.Width <= ProductImageContractV1.MainMaximumSide);
            Assert.IsTrue(result.Main.Metadata.Height <= ProductImageContractV1.MainMaximumSide);
            Assert.IsTrue(result.Thumb.Metadata.Width <= ProductImageContractV1.ThumbMaximumSide);
            Assert.IsTrue(result.Thumb.Metadata.Height <= ProductImageContractV1.ThumbMaximumSide);
            Assert.IsTrue(result.Main.ByteSize <= ProductImageContractV1.MainMaximumBytes);
            Assert.IsTrue(result.Thumb.ByteSize <= ProductImageContractV1.ThumbMaximumBytes);
            Console.WriteLine(
                "Synthetic 2400x1800 preprocess elapsed_ms={0}; main={1} bytes; thumb={2} bytes",
                timer.ElapsedMilliseconds,
                result.Main.ByteSize,
                result.Thumb.ByteSize);
        }

        [TestMethod]
        public async Task CorruptAndCancelledInput_ReturnDeterministicFailure()
        {
            var service = new ProductImagePreprocessService();
            var corrupt = await service.PreprocessAsync(
                new MemoryStream(new byte[] { 1, 2, 3 }, false),
                "synthetic.jpg");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var cancelled = await service.PreprocessAsync(
                    new MemoryStream(
                        SyntheticProductImages.CreateJpeg(64, 64),
                        false),
                    "synthetic.jpg",
                    cancellation.Token);

                Assert.IsFalse(corrupt.IsSuccess);
                Assert.IsTrue(corrupt.Issues.Any(issue => issue.IsError));
                Assert.IsFalse(cancelled.IsSuccess);
                Assert.IsTrue(cancelled.Issues.Any(issue =>
                    issue.Code == "image_operation_cancelled"));
            }
        }

        [TestMethod]
        public async Task SourceRead_RunsOffCallerThread()
        {
            var bytes = SyntheticProductImages.CreatePng(128, 72);
            var callerThread = Thread.CurrentThread.ManagedThreadId;
            using (var stream = new ThreadRecordingStream(bytes))
            {
                var result = await new ProductImagePreprocessService()
                    .PreprocessAsync(stream, "synthetic.png");

                Assert.IsTrue(result.IsSuccess, Issues(result));
                Assert.AreNotEqual(callerThread, stream.ReadThreadId);
            }
        }

        [TestMethod]
        public async Task Win7PixelCap_RejectsLargeHeaderBeforeWicDecode()
        {
            var headerOnlyPng = new byte[24]
            {
                0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
                0x00, 0x00, 0x00, 0x0d,
                0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x13, 0x88,
                0x00, 0x00, 0x0f, 0xa0
            };

            var result = await new ProductImagePreprocessService()
                .PreprocessAsync(
                    new MemoryStream(headerOnlyPng, false),
                    "synthetic.png");

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(result.Issues.Any(issue =>
                issue.Code == "image_dimensions_invalid"));
        }

        private static string Issues(ProductImagePreprocessResult result)
        {
            return string.Join(
                "; ",
                result.Issues.Select(issue => issue.Code + ":" + issue.Message));
        }

        private sealed class ThreadRecordingStream : MemoryStream
        {
            internal ThreadRecordingStream(byte[] bytes)
                : base(bytes, false)
            {
            }

            internal int ReadThreadId { get; private set; }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                ReadThreadId = Thread.CurrentThread.ManagedThreadId;
                return base.ReadAsync(
                    buffer,
                    offset,
                    count,
                    cancellationToken);
            }
        }
    }
}
