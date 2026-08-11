using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Images;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Images;
using Win7POS.Data.Online;
using Win7POS.Wpf.Pos.Online;
using ImageContract = Win7POS.Core.Images.ProductImageContractV1;

namespace Win7POS.Wpf.Products.Images
{
    /// <summary>
    /// Shared bounded read/decode runtime. UI consumers own cancellation; the
    /// disk cache and in-memory decode cache single-flight identical requests.
    /// </summary>
    public static class ProductImageRuntime
    {
        private static readonly ProductImageDiskCache DiskCache =
            new ProductImageDiskCache(ProductImageCacheOptions.CreateDefault());
        private static readonly ProductImageDecodeService Decoder =
            new ProductImageDecodeService(
                new ProductImageDiskCacheStreamProvider(DiskCache));
        private static readonly SemaphoreSlim ReadRequestGate =
            new SemaphoreSlim(ImageContract.ReadRequestConcurrency);
        private static readonly SemaphoreSlim DownloadGate =
            new SemaphoreSlim(ImageContract.DownloadConcurrency);
        private static readonly SemaphoreSlim CacheScopeBindingGate =
            new SemaphoreSlim(1, 1);
        private static long _cacheScopeReadSequence;
        private static long _lastBoundCacheScopeReadSequence;
        private static long _cacheBindingGeneration;
        private static readonly ConcurrentDictionary<string, long>
            ProductCacheGenerations =
                new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private static readonly AsyncLocal<AcceptanceRuntimeContext>
            AcceptanceRuntimeOverride =
                new AsyncLocal<AcceptanceRuntimeContext>();

        internal static IDisposable UseTrustedProfileForAcceptance(
            string profileName,
            string cacheRoot)
        {
            if (!PosTrustedDeviceStore.IsValidProfileName(profileName))
                throw new ArgumentException(
                    "A valid trusted profile is required.",
                    nameof(profileName));
            var previous = AcceptanceRuntimeOverride.Value;
            var context = new AcceptanceRuntimeContext(
                profileName,
                cacheRoot);
            AcceptanceRuntimeOverride.Value = context;
            return new TrustedProfileScope(previous, context);
        }

        public static async Task LoadAsync(
            ProductDetailsRow product,
            ProductImageVariant variant,
            ProductImageDecodeProfile profile,
            ProductImageDisplayViewModel display,
            CancellationToken cancellationToken)
        {
            if (product == null) throw new ArgumentNullException(nameof(product));
            if (display == null) throw new ArgumentNullException(nameof(display));
            if (!ProductImageFeatureFlags.IsEnabled)
            {
                display.SetNoImage();
                return;
            }

            PosTrustedDeviceSession session;
            var store = CreateTrustedStore();
            if (store.TryRead(out session) &&
                PosProductImageContractV1.IsCanonicalUuid(session.ShopId) &&
                PosProductImageContractV1.IsCanonicalUuid(session.StaffId))
            {
                try
                {
                    await ReconcileTrustedIdentityAsync(
                        session.StaffId,
                        session.ShopId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Staged user work remains displayable. Remote cache paths
                    // retry the durable pending purge before any later access.
                }
            }

            if (await TryLoadStagedPreviewAsync(
                    product,
                    variant,
                    display,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (!PosProductImageContractV1.IsCanonicalUuid(
                product.RemoteProductId))
            {
                display.SetNoImage();
                return;
            }
            var productCacheGeneration = ReadProductCacheGeneration(
                product.RemoteProductId);

            var scopeStore = new ProductImageCacheScopeStore(
                new SqliteConnectionFactory(PosDbOptions.Default()));
            if (!PosProductImageContractV1.IsCanonicalUuid(
                product.PrimaryImageVersionId))
            {
                IncrementProductCacheGeneration(product.RemoteProductId);
                if (store.TryRead(out session) &&
                    PosProductImageContractV1.IsCanonicalUuid(session.ShopId) &&
                    PosProductImageContractV1.IsCanonicalUuid(session.StaffId) &&
                    Guid.TryParse(session.ShopId, out var purgeShopId) &&
                    Guid.TryParse(product.RemoteProductId, out var purgeProductId))
                {
                    var purgeScope = await scopeStore.ResolveActiveAsync(
                        session.StaffId,
                        session.ShopId,
                        cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(purgeScope))
                    {
                        await ActiveDiskCache.PurgeProductAsync(
                            purgeScope,
                            purgeShopId,
                            purgeProductId,
                            null,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                display.SetNoImage();
                return;
            }
            if (!store.TryRead(out session) ||
                !PosProductImageContractV1.IsCanonicalUuid(session.ShopId) ||
                !PosProductImageContractV1.IsCanonicalUuid(session.StaffId))
            {
                display.SetUnavailable(offline: true);
                return;
            }
            try
            {
                await ReconcileTrustedIdentityAsync(
                    session.StaffId,
                    session.ShopId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                display.SetUnavailable(offline: true);
                return;
            }
            Guid shopId;
            Guid productId;
            Guid.TryParse(session.ShopId, out shopId);
            Guid.TryParse(product.RemoteProductId, out productId);
            var accountScope = await scopeStore.ResolveAsync(
                session.StaffId,
                session.ShopId,
                cancellationToken).ConfigureAwait(false);

            ProductImageIdentity targetIdentity = null;
            ProductImageValidationResult identityValidation;
            var fallbackLoaded = false;
            if (!string.IsNullOrEmpty(accountScope))
            {
                if (!ProductImageIdentity.TryCreate(
                    accountScope,
                    session.ShopId,
                    product.RemoteProductId,
                    product.PrimaryImageVersionId,
                    out targetIdentity,
                    out identityValidation))
                {
                    display.SetUnavailable();
                    return;
                }
                var fallback = await ActiveDiskCache.GetPromotedForProductAsync(
                    accountScope,
                    shopId,
                    productId,
                    variant,
                    cancellationToken).ConfigureAwait(false);
                if (fallback != null)
                {
                    var decodedFallback = await ActiveDecoder.DecodeAsync(
                        fallback.Reference,
                        profile,
                        cancellationToken).ConfigureAwait(false);
                    if (decodedFallback.IsLoaded)
                    {
                        // The last server-bound partition is safe for immediate
                        // offline display. When online we still refresh the
                        // read contract so a cacheScope rotation is observed.
                        display.Apply(decodedFallback);
                        fallbackLoaded = true;
                    }
                }
            }
            if (!fallbackLoaded) display.SetLoading();

            if (!PosAdminWebOptions.TryLoad(out var options, out _) ||
                !PosProductImageStorageOrigin.TryLoad(out var storageOrigin, out _))
            {
                if (!fallbackLoaded) display.SetUnavailable(offline: true);
                return;
            }

            await ReadRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-read immediately before placing credentials in the envelope.
                if (!store.TryRead(out session))
                {
                    if (!fallbackLoaded) display.SetUnavailable(offline: true);
                    return;
                }
                var envelope = new PosProductImageEnvelope(
                    typeof(ProductImageRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                    session.ShopId,
                    session.ShopDeviceId,
                    session.StaffId,
                    session.StaffCredentialVersion,
                    session.PosSessionId,
                    session.DeviceToken,
                    session.SessionToken);
                var request = new PosProductImageReadUrlsRequest(
                    envelope,
                    new[]
                    {
                        new PosProductImageReadRef(
                            product.RemoteProductId,
                            ImageContract.VariantName(variant),
                            product.PrimaryImageVersionId)
                    });
                var cacheScopeReadSequence = Interlocked.Increment(
                    ref _cacheScopeReadSequence);
                using (var client = new PosProductImageClient(options, storageOrigin))
                {
                    var response = await client.ReadUrlsAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccess)
                    {
                        if (response.FailureKind ==
                            PosProductImageFailureKind.AuthDenied)
                        {
                            PosOnlineSyncSignalBus.Signal(
                                OnlineSyncLane.Heartbeat,
                                OnlineSyncLaneTrigger.Foreground);
                        }
                        if (!fallbackLoaded)
                        {
                            display.SetUnavailable(
                                response.FailureKind == PosProductImageFailureKind.RetryableTransport);
                        }
                        return;
                    }
                    string responseAccountScope;
                    await CacheScopeBindingGate.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        PosTrustedDeviceSession responseSession;
                        if (!store.TryRead(out responseSession) ||
                            !SameReadIdentity(session, responseSession))
                        {
                            display.SetUnavailable(offline: true);
                            return;
                        }
                        if (cacheScopeReadSequence < Volatile.Read(
                                ref _lastBoundCacheScopeReadSequence))
                        {
                            responseAccountScope =
                                ProductImageCacheScopeStore.DeriveAccountScope(
                                    response.Value.CacheScope);
                            var activeAccountScope = await scopeStore.ResolveActiveAsync(
                                session.StaffId,
                                session.ShopId,
                                cancellationToken).ConfigureAwait(false);
                            if (!string.Equals(
                                    responseAccountScope,
                                    activeAccountScope,
                                    StringComparison.Ordinal))
                            {
                                display.SetUnavailable();
                                return;
                            }
                        }
                        else
                        {
                            var scopeBinding = await scopeStore.BindWithTransitionAsync(
                                session.StaffId,
                                session.ShopId,
                                response.Value.CacheScope,
                                cancellationToken).ConfigureAwait(false);
                            responseAccountScope = scopeBinding.AccountScope;
                            if (!string.IsNullOrEmpty(scopeBinding.PurgeToken))
                            {
                                Interlocked.Increment(ref _cacheBindingGeneration);
                                await ActiveDiskCache.PurgeAllAsync(cancellationToken)
                                    .ConfigureAwait(false);
                                ActiveDecoder.TrimMemoryCache();
                                if (!await scopeStore.AcknowledgePurgeAsync(
                                        session.StaffId,
                                        session.ShopId,
                                        responseAccountScope,
                                        scopeBinding.PurgeToken,
                                        cancellationToken).ConfigureAwait(false))
                                {
                                    display.SetUnavailable();
                                    return;
                                }
                            }
                            Volatile.Write(
                                ref _lastBoundCacheScopeReadSequence,
                                cacheScopeReadSequence);
                        }
                        session = responseSession;
                    }
                    finally
                    {
                        CacheScopeBindingGate.Release();
                    }
                    if (!string.Equals(
                            accountScope,
                            responseAccountScope,
                            StringComparison.Ordinal))
                    {
                        accountScope = responseAccountScope;
                        if (!ProductImageIdentity.TryCreate(
                            accountScope,
                            session.ShopId,
                            product.RemoteProductId,
                            product.PrimaryImageVersionId,
                            out targetIdentity,
                            out identityValidation))
                        {
                            display.SetUnavailable();
                            return;
                        }
                        fallbackLoaded = false;
                        display.SetLoading();
                    }
                    var item = response.Value.Items[0];
                    if (item.Status != "ready")
                    {
                        if (!fallbackLoaded) display.SetUnavailable();
                        return;
                    }
                    ProductImageMetadata metadata;
                    ProductImageValidationResult metadataValidation;
                    if (!ProductImageMetadata.TryCreate(
                        variant,
                        item.Metadata.MimeType,
                        item.Metadata.Bytes,
                        item.Metadata.Width,
                        item.Metadata.Height,
                        item.Metadata.Sha256,
                        out metadata,
                        out metadataValidation))
                    {
                        if (!fallbackLoaded) display.SetCorrupt();
                        return;
                    }
                    var updatedAt = ParseTimestamp(product.PrimaryImageUpdatedAt);
                    var reference = new ProductImageReference(
                        targetIdentity,
                        variant,
                        metadata,
                        updatedAt);
                    ProductImageCacheEntry cached;
                    var cacheBindingGeneration = Volatile.Read(
                        ref _cacheBindingGeneration);
                    try
                    {
                        cached = await ActiveDiskCache.GetOrAddAsync(
                            reference,
                            async producerCancellation =>
                            {
                                await DownloadGate.WaitAsync(producerCancellation)
                                    .ConfigureAwait(false);
                                try
                                {
                                    var download = await client.DownloadJpegAsync(
                                        item.SignedUrl,
                                        session.ShopId,
                                        item.ProductId,
                                        item.VersionId,
                                        item.Variant,
                                        item.Metadata,
                                        producerCancellation).ConfigureAwait(false);
                                    if (!download.IsSuccess &&
                                        download.Code == "expired_capability")
                                    {
                                        PosTrustedDeviceSession refreshedSession;
                                        if (!store.TryRead(out refreshedSession) ||
                                            !SameReadIdentity(session, refreshedSession))
                                        {
                                            throw new ProductImageReadException(
                                                "read_identity_changed",
                                                false);
                                        }
                                        var refreshedRequest = new PosProductImageReadUrlsRequest(
                                            new PosProductImageEnvelope(
                                                typeof(ProductImageRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                                                refreshedSession.ShopId,
                                                refreshedSession.ShopDeviceId,
                                                refreshedSession.StaffId,
                                                refreshedSession.StaffCredentialVersion,
                                                refreshedSession.PosSessionId,
                                                refreshedSession.DeviceToken,
                                                refreshedSession.SessionToken),
                                            new[]
                                            {
                                                new PosProductImageReadRef(
                                                    product.RemoteProductId,
                                                    item.Variant,
                                                    product.PrimaryImageVersionId)
                                            });
                                        var refreshed = await client.ReadUrlsAsync(
                                            refreshedRequest,
                                            producerCancellation).ConfigureAwait(false);
                                        if (!refreshed.IsSuccess ||
                                            refreshed.Value.Items.Length != 1 ||
                                            refreshed.Value.Items[0].Status != "ready" ||
                                            !string.Equals(
                                                refreshed.Value.CacheScope,
                                                response.Value.CacheScope,
                                                StringComparison.Ordinal) ||
                                            !SameMetadata(item.Metadata, refreshed.Value.Items[0].Metadata))
                                        {
                                            throw new ProductImageReadException(
                                                refreshed.IsSuccess
                                                    ? "read_refresh_invalid"
                                                    : refreshed.Code,
                                                false);
                                        }
                                        var refreshedItem = refreshed.Value.Items[0];
                                        download = await client.DownloadJpegAsync(
                                            refreshedItem.SignedUrl,
                                            refreshedSession.ShopId,
                                            refreshedItem.ProductId,
                                            refreshedItem.VersionId,
                                            refreshedItem.Variant,
                                            refreshedItem.Metadata,
                                            producerCancellation).ConfigureAwait(false);
                                    }
                                    if (!download.IsSuccess)
                                        throw new ProductImageReadException(
                                            download.Code,
                                            download.Retryable);
                                    return new MemoryStream(
                                        download.CopyBytes(),
                                        writable: false);
                                }
                                finally
                                {
                                    DownloadGate.Release();
                                }
                            },
                            cancellationToken,
                            () => Volatile.Read(ref _cacheBindingGeneration) ==
                                cacheBindingGeneration &&
                                ReadProductCacheGeneration(product.RemoteProductId) ==
                                productCacheGeneration).ConfigureAwait(false);
                    }
                    catch (ProductImageReadException error)
                    {
                        if (!fallbackLoaded) display.SetUnavailable(error.Retryable);
                        return;
                    }
                    catch (InvalidDataException)
                    {
                        if (!fallbackLoaded) display.SetCorrupt();
                        return;
                    }
                    if (cached == null)
                    {
                        if (!fallbackLoaded) display.SetUnavailable();
                        return;
                    }
                    PosTrustedDeviceSession completedSession;
                    string completedAccountScope;
                    try
                    {
                        completedAccountScope = await scopeStore.ResolveActiveAsync(
                            session.StaffId,
                            session.ShopId,
                            CancellationToken.None).ConfigureAwait(false);
                        if (!store.TryRead(out completedSession) ||
                            !SameReadIdentity(session, completedSession) ||
                            !string.Equals(
                                accountScope,
                                completedAccountScope,
                                StringComparison.Ordinal))
                        {
                            await ActiveDiskCache.PurgeAccountAsync(
                                accountScope,
                                CancellationToken.None).ConfigureAwait(false);
                            ActiveDecoder.TrimMemoryCache();
                            display.SetUnavailable(offline: true);
                            return;
                        }
                    }
                    catch
                    {
                        await ActiveDiskCache.PurgeAccountAsync(
                            accountScope,
                            CancellationToken.None).ConfigureAwait(false);
                        ActiveDecoder.TrimMemoryCache();
                        throw;
                    }
                    var decoded = await ActiveDecoder.DecodeAsync(
                        reference,
                        profile,
                        cancellationToken).ConfigureAwait(false);
                    if (!decoded.IsLoaded)
                    {
                        if (!fallbackLoaded) display.Apply(decoded);
                        return;
                    }
                    await ActiveDiskCache.PromoteVariantAsync(reference, cancellationToken)
                        .ConfigureAwait(false);
                    display.Apply(decoded);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Virtualized row/editor lifetime cancellation is expected.
            }
            catch (IOException)
            {
                if (!fallbackLoaded) display.SetUnavailable();
            }
            finally
            {
                ReadRequestGate.Release();
            }
        }

        public static async Task PurgeProductAsync(
            ProductDetailsRow product,
            CancellationToken cancellationToken = default)
        {
            if (product == null ||
                !PosProductImageContractV1.IsCanonicalUuid(product.RemoteProductId))
                return;
            IncrementProductCacheGeneration(product.RemoteProductId);
            PosTrustedDeviceSession session;
            if (!CreateTrustedStore().TryRead(out session) ||
                !Guid.TryParse(session.ShopId, out var shopId) ||
                !Guid.TryParse(product.RemoteProductId, out var productId))
                return;
            var accountScope = await new ProductImageCacheScopeStore(
                new SqliteConnectionFactory(PosDbOptions.Default()))
                .ResolveAsync(
                    session.StaffId,
                    session.ShopId,
                    cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(accountScope))
            {
                await ActiveDiskCache.PurgeProductAsync(
                    accountScope,
                    shopId,
                    productId,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            ActiveDecoder.TrimMemoryCache();
        }

        public static Task<ProductImageCacheSnapshot> GetCacheSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            return ActiveDiskCache.GetSnapshotAsync(cancellationToken);
        }

        public static async Task ReconcileTrustedIdentityAsync(
            string staffId,
            string shopId,
            CancellationToken cancellationToken = default(CancellationToken),
            bool forcePurge = false)
        {
            if (!PosProductImageContractV1.IsCanonicalUuid(staffId) ||
                !PosProductImageContractV1.IsCanonicalUuid(shopId))
            {
                throw new IOException("product_image_cache_identity_invalid");
            }
            await CacheScopeBindingGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var scopeStore = new ProductImageCacheScopeStore(
                    new SqliteConnectionFactory(PosDbOptions.Default()));
                var purgeToken = await scopeStore.ObserveTrustedIdentityAsync(
                    staffId,
                    shopId,
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(purgeToken) && !forcePurge) return;
                Interlocked.Increment(ref _cacheBindingGeneration);
                await ActiveDiskCache.PurgeAllAsync(cancellationToken)
                    .ConfigureAwait(false);
                ActiveDecoder.TrimMemoryCache();
                if (!string.IsNullOrEmpty(purgeToken) &&
                    !await scopeStore.AcknowledgePurgeAsync(
                        staffId,
                        shopId,
                        null,
                        purgeToken,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException("product_image_cache_purge_ack_failed");
                }
            }
            finally
            {
                CacheScopeBindingGate.Release();
            }
        }

        private static DateTimeOffset? ParseTimestamp(string value)
        {
            DateTimeOffset parsed;
            return PosProductImageContractV1.IsCanonicalTimestamp(value) &&
                   DateTimeOffset.TryParse(value, out parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        private static long ReadProductCacheGeneration(string productId)
        {
            long generation;
            return ProductCacheGenerations.TryGetValue(productId, out generation)
                ? generation
                : 0L;
        }

        private static void IncrementProductCacheGeneration(string productId)
        {
            ProductCacheGenerations.AddOrUpdate(
                productId,
                1L,
                (_, generation) => unchecked(generation + 1L));
        }

        private static bool SameReadIdentity(
            PosTrustedDeviceSession left,
            PosTrustedDeviceSession right)
        {
            return left != null && right != null &&
                   left.ShopId == right.ShopId &&
                   left.ShopDeviceId == right.ShopDeviceId &&
                   left.StaffId == right.StaffId &&
                   left.StaffCredentialVersion == right.StaffCredentialVersion;
        }

        private static PosTrustedDeviceStore CreateTrustedStore()
        {
            var profileName = AcceptanceRuntimeOverride.Value?.ProfileName;
            return string.IsNullOrEmpty(profileName)
                ? new PosTrustedDeviceStore()
                : new PosTrustedDeviceStore(profileName);
        }

        private static ProductImageDiskCache ActiveDiskCache =>
            AcceptanceRuntimeOverride.Value?.DiskCache ?? DiskCache;

        private static ProductImageDecodeService ActiveDecoder =>
            AcceptanceRuntimeOverride.Value?.Decoder ?? Decoder;

        private sealed class AcceptanceRuntimeContext : IDisposable
        {
            internal AcceptanceRuntimeContext(
                string profileName,
                string cacheRoot)
            {
                ProfileName = profileName;
                DiskCache = new ProductImageDiskCache(
                    new ProductImageCacheOptions(
                        cacheRoot,
                        maximumBytes: 8 * 1024 * 1024,
                        maximumEntries: 32,
                        maximumConcurrentProducers: 2));
                Decoder = new ProductImageDecodeService(
                    new ProductImageDiskCacheStreamProvider(DiskCache));
            }

            internal string ProfileName { get; }
            internal ProductImageDiskCache DiskCache { get; }
            internal ProductImageDecodeService Decoder { get; }

            public void Dispose()
            {
                Decoder.TrimMemoryCache();
                DiskCache.Dispose();
            }
        }

        private sealed class TrustedProfileScope : IDisposable
        {
            private readonly AcceptanceRuntimeContext _previous;
            private readonly AcceptanceRuntimeContext _current;
            private bool _disposed;

            internal TrustedProfileScope(
                AcceptanceRuntimeContext previous,
                AcceptanceRuntimeContext current)
            {
                _previous = previous;
                _current = current;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                AcceptanceRuntimeOverride.Value = _previous;
                _current.Dispose();
            }
        }

        private static bool SameMetadata(
            PosProductImageUploadMetadata left,
            PosProductImageUploadMetadata right)
        {
            return left != null && right != null &&
                   left.Bytes == right.Bytes &&
                   left.Width == right.Width &&
                   left.Height == right.Height &&
                   left.MimeType == right.MimeType &&
                   left.Sha256 == right.Sha256;
        }

        private static async Task<bool> TryLoadStagedPreviewAsync(
            ProductDetailsRow product,
            ProductImageVariant variant,
            ProductImageDisplayViewModel display,
            CancellationToken cancellationToken)
        {
            if (product.Id <= 0) return false;
            try
            {
                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                var operation = await new ProductImageOperationOutboxRepository(
                        factory)
                    .GetLatestForProductAsync(product.Id)
                    .ConfigureAwait(false);
                if (operation == null ||
                    operation.OperationKind != ProductImageOperationKinds.Replace ||
                    operation.State == ProductImageOperationStates.Completed ||
                    operation.State == ProductImageOperationStates.FailedBlocked)
                {
                    return false;
                }
                var main = variant == ProductImageVariant.Main;
                var identity = main
                    ? operation.StagedMainIdentity
                    : operation.StagedThumbIdentity;
                if (string.IsNullOrWhiteSpace(identity)) return false;
                ProductImageMetadata metadata;
                ProductImageValidationResult validation;
                if (!ProductImageMetadata.TryCreate(
                    variant,
                    ImageContract.WireMimeType,
                    main
                        ? operation.MainBytes.GetValueOrDefault()
                        : operation.ThumbBytes.GetValueOrDefault(),
                    main
                        ? operation.MainWidth.GetValueOrDefault()
                        : operation.ThumbWidth.GetValueOrDefault(),
                    main
                        ? operation.MainHeight.GetValueOrDefault()
                        : operation.ThumbHeight.GetValueOrDefault(),
                    main ? operation.MainSha256 : operation.ThumbSha256,
                    out metadata,
                    out validation))
                {
                    display.SetCorrupt();
                    return true;
                }
                using (var stream = await new ProductImageStagingStore()
                    .OpenVerifiedReadAsync(
                        identity,
                        variant,
                        metadata,
                        cancellationToken).ConfigureAwait(false))
                {
                    var bytes = new byte[checked((int)stream.Length)];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = await stream.ReadAsync(
                            bytes,
                            offset,
                            bytes.Length - offset,
                            cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException();
                        offset += read;
                    }
                    var preview = await ProductImageWorkflowService
                        .DecodeLocalPreviewAsync(bytes, cancellationToken)
                        .ConfigureAwait(false);
                    display.SetLoaded(preview);
                    return true;
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                display.SetCorrupt();
                return true;
            }
            catch (IOException)
            {
                display.SetUnavailable();
                return true;
            }
            catch
            {
                display.SetUnavailable();
                return true;
            }
        }

        private sealed class ProductImageReadException : IOException
        {
            public ProductImageReadException(string code, bool retryable)
                : base(string.IsNullOrWhiteSpace(code) ? "image_read_failed" : code)
            {
                Retryable = retryable;
            }

            public bool Retryable { get; }
        }
    }
}
