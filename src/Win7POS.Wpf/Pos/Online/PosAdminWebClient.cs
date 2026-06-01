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
        private const string CatalogPullPath = "/api/pos/catalog/pull";
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
    public sealed class PosCatalogPullRequest
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

        [DataMember(Name = "syncCursor", EmitDefaultValue = false)]
        public string SyncCursor { get; set; }

        [DataMember(Name = "updated_since", EmitDefaultValue = false)]
        public string UpdatedSince { get; set; }
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
    public sealed class PosCatalogPullResponse
    {
        [DataMember(Name = "catalog")]
        public PosCatalogPayload Catalog { get; set; }

        [DataMember(Name = "catalogVersion")]
        public string CatalogVersion { get; set; }

        [DataMember(Name = "code")]
        public string Code { get; set; }

        [DataMember(Name = "generatedAt")]
        public string GeneratedAt { get; set; }

        [DataMember(Name = "hasMore")]
        public bool HasMore { get; set; }

        [DataMember(Name = "ok")]
        public bool Ok { get; set; }

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "serverTime")]
        public string ServerTime { get; set; }

        [DataMember(Name = "shop")]
        public PosShopResponse Shop { get; set; }

        [DataMember(Name = "syncCursor")]
        public string SyncCursor { get; set; }

        [DataMember(Name = "syncMode")]
        public string SyncMode { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogPayload
    {
        [DataMember(Name = "categories")]
        public PosCatalogCategoryResponse[] Categories { get; set; }

        [DataMember(Name = "prices")]
        public PosCatalogPriceResponse[] Prices { get; set; }

        [DataMember(Name = "products")]
        public PosCatalogProductResponse[] Products { get; set; }

        [DataMember(Name = "suppliers")]
        public PosCatalogSupplierResponse[] Suppliers { get; set; }

        [DataMember(Name = "tombstones")]
        public PosCatalogTombstonesResponse Tombstones { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogTombstonesResponse
    {
        [DataMember(Name = "categories")]
        public PosCatalogCategoryTombstoneResponse[] Categories { get; set; }

        [DataMember(Name = "products")]
        public PosCatalogProductTombstoneResponse[] Products { get; set; }

        [DataMember(Name = "suppliers")]
        public PosCatalogSupplierTombstoneResponse[] Suppliers { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogProductTombstoneResponse
    {
        [DataMember(Name = "deletedAt")]
        public string DeletedAt { get; set; }

        [DataMember(Name = "productId")]
        public string ProductId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogCategoryTombstoneResponse
    {
        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "deletedAt")]
        public string DeletedAt { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogSupplierTombstoneResponse
    {
        [DataMember(Name = "deletedAt")]
        public string DeletedAt { get; set; }

        [DataMember(Name = "supplierId")]
        public string SupplierId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogProductResponse
    {
        [DataMember(Name = "barcode")]
        public string Barcode { get; set; }

        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "itemNumber")]
        public string ItemNumber { get; set; }

        [DataMember(Name = "productId")]
        public string ProductId { get; set; }

        [DataMember(Name = "productName")]
        public string ProductName { get; set; }

        [DataMember(Name = "purchasePrice")]
        public double? PurchasePrice { get; set; }

        [DataMember(Name = "retailPrice")]
        public double? RetailPrice { get; set; }

        [DataMember(Name = "secondProductName")]
        public string SecondProductName { get; set; }

        [DataMember(Name = "stockQuantity")]
        public double? StockQuantity { get; set; }

        [DataMember(Name = "supplierId")]
        public string SupplierId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogCategoryResponse
    {
        [DataMember(Name = "categoryId")]
        public string CategoryId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogSupplierResponse
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "supplierId")]
        public string SupplierId { get; set; }

        [DataMember(Name = "updatedAt")]
        public string UpdatedAt { get; set; }
    }

    [DataContract]
    public sealed class PosCatalogPriceResponse
    {
        [DataMember(Name = "effectiveAt")]
        public string EffectiveAt { get; set; }

        [DataMember(Name = "price")]
        public double Price { get; set; }

        [DataMember(Name = "priceId")]
        public string PriceId { get; set; }

        [DataMember(Name = "productId")]
        public string ProductId { get; set; }

        [DataMember(Name = "source")]
        public string Source { get; set; }

        [DataMember(Name = "type")]
        public string Type { get; set; }
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
