using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
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
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly HttpClient _httpClient;

        public PosAdminWebClient(PosAdminWebOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            EnsureTls12();
            _httpClient = new HttpClient
            {
                BaseAddress = options.BaseUri,
                Timeout = RequestTimeout,
            };
        }

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
            _httpClient.Dispose();
        }

        private static void EnsureTls12()
        {
            ServicePointManager.SecurityProtocol =
                ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
        }

        private async Task<PosOnlineResult<TResponse>> PostJsonAsync<TRequest, TResponse>(
            string relativePath,
            TRequest request,
            CancellationToken cancellationToken)
            where TResponse : class
        {
            var clientRequestId = CreateClientRequestId(relativePath);
            try
            {
                var json = Serialize(request);
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var requestMessage = new HttpRequestMessage(
                    HttpMethod.Post,
                    relativePath.TrimStart('/')))
                {
                    requestMessage.Content = content;
                    requestMessage.Headers.TryAddWithoutValidation("X-Client-Request-Id", clientRequestId);
                    requestMessage.Headers.TryAddWithoutValidation("User-Agent", "Win7POS/online-client");

                    using (var response = await _httpClient
                        .SendAsync(requestMessage, cancellationToken)
                        .ConfigureAwait(false))
                    {
                    var serverRequestId = FirstHeaderValue(response, "X-Request-Id");
                    var cfRay = FirstHeaderValue(response, "CF-Ray");
                    var responseJson = await ReadResponseBodyAsync(
                        response.Content,
                        cancellationToken).ConfigureAwait(false);

                    if (responseJson == null)
                    {
                        return PosOnlineResult<TResponse>.Failure(
                            "response_too_large",
                            "Risposta Admin Web POS troppo grande.",
                            false,
                            clientRequestId,
                            serverRequestId,
                            cfRay);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var error = TryDeserialize<PosErrorResponse>(responseJson);
                        return PosOnlineResult<TResponse>.Failure(
                            error?.Code,
                            string.IsNullOrWhiteSpace(error?.Message)
                                ? "Autorizzazione POS online non riuscita."
                                : error.Message,
                            response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden,
                            clientRequestId,
                            FirstNonEmpty(serverRequestId, error?.RequestId),
                            cfRay);
                    }

                    var parsed = TryDeserialize<TResponse>(responseJson);
                    if (parsed == null)
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
                        parsed,
                        clientRequestId,
                        serverRequestId,
                        cfRay);
                    }
                }
            }
            catch (TaskCanceledException)
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
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return null;
            }

            return normalized.Length > maxLength
                ? normalized.Substring(0, maxLength)
                : normalized;
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static async Task<string> ReadResponseBodyAsync(
            HttpContent content,
            CancellationToken cancellationToken)
        {
            if (content == null)
            {
                return string.Empty;
            }

            var contentLength = content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > MaxResponseBodyBytes)
            {
                return null;
            }

            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                var totalBytes = 0;

                while (true)
                {
                    var read = await stream
                        .ReadAsync(chunk, 0, chunk.Length, cancellationToken)
                        .ConfigureAwait(false);

                    if (read <= 0)
                    {
                        break;
                    }

                    totalBytes += read;
                    if (totalBytes > MaxResponseBodyBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static T TryDeserialize<T>(string json) where T : class
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                {
                    return serializer.ReadObject(stream) as T;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
