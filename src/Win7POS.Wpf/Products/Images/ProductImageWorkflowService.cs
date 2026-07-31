using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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
    public sealed class ProductImageMutationResult
    {
        internal ProductImageMutationResult(
            string operationId,
            string state,
            byte[] previewBytes)
        {
            OperationId = operationId ?? string.Empty;
            State = state ?? string.Empty;
            PreviewBytes = previewBytes == null ? null : (byte[])previewBytes.Clone();
        }

        public string OperationId { get; }
        public byte[] PreviewBytes { get; }
        public string State { get; }
    }

    public sealed class ProductImageWorkflowService
    {
        private readonly ProductImagePreprocessService _preprocess;
        private readonly ProductImageStagingStore _staging;
        private readonly ProductImageOperationOutboxRepository _outbox;
        private readonly PosTrustedDeviceStore _trustedStore;

        public ProductImageWorkflowService()
            : this(
                new SqliteConnectionFactory(PosDbOptions.Default()),
                new ProductImageStagingStore(),
                new PosTrustedDeviceStore())
        {
        }

        internal ProductImageWorkflowService(
            SqliteConnectionFactory factory,
            ProductImageStagingStore staging,
            PosTrustedDeviceStore trustedStore)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _preprocess = new ProductImagePreprocessService();
            _staging = staging ?? throw new ArgumentNullException(nameof(staging));
            _outbox = new ProductImageOperationOutboxRepository(factory);
            _trustedStore = trustedStore ??
                throw new ArgumentNullException(nameof(trustedStore));
        }

        public async Task<ProductImageMutationResult> ChooseOrReplaceAsync(
            ProductDetailsRow product,
            string sourceFile,
            IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            if (product == null || product.Id <= 0)
                throw new ArgumentException("product_image_product_invalid", nameof(product));
            if (string.IsNullOrWhiteSpace(sourceFile))
                throw new ArgumentException("product_image_source_required", nameof(sourceFile));
            progress?.Report("preprocessing");
            var processed = await _preprocess.PreprocessFileAsync(
                sourceFile,
                cancellationToken).ConfigureAwait(false);
            if (!processed.IsSuccess)
            {
                var code = processed.Issues.Count > 0
                    ? processed.Issues[0].Code
                    : "image_preprocess_failed";
                throw new InvalidDataException(code);
            }

            progress?.Report("staging");
            var staged = await _staging.StagePairAsync(
                processed.Main,
                processed.Thumb,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var intendedIdentity = "local-image-" + Guid.NewGuid().ToString("N");
                var request = new ProductImageReplaceEnqueueRequest
                {
                    LocalProductId = product.Id,
                    ExpectedCurrentVersionId = CanonicalOrNull(
                        product.PrimaryImageVersionId),
                    IntendedLocalVersionIdentity = intendedIdentity,
                    Main = StagedVariant(processed.Main, staged.MainIdentity),
                    Thumb = StagedVariant(processed.Thumb, staged.ThumbIdentity)
                };
                request.PayloadHash = WaitingDependencyPayloadHash(
                    intendedIdentity,
                    request);
                Func<string, string, string> sealPayloadHash = null;
                if (PosProductImageContractV1.IsCanonicalUuid(
                    product.RemoteProductId))
                {
                    PosTrustedDeviceSession session;
                    if (!_trustedStore.TryRead(out session))
                    {
                        throw new InvalidOperationException(
                            "product_image_trusted_session_missing");
                    }
                    sealPayloadHash = (operationId, idempotencyKey) =>
                        ReplacePayloadHash(
                            product,
                            request,
                            session.ShopId,
                            operationId,
                            idempotencyKey);
                }
                var enqueued = await _outbox.EnqueueReplaceAsync(
                    request,
                    sealPayloadHash,
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(
                    enqueued.State == ProductImageOperationStates.WaitingDependency
                        ? "waiting_dependency"
                        : "queued");
                SignalMutation();
                return new ProductImageMutationResult(
                    enqueued.OperationId,
                    enqueued.State,
                    processed.Main.CopyBytes());
            }
            catch
            {
                await _staging.DeletePairAsync(
                    staged.MainIdentity,
                    staged.ThumbIdentity,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        public async Task<ProductImageMutationResult> RemoveAsync(
            ProductDetailsRow product,
            CancellationToken cancellationToken)
        {
            if (product == null || product.Id <= 0 ||
                !PosProductImageContractV1.IsCanonicalUuid(product.RemoteProductId) ||
                !PosProductImageContractV1.IsCanonicalUuid(product.PrimaryImageVersionId))
            {
                throw new InvalidOperationException("product_image_remove_unavailable");
            }
            PosTrustedDeviceSession session;
            if (!_trustedStore.TryRead(out session))
                throw new InvalidOperationException("product_image_trusted_session_missing");
            var payloadHash = new PosProductImageRemoveRequest(
                "image-preview-remove",
                "image-preview-remove-idem",
                Envelope(session),
                product.RemoteProductId,
                product.PrimaryImageVersionId).PayloadHash;
            var result = await _outbox.EnqueueRemoveAsync(
                new ProductImageRemoveEnqueueRequest
                {
                    LocalProductId = product.Id,
                    ExpectedCurrentVersionId = product.PrimaryImageVersionId,
                    PayloadHash = payloadHash
                },
                (operationId, idempotencyKey) =>
                    new PosProductImageRemoveRequest(
                        operationId + "-remove",
                        idempotencyKey + "-remove",
                        Envelope(session),
                        product.RemoteProductId,
                        product.PrimaryImageVersionId).PayloadHash,
                cancellationToken).ConfigureAwait(false);
            SignalMutation();
            return new ProductImageMutationResult(
                result.OperationId,
                result.State,
                null);
        }

        public async Task<bool> RetryLatestBlockedAsync(
            ProductDetailsRow product,
            CancellationToken cancellationToken)
        {
            if (product == null) return false;
            var blocked = await _outbox.GetLatestForProductAsync(product.Id)
                .ConfigureAwait(false);
            if (blocked == null || blocked.State != ProductImageOperationStates.FailedBlocked)
                return false;
            PosTrustedDeviceSession session;
            if (!_trustedStore.TryRead(out session) ||
                !PosProductImageContractV1.IsCanonicalUuid(product.RemoteProductId))
                return false;
            var expected = CanonicalOrNull(product.PrimaryImageVersionId);
            Func<string, string, string> resealPayloadHash;
            if (blocked.OperationKind == ProductImageOperationKinds.Replace)
            {
                blocked.RemoteProductId = product.RemoteProductId;
                blocked.ExpectedCurrentVersionId = expected;
                resealPayloadHash = (operationId, idempotencyKey) =>
                    ReplacePayloadHash(
                        blocked,
                        session.ShopId,
                        operationId,
                        idempotencyKey);
            }
            else
            {
                if (expected == null) return false;
                resealPayloadHash = (operationId, idempotencyKey) =>
                    new PosProductImageRemoveRequest(
                        operationId + "-remove",
                        idempotencyKey + "-remove",
                        Envelope(session),
                        product.RemoteProductId,
                        expected).PayloadHash;
            }
            var changed = await _outbox.RetryBlockedAsNewAsync(
                blocked.OperationId,
                product.RemoteProductId,
                expected,
                resealPayloadHash).ConfigureAwait(false);
            if (changed) SignalMutation();
            return changed;
        }

        public Task<ProductImageOperationRow> GetLatestOperationAsync(long productId)
        {
            return _outbox.GetLatestForProductAsync(productId);
        }

        public Task LoadEditorImageAsync(
            ProductDetailsRow product,
            ProductImageDisplayViewModel display,
            CancellationToken cancellationToken)
        {
            return ProductImageRuntime.LoadAsync(
                product,
                ProductImageVariant.Main,
                ProductImageDecodeProfile.EditorPreview,
                display,
                cancellationToken);
        }

        public static Task<BitmapSource> DecodeLocalPreviewAsync(
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 512;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return (BitmapSource)image;
                }
            }, cancellationToken);
        }

        private static string WaitingDependencyPayloadHash(
            string intendedIdentity,
            ProductImageReplaceEnqueueRequest request)
        {
            return "sha256:" + ProductImageHash.Sha256Hex(Encoding.UTF8.GetBytes(
                "waiting-dependency-v1\n" + intendedIdentity + "\n" +
                request.Main.Sha256 + "\n" + request.Thumb.Sha256));
        }

        private static string ReplacePayloadHash(
            ProductDetailsRow product,
            ProductImageReplaceEnqueueRequest request,
            string shopId,
            string operationId,
            string idempotencyKey)
        {
            var operation = new ProductImageOperationRow
            {
                OperationId = operationId,
                IdempotencyKey = idempotencyKey,
                RemoteProductId = product.RemoteProductId,
                ExpectedCurrentVersionId = request.ExpectedCurrentVersionId,
                MainBytes = request.Main.Bytes,
                MainWidth = request.Main.Width,
                MainHeight = request.Main.Height,
                MainSha256 = request.Main.Sha256,
                ThumbBytes = request.Thumb.Bytes,
                ThumbWidth = request.Thumb.Width,
                ThumbHeight = request.Thumb.Height,
                ThumbSha256 = request.Thumb.Sha256
            };
            return ReplacePayloadHash(operation, shopId);
        }

        private static string ReplacePayloadHash(
            ProductImageOperationRow operation,
            string shopId,
            string operationId = null,
            string idempotencyKey = null)
        {
            return new PosProductImageIntentRequest(
                (operationId ?? operation.OperationId) + "-intent",
                (idempotencyKey ?? operation.IdempotencyKey) + "-intent",
                new PosProductImageEnvelope(
                    "0.0.0.0",
                    shopId,
                    "10000000-0000-4000-8000-000000000001",
                    "10000000-0000-4000-8000-000000000002",
                    1,
                    "10000000-0000-4000-8000-000000000003",
                    "ephemeral",
                    "ephemeral"),
                operation.RemoteProductId,
                operation.ExpectedCurrentVersionId,
                UploadMetadata(operation, ProductImageVariant.Main),
                UploadMetadata(operation, ProductImageVariant.Thumb)).PayloadHash;
        }

        private static PosProductImageEnvelope Envelope(PosTrustedDeviceSession session)
        {
            return new PosProductImageEnvelope(
                typeof(ProductImageWorkflowService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                session.ShopId,
                session.ShopDeviceId,
                session.StaffId,
                session.StaffCredentialVersion,
                session.PosSessionId,
                session.DeviceToken,
                session.SessionToken);
        }

        private static ProductImageStagedVariant StagedVariant(
            ProductImageProcessedVariant value,
            string identity)
        {
            return new ProductImageStagedVariant
            {
                Bytes = value.Metadata.ByteSize,
                Width = value.Metadata.Width,
                Height = value.Metadata.Height,
                Sha256 = value.Metadata.Sha256,
                Identity = identity
            };
        }

        private static PosProductImageUploadMetadata UploadMetadata(
            ProductImageOperationRow operation,
            ProductImageVariant variant)
        {
            var main = variant == ProductImageVariant.Main;
            return new PosProductImageUploadMetadata(
                main ? operation.MainBytes.Value : operation.ThumbBytes.Value,
                main ? operation.MainHeight.Value : operation.ThumbHeight.Value,
                ImageContract.WireMimeType,
                main ? operation.MainSha256 : operation.ThumbSha256,
                main ? operation.MainWidth.Value : operation.ThumbWidth.Value);
        }

        private static string CanonicalOrNull(string value)
        {
            return PosProductImageContractV1.IsCanonicalUuid(value) ? value : null;
        }

        private static void SignalMutation()
        {
            PosOnlineSyncSignalBus.Signal(
                OnlineSyncLane.ProductImageOutbox,
                OnlineSyncLaneTrigger.LocalCommit);
        }
    }
}
