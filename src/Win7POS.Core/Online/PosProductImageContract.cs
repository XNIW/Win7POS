using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Images;

namespace Win7POS.Core.Online
{
    public static class PosProductImageContractV1
    {
        public const string SchemaVersion = "pos-product-image-v1";
        public const int MaximumJsonBodyBytes = 16 * 1024;
        public const int MaximumReadResponseBytes = 64 * 1024;
        public const int ReadUrlTimeToLiveSeconds = 300;
        public const int ReadUrlSafetyWindowSeconds = 30;
        public const int UploadCapabilitySeconds = 7200;
        public const int CleanupFenceSeconds = (2 * 60 * 60) + (5 * 60);

        public static bool IsPayloadHash(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value.Length != 71 ||
                !value.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 7; index < value.Length; index++)
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

        public static bool IsCanonicalUuid(string value)
        {
            Guid parsed;
            return !string.IsNullOrEmpty(value) &&
                   value == value.ToLowerInvariant() &&
                   Guid.TryParseExact(value, "D", out parsed) &&
                   parsed != Guid.Empty;
        }

        public static bool IsCanonicalTimestamp(string value)
        {
            DateTimeOffset parsed;
            return !string.IsNullOrEmpty(value) &&
                   value.Length == 27 &&
                   value[19] == '.' &&
                   value.EndsWith("Z", StringComparison.Ordinal) &&
                   DateTimeOffset.TryParseExact(
                       value,
                       "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                       out parsed);
        }

        public static bool IsCacheScope(string value)
        {
            return IsBoundedTextWithoutControls(value, 256);
        }

        public static bool IsErrorCode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 80 ||
                value[0] < 'a' || value[0] > 'z')
            {
                return false;
            }
            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= '0' && character <= '9') ||
                      character == '_'))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsBoundedTextWithoutControls(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
                return false;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] <= '\u001f' || value[index] == '\u007f')
                    return false;
            }
            return true;
        }

        public static string SerializeRequest<T>(T request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, request);
                if (stream.Length > MaximumJsonBodyBytes)
                {
                    throw new SerializationException("product_image_request_too_large");
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static bool TryDeserializeStrict<T>(
            byte[] utf8,
            int maximumBytes,
            out T value)
            where T : class, IPosProductImageStrictContract
        {
            value = null;
            if (utf8 == null || utf8.Length < 2 || utf8.Length > maximumBytes)
            {
                return false;
            }

            try
            {
                var length = utf8.Length;
                while (length > 0 && IsJsonWhitespace(utf8[length - 1]))
                {
                    length--;
                }
                if (length < 2) return false;

                using (var stream = new MemoryStream(utf8, 0, length, false, true))
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    var parsed = serializer.ReadObject(stream) as T;
                    if (parsed == null || stream.Position != stream.Length)
                    {
                        return false;
                    }
                    ClearExtensionData(parsed);
                    using (var canonical = new MemoryStream())
                    {
                        serializer.WriteObject(canonical, parsed);
                        var compactInput = CanonicalizeJsonForStrictComparison(
                            utf8,
                            length);
                        var serialized = Encoding.UTF8.GetString(canonical.ToArray());
                        if (!string.Equals(compactInput, serialized, StringComparison.Ordinal) ||
                            !parsed.IsStrictlyValid())
                        {
                            return false;
                        }
                    }
                    value = parsed;
                    return true;
                }
            }
            catch (SerializationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool IsJsonWhitespace(byte value)
        {
            return value == 0x20 || value == 0x09 || value == 0x0a || value == 0x0d;
        }

        private static string CanonicalizeJsonForStrictComparison(
            byte[] utf8,
            int length)
        {
            var source = Encoding.UTF8.GetString(utf8, 0, length);
            var result = new StringBuilder(source.Length);
            var inString = false;
            var escaped = false;
            foreach (var character in source)
            {
                if (inString)
                {
                    // DataContractJsonSerializer emits every solidus as `\/`,
                    // while JSON.stringify and other conforming writers normally
                    // emit `/`. Both spellings decode to the same string. Fold the
                    // optional escape to the serializer form before enforcing the
                    // otherwise byte-exact member order and shape comparison.
                    if (!escaped && character == '/') result.Append('\\');
                    result.Append(character);
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                }
                else if (character == '"')
                {
                    inString = true;
                    result.Append(character);
                }
                else if (character != ' ' && character != '\t' &&
                         character != '\r' && character != '\n')
                {
                    result.Append(character);
                }
            }
            return result.ToString();
        }

        private static void ClearExtensionData(IPosProductImageStrictContract contract)
        {
            contract.ExtensionData = null;
            var readResponse = contract as PosProductImageReadUrlsResponse;
            if (readResponse?.Items != null)
            {
                foreach (var item in readResponse.Items)
                {
                    if (item == null) continue;
                    item.ExtensionData = null;
                    if (item.Metadata != null) item.Metadata.ExtensionData = null;
                }
            }
        }
    }

    public interface IPosProductImageStrictContract : IExtensibleDataObject
    {
        bool IsStrictlyValid();
    }

    [DataContract]
    public sealed class PosProductImageEnvelope
    {
        public PosProductImageEnvelope(
            string appVersion,
            string shopId,
            string shopDeviceId,
            string staffId,
            int staffCredentialVersion,
            string posSessionId,
            string deviceToken,
            string sessionToken)
        {
            AppVersion = appVersion ?? string.Empty;
            ShopId = shopId ?? string.Empty;
            ShopDeviceId = shopDeviceId ?? string.Empty;
            StaffId = staffId ?? string.Empty;
            StaffCredentialVersion = staffCredentialVersion;
            PosSessionId = posSessionId ?? string.Empty;
            DeviceToken = deviceToken ?? string.Empty;
            SessionToken = sessionToken ?? string.Empty;
        }

        [DataMember(Name = "appVersion", Order = 1)]
        public string AppVersion { get; private set; }

        [DataMember(Name = "shopId", Order = 2)]
        public string ShopId { get; private set; }

        [DataMember(Name = "shopDeviceId", Order = 3)]
        public string ShopDeviceId { get; private set; }

        [DataMember(Name = "staffId", Order = 4)]
        public string StaffId { get; private set; }

        [DataMember(Name = "staffCredentialVersion", Order = 5)]
        public int StaffCredentialVersion { get; private set; }

        [DataMember(Name = "posSessionId", Order = 6)]
        public string PosSessionId { get; private set; }

        [DataMember(Name = "deviceToken", Order = 7)]
        public string DeviceToken { get; private set; }

        [DataMember(Name = "sessionToken", Order = 8)]
        public string SessionToken { get; private set; }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(AppVersion) &&
                   AppVersion.Length <= 80 &&
                   PosProductImageContractV1.IsCanonicalUuid(ShopId) &&
                   PosProductImageContractV1.IsCanonicalUuid(ShopDeviceId) &&
                   PosProductImageContractV1.IsCanonicalUuid(StaffId) &&
                   StaffCredentialVersion >= 1 &&
                   PosProductImageContractV1.IsCanonicalUuid(PosSessionId) &&
                   !string.IsNullOrWhiteSpace(DeviceToken) &&
                   DeviceToken.Length <= 512 &&
                   !string.IsNullOrWhiteSpace(SessionToken) &&
                   SessionToken.Length <= 512;
        }
    }

    [DataContract]
    public sealed class PosProductImageUploadMetadata : IPosProductImageStrictContract
    {
        public PosProductImageUploadMetadata(
            int bytes,
            int height,
            string mimeType,
            string sha256,
            int width)
        {
            Bytes = bytes;
            Height = height;
            MimeType = mimeType;
            Sha256 = sha256;
            Width = width;
        }

        private PosProductImageUploadMetadata() { }

        [DataMember(Name = "bytes", Order = 1)] public int Bytes { get; private set; }
        [DataMember(Name = "height", Order = 2)] public int Height { get; private set; }
        [DataMember(Name = "mimeType", Order = 3)] public string MimeType { get; private set; }
        [DataMember(Name = "sha256", Order = 4)] public string Sha256 { get; private set; }
        [DataMember(Name = "width", Order = 5)] public int Width { get; private set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            return string.Equals(MimeType, ProductImageContractV1.WireMimeType, StringComparison.Ordinal) &&
                   Bytes >= 1 && Bytes <= ProductImageContractV1.MainMaximumBytes &&
                   Width >= 1 && Width <= ProductImageContractV1.MainMaximumSide &&
                   Height >= 1 && Height <= ProductImageContractV1.MainMaximumSide &&
                   !string.IsNullOrEmpty(Sha256) &&
                   Sha256.Length == 64 &&
                   Sha256.All(character =>
                       (character >= '0' && character <= '9') ||
                       (character >= 'a' && character <= 'f'));
        }
    }

    [DataContract]
    public abstract class PosProductImageMutationRequestBase
    {
        protected PosProductImageMutationRequestBase(
            string operationId,
            string idempotencyKey,
            PosProductImageEnvelope envelope,
            string productId,
            string expectedCurrentVersionId)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            OperationId = operationId ?? string.Empty;
            IdempotencyKey = idempotencyKey ?? string.Empty;
            AppVersion = envelope.AppVersion;
            ShopId = envelope.ShopId;
            ShopDeviceId = envelope.ShopDeviceId;
            StaffId = envelope.StaffId;
            StaffCredentialVersion = envelope.StaffCredentialVersion;
            PosSessionId = envelope.PosSessionId;
            DeviceToken = envelope.DeviceToken;
            SessionToken = envelope.SessionToken;
            ProductId = productId ?? string.Empty;
            ExpectedCurrentVersionId = expectedCurrentVersionId;
        }

        protected PosProductImageMutationRequestBase() { }

        public string OperationId { get; protected set; }
        public string IdempotencyKey { get; protected set; }
        public string PayloadHash { get; protected set; }
        public string AppVersion { get; protected set; }
        public string ShopId { get; protected set; }
        public string ShopDeviceId { get; protected set; }
        public string StaffId { get; protected set; }
        public int StaffCredentialVersion { get; protected set; }
        public string PosSessionId { get; protected set; }
        public string DeviceToken { get; protected set; }
        public string SessionToken { get; protected set; }
        public string ProductId { get; protected set; }
        public string ExpectedCurrentVersionId { get; protected set; }

        protected bool CommonIsValid(bool requireExpectedVersion)
        {
            return PosProductImageIdentityPolicy.IsSafeId(OperationId) &&
                   PosProductImageIdentityPolicy.IsSafeId(IdempotencyKey) &&
                   PosProductImageContractV1.IsPayloadHash(PayloadHash) &&
                   !string.IsNullOrWhiteSpace(AppVersion) &&
                   PosProductImageContractV1.IsCanonicalUuid(ShopId) &&
                   PosProductImageContractV1.IsCanonicalUuid(ShopDeviceId) &&
                   PosProductImageContractV1.IsCanonicalUuid(StaffId) &&
                   StaffCredentialVersion >= 1 &&
                   PosProductImageContractV1.IsCanonicalUuid(PosSessionId) &&
                   !string.IsNullOrWhiteSpace(DeviceToken) &&
                   DeviceToken.Length <= 512 &&
                   !string.IsNullOrWhiteSpace(SessionToken) &&
                   SessionToken.Length <= 512 &&
                   PosProductImageContractV1.IsCanonicalUuid(ProductId) &&
                   (ExpectedCurrentVersionId == null
                       ? !requireExpectedVersion
                       : PosProductImageContractV1.IsCanonicalUuid(ExpectedCurrentVersionId));
        }
    }

    [DataContract]
    public sealed class PosProductImageIntentRequest : PosProductImageMutationRequestBase
    {
        public PosProductImageIntentRequest(
            string operationId,
            string idempotencyKey,
            PosProductImageEnvelope envelope,
            string productId,
            string expectedCurrentVersionId,
            PosProductImageUploadMetadata main,
            PosProductImageUploadMetadata thumb)
            : base(operationId, idempotencyKey, envelope, productId, expectedCurrentVersionId)
        {
            Main = main ?? throw new ArgumentNullException(nameof(main));
            Thumb = thumb ?? throw new ArgumentNullException(nameof(thumb));
            PayloadHash = PosProductImageCanonicalPayload.ComputeHash(this);
        }

        private PosProductImageIntentRequest() { }

        [DataMember(Name = "schemaVersion", Order = 1)]
        public string SchemaVersion
        {
            get => PosProductImageContractV1.SchemaVersion;
            private set { }
        }

        [DataMember(Name = "operation", Order = 2)]
        public string Operation
        {
            get => "intent";
            private set { }
        }
        [DataMember(Name = "operationId", Order = 3)] public new string OperationId { get => base.OperationId; private set => base.OperationId = value; }
        [DataMember(Name = "idempotencyKey", Order = 4)] public new string IdempotencyKey { get => base.IdempotencyKey; private set => base.IdempotencyKey = value; }
        [DataMember(Name = "payloadHash", Order = 5)] public new string PayloadHash { get => base.PayloadHash; private set => base.PayloadHash = value; }
        [DataMember(Name = "appVersion", Order = 6)] public new string AppVersion { get => base.AppVersion; private set => base.AppVersion = value; }
        [DataMember(Name = "shopId", Order = 7)] public new string ShopId { get => base.ShopId; private set => base.ShopId = value; }
        [DataMember(Name = "shopDeviceId", Order = 8)] public new string ShopDeviceId { get => base.ShopDeviceId; private set => base.ShopDeviceId = value; }
        [DataMember(Name = "staffId", Order = 9)] public new string StaffId { get => base.StaffId; private set => base.StaffId = value; }
        [DataMember(Name = "staffCredentialVersion", Order = 10)] public new int StaffCredentialVersion { get => base.StaffCredentialVersion; private set => base.StaffCredentialVersion = value; }
        [DataMember(Name = "posSessionId", Order = 11)] public new string PosSessionId { get => base.PosSessionId; private set => base.PosSessionId = value; }
        [DataMember(Name = "deviceToken", Order = 12)] public new string DeviceToken { get => base.DeviceToken; private set => base.DeviceToken = value; }
        [DataMember(Name = "sessionToken", Order = 13)] public new string SessionToken { get => base.SessionToken; private set => base.SessionToken = value; }
        [DataMember(Name = "productId", Order = 14)] public new string ProductId { get => base.ProductId; private set => base.ProductId = value; }
        [DataMember(Name = "expectedCurrentVersionId", Order = 15)] public new string ExpectedCurrentVersionId { get => base.ExpectedCurrentVersionId; private set => base.ExpectedCurrentVersionId = value; }
        [DataMember(Name = "main", Order = 16)] public PosProductImageUploadMetadata Main { get; private set; }
        [DataMember(Name = "thumb", Order = 17)] public PosProductImageUploadMetadata Thumb { get; private set; }

        public bool IsValid()
        {
            return CommonIsValid(false) &&
                   Main != null && Main.IsStrictlyValid() &&
                   Thumb != null && Thumb.IsStrictlyValid() &&
                   Main.Bytes <= ProductImageContractV1.MainMaximumBytes &&
                   Main.Width <= ProductImageContractV1.MainMaximumSide &&
                   Main.Height <= ProductImageContractV1.MainMaximumSide &&
                   Thumb.Bytes <= ProductImageContractV1.ThumbMaximumBytes &&
                   Thumb.Width <= ProductImageContractV1.ThumbMaximumSide &&
                   Thumb.Height <= ProductImageContractV1.ThumbMaximumSide &&
                   string.Equals(PayloadHash, PosProductImageCanonicalPayload.ComputeHash(this), StringComparison.Ordinal);
        }
    }

    [DataContract]
    public sealed class PosProductImageFinalizeRequest : PosProductImageMutationRequestBase
    {
        public PosProductImageFinalizeRequest(
            string operationId,
            string idempotencyKey,
            PosProductImageEnvelope envelope,
            string productId,
            string expectedCurrentVersionId,
            string versionId)
            : base(operationId, idempotencyKey, envelope, productId, expectedCurrentVersionId)
        {
            VersionId = versionId ?? string.Empty;
            PayloadHash = PosProductImageCanonicalPayload.ComputeHash(this);
        }

        private PosProductImageFinalizeRequest() { }

        [DataMember(Name = "schemaVersion", Order = 1)]
        public string SchemaVersion
        {
            get => PosProductImageContractV1.SchemaVersion;
            private set { }
        }

        [DataMember(Name = "operation", Order = 2)]
        public string Operation
        {
            get => "finalize";
            private set { }
        }
        [DataMember(Name = "operationId", Order = 3)] public new string OperationId { get => base.OperationId; private set => base.OperationId = value; }
        [DataMember(Name = "idempotencyKey", Order = 4)] public new string IdempotencyKey { get => base.IdempotencyKey; private set => base.IdempotencyKey = value; }
        [DataMember(Name = "payloadHash", Order = 5)] public new string PayloadHash { get => base.PayloadHash; private set => base.PayloadHash = value; }
        [DataMember(Name = "appVersion", Order = 6)] public new string AppVersion { get => base.AppVersion; private set => base.AppVersion = value; }
        [DataMember(Name = "shopId", Order = 7)] public new string ShopId { get => base.ShopId; private set => base.ShopId = value; }
        [DataMember(Name = "shopDeviceId", Order = 8)] public new string ShopDeviceId { get => base.ShopDeviceId; private set => base.ShopDeviceId = value; }
        [DataMember(Name = "staffId", Order = 9)] public new string StaffId { get => base.StaffId; private set => base.StaffId = value; }
        [DataMember(Name = "staffCredentialVersion", Order = 10)] public new int StaffCredentialVersion { get => base.StaffCredentialVersion; private set => base.StaffCredentialVersion = value; }
        [DataMember(Name = "posSessionId", Order = 11)] public new string PosSessionId { get => base.PosSessionId; private set => base.PosSessionId = value; }
        [DataMember(Name = "deviceToken", Order = 12)] public new string DeviceToken { get => base.DeviceToken; private set => base.DeviceToken = value; }
        [DataMember(Name = "sessionToken", Order = 13)] public new string SessionToken { get => base.SessionToken; private set => base.SessionToken = value; }
        [DataMember(Name = "productId", Order = 14)] public new string ProductId { get => base.ProductId; private set => base.ProductId = value; }
        [DataMember(Name = "expectedCurrentVersionId", Order = 15)] public new string ExpectedCurrentVersionId { get => base.ExpectedCurrentVersionId; private set => base.ExpectedCurrentVersionId = value; }
        [DataMember(Name = "versionId", Order = 16)] public string VersionId { get; private set; }

        public bool IsValid()
        {
            return CommonIsValid(false) &&
                   PosProductImageContractV1.IsCanonicalUuid(VersionId) &&
                   string.Equals(PayloadHash, PosProductImageCanonicalPayload.ComputeHash(this), StringComparison.Ordinal);
        }
    }

    [DataContract]
    public sealed class PosProductImageRemoveRequest : PosProductImageMutationRequestBase
    {
        public PosProductImageRemoveRequest(
            string operationId,
            string idempotencyKey,
            PosProductImageEnvelope envelope,
            string productId,
            string expectedCurrentVersionId)
            : base(operationId, idempotencyKey, envelope, productId, expectedCurrentVersionId)
        {
            PayloadHash = PosProductImageCanonicalPayload.ComputeHash(this);
        }

        private PosProductImageRemoveRequest() { }

        [DataMember(Name = "schemaVersion", Order = 1)]
        public string SchemaVersion
        {
            get => PosProductImageContractV1.SchemaVersion;
            private set { }
        }

        [DataMember(Name = "operation", Order = 2)]
        public string Operation
        {
            get => "remove";
            private set { }
        }
        [DataMember(Name = "operationId", Order = 3)] public new string OperationId { get => base.OperationId; private set => base.OperationId = value; }
        [DataMember(Name = "idempotencyKey", Order = 4)] public new string IdempotencyKey { get => base.IdempotencyKey; private set => base.IdempotencyKey = value; }
        [DataMember(Name = "payloadHash", Order = 5)] public new string PayloadHash { get => base.PayloadHash; private set => base.PayloadHash = value; }
        [DataMember(Name = "appVersion", Order = 6)] public new string AppVersion { get => base.AppVersion; private set => base.AppVersion = value; }
        [DataMember(Name = "shopId", Order = 7)] public new string ShopId { get => base.ShopId; private set => base.ShopId = value; }
        [DataMember(Name = "shopDeviceId", Order = 8)] public new string ShopDeviceId { get => base.ShopDeviceId; private set => base.ShopDeviceId = value; }
        [DataMember(Name = "staffId", Order = 9)] public new string StaffId { get => base.StaffId; private set => base.StaffId = value; }
        [DataMember(Name = "staffCredentialVersion", Order = 10)] public new int StaffCredentialVersion { get => base.StaffCredentialVersion; private set => base.StaffCredentialVersion = value; }
        [DataMember(Name = "posSessionId", Order = 11)] public new string PosSessionId { get => base.PosSessionId; private set => base.PosSessionId = value; }
        [DataMember(Name = "deviceToken", Order = 12)] public new string DeviceToken { get => base.DeviceToken; private set => base.DeviceToken = value; }
        [DataMember(Name = "sessionToken", Order = 13)] public new string SessionToken { get => base.SessionToken; private set => base.SessionToken = value; }
        [DataMember(Name = "productId", Order = 14)] public new string ProductId { get => base.ProductId; private set => base.ProductId = value; }
        [DataMember(Name = "expectedCurrentVersionId", Order = 15)] public new string ExpectedCurrentVersionId { get => base.ExpectedCurrentVersionId; private set => base.ExpectedCurrentVersionId = value; }

        public bool IsValid()
        {
            return CommonIsValid(true) &&
                   string.Equals(PayloadHash, PosProductImageCanonicalPayload.ComputeHash(this), StringComparison.Ordinal);
        }
    }

    [DataContract]
    public sealed class PosProductImageReadRef : IPosProductImageStrictContract
    {
        public PosProductImageReadRef(string productId, string variant, string versionId)
        {
            ProductId = productId;
            Variant = variant;
            VersionId = versionId;
        }

        private PosProductImageReadRef() { }
        [DataMember(Name = "productId", Order = 1)] public string ProductId { get; private set; }
        [DataMember(Name = "variant", Order = 2)] public string Variant { get; private set; }
        [DataMember(Name = "versionId", Order = 3)] public string VersionId { get; private set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            return PosProductImageContractV1.IsCanonicalUuid(ProductId) &&
                   (Variant == "main" || Variant == "thumb") &&
                   PosProductImageContractV1.IsCanonicalUuid(VersionId);
        }
    }

    [DataContract]
    public sealed class PosProductImageReadUrlsRequest
    {
        public PosProductImageReadUrlsRequest(
            PosProductImageEnvelope envelope,
            IReadOnlyList<PosProductImageReadRef> refs)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            SchemaVersion = PosProductImageContractV1.SchemaVersion;
            AppVersion = envelope.AppVersion;
            ShopId = envelope.ShopId;
            ShopDeviceId = envelope.ShopDeviceId;
            StaffId = envelope.StaffId;
            StaffCredentialVersion = envelope.StaffCredentialVersion;
            PosSessionId = envelope.PosSessionId;
            DeviceToken = envelope.DeviceToken;
            SessionToken = envelope.SessionToken;
            Refs = (refs ?? throw new ArgumentNullException(nameof(refs))).ToArray();
            if (Refs.Length < 1 || Refs.Length > ProductImageContractV1.ReadBatchMaximum)
            {
                throw new ArgumentOutOfRangeException(nameof(refs));
            }
        }

        private PosProductImageReadUrlsRequest() { }
        [DataMember(Name = "schemaVersion", Order = 1)] public string SchemaVersion { get; private set; }
        [DataMember(Name = "appVersion", Order = 2)] public string AppVersion { get; private set; }
        [DataMember(Name = "shopId", Order = 3)] public string ShopId { get; private set; }
        [DataMember(Name = "shopDeviceId", Order = 4)] public string ShopDeviceId { get; private set; }
        [DataMember(Name = "staffId", Order = 5)] public string StaffId { get; private set; }
        [DataMember(Name = "staffCredentialVersion", Order = 6)] public int StaffCredentialVersion { get; private set; }
        [DataMember(Name = "posSessionId", Order = 7)] public string PosSessionId { get; private set; }
        [DataMember(Name = "deviceToken", Order = 8)] public string DeviceToken { get; private set; }
        [DataMember(Name = "sessionToken", Order = 9)] public string SessionToken { get; private set; }
        [DataMember(Name = "refs", Order = 10)] public PosProductImageReadRef[] Refs { get; private set; }

        public bool IsValid()
        {
            return SchemaVersion == PosProductImageContractV1.SchemaVersion &&
                   !string.IsNullOrWhiteSpace(AppVersion) && AppVersion.Length <= 80 &&
                   PosProductImageContractV1.IsCanonicalUuid(ShopId) &&
                   PosProductImageContractV1.IsCanonicalUuid(ShopDeviceId) &&
                   PosProductImageContractV1.IsCanonicalUuid(StaffId) &&
                   StaffCredentialVersion >= 1 &&
                   PosProductImageContractV1.IsCanonicalUuid(PosSessionId) &&
                   !string.IsNullOrWhiteSpace(DeviceToken) && DeviceToken.Length <= 512 &&
                   !string.IsNullOrWhiteSpace(SessionToken) && SessionToken.Length <= 512 &&
                   Refs != null && Refs.Length >= 1 &&
                   Refs.Length <= ProductImageContractV1.ReadBatchMaximum &&
                   Refs.All(item => item != null && item.IsStrictlyValid()) &&
                   Refs.Select(item => item.ProductId + "\n" + item.Variant + "\n" + item.VersionId)
                       .Distinct(StringComparer.Ordinal).Count() == Refs.Length;
        }
    }

    public static class PosProductImageCanonicalPayload
    {
        public static string Write(PosProductImageIntentRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var builder = Begin("intent", request.ShopId, request.ProductId, request.ExpectedCurrentVersionId);
            builder.Append(",\"main\":");
            AppendMetadata(builder, request.Main);
            builder.Append(",\"thumb\":");
            AppendMetadata(builder, request.Thumb);
            builder.Append('}');
            return builder.ToString();
        }

        public static string Write(PosProductImageFinalizeRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var builder = Begin("finalize", request.ShopId, request.ProductId, request.ExpectedCurrentVersionId);
            builder.Append(",\"versionId\":");
            AppendString(builder, request.VersionId.ToLowerInvariant());
            builder.Append('}');
            return builder.ToString();
        }

        public static string Write(PosProductImageRemoveRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var builder = Begin("remove", request.ShopId, request.ProductId, request.ExpectedCurrentVersionId);
            builder.Append('}');
            return builder.ToString();
        }

        public static string ComputeHash(PosProductImageIntentRequest request) => Hash(Write(request));
        public static string ComputeHash(PosProductImageFinalizeRequest request) => Hash(Write(request));
        public static string ComputeHash(PosProductImageRemoveRequest request) => Hash(Write(request));

        private static StringBuilder Begin(
            string operation,
            string shopId,
            string productId,
            string expectedCurrentVersionId)
        {
            var builder = new StringBuilder(512);
            builder.Append("{\"schemaVersion\":");
            AppendString(builder, PosProductImageContractV1.SchemaVersion);
            builder.Append(",\"operation\":");
            AppendString(builder, operation);
            builder.Append(",\"shopId\":");
            AppendString(builder, shopId.ToLowerInvariant());
            builder.Append(",\"productId\":");
            AppendString(builder, productId.ToLowerInvariant());
            builder.Append(",\"expectedCurrentVersionId\":");
            if (expectedCurrentVersionId == null) builder.Append("null");
            else AppendString(builder, expectedCurrentVersionId.ToLowerInvariant());
            return builder;
        }

        private static void AppendMetadata(
            StringBuilder builder,
            PosProductImageUploadMetadata metadata)
        {
            builder.Append("{\"bytes\":");
            builder.Append(metadata.Bytes.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"height\":");
            builder.Append(metadata.Height.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"mimeType\":");
            AppendString(builder, metadata.MimeType);
            builder.Append(",\"sha256\":");
            AppendString(builder, metadata.Sha256);
            builder.Append(",\"width\":");
            builder.Append(metadata.Width.ToString(CultureInfo.InvariantCulture));
            builder.Append('}');
        }

        private static string Hash(string canonicalJson)
        {
            using (var algorithm = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(canonicalJson);
                var digest = algorithm.ComputeHash(bytes);
                var builder = new StringBuilder(71);
                builder.Append("sha256:");
                foreach (var value in digest)
                {
                    builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private static void AppendString(StringBuilder builder, string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }

    public sealed class PosProductImageOperationIdentity
    {
        public PosProductImageOperationIdentity(
            string operationId,
            string idempotencyKey,
            string payloadHash)
        {
            if (!PosProductImageIdentityPolicy.IsSafeId(operationId))
                throw new ArgumentException("product_image_operation_id_invalid", nameof(operationId));
            if (!PosProductImageIdentityPolicy.IsSafeId(idempotencyKey))
                throw new ArgumentException("product_image_idempotency_key_invalid", nameof(idempotencyKey));
            if (!PosProductImageContractV1.IsPayloadHash(payloadHash))
                throw new ArgumentException("product_image_payload_hash_invalid", nameof(payloadHash));
            OperationId = operationId;
            IdempotencyKey = idempotencyKey;
            PayloadHash = payloadHash;
        }

        public string OperationId { get; }
        public string IdempotencyKey { get; }
        public string PayloadHash { get; }
    }

    public static class PosProductImageIdentityPolicy
    {
        public static bool IsSafeId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 120) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= 'A' && character <= 'Z') ||
                      (character >= 'a' && character <= 'z') ||
                      (character >= '0' && character <= '9') ||
                      character == '.' || character == '_' ||
                      character == ':' || character == '-'))
                {
                    return false;
                }
            }
            var lowered = value.ToLowerInvariant();
            return !lowered.Contains("token") &&
                   !lowered.Contains("secret") &&
                   !lowered.Contains("password") &&
                   !lowered.Contains("credential") &&
                   !lowered.Contains("bearer");
        }
    }

    [DataContract]
    public sealed class PosProductImageIntentResponse : IPosProductImageStrictContract
    {
        [DataMember(Name = "schemaVersion", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "operation", Order = 2)] public string Operation { get; set; }
        [DataMember(Name = "operationId", Order = 3)] public string OperationId { get; set; }
        [DataMember(Name = "idempotencyKey", Order = 4)] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash", Order = 5)] public string PayloadHash { get; set; }
        [DataMember(Name = "ok", Order = 6)] public bool Ok { get; set; }
        [DataMember(Name = "code", Order = 7)] public string Code { get; set; }
        [DataMember(Name = "replayed", Order = 8)] public bool Replayed { get; set; }
        [DataMember(Name = "serverTime", Order = 9)] public string ServerTime { get; set; }
        [DataMember(Name = "cacheScope", Order = 10)] public string CacheScope { get; set; }
        [DataMember(Name = "status", Order = 11)] public string Status { get; set; }
        [DataMember(Name = "versionId", Order = 12)] public string VersionId { get; set; }
        [DataMember(Name = "expiresAt", Order = 13, EmitDefaultValue = false)] public string ExpiresAt { get; set; }
        [DataMember(Name = "mainUploadUrl", Order = 14, EmitDefaultValue = false)] public string MainUploadUrl { get; set; }
        [DataMember(Name = "thumbUploadUrl", Order = 15, EmitDefaultValue = false)] public string ThumbUploadUrl { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            var upload = Status == "upload_required";
            return SchemaVersion == PosProductImageContractV1.SchemaVersion &&
                   Operation == "intent" &&
                   PosProductImageIdentityPolicy.IsSafeId(OperationId) &&
                   PosProductImageIdentityPolicy.IsSafeId(IdempotencyKey) &&
                   PosProductImageContractV1.IsPayloadHash(PayloadHash) &&
                   Ok && Code == "success" &&
                   PosProductImageContractV1.IsCanonicalTimestamp(ServerTime) &&
                   PosProductImageContractV1.IsCacheScope(CacheScope) &&
                   PosProductImageContractV1.IsCanonicalUuid(VersionId) &&
                   (Status == "noop" || upload) &&
                   (upload
                       ? PosProductImageContractV1.IsCanonicalTimestamp(ExpiresAt) &&
                         !string.IsNullOrWhiteSpace(MainUploadUrl) &&
                         !string.IsNullOrWhiteSpace(ThumbUploadUrl)
                       : ExpiresAt == null && MainUploadUrl == null && ThumbUploadUrl == null);
        }
    }

    [DataContract]
    public sealed class PosProductImageFinalizeResponse : IPosProductImageStrictContract
    {
        [DataMember(Name = "schemaVersion", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "operation", Order = 2)] public string Operation { get; set; }
        [DataMember(Name = "operationId", Order = 3)] public string OperationId { get; set; }
        [DataMember(Name = "idempotencyKey", Order = 4)] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash", Order = 5)] public string PayloadHash { get; set; }
        [DataMember(Name = "ok", Order = 6)] public bool Ok { get; set; }
        [DataMember(Name = "code", Order = 7)] public string Code { get; set; }
        [DataMember(Name = "replayed", Order = 8)] public bool Replayed { get; set; }
        [DataMember(Name = "serverTime", Order = 9)] public string ServerTime { get; set; }
        [DataMember(Name = "status", Order = 10)] public string Status { get; set; }
        [DataMember(Name = "versionId", Order = 11)] public string VersionId { get; set; }
        [DataMember(Name = "imageUpdatedAt", Order = 12)] public string ImageUpdatedAt { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            return SchemaVersion == PosProductImageContractV1.SchemaVersion &&
                   Operation == "finalize" &&
                   PosProductImageIdentityPolicy.IsSafeId(OperationId) &&
                   PosProductImageIdentityPolicy.IsSafeId(IdempotencyKey) &&
                   PosProductImageContractV1.IsPayloadHash(PayloadHash) &&
                   Ok && Code == "success" &&
                   PosProductImageContractV1.IsCanonicalTimestamp(ServerTime) &&
                   (Status == "finalized" || Status == "already_finalized") &&
                   PosProductImageContractV1.IsCanonicalUuid(VersionId) &&
                   PosProductImageContractV1.IsCanonicalTimestamp(ImageUpdatedAt);
        }
    }

    [DataContract]
    public sealed class PosProductImageReadItem : IPosProductImageStrictContract
    {
        [DataMember(Name = "expiresAt", Order = 1, EmitDefaultValue = false)] public string ExpiresAt { get; set; }
        [DataMember(Name = "metadata", Order = 2, EmitDefaultValue = false)] public PosProductImageUploadMetadata Metadata { get; set; }
        [DataMember(Name = "productId", Order = 3)] public string ProductId { get; set; }
        [DataMember(Name = "signedUrl", Order = 4, EmitDefaultValue = false)] public string SignedUrl { get; set; }
        [DataMember(Name = "status", Order = 5)] public string Status { get; set; }
        [DataMember(Name = "variant", Order = 6)] public string Variant { get; set; }
        [DataMember(Name = "versionId", Order = 7)] public string VersionId { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            var ready = Status == "ready";
            var metadataMatchesVariant = Metadata == null ||
                (Variant == "main"
                    ? Metadata.Bytes <= ProductImageContractV1.MainMaximumBytes &&
                      Metadata.Width <= ProductImageContractV1.MainMaximumSide &&
                      Metadata.Height <= ProductImageContractV1.MainMaximumSide
                    : Metadata.Bytes <= ProductImageContractV1.ThumbMaximumBytes &&
                      Metadata.Width <= ProductImageContractV1.ThumbMaximumSide &&
                      Metadata.Height <= ProductImageContractV1.ThumbMaximumSide);
            return PosProductImageContractV1.IsCanonicalUuid(ProductId) &&
                   PosProductImageContractV1.IsCanonicalUuid(VersionId) &&
                   (Variant == "main" || Variant == "thumb") &&
                   (ready || Status == "not_found") &&
                   (ready
                       ? PosProductImageContractV1.IsCanonicalTimestamp(ExpiresAt) &&
                         Metadata != null && Metadata.IsStrictlyValid() && metadataMatchesVariant &&
                         !string.IsNullOrWhiteSpace(SignedUrl)
                       : ExpiresAt == null && Metadata == null && SignedUrl == null);
        }
    }

    [DataContract]
    public sealed class PosProductImageReadUrlsResponse : IPosProductImageStrictContract
    {
        [DataMember(Name = "schemaVersion", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "operation", Order = 2)] public string Operation { get; set; }
        [DataMember(Name = "ok", Order = 3)] public bool Ok { get; set; }
        [DataMember(Name = "code", Order = 4)] public string Code { get; set; }
        [DataMember(Name = "serverTime", Order = 5)] public string ServerTime { get; set; }
        [DataMember(Name = "cacheScope", Order = 10)] public string CacheScope { get; set; }
        [DataMember(Name = "items", Order = 11)] public PosProductImageReadItem[] Items { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            return SchemaVersion == PosProductImageContractV1.SchemaVersion &&
                   Operation == "read-urls" &&
                   Ok && Code == "success" &&
                   PosProductImageContractV1.IsCanonicalTimestamp(ServerTime) &&
                   PosProductImageContractV1.IsCacheScope(CacheScope) &&
                   Items != null && Items.Length >= 1 &&
                   Items.Length <= ProductImageContractV1.ReadBatchMaximum &&
                   Items.All(item => item != null && item.IsStrictlyValid());
        }
    }

    [DataContract]
    public sealed class PosProductImageRemoveResponse : IPosProductImageStrictContract
    {
        [DataMember(Name = "schemaVersion", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "operation", Order = 2)] public string Operation { get; set; }
        [DataMember(Name = "operationId", Order = 3)] public string OperationId { get; set; }
        [DataMember(Name = "idempotencyKey", Order = 4)] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash", Order = 5)] public string PayloadHash { get; set; }
        [DataMember(Name = "ok", Order = 6)] public bool Ok { get; set; }
        [DataMember(Name = "code", Order = 7)] public string Code { get; set; }
        [DataMember(Name = "replayed", Order = 8)] public bool Replayed { get; set; }
        [DataMember(Name = "serverTime", Order = 9)] public string ServerTime { get; set; }
        [DataMember(Name = "shopId", Order = 10)] public string ShopId { get; set; }
        [DataMember(Name = "productId", Order = 11)] public string ProductId { get; set; }
        [DataMember(Name = "versionId", Order = 12)] public string VersionId { get; set; }
        [DataMember(Name = "currentImageVersionId", Order = 13)] public string CurrentImageVersionId { get; set; }
        [DataMember(Name = "status", Order = 14)] public string Status { get; set; }
        [DataMember(Name = "cleanupStatus", Order = 15, EmitDefaultValue = false)] public string CleanupStatus { get; set; }
        [DataMember(Name = "imageUpdatedAt", Order = 16, EmitDefaultValue = false)] public string ImageUpdatedAt { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            var removed = Status == "removed";
            return SchemaVersion == PosProductImageContractV1.SchemaVersion &&
                   Operation == "remove" &&
                   PosProductImageIdentityPolicy.IsSafeId(OperationId) &&
                   PosProductImageIdentityPolicy.IsSafeId(IdempotencyKey) &&
                   PosProductImageContractV1.IsPayloadHash(PayloadHash) &&
                   Ok && Code == "success" &&
                   PosProductImageContractV1.IsCanonicalTimestamp(ServerTime) &&
                   PosProductImageContractV1.IsCanonicalUuid(ShopId) &&
                   PosProductImageContractV1.IsCanonicalUuid(ProductId) &&
                   PosProductImageContractV1.IsCanonicalUuid(VersionId) &&
                   CurrentImageVersionId == null &&
                   (removed || Status == "already_removed") &&
                   (removed
                       ? (CleanupStatus == "complete" || CleanupStatus == "pending") &&
                         PosProductImageContractV1.IsCanonicalTimestamp(ImageUpdatedAt)
                       : CleanupStatus == null && ImageUpdatedAt == null);
        }
    }

    [DataContract]
    public sealed class PosProductImageError : IPosProductImageStrictContract
    {
        [DataMember(Name = "schemaVersion", Order = 1)] public string SchemaVersion { get; set; }
        [DataMember(Name = "operation", Order = 2)] public string Operation { get; set; }
        [DataMember(Name = "operationId", Order = 3, EmitDefaultValue = false)] public string OperationId { get; set; }
        [DataMember(Name = "idempotencyKey", Order = 4, EmitDefaultValue = false)] public string IdempotencyKey { get; set; }
        [DataMember(Name = "payloadHash", Order = 5, EmitDefaultValue = false)] public string PayloadHash { get; set; }
        [DataMember(Name = "ok", Order = 6)] public bool Ok { get; set; }
        [DataMember(Name = "code", Order = 7)] public string Code { get; set; }
        [DataMember(Name = "message", Order = 8, EmitDefaultValue = false)] public string Message { get; set; }
        [DataMember(Name = "retryable", Order = 9)] public bool Retryable { get; set; }
        [DataMember(Name = "serverTime", Order = 10)] public string ServerTime { get; set; }
        [DataMember(Name = "requestId", Order = 11)] public string RequestId { get; set; }
        [DataMember(Name = "clientRequestId", Order = 12, EmitDefaultValue = false)] public string ClientRequestId { get; set; }
        [DataMember(Name = "terminal", Order = 13, EmitDefaultValue = false)] public bool? Terminal { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }

        public bool IsStrictlyValid()
        {
            return SchemaVersion == PosProductImageContractV1.SchemaVersion &&
                   !Ok &&
                   (Operation == "intent" || Operation == "finalize" ||
                    Operation == "read-urls" || Operation == "remove") &&
                   (OperationId == null ||
                    PosProductImageIdentityPolicy.IsSafeId(OperationId)) &&
                   (IdempotencyKey == null ||
                    PosProductImageIdentityPolicy.IsSafeId(IdempotencyKey)) &&
                   (PayloadHash == null ||
                    PosProductImageContractV1.IsPayloadHash(PayloadHash)) &&
                   PosProductImageContractV1.IsErrorCode(Code) &&
                   (Message == null ||
                    PosProductImageContractV1.IsBoundedTextWithoutControls(Message, 160)) &&
                   PosProductImageIdentityPolicy.IsSafeId(RequestId) &&
                   (ClientRequestId == null ||
                    PosProductImageIdentityPolicy.IsSafeId(ClientRequestId)) &&
                   PosProductImageContractV1.IsCanonicalTimestamp(ServerTime);
        }
    }

    public enum PosProductImageFailureKind
    {
        None = 0,
        AuthDenied,
        Validation,
        Conflict,
        IdempotencyMismatch,
        ExpiredCapability,
        RetryableTransport,
        RetryableUpstream,
        CorruptResponse,
        RateLimited,
        TerminalImageValidation
    }

    public static class PosProductImageResultMapping
    {
        public static PosProductImageFailureKind Map(int? httpStatus, string code, bool retryable)
        {
            if (httpStatus == 401 || httpStatus == 403 || code == "auth_denied" || code == "permission_denied")
                return PosProductImageFailureKind.AuthDenied;
            if (code == "idempotency_conflict" || code == "idempotency_payload_mismatch" || code == "payload_hash_mismatch")
                return PosProductImageFailureKind.IdempotencyMismatch;
            if (code == "expected_version_conflict" || code == "stale_conflict" || code == "receipt_conflict")
                return PosProductImageFailureKind.Conflict;
            if (code == "intent_expired" || code == "expired_capability")
                return PosProductImageFailureKind.ExpiredCapability;
            if (httpStatus == 429 || code == "rate_limited")
                return PosProductImageFailureKind.RateLimited;
            if (!string.IsNullOrEmpty(code) &&
                (code.StartsWith("jpeg_", StringComparison.Ordinal) || code == "storage_object_missing"))
                return PosProductImageFailureKind.TerminalImageValidation;
            if (retryable || httpStatus >= 500)
                return PosProductImageFailureKind.RetryableUpstream;
            if (httpStatus == 400 || httpStatus == 404 || httpStatus == 422 || code == "validation_failed")
                return PosProductImageFailureKind.Validation;
            return PosProductImageFailureKind.CorruptResponse;
        }
    }
}
