using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

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
            CancellationToken cancellationToken)
        {
            if (options == null)
            {
                return PosOnlineBootstrapResult.Failure("invalid_options", "Configurazione Admin Web POS non valida.", false);
            }

            if (request == null || string.IsNullOrWhiteSpace(localCredential))
            {
                return PosOnlineBootstrapResult.Failure("validation_failed", "Inserisci codice negozio, codice staff e PIN/password.", false);
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
                    return PosOnlineBootstrapResult.Failure(result.Code, result.Message, result.Denied);
                }

                var response = result.Value;
                if (!ValidateFirstLoginResponse(response))
                {
                    return PosOnlineBootstrapResult.Failure("invalid_response", "Risposta Admin Web POS non valida.", false);
                }

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

                _trustedDeviceStore.SaveFirstLogin(response);
                await PosOnlineShopSnapshot.SaveAsync(_factory, response.Shop).ConfigureAwait(false);
                await PosOnlinePolicySnapshot.SaveAsync(_factory, response.Policy).ConfigureAwait(false);
                _logger.LogInfo(
                    "POS online bootstrap success: category=online.bootstrap clientRequestId=" +
                    SafeAuditValue(result.ClientRequestId) +
                    ", serverRequestId=" + SafeAuditValue(result.ServerRequestId) +
                    ", shopCode=" + SafeAuditValue(response.Shop.ShopCode) +
                    ", staffCode=" + SafeAuditValue(response.Staff.StaffCode));

                var security = new SecurityRepository(_factory);
                await security.LogEventAsync(
                    SecurityEventCodes.PosOnlineBootstrap,
                    "shop_code=" + SafeAuditValue(response.Shop.ShopCode) +
                    ", staff_code=" + SafeAuditValue(response.Staff.StaffCode) +
                    ", role_key=" + SafeAuditValue(response.Staff.RoleKey) +
                    ", remote_staff_id=" + SafeAuditValue(response.Staff.StaffId))
                    .ConfigureAwait(false);

                try
                {
                    var salesSync = new PosSalesSyncService(_factory);
                    await salesSync.TrySyncPendingAsync(options, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Bootstrap sales sync skipped.", ex);
                }

                try
                {
                    var catalogPull = new PosCatalogPullService(_factory);
                    await catalogPull.TryPullCatalogAsync(options, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Bootstrap catalog pull skipped.", ex);
                }

                return PosOnlineBootstrapResult.Ok();
            }
            catch (OperationCanceledException)
            {
                return PosOnlineBootstrapResult.Failure("timeout", "Admin Web POS non ha risposto entro il timeout.", false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("POS online bootstrap non completato.", ex);
                return PosOnlineBootstrapResult.Failure("failure", "Collegamento POS online non completato.", false);
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

        private static bool ValidateFirstLoginResponse(PosFirstLoginResponse response)
        {
            return response != null &&
                   !string.IsNullOrWhiteSpace(response.TrustedDeviceToken) &&
                   response.Session != null &&
                   !string.IsNullOrWhiteSpace(response.Session.SessionToken) &&
                   !string.IsNullOrWhiteSpace(response.Session.PosSessionId) &&
                   response.Device != null &&
                   !string.IsNullOrWhiteSpace(response.Device.ShopDeviceId) &&
                   response.Staff != null &&
                   !string.IsNullOrWhiteSpace(response.Staff.StaffId) &&
                   !string.IsNullOrWhiteSpace(response.Staff.StaffCode) &&
                   response.Shop != null &&
                   !string.IsNullOrWhiteSpace(response.Shop.ShopCode);
        }
    }

    public sealed class PosOnlineBootstrapResult
    {
        private PosOnlineBootstrapResult(bool success, string code, string message, bool denied)
        {
            Code = code;
            Denied = denied;
            Message = message;
            Success = success;
        }

        public string Code { get; }
        public bool Denied { get; }
        public string Message { get; }
        public bool Success { get; }

        public static PosOnlineBootstrapResult Ok()
        {
            return new PosOnlineBootstrapResult(true, "success", string.Empty, false);
        }

        public static PosOnlineBootstrapResult Failure(string code, string message, bool denied)
        {
            return new PosOnlineBootstrapResult(
                false,
                string.IsNullOrWhiteSpace(code) ? "failure" : code,
                string.IsNullOrWhiteSpace(message) ? "Collegamento POS online non completato." : message,
                denied);
        }
    }
}
