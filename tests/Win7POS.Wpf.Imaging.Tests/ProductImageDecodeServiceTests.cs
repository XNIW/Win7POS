using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Images;
using Win7POS.Data.Images;
using Win7POS.Wpf.Products.Images;

namespace Win7POS.Wpf.Imaging.Tests
{
    [TestClass]
    public sealed class ProductImageDecodeServiceTests
    {
        [TestMethod]
        public async Task ListThumbnail_DecodesBoundedFrozenImageAndReleasesSource()
        {
            var bytes = SyntheticProductImages.CreateJpeg(1600, 900);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Main,
                1600,
                900);
            var provider = Provider(reference, bytes);
            var service = new ProductImageDecodeService(
                provider,
                new ProductImageDecodeOptions(
                    listMaximumSide: 128,
                    editorMaximumSide: 512,
                    maximumConcurrency: 2,
                    maximumMemoryEntries: 16));

            var result = await service.DecodeAsync(
                reference,
                ProductImageDecodeProfile.ListThumbnail);

            Assert.IsTrue(result.IsLoaded, result.ErrorCode);
            Assert.IsTrue(result.Image.IsFrozen);
            Assert.IsTrue(result.DecodedWidth <= 128);
            Assert.IsTrue(result.DecodedHeight <= 128);
            Assert.IsTrue(result.DecodedWidth < result.SourceWidth);
            Assert.AreEqual(1, provider.OpenCount);
        }

        [TestMethod]
        public async Task RepeatedAndConcurrentSameKey_UseMemoryAndOneDecodeFlight()
        {
            var bytes = SyntheticProductImages.CreateJpeg(800, 500);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Main,
                800,
                500);
            var provider = Provider(reference, bytes, delayMilliseconds: 50);
            var service = new ProductImageDecodeService(provider);

            var concurrent = await Task.WhenAll(
                Enumerable.Range(0, 24)
                    .Select(_ => service.DecodeAsync(
                        reference,
                        ProductImageDecodeProfile.ListThumbnail)));
            var repeated = await service.DecodeAsync(
                reference,
                ProductImageDecodeProfile.ListThumbnail);

            Assert.IsTrue(concurrent.All(result => result.IsLoaded));
            Assert.AreEqual(1, provider.OpenCount);
            Assert.AreEqual(1, service.DecodeInvocationCount);
            Assert.IsTrue(repeated.FromMemoryCache);
        }

        [TestMethod]
        public async Task SyntheticBatchOf120_RemainsWithinConfiguredBounds()
        {
            var bytes = SyntheticProductImages.CreateJpeg(640, 360);
            var references = Enumerable.Range(1, 120)
                .Select(index => SyntheticProductImages.CreateReference(
                    bytes,
                    ProductImageVariant.Main,
                    640,
                    360,
                    index))
                .ToArray();
            var map = references.ToDictionary(
                reference => ProductImageCacheKey.FromReference(reference).FileStem,
                _ => bytes);
            var provider = new SyntheticStreamProvider(map, delayMilliseconds: 2);
            var service = new ProductImageDecodeService(
                provider,
                new ProductImageDecodeOptions(
                    listMaximumSide: 96,
                    editorMaximumSide: 512,
                    maximumConcurrency: 2,
                    maximumMemoryEntries: 24));

            var timer = Stopwatch.StartNew();
            var results = await Task.WhenAll(references.Select(reference =>
                service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail)));
            timer.Stop();

            Assert.IsTrue(results.All(result => result.IsLoaded));
            Assert.IsTrue(results.All(result =>
                result.DecodedWidth <= 96 &&
                result.DecodedHeight <= 96));
            Assert.IsTrue(service.MaximumObservedConcurrentDecodes <= 2);
            Assert.IsTrue(provider.MaximumActive <= 2);
            Assert.IsTrue(service.MemoryCacheEntryCount <= 24);
            Console.WriteLine(
                "Synthetic thumbnail batch count=120 elapsed_ms={0} max_decode={1} memory_entries={2}",
                timer.ElapsedMilliseconds,
                service.MaximumObservedConcurrentDecodes,
                service.MemoryCacheEntryCount);
        }

        [TestMethod]
        public async Task CorruptBytes_ReturnStateWithoutEscapingException()
        {
            var valid = SyntheticProductImages.CreateJpeg(64, 64);
            var corrupt = (byte[])valid.Clone();
            corrupt[corrupt.Length - 1] = 0x00;
            var reference = SyntheticProductImages.CreateReference(
                corrupt,
                ProductImageVariant.Thumb,
                64,
                64);
            var provider = Provider(reference, corrupt);
            var service = new ProductImageDecodeService(provider);

            var result = await service.DecodeAsync(
                reference,
                ProductImageDecodeProfile.ListThumbnail);

            Assert.AreEqual(ProductImageDisplayState.Corrupt, result.State);
            Assert.IsNull(result.Image);
        }

        [TestMethod]
        public async Task Cancellation_StopsQueuedDistinctRequests()
        {
            var bytes = SyntheticProductImages.CreateJpeg(384, 216);
            var references = Enumerable.Range(1, 8)
                .Select(index => SyntheticProductImages.CreateReference(
                    bytes,
                    ProductImageVariant.Thumb,
                    384,
                    216,
                    index))
                .ToArray();
            var map = references.ToDictionary(
                reference => ProductImageCacheKey.FromReference(reference).FileStem,
                _ => bytes);
            var provider = new SyntheticStreamProvider(map, delayMilliseconds: 100);
            var service = new ProductImageDecodeService(
                provider,
                new ProductImageDecodeOptions(
                    maximumConcurrency: 1));
            var tokens = references
                .Select(_ => new CancellationTokenSource())
                .ToArray();

            var requests = references
                .Select((reference, index) => service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail,
                    tokens[index].Token))
                .ToArray();
            for (var index = 1; index < tokens.Length; index++)
            {
                tokens[index].Cancel();
            }

            var results = await Task.WhenAll(requests);
            foreach (var token in tokens)
            {
                token.Dispose();
            }

            Assert.IsTrue(results.Skip(1).All(result =>
                result.State == ProductImageDisplayState.Unavailable &&
                result.ErrorCode == "image_operation_cancelled"));
            Assert.IsTrue(service.DecodeInvocationCount < references.Length);
            Assert.IsTrue(service.MaximumObservedConcurrentDecodes <= 1);
        }

        [TestMethod]
        public async Task PreCancelledRequest_ReturnsStateWithoutSynchronousException()
        {
            var bytes = SyntheticProductImages.CreateJpeg(64, 64);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                64,
                64);
            var provider = Provider(reference, bytes);
            var service = new ProductImageDecodeService(provider);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();

                var result = await service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail,
                    cancellation.Token);

                Assert.AreEqual(ProductImageDisplayState.Unavailable, result.State);
                Assert.AreEqual("image_operation_cancelled", result.ErrorCode);
                Assert.AreEqual(0, provider.OpenCount);
            }
        }

        [TestMethod]
        public async Task SameKeyCancellation_IsIsolatedPerConsumer()
        {
            var bytes = SyntheticProductImages.CreateJpeg(256, 144);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                256,
                144);
            var provider = Provider(reference, bytes, delayMilliseconds: 100);
            var service = new ProductImageDecodeService(provider);
            using (var firstCancellation = new CancellationTokenSource())
            {
                var cancelledConsumer = service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail,
                    firstCancellation.Token);
                var activeConsumer = service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail);
                firstCancellation.Cancel();

                var cancelled = await cancelledConsumer;
                var loaded = await activeConsumer;

                Assert.AreEqual(
                    ProductImageDisplayState.Unavailable,
                    cancelled.State);
                Assert.AreEqual("image_operation_cancelled", cancelled.ErrorCode);
                Assert.IsTrue(loaded.IsLoaded, loaded.ErrorCode);
                Assert.AreEqual(1, provider.OpenCount);
                Assert.AreEqual(1, service.DecodeInvocationCount);
            }
        }

        [TestMethod]
        public async Task CancelThenImmediateRetry_DoesNotOverlapSameKeyDecode()
        {
            var bytes = SyntheticProductImages.CreateJpeg(256, 144);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                256,
                144);
            var provider = new DelayedCancellationProvider(bytes);
            var service = new ProductImageDecodeService(provider);
            using (var cancellation = new CancellationTokenSource())
            {
                var first = service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail,
                    cancellation.Token);
                Assert.IsTrue(
                    await WaitForSignalAsync(
                        provider.Started.Task,
                        TimeSpan.FromSeconds(5)));
                cancellation.Cancel();
                var cancelled = await first;
                Assert.AreEqual(
                    "image_operation_cancelled",
                    cancelled.ErrorCode);

                var immediateRetry = service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail);
                await Task.Delay(50);
                Assert.AreEqual(1, provider.OpenCount);
                Assert.AreEqual(1, provider.MaximumActive);

                provider.AllowCancelledOpenToExit.TrySetResult(true);
                var joinedCancelledFlight = await immediateRetry;
                Assert.AreEqual(
                    "image_operation_cancelled",
                    joinedCancelledFlight.ErrorCode);
                Assert.IsTrue(
                    await WaitForSignalAsync(
                        provider.FirstExited.Task,
                        TimeSpan.FromSeconds(5)));

                var loaded = await service.DecodeAsync(
                    reference,
                    ProductImageDecodeProfile.ListThumbnail);
                Assert.IsTrue(loaded.IsLoaded, loaded.ErrorCode);
                Assert.AreEqual(2, provider.OpenCount);
                Assert.AreEqual(1, provider.MaximumActive);
            }
        }

        [TestMethod]
        public async Task StreamOpenAndReadPipeline_StartsOffCallerThread()
        {
            var bytes = SyntheticProductImages.CreateJpeg(128, 72);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                128,
                72);
            var provider = Provider(reference, bytes);
            var service = new ProductImageDecodeService(provider);
            var callerThread = Thread.CurrentThread.ManagedThreadId;

            var result = await service.DecodeAsync(
                reference,
                ProductImageDecodeProfile.ListThumbnail);

            Assert.IsTrue(result.IsLoaded, result.ErrorCode);
            Assert.AreNotEqual(callerThread, provider.OpenThreadId);
        }

        [TestMethod]
        public async Task UndecodableStagedVersion_DoesNotRemoveValidPreviousEntry()
        {
            var root = SyntheticProductImages.CreateTempDirectory();
            try
            {
                var oldBytes = SyntheticProductImages.CreateJpeg(128, 96);
                var badBytes =
                    SyntheticProductImages.CreateStructurallyValidUndecodableJpeg();
                var oldReference = SyntheticProductImages.CreateReference(
                    oldBytes,
                    ProductImageVariant.Thumb,
                    128,
                    96,
                    ordinal: 31,
                    imageUpdatedAt: DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
                var badReference = SyntheticProductImages.CreateReference(
                    badBytes,
                    ProductImageVariant.Thumb,
                    1,
                    1,
                    ordinal: 32,
                    imageUpdatedAt: DateTimeOffset.Parse("2026-07-30T12:01:00Z"));
                using (var cache = new ProductImageDiskCache(
                           new ProductImageCacheOptions(root)))
                {
                    await cache.GetOrAddAsync(
                        oldReference,
                        _ => Task.FromResult<Stream>(
                            new MemoryStream(oldBytes, false)));
                    await cache.PromoteVariantAsync(oldReference);
                    await cache.GetOrAddAsync(
                        badReference,
                        _ => Task.FromResult<Stream>(
                            new MemoryStream(badBytes, false)));
                    var decoder = new ProductImageDecodeService(
                        new ProductImageDiskCacheStreamProvider(cache));

                    var badResult = await decoder.DecodeAsync(
                        badReference,
                        ProductImageDecodeProfile.ListThumbnail);
                    var oldResult = await decoder.DecodeAsync(
                        oldReference,
                        ProductImageDecodeProfile.ListThumbnail);

                    Assert.AreEqual(
                        ProductImageDisplayState.Corrupt,
                        badResult.State);
                    Assert.IsTrue(oldResult.IsLoaded, oldResult.ErrorCode);
                    Assert.IsNotNull(await cache.GetAsync(oldReference));
                }
            }
            finally
            {
                SyntheticProductImages.DeleteTempDirectory(root);
            }
        }

        [TestMethod]
        public async Task UndefinedDecodeProfile_ReturnsDeterministicError()
        {
            var bytes = SyntheticProductImages.CreateJpeg(64, 64);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                64,
                64);
            var provider = Provider(reference, bytes);
            var service = new ProductImageDecodeService(provider);

            var result = await service.DecodeAsync(
                reference,
                (ProductImageDecodeProfile)99);

            Assert.AreEqual(ProductImageDisplayState.Error, result.State);
            Assert.AreEqual("image_decode_profile_invalid", result.ErrorCode);
            Assert.AreEqual(0, provider.OpenCount);
        }

        [TestMethod]
        public async Task MemoryReferences_CanBeExplicitlyReleased()
        {
            var bytes = SyntheticProductImages.CreateJpeg(128, 96);
            var reference = SyntheticProductImages.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                128,
                96);
            var service = new ProductImageDecodeService(
                Provider(reference, bytes));
            Assert.IsTrue((await service.DecodeAsync(
                reference,
                ProductImageDecodeProfile.ListThumbnail)).IsLoaded);
            Assert.AreEqual(1, service.MemoryCacheEntryCount);

            service.TrimMemoryCache();

            Assert.AreEqual(0, service.MemoryCacheEntryCount);
        }

        private static SyntheticStreamProvider Provider(
            ProductImageReference reference,
            byte[] bytes,
            int delayMilliseconds = 0)
        {
            return new SyntheticStreamProvider(
                new Dictionary<string, byte[]>
                {
                    {
                        ProductImageCacheKey.FromReference(reference).FileStem,
                        bytes
                    }
                },
                delayMilliseconds);
        }

        private static async Task<bool> WaitForSignalAsync(
            Task<bool> signal,
            TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal, Task.Delay(timeout));
            Assert.AreSame(signal, completed, "Timed out waiting for test signal.");
            return await signal;
        }

        private sealed class DelayedCancellationProvider :
            IProductImageStreamProvider
        {
            private readonly byte[] _bytes;
            private int _openCount;
            private int _active;
            private int _maximumActive;

            internal DelayedCancellationProvider(byte[] bytes)
            {
                _bytes = bytes;
            }

            internal TaskCompletionSource<bool> Started { get; } =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<bool> AllowCancelledOpenToExit { get; } =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<bool> FirstExited { get; } =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal int OpenCount => Volatile.Read(ref _openCount);
            internal int MaximumActive => Volatile.Read(ref _maximumActive);

            public async Task<Stream> OpenReadAsync(
                ProductImageReference reference,
                CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _openCount);
                var active = Interlocked.Increment(ref _active);
                UpdateMaximum(active);
                try
                {
                    if (call == 1)
                    {
                        Started.TrySetResult(true);
                        try
                        {
                            await Task.Delay(
                                    Timeout.InfiniteTimeSpan,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            await AllowCancelledOpenToExit.Task
                                .ConfigureAwait(false);
                            throw;
                        }
                        finally
                        {
                            FirstExited.TrySetResult(true);
                        }
                    }

                    return new MemoryStream(_bytes, writable: false);
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
}
