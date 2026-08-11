using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win7POS.Core.Images;

namespace Win7POS.Wpf.Products.Images
{
    public enum ProductImageDecodeProfile
    {
        ListThumbnail = 0,
        EditorPreview = 1
    }

    public sealed class ProductImageDecodeOptions
    {
        public ProductImageDecodeOptions(
            int listMaximumSide = 128,
            int editorMaximumSide = 512,
            int maximumConcurrency = 2,
            int maximumMemoryEntries = 64)
        {
            if (listMaximumSide < 32 ||
                listMaximumSide > ProductImageContractV1.ThumbMaximumSide)
            {
                throw new ArgumentOutOfRangeException(nameof(listMaximumSide));
            }

            if (editorMaximumSide < listMaximumSide ||
                editorMaximumSide > ProductImageContractV1.MainMaximumSide)
            {
                throw new ArgumentOutOfRangeException(nameof(editorMaximumSide));
            }

            if (maximumConcurrency < 1 || maximumConcurrency > 4)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
            }

            if (maximumMemoryEntries < 1 || maximumMemoryEntries > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumMemoryEntries));
            }

            ListMaximumSide = listMaximumSide;
            EditorMaximumSide = editorMaximumSide;
            MaximumConcurrency = maximumConcurrency;
            MaximumMemoryEntries = maximumMemoryEntries;
        }

        public int ListMaximumSide { get; }
        public int EditorMaximumSide { get; }
        public int MaximumConcurrency { get; }
        public int MaximumMemoryEntries { get; }

        internal int MaximumSide(ProductImageDecodeProfile profile)
        {
            switch (profile)
            {
                case ProductImageDecodeProfile.ListThumbnail:
                    return ListMaximumSide;
                case ProductImageDecodeProfile.EditorPreview:
                    return EditorMaximumSide;
                default:
                    throw new ArgumentOutOfRangeException(nameof(profile));
            }
        }
    }

    public sealed class ProductImageDecodeResult
    {
        internal ProductImageDecodeResult(
            ProductImageDisplayState state,
            BitmapSource image,
            string errorCode,
            int sourceWidth,
            int sourceHeight,
            bool fromMemoryCache)
        {
            State = state;
            Image = image;
            ErrorCode = errorCode ?? string.Empty;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            FromMemoryCache = fromMemoryCache;
        }

        public ProductImageDisplayState State { get; }
        public BitmapSource Image { get; }
        public string ErrorCode { get; }
        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int DecodedWidth => Image?.PixelWidth ?? 0;
        public int DecodedHeight => Image?.PixelHeight ?? 0;
        public bool FromMemoryCache { get; }
        public bool IsLoaded =>
            State == ProductImageDisplayState.Loaded &&
            Image != null;
    }

    public sealed class ProductImageDecodeService
    {
        private readonly IProductImageStreamProvider _streamProvider;
        private readonly ProductImageDecodeOptions _options;
        private readonly SemaphoreSlim _decodeGate;
        private readonly object _memoryGate = new object();
        private readonly object _flightGate = new object();
        private readonly Dictionary<string, MemoryEntry> _memory =
            new Dictionary<string, MemoryEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, DecodeFlight> _flights =
            new Dictionary<string, DecodeFlight>(StringComparer.Ordinal);
        private long _accessSequence;
        private int _activeDecodes;
        private int _maximumObservedConcurrentDecodes;
        private int _decodeInvocationCount;

        public ProductImageDecodeService(
            IProductImageStreamProvider streamProvider,
            ProductImageDecodeOptions options = null)
        {
            _streamProvider = streamProvider ??
                              throw new ArgumentNullException(nameof(streamProvider));
            _options = options ?? new ProductImageDecodeOptions();
            _decodeGate = new SemaphoreSlim(
                _options.MaximumConcurrency,
                _options.MaximumConcurrency);
        }

        public int MemoryCacheEntryCount
        {
            get
            {
                lock (_memoryGate)
                {
                    RemoveCollectedMemoryEntries();
                    return _memory.Count;
                }
            }
        }

        public int DecodeInvocationCount => Volatile.Read(ref _decodeInvocationCount);
        public int MaximumObservedConcurrentDecodes =>
            Volatile.Read(ref _maximumObservedConcurrentDecodes);

        public Task<ProductImageDecodeResult> DecodeAsync(
            ProductImageReference reference,
            ProductImageDecodeProfile profile,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(Failure(
                    ProductImageDisplayState.Unavailable,
                    "image_operation_cancelled"));
            }

            if (profile != ProductImageDecodeProfile.ListThumbnail &&
                profile != ProductImageDecodeProfile.EditorPreview)
            {
                return Task.FromResult(Failure(
                    ProductImageDisplayState.Error,
                    "image_decode_profile_invalid"));
            }

            var key = FlightKey(reference, profile);
            var memoryHit = TryGetMemory(key);
            if (memoryHit != null)
            {
                return Task.FromResult(memoryHit);
            }

            DecodeFlight flight;
            lock (_flightGate)
            {
                if (!_flights.TryGetValue(key, out flight))
                {
                    flight = new DecodeFlight();
                    _flights.Add(key, flight);
                    flight.Task = Task.Run(
                        () => RunDecodeFlightAsync(
                            key,
                            flight,
                            reference,
                            profile,
                            key));
                }

                flight.ConsumerCount++;
            }

            return WaitForFlightAsync(key, flight, cancellationToken);
        }

        public void TrimMemoryCache()
        {
            lock (_memoryGate)
            {
                _memory.Clear();
            }
        }

        private async Task<ProductImageDecodeResult> WaitForFlightAsync(
            string key,
            DecodeFlight flight,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!cancellationToken.CanBeCanceled)
                {
                    return await flight.Task.ConfigureAwait(false);
                }

                var cancellationSignal =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(
                           () => cancellationSignal.TrySetResult(true)))
                {
                    var completed = await Task.WhenAny(
                            flight.Task,
                            cancellationSignal.Task)
                        .ConfigureAwait(false);
                    if (completed != flight.Task)
                    {
                        return Failure(
                            ProductImageDisplayState.Unavailable,
                            "image_operation_cancelled");
                    }
                }

                return await flight.Task.ConfigureAwait(false);
            }
            finally
            {
                ReleaseFlightConsumer(flight);
            }
        }

        private void ReleaseFlightConsumer(DecodeFlight flight)
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

        private async Task<ProductImageDecodeResult> RunDecodeFlightAsync(
            string flightKey,
            DecodeFlight flight,
            ProductImageReference reference,
            ProductImageDecodeProfile profile,
            string memoryKey)
        {
            try
            {
                return await DecodeCoreAsync(
                        reference,
                        profile,
                        memoryKey,
                        flight.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_flightGate)
                {
                    if (_flights.TryGetValue(flightKey, out var current) &&
                        ReferenceEquals(current, flight))
                    {
                        _flights.Remove(flightKey);
                    }
                }

                flight.Cancellation.Dispose();
            }
        }

        private async Task<ProductImageDecodeResult> DecodeCoreAsync(
            ProductImageReference reference,
            ProductImageDecodeProfile profile,
            string memoryKey,
            CancellationToken cancellationToken)
        {
            var entered = false;
            try
            {
                await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                cancellationToken.ThrowIfCancellationRequested();

                byte[] bytes;
                using (var stream = await _streamProvider
                           .OpenReadAsync(reference, cancellationToken)
                           .ConfigureAwait(false))
                {
                    bytes = await ReadExactAsync(
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
                    return Failure(
                        ProductImageDisplayState.Corrupt,
                        validation.Messages.FirstOrDefault() ?? "image_invalid");
                }

                var headerValidation = ProductImageBinaryPolicy.Inspect(
                    bytes,
                    reference.Metadata.ByteSize,
                    ProductImageContractV1.InputMaximumPixels,
                    out var header);
                if (!headerValidation.IsValid)
                {
                    return Failure(
                        ProductImageDisplayState.Corrupt,
                        "image_decode_failed");
                }

                var active = Interlocked.Increment(ref _activeDecodes);
                UpdateMaximumObserved(active);
                Interlocked.Increment(ref _decodeInvocationCount);
                try
                {
                    var bitmap = await Task.Run(
                            () => DecodeBitmap(
                                bytes,
                                header,
                                _options.MaximumSide(profile),
                                cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                    var result = new ProductImageDecodeResult(
                        ProductImageDisplayState.Loaded,
                        bitmap,
                        string.Empty,
                        header.Width,
                        header.Height,
                        fromMemoryCache: false);
                    PutMemory(memoryKey, result);
                    return result;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeDecodes);
                }
            }
            catch (OperationCanceledException)
            {
                return Failure(
                    ProductImageDisplayState.Unavailable,
                    "image_operation_cancelled");
            }
            catch (FileNotFoundException)
            {
                return Failure(
                    ProductImageDisplayState.Unavailable,
                    "image_offline_not_cached");
            }
            catch (InvalidDataException)
            {
                return Failure(
                    ProductImageDisplayState.Corrupt,
                    "image_decode_failed");
            }
            catch (FileFormatException)
            {
                return Failure(
                    ProductImageDisplayState.Corrupt,
                    "image_decode_failed");
            }
            catch (NotSupportedException)
            {
                return Failure(
                    ProductImageDisplayState.Corrupt,
                    "image_decode_failed");
            }
            catch (IOException)
            {
                return Failure(
                    ProductImageDisplayState.Unavailable,
                    "image_stream_unavailable");
            }
            catch (Exception)
            {
                return Failure(
                    ProductImageDisplayState.Error,
                    "image_request_failed");
            }
            finally
            {
                if (entered)
                {
                    _decodeGate.Release();
                }
            }
        }

        private static BitmapSource DecodeBitmap(
            byte[] bytes,
            ProductImageHeader header,
            int maximumSide,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions =
                    BitmapCreateOptions.PreservePixelFormat |
                    BitmapCreateOptions.IgnoreColorProfile;
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
                cancellationToken.ThrowIfCancellationRequested();

                if (bitmap.PixelWidth > maximumSide ||
                    bitmap.PixelHeight > maximumSide)
                {
                    throw new InvalidDataException("image_decode_not_bounded");
                }

                return bitmap;
            }
        }

        private ProductImageDecodeResult TryGetMemory(string key)
        {
            lock (_memoryGate)
            {
                if (!_memory.TryGetValue(key, out var entry))
                {
                    return null;
                }

                var image = entry.Image.Target as BitmapSource;
                if (image == null)
                {
                    _memory.Remove(key);
                    return null;
                }

                entry.LastAccess = ++_accessSequence;
                return new ProductImageDecodeResult(
                    ProductImageDisplayState.Loaded,
                    image,
                    string.Empty,
                    entry.SourceWidth,
                    entry.SourceHeight,
                    fromMemoryCache: true);
            }
        }

        private void PutMemory(string key, ProductImageDecodeResult result)
        {
            lock (_memoryGate)
            {
                RemoveCollectedMemoryEntries();
                _memory[key] = new MemoryEntry
                {
                    Image = new WeakReference(result.Image),
                    LastAccess = ++_accessSequence,
                    SourceWidth = result.SourceWidth,
                    SourceHeight = result.SourceHeight
                };
                while (_memory.Count > _options.MaximumMemoryEntries)
                {
                    var victim = _memory
                        .OrderBy(pair => pair.Value.LastAccess)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                        .First();
                    _memory.Remove(victim.Key);
                }
            }
        }

        private void RemoveCollectedMemoryEntries()
        {
            var collected = _memory
                .Where(pair => !pair.Value.Image.IsAlive)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in collected)
            {
                _memory.Remove(key);
            }
        }

        private static async Task<byte[]> ReadExactAsync(
            Stream stream,
            int expectedBytes,
            CancellationToken cancellationToken)
        {
            if (stream == null || !stream.CanRead)
            {
                throw new IOException("image_stream_unavailable");
            }

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

        private static ProductImageDecodeResult Failure(
            ProductImageDisplayState state,
            string errorCode)
        {
            return new ProductImageDecodeResult(
                state,
                null,
                errorCode,
                0,
                0,
                fromMemoryCache: false);
        }

        private static string FlightKey(
            ProductImageReference reference,
            ProductImageDecodeProfile profile)
        {
            return ProductImageCacheKey.FromReference(reference).FileStem +
                   ":" +
                   ((int)profile).ToString();
        }

        private void UpdateMaximumObserved(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumObservedConcurrentDecodes);
                if (active <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(
                        ref _maximumObservedConcurrentDecodes,
                        active,
                        current) == current)
                {
                    return;
                }
            }
        }

        private sealed class MemoryEntry
        {
            internal WeakReference Image;
            internal long LastAccess;
            internal int SourceWidth;
            internal int SourceHeight;
        }

        private sealed class DecodeFlight
        {
            internal readonly CancellationTokenSource Cancellation =
                new CancellationTokenSource();
            internal Task<ProductImageDecodeResult> Task;
            internal int ConsumerCount;
        }
    }
}
