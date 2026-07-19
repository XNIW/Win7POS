using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
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
        private readonly PosTrustedDeviceStore _trustedDeviceStore;

        public PosOnlineBootstrapService(SqliteConnectionFactory factory)
            : this(factory, new PosTrustedDeviceStore(), new FileLogger("PosOnlineBootstrapService"))
        {
        }

        public PosOnlineBootstrapService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore trustedDeviceStore)
            : this(factory, trustedDeviceStore, new FileLogger("PosOnlineBootstrapService"))
        {
        }

        internal PosOnlineBootstrapService(
            SqliteConnectionFactory factory,
            PosTrustedDeviceStore trustedDeviceStore,
            FileLogger logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _trustedDeviceStore = trustedDeviceStore ?? throw new ArgumentNullException(nameof(trustedDeviceStore));
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

            try
            {
                PosOnlineResult<PosFirstLoginResponse> result;
                using (var client = new PosAdminWebClient(options))
                {
                    result = await client.FirstLoginAsync(request, cancellationToken).ConfigureAwait(false);
                }

                if (!result.Success || result.Value == null)
                {
                    _logger.LogWarning(
                        "POS online bootstrap failed: category=online.bootstrap code=" + SafeAuditValue(result.Code) +
                        ", clientRequestId=" + SafeAuditValue(result.ClientRequestId) +
                        ", serverRequestId=" + SafeAuditValue(result.ServerRequestId) +
                        ", cfRay=" + SafeAuditValue(result.CfRay));
                    return PosOnlineBootstrapResult.Failure(
                        result.Code,
                        LocalizeOnlineResultMessage(result),
                        result.Denied,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay);
                }

                var response = result.Value;
                if (!ValidateFirstLoginResponse(response))
                {
                    _logger.LogWarning("POS online bootstrap invalid first-login response.");
                    return PosOnlineBootstrapResult.Failure(
                        "invalid_response",
                        PosLocalization.T("onlineFirstLogin.invalidResponse"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay);
                }

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
                        result.CfRay);
                }

                PosShopTransitionDecision shopTransition;
                try
                {
                    PosTrustedDeviceSession trustedSession = null;
                    _trustedDeviceStore.TryRead(out trustedSession);
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
                        result.CfRay);
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
                        result.CfRay);
                }

                progress?.Report(PosCatalogPullProgress.ForPhase("access_verified"));
                IDisposable shopTransitionLease = null;
                try
                {
                    if (shopTransition.RequiresCatalogReset)
                    {
                        shopTransitionLease = await new PosShopTransitionGuard(_factory)
                            .ApplyAuthorizedTransitionAndHoldAsync(shopTransition)
                            .ConfigureAwait(false);
                    }

                    await PosOnlineShopSnapshot.SaveAsync(_factory, response.Shop).ConfigureAwait(false);
                    await PosOnlinePolicySnapshot.SaveAsync(_factory, response.Policy).ConfigureAwait(false);
                    _trustedDeviceStore.SaveFirstLogin(response);
                    progress?.Report(PosCatalogPullProgress.ForPhase("device_linked"));

                    var users = new UserRepository(_factory);
                    await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
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
                }
                catch (Exception ex)
                {
                    _trustedDeviceStore.Clear();
                    _logger.LogWarning("POS online bootstrap local trust/mirror persistence failed.", ex);
                    return PosOnlineBootstrapResult.Failure(
                        "local_persistence_failed",
                        PosLocalization.T("onlineFirstLogin.localRequestError"),
                        false,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay);
                }
                finally
                {
                    shopTransitionLease?.Dispose();
                }

                _logger.LogInfo(
                    "POS online bootstrap success: category=online.bootstrap clientRequestId=" +
                    SafeAuditValue(result.ClientRequestId) +
                    ", serverRequestId=" + SafeAuditValue(result.ServerRequestId) +
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
                    var salesSync = new PosSalesSyncService(_factory);
                    var salesDrain = await salesSync
                        .TrySyncPendingAsync(options, cancellationToken)
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
                            result.CfRay);
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
                    var catalogPull = new PosCatalogPullService(_factory);
                    progress?.Report(PosCatalogPullProgress.ForPhase("catalog"));
                    var catalogOutcome = await catalogPull
                        .TryPullInitialCatalogAsync(options, cancellationToken, progress)
                        .ConfigureAwait(false);
                    if (catalogOutcome.Completed && catalogOutcome.CatalogSaleSafe)
                    {
                        progress?.Report(PosCatalogPullProgress.ForPhase("finalizing"));
                        return PosOnlineBootstrapResult.Ok(
                            catalogOutcome,
                            result.ClientRequestId,
                            result.ServerRequestId,
                            result.CfRay);
                    }

                    if (!catalogOutcome.Completed)
                    {
                        _logger.LogWarning(
                            "Bootstrap catalog pull incomplete: category=online.bootstrap.catalog code=" +
                            SafeAuditValue(catalogOutcome.StatusCode) +
                            ", pages=" + catalogOutcome.PagesProcessed.ToString() +
                            ", hasMore=" + catalogOutcome.HasMore.ToString() +
                            ", authDenied=" + catalogOutcome.AuthDenied.ToString());
                    }

                    return PosOnlineBootstrapResult.CatalogIncomplete(
                        catalogOutcome.StatusCode,
                        catalogOutcome.AuthDenied
                            ? PosLocalization.T("onlineFirstLogin.catalogAuthDenied")
                            : PosLocalization.T("onlineFirstLogin.catalogIncomplete"),
                        catalogOutcome.AuthDenied,
                        !catalogOutcome.AuthDenied,
                        catalogOutcome,
                        result.ClientRequestId,
                        result.ServerRequestId,
                        result.CfRay);
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
                        result.CfRay);
                }
            }
            catch (OperationCanceledException)
            {
                return PosOnlineBootstrapResult.Failure(
                    "timeout",
                    PosLocalization.T("onlineFirstLogin.timeout"),
                    false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("POS online bootstrap non completato.", ex);
                return PosOnlineBootstrapResult.Failure(
                    "failure",
                    PosLocalization.T("onlineFirstLogin.connectionFailed"),
                    false);
            }
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
            string cfRay)
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
        public string Message { get; }
        public bool RequiresRetry { get; }
        public string ServerRequestId { get; }
        public bool Success { get; }

        public static PosOnlineBootstrapResult Ok(
            PosCatalogPullOutcome catalogOutcome,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null)
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
                cfRay);
        }

        public static PosOnlineBootstrapResult Failure(
            string code,
            string message,
            bool denied,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null)
        {
            return new PosOnlineBootstrapResult(
                false,
                string.IsNullOrWhiteSpace(code) ? "failure" : code,
                string.IsNullOrWhiteSpace(message)
                    ? PosLocalization.T("onlineFirstLogin.connectionFailed")
                    : message,
                denied,
                false,
                false,
                string.IsNullOrWhiteSpace(code) ? "failure" : code,
                string.IsNullOrWhiteSpace(code) ? "failure" : code,
                false,
                false,
                clientRequestId,
                serverRequestId,
                cfRay);
        }

        public static PosOnlineBootstrapResult CatalogIncomplete(
            string code,
            string message,
            bool denied,
            bool requiresRetry,
            PosCatalogPullOutcome catalogOutcome,
            string clientRequestId = null,
            string serverRequestId = null,
            string cfRay = null)
        {
            var status = string.IsNullOrWhiteSpace(catalogOutcome?.StatusCode)
                ? (string.IsNullOrWhiteSpace(code) ? "catalog_incomplete" : code)
                : catalogOutcome.StatusCode;

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
                clientRequestId,
                serverRequestId,
                cfRay);
        }
    }
}
