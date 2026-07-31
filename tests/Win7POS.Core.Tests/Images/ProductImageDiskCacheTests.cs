using System.Collections.Concurrent;
using Win7POS.Core.Images;
using Win7POS.Data.Images;

namespace Win7POS.Core.Tests.Images;

[TestClass]
public sealed class ProductImageDiskCacheTests
{
    [TestMethod]
    public async Task SameKeyRequests_AreCoalesced()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var reference = ProductImageTestData.CreateReference(bytes);
            using var cache = CreateCache(root);
            var factoryCalls = 0;
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Stream> Factory(CancellationToken token)
            {
                Interlocked.Increment(ref factoryCalls);
                await release.Task.WaitAsync(token);
                return new MemoryStream(bytes, writable: false);
            }

            var first = cache.GetOrAddAsync(reference, Factory);
            var second = cache.GetOrAddAsync(reference, Factory);
            release.SetResult(true);
            var results = await Task.WhenAll(first, second);

            Assert.AreEqual(1, factoryCalls);
            CollectionAssert.AreEqual(results[0].CopyBytes(), results[1].CopyBytes());
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task EntryBudget_EvictsLeastRecentlyUsedAndAccountsBytes()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = new ProductImageDiskCache(
                new ProductImageCacheOptions(
                    root,
                    maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
                    maximumEntries: 2));
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var first = ProductImageTestData.CreateReference(
                bytes,
                productId: Guid.Parse("10000000-0000-4000-8000-000000000001"));
            var second = ProductImageTestData.CreateReference(
                bytes,
                productId: Guid.Parse("10000000-0000-4000-8000-000000000002"));
            var third = ProductImageTestData.CreateReference(
                bytes,
                productId: Guid.Parse("10000000-0000-4000-8000-000000000003"));

            await AddAsync(cache, first, bytes);
            await AddAsync(cache, second, bytes);
            Assert.IsNotNull(await cache.GetAsync(first));
            await AddAsync(cache, third, bytes);

            Assert.IsNotNull(await cache.GetAsync(first));
            Assert.IsNull(await cache.GetAsync(second));
            Assert.IsNotNull(await cache.GetAsync(third));
            var snapshot = await cache.GetSnapshotAsync();
            Assert.AreEqual(2, snapshot.EntryCount);
            Assert.AreEqual(AccountedBytes(root), snapshot.TotalBytes);
            Assert.IsTrue(snapshot.TotalBytes > 2L * bytes.Length);
            Assert.IsTrue(
                snapshot.TotalBytes <= ProductImageCacheOptions.MinimumMaximumBytes);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task ReplacementInvalidation_RemovesOnlyOldVersions()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var oldReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("20000000-0000-4000-8000-000000000001"));
            var newReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("20000000-0000-4000-8000-000000000002"));
            await AddAsync(cache, oldReference, bytes);
            await AddAsync(cache, newReference, bytes);

            Assert.IsNotNull(await cache.GetAsync(oldReference));
            Assert.IsNotNull(await cache.GetAsync(newReference));
            await cache.PromoteVariantAsync(newReference);

            Assert.IsNull(await cache.GetAsync(oldReference));
            Assert.IsNotNull(await cache.GetAsync(newReference));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task FailedReplacement_DoesNotPoisonPreviousEntry()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var oldReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("30000000-0000-4000-8000-000000000001"));
            var nextReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("30000000-0000-4000-8000-000000000002"));
            await AddAsync(cache, oldReference, bytes);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await cache.GetOrAddAsync(
                    nextReference,
                    _ => Task.FromResult<Stream>(
                        new MemoryStream(new byte[] { 1, 2, 3 }))));

            CollectionAssert.AreEqual(
                bytes,
                (await cache.GetAsync(oldReference))!.CopyBytes());
            Assert.IsNull(await cache.GetAsync(nextReference));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task SaturatedCache_AdmitsReplacementWithoutEvictingFallback()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = new ProductImageDiskCache(
                new ProductImageCacheOptions(
                    root,
                    maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
                    maximumEntries: 2));
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var unrelated = ProductImageTestData.CreateReference(
                bytes,
                productId: Guid.Parse("30500000-0000-4000-8000-000000000010"));
            var oldReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("30500000-0000-4000-8000-000000000001"),
                imageUpdatedAt: DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
            var stagedReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("30500000-0000-4000-8000-000000000002"),
                imageUpdatedAt: DateTimeOffset.Parse("2026-07-30T12:01:00Z"));
            await AddAsync(cache, unrelated, bytes);
            await cache.PromoteVariantAsync(unrelated);
            await AddAsync(cache, oldReference, bytes);
            await cache.PromoteVariantAsync(oldReference);

            await AddAsync(cache, stagedReference, bytes);

            Assert.IsNotNull(await cache.GetAsync(oldReference));
            Assert.IsNotNull(await cache.GetAsync(stagedReference));
            Assert.IsNull(await cache.GetAsync(unrelated));
            Assert.AreEqual(2, (await cache.GetSnapshotAsync()).EntryCount);

            await cache.PromoteVariantAsync(stagedReference);
            Assert.IsNull(await cache.GetAsync(oldReference));
            Assert.IsNotNull(await cache.GetAsync(stagedReference));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task CorruptIndexAndRestart_RebuildFromCommittedMetadata()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var reference = ProductImageTestData.CreateReference(bytes);
            using (var initial = CreateCache(root))
            {
                await AddAsync(initial, reference, bytes);
            }
            File.WriteAllText(Path.Combine(root, "index-v1.json"), "{corrupt");

            using var restarted = CreateCache(root);
            var entry = await restarted.GetAsync(reference);
            var snapshot = await restarted.GetSnapshotAsync();

            Assert.IsNotNull(entry);
            CollectionAssert.AreEqual(bytes, entry.CopyBytes());
            Assert.IsTrue(snapshot.IndexWasRebuilt);
            Assert.AreEqual(AccountedBytes(root), snapshot.TotalBytes);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task InterruptedPromotion_RebuildKeepsNewestPromotedVariant()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var oldReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("30700000-0000-4000-8000-000000000001"));
            var newReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("30700000-0000-4000-8000-000000000002"));
            using (var initial = CreateCache(root))
            {
                await AddAsync(initial, oldReference, bytes);
                await initial.PromoteVariantAsync(oldReference);
                await AddAsync(initial, newReference, bytes);
            }

            var newStem = ProductImageCacheKey
                .FromReference(newReference)
                .FileStem;
            var newMetadataPath = Path.Combine(root, newStem + ".meta");
            var newMetadata = await File.ReadAllTextAsync(newMetadataPath);
            StringAssert.Contains(newMetadata, "\"isPromoted\":false");
            await File.WriteAllTextAsync(
                newMetadataPath,
                newMetadata.Replace(
                    "\"isPromoted\":false",
                    "\"isPromoted\":true",
                    StringComparison.Ordinal));
            await File.WriteAllTextAsync(
                Path.Combine(root, "index-v1.json"),
                "{corrupt");

            using var restarted = CreateCache(root);
            var snapshot = await restarted.GetSnapshotAsync();

            Assert.IsTrue(snapshot.IndexWasRebuilt);
            Assert.IsNull(await restarted.GetAsync(oldReference));
            Assert.IsNotNull(await restarted.GetAsync(newReference));
            Assert.AreEqual(1, snapshot.EntryCount);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task CleanRestart_DiskHitSkipsProducer()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var reference = ProductImageTestData.CreateReference(bytes);
            using (var initial = CreateCache(root))
            {
                await AddAsync(initial, reference, bytes);
            }
            var producerCalls = 0;
            using var restarted = CreateCache(root);

            var entry = await restarted.GetOrAddAsync(
                reference,
                _ =>
                {
                    Interlocked.Increment(ref producerCalls);
                    throw new InvalidOperationException("Producer must not run.");
                });

            Assert.AreEqual(0, producerCalls);
            CollectionAssert.AreEqual(bytes, entry.CopyBytes());
            Assert.IsFalse((await restarted.GetSnapshotAsync()).IndexWasRebuilt);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task InterruptedWriteAndStaleTemporaryFile_AreCleaned()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var tempPath = Path.Combine(root, "interrupted.tmp");
            await File.WriteAllBytesAsync(tempPath, new byte[] { 1, 2, 3 });
            using var cache = new ProductImageDiskCache(
                new ProductImageCacheOptions(
                    root,
                    staleTemporaryFileAge: TimeSpan.FromHours(24)));

            var snapshot = await cache.GetSnapshotAsync();

            Assert.AreEqual(0, snapshot.TemporaryFileCount);
            Assert.IsFalse(File.Exists(tempPath));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task ConcurrentVersionCompletion_CannotRestoreStaleVersion()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = new ProductImageDiskCache(
                new ProductImageCacheOptions(
                    root,
                    maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
                    maximumEntries: 2));
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var fallbackReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("31000000-0000-4000-8000-000000000001"),
                imageUpdatedAt: DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
            var staleReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("31000000-0000-4000-8000-000000000002"),
                imageUpdatedAt: DateTimeOffset.Parse("2026-07-30T12:01:00Z"));
            var currentReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("31000000-0000-4000-8000-000000000003"),
                imageUpdatedAt: DateTimeOffset.Parse("2026-07-30T12:02:00Z"));
            var oldStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOld = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Stream> OldFactory(CancellationToken token)
            {
                oldStarted.TrySetResult(true);
                await releaseOld.Task.WaitAsync(token);
                return new MemoryStream(bytes, writable: false);
            }

            await AddAsync(cache, fallbackReference, bytes);
            await cache.PromoteVariantAsync(fallbackReference);
            var oldTask = cache.GetOrAddAsync(staleReference, OldFactory);
            Assert.IsTrue(await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            var current = await AddAsync(cache, currentReference, bytes);
            releaseOld.TrySetResult(true);

            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await oldTask);
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await cache.PromoteVariantAsync(staleReference));
            Assert.IsNotNull(await cache.GetAsync(fallbackReference));
            Assert.IsNull(await cache.GetAsync(staleReference));
            Assert.IsNotNull(await cache.GetAsync(currentReference));
            await cache.PromoteVariantAsync(currentReference);

            Assert.IsNotNull(current);
            Assert.IsNull(await cache.GetAsync(fallbackReference));
            Assert.IsNotNull(await cache.GetAsync(currentReference));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task Promotion_IsScopedToRequestedVariant()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var oldVersion = Guid.Parse("32000000-0000-4000-8000-000000000001");
            var nextVersion = Guid.Parse("32000000-0000-4000-8000-000000000002");
            var oldMain = ProductImageTestData.CreateReference(
                bytes,
                ProductImageVariant.Main,
                versionId: oldVersion);
            var oldThumb = ProductImageTestData.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                versionId: oldVersion);
            var nextThumb = ProductImageTestData.CreateReference(
                bytes,
                ProductImageVariant.Thumb,
                versionId: nextVersion);

            await AddAsync(cache, oldMain, bytes);
            await cache.PromoteVariantAsync(oldMain);
            await AddAsync(cache, oldThumb, bytes);
            await cache.PromoteVariantAsync(oldThumb);
            await AddAsync(cache, nextThumb, bytes);
            await cache.PromoteVariantAsync(nextThumb);

            Assert.IsNotNull(await cache.GetAsync(oldMain));
            Assert.IsNull(await cache.GetAsync(oldThumb));
            Assert.IsNotNull(await cache.GetAsync(nextThumb));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task PromotedFallback_SurvivesUnrelatedPressureAndRestart()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var productId = Guid.Parse("33000000-0000-4000-8000-000000000001");
            var oldReference = ProductImageTestData.CreateReference(
                bytes,
                productId: productId,
                versionId: Guid.Parse("33000000-0000-4000-8000-000000000002"));
            var undecodedReplacement = ProductImageTestData.CreateReference(
                bytes,
                productId: productId,
                versionId: Guid.Parse("33000000-0000-4000-8000-000000000003"));
            var unrelated = ProductImageTestData.CreateReference(
                bytes,
                productId: Guid.Parse("33000000-0000-4000-8000-000000000004"));
            var options = new ProductImageCacheOptions(
                root,
                maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
                maximumEntries: 2);

            using (var initial = new ProductImageDiskCache(options))
            {
                await AddAsync(initial, oldReference, bytes);
                await initial.PromoteVariantAsync(oldReference);
                await AddAsync(initial, undecodedReplacement, bytes);
            }

            using (var restarted = new ProductImageDiskCache(options))
            {
                await AddAsync(restarted, unrelated, bytes);

                Assert.IsNotNull(await restarted.GetAsync(oldReference));
                Assert.IsNull(await restarted.GetAsync(undecodedReplacement));
                Assert.IsNotNull(await restarted.GetAsync(unrelated));
                Assert.AreEqual(2, (await restarted.GetSnapshotAsync()).EntryCount);
            }
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task NullOrEqualTimestamps_DoNotUseUuidOrdering()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var firstWithTimestamp = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"));
            var secondWithTimestamp = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("00000000-0000-4000-8000-000000000001"));
            var first = new ProductImageReference(
                firstWithTimestamp.Identity,
                firstWithTimestamp.Variant,
                firstWithTimestamp.Metadata,
                imageUpdatedAt: null);
            var second = new ProductImageReference(
                secondWithTimestamp.Identity,
                secondWithTimestamp.Variant,
                secondWithTimestamp.Metadata,
                imageUpdatedAt: null);

            await AddAsync(cache, first, bytes);
            await AddAsync(cache, second, bytes);

            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await cache.PromoteVariantAsync(first));
            await cache.PromoteVariantAsync(second);
            Assert.IsNull(await cache.GetAsync(first));
            Assert.IsNotNull(await cache.GetAsync(second));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task FailedCandidate_DoesNotPoisonPromotedFallback()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var oldReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("34000000-0000-4000-8000-000000000001"));
            var failedReference = ProductImageTestData.CreateReference(
                bytes,
                versionId: Guid.Parse("34000000-0000-4000-8000-000000000002"));
            await AddAsync(cache, oldReference, bytes);
            await cache.PromoteVariantAsync(oldReference);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await cache.GetOrAddAsync(
                    failedReference,
                    _ => Task.FromResult<Stream>(
                        new MemoryStream(new byte[] { 1, 2, 3 }, false))));

            await cache.PromoteVariantAsync(oldReference);
            Assert.IsNotNull(await cache.GetAsync(oldReference));
            Assert.IsNull(await cache.GetAsync(failedReference));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task StrayFilenameAndCorruptTimestamp_DoNotBreakRebuild()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(root, "foo.img"),
                new byte[] { 1, 2, 3 });
            using (var first = CreateCache(root))
            {
                var empty = await first.GetSnapshotAsync();
                Assert.AreEqual(0, empty.EntryCount);
                Assert.IsFalse(File.Exists(Path.Combine(root, "foo.img")));

                var bytes = ProductImageTestData.CreateParserValidJpeg();
                await AddAsync(
                    first,
                    ProductImageTestData.CreateReference(bytes),
                    bytes);
            }

            var metadataPath = Directory
                .EnumerateFiles(root, "*.meta", SearchOption.TopDirectoryOnly)
                .Single();
            var metadata = await File.ReadAllTextAsync(metadataPath);
            metadata = System.Text.RegularExpressions.Regex.Replace(
                metadata,
                "\"imageUpdatedAtUtcTicks\":-?[0-9]+",
                "\"imageUpdatedAtUtcTicks\":9223372036854775807");
            await File.WriteAllTextAsync(metadataPath, metadata);
            await File.WriteAllTextAsync(
                Path.Combine(root, "index-v1.json"),
                "{corrupt");

            using var restarted = CreateCache(root);
            var snapshot = await restarted.GetSnapshotAsync();
            Assert.IsTrue(snapshot.IndexWasRebuilt);
            Assert.AreEqual(0, snapshot.EntryCount);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task RootLock_RejectsSecondLiveCacheInstance()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var first = CreateCache(root);
            await first.GetSnapshotAsync();
            using var second = CreateCache(root);

            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await second.GetSnapshotAsync());
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task DistinctKeyProducers_AreBoundedByConfiguredConcurrency()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = new ProductImageDiskCache(
                new ProductImageCacheOptions(
                    root,
                    maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
                    maximumEntries: 64,
                    maximumConcurrentProducers: 2));
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var references = Enumerable.Range(1, 20)
                .Select(index => ProductImageTestData.CreateReference(
                    bytes,
                    productId: Guid.Parse(
                        $"41000000-0000-4000-8000-{index:D12}")))
                .ToArray();
            var active = 0;
            var maximum = 0;

            async Task<Stream> Factory(CancellationToken token)
            {
                var current = Interlocked.Increment(ref active);
                while (true)
                {
                    var observed = Volatile.Read(ref maximum);
                    if (current <= observed ||
                        Interlocked.CompareExchange(
                            ref maximum,
                            current,
                            observed) == observed)
                    {
                        break;
                    }
                }

                try
                {
                    await Task.Delay(10, token);
                    return new MemoryStream(bytes, writable: false);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }

            await Task.WhenAll(references.Select(reference =>
                cache.GetOrAddAsync(reference, Factory)));

            Assert.IsTrue(maximum <= 2, $"Observed {maximum} producers.");
            Assert.AreEqual(20, (await cache.GetSnapshotAsync()).EntryCount);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task CorruptedMetadataTraversal_CannotTouchOutsideSentinel()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        var sentinel = Path.Combine(
            Directory.GetParent(root)!.FullName,
            "outside-" + Guid.NewGuid().ToString("N") + ".sentinel");
        try
        {
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var reference = ProductImageTestData.CreateReference(bytes);
            using (var initial = CreateCache(root))
            {
                await AddAsync(initial, reference, bytes);
            }
            var metadataPath = Directory
                .EnumerateFiles(root, "*.meta", SearchOption.TopDirectoryOnly)
                .Single();
            await File.WriteAllTextAsync(
                metadataPath,
                "{\"schemaVersion\":1,\"fileStem\":\"..\\\\outside\"}");
            await File.WriteAllTextAsync(
                Path.Combine(root, "index-v1.json"),
                "{corrupt");
            await File.WriteAllTextAsync(sentinel, "keep");

            using var restarted = CreateCache(root);
            var snapshot = await restarted.GetSnapshotAsync();

            Assert.IsTrue(File.Exists(sentinel));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
            Assert.AreEqual(0, snapshot.EntryCount);
            Assert.IsTrue(snapshot.IndexWasRebuilt);
        }
        finally
        {
            if (File.Exists(sentinel))
            {
                File.Delete(sentinel);
            }

            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task ConcurrentReadWrite_RemainsConsistent()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var references = Enumerable.Range(1, 40)
                .Select(index => ProductImageTestData.CreateReference(
                    bytes,
                    productId: Guid.Parse(
                        $"40000000-0000-4000-8000-{index:D12}")))
                .ToArray();
            var errors = new ConcurrentQueue<Exception>();

            await Task.WhenAll(references.Select(async reference =>
            {
                try
                {
                    await AddAsync(cache, reference, bytes);
                    var read = await cache.GetAsync(reference);
                    Assert.IsNotNull(read);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }));

            Assert.AreEqual(0, errors.Count);
            var snapshot = await cache.GetSnapshotAsync();
            Assert.AreEqual(40, snapshot.EntryCount);
            Assert.AreEqual(AccountedBytes(root), snapshot.TotalBytes);
            Assert.IsTrue(snapshot.TotalBytes > 40L * bytes.Length);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task Cancellation_StopsQueuedProducerWhenLastConsumerLeaves()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var reference = ProductImageTestData.CreateReference(bytes);
            using var cancellation = new CancellationTokenSource();
            var producerStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var producerCancelled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Stream> Factory(CancellationToken token)
            {
                producerStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException();
                }
                catch (OperationCanceledException)
                {
                    producerCancelled.TrySetResult(true);
                    throw;
                }
            }

            var request = cache.GetOrAddAsync(reference, Factory, cancellation.Token);
            Assert.IsTrue(await producerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await request);
            Assert.IsTrue(await producerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsNull(await cache.GetAsync(reference));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task CancelThenImmediateRetry_DoesNotOverlapSameKeyProducer()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            using var cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var reference = ProductImageTestData.CreateReference(bytes);
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCancelledProducerToExit = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var active = 0;
            var maximumActive = 0;

            async Task<Stream> SlowFactory(CancellationToken token)
            {
                Interlocked.Increment(ref calls);
                var current = Interlocked.Increment(ref active);
                SetMaximum(ref maximumActive, current);
                started.TrySetResult(true);
                try
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        await allowCancelledProducerToExit.Task;
                        throw;
                    }

                    throw new InvalidOperationException();
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    exited.TrySetResult(true);
                }
            }

            var first = cache.GetOrAddAsync(
                reference,
                SlowFactory,
                cancellation.Token);
            Assert.IsTrue(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await first);

            var immediateRetry = cache.GetOrAddAsync(reference, SlowFactory);
            await Task.Delay(50);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, maximumActive);
            allowCancelledProducerToExit.TrySetResult(true);
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await immediateRetry);
            Assert.IsTrue(await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            var final = await AddAsync(cache, reference, bytes);
            Assert.IsNotNull(final);
            Assert.AreEqual(1, maximumActive);
            Assert.AreEqual(1, calls);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task Dispose_DrainsProducerBeforeReleasingRootLock()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        ProductImageDiskCache? cache = null;
        try
        {
            cache = CreateCache(root);
            var bytes = ProductImageTestData.CreateParserValidJpeg();
            var reference = ProductImageTestData.CreateReference(bytes);
            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowExit = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Stream> Factory(CancellationToken token)
            {
                started.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException();
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult(true);
                    await allowExit.Task;
                    throw;
                }
            }

            var request = cache.GetOrAddAsync(reference, Factory);
            Assert.IsTrue(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            var dispose = Task.Run(() => cache.Dispose());
            Assert.IsTrue(
                await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            using (var contender = CreateCache(root))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await contender.GetSnapshotAsync());
            }

            allowExit.TrySetResult(true);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await request);
            cache = null;

            using var reopened = CreateCache(root);
            Assert.IsNotNull(await reopened.GetSnapshotAsync());
        }
        finally
        {
            cache?.Dispose();
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task Dispose_DuringInitializationDrainsBeforeReopen()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        ProductImageDiskCache? cache = null;
        try
        {
            for (var index = 0; index < 300; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, $"{index:D4}.meta"),
                    "{}");
            }

            cache = CreateCache(root);
            var initialize = cache.GetSnapshotAsync();
            await Task.Yield();
            var dispose = Task.Run(() => cache.Dispose());

            await initialize.WaitAsync(TimeSpan.FromSeconds(10));
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
            cache = null;

            using var reopened = CreateCache(root);
            Assert.AreEqual(0, (await reopened.GetSnapshotAsync()).EntryCount);
        }
        finally
        {
            cache?.Dispose();
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task DirectoryOverflow_FailsInsteadOfReturningPartialAccounting()
    {
        var root = ProductImageTestData.CreateTempDirectory();
        try
        {
            const int overflowFileCount = 1593;
            for (var index = 0; index < overflowFileCount; index++)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(root, $"{index:D4}.tmp"),
                    Array.Empty<byte>());
            }

            using var cache = new ProductImageDiskCache(
                new ProductImageCacheOptions(
                    root,
                    maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
                    maximumEntries: 2));

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await cache.GetSnapshotAsync());
            Assert.AreEqual("image_cache_directory_overflow", error.Message);
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
        }
    }

    [TestMethod]
    public async Task DanglingRootLockSymlink_IsRejectedWithoutCreatingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = ProductImageTestData.CreateTempDirectory();
        var target = Path.Combine(
            Directory.GetParent(root)!.FullName,
            "missing-lock-target-" + Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                File.CreateSymbolicLink(
                    Path.Combine(root, ".cache.lock"),
                    target);
            }
            catch (Exception error) when (
                error is UnauthorizedAccessException ||
                error is PlatformNotSupportedException ||
                error is IOException)
            {
                return;
            }

            using var cache = CreateCache(root);
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await cache.GetSnapshotAsync());
            Assert.IsFalse(File.Exists(target));
        }
        finally
        {
            ProductImageTestData.DeleteTempDirectory(root);
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    [TestMethod]
    public async Task ReparseAncestor_IsRejectedBeforeLeafCreation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = ProductImageTestData.CreateTempDirectory();
        var target = ProductImageTestData.CreateTempDirectory();
        var link = Path.Combine(parent, "linked");
        var cacheRoot = Path.Combine(link, "ImageCache");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception error) when (
                error is UnauthorizedAccessException ||
                error is PlatformNotSupportedException ||
                error is IOException)
            {
                return;
            }

            using var cache = CreateCache(cacheRoot);
            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await cache.GetSnapshotAsync());
            Assert.IsFalse(Directory.Exists(Path.Combine(target, "ImageCache")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            ProductImageTestData.DeleteTempDirectory(parent);
            ProductImageTestData.DeleteTempDirectory(target);
        }
    }

    [TestMethod]
    public void CacheRoot_RejectsFilesystemRootAndProgramFiles()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProductImageCacheOptions(Path.GetPathRoot(Path.GetTempPath())!));
        var safeRoot = Path.Combine(
            Path.GetTempPath(),
            "Win7POS-product-image-tests",
            Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductImageCacheOptions(
                safeRoot,
                maximumBytes:
                    ProductImageCacheOptions.MinimumMaximumBytes - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductImageCacheOptions(
                safeRoot,
                maximumEntries:
                    ProductImageCacheOptions.MinimumMaximumEntries - 1));

        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            Assert.Throws<ArgumentException>(() =>
                new ProductImageCacheOptions(
                    Path.Combine(programFiles, "Win7POS", "ImageCache")));
        }
    }

    private static ProductImageDiskCache CreateCache(string root)
    {
        return new ProductImageDiskCache(
            new ProductImageCacheOptions(
                root,
                maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
                maximumEntries: 256,
                staleTemporaryFileAge: TimeSpan.FromMinutes(1)));
    }

    private static long AccountedBytes(string root)
    {
        return Directory
            .EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name == "index-v1.json" ||
                       name == ".cache.lock" ||
                       name.EndsWith(".img", StringComparison.Ordinal) ||
                       name.EndsWith(".meta", StringComparison.Ordinal) ||
                       name.EndsWith(".tmp", StringComparison.Ordinal);
            })
            .Sum(path => new FileInfo(path).Length);
    }

    private static Task<ProductImageCacheEntry> AddAsync(
        ProductImageDiskCache cache,
        ProductImageReference reference,
        byte[] bytes)
    {
        return cache.GetOrAddAsync(
            reference,
            _ => Task.FromResult<Stream>(
                new MemoryStream(bytes, writable: false)));
    }

    private static void SetMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current ||
                Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}
