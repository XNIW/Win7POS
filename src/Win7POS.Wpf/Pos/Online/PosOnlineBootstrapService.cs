using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosOnlineBootstrapService
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly FileLogger _logger;
        private readonly PosOnlineSyncSupervisorHost _syncHost;
        private readonly PosTrustedDeviceStore _trustedDeviceStore;

        public PosOnlineBootstrapService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore trustedDeviceStore,
            PosOnlineSyncSupervisorHost syncHost)
            : this(
                factory,
                trustedDeviceStore,
                syncHost,
                new FileLogger("PosOnlineBootstrapService"))
        {
        }

        internal PosOnlineBootstrapService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore trustedDeviceStore,
            PosOnlineSyncSupervisorHost syncHost,
            FileLogger logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _trustedDeviceStore = trustedDeviceStore ?? throw new ArgumentNullException(nameof(trustedDeviceStore));
            _syncHost = syncHost ?? throw new ArgumentNullException(nameof(syncHost));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<PosOnlineBootstrapResult> BootstrapAsync(
            PosAdminWebOptions options,
            PosFirstLoginRequest request,
            string localCredential,
            CancellationToken cancellationToken,
            IProgress<PosCatalogPullProgress> progress = null)
        {
            if (options == null)
            {
                return PosOnlineBootstrapResult.Failure(
                    "invalid_options",
                    PosLocalization.T("onlineFirstLogin.invalidOptions"),
                    false);
            }

            if (request == null || string.IsNullOrWhiteSpace(localCredential))
            {
                return PosOnlineBootstrapResult.Failure(
                    "validation_failed",
                    PosLocalization.T("onlineFirstLogin.missingCredentials"),
                    false);
            }

            var currentStage = "request_build";
            var firstLoginSucceeded = false;
            var trustedSessionPersisted = false;
            var catalogStarted = false;
            string clientRequestId = null;
            string serverRequestId = null;
            string cfRay = null;
            int? httpStatus = null;
            var requestReachedServer = false;

            try
            {
                currentStage = "trusted_session_persistence";
                var authenticatedTransition = await _syncHost
                    .BeginAuthenticatedTrustTransitionAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (authenticatedTransition == null)
                {
                    return PosOnlineBootstrapResult.Failure(
                        "sync_maintenance_active",
                        PosLocalization.T("onlineFirstLogin.localRequestError"),
                        false);
                }

                PosOnlineResult<PosFirstLoginResponse> result;
                using (var client = new PosAdminWebClient(options))
                {
                    currentStage = "server_response";
                    result = await client.FirstLoginAsync(request, cancellationToken).ConfigureAwait(false);
                }

                clientRequestId = result?.ClientRequestId;
                serverRequestId = result?.ServerRequestId;
                cfRay = result?.CfRay;
                httpStatus = result?.HttpStatus;
                requestReachedServer = result != null && result.RequestReachedServer;

                if (result == null)
                {
                    return PosOnlineBootstrapResult.Failure(
                        "invalid_response",
                        PosLocalization.T("onlineFirstLogin.invalidResponse"),
                        false,
                        failureStage: "invalid_response",
                        rootCode: "invalid_response");
                }

                if (!result.Success || result.Value == null)
                {
                    if (result.Denied ||
                        SharedAuthStopPolicy.IsAuthenticationDenied(result.Code))
                    {
                        try
                        {
                            await _syncHost.RejectAuthenticatedTrustTransitionAsync(
                                    authenticatedTransition,
                                    "auth_denied")
                                .ConfigureAwait(false);
                        }
                        catch (Exception revokeException)
                        {
                            // The denial remains authoritative, but cleanup is
                            // scoped to this attempt and must never target a newer
                            // generation.
                            _logger.LogWarning(
                                "POS online bootstrap scoped denial cleanup failed.",
                                revokeException);
                        }
                    }
                    _logger.LogWarning(
                        "POS online bootstrap failed: category=online.bootstrap code=" + SafeAuditValue(result.Code) +
                        ", clientRequestId=" + PosTechnicalIdentifier.Redact(result.ClientRequestId) +
                        ", serverRequestId=" + PosTechnicalIdentifier.Redact(result.ServerRequestId) +
                        ", cfRay=" + PosTechnicalIdentifier.Redact(result.CfRay));
                    return CreateFirstLoginFailure(result);
                }

                var response = result.Value;
                currentStage = "first_login_contract";
                PosAuthoritativeReceiptClock authoritativeReceiptClock;
                try
                {
                    authoritativeReceiptClock =
                        _trustedDeviceStore.CaptureOnlineReceiptClock(
                            !string.IsNullOrWhiteSpace(
                                response.EffectiveOfflineAuthorizationExpiresAt));
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogWarning(
                        "POS online bootstrap could not capture the authoritative receipt clock.",
                        ex);
                    return PosOnlineBootstrapResult.Failure(
                        "trusted_time_continuity_lost",
                        PosLocalization.T("onlineFirstLogin.localRequestError"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        failureStage: "trusted_session_persistence",
                        rootCode: "trusted_time_continuity_lost",
                        httpStatus: result.HttpStatus,
                        deviceApprovalState: PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(
                            null,
                            response.Device?.Status),
                        requestReachedServer: result.RequestReachedServer,
                        firstLoginSucceeded: true,
                        exceptionType: SafeExceptionType(ex));
                }
                try
                {
                    ReceiptShopMetadataPolicy.EnsureValidRemoteShop(response?.Shop);
                }
                catch (ReceiptContentValidationException ex)
                {
                    _logger.LogWarning(
                        "POS online bootstrap rejected shop metadata: code=" +
                        SafeAuditValue(ex.Code) + " field=" + SafeAuditValue(ex.Field));
                    return PosOnlineBootstrapResult.Failure(
                        "shop_metadata_invalid",
                        PosLocalization.T("onlineFirstLogin.invalidResponse"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        failureStage: "first_login_contract",
                        rootCode: "shop_metadata_invalid",
                        httpStatus: result.HttpStatus,
                        requestReachedServer: result.RequestReachedServer,
                        exceptionType: SafeExceptionType(ex));
                }
                if (!ValidateFirstLoginResponse(response))
                {
                    _logger.LogWarning("POS online bootstrap invalid first-login response.");
                    return PosOnlineBootstrapResult.Failure(
                        "invalid_response",
                        PosLocalization.T("onlineFirstLogin.invalidResponse"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        failureStage: "invalid_response",
                        rootCode: "invalid_response",
                        httpStatus: result.HttpStatus,
                        requestReachedServer: result.RequestReachedServer);
                }

                firstLoginSucceeded = true;

                var policyCompatibility = PosOnlineCompatibilityValidator.ValidatePolicy(response.Policy);
                if (!string.IsNullOrWhiteSpace(policyCompatibility))
                {
                    _logger.LogWarning(
                        "POS online bootstrap incompatible policy: code=" +
                        SafeAuditValue(policyCompatibility));
                    return PosOnlineBootstrapResult.Failure(
                        policyCompatibility,
                        PosLocalization.T("onlineFirstLogin.invalidResponse"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        failureStage: "first_login_contract",
                        rootCode: policyCompatibility,
                        httpStatus: result.HttpStatus,
                        requestReachedServer: result.RequestReachedServer);
                }

                PosShopTransitionDecision shopTransition;
                currentStage = "shop_transition";
                try
                {
                    PosTrustedDeviceSession trustedSession = null;
                    if (_trustedDeviceStore.TryRead(out var storedSession) &&
                        authenticatedTransition.ExpectedCurrentState.Exists &&
                        authenticatedTransition.ExpectedCurrentState.Active &&
                        PosOnlineSyncSupervisorHost.TryCreateGeneration(
                            storedSession,
                            out var storedGeneration) &&
                        string.Equals(
                            storedGeneration.Fingerprint,
                            authenticatedTransition.ExpectedCurrentState.Fingerprint,
                            StringComparison.Ordinal))
                    {
                        trustedSession = storedSession;
                    }
                    shopTransition = await new PosShopTransitionGuard(_factory)
                        .EvaluateAsync(
                            trustedSession?.ShopId,
                            trustedSession?.ShopCode,
                            response.Shop.ShopId,
                            response.Shop.ShopCode)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("POS online bootstrap local shop transition check failed.", ex);
                    return PosOnlineBootstrapResult.Failure(
                        "local_persistence_failed",
                        PosLocalization.T("onlineFirstLogin.localRequestError"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        failureStage: "shop_transition",
                        rootCode: "local_persistence_failed",
                        httpStatus: result.HttpStatus,
                        deviceApprovalState: PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(
                            null,
                            response.Device?.Status),
                        requestReachedServer: result.RequestReachedServer,
                        firstLoginSucceeded: true,
                        exceptionType: SafeExceptionType(ex));
                }

                if (!shopTransition.Allowed)
                {
                    _logger.LogWarning(
                        "POS online bootstrap shop transition blocked: category=online.bootstrap.shop_transition code=" +
                        SafeAuditValue(shopTransition.Code) +
                        ", unresolvedOutbox=" + BoolText(shopTransition.HasUnresolvedOutbox));
                    return PosOnlineBootstrapResult.Failure(
                        shopTransition.Code,
                        PosLocalization.T("onlineFirstLogin.localRequestError"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        failureStage: "shop_transition",
                        rootCode: shopTransition.Code,
                        httpStatus: result.HttpStatus,
                        deviceApprovalState: PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(
                            null,
                            response.Device?.Status),
                        requestReachedServer: result.RequestReachedServer,
                        firstLoginSucceeded: true);
                }

                progress?.Report(PosCatalogPullProgress.ForPhase("access_verified"));
                currentStage = "trusted_session_persistence";
                var activatedGenerationId = OnlineSyncGeneration.CreateGenerationId();
                if (authenticatedTransition.ExpectedCurrentState.Exists &&
                    authenticatedTransition.ExpectedCurrentState.Active &&
                    _trustedDeviceStore.TryGetReusableGenerationId(
                        response,
                        authenticatedTransition.ExpectedCurrentState.Fingerprint,
                        out var reusableGenerationId))
                {
                    // An exact response retry (for example after a lost local
                    // acknowledgement) must retain the original generation so
                    // the process monotonic high-water cannot be reset.
                    activatedGenerationId = reusableGenerationId;
                }
                try
                {
                    var generation = await _syncHost.ActivateAuthenticatedTrustAsync(
                            response,
                            activatedGenerationId,
                            authoritativeReceiptClock,
                            authenticatedTransition,
                            async () =>
                            {
                                if (!shopTransition.RequiresCatalogReset)
                                    return null;
                                return await new PosShopTransitionGuard(_factory)
                                    .ApplyAuthorizedTransitionAndHoldAsync(shopTransition)
                                    .ConfigureAwait(false);
                            },
                            async activatedGeneration =>
                            {
                                await PosOnlineShopSnapshot.SaveAsync(
                                    _factory,
                                    response.Shop,
                                    activatedGeneration).ConfigureAwait(false);
                                await PosOnlinePolicySnapshot.SaveAsync(
                                    _factory,
                                    response.Policy,
                                    activatedGeneration).ConfigureAwait(false);
                                progress?.Report(PosCatalogPullProgress.ForPhase("device_linked"));

                                var users = new UserRepository(_factory);
                                await users.UpsertRemoteStaffMirrorAsync(
                                    new RemoteStaffMirrorInput
                                    {
                                        Credential = localCredential,
                                        CredentialVersion = response.Staff.CredentialVersion,
                                        DisplayName = response.Staff.DisplayName,
                                        RemoteRoleKey = response.Staff.RoleKey,
                                        RemoteShopId = response.Shop.ShopId,
                                        RemoteStaffId = response.Staff.StaffId,
                                        ShopCode = response.Shop.ShopCode,
                                        StaffCode = response.Staff.StaffCode
                                    }).ConfigureAwait(false);
                                progress?.Report(PosCatalogPullProgress.ForPhase("operator_configured"));
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (generation == null)
                    {
                        _logger.LogWarning(
                            "POS online bootstrap superseded before local activation.");
                        return PosOnlineBootstrapResult.Failure(
                            "authentication_superseded",
                            PosLocalization.T("onlineFirstLogin.localRequestError"),
                            false,
                            result.ClientRequestId,
                            result.ServerRequestId,
                            result.CfRay,
                            failureStage: "trusted_session_persistence",
                            rootCode: "authentication_superseded",
                            httpStatus: result.HttpStatus,
                            deviceApprovalState: PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(
                                null,
                                response.Device?.Status),
                            requestReachedServer: result.RequestReachedServer,
                            firstLoginSucceeded: true);
                    }
                    if (!string.Equals(
                        generation.GenerationId,
                        activatedGenerationId,
                        StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The authenticated sync generation identity changed.");
                    }
                    trustedSessionPersisted = true;
                }
                catch (Exception ex)
                {
                    // The host already rolls back only this activation. A second
                    // exact-ID clear is safe and covers transient file failures
                    // without touching a newer successful login.
                    _trustedDeviceStore.TryClear(activatedGenerationId);
                    _logger.LogWarning("POS online bootstrap local trust/mirror persistence failed.", ex);
                    return PosOnlineBootstrapResult.Failure(
                        "local_persistence_failed",
                        PosLocalization.T("onlineFirstLogin.localRequestError"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        failureStage: "trusted_session_persistence",
                        rootCode: "local_persistence_failed",
                        httpStatus: result.HttpStatus,
                        deviceApprovalState: PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(
                            null,
                            response.Device?.Status),
                        requestReachedServer: result.RequestReachedServer,
                        firstLoginSucceeded: true,
                        exceptionType: SafeExceptionType(ex));
                }

                _logger.LogInfo(
                    "POS online bootstrap success: category=online.bootstrap clientRequestId=" +
                    PosTechnicalIdentifier.Redact(result.ClientRequestId) +
                    ", serverRequestId=" + PosTechnicalIdentifier.Redact(result.ServerRequestId) +
                    ", shopCodePresent=" + BoolText(!string.IsNullOrWhiteSpace(response.Shop.ShopCode)) +
                    ", staffCodePresent=" + BoolText(!string.IsNullOrWhiteSpace(response.Staff.StaffCode)) +
                    ", role_key=" + SafeAuditValue(response.Staff.RoleKey));

                var security = new SecurityRepository(_factory);
                await security.LogEventAsync(
                    SecurityEventCodes.PosOnlineBootstrap,
                    "shop_code_present=" + BoolText(!string.IsNullOrWhiteSpace(response.Shop.ShopCode)) +
                    ", staff_code_present=" + BoolText(!string.IsNullOrWhiteSpace(response.Staff.StaffCode)) +
                    ", role_key=" + SafeAuditValue(response.Staff.RoleKey) +
                    ", remote_staff_id_present=" + BoolText(!string.IsNullOrWhiteSpace(response.Staff.StaffId)))
                    .ConfigureAwait(false);

                try
                {
                    currentStage = "session_creation";
                    var salesDrain = await _syncHost
                        .TriggerAsync(
                            OnlineSyncLane.SalesOutbox,
                            OnlineSyncLaneTrigger.StartOfDay,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (salesDrain.AuthenticationDenied)
                    {
                        _logger.LogWarning(
                            "Bootstrap stopped after sales sync authorization denial: category=online.bootstrap.sales code=auth_denied");
                        return PosOnlineBootstrapResult.Failure(
                            "auth_denied",
                            PosLocalization.T("onlineFirstLogin.authorizationFailed"),
                            true,
                            result.ClientRequestId,
                            result.ServerRequestId,
                            result.CfRay,
                            failureStage: "session_creation",
                            rootCode: "auth_denied",
                            httpStatus: result.HttpStatus,
                            deviceApprovalState: "approved",
                            requestReachedServer: result.RequestReachedServer,
                            firstLoginSucceeded: true,
                            trustedSessionPersisted: true,
                            catalogStarted: false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Bootstrap sales sync skipped.", ex);
                }

                try
                {
                    currentStage = "catalog_start";
                    progress?.Report(PosCatalogPullProgress.ForPhase("catalog"));
                    catalogStarted = true;
                    currentStage = "catalog_pull";
                    var catalogLane = await _syncHost
                        .TriggerAsync(
                            OnlineSyncLane.CatalogDelta,
                            OnlineSyncLaneTrigger.FirstBootstrap,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var catalogSaleSafe = await PosCatalogPullService
                        .IsCatalogSaleSafeAsync(_factory).ConfigureAwait(false);
                    var catalogOutcome = catalogLane.Success &&
                        !catalogLane.CatalogHasMore &&
                        catalogSaleSafe
                        ? PosCatalogPullOutcome.CompletedOk(
                            catalogLane.CatalogPagesProcessed,
                            productsApplied: catalogLane.CatalogRowsApplied,
                            diagnostic: catalogLane.CatalogDiagnostic)
                        : PosCatalogPullOutcome.Failure(
                            catalogLane.Code,
                            catalogLane.AuthenticationDenied,
                            catalogLane.CatalogHasMore,
                            catalogLane.CatalogPagesProcessed,
                            productsApplied: catalogLane.CatalogRowsApplied,
                            diagnostic: catalogLane.CatalogDiagnostic);
                    if (catalogOutcome.Completed && catalogOutcome.CatalogSaleSafe)
                    {
                        progress?.Report(PosCatalogPullProgress.ForPhase("finalizing"));
                        return PosOnlineBootstrapResult.Ok(
                            catalogOutcome,
                            result.ClientRequestId,
                            result.ServerRequestId,
                            result.CfRay,
                            result.HttpStatus,
                            PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(
                                null,
                                response.Device?.Status));
                    }

                    if (!catalogOutcome.Completed)
                    {
                        _logger.LogWarning(
                            "Bootstrap catalog pull incomplete: category=online.bootstrap.catalog code=" +
                            SafeAuditValue(catalogOutcome.StatusCode) +
                            ", pages=" + catalogOutcome.PagesProcessed.ToString() +
                            ", hasMore=" + catalogOutcome.HasMore.ToString() +
                            ", authDenied=" + catalogOutcome.AuthDenied.ToString() +
                            ", stage=" + SafeAuditValue(catalogOutcome.Diagnostic?.Stage) +
                            ", httpStatus=" + (catalogOutcome.Diagnostic?.HttpStatus?.ToString() ?? "none") +
                            ", incidentId=" + SafeAuditValue(catalogOutcome.Diagnostic?.LocalIncidentId));
                    }

                    return PosOnlineBootstrapResult.CatalogIncomplete(
                        catalogOutcome.StatusCode,
                        catalogOutcome.AuthDenied
                            ? PosLocalization.T("onlineFirstLogin.catalogAuthDenied")
                            : PosLocalization.T("onlineFirstLogin.catalogIncomplete"),
                        catalogOutcome.AuthDenied,
                        CatalogRetryPolicy.ShouldOfferManualRetry(
                            catalogOutcome.StatusCode,
                            catalogOutcome.AuthDenied),
                        catalogOutcome,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        result.HttpStatus,
                        result.RequestReachedServer);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Bootstrap catalog pull incomplete.", ex);
                    return PosOnlineBootstrapResult.CatalogIncomplete(
                        "catalog_exception",
                        PosLocalization.T("onlineFirstLogin.catalogIncomplete"),
                        false,
                        true,
                        null,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay,
                        result.HttpStatus,
                        result.RequestReachedServer);
                }
            }
            catch (OperationCanceledException)
            {
                return PosOnlineBootstrapResult.Failure(
                    "timeout",
                    PosLocalization.T("onlineFirstLogin.timeout"),
                    false,
                    clientRequestId,
                    serverRequestId,
                    cfRay,
                    failureStage: currentStage == "catalog_pull" ? "timeout" : currentStage,
                    rootCode: "timeout",
                    httpStatus: httpStatus,
                    retryable: true,
                    exceptionType: "OperationCanceledException",
                    requestReachedServer: requestReachedServer,
                    firstLoginSucceeded: firstLoginSucceeded,
                    trustedSessionPersisted: trustedSessionPersisted,
                    catalogStarted: catalogStarted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("POS online bootstrap non completato.", ex);
                return PosOnlineBootstrapResult.Failure(
                    "unexpected",
                    PosLocalization.T("onlineFirstLogin.connectionFailed"),
                    false,
                    clientRequestId,
                    serverRequestId,
                    cfRay,
                    failureStage: currentStage,
                    rootCode: "unexpected",
                    httpStatus: httpStatus,
                    exceptionType: SafeExceptionType(ex),
                    requestReachedServer: requestReachedServer,
                    firstLoginSucceeded: firstLoginSucceeded,
                    trustedSessionPersisted: trustedSessionPersisted,
                    catalogStarted: catalogStarted);
            }
        }

        private static PosOnlineBootstrapResult CreateFirstLoginFailure(
            PosOnlineResult<PosFirstLoginResponse> result)
        {
            var rootCode = PosBootstrapDiagnosticsPolicy.GetRootCode(
                result?.Code,
                result?.HttpStatus);
            var authenticationDenied = result != null &&
                (result.Denied || SharedAuthStopPolicy.IsAuthenticationDenied(result.Code));
            var stage = PosBootstrapDiagnosticsPolicy.GetFailureStage(
                rootCode,
                result?.HttpStatus,
                result != null && result.RequestReachedServer);
            var diagnostic = new PosRuntimeDiagnostic(
                "online.bootstrap",
                stage,
                rootCode,
                result?.HttpStatus,
                result != null && result.Retryable,
                authenticationDenied,
                1,
                null,
                0,
                0,
                0,
                false,
                false,
                result?.ClientRequestId,
                result?.ServerRequestId,
                result?.CfRay,
                PosRuntimeDiagnostic.CreateLocalIncidentId(),
                DateTimeOffset.UtcNow,
                result?.ElapsedMilliseconds ?? 0,
                result?.ExceptionType,
                "first_login_failed");
            return PosOnlineBootstrapResult.Failure(
                result?.Code,
                LocalizeOnlineResultMessage(result),
                authenticationDenied,
                result?.ClientRequestId,
                result?.ServerRequestId,
                result?.CfRay,
                diagnostic,
                stage,
                rootCode,
                result?.HttpStatus,
                result != null && result.Retryable,
                PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(rootCode, null),
                result?.ExceptionType,
                result != null && result.RequestReachedServer);
        }

        private static string LocalizeOnlineResultMessage<TResponse>(PosOnlineResult<TResponse> result)
            where TResponse : class
        {
            if (result == null)
            {
                return PosLocalization.T("onlineFirstLogin.connectionFailed");
            }

            if (result.Denied)
            {
                return PosLocalization.T("onlineFirstLogin.authorizationFailed");
            }

            switch ((result.Code ?? string.Empty).Trim())
            {
                case "response_too_large":
                    return PosLocalization.T("onlineFirstLogin.responseTooLarge");
                case "invalid_response":
                    return PosLocalization.T("onlineFirstLogin.invalidResponse");
                case "timeout":
                    return PosLocalization.T("onlineFirstLogin.timeout");
                case "network_error":
                    return PosLocalization.T("onlineFirstLogin.networkError");
                case "io_error":
                    return PosLocalization.T("onlineFirstLogin.localRequestError");
                case "invalid_operation":
                    return PosLocalization.T("onlineFirstLogin.invalidOptions");
                default:
                    return PosLocalization.T("onlineFirstLogin.connectionFailed");
            }
        }

        private static string SafeAuditValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();

            if (normalized.Length > 80)
            {
                return normalized.Substring(0, 80);
            }

            return normalized;
        }

        private static string SafeExceptionType(Exception exception)
        {
            if (exception == null)
            {
                return string.Empty;
            }

            var name = exception.GetType().Name ?? string.Empty;
            var builder = new System.Text.StringBuilder(Math.Min(name.Length, 120));
            foreach (var character in name)
            {
                if (builder.Length >= 120)
                {
                    break;
                }

                if (char.IsLetterOrDigit(character) || character == '.' || character == '_')
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        private static string BoolText(bool value)
        {
            return value ? "yes" : "no";
        }

        private static bool ValidateFirstLoginResponse(PosFirstLoginResponse response)
        {
            return response != null &&
                   response.Ok &&
                   !string.IsNullOrWhiteSpace(response.TrustedDeviceToken) &&
                   !string.IsNullOrWhiteSpace(response.ServerTime) &&
                   response.Session != null &&
                   !string.IsNullOrWhiteSpace(response.Session.ExpiresAt) &&
                   !string.IsNullOrWhiteSpace(response.Session.SessionToken) &&
                   !string.IsNullOrWhiteSpace(response.Session.PosSessionId) &&
                   response.Device != null &&
                   response.Device.Trusted &&
                   string.Equals(response.Device.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(response.Device.ShopDeviceId) &&
                   response.Policy != null &&
                   !string.IsNullOrWhiteSpace(response.Policy.ContractVersion) &&
                   response.Staff != null &&
                   !string.IsNullOrWhiteSpace(response.Staff.StaffId) &&
                   !string.IsNullOrWhiteSpace(response.Staff.StaffCode) &&
                   response.Shop != null &&
                   !string.IsNullOrWhiteSpace(response.Shop.ShopId) &&
                   !string.IsNullOrWhiteSpace(response.Shop.ShopCode);
        }
    }

    public sealed class PosOnlineBootstrapResult
    {
        private PosOnlineBootstrapResult(
            bool success,
            string code,
            string message,
            bool denied,
            bool catalogCompleted,
            bool catalogSaleSafe,
            string catalogStatus,
            string catalogLastError,
            bool canOpenPos,
            bool requiresRetry,
            string clientRequestId,
            string serverRequestId,
            string cfRay,
            PosRuntimeDiagnostic diagnostic,
            string failureStage,
            string rootCode,
            int? httpStatus,
            int? firstLoginHttpStatus,
            bool authenticationDenied,
            bool retryable,
            string deviceApprovalState,
            string exceptionType,
            bool requestReachedServer,
            bool firstLoginSucceeded,
            bool trustedSessionPersisted,
            bool catalogStarted)
        {
            CanOpenPos = canOpenPos;
            CatalogCompleted = catalogCompleted;
            CatalogLastError = catalogLastError ?? string.Empty;
            CatalogSaleSafe = catalogSaleSafe;
            CatalogStatus = catalogStatus ?? string.Empty;
            CfRay = cfRay ?? string.Empty;
            ClientRequestId = clientRequestId ?? string.Empty;
            Code = code;
            Denied = denied;
            Message = message;
            RequiresRetry = requiresRetry;
            ServerRequestId = serverRequestId ?? string.Empty;
            Success = success;
            Diagnostic = diagnostic;
            FailureStage = string.IsNullOrWhiteSpace(failureStage) ? "completed" : failureStage;
            RootCode = string.IsNullOrWhiteSpace(rootCode) ? "unknown" : rootCode;
            HttpStatus = httpStatus.HasValue && httpStatus.Value >= 100 && httpStatus.Value <= 599
                ? httpStatus
                : null;
            FirstLoginHttpStatus = firstLoginHttpStatus.HasValue &&
                firstLoginHttpStatus.Value >= 100 &&
                firstLoginHttpStatus.Value <= 599
                ? firstLoginHttpStatus
                : null;
            AuthenticationDenied = authenticationDenied;
            Retryable = retryable && !authenticationDenied;
            DeviceApprovalState = string.IsNullOrWhiteSpace(deviceApprovalState)
                ? "unknown"
                : deviceApprovalState;
            ExceptionType = exceptionType ?? string.Empty;
            RequestReachedServer = requestReachedServer;
            FirstLoginSucceeded = firstLoginSucceeded;
            TrustedSessionPersisted = trustedSessionPersisted;
            CatalogStarted = catalogStarted;
        }

        public bool CanOpenPos { get; }
        public bool CatalogCompleted { get; }
        public string CatalogLastError { get; }
        public bool CatalogSaleSafe { get; }
        public string CatalogStatus { get; }
        public string CfRay { get; }
        public string ClientRequestId { get; }
        public string Code { get; }
        public bool Denied { get; }
        public PosRuntimeDiagnostic Diagnostic { get; }
        public string Message { get; }
        public bool RequiresRetry { get; }
        public string ServerRequestId { get; }
        public bool Success { get; }
        public string FailureStage { get; }
        public string RootCode { get; }
        public int? HttpStatus { get; }
        public int? FirstLoginHttpStatus { get; }
        public bool AuthenticationDenied { get; }
        public bool Retryable { get; }
        public string DeviceApprovalState { get; }
        public string ExceptionType { get; }
        public bool RequestReachedServer { get; }
        public bool FirstLoginSucceeded { get; }
        public bool TrustedSessionPersisted { get; }
        public bool CatalogStarted { get; }

        public static PosOnlineBootstrapResult Ok(
            PosCatalogPullOutcome catalogOutcome,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null,
            int? httpStatus = null,
            string deviceApprovalState = "approved")
        {
            return new PosOnlineBootstrapResult(
                true,
                "success",
                string.Empty,
                false,
                catalogOutcome != null && catalogOutcome.Completed,
                catalogOutcome != null && catalogOutcome.CatalogSaleSafe,
                catalogOutcome?.StatusCode ?? "completed",
                string.Empty,
                true,
                false,
                clientRequestId,
                serverRequestId,
                cfRay,
                catalogOutcome?.Diagnostic,
                "completed",
                "success",
                httpStatus,
                httpStatus,
                false,
                false,
                deviceApprovalState,
                string.Empty,
                true,
                true,
                true,
                true);
        }

        public static PosOnlineBootstrapResult Failure(
            string code,
            string message,
            bool denied,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null,
            PosRuntimeDiagnostic diagnostic = null,
            string failureStage = null,
            string rootCode = null,
            int? httpStatus = null,
            bool retryable = false,
            string deviceApprovalState = null,
            string exceptionType = null,
            bool requestReachedServer = false,
            bool firstLoginSucceeded = false,
            bool trustedSessionPersisted = false,
            bool catalogStarted = false)
        {
            var resolvedCode = string.IsNullOrWhiteSpace(code) ? "unknown" : code;
            var resolvedRootCode = PosBootstrapDiagnosticsPolicy.GetRootCode(rootCode ?? resolvedCode, httpStatus);
            var resolvedAuthenticationDenied = denied;
            return new PosOnlineBootstrapResult(
                false,
                resolvedCode,
                string.IsNullOrWhiteSpace(message)
                    ? PosLocalization.T("onlineFirstLogin.connectionFailed")
                    : message,
                denied,
                false,
                false,
                resolvedCode,
                resolvedCode,
                false,
                false,
                clientRequestId,
                serverRequestId,
                cfRay,
                diagnostic,
                failureStage ?? PosBootstrapDiagnosticsPolicy.GetFailureStage(
                    resolvedRootCode,
                    httpStatus,
                    requestReachedServer),
                resolvedRootCode,
                httpStatus,
                httpStatus,
                resolvedAuthenticationDenied,
                retryable || PosBootstrapDiagnosticsPolicy.IsRetryable(
                    resolvedRootCode,
                    httpStatus,
                    resolvedAuthenticationDenied),
                deviceApprovalState ?? PosBootstrapDiagnosticsPolicy.GetDeviceApprovalState(
                    resolvedRootCode,
                    null),
                exceptionType,
                requestReachedServer,
                firstLoginSucceeded,
                trustedSessionPersisted,
                catalogStarted);
        }

        public static PosOnlineBootstrapResult CatalogIncomplete(
            string code,
            string message,
            bool denied,
            bool requiresRetry,
            PosCatalogPullOutcome catalogOutcome,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null,
            int? httpStatus = null,
            bool requestReachedServer = true)
        {
            var status = string.IsNullOrWhiteSpace(catalogOutcome?.StatusCode)
                ? (string.IsNullOrWhiteSpace(code) ? "catalog_incomplete" : code)
                : catalogOutcome.StatusCode;
            var effectiveHttpStatus = catalogOutcome?.Diagnostic?.HttpStatus ?? httpStatus;
            var effectiveRequestReachedServer = requestReachedServer ||
                catalogOutcome?.Diagnostic?.HttpStatus.HasValue == true;

            return new PosOnlineBootstrapResult(
                !denied,
                string.IsNullOrWhiteSpace(code) ? status : code,
                string.IsNullOrWhiteSpace(message)
                    ? PosLocalization.T("onlineFirstLogin.catalogIncomplete")
                    : message,
                denied,
                catalogOutcome != null && catalogOutcome.Completed,
                catalogOutcome != null && catalogOutcome.CatalogSaleSafe,
                status,
                status,
                false,
                requiresRetry && !denied,
                catalogOutcome?.Diagnostic?.ClientRequestId ?? clientRequestId,
                catalogOutcome?.Diagnostic?.ServerRequestId ?? serverRequestId,
                catalogOutcome?.Diagnostic?.CfRay ?? cfRay,
                catalogOutcome?.Diagnostic,
                "catalog_pull",
                PosBootstrapDiagnosticsPolicy.GetRootCode(code ?? status, effectiveHttpStatus),
                effectiveHttpStatus,
                httpStatus,
                denied,
                requiresRetry,
                "approved",
                catalogOutcome?.Diagnostic?.ExceptionType ?? string.Empty,
                effectiveRequestReachedServer,
                true,
                true,
                true);
        }
    }
}
