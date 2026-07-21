using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed class PosAdminWebClient : IDisposable
    {
        private const string FirstLoginPath = "/api/pos/auth/first-login";
        private const string HeartbeatPath = "/api/pos/session/heartbeat";
        private const string CatalogPullPath = "/api/pos/catalog/pull";
        private const string CatalogImportSyncPath = "/api/pos/catalog/import-sync";
        private const string SalesSyncPath = "/api/pos/sales/sync";
        private const int MaxResponseBodyBytes = 8 * 1024 * 1024;
        private const int MaxErrorResponseBodyBytes = 64 * 1024;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly object SharedTransportSync = new object();
        private static SharedTransportEntry _currentSharedTransport;

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly SharedTransportEntry _sharedTransport;
        private bool _disposed;

        public PosAdminWebClient(PosAdminWebOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            EnsureTls12();
            _sharedTransport = AcquireSharedTransport(options);
            _httpClient = _sharedTransport.Client;
        }

        internal PosAdminWebClient(PosAdminWebOptions options, HttpMessageHandler handler)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            EnsureTls12();
            _httpClient = BuildHttpTransport(options.BaseUri, handler, true);
            _ownsHttpClient = true;
        }

        internal HttpClient TransportForTests => _httpClient;

        public async Task<PosOnlineResult<PosFirstLoginResponse>> FirstLoginAsync(
            PosFirstLoginRequest request,
            CancellationToken cancellationToken)
        {
            return await PostJsonAsync<PosFirstLoginRequest, PosFirstLoginResponse>(
                FirstLoginPath,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<PosOnlineResult<PosHeartbeatResponse>> HeartbeatAsync(
            PosHeartbeatRequest request,
            CancellationToken cancellationToken)
        {
            return await PostJsonAsync<PosHeartbeatRequest, PosHeartbeatResponse>(
                HeartbeatPath,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<PosOnlineResult<PosCatalogPullResponse>> CatalogPullAsync(
            PosCatalogPullRequest request,
            CancellationToken cancellationToken)
        {
            return await PostJsonAsync<PosCatalogPullRequest, PosCatalogPullResponse>(
                CatalogPullPath,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<PosOnlineResult<PosSalesSyncResponse>> SalesSyncAsync(
            PosSalesSyncRequest request,
            CancellationToken cancellationToken)
        {
            return await PostJsonAsync<PosSalesSyncRequest, PosSalesSyncResponse>(
                SalesSyncPath,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<PosOnlineResult<PosCatalogImportResponse>> CatalogImportAsync(
            PosCatalogImportRequest request,
            CancellationToken cancellationToken)
        {
            return await PostJsonAsync<PosCatalogImportRequest, PosCatalogImportResponse>(
                CatalogImportSyncPath,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
            else
            {
                ReleaseSharedTransport(_sharedTransport);
            }
        }

        private static void EnsureTls12()
        {
            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
        }

        private static SharedTransportEntry AcquireSharedTransport(PosAdminWebOptions options)
        {
            var key = CreateTransportKey(options);
            HttpClient retiredClient = null;
            SharedTransportEntry acquired;
            lock (SharedTransportSync)
            {
                if (_currentSharedTransport != null &&
                    string.Equals(_currentSharedTransport.Key, key, StringComparison.Ordinal))
                {
                    _currentSharedTransport.ReferenceCount += 1;
                    return _currentSharedTransport;
                }

                var replacement = new SharedTransportEntry(
                    key,
                    BuildHttpTransport(options.BaseUri, null, false));
                var previous = _currentSharedTransport;
                _currentSharedTransport = replacement;
                replacement.ReferenceCount = 1;
                acquired = replacement;

                if (previous != null)
                {
                    previous.Retired = true;
                    if (previous.ReferenceCount == 0 && !previous.Disposed)
                    {
                        previous.Disposed = true;
                        retiredClient = previous.Client;
                    }
                }
            }

            retiredClient?.Dispose();
            return acquired;
        }

        private static void ReleaseSharedTransport(SharedTransportEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            HttpClient retiredClient = null;
            lock (SharedTransportSync)
            {
                if (entry.ReferenceCount > 0)
                {
                    entry.ReferenceCount -= 1;
                }

                if (entry.Retired && entry.ReferenceCount == 0 && !entry.Disposed)
                {
                    entry.Disposed = true;
                    retiredClient = entry.Client;
                }
            }

            retiredClient?.Dispose();
        }

        private static HttpClient BuildHttpTransport(
            Uri baseUri,
            HttpMessageHandler handler,
            bool disposeHandler)
        {
            var client = handler == null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler);
            client.BaseAddress = baseUri;
            // ResponseHeadersRead completes SendAsync after the headers. The linked
            // request token below owns the timeout for both headers and streamed body.
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        private static string CreateTransportKey(PosAdminWebOptions options)
        {
            return options.BaseUri.AbsoluteUri.TrimEnd('/') + "|source=" +
                ((int)options.BaseUrlSource).ToString() + "|environment=" +
                (options.PackagedEnvironment ?? string.Empty).Trim().ToLowerInvariant();
        }

        private async Task<PosOnlineResult<TResponse>> PostJsonAsync<TRequest, TResponse>(
            string relativePath,
            TRequest request,
            CancellationToken cancellationToken)
            where TResponse : class
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PosAdminWebClient));

            var clientRequestId = CreateClientRequestId(relativePath);
            using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
            try
            {
                requestTimeout.CancelAfter(RequestTimeout);
                var requestToken = requestTimeout.Token;
                using (var content = new JsonDataContractContent<TRequest>(request))
                using (var requestMessage = new HttpRequestMessage(
                    HttpMethod.Post,
                    relativePath.TrimStart('/')))
                {
                    requestMessage.Content = content;
                    requestMessage.Headers.TryAddWithoutValidation("X-Client-Request-Id", clientRequestId);
                    requestMessage.Headers.TryAddWithoutValidation("User-Agent", "Win7POS/online-client");

                    using (var response = await _httpClient
                        .SendAsync(
                            requestMessage,
                            HttpCompletionOption.ResponseHeadersRead,
                            requestToken)
                        .ConfigureAwait(false))
                    {
                        var serverRequestId = FirstHeaderValue(response, "X-Request-Id");
                        var cfRay = FirstHeaderValue(response, "CF-Ray");
                        var authenticationDenied =
                            response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden;

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorRead = await ReadJsonAsync<PosErrorResponse>(
                                response.Content,
                                MaxErrorResponseBodyBytes,
                                requestToken).ConfigureAwait(false);
                            if (errorRead.TooLarge)
                            {
                                return PosOnlineResult<TResponse>.Failure(
                                    "response_too_large",
                                    "Risposta di errore Admin Web POS troppo grande.",
                                    authenticationDenied,
                                    clientRequestId,
                                    serverRequestId,
                                    cfRay);
                            }

                            var error = errorRead.Value;
                            return PosOnlineResult<TResponse>.Failure(
                                NormalizeErrorCode(error?.Code),
                                string.IsNullOrWhiteSpace(error?.Message)
                                    ? "Autorizzazione POS online non riuscita."
                                    : NormalizeErrorMessage(error.Message),
                                authenticationDenied,
                                clientRequestId,
                                FirstNonEmpty(serverRequestId, error?.RequestId),
                                cfRay);
                        }

                        var responseRead = await ReadJsonAsync<TResponse>(
                            response.Content,
                            MaxResponseBodyBytes,
                            requestToken).ConfigureAwait(false);
                        if (responseRead.TooLarge)
                        {
                            return PosOnlineResult<TResponse>.Failure(
                                "response_too_large",
                                "Risposta Admin Web POS troppo grande.",
                                false,
                                clientRequestId,
                                serverRequestId,
                                cfRay);
                        }

                        if (responseRead.Value == null)
                        {
                            return PosOnlineResult<TResponse>.Failure(
                                "invalid_response",
                                "Risposta Admin Web POS non valida.",
                                false,
                                clientRequestId,
                                serverRequestId,
                                cfRay);
                        }

                        return PosOnlineResult<TResponse>.Ok(
                            responseRead.Value,
                            clientRequestId,
                            serverRequestId,
                            cfRay);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "timeout",
                    "Admin Web POS non ha risposto entro il timeout.",
                    false,
                    clientRequestId);
            }
            catch (HttpRequestException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "network_error",
                    "Admin Web POS non e raggiungibile.",
                    false,
                    clientRequestId);
            }
            catch (IOException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "io_error",
                    "Errore locale durante la richiesta POS online.",
                    false,
                    clientRequestId);
            }
            catch (InvalidOperationException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "invalid_operation",
                    "Configurazione Admin Web POS non valida.",
                    false,
                    clientRequestId);
            }
            }
        }

        private static string CreateClientRequestId(string relativePath)
        {
            var route = (relativePath ?? "pos")
                .Trim('/')
                .Replace('/', '-')
                .Replace('_', '-');
            if (route.Length > 32)
            {
                route = route.Substring(route.Length - 32);
            }

            return "win7pos-" + route + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static string FirstHeaderValue(HttpResponseMessage response, string name)
        {
            if (response == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            IEnumerable<string> values;
            if (!response.Headers.TryGetValues(name, out values))
            {
                return null;
            }

            foreach (var value in values)
            {
                return TrimTechnicalId(value, 120);
            }

            return null;
        }

        private static string FirstNonEmpty(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                ? left
                : TrimTechnicalId(right, 120);
        }

        private static string TrimTechnicalId(string value, int maxLength)
        {
            var source = (value ?? string.Empty).Trim();
            var builder = new System.Text.StringBuilder(Math.Min(source.Length, maxLength));
            foreach (var character in source)
            {
                if (builder.Length >= maxLength)
                {
                    break;
                }

                if (!char.IsControl(character))
                {
                    builder.Append(character);
                }
            }

            var normalized = builder.ToString().Trim();
            if (normalized.Length == 0)
            {
                return null;
            }

            return normalized.Length > maxLength
                ? normalized.Substring(0, maxLength)
                : normalized;
        }

        private static string NormalizeErrorCode(string value)
        {
            var source = TrimTechnicalId(value, 64);
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            var builder = new System.Text.StringBuilder(source.Length);
            foreach (var character in source)
            {
                builder.Append(
                    char.IsLetterOrDigit(character) || character == '_' ||
                    character == '-' || character == '.'
                        ? character
                        : '_');
            }

            return builder.ToString();
        }

        private static string NormalizeErrorMessage(string value)
        {
            var source = (value ?? string.Empty).Trim();
            var builder = new System.Text.StringBuilder(Math.Min(source.Length, 512));
            var previousWasSpace = false;
            foreach (var character in source)
            {
                if (builder.Length >= 512)
                {
                    break;
                }

                var normalized = char.IsControl(character) ? ' ' : character;
                if (char.IsWhiteSpace(normalized))
                {
                    if (previousWasSpace)
                    {
                        continue;
                    }
                    normalized = ' ';
                    previousWasSpace = true;
                }
                else
                {
                    previousWasSpace = false;
                }
                builder.Append(normalized);
            }

            var result = builder.ToString().Trim();
            return result.Length == 0
                ? "Autorizzazione POS online non riuscita."
                : result;
        }

        private static async Task<BoundedJsonReadResult<T>> ReadJsonAsync<T>(
            HttpContent content,
            int maxBytes,
            CancellationToken cancellationToken)
            where T : class
        {
            if (content == null)
            {
                return BoundedJsonReadResult<T>.Invalid();
            }

            var contentLength = content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
            {
                return BoundedJsonReadResult<T>.Oversized();
            }

            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var bounded = new BoundedCountingReadStream(stream, maxBytes, cancellationToken))
            using (var cancellationRegistration = cancellationToken.Register(
                state => ((Stream)state).Dispose(),
                stream))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return await Task.Run(() =>
                    {
                        var serializer = new DataContractJsonSerializer(typeof(T));
                        var value = serializer.ReadObject(bounded) as T;
                        bounded.DrainToEnd();
                        return value == null
                            ? BoundedJsonReadResult<T>.Invalid()
                            : BoundedJsonReadResult<T>.Success(value);
                    }).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }
                catch (ResponseBodyTooLargeException)
                {
                    return BoundedJsonReadResult<T>.Oversized();
                }
                catch (SerializationException)
                {
                    return BoundedJsonReadResult<T>.Invalid();
                }
                catch (System.Xml.XmlException)
                {
                    return BoundedJsonReadResult<T>.Invalid();
                }
            }
        }

        private sealed class BoundedJsonReadResult<T> where T : class
        {
            private BoundedJsonReadResult(T value, bool tooLarge)
            {
                Value = value;
                TooLarge = tooLarge;
            }

            internal T Value { get; }
            internal bool TooLarge { get; }

            internal static BoundedJsonReadResult<T> Invalid() =>
                new BoundedJsonReadResult<T>(null, false);

            internal static BoundedJsonReadResult<T> Oversized() =>
                new BoundedJsonReadResult<T>(null, true);

            internal static BoundedJsonReadResult<T> Success(T value) =>
                new BoundedJsonReadResult<T>(value, false);
        }

        private sealed class ResponseBodyTooLargeException : IOException
        {
        }

        private sealed class SharedTransportEntry
        {
            internal SharedTransportEntry(string key, HttpClient client)
            {
                Key = key;
                Client = client;
            }

            internal HttpClient Client { get; }
            internal bool Disposed { get; set; }
            internal string Key { get; }
            internal int ReferenceCount { get; set; }
            internal bool Retired { get; set; }
        }

        private sealed class JsonDataContractContent<T> : HttpContent
        {
            private readonly T _value;

            internal JsonDataContractContent(T value)
            {
                _value = value;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    "application/json")
                {
                    CharSet = "utf-8"
                };
            }

            protected override Task SerializeToStreamAsync(
                Stream stream,
                TransportContext context)
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(stream, _value);
                return Task.CompletedTask;
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        private sealed class BoundedCountingReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _maxBytes;
            private readonly CancellationToken _cancellationToken;
            private long _bytesRead;

            internal BoundedCountingReadStream(
                Stream inner,
                long maxBytes,
                CancellationToken cancellationToken)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _maxBytes = maxBytes;
                _cancellationToken = cancellationToken;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => _bytesRead;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_bytesRead >= _maxBytes)
                {
                    var extra = _inner.Read(buffer, offset, Math.Min(count, 1));
                    if (extra > 0) throw new ResponseBodyTooLargeException();
                    return 0;
                }

                var allowed = (int)Math.Min(count, (_maxBytes - _bytesRead) + 1);
                var read = _inner.Read(buffer, offset, allowed);
                _bytesRead += read;
                if (_bytesRead > _maxBytes) throw new ResponseBodyTooLargeException();
                return read;
            }

            public override async Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                if (_bytesRead >= _maxBytes)
                {
                    var extra = await _inner.ReadAsync(
                        buffer,
                        offset,
                        Math.Min(count, 1),
                        cancellationToken).ConfigureAwait(false);
                    if (extra > 0) throw new ResponseBodyTooLargeException();
                    return 0;
                }

                var allowed = (int)Math.Min(count, (_maxBytes - _bytesRead) + 1);
                var read = await _inner.ReadAsync(
                    buffer,
                    offset,
                    allowed,
                    cancellationToken).ConfigureAwait(false);
                _bytesRead += read;
                if (_bytesRead > _maxBytes) throw new ResponseBodyTooLargeException();
                return read;
            }

            internal void DrainToEnd()
            {
                var buffer = new byte[4096];
                while (Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) =>
                throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }
    }
}
