using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Images;
using Win7POS.Core.Online;
using ImageContract = Win7POS.Core.Images.ProductImageContractV1;

namespace Win7POS.Data.Online
{
    public interface IPosProductImageTransport : IDisposable
    {
        Task<PosProductImageClientResult<PosProductImageIntentResponse>> IntentAsync(
            PosProductImageIntentRequest request,
            CancellationToken cancellationToken);
        Task<PosProductImageClientResult<PosProductImageFinalizeResponse>> FinalizeAsync(
            PosProductImageFinalizeRequest request,
            CancellationToken cancellationToken);
        Task<PosProductImageClientResult<PosProductImageRemoveResponse>> RemoveAsync(
            PosProductImageRemoveRequest request,
            CancellationToken cancellationToken);
        Task<PosProductImageClientResult<PosProductImageReadUrlsResponse>> ReadUrlsAsync(
            PosProductImageReadUrlsRequest request,
            CancellationToken cancellationToken);
        Task<PosProductImageUploadResult> UploadJpegAsync(
            string signedUrl,
            string shopId,
            string productId,
            string versionId,
            string variant,
            Stream jpeg,
            int exactLength,
            CancellationToken cancellationToken);
        Task<PosProductImageDownloadResult> DownloadJpegAsync(
            string signedUrl,
            string shopId,
            string productId,
            string versionId,
            string variant,
            PosProductImageUploadMetadata expected,
            CancellationToken cancellationToken);
    }

    public sealed class PosProductImageClientResult<T> where T : class
    {
        private PosProductImageClientResult() { }

        public string Code { get; private set; }
        public PosProductImageFailureKind FailureKind { get; private set; }
        public int? HttpStatus { get; private set; }
        public bool IsSuccess { get; private set; }
        public bool Retryable { get; private set; }
        public T Value { get; private set; }

        internal static PosProductImageClientResult<T> Success(T value, int status) =>
            new PosProductImageClientResult<T>
            {
                IsSuccess = true,
                Code = "success",
                FailureKind = PosProductImageFailureKind.None,
                HttpStatus = status,
                Value = value
            };

        internal static PosProductImageClientResult<T> Failure(
            string code,
            PosProductImageFailureKind kind,
            int? status,
            bool retryable) =>
            new PosProductImageClientResult<T>
            {
                IsSuccess = false,
                Code = SafeCode(code),
                FailureKind = kind,
                HttpStatus = status,
                Retryable = retryable
            };

        private static string SafeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0 || normalized.Length > 80 ||
                normalized.Any(character =>
                    !((character >= 'a' && character <= 'z') ||
                      (character >= '0' && character <= '9') ||
                      character == '_')))
            {
                return "corrupt_response";
            }
            return normalized;
        }
    }

    public sealed class PosProductImageUploadResult
    {
        private PosProductImageUploadResult() { }
        public string Code { get; private set; }
        public int? HttpStatus { get; private set; }
        public bool IsSuccess { get; private set; }
        public bool Retryable { get; private set; }

        internal static PosProductImageUploadResult Success(int status) =>
            new PosProductImageUploadResult { IsSuccess = true, Code = "success", HttpStatus = status };

        internal static PosProductImageUploadResult Failure(string code, int? status, bool retryable) =>
            new PosProductImageUploadResult
            {
                Code = code,
                HttpStatus = status,
                Retryable = retryable
            };
    }

    public sealed class PosProductImageDownloadResult
    {
        private PosProductImageDownloadResult() { }

        private byte[] _bytes;
        public string Code { get; private set; }
        public int? HttpStatus { get; private set; }
        public bool IsSuccess { get; private set; }
        public bool Retryable { get; private set; }
        public byte[] CopyBytes() => _bytes == null ? null : (byte[])_bytes.Clone();

        internal static PosProductImageDownloadResult Success(byte[] bytes, int status) =>
            new PosProductImageDownloadResult
            {
                IsSuccess = true,
                Code = "success",
                HttpStatus = status,
                _bytes = (byte[])bytes.Clone()
            };

        internal static PosProductImageDownloadResult Failure(
            string code,
            int? status,
            bool retryable) =>
            new PosProductImageDownloadResult
            {
                Code = code,
                HttpStatus = status,
                Retryable = retryable
            };
    }

    public sealed class PosProductImageUrlPolicy
    {
        private readonly Uri _storageOrigin;

        public PosProductImageUrlPolicy(Uri storageOrigin)
        {
            if (storageOrigin == null || !storageOrigin.IsAbsoluteUri ||
                (!string.Equals(storageOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                 !(storageOrigin.IsLoopback && string.Equals(storageOrigin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))) ||
                !string.IsNullOrEmpty(storageOrigin.UserInfo) ||
                storageOrigin.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(storageOrigin.Query) ||
                !string.IsNullOrEmpty(storageOrigin.Fragment))
            {
                throw new ArgumentException("product_image_storage_origin_invalid", nameof(storageOrigin));
            }
            _storageOrigin = storageOrigin;
        }

        public bool IsUploadUrl(
            string value,
            string shopId,
            string productId,
            string versionId,
            string variant)
        {
            try
            {
                return IsExact(
                    value,
                    "/storage/v1/object/upload/sign/product-images/" +
                    CanonicalObjectPath(shopId, productId, versionId, variant));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public bool IsReadUrl(
            string value,
            string shopId,
            string productId,
            string versionId,
            string variant)
        {
            try
            {
                return IsExact(
                    value,
                    "/storage/v1/object/sign/product-images/" +
                    CanonicalObjectPath(shopId, productId, versionId, variant));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private bool IsExact(string value, string expectedPath)
        {
            Uri parsed;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 ||
                !Uri.TryCreate(value, UriKind.Absolute, out parsed) ||
                !string.Equals(parsed.Scheme, _storageOrigin.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parsed.Host, _storageOrigin.Host, StringComparison.OrdinalIgnoreCase) ||
                parsed.Port != _storageOrigin.Port ||
                !string.IsNullOrEmpty(parsed.UserInfo) ||
                !string.IsNullOrEmpty(parsed.Fragment) ||
                string.IsNullOrEmpty(parsed.Query))
            {
                return false;
            }
            string decoded;
            try { decoded = Uri.UnescapeDataString(parsed.AbsolutePath); }
            catch (UriFormatException) { return false; }
            return string.Equals(decoded, expectedPath, StringComparison.Ordinal) &&
                   decoded.IndexOf("..", StringComparison.Ordinal) < 0 &&
                   decoded.IndexOf('\\') < 0;
        }

        private static string CanonicalObjectPath(
            string shopId,
            string productId,
            string versionId,
            string variant)
        {
            if (!PosProductImageContractV1.IsCanonicalUuid(shopId) ||
                !PosProductImageContractV1.IsCanonicalUuid(productId) ||
                !PosProductImageContractV1.IsCanonicalUuid(versionId) ||
                (variant != "main" && variant != "thumb"))
            {
                throw new ArgumentException("product_image_object_identity_invalid");
            }
            return "shops/" + shopId + "/products/" + productId +
                   "/primary/" + versionId + "/" + variant + ".jpg";
        }
    }

    /// <summary>
    /// Dedicated no-redirect transport for the four trusted POS image routes
    /// and their short-lived Storage capabilities. Capability values never
    /// leave method scope and are never included in failures.
    /// </summary>
    public sealed class PosProductImageClient : IPosProductImageTransport
    {
        public const string IntentPath = "/api/pos/catalog/product-images/intent";
        public const string FinalizePath = "/api/pos/catalog/product-images/finalize";
        public const string ReadUrlsPath = "/api/pos/catalog/product-images/read-urls";
        public const string RemovePath = "/api/pos/catalog/product-images/remove";
        private const int UploadResponseMaximumBytes = 4096;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
        private readonly HttpClient _client;
        private readonly PosProductImageUrlPolicy _urlPolicy;
        private readonly Func<DateTimeOffset> _clock;
        private bool _disposed;

        public PosProductImageClient(PosAdminWebOptions options, Uri storageOrigin)
            : this(options, storageOrigin, CreateHandler())
        {
        }

        internal PosProductImageClient(
            PosAdminWebOptions options,
            Uri storageOrigin,
            HttpMessageHandler handler,
            Func<DateTimeOffset> clock = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!string.Equals(options.BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !(options.BaseUri.IsLoopback && string.Equals(options.BaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("product_image_https_required", nameof(options));
            }
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _urlPolicy = new PosProductImageUrlPolicy(storageOrigin);
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _client = new HttpClient(handler, true)
            {
                BaseAddress = options.BaseUri,
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public Task<PosProductImageClientResult<PosProductImageIntentResponse>> IntentAsync(
            PosProductImageIntentRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || !request.IsValid())
                throw new ArgumentException("product_image_intent_invalid", nameof(request));
            return PostAsync<PosProductImageIntentRequest, PosProductImageIntentResponse>(
                IntentPath,
                request,
                cancellationToken,
                response =>
                    MutationIdentityMatches(request.OperationId, request.IdempotencyKey, request.PayloadHash,
                        response.OperationId, response.IdempotencyKey, response.PayloadHash) &&
                    (response.Status != "upload_required" ||
                     (IsFutureLease(response.ServerTime, response.ExpiresAt, PosProductImageContractV1.UploadCapabilitySeconds, 0) &&
                      _urlPolicy.IsUploadUrl(response.MainUploadUrl, request.ShopId, request.ProductId, response.VersionId, "main") &&
                      _urlPolicy.IsUploadUrl(response.ThumbUploadUrl, request.ShopId, request.ProductId, response.VersionId, "thumb"))));
        }

        public Task<PosProductImageClientResult<PosProductImageFinalizeResponse>> FinalizeAsync(
            PosProductImageFinalizeRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || !request.IsValid())
                throw new ArgumentException("product_image_finalize_invalid", nameof(request));
            return PostAsync<PosProductImageFinalizeRequest, PosProductImageFinalizeResponse>(
                FinalizePath,
                request,
                cancellationToken,
                response => MutationIdentityMatches(
                    request.OperationId,
                    request.IdempotencyKey,
                    request.PayloadHash,
                    response.OperationId,
                    response.IdempotencyKey,
                    response.PayloadHash) && response.VersionId == request.VersionId);
        }

        public Task<PosProductImageClientResult<PosProductImageRemoveResponse>> RemoveAsync(
            PosProductImageRemoveRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || !request.IsValid())
                throw new ArgumentException("product_image_remove_invalid", nameof(request));
            return PostAsync<PosProductImageRemoveRequest, PosProductImageRemoveResponse>(
                RemovePath,
                request,
                cancellationToken,
                response => MutationIdentityMatches(
                    request.OperationId,
                    request.IdempotencyKey,
                    request.PayloadHash,
                    response.OperationId,
                    response.IdempotencyKey,
                    response.PayloadHash) &&
                    response.ProductId == request.ProductId &&
                    response.ShopId == request.ShopId &&
                    response.VersionId == request.ExpectedCurrentVersionId);
        }

        public Task<PosProductImageClientResult<PosProductImageReadUrlsResponse>> ReadUrlsAsync(
            PosProductImageReadUrlsRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null || !request.IsValid())
                throw new ArgumentException("product_image_read_request_invalid", nameof(request));
            var requested = new HashSet<string>(
                request.Refs.Select(Key),
                StringComparer.Ordinal);
            return PostAsync<PosProductImageReadUrlsRequest, PosProductImageReadUrlsResponse>(
                ReadUrlsPath,
                request,
                cancellationToken,
                response =>
                {
                    if (response.Items.Length != requested.Count ||
                        response.Items.Select(Key).Distinct(StringComparer.Ordinal).Count() != response.Items.Length ||
                        response.Items.Any(item => !requested.Contains(Key(item))))
                    {
                        return false;
                    }
                    return response.Items.All(item => item.Status != "ready" ||
                        (IsFutureLease(
                            response.ServerTime,
                            item.ExpiresAt,
                            PosProductImageContractV1.ReadUrlTimeToLiveSeconds,
                            PosProductImageContractV1.ReadUrlSafetyWindowSeconds) &&
                         _urlPolicy.IsReadUrl(
                             item.SignedUrl,
                             request.ShopId,
                             item.ProductId,
                             item.VersionId,
                             item.Variant)));
                });
        }

        public async Task<PosProductImageUploadResult> UploadJpegAsync(
            string signedUrl,
            string shopId,
            string productId,
            string versionId,
            string variant,
            Stream jpeg,
            int exactLength,
            CancellationToken cancellationToken)
        {
            EnsureNotDisposed();
            var maximumBytes = variant == "thumb"
                ? ImageContract.ThumbMaximumBytes
                : variant == "main"
                    ? ImageContract.MainMaximumBytes
                    : 0;
            if (!_urlPolicy.IsUploadUrl(signedUrl, shopId, productId, versionId, variant) ||
                jpeg == null || !jpeg.CanRead || !jpeg.CanSeek ||
                exactLength < 1 || exactLength > maximumBytes ||
                jpeg.Length - jpeg.Position != exactLength)
            {
                throw new ArgumentException("product_image_upload_invalid");
            }
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(UploadTimeout);
                try
                {
                    using (var content = new StreamContent(jpeg))
                    using (var message = new HttpRequestMessage(HttpMethod.Put, signedUrl))
                    {
                        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                        content.Headers.ContentLength = exactLength;
                        message.Content = content;
                        message.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
                        using (var response = await _client.SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token).ConfigureAwait(false))
                        {
                            await ReadBoundedAsync(response.Content, UploadResponseMaximumBytes, timeout.Token)
                                .ConfigureAwait(false);
                            var status = (int)response.StatusCode;
                            return response.IsSuccessStatusCode
                                ? PosProductImageUploadResult.Success(status)
                                : PosProductImageUploadResult.Failure(
                                    status == 401 || status == 403
                                        ? "expired_capability"
                                        : status >= 500
                                            ? "retryable_upstream"
                                            : "upload_rejected",
                                    status,
                                    status >= 500 || status == 429 ||
                                    status == 401 || status == 403);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return PosProductImageUploadResult.Failure("timeout", null, true);
                }
                catch (HttpRequestException)
                {
                    return PosProductImageUploadResult.Failure("network_error", null, true);
                }
                catch (InvalidDataException)
                {
                    return PosProductImageUploadResult.Failure(
                        "upload_response_too_large",
                        null,
                        false);
                }
                catch (IOException)
                {
                    return PosProductImageUploadResult.Failure("io_error", null, true);
                }
            }
        }

        public async Task<PosProductImageDownloadResult> DownloadJpegAsync(
            string signedUrl,
            string shopId,
            string productId,
            string versionId,
            string variant,
            PosProductImageUploadMetadata expected,
            CancellationToken cancellationToken)
        {
            EnsureNotDisposed();
            var imageVariant = variant == "main"
                ? ProductImageVariant.Main
                : variant == "thumb"
                    ? ProductImageVariant.Thumb
                    : (ProductImageVariant)(-1);
            ProductImageMetadata metadata;
            ProductImageValidationResult metadataValidation;
            if (!_urlPolicy.IsReadUrl(signedUrl, shopId, productId, versionId, variant) ||
                expected == null || !expected.IsStrictlyValid() ||
                !ImageContract.IsSupportedVariant(imageVariant) ||
                !ProductImageMetadata.TryCreate(
                    imageVariant,
                    expected.MimeType,
                    expected.Bytes,
                    expected.Width,
                    expected.Height,
                    expected.Sha256,
                    out metadata,
                    out metadataValidation))
            {
                throw new ArgumentException("product_image_download_invalid");
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(DownloadTimeout);
                try
                {
                    using (var message = new HttpRequestMessage(HttpMethod.Get, signedUrl))
                    {
                        message.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
                        message.Headers.TryAddWithoutValidation("Pragma", "no-cache");
                        using (var response = await _client.SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token).ConfigureAwait(false))
                        {
                            var status = (int)response.StatusCode;
                            if (!response.IsSuccessStatusCode)
                            {
                                await ReadBoundedAsync(
                                    response.Content,
                                    UploadResponseMaximumBytes,
                                    timeout.Token).ConfigureAwait(false);
                                return PosProductImageDownloadResult.Failure(
                                    status == 401 || status == 403
                                        ? "expired_capability"
                                        : status >= 500 || status == 429
                                            ? "retryable_upstream"
                                            : status == 404
                                                ? "image_not_found"
                                                : "download_rejected",
                                    status,
                                    status >= 500 || status == 429 ||
                                    status == 401 || status == 403);
                            }
                            if (!string.Equals(
                                    response.Content?.Headers?.ContentType?.MediaType,
                                    ImageContract.WireMimeType,
                                    StringComparison.OrdinalIgnoreCase) ||
                                (response.Content?.Headers?.ContentEncoding?.Any() ?? false))
                            {
                                return PosProductImageDownloadResult.Failure(
                                    "download_content_type_invalid",
                                    status,
                                    false);
                            }
                            byte[] bytes;
                            try
                            {
                                bytes = await ReadBoundedAsync(
                                    response.Content,
                                    ImageContract.MaximumBytes(imageVariant),
                                    timeout.Token).ConfigureAwait(false);
                            }
                            catch (InvalidDataException)
                            {
                                return PosProductImageDownloadResult.Failure(
                                    "download_too_large",
                                    status,
                                    false);
                            }
                            var validation = ProductImageBinaryPolicy.ValidateCanonicalWireJpeg(
                                bytes,
                                metadata);
                            return validation.IsValid
                                ? PosProductImageDownloadResult.Success(bytes, status)
                                : PosProductImageDownloadResult.Failure(
                                    "download_corrupt",
                                    status,
                                    false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return PosProductImageDownloadResult.Failure("timeout", null, true);
                }
                catch (HttpRequestException)
                {
                    return PosProductImageDownloadResult.Failure("network_error", null, true);
                }
                catch (InvalidDataException)
                {
                    return PosProductImageDownloadResult.Failure(
                        "download_response_too_large",
                        null,
                        false);
                }
                catch (IOException)
                {
                    return PosProductImageDownloadResult.Failure("io_error", null, true);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _client.Dispose();
        }

        private async Task<PosProductImageClientResult<TResponse>> PostAsync<TRequest, TResponse>(
            string path,
            TRequest request,
            CancellationToken cancellationToken,
            Func<TResponse, bool> responseIdentityValid)
            where TResponse : class, IPosProductImageStrictContract
        {
            EnsureNotDisposed();
            var json = PosProductImageContractV1.SerializeRequest(request);
            var utf8 = Encoding.UTF8.GetBytes(json);
            if (utf8.Length > PosProductImageContractV1.MaximumJsonBodyBytes)
                throw new ArgumentException("product_image_request_too_large", nameof(request));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    using (var content = new ByteArrayContent(utf8))
                    using (var message = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/')))
                    {
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        content.Headers.ContentLength = utf8.Length;
                        message.Content = content;
                        message.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
                        message.Headers.TryAddWithoutValidation("Pragma", "no-cache");
                        message.Headers.TryAddWithoutValidation("User-Agent", "Win7POS/product-image-v1");
                        using (var response = await _client.SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token).ConfigureAwait(false))
                        {
                            var status = (int)response.StatusCode;
                            var maximum = typeof(TResponse) == typeof(PosProductImageReadUrlsResponse)
                                ? PosProductImageContractV1.MaximumReadResponseBytes
                                : PosProductImageContractV1.MaximumJsonBodyBytes;
                            byte[] body;
                            try
                            {
                                body = await ReadBoundedAsync(response.Content, maximum, timeout.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (InvalidDataException)
                            {
                                return PosProductImageClientResult<TResponse>.Failure(
                                    "response_too_large",
                                    PosProductImageFailureKind.CorruptResponse,
                                    status,
                                    false);
                            }
                            if (!response.IsSuccessStatusCode)
                            {
                                PosProductImageError error;
                                if (!PosProductImageContractV1.TryDeserializeStrict(
                                    body,
                                    PosProductImageContractV1.MaximumReadResponseBytes,
                                    out error))
                                {
                                    return PosProductImageClientResult<TResponse>.Failure(
                                        "corrupt_response",
                                        PosProductImageFailureKind.CorruptResponse,
                                        status,
                                        false);
                                }
                                var kind = PosProductImageResultMapping.Map(status, error.Code, error.Retryable);
                                return PosProductImageClientResult<TResponse>.Failure(
                                    error.Code,
                                    kind,
                                    status,
                                    error.Retryable || kind == PosProductImageFailureKind.RateLimited);
                            }
                            TResponse parsed;
                            if (!IsJson(response.Content) ||
                                !PosProductImageContractV1.TryDeserializeStrict(body, maximum, out parsed) ||
                                !responseIdentityValid(parsed))
                            {
                                return PosProductImageClientResult<TResponse>.Failure(
                                    "corrupt_response",
                                    PosProductImageFailureKind.CorruptResponse,
                                    status,
                                    false);
                            }
                            return PosProductImageClientResult<TResponse>.Success(parsed, status);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return PosProductImageClientResult<TResponse>.Failure(
                        "timeout",
                        PosProductImageFailureKind.RetryableTransport,
                        null,
                        true);
                }
                catch (HttpRequestException)
                {
                    return PosProductImageClientResult<TResponse>.Failure(
                        "network_error",
                        PosProductImageFailureKind.RetryableTransport,
                        null,
                        true);
                }
                catch (IOException)
                {
                    return PosProductImageClientResult<TResponse>.Failure(
                        "io_error",
                        PosProductImageFailureKind.RetryableTransport,
                        null,
                        true);
                }
            }
        }

        private static HttpMessageHandler CreateHandler()
        {
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false
            };
        }

        private static async Task<byte[]> ReadBoundedAsync(
            HttpContent content,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            if (content == null) return Array.Empty<byte>();
            if (content.Headers.ContentLength.HasValue &&
                content.Headers.ContentLength.Value > maximumBytes)
            {
                throw new InvalidDataException("product_image_response_too_large");
            }
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var result = new MemoryStream())
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var read = await stream.ReadAsync(
                        buffer,
                        0,
                        Math.Min(buffer.Length, maximumBytes + 1 - (int)result.Length),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    result.Write(buffer, 0, read);
                    if (result.Length > maximumBytes)
                        throw new InvalidDataException("product_image_response_too_large");
                }
                return result.ToArray();
            }
        }

        private static bool IsJson(HttpContent content)
        {
            return string.Equals(
                content?.Headers?.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool MutationIdentityMatches(
            string operationId,
            string idempotencyKey,
            string payloadHash,
            string responseOperationId,
            string responseIdempotencyKey,
            string responsePayloadHash)
        {
            return operationId == responseOperationId &&
                   idempotencyKey == responseIdempotencyKey &&
                   payloadHash == responsePayloadHash;
        }

        private bool IsFutureLease(
            string serverTime,
            string expiresAt,
            int maximumSeconds,
            int safetySeconds)
        {
            DateTimeOffset server;
            DateTimeOffset expiry;
            return PosProductImageContractV1.IsCanonicalTimestamp(serverTime) &&
                   PosProductImageContractV1.IsCanonicalTimestamp(expiresAt) &&
                   DateTimeOffset.TryParse(serverTime, out server) &&
                   DateTimeOffset.TryParse(expiresAt, out expiry) &&
                   expiry > server.AddSeconds(safetySeconds) &&
                   expiry > _clock().AddSeconds(safetySeconds) &&
                   expiry <= server.AddSeconds(maximumSeconds);
        }

        private static string Key(PosProductImageReadRef value) =>
            value.ProductId + "\n" + value.Variant + "\n" + value.VersionId;

        private static string Key(PosProductImageReadItem value) =>
            value.ProductId + "\n" + value.Variant + "\n" + value.VersionId;

        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PosProductImageClient));
        }
    }
}
