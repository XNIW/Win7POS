using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>
    /// Executable, non-shipping regression for the two-phase offline
    /// authorization cache. It uses an isolated harness data root and never
    /// constructs a printer, drawer, online client or window.
    /// </summary>
    internal static class AuthorizationLeaseWpfSmoke
    {
        private const string CorrectPin = "2468";
        private const string WrongPin = "0000";
        private const string ShopId = "qa-auth-shop";
        private const string ShopCode = "QA-AUTH";
        private const string StaffId = "qa-auth-staff";
        private const string StaffCode = "qa-cashier";
        private const int CredentialVersion = 7;
        private static readonly string ProcessInstanceId =
            Guid.NewGuid().ToString("N");

        public static async Task<string> RunAsync()
        {
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            var users = new UserRepository(factory);
            var security = new SecurityRepository(factory);
            await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
            {
                Credential = CorrectPin,
                CredentialVersion = CredentialVersion,
                DisplayName = "QA Authorization Cashier",
                RemoteRoleKey = "cashier",
                RemoteShopId = ShopId,
                RemoteStaffId = StaffId,
                ShopCode = ShopCode,
                StaffCode = StaffCode
            }).ConfigureAwait(true);

            var username = await users.FindTrustedRemoteStaffUsernameAsync(
                ShopId,
                ShopCode,
                StaffId,
                StaffCode,
                CredentialVersion).ConfigureAwait(true);
            Require(!string.IsNullOrWhiteSpace(username), "trusted mirror was not seeded");

            var store = new PosTrustedDeviceStore();
            store.Clear();
            var legacyResponse = BuildResponse(includeOfflineAttestation: false);
            store.SaveFirstLogin(
                legacyResponse,
                "qa-auth-legacy-online-generation");
            Require(
                new PosTrustedDeviceStore().TryRead(out var legacyOnlineSession),
                "legacy Admin response did not remain usable online");
            Require(
                PosOfflineAuthorizationLeasePolicy.ValidateOnlineReceipt(
                    legacyOnlineSession,
                    DateTimeOffset.UtcNow).Allowed,
                "legacy Admin response was rejected by the online path");
            RequireDenied(
                PosOfflineAuthorizationLeasePolicy.Evaluate(
                    legacyOnlineSession,
                    DateTimeOffset.UtcNow),
                "offline_attestation_required",
                "legacy Admin response synthesized an offline lease");
            var deniedGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => DateTimeOffset.UtcNow);
            var deniedOperatorSession =
                new OperatorSession(users, security, deniedGuard);
            deniedOperatorSession.SetUserForTesting(new UserAccount
            {
                Id = 900000,
                Username = username,
                DisplayName = "QA Denied Cashier",
                IsActive = true,
                RoleCode = "cashier",
                RoleName = "Cashier",
                PermissionCodes = new List<string> { PermissionCodes.PosPay }
            });
            var deniedPermissionService =
                new PermissionService(deniedOperatorSession);
            var salesBefore = CountSaleRows(factory);
            var outboxBefore = CountSalesOutboxRows(factory);
            var deniedAtPaymentBoundary = false;
            try
            {
                deniedPermissionService.Demand(
                    PermissionCodes.PosPay,
                    "qa unauthorized sale sink");
                InsertUnauthorizedSaleAndOutbox(factory, "same-process");
            }
            catch (PosAuthorizationLeaseException)
            {
                deniedAtPaymentBoundary = true;
            }
            Require(
                deniedAtPaymentBoundary &&
                CountSaleRows(factory) == salesBefore &&
                CountSalesOutboxRows(factory) == outboxBefore,
                "unauthorized sale reached the durable sale or publication outbox sink");
            VerifyLegacyStateReread(store, legacyResponse, formatVersion: 1);
            VerifyLegacyStateReread(store, legacyResponse, formatVersion: 2);
            VerifyLegacyStateReread(store, legacyResponse, formatVersion: 3);

            var response = BuildResponse(includeOfflineAttestation: true);
            store.SaveFirstLogin(response, "qa-auth-generation-1");
            Require(store.TryRead(out var trusted), "trusted session was not persisted");
            Require(
                trusted.OfflineAuthorizationAttested &&
                string.Equals(
                    trusted.EffectiveOfflineAuthorizationExpiresAt,
                    response.EffectiveOfflineAuthorizationExpiresAt,
                    StringComparison.Ordinal),
                "authoritative offline expiry was not preserved across same-process reread");
            var trustedStateJson = File.ReadAllText(store.TrustedDeviceFilePath);
            File.WriteAllText(
                store.TrustedDeviceFilePath,
                trustedStateJson.Replace(
                    response.EffectiveOfflineAuthorizationExpiresAt,
                    DateTimeOffset.Parse(
                            response.EffectiveOfflineAuthorizationExpiresAt,
                            CultureInfo.InvariantCulture)
                        .AddMinutes(1)
                        .ToString("O", CultureInfo.InvariantCulture)));
            Require(
                new PosTrustedDeviceStore().TryRead(out var tamperedSession),
                "tampered expiry prevented bounded online recovery");
            RequireDenied(
                PosOfflineAuthorizationLeasePolicy.Evaluate(
                    tamperedSession,
                    DateTimeOffset.UtcNow),
                "offline_attestation_required",
                "tampered expiry retained offline authorization");
            store.SaveFirstLogin(response, "qa-auth-generation-1");
            Require(
                store.TryRead(out trusted),
                "trusted session was not restored after tamper check");
            store.SaveFirstLogin(response, "qa-auth-generation-1");
            Require(
                store.TryRead(out trusted) &&
                trusted.OfflineAuthorizationAttested,
                "first-login retry after a lost response lost the attestation");

            var originalBound =
                trusted.EffectiveOfflineAuthorizationExpiresAt;
            var heartbeat = BuildHeartbeat(response, "heartbeat-token-1");
            Require(
                store.TrySaveHeartbeat(
                    trusted.GenerationId,
                    trusted,
                    heartbeat,
                    out var heartbeatSession),
                "heartbeat did not commit against the current generation");
            Require(
                string.Equals(
                    heartbeatSession.EffectiveOfflineAuthorizationExpiresAt,
                    originalBound,
                    StringComparison.Ordinal),
                "heartbeat extended the authoritative offline expiry");

            var firstConcurrent = BuildHeartbeat(
                response,
                "heartbeat-token-concurrent-a");
            var secondConcurrent = BuildHeartbeat(
                response,
                "heartbeat-token-concurrent-b");
            var firstCommit = false;
            var secondCommit = false;
            await Task.WhenAll(
                Task.Run(() =>
                {
                    firstCommit = store.TrySaveHeartbeat(
                        heartbeatSession.GenerationId,
                        heartbeatSession,
                        firstConcurrent,
                        out _);
                }),
                Task.Run(() =>
                {
                    secondCommit = store.TrySaveHeartbeat(
                        heartbeatSession.GenerationId,
                        heartbeatSession,
                        secondConcurrent,
                        out _);
                })).ConfigureAwait(true);
            Require(
                firstCommit != secondCommit,
                "concurrent heartbeat receipts did not resolve with one CAS winner");
            Require(
                store.TryRead(out trusted) &&
                string.Equals(
                    trusted.EffectiveOfflineAuthorizationExpiresAt,
                    originalBound,
                    StringComparison.Ordinal),
                "concurrent heartbeat changed the authoritative offline expiry");

            var localReceipt = ParseUtc(trusted.LastOkLocalAt, "local receipt");
            var clock = localReceipt.AddHours(1);
            var guard = new PosOfflineAuthorizationLeaseGuard(store, () => clock);
            var operatorSession = new OperatorSession(users, security, guard);
            var staleAdministrator = new UserAccount
            {
                Id = 900001,
                Username = username,
                DisplayName = "QA Stale Administrator",
                IsActive = true,
                RoleCode = "admin",
                RoleName = "Administrator",
                PermissionCodes = new List<string> { PermissionCodes.DbMaintenance }
            };
            operatorSession.SetUserForTesting(staleAdministrator);

            var beforeWrongPin = guard.Evaluate();
            RequireDenied(
                beforeWrongPin,
                "sync_generation_inactive",
                "fresh generation must start uncommitted");
            var wrongPinResult = await operatorSession.LoginAsync(username, WrongPin)
                .ConfigureAwait(true);
            Require(wrongPinResult == LoginResult.Failed, "wrong PIN was not rejected");
            Require(
                ReferenceEquals(operatorSession.CurrentUser, staleAdministrator),
                "failed PIN unexpectedly replaced the cached operator snapshot");
            RequireDenied(
                guard.Evaluate(),
                "sync_generation_inactive",
                "wrong PIN primed the reusable authorization cache");

            var firstEpoch = await guard.PreflightAsync().ConfigureAwait(true);
            Require(firstEpoch.Decision.Allowed, "epoch preflight 1 was denied");
            PosOnlineSyncRevocationLatch.InvalidateAuthorizationState();
            var secondEpoch = await guard.PreflightAsync().ConfigureAwait(true);
            Require(secondEpoch.Decision.Allowed, "epoch preflight 2 was denied");
            var epochCommit = await guard
                .CommitAuthenticationAsync(firstEpoch, secondEpoch)
                .ConfigureAwait(true);
            RequireDenied(
                epochCommit.Decision,
                "sync_generation_changed",
                "epoch change was accepted across authentication");
            RequireDenied(
                guard.Evaluate(),
                "sync_generation_inactive",
                "failed epoch commit primed the reusable cache");

            var firstGeneration = await guard.PreflightAsync().ConfigureAwait(true);
            Require(firstGeneration.Decision.Allowed, "generation preflight 1 was denied");
            store.SaveFirstLogin(response, "qa-auth-generation-2");
            var secondGeneration = await guard.PreflightAsync().ConfigureAwait(true);
            Require(secondGeneration.Decision.Allowed, "generation preflight 2 was denied");
            var generationCommit = await guard
                .CommitAuthenticationAsync(firstGeneration, secondGeneration)
                .ConfigureAwait(true);
            RequireDenied(
                generationCommit.Decision,
                "sync_generation_changed",
                "generation change was accepted across authentication");
            RequireDenied(
                guard.Evaluate(),
                "sync_generation_inactive",
                "failed generation commit primed the reusable cache");

            Require(store.TryRead(out trusted), "replacement trusted session was not persisted");
            localReceipt = ParseUtc(trusted.LastOkLocalAt, "replacement local receipt");
            clock = localReceipt.AddHours(1);
            var successfulLogin = await operatorSession.LoginAsync(username, CorrectPin)
                .ConfigureAwait(true);
            Require(successfulLogin == LoginResult.Success, "correct PIN did not authenticate");
            Require(
                operatorSession.CurrentUser != null &&
                string.Equals(operatorSession.CurrentUser.RoleCode, "cashier", StringComparison.Ordinal),
                "successful PIN did not replace the stale administrator snapshot");
            Require(guard.Evaluate().Allowed, "successful PIN did not prime the authorization cache");
            Require(
                await MainWindow.IsSessionBoundToCurrentTrustedIdentityAsync(
                    factory,
                    operatorSession).ConfigureAwait(true),
                "fresh authenticated authority did not match its durable account");

            var authenticatedAccount = operatorSession.CurrentUser;
            await users.UpdateAsync(
                authenticatedAccount.Id,
                authenticatedAccount.DisplayName,
                authenticatedAccount.RoleId,
                isActive: true,
                maxDiscountPercent: authenticatedAccount.MaxDiscountPercent + 1,
                requirePinChange: authenticatedAccount.RequirePinChange).ConfigureAwait(true);
            Require(
                !await MainWindow.IsSessionBoundToCurrentTrustedIdentityAsync(
                    factory,
                    operatorSession).ConfigureAwait(true),
                "durable authority change left the cached operator session bound");

            clock = localReceipt.AddHours(2);
            Require(guard.Evaluate().Allowed, "forward high-water advance was denied");
            clock = localReceipt.AddMinutes(90);
            RequireDenied(
                guard.Evaluate(),
                "clock_rollback",
                "authorization high-water moved backwards");
            clock = localReceipt.AddMinutes(150);
            Require(guard.Evaluate().Allowed, "forward time after rollback denial was not accepted");

            return "PASS authorization lease dynamic smoke" + Environment.NewLine +
                "wrongPinDoesNotPrime=True" + Environment.NewLine +
                "epochChangeDenied=True" + Environment.NewLine +
                "generationChangeDenied=True" + Environment.NewLine +
                "successfulPinPrimes=True" + Environment.NewLine +
                "durableAuthorityChangeDenied=True" + Environment.NewLine +
                "highWaterMonotonic=True" + Environment.NewLine +
                "legacyAdminOnlineCompatible=True" + Environment.NewLine +
                "legacyOfflineDenied=True" + Environment.NewLine +
                "unauthorizedSaleSinkRows=0" + Environment.NewLine +
                "unauthorizedPublicationOutboxRows=0" + Environment.NewLine +
                "legacyV1V2V3OfflineDenied=True" + Environment.NewLine +
                "attestedExpirySameProcessReread=True" + Environment.NewLine +
                "tamperedExpiryDenied=True" + Environment.NewLine +
                "firstLoginLostResponseRetry=True" + Environment.NewLine +
                "heartbeatDoesNotExtendBound=True" + Environment.NewLine +
                "concurrentHeartbeatCas=True" + Environment.NewLine +
                "hardwareEffects=0" + Environment.NewLine;
        }

        public static async Task<string> PrepareRestartProbeAsync()
        {
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            var users = new UserRepository(factory);
            await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
            {
                Credential = CorrectPin,
                CredentialVersion = CredentialVersion,
                DisplayName = "QA Restart Cashier",
                RemoteRoleKey = "cashier",
                RemoteShopId = ShopId,
                RemoteStaffId = StaffId,
                ShopCode = ShopCode,
                StaffCode = StaffCode
            }).ConfigureAwait(true);

            var store = new PosTrustedDeviceStore();
            store.Clear();
            store.SaveFirstLogin(
                BuildResponse(includeOfflineAttestation: true),
                "qa-auth-process-restart");
            Require(
                store.TryRead(out var trusted) &&
                trusted.TrustedStateFormatVersion == 4 &&
                trusted.OfflineAuthorizationAttested,
                "restart probe did not persist a process-scoped offline attestation");

            return "PASS authorization lease restart prepare" + Environment.NewLine +
                "processInstance=" + ProcessInstanceId + Environment.NewLine +
                "stateFormat=4" + Environment.NewLine +
                "offlineAttested=True" + Environment.NewLine;
        }

        public static async Task<string> VerifyRestartProbeAsync()
        {
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            var users = new UserRepository(factory);
            var security = new SecurityRepository(factory);
            var username = await users.FindTrustedRemoteStaffUsernameAsync(
                ShopId,
                ShopCode,
                StaffId,
                StaffCode,
                CredentialVersion).ConfigureAwait(true);
            Require(
                !string.IsNullOrWhiteSpace(username),
                "restart probe could not reload the trusted remote mirror");

            var store = new PosTrustedDeviceStore();
            Require(
                store.TryRead(out var restartedSession),
                "restart probe state was not readable for bounded online recovery");
            Require(
                restartedSession.TrustedStateFormatVersion == 4,
                "restart probe did not preserve the current trusted-state format");
            Require(
                PosOfflineAuthorizationLeasePolicy.ValidateOnlineReceipt(
                    restartedSession,
                    DateTimeOffset.UtcNow).Allowed,
                "restart probe blocked bounded online recovery");
            Require(
                !restartedSession.OfflineAuthorizationAttested &&
                string.IsNullOrWhiteSpace(
                    restartedSession.EffectiveOfflineAuthorizationExpiresAt),
                "process restart retained the prior offline attestation");
            RequireDenied(
                PosOfflineAuthorizationLeasePolicy.Evaluate(
                    restartedSession,
                    DateTimeOffset.UtcNow),
                "offline_attestation_required",
                "process restart retained offline authorization");

            var guard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => DateTimeOffset.UtcNow);
            var operatorSession = new OperatorSession(users, security, guard);
            operatorSession.SetUserForTesting(new UserAccount
            {
                Id = 900002,
                Username = username,
                DisplayName = "QA Restart Cashier",
                IsActive = true,
                RoleCode = "cashier",
                RoleName = "Cashier",
                PermissionCodes = new List<string> { PermissionCodes.PosPay }
            });
            var permissions = new PermissionService(operatorSession);
            var salesBefore = CountSaleRows(factory);
            var outboxBefore = CountSalesOutboxRows(factory);
            var deniedBeforeSink = false;
            try
            {
                permissions.Demand(
                    PermissionCodes.PosPay,
                    "qa restarted authorization sale sink");
                InsertUnauthorizedSaleAndOutbox(factory, "restart");
            }
            catch (PosAuthorizationLeaseException)
            {
                deniedBeforeSink = true;
            }
            Require(
                deniedBeforeSink &&
                CountSaleRows(factory) == salesBefore &&
                CountSalesOutboxRows(factory) == outboxBefore,
                "restarted offline authority reached the sale or publication outbox sink");

            store.SaveFirstLogin(
                BuildResponse(includeOfflineAttestation: true),
                "qa-auth-process-restart-recovered");
            Require(
                new PosTrustedDeviceStore().TryRead(out var recoveredSession) &&
                recoveredSession.OfflineAuthorizationAttested,
                "fresh online authorization did not recover after process restart");

            return "PASS authorization lease restart verify" + Environment.NewLine +
                "processInstance=" + ProcessInstanceId + Environment.NewLine +
                "onlineRecoveryReadable=True" + Environment.NewLine +
                "offlineAttestationAfterRestart=False" + Environment.NewLine +
                "offlineDenial=offline_attestation_required" + Environment.NewLine +
                "unauthorizedSaleSinkRows=0" + Environment.NewLine +
                "unauthorizedPublicationOutboxRows=0" + Environment.NewLine +
                "freshOnlineRecovery=True" + Environment.NewLine;
        }

        private static void VerifyLegacyStateReread(
            PosTrustedDeviceStore store,
            PosFirstLoginResponse response,
            int formatVersion)
        {
            store.SaveFirstLogin(
                response,
                "qa-auth-legacy-v" + formatVersion);
            var json = File.ReadAllText(store.TrustedDeviceFilePath);
            json = json.Replace(
                "\"formatVersion\":4",
                "\"formatVersion\":" + formatVersion);
            File.WriteAllText(store.TrustedDeviceFilePath, json);

            Require(
                new PosTrustedDeviceStore().TryRead(out var legacySession),
                "legacy v" + formatVersion + " state did not reload online");
            Require(
                legacySession.TrustedStateFormatVersion == formatVersion,
                "legacy state version changed during reread");
            RequireDenied(
                PosOfflineAuthorizationLeasePolicy.Evaluate(
                    legacySession,
                    DateTimeOffset.UtcNow),
                "offline_attestation_required",
                "legacy v" + formatVersion + " state authorized offline");
        }

        private static PosFirstLoginResponse BuildResponse(
            bool includeOfflineAttestation)
        {
            var now = DateTimeOffset.UtcNow;
            return new PosFirstLoginResponse
            {
                EffectiveOfflineAuthorizationExpiresAt =
                    includeOfflineAttestation
                        ? now.AddHours(4).ToString(
                            "O",
                            CultureInfo.InvariantCulture)
                        : null,
                Ok = true,
                ServerTime = now.ToString("O", CultureInfo.InvariantCulture),
                TrustedDeviceToken = "qa-auth-device-token",
                Device = new PosTrustedDeviceResponse
                {
                    ShopDeviceId = "qa-auth-device",
                    Status = "active",
                    Trusted = true
                },
                Session = new PosSessionResponse
                {
                    ExpiresAt = now.AddSeconds(PosOnlineContract.OfflineAuthorizationMaxAgeSeconds)
                        .ToString("O", CultureInfo.InvariantCulture),
                    HeartbeatAfterSeconds = 300,
                    PosSessionId = "qa-auth-session",
                    SessionToken = "qa-auth-session-token"
                },
                Shop = new PosShopResponse
                {
                    ShopCode = ShopCode,
                    ShopId = ShopId,
                    ShopName = "QA Authorization Shop",
                    ShopStatus = "active",
                    Source = "qa_harness"
                },
                Staff = new PosStaffResponse
                {
                    CredentialVersion = CredentialVersion,
                    DisplayName = "QA Authorization Cashier",
                    RoleKey = "cashier",
                    StaffCode = StaffCode,
                    StaffId = StaffId
                }
            };
        }

        private static PosHeartbeatResponse BuildHeartbeat(
            PosFirstLoginResponse firstLogin,
            string sessionToken)
        {
            var serverTime = DateTimeOffset.Parse(
                    firstLogin.ServerTime,
                    CultureInfo.InvariantCulture)
                .AddMinutes(1);
            return new PosHeartbeatResponse
            {
                Ok = true,
                ServerTime = serverTime.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                Session = new PosSessionResponse
                {
                    ExpiresAt = serverTime.AddHours(12).ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    HeartbeatAfterSeconds = 300,
                    PosSessionId = firstLogin.Session.PosSessionId,
                    SessionToken = sessionToken
                }
            };
        }

        private static void InsertUnauthorizedSaleAndOutbox(
            SqliteConnectionFactory factory,
            string suffix)
        {
            var clientSaleId = "qa-unauthorized-client-sale-" + suffix;
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var saleId = connection.ExecuteScalar<long>(
                    @"INSERT INTO sales(
  client_sale_id, code, createdAt, kind, total, paidCash, paidCard, change)
VALUES(
  @clientSaleId, @code, @createdAt, 0, 100, 100, 0, 0);
SELECT last_insert_rowid();",
                    new
                    {
                        clientSaleId,
                        code = "QA-UNAUTHORIZED-SALE-" + suffix,
                        createdAt
                    },
                    transaction);
                connection.Execute(
                    @"INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, idempotency_key, created_at, updated_at)
VALUES(
  @saleId, @clientSaleId, @idempotencyKey, @createdAt, @createdAt);",
                    new
                    {
                        saleId,
                        clientSaleId,
                        idempotencyKey =
                            "qa-unauthorized-idempotency-" + suffix,
                        createdAt
                    },
                    transaction);
                transaction.Commit();
            }
        }

        private static long CountSaleRows(SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                return connection.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sales;");
            }
        }

        private static long CountSalesOutboxRows(
            SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                return connection.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sales_sync_outbox;");
            }
        }

        private static DateTimeOffset ParseUtc(string value, string label)
        {
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                throw new InvalidOperationException("Invalid " + label + " timestamp.");
            }
            return parsed;
        }

        private static void RequireDenied(
            PosOfflineAuthorizationLeaseDecision decision,
            string expectedCode,
            string message)
        {
            Require(
                decision != null &&
                !decision.Allowed &&
                string.Equals(decision.Code, expectedCode, StringComparison.Ordinal),
                message + ": actual=" + (decision?.Code ?? "<null>"));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
