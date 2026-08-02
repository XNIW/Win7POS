using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Images;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Images;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Images;

[TestClass]
public sealed class ProductImageRestoreIsolationTests
{
    private const string StaffA = "60000000-0000-4000-8000-000000000201";
    private const string StaffB = "60000000-0000-4000-8000-000000000202";
    private const string ShopA = "10000000-0000-4000-8000-000000000201";
    private const string ShopB = "10000000-0000-4000-8000-000000000202";
    private const string ShopCodeA = "SHOP-A";
    private const string ShopCodeB = "SHOP-B";
    private static readonly Guid RemoteProductId =
        Guid.Parse("20000000-0000-4000-8000-000000000201");
    private static readonly Guid VersionA =
        Guid.Parse("30000000-0000-4000-8000-000000000201");
    private static readonly Guid VersionB =
        Guid.Parse("30000000-0000-4000-8000-000000000202");

    [TestMethod]
    public async Task RestoreAndTrustedIdentityTransition_IsolateForeignCacheStagingAndOutbox()
    {
        var environment = TestEnvironment.Create();
        try
        {
            await SaveShopAsync(
                environment.Factory,
                ShopA,
                ShopCodeA,
                generation: null);
            var catalogState = new CatalogShopStateRepository(environment.Factory);
            var bindingA = await catalogState.EnsureAndLoadCursorAsync(
                ShopA,
                ShopCodeA);
            Assert.IsTrue(bindingA.IsValid);

            var scopeStore = new ProductImageCacheScopeStore(environment.Factory);
            var scopeBindingA = await scopeStore.BindWithTransitionAsync(
                StaffA,
                ShopA,
                "server-cache-scope-a");
            Assert.IsNotNull(scopeBindingA.PurgeToken);
            Assert.IsTrue(await scopeStore.AcknowledgePurgeAsync(
                StaffA,
                ShopA,
                scopeBindingA.AccountScope,
                scopeBindingA.PurgeToken));
            Assert.AreEqual(
                scopeBindingA.AccountScope,
                await scopeStore.ResolveActiveAsync(StaffA, ShopA));

            var mainA = Variant(ProductImageVariant.Main, 8, 6);
            var thumbA = Variant(ProductImageVariant.Thumb, 4, 3);
            var staging = new ProductImageStagingStore(
                new ProductImageStagingOptions(
                    environment.StagingRoot,
                    TimeSpan.FromMinutes(5)));
            var stagedA = await staging.StagePairAsync(mainA, thumbA);
            var localProductId = await InsertProductAsync(environment.Factory);
            var outbox = new ProductImageOperationOutboxRepository(
                environment.Factory);
            var operation = await outbox.EnqueueReplaceAsync(
                new ProductImageReplaceEnqueueRequest
                {
                    LocalProductId = localProductId,
                    ExpectedCurrentVersionId = VersionA.ToString("D"),
                    IntendedLocalVersionIdentity = "local-version-a",
                    PayloadHash = PayloadHash('a'),
                    Main = StagedVariant(stagedA.MainIdentity, mainA),
                    Thumb = StagedVariant(stagedA.ThumbIdentity, thumbA)
                },
                (_, _) => PayloadHash('b'));

            var generationA = Generation(
                "generation-product-image-a",
                ShopA,
                ShopCodeA);
            var generationB = Generation(
                "generation-product-image-b",
                ShopB,
                ShopCodeB);
            var generations = new OnlineSyncGenerationRepository(
                environment.Factory);
            await generations.ActivateAndRecoverAsync(generationA, 100);

            ProductImageReference referenceB;
            using (var cache = CreateCache(environment.CacheRoot))
            {
                var referenceA = Reference(
                    scopeBindingA.AccountScope,
                    Guid.Parse(ShopA),
                    RemoteProductId,
                    VersionA,
                    mainA);
                await AddAndPromoteAsync(cache, referenceA, mainA.CopyBytes());
                Assert.IsNotNull(await cache.GetPromotedForProductAsync(
                    scopeBindingA.AccountScope,
                    Guid.Parse(ShopA),
                    RemoteProductId,
                    ProductImageVariant.Main));

                var scopeB = ProductImageCacheScopeStore.DeriveAccountScope(
                    "server-cache-scope-b");
                Assert.IsNull(await scopeStore.ResolveActiveAsync(StaffB, ShopB));
                Assert.IsNull(await cache.GetPromotedForProductAsync(
                    scopeB,
                    Guid.Parse(ShopB),
                    RemoteProductId,
                    ProductImageVariant.Main));

                var restoreSafety = new RestoreShopSafetyRepository(
                    environment.Factory);
                var candidateWithForeignWork = await restoreSafety
                    .ValidateCandidateAsync(ShopA, ShopCodeA);
                var liveWithForeignWork = await restoreSafety
                    .ValidateLivePreSwapAsync(
                        ShopA,
                        ShopCodeA,
                        bindingA.Epoch);
                Assert.IsFalse(candidateWithForeignWork.IsValid);
                Assert.AreEqual(
                    "restore_candidate_outbox_unresolved",
                    candidateWithForeignWork.Code);
                Assert.IsFalse(liveWithForeignWork.IsValid);
                Assert.AreEqual(
                    "restore_live_product_image_outbox_unresolved",
                    liveWithForeignWork.Code);

                var transitionGuard = new PosShopTransitionGuard(
                    environment.Factory);
                var blockedTransition = await transitionGuard.EvaluateAsync(
                    ShopA,
                    ShopCodeA,
                    ShopB,
                    ShopCodeB);
                Assert.IsFalse(blockedTransition.Allowed);
                Assert.IsTrue(blockedTransition.HasUnresolvedOutbox);
                Assert.AreEqual(
                    "shop_switch_blocked_unresolved_outbox",
                    blockedTransition.Code);

                var foreignRunnerCalls = 0;
                using (var foreignSupervisor = new OnlineSyncSupervisor(
                           generationB,
                           (_, _, _) =>
                           {
                               Interlocked.Increment(ref foreignRunnerCalls);
                               return Task.FromResult(TerminalSuccess());
                           },
                           generations.IsCurrentAndActiveAsync,
                           (_, _) => Task.CompletedTask,
                           networkConcurrency: 2))
                {
                    var foreignOutcome = await foreignSupervisor.TriggerAsync(
                        OnlineSyncLane.ProductImageOutbox,
                        OnlineSyncLaneTrigger.LocalCommit);
                    Assert.AreEqual("stale_generation", foreignOutcome.Code);
                    Assert.AreEqual(0, foreignRunnerCalls);
                    await foreignSupervisor.StopAsync();
                }

                await AssertCapabilityShapedFixtureIsRejectedAndPurgedAsync(
                    staging,
                    environment.StagingRoot,
                    mainA.Metadata,
                    stagedA);

                var claimA = await outbox.ClaimNextAsync(
                    generationA.GenerationId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Assert.IsNotNull(claimA);
                Assert.AreEqual(
                    generationA.GenerationId,
                    claimA.ClaimGenerationId);
                await staging.DeletePairAsync(
                    stagedA.MainIdentity,
                    stagedA.ThumbIdentity);
                Assert.IsTrue(await outbox.CompleteCleanupAsync(
                    claimA,
                    "test_identity_transition_cleanup"));

                var completed = await outbox.GetAsync(operation.OperationId);
                Assert.AreEqual(ProductImageOperationStates.Completed, completed.State);
                Assert.IsNull(completed.StagedMainIdentity);
                Assert.IsNull(completed.StagedThumbIdentity);
                Assert.AreEqual(0L, await outbox.CountUnresolvedAsync());

                await generations.ResetForRestoreAsync(
                    generationA,
                    ShopA,
                    ShopCodeA,
                    200);
                Assert.IsFalse(await generations.IsCurrentAndActiveAsync(
                    generationA));
                Assert.IsFalse(await generations.IsCurrentAndActiveAsync(
                    generationB));

                var allowedTransition = await transitionGuard.EvaluateAsync(
                    ShopA,
                    ShopCodeA,
                    ShopB,
                    ShopCodeB);
                Assert.IsTrue(allowedTransition.Allowed);
                Assert.IsTrue(allowedTransition.RequiresCatalogReset);
                await transitionGuard.ApplyAuthorizedTransitionAsync(
                    allowedTransition);
                await generations.ActivateAndRecoverAsync(generationB, 300);
                Assert.IsTrue(await generations.IsCurrentAndActiveAsync(
                    generationB));
                Assert.IsFalse(await generations.IsCurrentAndActiveAsync(
                    generationA));

                var trustedBRunnerCalls = 0;
                var foreignClaims = 0;
                var foreignNetworkRequests = 0;
                using (var trustedBSupervisor = new OnlineSyncSupervisor(
                           generationB,
                           async (context, _, cancellationToken) =>
                           {
                               Interlocked.Increment(ref trustedBRunnerCalls);
                               var foreignClaim = await outbox.ClaimNextAsync(
                                   context.Generation.GenerationId,
                                   DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                   cancellationToken);
                               if (foreignClaim == null) return TerminalSuccess();
                               Interlocked.Increment(ref foreignClaims);
                               return await context.ExecuteRequestAsync(_ =>
                               {
                                   Interlocked.Increment(ref foreignNetworkRequests);
                                   return Task.FromResult(TerminalSuccess());
                               }, cancellationToken);
                           },
                           generations.IsCurrentAndActiveAsync,
                           (_, _) => Task.CompletedTask,
                           networkConcurrency: 2))
                {
                    var trustedBOutcome = await trustedBSupervisor.TriggerAsync(
                        OnlineSyncLane.ProductImageOutbox,
                        OnlineSyncLaneTrigger.LocalCommit);
                    Assert.IsTrue(trustedBOutcome.Success);
                    Assert.AreEqual(1, trustedBRunnerCalls);
                    Assert.AreEqual(0, foreignClaims);
                    Assert.AreEqual(0, foreignNetworkRequests);
                    await trustedBSupervisor.StopAsync();
                }

                var identityPurge = await scopeStore.ObserveTrustedIdentityAsync(
                    StaffB,
                    ShopB);
                Assert.IsNotNull(identityPurge);
                Assert.IsNull(await scopeStore.ResolveActiveAsync(StaffA, ShopA));
                Assert.IsNull(await scopeStore.ResolveActiveAsync(StaffB, ShopB));
                Assert.IsFalse(await scopeStore.AcknowledgePurgeAsync(
                    StaffA,
                    ShopA,
                    null,
                    identityPurge));
                Assert.AreEqual(1, await cache.PurgeAllAsync());
                Assert.IsTrue(await scopeStore.AcknowledgePurgeAsync(
                    StaffB,
                    ShopB,
                    null,
                    identityPurge));

                var scopeBindingB = await scopeStore.BindWithTransitionAsync(
                    StaffB,
                    ShopB,
                    "server-cache-scope-b");
                Assert.IsNotNull(scopeBindingB.PurgeToken);
                Assert.AreEqual(0, await cache.PurgeAllAsync());
                Assert.IsTrue(await scopeStore.AcknowledgePurgeAsync(
                    StaffB,
                    ShopB,
                    scopeBindingB.AccountScope,
                    scopeBindingB.PurgeToken));
                Assert.AreEqual(
                    scopeBindingB.AccountScope,
                    await scopeStore.ResolveActiveAsync(StaffB, ShopB));
                Assert.IsNull(await cache.GetAsync(referenceA));

                var mainB = Variant(ProductImageVariant.Main, 9, 7);
                referenceB = Reference(
                    scopeBindingB.AccountScope,
                    Guid.Parse(ShopB),
                    RemoteProductId,
                    VersionB,
                    mainB);
                await AddAndPromoteAsync(cache, referenceB, mainB.CopyBytes());
                Assert.IsNotNull(await cache.GetPromotedForProductAsync(
                    scopeBindingB.AccountScope,
                    Guid.Parse(ShopB),
                    RemoteProductId,
                    ProductImageVariant.Main));

                await SaveShopAsync(
                    environment.Factory,
                    ShopB,
                    ShopCodeB,
                    generationB);
                var bindingB = await catalogState.EnsureAndLoadCursorAsync(
                    ShopB,
                    ShopCodeB,
                    generationB);
                Assert.IsTrue(bindingB.IsValid);
                Assert.IsTrue((await restoreSafety.ValidateCandidateAsync(
                    ShopB,
                    ShopCodeB)).IsValid);
                Assert.IsTrue((await restoreSafety.ValidateLivePreSwapAsync(
                    ShopB,
                    ShopCodeB,
                    bindingB.Epoch)).IsValid);

                Assert.IsNull(await outbox.ClaimNextAsync(
                    generationB.GenerationId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Assert.AreEqual(
                    0,
                    (await outbox.GetReferencedStagingIdentitiesAsync()).Count);
                await AssertPersistenceArchitectureAsync(environment.Factory);
            }

            SqliteConnection.ClearAllPools();
            var restartedFactory = new SqliteConnectionFactory(
                PosDbOptions.ForPath(environment.Factory.DbPath));
            var restartedScopeStore = new ProductImageCacheScopeStore(
                restartedFactory);
            Assert.IsNull(await restartedScopeStore.ResolveActiveAsync(
                StaffA,
                ShopA));
            var restartedScopeB = await restartedScopeStore.ResolveActiveAsync(
                StaffB,
                ShopB);
            Assert.IsNotNull(restartedScopeB);

            using (var restartedCache = CreateCache(environment.CacheRoot))
            {
                Assert.IsNotNull(await restartedCache.GetAsync(referenceB));
                Assert.IsNotNull(await restartedCache.GetPromotedForProductAsync(
                    restartedScopeB,
                    Guid.Parse(ShopB),
                    RemoteProductId,
                    ProductImageVariant.Main));
                Assert.IsNull(await restartedCache.GetPromotedForProductAsync(
                    ProductImageCacheScopeStore.DeriveAccountScope(
                        "server-cache-scope-a"),
                    Guid.Parse(ShopA),
                    RemoteProductId,
                    ProductImageVariant.Main));
                Assert.AreEqual(1, await restartedCache.PurgeAllAsync());
                Assert.AreEqual(
                    0,
                    (await restartedCache.GetSnapshotAsync()).EntryCount);
            }

            var restartedOutbox = new ProductImageOperationOutboxRepository(
                restartedFactory);
            Assert.IsNull(await restartedOutbox.ClaimNextAsync(
                generationB.GenerationId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var restartedStaging = new ProductImageStagingStore(
                new ProductImageStagingOptions(
                    environment.StagingRoot,
                    TimeSpan.FromMinutes(5)));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                restartedStaging.OpenVerifiedReadAsync(
                    stagedA.MainIdentity,
                    ProductImageVariant.Main,
                    mainA.Metadata));
            Assert.AreEqual(
                0,
                Directory.EnumerateFiles(
                    environment.StagingRoot,
                    "*",
                    SearchOption.AllDirectories).Count());
            AssertNoCapabilityFixtureWasPersisted(
                environment.CacheRoot,
                environment.StagingRoot);
        }
        finally
        {
            environment.Dispose();
            Assert.IsFalse(
                Directory.Exists(environment.Root),
                "The integrated product-image isolation fixture left residual files.");
        }
    }

    private static async Task AssertCapabilityShapedFixtureIsRejectedAndPurgedAsync(
        ProductImageStagingStore staging,
        string stagingRoot,
        ProductImageMetadata expectedMetadata,
        ProductImageStagingPair referencedPair)
    {
        const string rogueIdentity =
            "stage-ffffffffffffffffffffffffffffffff-main.jpg";
        var capabilityShapedBytes = Encoding.ASCII.GetBytes(string.Concat(
            "ht",
            "tps://invalid.example/upload?",
            "to",
            "ken=synthetic"));
        var roguePath = Path.Combine(stagingRoot, rogueIdentity);
        await File.WriteAllBytesAsync(roguePath, capabilityShapedBytes);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(
            roguePath,
            now.AddHours(-1).UtcDateTime);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            staging.OpenVerifiedReadAsync(
                rogueIdentity,
                ProductImageVariant.Main,
                expectedMetadata));
        Assert.AreEqual(1, await staging.CleanupOrphansAsync(
            new[]
            {
                referencedPair.MainIdentity,
                referencedPair.ThumbIdentity
            },
            now));
        Assert.IsFalse(File.Exists(roguePath));
    }

    private static async Task AssertPersistenceArchitectureAsync(
        SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        var productImageColumns = (await connection.QueryAsync<string>(@"
SELECT name
FROM pragma_table_info('products')
WHERE name LIKE '%image%'
ORDER BY name;")).ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "primary_image_updated_at",
                "primary_image_version_id"
            },
            productImageColumns);

        var outboxColumns = (await connection.QueryAsync<string>(@"
SELECT name
FROM pragma_table_info('product_image_operation_outbox')
ORDER BY name;")).ToArray();
        var forbiddenColumnMarkers = new[]
        {
            "authorization",
            "capability",
            "credential",
            "secret",
            "signed",
            "token",
            "url"
        };
        Assert.IsFalse(outboxColumns.Any(column =>
            forbiddenColumnMarkers.Any(marker =>
                column.Contains(marker, StringComparison.OrdinalIgnoreCase))));

        var tables = (await connection.QueryAsync<string>(@"
SELECT name
FROM sqlite_master
WHERE type = 'table'
  AND name NOT LIKE 'sqlite_%'
ORDER BY name;")).ToArray();
        foreach (var table in tables)
        {
            var quotedTable = "\"" + table.Replace("\"", "\"\"") + "\"";
            var rows = await connection.QueryAsync($"SELECT * FROM {quotedTable};");
            foreach (var row in rows.Cast<IDictionary<string, object>>())
            {
                foreach (var field in row)
                {
                    var text = field.Value switch
                    {
                        string value => value,
                        byte[] value => Encoding.UTF8.GetString(value),
                        _ => null
                    };
                    if (text == null) continue;
                    AssertNoCapabilityMarker(text, table, field.Key);
                }
            }
        }
    }

    private static void AssertNoCapabilityMarker(
        string value,
        string table,
        string field)
    {
        var markers = new[]
        {
            string.Concat("ht", "tps://"),
            "invalid.example",
            "/upload?",
            string.Concat("to", "ken=")
        };
        foreach (var marker in markers)
        {
            Assert.IsFalse(
                value.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"Durable field {table}.{field} contains capability-shaped data.");
        }
    }

    private static void AssertNoCapabilityFixtureWasPersisted(
        params string[] roots)
    {
        foreach (var path in roots
                     .Where(Directory.Exists)
                     .SelectMany(root => Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories)))
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.IsFalse(text.Contains("invalid.example", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("token=synthetic", StringComparison.Ordinal));
        }
    }

    private static ProductImageDiskCache CreateCache(string root)
    {
        return new ProductImageDiskCache(new ProductImageCacheOptions(
            root,
            maximumBytes: ProductImageCacheOptions.MinimumMaximumBytes,
            maximumEntries: 32,
            maximumConcurrentProducers: 2,
            staleTemporaryFileAge: TimeSpan.FromMinutes(1)));
    }

    private static async Task AddAndPromoteAsync(
        ProductImageDiskCache cache,
        ProductImageReference reference,
        byte[] bytes)
    {
        await cache.GetOrAddAsync(
            reference,
            _ => Task.FromResult<Stream>(
                new MemoryStream(bytes, writable: false)));
        await cache.PromoteVariantAsync(reference);
    }

    private static ProductImageReference Reference(
        string accountScope,
        Guid shopId,
        Guid productId,
        Guid versionId,
        ProductImageProcessedVariant variant)
    {
        Assert.IsTrue(ProductImageIdentity.TryCreate(
            accountScope,
            shopId.ToString("D"),
            productId.ToString("D"),
            versionId.ToString("D"),
            out var identity,
            out var validation),
            string.Join(",", validation.Messages));
        return new ProductImageReference(
            identity!,
            variant.Variant,
            variant.Metadata,
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
    }

    private static ProductImageProcessedVariant Variant(
        ProductImageVariant variant,
        ushort width,
        ushort height)
    {
        var bytes = ProductImageTestData.CreateParserValidJpeg(width, height);
        Assert.IsTrue(ProductImageMetadata.TryCreate(
            variant,
            ProductImageContractV1.WireMimeType,
            bytes.Length,
            width,
            height,
            ProductImageHash.Sha256Hex(bytes),
            out var metadata,
            out var validation),
            string.Join(",", validation.Messages));
        return new ProductImageProcessedVariant(variant, bytes, metadata!);
    }

    private static ProductImageStagedVariant StagedVariant(
        string identity,
        ProductImageProcessedVariant variant)
    {
        return new ProductImageStagedVariant
        {
            Bytes = variant.Metadata.ByteSize,
            Height = variant.Metadata.Height,
            Identity = identity,
            Sha256 = variant.Metadata.Sha256,
            Width = variant.Metadata.Width
        };
    }

    private static async Task<long> InsertProductAsync(
        SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        await connection.ExecuteAsync(@"
INSERT INTO products(
  barcode,
  name,
  unitPrice,
  remote_product_id,
  primary_image_version_id,
  primary_image_updated_at,
  is_active)
VALUES(
  'ISOLATION-IMAGE-001',
  'Synthetic isolation image',
  100,
  @remoteProductId,
  @versionId,
  '2026-08-02T12:00:00Z',
  1);",
            new
            {
                remoteProductId = RemoteProductId.ToString("D"),
                versionId = VersionA.ToString("D")
            });
        return await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM products WHERE barcode = 'ISOLATION-IMAGE-001';");
    }

    private static async Task SaveShopAsync(
        SqliteConnectionFactory factory,
        string shopId,
        string shopCode,
        OnlineSyncGeneration? generation)
    {
        await new ShopOfficialSnapshotRepository(factory).SaveAsync(
            new OfficialShopSnapshot
            {
                ShopCode = shopCode,
                ShopId = shopId,
                ShopName = shopCode,
                Source = "test"
            },
            generation);
    }

    private static OnlineSyncGeneration Generation(
        string generationId,
        string shopId,
        string shopCode)
    {
        return new OnlineSyncGeneration(
            generationId,
            "session-" + generationId,
            "device-" + generationId,
            shopId,
            shopCode);
    }

    private static OnlineSyncLaneOutcome TerminalSuccess()
    {
        return new OnlineSyncLaneOutcome(success: true, terminal: true);
    }

    private static string PayloadHash(char character)
    {
        return "sha256:" + new string(character, 64);
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(string root)
        {
            Root = root;
            CacheRoot = Path.Combine(root, "cache");
            StagingRoot = Path.Combine(root, "staging");
            Directory.CreateDirectory(CacheRoot);
            Directory.CreateDirectory(StagingRoot);
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        internal string CacheRoot { get; }
        internal SqliteConnectionFactory Factory { get; }
        internal string Root { get; }
        internal string StagingRoot { get; }

        internal static TestEnvironment Create()
        {
            return new TestEnvironment(
                ProductImageTestData.CreateTempDirectory());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            ProductImageTestData.DeleteTempDirectory(Root);
        }
    }
}
