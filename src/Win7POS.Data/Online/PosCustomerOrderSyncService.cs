using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    /// <summary>
    /// Pulls a bounded customer-order batch and drains durable acknowledgements.
    /// Credentials are injected only inside the supervisor's send-time request
    /// scope and never enter the SQLite inbox.
    /// </summary>
    public sealed class PosCustomerOrderSyncService
    {
        private readonly CustomerOrderInboxRepository _inbox;
        private readonly Func<
            PosAdminWebOptions,
            PosCustomerOrderClaimRequest,
            CancellationToken,
            Task<PosOnlineResult<PosCustomerOrderClaimResponse>>> _claimSender;
        private readonly Func<
            PosAdminWebOptions,
            PosCustomerOrderAckRequest,
            CancellationToken,
            Task<PosOnlineResult<PosCustomerOrderAckResponse>>> _ackSender;
        private readonly bool _enabled;

        public PosCustomerOrderSyncService(SqliteConnectionFactory factory)
            : this(
                factory,
                null,
                null,
                PosOnlineContract.CustomerOrderHandoffEnabledByDefault)
        {
        }

        internal PosCustomerOrderSyncService(
            SqliteConnectionFactory factory,
            Func<
                PosAdminWebOptions,
                PosCustomerOrderClaimRequest,
                CancellationToken,
                Task<PosOnlineResult<PosCustomerOrderClaimResponse>>> claimSender,
            Func<
                PosAdminWebOptions,
                PosCustomerOrderAckRequest,
                CancellationToken,
                Task<PosOnlineResult<PosCustomerOrderAckResponse>>> ackSender,
            bool enabled = true)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _inbox = new CustomerOrderInboxRepository(factory);
            _claimSender = claimSender ?? SendClaimAsync;
            _ackSender = ackSender ?? SendAckAsync;
            _enabled = enabled;
        }

        public async Task<OutboxDrainResult> SyncPendingAsync(
            PosAdminWebOptions options,
            OnlineSyncLaneExecutionContext executionContext,
            string appVersion,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (executionContext == null)
                throw new ArgumentNullException(nameof(executionContext));
            if (!_enabled) return OutboxDrainResult.Empty();

            var run = new RunAccumulator();
            if (!await DrainDueAcksAsync(
                    options,
                    executionContext,
                    appVersion,
                    run,
                    cancellationToken).ConfigureAwait(false))
            {
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            var beforeClaim = await _inbox.GetDrainStateAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .ConfigureAwait(false);
            if (beforeClaim.Unresolved >=
                CustomerOrderInboxRepository.MaximumUnresolvedRows)
            {
                run.SetFailure(
                    SyncFailureKind.LocalPersistence,
                    "customer_order_inbox_capacity");
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            var claimRequest = new PosCustomerOrderClaimRequest
            {
                AppVersion = NormalizeVersion(appVersion),
                Limit = PosOnlineContract.CustomerOrderMaximumBatchCount,
                SchemaVersion = PosOnlineContract.CustomerOrderHandoffSchemaVersion
            };
            PosOnlineResult<PosCustomerOrderClaimResponse> claimResult;
            try
            {
                claimResult = await executionContext.ExecuteCredentialedRequestAsync(
                    (credentials, token) =>
                    {
                        ApplyCredentials(claimRequest, credentials);
                        return _claimSender(options, claimRequest, token);
                    },
                    result => AuthenticationDenialCode(result),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OnlineSyncCredentialsChangedException)
            {
                run.SetFailure(
                    SyncFailureKind.AuthenticationDenied,
                    "customer_order_credentials_changed");
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            if (claimResult == null || !claimResult.Success || claimResult.Value == null)
            {
                run.SetFailure(
                    FailureKind(claimResult),
                    FailureCode(claimResult, "customer_order_claim_failed"));
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var validation = PosCustomerOrderHandoffCodec.ValidateClaimResponse(
                claimResult.Value,
                executionContext.Generation,
                claimRequest.Limit,
                nowMs);
            if (validation.Length > 0)
            {
                run.SetFailure(SyncFailureKind.LocalValidation, validation);
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            CustomerOrderInboxPersistResult persisted;
            try
            {
                persisted = await _inbox.PersistClaimAsync(
                    claimResult.Value.Handoffs,
                    executionContext.Generation,
                    nowMs).ConfigureAwait(false);
            }
            catch (OnlineSyncGenerationChangedException)
            {
                throw;
            }
            catch (Exception)
            {
                run.SetFailure(
                    SyncFailureKind.LocalPersistence,
                    "customer_order_inbox_write_failed");
                return await CompleteAsync(run).ConfigureAwait(false);
            }

            run.Received += persisted.Inserted;
            run.Replayed += persisted.Replayed;
            run.RemoteBatchWasFull =
                claimResult.Value.Handoffs.Length == claimRequest.Limit;

            await DrainDueAcksAsync(
                    options,
                    executionContext,
                    appVersion,
                    run,
                    cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(run).ConfigureAwait(false);
        }

        private async Task<bool> DrainDueAcksAsync(
            PosAdminWebOptions options,
            OnlineSyncLaneExecutionContext executionContext,
            string appVersion,
            RunAccumulator run,
            CancellationToken cancellationToken)
        {
            while (run.Attempted < PosOnlineContract.CustomerOrderMaximumBatchCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var item = await _inbox.ClaimNextAckAsync(
                    executionContext.Generation,
                    nowMs).ConfigureAwait(false);
                if (item == null) return true;

                run.Attempted += 1;
                var request = new PosCustomerOrderAckRequest
                {
                    AppVersion = NormalizeVersion(appVersion),
                    ExpectedStatusVersion = item.AckExpectedStatusVersion,
                    HandoffId = item.HandoffId,
                    IdempotencyKey = item.AckIdempotencyKey,
                    LeaseToken = item.LeaseToken,
                    Outcome = item.AckOutcome,
                    PosSaleId = string.IsNullOrWhiteSpace(item.AckPosSaleId)
                        ? null
                        : item.AckPosSaleId,
                    SchemaVersion = PosOnlineContract.CustomerOrderAckSchemaVersion
                };

                PosOnlineResult<PosCustomerOrderAckResponse> result;
                try
                {
                    result = await executionContext.ExecuteCredentialedRequestAsync(
                        (credentials, token) =>
                        {
                            ApplyCredentials(request, credentials);
                            return _ackSender(options, request, token);
                        },
                        response => AuthenticationDenialCode(response),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await _inbox.MarkAttemptFailureAsync(
                        item,
                        executionContext.Generation,
                        "customer_order_ack_cancelled",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        retryable: true,
                        requireFreshLease: false).ConfigureAwait(false);
                    throw;
                }
                catch (OnlineSyncCredentialsChangedException)
                {
                    await _inbox.MarkAttemptFailureAsync(
                        item,
                        executionContext.Generation,
                        "customer_order_credentials_changed",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        retryable: true,
                        requireFreshLease: false).ConfigureAwait(false);
                    run.Retried += 1;
                    run.SetFailure(
                        SyncFailureKind.AuthenticationDenied,
                        "customer_order_credentials_changed");
                    return false;
                }

                if (result == null || !result.Success || result.Value == null)
                {
                    var code = FailureCode(result, "customer_order_ack_failed");
                    var denied = result != null &&
                        (result.Denied ||
                         SharedAuthStopPolicy.IsAuthenticationDenied(code));
                    var requireFreshLease = code == "lease_conflict";
                    var retryable = denied || requireFreshLease ||
                        result?.Retryable == true || IsRetryableCode(code);
                    var transitioned = await _inbox.MarkAttemptFailureAsync(
                        item,
                        executionContext.Generation,
                        code,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        retryable,
                        requireFreshLease).ConfigureAwait(false);
                    if (transitioned)
                    {
                        if (retryable) run.Retried += 1;
                        else run.Blocked += 1;
                    }
                    run.SetFailure(
                        denied
                            ? SyncFailureKind.AuthenticationDenied
                            : FailureKind(result),
                        code);
                    return false;
                }

                var validation = PosCustomerOrderHandoffCodec.ValidateAckResponse(
                    result.Value,
                    item);
                if (validation.Length > 0)
                {
                    var transitioned = await _inbox.MarkAttemptFailureAsync(
                        item,
                        executionContext.Generation,
                        validation,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        retryable: true,
                        requireFreshLease: false).ConfigureAwait(false);
                    if (transitioned) run.Retried += 1;
                    run.SetFailure(SyncFailureKind.LocalValidation, validation);
                    return false;
                }

                if (!await _inbox.MarkAckedAsync(
                        item,
                        result.Value,
                        executionContext.Generation,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ConfigureAwait(false))
                {
                    run.SetFailure(
                        SyncFailureKind.ConcurrentDrain,
                        "customer_order_ack_fence_lost");
                    return false;
                }
                run.Acked += 1;
            }
            return true;
        }

        private async Task<OutboxDrainResult> CompleteAsync(RunAccumulator run)
        {
            var state = await _inbox.GetDrainStateAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .ConfigureAwait(false);
            var remaining = state.DueAcks + (run.RemoteBatchWasFull ? 1 : 0);
            return new OutboxDrainResult(
                run.Attempted,
                run.Acked,
                run.Retried,
                run.Blocked,
                remaining,
                state.NextRetryAt,
                run.FailureKind,
                run.DiagnosticCode);
        }

        private static void ApplyCredentials(
            PosCustomerOrderClaimRequest request,
            OnlineSyncRequestCredentials credentials)
        {
            request.DeviceToken = credentials.DeviceToken;
            request.PosSessionId = credentials.PosSessionId;
            request.SessionToken = credentials.SessionToken;
            request.ShopDeviceId = credentials.ShopDeviceId;
        }

        private static void ApplyCredentials(
            PosCustomerOrderAckRequest request,
            OnlineSyncRequestCredentials credentials)
        {
            request.DeviceToken = credentials.DeviceToken;
            request.PosSessionId = credentials.PosSessionId;
            request.SessionToken = credentials.SessionToken;
            request.ShopDeviceId = credentials.ShopDeviceId;
        }

        private static async Task<PosOnlineResult<PosCustomerOrderClaimResponse>>
            SendClaimAsync(
                PosAdminWebOptions options,
                PosCustomerOrderClaimRequest request,
                CancellationToken cancellationToken)
        {
            using (var client = new PosAdminWebClient(options))
            {
                return await client.CustomerOrderClaimAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static async Task<PosOnlineResult<PosCustomerOrderAckResponse>>
            SendAckAsync(
                PosAdminWebOptions options,
                PosCustomerOrderAckRequest request,
                CancellationToken cancellationToken)
        {
            using (var client = new PosAdminWebClient(options))
            {
                return await client.CustomerOrderAckAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static string AuthenticationDenialCode<T>(PosOnlineResult<T> result)
            where T : class
        {
            if (result == null) return string.Empty;
            var code = FailureCode(result, string.Empty);
            return result.Denied || SharedAuthStopPolicy.IsAuthenticationDenied(code)
                ? code.Length == 0 ? "auth_denied" : code
                : string.Empty;
        }

        private static SyncFailureKind FailureKind<T>(PosOnlineResult<T> result)
            where T : class
        {
            if (result == null) return SyncFailureKind.Unexpected;
            if (result.Denied) return SyncFailureKind.AuthenticationDenied;
            var code = FailureCode(result, string.Empty);
            if (SharedAuthStopPolicy.IsAuthenticationDenied(code))
                return SyncFailureKind.AuthenticationDenied;
            if (code == "timeout") return SyncFailureKind.Timeout;
            if (code == "network_error" || code == "dns" || code == "tls" ||
                code == "io_error")
            {
                return SyncFailureKind.Network;
            }
            return result.Retryable
                ? SyncFailureKind.RetryableRemote
                : SyncFailureKind.PermanentRemote;
        }

        private static string FailureCode<T>(
            PosOnlineResult<T> result,
            string fallback)
            where T : class
        {
            var code = CustomerOrderInboxRepository.NormalizeCode(result?.Code);
            return code == "customer_order_sync_failure"
                ? CustomerOrderInboxRepository.NormalizeCode(fallback)
                : code;
        }

        private static bool IsRetryableCode(string code)
        {
            return code == "timeout" || code == "network_error" || code == "dns" ||
                code == "tls" || code == "io_error" || code == "http_5xx" ||
                code == "db_failure" || code == "not_configured";
        }

        private static string NormalizeVersion(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 || normalized.Length > 64
                ? null
                : normalized;
        }

        private sealed class RunAccumulator
        {
            public int Acked;
            public int Attempted;
            public int Blocked;
            public string DiagnosticCode = string.Empty;
            public SyncFailureKind FailureKind;
            public int Received;
            public bool RemoteBatchWasFull;
            public int Replayed;
            public int Retried;

            public void SetFailure(SyncFailureKind kind, string code)
            {
                if (FailureKind != SyncFailureKind.None) return;
                FailureKind = kind;
                DiagnosticCode = CustomerOrderInboxRepository.NormalizeCode(code);
            }
        }
    }
}
