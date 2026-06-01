using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosAdminWebClient : IDisposable
    {
        private const string FirstLoginPath = "/api/pos/auth/first-login";
        private const string HeartbeatPath = "/api/pos/session/heartbeat";
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
            try
            {
                var json = Serialize(request);
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await _httpClient
                    .PostAsync(relativePath.TrimStart('/'), content, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var error = TryDeserialize<PosErrorResponse>(responseJson);
                        return PosOnlineResult<TResponse>.Failure(
                            error?.Code,
                            "Autorizzazione POS online non riuscita.",
                            response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden);
                    }

                    var parsed = TryDeserialize<TResponse>(responseJson);
                    if (parsed == null)
                    {
                        return PosOnlineResult<TResponse>.Failure(
                            "invalid_response",
                            "Risposta Admin Web POS non valida.",
                            false);
                    }

                    return PosOnlineResult<TResponse>.Ok(parsed);
                }
            }
            catch (TaskCanceledException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "timeout",
                    "Admin Web POS non ha risposto entro il timeout.",
                    false);
            }
            catch (HttpRequestException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "network_error",
                    "Admin Web POS non e raggiungibile.",
                    false);
            }
            catch (IOException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "io_error",
                    "Errore locale durante la richiesta POS online.",
                    false);
            }
            catch (InvalidOperationException)
            {
                return PosOnlineResult<TResponse>.Failure(
                    "invalid_operation",
                    "Configurazione Admin Web POS non valida.",
                    false);
            }
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

    public sealed class PosOnlineResult<T> where T : class
    {
        private PosOnlineResult(bool success, T value, string code, string message, bool denied)
        {
            Success = success;
            Value = value;
            Code = code;
            Message = message;
            Denied = denied;
        }

        public bool Denied { get; }
        public string Code { get; }
        public string Message { get; }
        public bool Success { get; }
        public T Value { get; }

        public static PosOnlineResult<T> Ok(T value)
        {
            return new PosOnlineResult<T>(true, value, "success", "", false);
        }

        public static PosOnlineResult<T> Failure(string code, string message, bool denied)
        {
            return new PosOnlineResult<T>(false, null, code ?? "failure", message ?? "Errore POS online.", denied);
        }
    }

    [DataContract]
    public sealed class PosFirstLoginRequest
    {
        [DataMember(Name = "credential")]
        public string Credential { get; set; }

        [DataMember(Name = "device")]
        public PosFirstLoginDevice Device { get; set; }

        [DataMember(Name = "shopCode")]
        public string ShopCode { get; set; }

        [DataMember(Name = "staffCode")]
        public string StaffCode { get; set; }
    }

    [DataContract]
    public sealed class PosFirstLoginDevice
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "deviceIdentifier")]
        public string DeviceIdentifier { get; set; }

        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }
    }

    [DataContract]
    public sealed class PosHeartbeatRequest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "deviceToken")]
        public string DeviceToken { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "sessionToken")]
        public string SessionToken { get; set; }

        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }
    }

    [DataContract]
    public sealed class PosFirstLoginResponse
    {
        [DataMember(Name = "device")]
        public PosTrustedDeviceResponse Device { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "session")]
        public PosSessionResponse Session { get; set; }

        [DataMember(Name = "shop")]
        public PosShopResponse Shop { get; set; }

        [DataMember(Name = "staff")]
        public PosStaffResponse Staff { get; set; }

        [DataMember(Name = "trustedDeviceToken")]
        public string TrustedDeviceToken { get; set; }
    }

    [DataContract]
    public sealed class PosHeartbeatResponse
    {
        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "session")]
        public PosSessionResponse Session { get; set; }
    }

    [DataContract]
    public sealed class PosTrustedDeviceResponse
    {
        [DataMember(Name = "shopDeviceId")]
        public string ShopDeviceId { get; set; }

        [DataMember(Name = "status")]
        public string Status { get; set; }

        [DataMember(Name = "trusted")]
        public bool Trusted { get; set; }
    }

    [DataContract]
    public sealed class PosSessionResponse
    {
        [DataMember(Name = "expiresAt")]
        public string ExpiresAt { get; set; }

        [DataMember(Name = "heartbeatAfterSeconds")]
        public int HeartbeatAfterSeconds { get; set; }

        [DataMember(Name = "posSessionId")]
        public string PosSessionId { get; set; }

        [DataMember(Name = "sessionToken", EmitDefaultValue = false)]
        public string SessionToken { get; set; }
    }

    [DataContract]
    public sealed class PosShopResponse
    {
        [DataMember(Name = "shopCode")]
        public string ShopCode { get; set; }

        [DataMember(Name = "shopId")]
        public string ShopId { get; set; }

        [DataMember(Name = "shopName")]
        public string ShopName { get; set; }
    }

    [DataContract]
    public sealed class PosStaffResponse
    {
        [DataMember(Name = "displayName")]
        public string DisplayName { get; set; }

        [DataMember(Name = "roleKey")]
        public string RoleKey { get; set; }

        [DataMember(Name = "staffCode")]
        public string StaffCode { get; set; }

        [DataMember(Name = "staffId")]
        public string StaffId { get; set; }
    }

    [DataContract]
    public sealed class PosErrorResponse
    {
        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "message")]
        public string Message { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }
    }
}
