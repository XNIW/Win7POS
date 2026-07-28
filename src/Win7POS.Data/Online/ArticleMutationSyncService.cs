using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    /// <summary>
    /// Drains one bounded article-mutation batch. The supervisor owns single-flight
    /// execution; this service owns durable claims and whole-response validation.
    /// Trusted credentials exist only in the send-time envelope.
    /// </summary>
    public sealed class ArticleMutationSyncService
    {
        private static readonly string ProcessClaimOwnerId =
            "process-" + Guid.NewGuid().ToString("N");
        private readonly ArticleMutationOutboxRepository _outbox;
        private readonly Func<
            PosAdminWebOptions,
            PosArticleMutationEnvelope,
            CancellationToken,
            Task<PosOnlineResult<PosArticleMutationResponse>>> _sender;

        public ArticleMutationSyncService(SqliteConnectionFactory factory)
            : this(factory, null)
        {
        }

        internal ArticleMutationSyncService(
            SqliteConnectionFactory factory,
            Func<
                PosAdminWebOptions,
                PosArticleMutationEnvelope,
                CancellationToken,
                Task<PosOnlineResult<PosArticleMutationResponse>>> sender)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _outbox = new ArticleMutationOutboxRepository(factory);
            _sender = sender;
        }

        public async Task<ArticleMutationSyncResult> SyncPendingAsync(
            PosAdminWebOptions options,
            PosTrustedDeviceSession trustedSession,
            OnlineSyncLaneExecutionContext executionContext,
            string appVersion,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (trustedSession == null)
                throw new ArgumentNullException(nameof(trustedSession));
            if (executionContext == null)
                throw new ArgumentNullException(nameof(executionContext));

            // The trusted sync generation can survive a process restart. The
            // ephemeral process owner distinguishes an interrupted prior
            // process from an active same-process sender.
            await _outbox.RecoverInterruptedAsync(
                    TimeSpan.Zero,
                    ProcessClaimOwnerId)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var claim = await _outbox.ClaimBatchAsync(
                executionContext.Generation.GenerationId,
                PosArticleMutationContract.MaximumBatchCount,
                ProcessClaimOwnerId).ConfigureAwait(false);
            if (claim.Requests.Count == 0)
            {
                return await BuildEmptyResultAsync().ConfigureAwait(false);
            }

            PosOnlineResult<PosArticleMutationResponse> response;
            try
            {
                response = await executionContext.ExecuteCredentialedRequestAsync(
                    (credentials, token) => SendAsync(
                        options,
                            CreateEnvelope(
                                trustedSession,
                                credentials,
                                appVersion,
                                claim.Requests),
                        token),
                    AuthenticationDenialCode,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _outbox.ReleaseClaimForTransportFailureAsync(
                    claim,
                    "article_mutation_client_interrupted",
                    authenticationDenied: false).ConfigureAwait(false);
                throw;
            }

            var responseCode = NormalizeCode(response?.Code) ??
                "article_mutation_transport_failure";
            var authenticationDenied = response != null &&
                (response.Denied ||
                 response.HttpStatus == 401 ||
                 response.HttpStatus == 403);
            if (response == null || !response.Success || response.Value == null)
            {
                await _outbox.ReleaseClaimForTransportFailureAsync(
                    claim,
                    responseCode,
                    authenticationDenied).ConfigureAwait(false);
                return await BuildTransportFailureResultAsync(
                    claim.Requests.Count,
                    responseCode,
                    authenticationDenied,
                    response?.Retryable == true,
                    response?.Code).ConfigureAwait(false);
            }

            var knownAttempts = await _outbox.GetKnownAttemptTokensAsync(
                claim.Requests.Select(item => item.Intent.MutationId))
                .ConfigureAwait(false);
            var validation = PosArticleMutationResponseValidator.Validate(
                response.Value,
                claim.Requests,
                (mutationId, attemptToken) =>
                {
                    ISet<string> tokens;
                    return knownAttempts.TryGetValue(mutationId, out tokens) &&
                        tokens.Contains(attemptToken);
                });
            if (!validation.IsValid)
            {
                await _outbox.ReleaseClaimForTransportFailureAsync(
                    claim,
                    validation.Code,
                    authenticationDenied: false).ConfigureAwait(false);
                return await BuildTransportFailureResultAsync(
                    claim.Requests.Count,
                    validation.Code,
                    authenticationDenied: false,
                    retryable: true,
                    transportCode: null).ConfigureAwait(false);
            }

            var acked = 0;
            var retried = 0;
            var blocked = 0;
            var failedAuth = false;
            foreach (var item in validation.ResultsByMutationId.Values)
            {
                switch (PosArticleMutationStatusPolicy.Classify(
                    item.DeliveryStatus))
                {
                    case PosArticleMutationLocalDisposition.Completed:
                        acked++;
                        break;
                    case PosArticleMutationLocalDisposition.RetryWait:
                        retried++;
                        break;
                    case PosArticleMutationLocalDisposition.FailedBlocked:
                        blocked++;
                        break;
                    case PosArticleMutationLocalDisposition.AuthStop:
                        retried++;
                        failedAuth = true;
                        break;
                }
            }

            if (failedAuth)
            {
                // ExecuteCredentialedRequestAsync normally stops the generation,
                // which releases every owned claim before returning. The explicit
                // release also covers injectable/test hosts whose auth-stop callback
                // does not own durable generation state; it is a fenced no-op after
                // the real generation release.
                await _outbox.ReleaseClaimForTransportFailureAsync(
                    claim,
                    PosArticleMutationStatusPolicy.FailedAuth,
                    authenticationDenied: true).ConfigureAwait(false);
                var stoppedState = await _outbox.GetDrainStateAsync()
                    .ConfigureAwait(false);
                return new ArticleMutationSyncResult(
                    claim.Requests.Count,
                    0,
                    claim.Requests.Count,
                    0,
                    stoppedState.RemainingDue,
                    stoppedState.NextRetryAt,
                    SyncFailureKind.AuthenticationDenied,
                    PosArticleMutationStatusPolicy.FailedAuth,
                    requestCatalogNow: false);
            }

            await _outbox.ApplyValidatedResponseAsync(claim, validation)
                .ConfigureAwait(false);
            var drainState = await _outbox.GetDrainStateAsync()
                .ConfigureAwait(false);
            var resultCode = failedAuth
                ? PosArticleMutationStatusPolicy.FailedAuth
                : blocked > 0
                    ? "article_mutation_blocked"
                    : retried > 0
                        ? PosArticleMutationStatusPolicy.RetryableUpstream
                        : "success";
            return new ArticleMutationSyncResult(
                claim.Requests.Count,
                acked,
                retried,
                blocked,
                drainState.RemainingDue,
                drainState.NextRetryAt,
                failedAuth
                    ? SyncFailureKind.AuthenticationDenied
                    : blocked > 0
                        ? SyncFailureKind.PermanentRemote
                        : retried > 0
                            ? SyncFailureKind.RetryableRemote
                            : SyncFailureKind.None,
                resultCode,
                requestCatalogNow: acked > 0 || blocked > 0);
        }

        private async Task<ArticleMutationSyncResult> BuildEmptyResultAsync()
        {
            var state = await _outbox.GetDrainStateAsync().ConfigureAwait(false);
            return new ArticleMutationSyncResult(
                0,
                0,
                0,
                0,
                state.RemainingDue,
                state.NextRetryAt,
                SyncFailureKind.None,
                "success",
                requestCatalogNow: false);
        }

        private async Task<ArticleMutationSyncResult>
            BuildTransportFailureResultAsync(
                int attempted,
                string code,
                bool authenticationDenied,
                bool retryable,
                string transportCode)
        {
            var state = await _outbox.GetDrainStateAsync().ConfigureAwait(false);
            return new ArticleMutationSyncResult(
                attempted,
                0,
                attempted,
                0,
                state.RemainingDue,
                state.NextRetryAt,
                authenticationDenied
                    ? SyncFailureKind.AuthenticationDenied
                    : IsOffline(transportCode)
                        ? SyncFailureKind.Network
                        : string.Equals(
                            transportCode,
                            "timeout",
                            StringComparison.OrdinalIgnoreCase)
                            ? SyncFailureKind.Timeout
                            : retryable
                                ? SyncFailureKind.RetryableRemote
                                : SyncFailureKind.Unexpected,
                code,
                requestCatalogNow: false);
        }

        private static PosArticleMutationEnvelope CreateEnvelope(
            PosTrustedDeviceSession session,
            OnlineSyncRequestCredentials credentials,
            string appVersion,
            IReadOnlyList<PosArticleMutationRequest> requests)
        {
            return new PosArticleMutationEnvelope
            {
                AppVersion = string.IsNullOrWhiteSpace(appVersion)
                    ? "0.0.0.0"
                    : appVersion.Trim(),
                ShopId = session.ShopId,
                ShopDeviceId = credentials.ShopDeviceId,
                StaffId = session.StaffId,
                StaffCredentialVersion = session.StaffCredentialVersion,
                PosSessionId = credentials.PosSessionId,
                DeviceToken = credentials.DeviceToken,
                SessionToken = credentials.SessionToken,
                Mutations = requests
            };
        }

        private async Task<PosOnlineResult<PosArticleMutationResponse>> SendAsync(
            PosAdminWebOptions options,
            PosArticleMutationEnvelope envelope,
            CancellationToken cancellationToken)
        {
            if (_sender != null)
                return await _sender(options, envelope, cancellationToken)
                    .ConfigureAwait(false);
            using (var client = new PosAdminWebClient(options))
            {
                return await client.ArticleMutationsAsync(
                    envelope,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static string AuthenticationDenialCode(
            PosOnlineResult<PosArticleMutationResponse> response)
        {
            if (response == null) return string.Empty;
            if (response.Denied ||
                response.HttpStatus == 401 ||
                response.HttpStatus == 403)
            {
                return NormalizeCode(response.Code) ?? "failed_auth";
            }
            if (response.Value?.Results != null &&
                response.Value.Results.Any(result =>
                    string.Equals(
                        result?.DeliveryStatus,
                        PosArticleMutationStatusPolicy.FailedAuth,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        result?.Ack?.Code,
                        PosArticleMutationStatusPolicy.FailedAuth,
                        StringComparison.Ordinal)))
            {
                return PosArticleMutationStatusPolicy.FailedAuth;
            }
            return string.Empty;
        }

        private static bool IsOffline(string code)
        {
            switch ((code ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "dns":
                case "network_error":
                case "tls":
                case "io_error":
                    return true;
                default:
                    return false;
            }
        }

        private static string NormalizeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            return normalized.Length == 0 ? null : normalized;
        }
    }

    public sealed class ArticleMutationSyncResult
    {
        public ArticleMutationSyncResult(
            int attempted,
            int acked,
            int retried,
            int blocked,
            long remainingDue,
            long? nextRetryAt,
            SyncFailureKind failureKind,
            string diagnosticCode,
            bool requestCatalogNow)
        {
            Attempted = attempted;
            Acked = acked;
            Retried = retried;
            Blocked = blocked;
            RemainingDue = remainingDue;
            NextRetryAt = nextRetryAt;
            FailureKind = failureKind;
            DiagnosticCode = diagnosticCode ?? string.Empty;
            RequestCatalogNow = requestCatalogNow;
        }

        public int Acked { get; }
        public int Attempted { get; }
        public bool AuthenticationDenied =>
            FailureKind == SyncFailureKind.AuthenticationDenied;
        public int Blocked { get; }
        public string DiagnosticCode { get; }
        public SyncFailureKind FailureKind { get; }
        public bool HasImmediateMore =>
            RemainingDue > 0 && !AuthenticationDenied;
        public long? NextRetryAt { get; }
        public long RemainingDue { get; }
        public bool RequestCatalogNow { get; }
        public int Retried { get; }
    }
}
