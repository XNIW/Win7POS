using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Images;
using Win7POS.Core.Online;
using Win7POS.Data.Images;
using ImageContract = Win7POS.Core.Images.ProductImageContractV1;

namespace Win7POS.Data.Online
{
    public sealed class ProductImageSyncResult
    {
        internal ProductImageSyncResult(
            bool success,
            string code,
            bool authenticationDenied,
            bool offline,
            bool hasImmediateMore,
            long? nextRetryAt,
            bool requestCatalogNow,
            bool terminal)
        {
            Success = success;
            Code = code ?? string.Empty;
            AuthenticationDenied = authenticationDenied;
            Offline = offline;
            HasImmediateMore = hasImmediateMore;
            NextRetryAt = nextRetryAt;
            RequestCatalogNow = requestCatalogNow;
            Terminal = terminal;
        }

        public bool AuthenticationDenied { get; }
        public string Code { get; }
        public bool HasImmediateMore { get; }
        public long? NextRetryAt { get; }
        public bool Offline { get; }
        public bool RequestCatalogNow { get; }
        public bool Success { get; }
        public bool Terminal { get; }
    }

    /// <summary>
    /// Executes one fenced image-state transition per supervisor turn. Every
    /// trusted envelope is created immediately before its request; signed
    /// capabilities remain in method locals and never enter durable state.
    /// </summary>
    public sealed class ProductImageSyncService
    {
        private const int MaximumAutomaticAttempts = 8;
        private readonly ProductImageOperationOutboxRepository _outbox;
        private readonly ProductImageStagingStore _staging;
        private readonly Func<PosAdminWebOptions, Uri, IPosProductImageTransport>
            _transportFactory;
        private readonly Func<DateTimeOffset> _clock;
        private int _orphanCleanupAttempted;

        public ProductImageSyncService(SqliteConnectionFactory factory)
            : this(factory, new ProductImageStagingStore(), null, null)
        {
        }

        internal ProductImageSyncService(
            SqliteConnectionFactory factory,
            ProductImageStagingStore staging,
            Func<PosAdminWebOptions, Uri, IPosProductImageTransport> transportFactory,
            Func<DateTimeOffset> clock)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _outbox = new ProductImageOperationOutboxRepository(factory);
            _staging = staging ?? throw new ArgumentNullException(nameof(staging));
            _transportFactory = transportFactory ??
                ((options, origin) => new PosProductImageClient(options, origin));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public async Task<ProductImageSyncResult> SyncNextAsync(
            PosAdminWebOptions options,
            Uri storageOrigin,
            PosTrustedDeviceSession trustedSession,
            OnlineSyncLaneExecutionContext executionContext,
            string appVersion,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (storageOrigin == null) throw new ArgumentNullException(nameof(storageOrigin));
            if (trustedSession == null) throw new ArgumentNullException(nameof(trustedSession));
            if (executionContext == null) throw new ArgumentNullException(nameof(executionContext));

            var dependencies = await _outbox.GetCatalogReconciledDependenciesAsync()
                .ConfigureAwait(false);
            foreach (var dependency in dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _outbox.ReleaseDependenciesAsync(
                    dependency.LocalProductId.Value,
                    dependency.RemoteProductId,
                    row => IntentPayloadHash(row, trustedSession.ShopId),
                    cancellationToken).ConfigureAwait(false);
            }
            if (Interlocked.CompareExchange(ref _orphanCleanupAttempted, 1, 0) == 0)
            {
                try
                {
                    var referenced = await _outbox.GetReferencedStagingIdentitiesAsync()
                        .ConfigureAwait(false);
                    await _staging.CleanupOrphansAsync(
                        referenced,
                        _clock(),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException)
                {
                    return await BuildResultAsync(
                        false,
                        "product_image_staging_cleanup_failed",
                        terminal: true).ConfigureAwait(false);
                }
            }
            await _outbox.RecoverInterruptedClaimsAsync(
                executionContext.Generation.GenerationId,
                cancellationToken).ConfigureAwait(false);
            var now = _clock();
            var claim = await _outbox.ClaimNextAsync(
                executionContext.Generation.GenerationId,
                now.ToUnixTimeMilliseconds(),
                cancellationToken).ConfigureAwait(false);
            if (claim == null)
                return await BuildResultAsync(true, "success").ConfigureAwait(false);

            using (var transport = _transportFactory(options, storageOrigin))
            {
                switch (claim.Operation.ResumeState)
                {
                    case ProductImageOperationStates.PendingIntent:
                        return await RunIntentAsync(
                            claim,
                            transport,
                            trustedSession,
                            executionContext,
                            appVersion,
                            cancellationToken).ConfigureAwait(false);
                    case ProductImageOperationStates.PendingUpload:
                        return await RunUploadAsync(
                            claim,
                            transport,
                            trustedSession,
                            executionContext,
                            appVersion,
                            cancellationToken).ConfigureAwait(false);
                    case ProductImageOperationStates.PendingFinalize:
                        return await RunFinalizeAsync(
                            claim,
                            transport,
                            trustedSession,
                            executionContext,
                            appVersion,
                            cancellationToken).ConfigureAwait(false);
                    case ProductImageOperationStates.PendingRemove:
                        return await RunRemoveAsync(
                            claim,
                            transport,
                            trustedSession,
                            executionContext,
                            appVersion,
                            cancellationToken).ConfigureAwait(false);
                    case ProductImageOperationStates.CleanupPending:
                        return await RunCleanupAsync(claim, cancellationToken)
                            .ConfigureAwait(false);
                    default:
                        await _outbox.BlockAsync(claim, "invalid_resume_state")
                            .ConfigureAwait(false);
                        return await BuildResultAsync(
                            false,
                            "invalid_resume_state",
                            terminal: true).ConfigureAwait(false);
                }
            }
        }

        private async Task<ProductImageSyncResult> RunIntentAsync(
            ProductImageOperationClaim claim,
            IPosProductImageTransport transport,
            PosTrustedDeviceSession session,
            OnlineSyncLaneExecutionContext context,
            string appVersion,
            CancellationToken cancellationToken)
        {
            var operation = claim.Operation;
            var main = UploadMetadata(operation, ProductImageVariant.Main);
            var thumb = UploadMetadata(operation, ProductImageVariant.Thumb);
            var response = await context.ExecuteCredentialedRequestAsync(
                (credentials, token) =>
                {
                    var request = new PosProductImageIntentRequest(
                        PhaseId(operation.OperationId, "intent"),
                        PhaseId(operation.IdempotencyKey, "intent"),
                        Envelope(session, credentials, appVersion),
                        operation.RemoteProductId,
                        operation.ExpectedCurrentVersionId,
                        main,
                        thumb);
                    if (!string.Equals(
                        request.PayloadHash,
                        operation.PayloadHash,
                        StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            PosProductImageClientResult<PosProductImageIntentResponse>.Failure(
                                "payload_hash_mismatch",
                                PosProductImageFailureKind.IdempotencyMismatch,
                                null,
                                false));
                    }
                    return transport.IntentAsync(request, token);
                },
                AuthenticationDenialCode,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                if (response.FailureKind == PosProductImageFailureKind.ExpiredCapability)
                {
                    if (operation.AttemptCount >= 2)
                        return await BlockAsync(claim, "intent_expired", true)
                            .ConfigureAwait(false);
                    await _outbox.RotateExpiredIntentAsync(
                        claim,
                        (operationId, idempotencyKey) => IntentPayloadHash(
                            operation,
                            session.ShopId,
                            operationId,
                            idempotencyKey)).ConfigureAwait(false);
                    return await BuildResultAsync(
                        true,
                        "expired_intent_rotated").ConfigureAwait(false);
                }
                return await HandleFailureAsync(claim, response).ConfigureAwait(false);
            }

            var value = response.Value;
            if (value.Status == "noop")
            {
                await _outbox.AdvanceAsync(
                    claim,
                    ProductImageOperationStates.CleanupPending,
                    value.VersionId).ConfigureAwait(false);
                return await BuildResultAsync(
                    true,
                    "intent_noop",
                    requestCatalogNow: true).ConfigureAwait(false);
            }
            await _outbox.AdvanceAsync(
                claim,
                ProductImageOperationStates.PendingUpload,
                value.VersionId).ConfigureAwait(false);
            return await BuildResultAsync(true, "intent_ready").ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> RunUploadAsync(
            ProductImageOperationClaim claim,
            IPosProductImageTransport transport,
            PosTrustedDeviceSession session,
            OnlineSyncLaneExecutionContext context,
            string appVersion,
            CancellationToken cancellationToken)
        {
            var operation = claim.Operation;
            var main = UploadMetadata(operation, ProductImageVariant.Main);
            var thumb = UploadMetadata(operation, ProductImageVariant.Thumb);
            var intent = await context.ExecuteCredentialedRequestAsync(
                (credentials, token) => transport.IntentAsync(
                    new PosProductImageIntentRequest(
                        PhaseId(operation.OperationId, "intent"),
                        PhaseId(operation.IdempotencyKey, "intent"),
                        Envelope(session, credentials, appVersion),
                        operation.RemoteProductId,
                        operation.ExpectedCurrentVersionId,
                        main,
                        thumb),
                    token),
                AuthenticationDenialCode,
                cancellationToken).ConfigureAwait(false);
            if (!intent.IsSuccess)
            {
                if (intent.FailureKind == PosProductImageFailureKind.ExpiredCapability)
                {
                    if (operation.AttemptCount >= 2)
                        return await BlockAsync(claim, "intent_expired", true)
                            .ConfigureAwait(false);
                    await _outbox.RotateExpiredIntentAsync(
                        claim,
                        (operationId, idempotencyKey) => IntentPayloadHash(
                            operation,
                            session.ShopId,
                            operationId,
                            idempotencyKey)).ConfigureAwait(false);
                    return await BuildResultAsync(
                        true,
                        "expired_intent_rotated").ConfigureAwait(false);
                }
                return await HandleFailureAsync(claim, intent).ConfigureAwait(false);
            }
            if (!string.Equals(
                intent.Value.VersionId,
                operation.ServerVersionId,
                StringComparison.Ordinal))
            {
                return await BlockAsync(claim, "intent_version_conflict", true)
                    .ConfigureAwait(false);
            }
            if (intent.Value.Status == "noop")
            {
                await _outbox.AdvanceAsync(
                    claim,
                    ProductImageOperationStates.CleanupPending,
                    intent.Value.VersionId).ConfigureAwait(false);
                return await BuildResultAsync(
                    true,
                    "intent_noop",
                    requestCatalogNow: true).ConfigureAwait(false);
            }

            PosProductImageUploadResult upload;
            try
            {
                using (var mainStream = await _staging.OpenVerifiedReadAsync(
                    operation.StagedMainIdentity,
                    ProductImageVariant.Main,
                    Metadata(operation, ProductImageVariant.Main),
                    cancellationToken).ConfigureAwait(false))
                {
                    upload = await context.ExecuteRequestAsync(
                        token => transport.UploadJpegAsync(
                            intent.Value.MainUploadUrl,
                            session.ShopId,
                            operation.RemoteProductId,
                            operation.ServerVersionId,
                            "main",
                            mainStream,
                            operation.MainBytes.Value,
                            token),
                        cancellationToken).ConfigureAwait(false);
                }
                if (!upload.IsSuccess)
                    return await HandleUploadFailureAsync(claim, upload).ConfigureAwait(false);
                using (var thumbStream = await _staging.OpenVerifiedReadAsync(
                    operation.StagedThumbIdentity,
                    ProductImageVariant.Thumb,
                    Metadata(operation, ProductImageVariant.Thumb),
                    cancellationToken).ConfigureAwait(false))
                {
                    upload = await context.ExecuteRequestAsync(
                        token => transport.UploadJpegAsync(
                            intent.Value.ThumbUploadUrl,
                            session.ShopId,
                            operation.RemoteProductId,
                            operation.ServerVersionId,
                            "thumb",
                            thumbStream,
                            operation.ThumbBytes.Value,
                            token),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (InvalidDataException)
            {
                return await BlockAsync(claim, "staged_image_corrupt", false)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return await BlockAsync(claim, "staged_image_missing", false)
                    .ConfigureAwait(false);
            }
            if (!upload.IsSuccess)
                return await HandleUploadFailureAsync(claim, upload).ConfigureAwait(false);

            await _outbox.AdvanceAsync(
                claim,
                ProductImageOperationStates.PendingFinalize,
                operation.ServerVersionId).ConfigureAwait(false);
            return await BuildResultAsync(true, "upload_complete").ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> RunFinalizeAsync(
            ProductImageOperationClaim claim,
            IPosProductImageTransport transport,
            PosTrustedDeviceSession session,
            OnlineSyncLaneExecutionContext context,
            string appVersion,
            CancellationToken cancellationToken)
        {
            var operation = claim.Operation;
            if (!PosProductImageContractV1.IsCanonicalUuid(operation.ServerVersionId))
                return await BlockAsync(claim, "finalize_version_missing", false)
                    .ConfigureAwait(false);
            var response = await context.ExecuteCredentialedRequestAsync(
                (credentials, token) => transport.FinalizeAsync(
                    new PosProductImageFinalizeRequest(
                        PhaseId(operation.OperationId, "finalize"),
                        PhaseId(operation.IdempotencyKey, "finalize"),
                        Envelope(session, credentials, appVersion),
                        operation.RemoteProductId,
                        operation.ExpectedCurrentVersionId,
                        operation.ServerVersionId),
                    token),
                AuthenticationDenialCode,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
                return await HandleFailureAsync(claim, response).ConfigureAwait(false);
            await _outbox.AdvanceAsync(
                claim,
                ProductImageOperationStates.CleanupPending,
                response.Value.VersionId,
                response.Value.ImageUpdatedAt).ConfigureAwait(false);
            return await BuildResultAsync(
                true,
                "finalize_complete",
                requestCatalogNow: true).ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> RunRemoveAsync(
            ProductImageOperationClaim claim,
            IPosProductImageTransport transport,
            PosTrustedDeviceSession session,
            OnlineSyncLaneExecutionContext context,
            string appVersion,
            CancellationToken cancellationToken)
        {
            var operation = claim.Operation;
            var response = await context.ExecuteCredentialedRequestAsync(
                (credentials, token) =>
                {
                    var request = new PosProductImageRemoveRequest(
                        PhaseId(operation.OperationId, "remove"),
                        PhaseId(operation.IdempotencyKey, "remove"),
                        Envelope(session, credentials, appVersion),
                        operation.RemoteProductId,
                        operation.ExpectedCurrentVersionId);
                    if (!string.Equals(
                        request.PayloadHash,
                        operation.PayloadHash,
                        StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            PosProductImageClientResult<PosProductImageRemoveResponse>.Failure(
                                "payload_hash_mismatch",
                                PosProductImageFailureKind.IdempotencyMismatch,
                                null,
                                false));
                    }
                    return transport.RemoveAsync(request, token);
                },
                AuthenticationDenialCode,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
                return await HandleFailureAsync(claim, response).ConfigureAwait(false);
            await _outbox.AdvanceAsync(
                claim,
                ProductImageOperationStates.CleanupPending,
                response.Value.VersionId,
                response.Value.ImageUpdatedAt).ConfigureAwait(false);
            return await BuildResultAsync(
                true,
                response.Value.CleanupStatus == "pending"
                    ? "remove_cleanup_pending"
                    : "remove_complete",
                requestCatalogNow: true).ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> RunCleanupAsync(
            ProductImageOperationClaim claim,
            CancellationToken cancellationToken)
        {
            if (claim.Operation.OperationKind == ProductImageOperationKinds.Replace)
            {
                await _staging.DeletePairAsync(
                    claim.Operation.StagedMainIdentity,
                    claim.Operation.StagedThumbIdentity,
                    cancellationToken).ConfigureAwait(false);
            }
            await _outbox.CompleteCleanupAsync(claim).ConfigureAwait(false);
            return await BuildResultAsync(true, "cleanup_complete").ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> HandleFailureAsync<T>(
            ProductImageOperationClaim claim,
            PosProductImageClientResult<T> result)
            where T : class
        {
            if (result.FailureKind == PosProductImageFailureKind.AuthDenied)
            {
                await _outbox.RetryAsync(claim, "auth_denied", _clock())
                    .ConfigureAwait(false);
                return await BuildResultAsync(
                    false,
                    "auth_denied",
                    authenticationDenied: true).ConfigureAwait(false);
            }
            if (result.FailureKind == PosProductImageFailureKind.Conflict)
                return await BlockAsync(claim, "image_conflict", true).ConfigureAwait(false);
            if (result.FailureKind == PosProductImageFailureKind.IdempotencyMismatch)
                return await BlockAsync(claim, "idempotency_mismatch", false).ConfigureAwait(false);
            if (result.FailureKind == PosProductImageFailureKind.TerminalImageValidation ||
                result.FailureKind == PosProductImageFailureKind.Validation)
            {
                return await BlockAsync(claim, SafeFailureCode(result.Code), false)
                    .ConfigureAwait(false);
            }
            var retryable = result.Retryable ||
                result.FailureKind == PosProductImageFailureKind.RetryableTransport ||
                result.FailureKind == PosProductImageFailureKind.RetryableUpstream ||
                result.FailureKind == PosProductImageFailureKind.RateLimited ||
                result.FailureKind == PosProductImageFailureKind.CorruptResponse;
            if (!retryable || claim.Operation.AttemptCount >= MaximumAutomaticAttempts)
                return await BlockAsync(claim, SafeFailureCode(result.Code), false)
                    .ConfigureAwait(false);
            await _outbox.RetryAsync(
                claim,
                SafeFailureCode(result.Code),
                NextRetry(claim.Operation)).ConfigureAwait(false);
            return await BuildResultAsync(
                false,
                SafeFailureCode(result.Code),
                offline: result.FailureKind == PosProductImageFailureKind.RetryableTransport)
                .ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> HandleUploadFailureAsync(
            ProductImageOperationClaim claim,
            PosProductImageUploadResult result)
        {
            // An upload capability is never reused. One retry replays intent
            // and obtains a fresh pair of URLs; a second failure is blocked.
            if (!result.Retryable || claim.Operation.AttemptCount >= 2)
                return await BlockAsync(claim, SafeFailureCode(result.Code), false)
                    .ConfigureAwait(false);
            await _outbox.RetryAsync(
                claim,
                SafeFailureCode(result.Code),
                result.Code == "expired_capability"
                    ? _clock()
                    : NextRetry(claim.Operation)).ConfigureAwait(false);
            return await BuildResultAsync(
                false,
                SafeFailureCode(result.Code),
                offline: result.HttpStatus == null).ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> BlockAsync(
            ProductImageOperationClaim claim,
            string code,
            bool requestCatalogNow)
        {
            await _outbox.BlockAsync(claim, SafeFailureCode(code)).ConfigureAwait(false);
            return await BuildResultAsync(
                false,
                SafeFailureCode(code),
                requestCatalogNow: requestCatalogNow,
                terminal: false).ConfigureAwait(false);
        }

        private async Task<ProductImageSyncResult> BuildResultAsync(
            bool success,
            string code,
            bool authenticationDenied = false,
            bool offline = false,
            bool requestCatalogNow = false,
            bool terminal = false)
        {
            var now = _clock().ToUnixTimeMilliseconds();
            var state = await _outbox.GetDrainStateAsync(now).ConfigureAwait(false);
            return new ProductImageSyncResult(
                success,
                code,
                authenticationDenied,
                offline,
                state.HasImmediateMore,
                state.NextWakeAt,
                requestCatalogNow,
                terminal || (!state.HasImmediateMore && state.NextWakeAt == null));
        }

        private DateTimeOffset NextRetry(ProductImageOperationRow operation)
        {
            var sequence = new[] { 5, 15, 30, 60, 120, 300 };
            var attempt = Math.Max(0, operation.AttemptCount);
            var baseSeconds = sequence[Math.Min(attempt, sequence.Length - 1)];
            byte sample;
            using (var sha = SHA256.Create())
            {
                var material = System.Text.Encoding.UTF8.GetBytes(
                    operation.OperationId + "|" + attempt);
                sample = sha.ComputeHash(material)[0];
            }
            var jitter = 0.8d + ((sample / 255d) * 0.4d);
            return _clock().AddSeconds(Math.Min(300d, baseSeconds * jitter));
        }

        private static PosProductImageEnvelope Envelope(
            PosTrustedDeviceSession session,
            OnlineSyncRequestCredentials credentials,
            string appVersion)
        {
            return new PosProductImageEnvelope(
                string.IsNullOrWhiteSpace(appVersion) ? "0.0.0.0" : appVersion.Trim(),
                session.ShopId,
                credentials.ShopDeviceId,
                session.StaffId,
                session.StaffCredentialVersion,
                credentials.PosSessionId,
                credentials.DeviceToken,
                credentials.SessionToken);
        }

        private static string IntentPayloadHash(
            ProductImageOperationRow operation,
            string shopId,
            string operationId = null,
            string idempotencyKey = null)
        {
            var placeholderEnvelope = new PosProductImageEnvelope(
                "0.0.0.0",
                shopId,
                "10000000-0000-4000-8000-000000000001",
                "10000000-0000-4000-8000-000000000002",
                1,
                "10000000-0000-4000-8000-000000000003",
                "ephemeral",
                "ephemeral");
            return new PosProductImageIntentRequest(
                PhaseId(operationId ?? operation.OperationId, "intent"),
                PhaseId(idempotencyKey ?? operation.IdempotencyKey, "intent"),
                placeholderEnvelope,
                operation.RemoteProductId,
                operation.ExpectedCurrentVersionId,
                UploadMetadata(operation, ProductImageVariant.Main),
                UploadMetadata(operation, ProductImageVariant.Thumb)).PayloadHash;
        }

        private static string AuthenticationDenialCode<T>(
            PosProductImageClientResult<T> result)
            where T : class
        {
            return result != null &&
                   result.FailureKind == PosProductImageFailureKind.AuthDenied
                ? result.Code
                : string.Empty;
        }

        private static string PhaseId(string value, string phase)
        {
            var result = (value ?? string.Empty) + "-" + phase;
            if (!PosProductImageIdentityPolicy.IsSafeId(result))
                throw new InvalidDataException("product_image_operation_identity_invalid");
            return result;
        }

        private static PosProductImageUploadMetadata UploadMetadata(
            ProductImageOperationRow operation,
            ProductImageVariant variant)
        {
            var metadata = Metadata(operation, variant);
            return new PosProductImageUploadMetadata(
                metadata.ByteSize,
                metadata.Height,
                metadata.MimeType,
                metadata.Sha256,
                metadata.Width);
        }

        private static ProductImageMetadata Metadata(
            ProductImageOperationRow operation,
            ProductImageVariant variant)
        {
            var main = variant == ProductImageVariant.Main;
            ProductImageMetadata metadata;
            ProductImageValidationResult validation;
            if (!ProductImageMetadata.TryCreate(
                variant,
                ImageContract.WireMimeType,
                main ? operation.MainBytes.GetValueOrDefault() : operation.ThumbBytes.GetValueOrDefault(),
                main ? operation.MainWidth.GetValueOrDefault() : operation.ThumbWidth.GetValueOrDefault(),
                main ? operation.MainHeight.GetValueOrDefault() : operation.ThumbHeight.GetValueOrDefault(),
                main ? operation.MainSha256 : operation.ThumbSha256,
                out metadata,
                out validation))
            {
                throw new InvalidDataException("product_image_operation_metadata_invalid");
            }
            return metadata;
        }

        private static string SafeFailureCode(string code)
        {
            var value = (code ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value.Length > 80) return "image_sync_failure";
            foreach (var character in value)
            {
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= '0' && character <= '9') ||
                      character == '_'))
                {
                    return "image_sync_failure";
                }
            }
            return value;
        }
    }
}
