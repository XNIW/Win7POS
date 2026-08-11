using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Win7POS.Core.Images;

namespace Win7POS.Data.Images
{
    public sealed class ProductImageCacheOptions
    {
        public const long DefaultMaximumBytes = 32L * 1024L * 1024L;
        public const int DefaultMaximumEntries = 256;
        public const int DefaultMaximumConcurrentProducers = 2;
        public const long MinimumMaximumBytes = 3L * 1024L * 1024L;
        public const int MinimumMaximumEntries = 2;

        public ProductImageCacheOptions(
            string rootPath,
            long maximumBytes = DefaultMaximumBytes,
            int maximumEntries = DefaultMaximumEntries,
            int maximumConcurrentProducers = DefaultMaximumConcurrentProducers,
            TimeSpan? staleTemporaryFileAge = null)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("A cache root is required.", nameof(rootPath));
            }

            var fullRoot = Path.GetFullPath(rootPath);
            if (IsFileSystemRoot(fullRoot) || IsBelowProgramFiles(fullRoot))
            {
                throw new ArgumentException(
                    "The image cache must be outside Program Files and below a dedicated root.",
                    nameof(rootPath));
            }

            if (maximumBytes < MinimumMaximumBytes ||
                maximumBytes > 256L * 1024L * 1024L)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }

            if (maximumEntries < MinimumMaximumEntries ||
                maximumEntries > 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            }

            if (maximumConcurrentProducers < 1 ||
                maximumConcurrentProducers > ProductImageContractV1.DownloadConcurrency)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumConcurrentProducers));
            }

            var temporaryAge = staleTemporaryFileAge ?? TimeSpan.FromHours(24);
            if (temporaryAge < TimeSpan.Zero || temporaryAge > TimeSpan.FromDays(30))
            {
                throw new ArgumentOutOfRangeException(nameof(staleTemporaryFileAge));
            }

            RootPath = fullRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            MaximumBytes = maximumBytes;
            MaximumEntries = maximumEntries;
            MaximumConcurrentProducers = maximumConcurrentProducers;
            StaleTemporaryFileAge = temporaryAge;
        }

        public string RootPath { get; }
        public long MaximumBytes { get; }
        public int MaximumEntries { get; }
        public int MaximumConcurrentProducers { get; }
        public TimeSpan StaleTemporaryFileAge { get; }

        public static ProductImageCacheOptions CreateDefault()
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return new ProductImageCacheOptions(
                Path.Combine(localAppData, "Win7POS", "ImageCache"));
        }

        private static bool IsFileSystemRoot(string path)
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) &&
                   string.Equals(
                       path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBelowProgramFiles(string path)
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            return roots
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Path.GetFullPath)
                .Any(root => IsWithin(path, root));
        }

        internal static bool IsWithin(string candidate, string root)
        {
            var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return string.Equals(
                       normalizedCandidate,
                       normalizedRoot,
                       StringComparison.OrdinalIgnoreCase) ||
                   normalizedCandidate.StartsWith(
                       normalizedRoot + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class ProductImageCacheEntry
    {
        private readonly byte[] _bytes;

        internal ProductImageCacheEntry(
            ProductImageReference reference,
            byte[] bytes,
            DateTimeOffset lastAccessUtc)
        {
            Reference = reference ?? throw new ArgumentNullException(nameof(reference));
            _bytes = bytes == null
                ? throw new ArgumentNullException(nameof(bytes))
                : (byte[])bytes.Clone();
            LastAccessUtc = lastAccessUtc;
        }

        public ProductImageReference Reference { get; }
        public int ByteSize => _bytes.Length;
        public DateTimeOffset LastAccessUtc { get; }

        public byte[] CopyBytes()
        {
            return (byte[])_bytes.Clone();
        }

        public Stream OpenRead()
        {
            return new MemoryStream(_bytes, writable: false);
        }
    }

    public sealed class ProductImageCacheSnapshot
    {
        internal ProductImageCacheSnapshot(
            long totalBytes,
            int entryCount,
            int temporaryFileCount,
            bool indexWasRebuilt)
        {
            TotalBytes = totalBytes;
            EntryCount = entryCount;
            TemporaryFileCount = temporaryFileCount;
            IndexWasRebuilt = indexWasRebuilt;
        }

        public long TotalBytes { get; }
        public int EntryCount { get; }
        public int TemporaryFileCount { get; }
        public bool IndexWasRebuilt { get; }
    }

    public sealed class ProductImageDiskCache : IDisposable
    {
        private const int IndexSchemaVersion = 1;
        private const int MetadataMaximumBytes = 16 * 1024;
        private const string IndexFileName = "index-v1.json";
        private const string RootLockFileName = ".cache.lock";
        private readonly ProductImageCacheOptions _options;
        private readonly object _initializeGate = new object();
        private readonly object _flightGate = new object();
        private readonly object _lifecycleGate = new object();
        private readonly ManualResetEventSlim _operationsDrained =
            new ManualResetEventSlim(true);
        private readonly SemaphoreSlim _stateGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _producerGate;
        private readonly Dictionary<string, ProductImageCacheMetadata> _entries =
            new Dictionary<string, ProductImageCacheMetadata>(StringComparer.Ordinal);
        private readonly Dictionary<string, CacheFlight> _flights =
            new Dictionary<string, CacheFlight>(StringComparer.Ordinal);
        private Task _initializeTask;
        private FileStream _rootLock;
        private long _nextStageSequence;
        private int _activeOperations;
        private bool _indexWasRebuilt;
        private bool _disposed;

        public ProductImageDiskCache(ProductImageCacheOptions options = null)
        {
            _options = options ?? ProductImageCacheOptions.CreateDefault();
            _producerGate = new SemaphoreSlim(
                _options.MaximumConcurrentProducers,
                _options.MaximumConcurrentProducers);
        }

        public string RootPath => _options.RootPath;

        public async Task<ProductImageCacheEntry> GetAsync(
            ProductImageReference reference,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            using (EnterOperation())
            {
                await EnsureInitializedAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(
                            () => TryReadEntry(reference, touch: true),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }

        public async Task<ProductImageCacheEntry> GetPromotedAsync(
            ProductImageIdentity identity,
            ProductImageVariant variant,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (!ProductImageContractV1.IsSupportedVariant(variant))
                throw new ArgumentOutOfRangeException(nameof(variant));
            using (EnterOperation())
            {
                await EnsureInitializedAsync().ConfigureAwait(false);
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(
                        () =>
                        {
                            var candidate = _entries.Values
                                .Where(entry => entry.IsPromoted &&
                                    MetadataMatchesIdentity(entry, identity, variant))
                                .OrderByDescending(entry => entry.StageSequence)
                                .FirstOrDefault();
                            if (candidate == null ||
                                !TryCreateReference(candidate, out var reference))
                            {
                                return null;
                            }
                            return TryReadEntry(reference, touch: true);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }

        public async Task<ProductImageCacheEntry> GetPromotedForProductAsync(
            string accountScope,
            Guid shopId,
            Guid productId,
            ProductImageVariant variant,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(accountScope))
                throw new ArgumentException("image_cache_account_scope_required", nameof(accountScope));
            if (!ProductImageContractV1.IsSupportedVariant(variant))
                throw new ArgumentOutOfRangeException(nameof(variant));
            using (EnterOperation())
            {
                await EnsureInitializedAsync().ConfigureAwait(false);
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(
                        () =>
                        {
                            var candidate = _entries.Values
                                .Where(entry => entry.IsPromoted &&
                                    string.Equals(entry.AccountScope, accountScope, StringComparison.Ordinal) &&
                                    string.Equals(entry.ShopId, Canonical(shopId), StringComparison.Ordinal) &&
                                    string.Equals(entry.ProductId, Canonical(productId), StringComparison.Ordinal) &&
                                    string.Equals(
                                        entry.Variant,
                                        ProductImageContractV1.VariantName(variant),
                                        StringComparison.Ordinal))
                                .OrderByDescending(entry => entry.StageSequence)
                                .FirstOrDefault();
                            if (candidate == null ||
                                !TryCreateReference(candidate, out var reference))
                            {
                                return null;
                            }
                            return TryReadEntry(reference, touch: true);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }

        public Task<int> PurgeProductAsync(
            string accountScope,
            Guid shopId,
            Guid productId,
            Guid? keepVersionId = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return PurgeScopeAsync(
                metadata =>
                    string.Equals(metadata.AccountScope, accountScope, StringComparison.Ordinal) &&
                    string.Equals(metadata.ShopId, Canonical(shopId), StringComparison.Ordinal) &&
                    string.Equals(metadata.ProductId, Canonical(productId), StringComparison.Ordinal) &&
                    (!keepVersionId.HasValue ||
                     !string.Equals(
                         metadata.VersionId,
                         Canonical(keepVersionId.Value),
                         StringComparison.Ordinal)),
                cancellationToken);
        }

        public Task<int> PurgeShopAsync(
            string accountScope,
            Guid shopId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return PurgeScopeAsync(
                metadata =>
                    string.Equals(metadata.AccountScope, accountScope, StringComparison.Ordinal) &&
                    string.Equals(metadata.ShopId, Canonical(shopId), StringComparison.Ordinal),
                cancellationToken);
        }

        public Task<int> PurgeAccountAsync(
            string accountScope,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return PurgeScopeAsync(
                metadata => string.Equals(
                    metadata.AccountScope,
                    accountScope,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        public Task<int> PurgeAllAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return PurgeScopeAsync(metadata => true, cancellationToken);
        }

        public async Task<ProductImageCacheEntry> GetOrAddAsync(
            ProductImageReference reference,
            Func<CancellationToken, Task<Stream>> streamFactory,
            CancellationToken cancellationToken = default(CancellationToken),
            Func<bool> commitAllowed = null)
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            if (streamFactory == null)
            {
                throw new ArgumentNullException(nameof(streamFactory));
            }

            using (EnterOperation())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureInitializedAsync().ConfigureAwait(false);
                var key = ProductImageCacheKey.FromReference(reference).FileStem;
                CacheFlight flight;
                lock (_lifecycleGate)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(
                            nameof(ProductImageDiskCache));
                    }

                    lock (_flightGate)
                    {
                        if (!_flights.TryGetValue(key, out flight))
                        {
                            flight = new CacheFlight();
                            _flights.Add(key, flight);
                            var stageSequence = checked(++_nextStageSequence);
                            flight.Task = RunFlightAsync(
                                key,
                                flight,
                                reference,
                                stageSequence,
                                streamFactory,
                                commitAllowed);
                        }

                        flight.ConsumerCount++;
                    }
                }

                return await WaitForFlightAsync(
                        flight,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public async Task PromoteVariantAsync(
            ProductImageReference reference,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            using (EnterOperation())
            {
                await EnsureInitializedAsync().ConfigureAwait(false);
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await Task.Run(
                            () =>
                            {
                                var target = TryReadEntry(reference, touch: false);
                                if (target == null ||
                                    !_entries.TryGetValue(
                                        ProductImageCacheKey
                                            .FromReference(reference)
                                            .FileStem,
                                        out var targetMetadata))
                                {
                                    throw new InvalidDataException(
                                        "image_cache_promotion_target_missing");
                                }

                                var sameVariant = _entries.Values
                                    .Where(entry =>
                                        IsSameProductVariant(entry, reference))
                                    .ToArray();
                                var latestSequence = sameVariant
                                    .Max(entry => entry.StageSequence);
                                if (targetMetadata.StageSequence != latestSequence)
                                {
                                    throw new InvalidDataException(
                                        "image_cache_promotion_superseded");
                                }

                                targetMetadata.IsPromoted = true;
                                WriteMetadataAtomically(targetMetadata);
                                foreach (var victim in sameVariant.Where(entry =>
                                             !string.Equals(
                                                 entry.FileStem,
                                                 targetMetadata.FileStem,
                                                 StringComparison.Ordinal)))
                                {
                                    RemoveEntryFiles(victim.FileStem);
                                    _entries.Remove(victim.FileStem);
                                }

                                EnforceBudget();
                                WriteIndexAtomically();
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }

        public async Task CleanupAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (EnterOperation())
            {
                await EnsureInitializedAsync().ConfigureAwait(false);
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await Task.Run(
                            () =>
                            {
                                CleanupTemporaryFiles();
                                RemoveUncommittedBlobs();
                                EnforceBudget();
                                WriteIndexAtomically();
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }

        public async Task<ProductImageCacheSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (EnterOperation())
            {
                await EnsureInitializedAsync().ConfigureAwait(false);
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(
                            () => new ProductImageCacheSnapshot(
                                GetAccountedDirectoryBytes(),
                                _entries.Count,
                                EnumerateFilesBounded(
                                        "*.tmp",
                                        MaximumDirectoryFiles())
                                    .Count(),
                                _indexWasRebuilt),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }

        private async Task<int> PurgeScopeAsync(
            Func<ProductImageCacheMetadata, bool> predicate,
            CancellationToken cancellationToken)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            using (EnterOperation())
            {
                await EnsureInitializedAsync().ConfigureAwait(false);
                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(
                        () =>
                        {
                            var victims = _entries.Values
                                .Where(predicate)
                                .Select(entry => entry.FileStem)
                                .ToArray();
                            foreach (var victim in victims)
                            {
                                RemoveEntryFiles(victim);
                                _entries.Remove(victim);
                            }
                            if (victims.Length > 0) WriteIndexAtomically();
                            return victims.Length;
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
        }

        private Task EnsureInitializedAsync()
        {
            lock (_initializeGate)
            {
                return _initializeTask ??
                       (_initializeTask = Task.Run((Action)Initialize));
            }
        }

        private void Initialize()
        {
            AssertRootChainHasNoReparsePoints();
            Directory.CreateDirectory(_options.RootPath);
            AssertContained(_options.RootPath);
            AssertRootChainHasNoReparsePoints();
            AcquireRootLock();
            try
            {
                CleanupTemporaryFiles(removeAll: true);
                if (!TryLoadIndex())
                {
                    _indexWasRebuilt = true;
                    RebuildIndex();
                }

                ReconcilePromotedVariants();
                RemoveUncommittedBlobs();
                EnforceBudget();
                WriteIndexAtomically();
                _nextStageSequence = _entries.Count == 0
                    ? 0
                    : _entries.Values.Max(entry => entry.StageSequence);
            }
            catch
            {
                _rootLock?.Dispose();
                _rootLock = null;
                throw;
            }
        }

        private async Task<ProductImageCacheEntry> RunFlightAsync(
            string key,
            CacheFlight flight,
            ProductImageReference reference,
            long stageSequence,
            Func<CancellationToken, Task<Stream>> streamFactory,
            Func<bool> commitAllowed)
        {
            try
            {
                return await ProduceEntryAsync(
                        reference,
                        stageSequence,
                        streamFactory,
                        commitAllowed,
                        flight.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_flightGate)
                {
                    if (_flights.TryGetValue(key, out var current) &&
                        ReferenceEquals(current, flight))
                    {
                        _flights.Remove(key);
                    }
                }

                flight.Cancellation.Dispose();
            }
        }

        private async Task<ProductImageCacheEntry> ProduceEntryAsync(
            ProductImageReference reference,
            long stageSequence,
            Func<CancellationToken, Task<Stream>> streamFactory,
            Func<bool> commitAllowed,
            CancellationToken cancellationToken)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = await Task.Run(
                        () => TryReadEntry(reference, touch: true),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existing != null)
                {
                    return existing;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            await _producerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] bytes;
                using (var stream = await streamFactory(cancellationToken)
                           .ConfigureAwait(false))
                {
                    if (stream == null || !stream.CanRead)
                    {
                        throw new InvalidDataException("image_stream_unavailable");
                    }

                    bytes = await ReadBoundedAsync(
                            stream,
                            reference.Metadata.ByteSize,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var validation = ProductImageBinaryPolicy.ValidateCanonicalWireJpeg(
                    bytes,
                    reference.Metadata);
                if (!validation.IsValid)
                {
                    throw new InvalidDataException(
                        validation.Messages.FirstOrDefault() ?? "image_invalid");
                }

                await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await Task.Run(
                            () =>
                            {
                                if (commitAllowed != null && !commitAllowed())
                                {
                                    throw new IOException(
                                        "image_cache_binding_changed");
                                }
                                return CommitEntry(reference, stageSequence, bytes);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _stateGate.Release();
                }
            }
            finally
            {
                _producerGate.Release();
            }
        }

        private async Task<ProductImageCacheEntry> WaitForFlightAsync(
            CacheFlight flight,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!cancellationToken.CanBeCanceled)
                {
                    return await flight.Task.ConfigureAwait(false);
                }

                var cancellationSignal =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(
                           () => cancellationSignal.TrySetResult(true)))
                {
                    var completed = await Task.WhenAny(
                            flight.Task,
                            cancellationSignal.Task)
                        .ConfigureAwait(false);
                    if (completed != flight.Task)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }

                return await flight.Task.ConfigureAwait(false);
            }
            finally
            {
                ReleaseFlightConsumer(flight);
            }
        }

        private void ReleaseFlightConsumer(CacheFlight flight)
        {
            lock (_flightGate)
            {
                flight.ConsumerCount--;
                if (flight.ConsumerCount <= 0)
                {
                    if (!flight.Task.IsCompleted)
                    {
                        flight.Cancellation.Cancel();
                    }
                }
            }
        }

        private ProductImageCacheEntry CommitEntry(
            ProductImageReference reference,
            long stageSequence,
            byte[] bytes)
        {
            var key = ProductImageCacheKey.FromReference(reference);
            var existing = TryReadEntry(reference, touch: true);
            if (existing != null)
            {
                return existing;
            }

            if (_entries.TryGetValue(key.FileStem, out var conflicting) &&
                !MetadataMatchesReference(conflicting, reference))
            {
                throw new InvalidDataException("image_cache_key_conflict");
            }

            if (_entries.Values.Any(entry =>
                    IsSameProductVariant(entry, reference) &&
                    entry.StageSequence > stageSequence))
            {
                throw new InvalidDataException(
                    "image_cache_version_superseded");
            }

            var now = DateTimeOffset.UtcNow;
            var metadata = CreateMetadata(
                reference,
                key,
                now,
                stageSequence,
                isPromoted: false);
            var nonce = Guid.NewGuid().ToString("N");
            var dataTemp = CachePath(key.FileStem + "." + nonce + ".img.tmp");
            var metadataTemp = CachePath(key.FileStem + "." + nonce + ".meta.tmp");
            var dataPath = DataPath(key.FileStem);
            var metadataPath = MetadataPath(key.FileStem);

            try
            {
                WriteFileDurably(dataTemp, bytes);
                WriteFileDurably(metadataTemp, Serialize(metadata));

                var tempBytes = ReadFileBounded(dataTemp, reference.Metadata.ByteSize);
                var validation = ProductImageBinaryPolicy.ValidateCanonicalWireJpeg(
                    tempBytes,
                    reference.Metadata);
                if (!validation.IsValid)
                {
                    throw new InvalidDataException(
                        validation.Messages.FirstOrDefault() ?? "image_cache_write_invalid");
                }

                if (File.Exists(dataPath) || File.Exists(metadataPath))
                {
                    var committed = TryReadEntry(reference, touch: true);
                    if (committed != null)
                    {
                        return committed;
                    }

                    throw new InvalidDataException("image_cache_key_conflict");
                }

                File.Move(dataTemp, dataPath);
                File.Move(metadataTemp, metadataPath);
                _entries[key.FileStem] = metadata;
                EnforceBudget(reference, key.FileStem);
                if (!_entries.ContainsKey(key.FileStem))
                {
                    throw new InvalidDataException(
                        "image_cache_staging_budget_unavailable");
                }

                WriteIndexAtomically();
                return new ProductImageCacheEntry(reference, bytes, now);
            }
            finally
            {
                DeleteContainedFileIfPresent(dataTemp);
                DeleteContainedFileIfPresent(metadataTemp);
            }
        }

        private ProductImageCacheEntry TryReadEntry(
            ProductImageReference reference,
            bool touch)
        {
            var key = ProductImageCacheKey.FromReference(reference);
            if (!_entries.TryGetValue(key.FileStem, out var metadata))
            {
                return null;
            }

            if (!MetadataMatchesReference(metadata, reference))
            {
                return null;
            }

            var dataPath = DataPath(key.FileStem);
            var metadataPath = MetadataPath(key.FileStem);
            if (!File.Exists(dataPath) || !File.Exists(metadataPath))
            {
                RemoveEntryFiles(key.FileStem);
                _entries.Remove(key.FileStem);
                WriteIndexAtomically();
                return null;
            }

            byte[] bytes;
            try
            {
                var committedMetadata = Deserialize<ProductImageCacheMetadata>(
                    ReadFileBounded(metadataPath, MetadataMaximumBytes));
                if (committedMetadata == null ||
                    !MetadataEquivalent(metadata, committedMetadata))
                {
                    throw new InvalidDataException("image_cache_metadata_invalid");
                }

                bytes = ReadFileBounded(dataPath, metadata.ByteSize);
                var validation = ProductImageBinaryPolicy.ValidateCanonicalWireJpeg(
                    bytes,
                    reference.Metadata);
                if (!validation.IsValid)
                {
                    throw new InvalidDataException("image_cache_payload_invalid");
                }
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is SerializationException ||
                error is InvalidDataException)
            {
                RemoveEntryFiles(key.FileStem);
                _entries.Remove(key.FileStem);
                WriteIndexAtomically();
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (touch)
            {
                metadata.LastAccessUtcTicks = now.UtcDateTime.Ticks;
                WriteMetadataAtomically(metadata);
                WriteIndexAtomically();
            }

            return new ProductImageCacheEntry(reference, bytes, now);
        }

        private bool TryLoadIndex()
        {
            var path = CachePath(IndexFileName);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var index = Deserialize<ProductImageCacheIndex>(
                    ReadFileBounded(path, MaximumIndexBytes()));
                if (index == null ||
                    index.SchemaVersion != IndexSchemaVersion ||
                    index.Entries == null ||
                    index.Entries.Count > MaximumScanEntries())
                {
                    return false;
                }

                var loaded = new Dictionary<string, ProductImageCacheMetadata>(
                    StringComparer.Ordinal);
                foreach (var entry in index.Entries)
                {
                    if (!MetadataShapeIsValid(entry) ||
                        loaded.ContainsKey(entry.FileStem))
                    {
                        return false;
                    }

                    var metadataPath = MetadataPath(entry.FileStem);
                    var dataPath = DataPath(entry.FileStem);
                    if (!File.Exists(metadataPath) || !File.Exists(dataPath))
                    {
                        return false;
                    }

                    var committed = Deserialize<ProductImageCacheMetadata>(
                        ReadFileBounded(metadataPath, MetadataMaximumBytes));
                    if (committed == null || !MetadataEquivalent(entry, committed))
                    {
                        return false;
                    }

                    loaded.Add(entry.FileStem, entry);
                }

                _entries.Clear();
                foreach (var pair in loaded)
                {
                    _entries.Add(pair.Key, pair.Value);
                }

                return true;
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is SerializationException ||
                error is InvalidDataException)
            {
                return false;
            }
        }

        private void RebuildIndex()
        {
            _entries.Clear();
            var metadataFiles = EnumerateFilesBounded(
                    "*.meta",
                    MaximumScanEntries())
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var metadataPath in metadataFiles)
            {
                try
                {
                    var metadata = Deserialize<ProductImageCacheMetadata>(
                        ReadFileBounded(metadataPath, MetadataMaximumBytes));
                    if (metadata == null ||
                        !MetadataShapeIsValid(metadata) ||
                        !string.Equals(
                            Path.GetFileName(metadataPath),
                            metadata.FileStem + ".meta",
                            StringComparison.Ordinal))
                    {
                        DeleteContainedFileIfPresent(metadataPath);
                        continue;
                    }

                    var dataPath = DataPath(metadata.FileStem);
                    if (!File.Exists(dataPath))
                    {
                        DeleteContainedFileIfPresent(metadataPath);
                        continue;
                    }

                    if (!TryCreateReference(metadata, out var reference))
                    {
                        RemoveEntryFiles(metadata.FileStem);
                        continue;
                    }

                    var bytes = ReadFileBounded(dataPath, metadata.ByteSize);
                    var validation = ProductImageBinaryPolicy.ValidateCanonicalWireJpeg(
                        bytes,
                        reference.Metadata);
                    if (!validation.IsValid)
                    {
                        RemoveEntryFiles(metadata.FileStem);
                        continue;
                    }

                    _entries[metadata.FileStem] = metadata;
                }
                catch (Exception error) when (
                    error is IOException ||
                    error is UnauthorizedAccessException ||
                    error is SerializationException ||
                    error is InvalidDataException)
                {
                    DeleteContainedFileIfPresent(metadataPath);
                }
            }
        }

        private void EnforceBudget(
            ProductImageReference stagedReference = null,
            string stagedFileStem = null)
        {
            var ordered = _entries.Values
                .OrderBy(entry => entry.IsPromoted ? 1 : 0)
                .ThenBy(entry => entry.LastAccessUtcTicks)
                .ThenBy(entry => entry.FileStem, StringComparer.Ordinal)
                .ToList();
            var totalBytes = CalculateCommittedCacheBytes();
            while ((totalBytes > _options.MaximumBytes ||
                    ordered.Count > _options.MaximumEntries) &&
                   ordered.Count > 0)
            {
                var victim = ordered.FirstOrDefault(entry =>
                    !IsProtectedDuringStaging(
                        entry,
                        stagedReference,
                        stagedFileStem));
                if (victim == null)
                {
                    if (string.IsNullOrEmpty(stagedFileStem) ||
                        !_entries.TryGetValue(stagedFileStem, out victim))
                    {
                        throw new InvalidDataException(
                            "image_cache_budget_unrecoverable");
                    }
                }

                ordered.Remove(victim);
                RemoveEntryFiles(victim.FileStem);
                _entries.Remove(victim.FileStem);
                totalBytes = CalculateCommittedCacheBytes();
            }
        }

        private static bool IsProtectedDuringStaging(
            ProductImageCacheMetadata metadata,
            ProductImageReference stagedReference,
            string stagedFileStem)
        {
            if (stagedReference == null)
            {
                return false;
            }

            return string.Equals(
                       metadata.FileStem,
                       stagedFileStem,
                       StringComparison.Ordinal) ||
                   (metadata.IsPromoted &&
                    IsSameProductVariant(metadata, stagedReference));
        }

        private void ReconcilePromotedVariants()
        {
            var duplicateVictims = _entries.Values
                .Where(entry => entry.IsPromoted)
                .GroupBy(
                    ProductVariantScopeKey,
                    StringComparer.Ordinal)
                .SelectMany(group => group
                    .OrderByDescending(entry => entry.StageSequence)
                    .ThenByDescending(
                        entry => entry.FileStem,
                        StringComparer.Ordinal)
                    .Skip(1))
                .Select(entry => entry.FileStem)
                .ToArray();
            foreach (var victim in duplicateVictims)
            {
                RemoveEntryFiles(victim);
                _entries.Remove(victim);
            }
        }

        private static string ProductVariantScopeKey(
            ProductImageCacheMetadata metadata)
        {
            return string.Join(
                "\n",
                metadata.AccountScope,
                metadata.ShopId,
                metadata.ProductId,
                metadata.Variant);
        }

        private static bool IsSameProductVariant(
            ProductImageCacheMetadata metadata,
            ProductImageReference reference)
        {
            var identity = reference.Identity;
            return string.Equals(
                       metadata.AccountScope,
                       identity.AccountScope,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       metadata.ShopId,
                       Canonical(identity.ShopId),
                       StringComparison.Ordinal) &&
                   string.Equals(
                        metadata.ProductId,
                        Canonical(identity.ProductId),
                        StringComparison.Ordinal) &&
                   string.Equals(
                        metadata.Variant,
                        ProductImageContractV1.VariantName(reference.Variant),
                        StringComparison.Ordinal);
        }

        private static bool MetadataMatchesIdentity(
            ProductImageCacheMetadata metadata,
            ProductImageIdentity identity,
            ProductImageVariant variant)
        {
            return metadata != null && identity != null &&
                   string.Equals(metadata.AccountScope, identity.AccountScope, StringComparison.Ordinal) &&
                   string.Equals(metadata.ShopId, Canonical(identity.ShopId), StringComparison.Ordinal) &&
                   string.Equals(metadata.ProductId, Canonical(identity.ProductId), StringComparison.Ordinal) &&
                   string.Equals(metadata.VersionId, Canonical(identity.VersionId), StringComparison.Ordinal) &&
                   string.Equals(
                       metadata.Variant,
                       ProductImageContractV1.VariantName(variant),
                       StringComparison.Ordinal);
        }

        private void RemoveUncommittedBlobs()
        {
            var committed = new HashSet<string>(
                _entries.Keys,
                StringComparer.Ordinal);
            foreach (var dataPath in EnumerateFilesBounded(
                         "*.img",
                         MaximumScanEntries()))
            {
                var fileStem = Path.GetFileNameWithoutExtension(dataPath);
                if (!IsSafeFileStem(fileStem))
                {
                    DeleteContainedFileIfPresent(dataPath);
                    continue;
                }

                if (!committed.Contains(fileStem) ||
                    !File.Exists(MetadataPath(fileStem)))
                {
                    DeleteContainedFileIfPresent(dataPath);
                }
            }

            foreach (var metadataPath in EnumerateFilesBounded(
                         "*.meta",
                         MaximumScanEntries()))
            {
                var fileStem = Path.GetFileNameWithoutExtension(metadataPath);
                if (!IsSafeFileStem(fileStem))
                {
                    DeleteContainedFileIfPresent(metadataPath);
                    continue;
                }

                if (!committed.Contains(fileStem) ||
                    !File.Exists(DataPath(fileStem)))
                {
                    DeleteContainedFileIfPresent(metadataPath);
                }
            }
        }

        private void CleanupTemporaryFiles(bool removeAll = false)
        {
            var threshold = DateTime.UtcNow - _options.StaleTemporaryFileAge;
            foreach (var path in EnumerateFilesBounded(
                         "*.tmp",
                         MaximumDirectoryFiles()))
            {
                try
                {
                    if (removeAll ||
                        _options.StaleTemporaryFileAge == TimeSpan.Zero ||
                        File.GetLastWriteTimeUtc(path) <= threshold)
                    {
                        DeleteContainedFileIfPresent(path);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private void WriteMetadataAtomically(ProductImageCacheMetadata metadata)
        {
            WriteAtomically(
                MetadataPath(metadata.FileStem),
                Serialize(metadata));
        }

        private void WriteIndexAtomically()
        {
            var index = new ProductImageCacheIndex
            {
                SchemaVersion = IndexSchemaVersion,
                Entries = _entries.Values
                    .OrderBy(entry => entry.FileStem, StringComparer.Ordinal)
                    .ToList()
            };
            WriteAtomically(CachePath(IndexFileName), Serialize(index));
        }

        private void WriteAtomically(string destination, byte[] bytes)
        {
            var temporary = CachePath(
                Path.GetFileName(destination) +
                "." +
                Guid.NewGuid().ToString("N") +
                ".tmp");
            try
            {
                WriteFileDurably(temporary, bytes);
                if (File.Exists(destination))
                {
                    AssertRegularFile(destination);
                    var backup = CachePath(
                        Path.GetFileName(destination) +
                        "." +
                        Guid.NewGuid().ToString("N") +
                        ".bak.tmp");
                    try
                    {
                        File.Replace(temporary, destination, backup, ignoreMetadataErrors: true);
                    }
                    finally
                    {
                        DeleteContainedFileIfPresent(backup);
                    }
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                DeleteContainedFileIfPresent(temporary);
            }
        }

        private static async Task<byte[]> ReadBoundedAsync(
            Stream stream,
            int expectedBytes,
            CancellationToken cancellationToken)
        {
            using (var output = new MemoryStream(expectedBytes))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await stream
                        .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (output.Length + read > expectedBytes)
                    {
                        throw new InvalidDataException("image_input_size_invalid");
                    }

                    await output
                        .WriteAsync(buffer, 0, read, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (output.Length != expectedBytes)
                {
                    throw new InvalidDataException("image_input_size_invalid");
                }

                return output.ToArray();
            }
        }

        private void WriteFileDurably(string path, byte[] bytes)
        {
            AssertContained(path);
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        private byte[] ReadFileBounded(string path, int maximumBytes)
        {
            AssertContained(path);
            AssertRegularFile(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1 || info.Length > maximumBytes)
            {
                throw new InvalidDataException("image_cache_file_size_invalid");
            }

            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       81920,
                       FileOptions.SequentialScan))
            {
                var length = checked((int)stream.Length);
                var bytes = new byte[length];
                var offset = 0;
                while (offset < length)
                {
                    var read = stream.Read(bytes, offset, length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    offset += read;
                }

                return bytes;
            }
        }

        private static byte[] Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        private static T Deserialize<T>(byte[] bytes) where T : class
        {
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                return new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
            }
        }

        private ProductImageCacheMetadata CreateMetadata(
            ProductImageReference reference,
            ProductImageCacheKey key,
            DateTimeOffset lastAccessUtc,
            long stageSequence,
            bool isPromoted)
        {
            return new ProductImageCacheMetadata
            {
                SchemaVersion = IndexSchemaVersion,
                FileStem = key.FileStem,
                CanonicalKey = key.CanonicalValue,
                AccountScope = reference.Identity.AccountScope,
                ShopId = Canonical(reference.Identity.ShopId),
                ProductId = Canonical(reference.Identity.ProductId),
                VersionId = Canonical(reference.Identity.VersionId),
                Variant = ProductImageContractV1.VariantName(reference.Variant),
                MimeType = reference.Metadata.MimeType,
                ByteSize = reference.Metadata.ByteSize,
                Width = reference.Metadata.Width,
                Height = reference.Metadata.Height,
                Sha256 = reference.Metadata.Sha256,
                ImageUpdatedAtUtcTicks = reference.ImageUpdatedAt?.UtcDateTime.Ticks,
                LastAccessUtcTicks = lastAccessUtc.UtcDateTime.Ticks,
                StageSequence = stageSequence,
                IsPromoted = isPromoted
            };
        }

        private static bool MetadataMatchesReference(
            ProductImageCacheMetadata metadata,
            ProductImageReference reference)
        {
            return metadata != null &&
                   string.Equals(
                       metadata.AccountScope,
                       reference.Identity.AccountScope,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       metadata.ShopId,
                       Canonical(reference.Identity.ShopId),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       metadata.ProductId,
                       Canonical(reference.Identity.ProductId),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       metadata.VersionId,
                       Canonical(reference.Identity.VersionId),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       metadata.Variant,
                       ProductImageContractV1.VariantName(reference.Variant),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       metadata.MimeType,
                       reference.Metadata.MimeType,
                       StringComparison.Ordinal) &&
                   metadata.ByteSize == reference.Metadata.ByteSize &&
                   metadata.Width == reference.Metadata.Width &&
                   metadata.Height == reference.Metadata.Height &&
                   string.Equals(
                       metadata.Sha256,
                       reference.Metadata.Sha256,
                       StringComparison.Ordinal);
        }

        private static bool MetadataEquivalent(
            ProductImageCacheMetadata left,
            ProductImageCacheMetadata right)
        {
            return left != null &&
                   right != null &&
                   left.SchemaVersion == right.SchemaVersion &&
                   left.ByteSize == right.ByteSize &&
                   left.Width == right.Width &&
                   left.Height == right.Height &&
                   left.ImageUpdatedAtUtcTicks == right.ImageUpdatedAtUtcTicks &&
                   left.StageSequence == right.StageSequence &&
                   left.IsPromoted == right.IsPromoted &&
                   string.Equals(left.FileStem, right.FileStem, StringComparison.Ordinal) &&
                   string.Equals(left.CanonicalKey, right.CanonicalKey, StringComparison.Ordinal) &&
                   string.Equals(left.AccountScope, right.AccountScope, StringComparison.Ordinal) &&
                   string.Equals(left.ShopId, right.ShopId, StringComparison.Ordinal) &&
                   string.Equals(left.ProductId, right.ProductId, StringComparison.Ordinal) &&
                   string.Equals(left.VersionId, right.VersionId, StringComparison.Ordinal) &&
                   string.Equals(left.Variant, right.Variant, StringComparison.Ordinal) &&
                   string.Equals(left.MimeType, right.MimeType, StringComparison.Ordinal) &&
                   string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
        }

        private static bool MetadataShapeIsValid(ProductImageCacheMetadata metadata)
        {
            if (metadata == null ||
                metadata.SchemaVersion != IndexSchemaVersion ||
                metadata.ByteSize < 1 ||
                metadata.ByteSize > ProductImageContractV1.MainMaximumBytes ||
                metadata.Width < 1 ||
                metadata.Height < 1 ||
                metadata.StageSequence < 1 ||
                metadata.LastAccessUtcTicks < DateTimeOffset.MinValue.UtcDateTime.Ticks ||
                metadata.LastAccessUtcTicks > DateTimeOffset.MaxValue.UtcDateTime.Ticks ||
                (metadata.ImageUpdatedAtUtcTicks.HasValue &&
                 (metadata.ImageUpdatedAtUtcTicks.Value <
                  DateTimeOffset.MinValue.UtcDateTime.Ticks ||
                  metadata.ImageUpdatedAtUtcTicks.Value >
                  DateTimeOffset.MaxValue.UtcDateTime.Ticks)) ||
                string.IsNullOrWhiteSpace(metadata.CanonicalKey) ||
                string.IsNullOrWhiteSpace(metadata.AccountScope) ||
                string.IsNullOrWhiteSpace(metadata.ShopId) ||
                string.IsNullOrWhiteSpace(metadata.ProductId) ||
                string.IsNullOrWhiteSpace(metadata.VersionId) ||
                (metadata.Variant != "main" && metadata.Variant != "thumb") ||
                metadata.MimeType != ProductImageContractV1.WireMimeType ||
                string.IsNullOrWhiteSpace(metadata.Sha256))
            {
                return false;
            }

            if (!ProductImageIdentity.TryCreate(
                    metadata.AccountScope,
                    metadata.ShopId,
                    metadata.ProductId,
                    metadata.VersionId,
                    out var identity,
                    out _))
            {
                return false;
            }

            var variant = metadata.Variant == "main"
                ? ProductImageVariant.Main
                : ProductImageVariant.Thumb;
            if (!ProductImageMetadata.TryCreate(
                    variant,
                    metadata.MimeType,
                    metadata.ByteSize,
                    metadata.Width,
                    metadata.Height,
                    metadata.Sha256,
                    out var imageMetadata,
                    out _))
            {
                return false;
            }

            var reference = new ProductImageReference(identity, variant, imageMetadata);
            var key = ProductImageCacheKey.FromReference(reference);
            return string.Equals(metadata.FileStem, key.FileStem, StringComparison.Ordinal) &&
                   string.Equals(metadata.CanonicalKey, key.CanonicalValue, StringComparison.Ordinal);
        }

        private static bool TryCreateReference(
            ProductImageCacheMetadata metadata,
            out ProductImageReference reference)
        {
            reference = null;
            if (!MetadataShapeIsValid(metadata) ||
                !ProductImageIdentity.TryCreate(
                    metadata.AccountScope,
                    metadata.ShopId,
                    metadata.ProductId,
                    metadata.VersionId,
                    out var identity,
                    out _))
            {
                return false;
            }

            var variant = metadata.Variant == "main"
                ? ProductImageVariant.Main
                : ProductImageVariant.Thumb;
            if (!ProductImageMetadata.TryCreate(
                    variant,
                    metadata.MimeType,
                    metadata.ByteSize,
                    metadata.Width,
                    metadata.Height,
                    metadata.Sha256,
                    out var imageMetadata,
                    out _))
            {
                return false;
            }

            DateTimeOffset? updatedAt = null;
            if (metadata.ImageUpdatedAtUtcTicks.HasValue)
            {
                updatedAt = new DateTimeOffset(
                    metadata.ImageUpdatedAtUtcTicks.Value,
                    TimeSpan.Zero);
            }

            reference = new ProductImageReference(
                identity,
                variant,
                imageMetadata,
                updatedAt);
            return true;
        }

        private void RemoveEntryFiles(string fileStem)
        {
            if (!IsSafeFileStem(fileStem))
            {
                throw new InvalidDataException("image_cache_key_invalid");
            }

            DeleteContainedFileIfPresent(DataPath(fileStem));
            DeleteContainedFileIfPresent(MetadataPath(fileStem));
        }

        private void DeleteContainedFileIfPresent(string path)
        {
            AssertContained(path);
            if (File.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // File.Delete removes the link itself and does not traverse
                    // to its target. Reparse entries are never read/replaced.
                    File.Delete(path);
                    return;
                }

                File.Delete(path);
            }
        }

        private string CachePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                Path.GetFileName(fileName) != fileName ||
                fileName.IndexOfAny(new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                }) >= 0)
            {
                throw new InvalidDataException("image_cache_path_invalid");
            }

            var path = Path.GetFullPath(Path.Combine(_options.RootPath, fileName));
            AssertContained(path);
            return path;
        }

        private string DataPath(string fileStem)
        {
            if (!IsSafeFileStem(fileStem))
            {
                throw new InvalidDataException("image_cache_key_invalid");
            }

            return CachePath(fileStem + ".img");
        }

        private string MetadataPath(string fileStem)
        {
            if (!IsSafeFileStem(fileStem))
            {
                throw new InvalidDataException("image_cache_key_invalid");
            }

            return CachePath(fileStem + ".meta");
        }

        private void AssertContained(string path)
        {
            if (!ProductImageCacheOptions.IsWithin(path, _options.RootPath))
            {
                throw new InvalidDataException("image_cache_path_invalid");
            }
        }

        private void AssertRootChainHasNoReparsePoints()
        {
            var fileSystemRoot = Path.GetPathRoot(_options.RootPath);
            var current = new DirectoryInfo(_options.RootPath);
            while (current != null &&
                   !string.Equals(
                       current.FullName.TrimEnd(
                           Path.DirectorySeparatorChar,
                           Path.AltDirectorySeparatorChar),
                       fileSystemRoot?.TrimEnd(
                           Path.DirectorySeparatorChar,
                           Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase))
            {
                if (current.Exists &&
                    (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "image_cache_reparse_root_forbidden");
                }

                current = current.Parent;
            }
        }

        private void AcquireRootLock()
        {
            var path = CachePath(RootLockFileName);
            SafeFileHandle handle = null;
            try
            {
                handle = NativeMethods.CreateFile(
                    path,
                    NativeMethods.GenericRead | NativeMethods.GenericWrite,
                    0,
                    IntPtr.Zero,
                    NativeMethods.OpenAlways,
                    NativeMethods.FileAttributeNormal |
                    NativeMethods.FileFlagOpenReparsePoint |
                    NativeMethods.FileFlagWriteThrough,
                    IntPtr.Zero);
                if (handle == null || handle.IsInvalid)
                {
                    var errorCode = Marshal.GetLastWin32Error();
                    handle?.Dispose();
                    if (errorCode == NativeMethods.ErrorSharingViolation)
                    {
                        throw new InvalidOperationException(
                            "image_cache_root_already_in_use",
                            new Win32Exception(errorCode));
                    }

                    throw new IOException(
                        "image_cache_root_lock_open_failed",
                        new Win32Exception(errorCode));
                }

                if (!NativeMethods.GetFileInformationByHandle(
                        handle,
                        out var information))
                {
                    throw new IOException(
                        "image_cache_root_lock_inspection_failed",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if ((information.FileAttributes &
                     NativeMethods.FileAttributeReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "image_cache_reparse_entry_forbidden");
                }

                _rootLock = new FileStream(
                    handle,
                    FileAccess.ReadWrite,
                    1,
                    isAsync: false);
                handle = null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        private void AssertRegularFile(string path)
        {
            AssertContained(path);
            if (!File.Exists(path))
            {
                return;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "image_cache_reparse_entry_forbidden");
            }
        }

        private IReadOnlyList<string> EnumerateFilesBounded(
            string pattern,
            int maximumFiles)
        {
            var files = new List<string>(Math.Min(maximumFiles, 1024));
            foreach (var path in Directory.EnumerateFiles(
                         _options.RootPath,
                         pattern,
                         SearchOption.TopDirectoryOnly))
            {
                if (files.Count >= maximumFiles)
                {
                    throw new InvalidDataException(
                        "image_cache_directory_overflow");
                }

                files.Add(path);
            }

            return files;
        }

        private long CalculateCommittedCacheBytes()
        {
            var index = new ProductImageCacheIndex
            {
                SchemaVersion = IndexSchemaVersion,
                Entries = _entries.Values
                    .OrderBy(entry => entry.FileStem, StringComparer.Ordinal)
                    .ToList()
            };
            long total = Serialize(index).Length;
            foreach (var entry in _entries.Values)
            {
                var dataPath = DataPath(entry.FileStem);
                var metadataPath = MetadataPath(entry.FileStem);
                total += File.Exists(dataPath)
                    ? new FileInfo(dataPath).Length
                    : entry.ByteSize;
                total += File.Exists(metadataPath)
                    ? new FileInfo(metadataPath).Length
                    : Serialize(entry).Length;
            }

            return total;
        }

        private long GetAccountedDirectoryBytes()
        {
            long total = 0;
            foreach (var path in EnumerateFilesBounded(
                         "*",
                         MaximumDirectoryFiles()))
            {
                var name = Path.GetFileName(path);
                if (!string.Equals(name, IndexFileName, StringComparison.Ordinal) &&
                    !string.Equals(name, RootLockFileName, StringComparison.Ordinal) &&
                    !name.EndsWith(".img", StringComparison.Ordinal) &&
                    !name.EndsWith(".meta", StringComparison.Ordinal) &&
                    !name.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    continue;
                }

                AssertRegularFile(path);
                total += new FileInfo(path).Length;
            }

            return total;
        }

        private int MaximumScanEntries()
        {
            return checked((_options.MaximumEntries * 4) + 512);
        }

        private int MaximumDirectoryFiles()
        {
            return checked((MaximumScanEntries() * 3) + 32);
        }

        private int MaximumIndexBytes()
        {
            return checked(MaximumScanEntries() * MetadataMaximumBytes);
        }

        private static bool IsSafeFileStem(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
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

        private static string Canonical(Guid value)
        {
            return value.ToString("D").ToLowerInvariant();
        }

        private IDisposable EnterOperation()
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(ProductImageDiskCache));
                }

                if (_activeOperations++ == 0)
                {
                    _operationsDrained.Reset();
                }

                return new OperationLease(this);
            }
        }

        private void ExitOperation()
        {
            lock (_lifecycleGate)
            {
                _activeOperations--;
                if (_activeOperations == 0)
                {
                    _operationsDrained.Set();
                }
            }
        }

        public void Dispose()
        {
            CacheFlight[] flights;
            Task initializeTask;
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            lock (_flightGate)
            {
                flights = _flights.Values.ToArray();
                foreach (var flight in flights)
                {
                    flight.Cancellation.Cancel();
                }
            }

            lock (_initializeGate)
            {
                initializeTask = _initializeTask;
            }

            WaitWithoutSurfacing(flights
                .Select(flight => (Task)flight.Task)
                .Concat(initializeTask == null
                    ? Enumerable.Empty<Task>()
                    : new[] { initializeTask })
                .ToArray());
            _operationsDrained.Wait();

            _rootLock?.Dispose();
            _rootLock = null;
            _producerGate.Dispose();
            _stateGate.Dispose();
            _operationsDrained.Dispose();
        }

        private static void WaitWithoutSurfacing(IReadOnlyCollection<Task> tasks)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            try
            {
                Task.WhenAll(tasks).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Disposal drains work and preserves the original request result.
            }
        }

        private sealed class CacheFlight
        {
            internal readonly CancellationTokenSource Cancellation =
                new CancellationTokenSource();
            internal Task<ProductImageCacheEntry> Task;
            internal int ConsumerCount;
        }

        private sealed class OperationLease : IDisposable
        {
            private ProductImageDiskCache _owner;

            internal OperationLease(ProductImageDiskCache owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.ExitOperation();
            }
        }

        private static class NativeMethods
        {
            internal const uint GenericRead = 0x80000000;
            internal const uint GenericWrite = 0x40000000;
            internal const uint OpenAlways = 4;
            internal const uint FileAttributeNormal = 0x00000080;
            internal const uint FileAttributeReparsePoint = 0x00000400;
            internal const uint FileFlagOpenReparsePoint = 0x00200000;
            internal const uint FileFlagWriteThrough = 0x80000000;
            internal const int ErrorSharingViolation = 32;

            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetFileInformationByHandle(
                SafeFileHandle file,
                out ByHandleFileInformation fileInformation);

            [StructLayout(LayoutKind.Sequential)]
            internal struct ByHandleFileInformation
            {
                internal uint FileAttributes;
                internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
                internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
                internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
                internal uint VolumeSerialNumber;
                internal uint FileSizeHigh;
                internal uint FileSizeLow;
                internal uint NumberOfLinks;
                internal uint FileIndexHigh;
                internal uint FileIndexLow;
            }
        }

        [DataContract]
        private sealed class ProductImageCacheIndex
        {
            [DataMember(Name = "schemaVersion", Order = 1)]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "entries", Order = 2)]
            public List<ProductImageCacheMetadata> Entries { get; set; }
        }

        [DataContract]
        private sealed class ProductImageCacheMetadata
        {
            [DataMember(Name = "schemaVersion", Order = 1)]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "fileStem", Order = 2)]
            public string FileStem { get; set; }

            [DataMember(Name = "canonicalKey", Order = 3)]
            public string CanonicalKey { get; set; }

            [DataMember(Name = "accountScope", Order = 4)]
            public string AccountScope { get; set; }

            [DataMember(Name = "shopId", Order = 5)]
            public string ShopId { get; set; }

            [DataMember(Name = "productId", Order = 6)]
            public string ProductId { get; set; }

            [DataMember(Name = "versionId", Order = 7)]
            public string VersionId { get; set; }

            [DataMember(Name = "variant", Order = 8)]
            public string Variant { get; set; }

            [DataMember(Name = "mimeType", Order = 9)]
            public string MimeType { get; set; }

            [DataMember(Name = "byteSize", Order = 10)]
            public int ByteSize { get; set; }

            [DataMember(Name = "width", Order = 11)]
            public int Width { get; set; }

            [DataMember(Name = "height", Order = 12)]
            public int Height { get; set; }

            [DataMember(Name = "sha256", Order = 13)]
            public string Sha256 { get; set; }

            [DataMember(Name = "imageUpdatedAtUtcTicks", Order = 14, EmitDefaultValue = false)]
            public long? ImageUpdatedAtUtcTicks { get; set; }

            [DataMember(Name = "lastAccessUtcTicks", Order = 15)]
            public long LastAccessUtcTicks { get; set; }

            [DataMember(Name = "stageSequence", Order = 16)]
            public long StageSequence { get; set; }

            [DataMember(Name = "isPromoted", Order = 17)]
            public bool IsPromoted { get; set; }
        }
    }

    public sealed class ProductImageDiskCacheStreamProvider :
        IProductImageStreamProvider
    {
        private readonly ProductImageDiskCache _cache;

        public ProductImageDiskCacheStreamProvider(ProductImageDiskCache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public async Task<Stream> OpenReadAsync(
            ProductImageReference reference,
            CancellationToken cancellationToken)
        {
            var entry = await _cache
                .GetAsync(reference, cancellationToken)
                .ConfigureAwait(false);
            if (entry == null)
            {
                throw new FileNotFoundException("image_offline_not_cached");
            }

            return entry.OpenRead();
        }
    }
}
