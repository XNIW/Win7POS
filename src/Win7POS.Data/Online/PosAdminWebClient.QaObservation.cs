using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed partial class PosAdminWebClient
    {
        private static CatalogImportQaObservationScope _catalogImportQaScope;

        // Internal, opt-in, process-local observation. No transport replacement,
        // archived response, credential override, lease or retry alteration.
        internal static CatalogImportQaObservationScope ObserveCatalogImportForQa(
            Uri baseUri, string shopDeviceId, Action<CatalogImportQaWireRecord> observe)
        {
            if (baseUri == null || baseUri.Scheme != Uri.UriSchemeHttps ||
                baseUri.Host != "merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev" ||
                string.IsNullOrWhiteSpace(shopDeviceId) || observe == null)
                throw new ArgumentException("catalog_import_qa_scope_invalid");
            var scope = new CatalogImportQaObservationScope(baseUri, shopDeviceId, observe);
            if (Interlocked.CompareExchange(ref _catalogImportQaScope, scope, null) != null)
                throw new InvalidOperationException("catalog_import_qa_scope_already_active");
            return scope;
        }

        private static CatalogImportQaObservationScope GetCatalogImportQaScope(Uri baseUri, string route, object request)
        {
            var scope = Volatile.Read(ref _catalogImportQaScope);
            return scope != null && scope.Matches(baseUri, route, request) ? scope : null;
        }

        internal sealed class CatalogImportQaObservationScope : IDisposable
        {
            private readonly Uri _baseUri;
            private readonly string _shopDeviceId;
            private readonly Action<CatalogImportQaWireRecord> _observe;
            private string _dropPlanId;
            private int _dropPartIndex;
            private int _dropArmed;
            private int _disposed;
            internal bool AcceptedResponseDropped { get; private set; }
            internal CatalogImportQaObservationScope(Uri baseUri, string device, Action<CatalogImportQaWireRecord> observe)
            { _baseUri = baseUri; _shopDeviceId = device; _observe = observe; }

            // The harness resolves this exact part from its persisted owned plan.
            internal void DropOneAcceptedPartResponse(string planId, int partIndex)
            {
                if (string.IsNullOrWhiteSpace(planId) || partIndex < 0 || Volatile.Read(ref _dropArmed) != 0 || AcceptedResponseDropped)
                    throw new ArgumentException("catalog_import_qa_fault_identity_invalid");
                _dropPlanId = planId; _dropPartIndex = partIndex;
                Volatile.Write(ref _dropArmed, 1);
            }

            internal bool Matches(Uri baseUri, string route, object request)
            {
                if (Volatile.Read(ref _disposed) != 0 || baseUri == null || !_baseUri.Equals(baseUri) || Device(request) != _shopDeviceId) return false;
                return route == CatalogPullPath || route == "/api/pos/catalog/import-sync" ||
                    route == PosCatalogImportReceiptContract.EndpointPath ||
                    route == PosCatalogImportReceiptContract.RetirementEndpointPath ||
                    route == PosCatalogImportCorrectionContract.EndpointPath ||
                    route == PosCatalogImportRecoveryMultipartContract.BasePath + "upload" ||
                    route == PosCatalogImportRecoveryMultipartContract.BasePath + "finalize" ||
                    route == PosCatalogImportRecoveryMultipartContract.BasePath + "plan" ||
                    route == PosCatalogImportRecoveryMultipartContract.BasePath + "receipt" ||
                    route == PosCatalogImportRecoveryMultipartContract.BasePath + "retire" ||
                    route == PosCatalogImportRecoveryMultipartContract.BasePath + "apply" ||
                    route == PosCatalogImportRecoveryMultipartContract.BasePath + "receipt-page";
            }

            internal CatalogImportQaWireRecord BeginRequest(string route, object request, byte[] bytes)
            {
                var ordinary = request as PosCatalogImportRequest;
                var correction = request as PosCatalogImportCorrectionRequest;
                var lookup = request as PosCatalogImportReceiptRequest;
                var correctionLookup = request as PosCatalogImportCorrectionReceiptRequest;
                var apply = request as PosCatalogImportRecoveryApplyRequest;
                var record = new CatalogImportQaWireRecord
                {
                    Route = route, StartedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), BodyUtf8Bytes = bytes.Length,
                    BodySha256 = Hash(bytes), Items = ordinary?.Items?.Length ?? correction?.Correction?.Items?.Length ??
                        lookup?.OriginalRequest?.Items?.Length ?? correctionLookup?.OriginalRequest?.Correction?.Items?.Length ?? 0,
                    ClientImportId = ordinary?.Batch?.ClientImportId ?? correction?.Correction?.ClientImportId ?? lookup?.ClientImportId ?? correctionLookup?.ClientImportId,
                    AttemptCount = ordinary?.Batch?.AttemptCount ?? 0,
                    PlanId = apply?.PlanId, PartIndex = apply?.PartIndex,
                    PersistedIntentHash = ordinary?.PayloadHash,
                    CredentialBindingSha256 = CredentialBinding(request),
                    ApplyIdentitySha256 = apply == null ? null : Hash(Encoding.UTF8.GetBytes(
                        Binding(route, apply.SchemaVersion, apply.PlanId, apply.PartIndex.ToString(CultureInfo.InvariantCulture))))
                };
                if (bytes.Length > CatalogImportPlanBuilder.MaximumTransportBytes || record.Items > CatalogImportPlanBuilder.MaximumRowsPerRequest)
                    throw new InvalidOperationException("catalog_import_qa_wire_limit_exceeded");
                _observe(record);
                return record;
            }

            internal void RecordHttpResponse(CatalogImportQaWireRecord record, HttpResponseMessage response)
            {
                record.HttpStatus = (int)response.StatusCode;
                record.ServerResponseObserved = true;
                IEnumerable<string> ids;
                if (response.Headers.TryGetValues("X-Request-Id", out ids))
                    foreach (var id in ids) { record.ServerRequestId = id; break; }
            }

            internal void RecordParsedResponse(CatalogImportQaWireRecord record, object response)
            {
                var multipart = response as PosCatalogImportRecoveryMultipartResponse;
                var ordinary = response as PosCatalogImportResponse;
                var receipt = response as PosCatalogImportReceiptResponse;
                record.ResponseStatus = multipart?.Receipt?.Status ?? ordinary?.Batch?.Status ?? receipt?.Status;
                record.CompletedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                if (Volatile.Read(ref _disposed) == 0 && record.Route == PosCatalogImportRecoveryMultipartContract.BasePath + "apply" &&
                    record.PlanId == _dropPlanId && record.PartIndex == _dropPartIndex &&
                    multipart?.Ok == true && multipart.Receipt?.Ok == true &&
                    (multipart.Receipt.Status == "accepted" || multipart.Receipt.Status == "idempotent" || multipart.Receipt.Status == "duplicate") &&
                    Interlocked.CompareExchange(ref _dropArmed, 0, 1) == 1)
                {
                    AcceptedResponseDropped = true; record.AcceptedResponseDropped = true;
                    throw new IOException("catalog_import_qa_owned_accepted_response_lost");
                }
            }

            private static string Device(object request)
            {
                if (request is PosCatalogImportRequest ordinary) return ordinary.ShopDeviceId;
                if (request is PosCatalogImportRecoveryMultipartRequest multipart) return multipart.ShopDeviceId;
                if (request is PosCatalogImportReceiptRequest receipt) return receipt.ShopDeviceId;
                if (request is PosCatalogImportCorrectionReceiptRequest correctionReceipt) return correctionReceipt.ShopDeviceId;
                if (request is PosCatalogImportCorrectionRequest correction) return correction.ShopDeviceId;
                if (request is PosCatalogPullRequest pull) return pull.ShopDeviceId;
                return null;
            }

            // Digest only: current credentials are never copied into evidence.
            // Length prefixes keep the binding unambiguous without serialization.
            private static string CredentialBinding(object request)
            {
                if (request is PosCatalogImportRequest ordinary) return Credentials(ordinary.DeviceToken, ordinary.SessionToken, ordinary.PosSessionId, ordinary.ShopDeviceId, ordinary.ShopCode);
                if (request is PosCatalogImportRecoveryMultipartRequest multipart) return Credentials(multipart.DeviceToken, multipart.SessionToken, multipart.PosSessionId, multipart.ShopDeviceId, multipart.ShopCode);
                if (request is PosCatalogImportReceiptRequest receipt) return Credentials(receipt.DeviceToken, receipt.SessionToken, receipt.PosSessionId, receipt.ShopDeviceId, receipt.ShopCode);
                if (request is PosCatalogImportCorrectionReceiptRequest correctionReceipt) return Credentials(correctionReceipt.DeviceToken, correctionReceipt.SessionToken, correctionReceipt.PosSessionId, correctionReceipt.ShopDeviceId, correctionReceipt.ShopCode);
                if (request is PosCatalogImportCorrectionRequest correction) return Credentials(correction.DeviceToken, correction.SessionToken, correction.PosSessionId, correction.ShopDeviceId, correction.ShopCode);
                if (request is PosCatalogPullRequest pull) return Credentials(pull.DeviceToken, pull.SessionToken, pull.PosSessionId, pull.ShopDeviceId, null);
                throw new InvalidOperationException("catalog_import_qa_request_shape_invalid");
            }

            private static string Credentials(string deviceToken, string sessionToken, string posSessionId, string shopDeviceId, string shopCode)
            {
                if (string.IsNullOrWhiteSpace(deviceToken) || string.IsNullOrWhiteSpace(sessionToken) ||
                    string.IsNullOrWhiteSpace(posSessionId) || string.IsNullOrWhiteSpace(shopDeviceId))
                    throw new InvalidOperationException("catalog_import_qa_request_credentials_missing");
                return Hash(Encoding.UTF8.GetBytes(Binding(deviceToken, sessionToken, posSessionId, shopDeviceId, shopCode)));
            }

            private static string Binding(params string[] values)
            {
                var binding = new StringBuilder();
                foreach (var value in values)
                    binding.Append((value ?? "").Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value ?? "");
                return binding.ToString();
            }

            public void Dispose() { Volatile.Write(ref _disposed, 1); Interlocked.CompareExchange(ref _catalogImportQaScope, null, this); }
        }

        [DataContract]
        internal sealed class CatalogImportQaWireRecord
        {
            [DataMember] public string Route { get; set; }
            [DataMember] public string StartedAtUtc { get; set; }
            [DataMember] public string CompletedAtUtc { get; set; }
            [DataMember] public int BodyUtf8Bytes { get; set; }
            [DataMember] public string BodySha256 { get; set; }
            [DataMember] public string CredentialBindingSha256 { get; set; }
            [DataMember] public string ApplyIdentitySha256 { get; set; }
            [DataMember] public int Items { get; set; }
            [DataMember] public string ClientImportId { get; set; }
            [DataMember] public int AttemptCount { get; set; }
            [DataMember] public string PersistedIntentHash { get; set; }
            [DataMember] public string PlanId { get; set; }
            [DataMember] public int? PartIndex { get; set; }
            [DataMember] public int HttpStatus { get; set; }
            [DataMember] public bool ServerResponseObserved { get; set; }
            [DataMember] public string ServerRequestId { get; set; }
            [DataMember] public string ResponseStatus { get; set; }
            [DataMember] public bool AcceptedResponseDropped { get; set; }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
