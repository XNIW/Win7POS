using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
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
        private const string RecoveryPin = "8642";
        private const string RecoveryUsername = "qa-local-recovery";
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
            if (await users.GetByUsernameAsync(RecoveryUsername)
                    .ConfigureAwait(true) == null)
            {
                using var recoveryConnection = factory.Open();
                var adminRoleId = recoveryConnection.ExecuteScalar<int>(
                    "SELECT id FROM roles WHERE LOWER(code) = 'admin' LIMIT 1");
                var recoverySalt = PinHelper.GenerateSalt();
                await users.CreateAsync(
                        RecoveryUsername,
                        "QA Local Recovery",
                        PinHelper.HashPin(RecoveryPin, recoverySalt),
                        recoverySalt,
                        adminRoleId)
                    .ConfigureAwait(true);
            }

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
            var retryGenerationId = string.Empty;
            Require(
                PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    trusted,
                    out var retrySourceGeneration) &&
                store.TryGetReusableGenerationId(
                    response,
                    retrySourceGeneration.Fingerprint,
                    out retryGenerationId) &&
                string.Equals(
                    retryGenerationId,
                    trusted.GenerationId,
                    StringComparison.Ordinal),
                "exact first-login response did not reuse its generation");
            store.SaveFirstLogin(response, retryGenerationId);
            Require(
                store.TryRead(out trusted) &&
                trusted.OfflineAuthorizationAttested,
                "first-login retry after a lost response lost the attestation");
            File.WriteAllBytes(
                store.TrustedDeviceFilePath,
                new byte[(64 * 1024) + 1]);
            Require(
                !new PosTrustedDeviceStore().TryRead(out _),
                "oversized trusted-device state was not rejected");
            store.SaveFirstLogin(response, retryGenerationId);
            Require(
                store.TryRead(out trusted),
                "trusted state was not recoverable after oversized input");

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

            store.SaveFirstLogin(response, "qa-auth-frozen-clock");
            Require(
                store.TryRead(out var frozenSession),
                "frozen-clock trusted session was not persisted");
            var frozenClock = ParseUtc(
                    frozenSession.LastOkLocalAt,
                    "frozen-clock local receipt")
                .AddMilliseconds(50);
            long monotonicTicks = 0;
            var frozenGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => frozenClock,
                () => monotonicTicks,
                TimeSpan.TicksPerSecond);
            var frozenOperatorSession =
                new OperatorSession(users, security, frozenGuard);
            Require(
                await frozenOperatorSession.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "frozen-clock control login did not authenticate");
            var frozenAllowed = frozenGuard.Evaluate();
            Require(
                frozenAllowed.Allowed &&
                frozenAllowed.EffectiveExpiresAt.HasValue &&
                frozenAllowed.EstimatedServerNow.HasValue,
                "frozen-clock control did not start from an allowed lease");
            monotonicTicks += TimeSpan.TicksPerMillisecond;
            var frozenProgress = frozenGuard.Evaluate();
            Require(
                frozenProgress.Allowed &&
                frozenProgress.EstimatedServerNow >
                    frozenAllowed.EstimatedServerNow &&
                frozenProgress.WallEstimatedServerNow ==
                    frozenAllowed.WallEstimatedServerNow,
                "equal wall timestamps did not preserve monotonic progress");
            var remainingTicks =
                (frozenProgress.EffectiveExpiresAt.Value -
                 frozenProgress.EstimatedServerNow.Value).Ticks;
            Require(
                remainingTicks > 0,
                "frozen-clock control did not start before expiry");
            monotonicTicks += remainingTicks;

            var frozenPermissionService =
                new PermissionService(frozenOperatorSession);
            var frozenSalesBefore = CountSaleRows(factory);
            var frozenOutboxBefore = CountSalesOutboxRows(factory);
            var frozenClockDeniedBeforeSink = false;
            try
            {
                frozenPermissionService.Demand(
                    PermissionCodes.PosPay,
                    "qa frozen-clock authorization sale sink");
                InsertUnauthorizedSaleAndOutbox(factory, "frozen-clock");
            }
            catch (PosAuthorizationLeaseException ex)
            {
                frozenClockDeniedBeforeSink = string.Equals(
                    ex.Code,
                    "offline_lease_expired",
                    StringComparison.Ordinal);
            }
            Require(
                frozenClockDeniedBeforeSink &&
                CountSaleRows(factory) == frozenSalesBefore &&
                CountSalesOutboxRows(factory) == frozenOutboxBefore,
                "frozen wall clock extended authority into the sale or outbox sink");

            store.SaveFirstLogin(response, "qa-auth-clock-regression");
            Require(
                store.TryRead(out var regressionSession),
                "clock-regression trusted session was not persisted");
            var regressionClock = ParseUtc(
                    regressionSession.LastOkLocalAt,
                    "clock-regression local receipt")
                .AddMilliseconds(50);
            long regressionTicks = 10;
            var regressionGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => regressionClock,
                () => regressionTicks,
                TimeSpan.TicksPerSecond);
            var regressionOperatorSession =
                new OperatorSession(users, security, regressionGuard);
            Require(
                await regressionOperatorSession.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "clock-regression control login did not authenticate");
            regressionTicks = 9;
            RequireDenied(
                regressionGuard.Evaluate(),
                "trusted_time_continuity_lost",
                "monotonic counter regression did not fail closed");

            store.SaveFirstLogin(response, "qa-auth-clock-provider-failure");
            Require(
                store.TryRead(out var providerFailureSession),
                "provider-failure trusted session was not persisted");
            var providerFailureClock = ParseUtc(
                    providerFailureSession.LastOkLocalAt,
                    "provider-failure local receipt")
                .AddMilliseconds(50);
            var providerFails = false;
            var providerFailureGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => providerFailureClock,
                () =>
                {
                    if (providerFails)
                    {
                        throw new InvalidOperationException(
                            "synthetic monotonic provider failure");
                    }
                    return 0;
                },
                TimeSpan.TicksPerSecond);
            var providerFailureOperatorSession =
                new OperatorSession(users, security, providerFailureGuard);
            Require(
                await providerFailureOperatorSession
                    .LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "provider-failure control login did not authenticate");
            providerFails = true;
            RequireDenied(
                providerFailureGuard.Evaluate(),
                "trusted_time_continuity_lost",
                "monotonic provider failure did not fail closed");

            var invalidFrequencyGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => providerFailureClock,
                () => 0,
                monotonicFrequency: 0);
            RequireDenied(
                (await invalidFrequencyGuard.PreflightAsync().ConfigureAwait(true))
                    .Decision,
                "trusted_time_continuity_lost",
                "invalid monotonic frequency did not fail closed");

            store.SaveFirstLogin(response, "qa-auth-clock-overflow");
            Require(
                store.TryRead(out var overflowSession),
                "clock-overflow trusted session was not persisted");
            var overflowClock = ParseUtc(
                    overflowSession.LastOkLocalAt,
                    "clock-overflow local receipt")
                .AddMilliseconds(50);
            long overflowTicks = 0;
            var overflowGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => overflowClock,
                () => overflowTicks,
                monotonicFrequency: 1);
            var overflowOperatorSession =
                new OperatorSession(users, security, overflowGuard);
            Require(
                await overflowOperatorSession.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "clock-overflow control login did not authenticate");
            overflowTicks = long.MaxValue;
            RequireDenied(
                overflowGuard.Evaluate(),
                "trusted_time_continuity_lost",
                "monotonic elapsed-time overflow did not fail closed");

            store.SaveFirstLogin(response, "qa-auth-preflight-clock");
            Require(
                store.TryRead(out var preflightClockSession),
                "preflight-clock trusted session was not persisted");
            var preflightClockLocalReceipt = ParseUtc(
                preflightClockSession.LastOkLocalAt,
                "preflight-clock local receipt");
            var preflightClock = preflightClockLocalReceipt
                .AddMilliseconds(50);
            var preflightEstimatedServerNow = ParseUtc(
                    preflightClockSession.LastOkServerAt,
                    "preflight-clock server receipt")
                .AddMilliseconds(50);
            var preflightExpiry = ParseUtc(
                preflightClockSession.EffectiveOfflineAuthorizationExpiresAt,
                "preflight-clock authoritative expiry");
            long preflightTicks = 0;
            var preflightAdvanced = false;
            var preflightClockGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => preflightClock,
                _ =>
                {
                    if (!preflightAdvanced)
                    {
                        preflightTicks +=
                            (preflightExpiry -
                             preflightEstimatedServerNow).Ticks;
                        preflightAdvanced = true;
                    }
                    return Task.FromResult(true);
                },
                () => preflightTicks,
                TimeSpan.TicksPerSecond);
            RequireDenied(
                (await preflightClockGuard.PreflightAsync()
                    .ConfigureAwait(true)).Decision,
                "offline_lease_expired",
                "preflight generation check froze authorization time");

            long activationDelayTicks = 0;
            var activationDelayStore = new PosTrustedDeviceStore(
                () => activationDelayTicks,
                TimeSpan.TicksPerSecond,
                "qa-activation-delay-clock");
            activationDelayStore.Clear();
            var activationDelayResponse =
                BuildResponse(includeOfflineAttestation: true);
            var activationDelayServerAt = ParseUtc(
                activationDelayResponse.ServerTime,
                "activation-delay server receipt");
            var activationDelayExpiry =
                activationDelayServerAt.AddSeconds(5);
            activationDelayResponse.EffectiveOfflineAuthorizationExpiresAt =
                activationDelayExpiry.ToString("O", CultureInfo.InvariantCulture);
            activationDelayResponse.Session.ExpiresAt =
                activationDelayExpiry.ToString("O", CultureInfo.InvariantCulture);
            var capturedBeforeActivation =
                activationDelayStore.CaptureOnlineReceiptClock(
                    requireMonotonicClock: true);
            activationDelayTicks =
                (activationDelayExpiry - activationDelayServerAt).Ticks;
            var activationDelayDenied = false;
            try
            {
                activationDelayStore.SaveFirstLogin(
                    activationDelayResponse,
                    "qa-auth-activation-delay",
                    capturedBeforeActivation);
            }
            catch (InvalidDataException ex)
            {
                activationDelayDenied = ex.Message.IndexOf(
                    "offline_lease_expired",
                    StringComparison.Ordinal) >= 0;
            }
            Require(
                activationDelayDenied &&
                !activationDelayStore.HasStoredState(),
                "expired local activation persisted an offline lease");

            long firstUseTicks = 0;
            var firstUseStore = new PosTrustedDeviceStore(
                () => firstUseTicks,
                TimeSpan.TicksPerSecond,
                "qa-first-use-clock");
            firstUseStore.Clear();
            var firstUseResponse = BuildResponse(includeOfflineAttestation: true);
            var firstUseServerAt = ParseUtc(
                firstUseResponse.ServerTime,
                "first-use server receipt");
            var firstUseExpiry = firstUseServerAt.AddSeconds(5);
            firstUseResponse.EffectiveOfflineAuthorizationExpiresAt =
                firstUseExpiry.ToString("O", CultureInfo.InvariantCulture);
            firstUseResponse.Session.ExpiresAt =
                firstUseExpiry.ToString("O", CultureInfo.InvariantCulture);
            firstUseStore.SaveFirstLogin(
                firstUseResponse,
                "qa-auth-first-use-clock");
            Require(
                firstUseStore.TryRead(out var firstUseSession),
                "first-use trusted session was not persisted");
            var frozenFirstUseWall = ParseUtc(
                firstUseSession.LastOkLocalAt,
                "first-use local receipt");
            firstUseTicks = (firstUseExpiry - firstUseServerAt).Ticks;
            var firstUseGuard = new PosOfflineAuthorizationLeaseGuard(
                firstUseStore,
                () => frozenFirstUseWall,
                () => firstUseTicks,
                TimeSpan.TicksPerSecond);
            var firstUseOperatorSession =
                new OperatorSession(users, security, firstUseGuard);
            Require(
                await firstUseOperatorSession.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.AuthorizationExpired &&
                string.Equals(
                    firstUseOperatorSession.LastAuthorizationFailureCode,
                    "offline_lease_expired",
                    StringComparison.Ordinal),
                "frozen wall clock allowed the first offline login after receipt expiry");
            firstUseOperatorSession.SetUserForTesting(new UserAccount
            {
                Id = 900003,
                Username = username,
                DisplayName = "QA First-use Cashier",
                IsActive = true,
                RoleCode = "cashier",
                RoleName = "Cashier",
                PermissionCodes = new List<string> { PermissionCodes.PosPay }
            });
            var firstUsePermissions = new PermissionService(firstUseOperatorSession);
            var firstUseSalesBefore = CountSaleRows(factory);
            var firstUseOutboxBefore = CountSalesOutboxRows(factory);
            var firstUseDeniedBeforeSink = false;
            try
            {
                firstUsePermissions.Demand(
                    PermissionCodes.PosPay,
                    "qa first-use expired authorization sale sink");
                InsertUnauthorizedSaleAndOutbox(factory, "first-use-expired");
            }
            catch (PosAuthorizationLeaseException ex)
            {
                firstUseDeniedBeforeSink = string.Equals(
                    ex.Code,
                    "offline_lease_expired",
                    StringComparison.Ordinal);
            }
            Require(
                firstUseDeniedBeforeSink &&
                CountSaleRows(factory) == firstUseSalesBefore &&
                CountSalesOutboxRows(factory) == firstUseOutboxBefore,
                "expired first-use authority reached the sale or outbox sink");

            long retryClockTicks = 0;
            var retryClockStore = new PosTrustedDeviceStore(
                () => retryClockTicks,
                TimeSpan.TicksPerSecond,
                "qa-retry-clock");
            retryClockStore.Clear();
            retryClockStore.SaveFirstLogin(
                firstUseResponse,
                "qa-auth-retry-clock");
            retryClockTicks = TimeSpan.FromSeconds(2).Ticks;
            var reusedRetryGenerationId = string.Empty;
            Require(
                retryClockStore.TryRead(out var firstRetrySession) &&
                PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    firstRetrySession,
                    out var firstRetryGeneration) &&
                retryClockStore.TryGetReusableGenerationId(
                    firstUseResponse,
                    firstRetryGeneration.Fingerprint,
                    out reusedRetryGenerationId),
                "production retry generation was not reusable");
            retryClockStore.SaveFirstLogin(
                firstUseResponse,
                reusedRetryGenerationId);
            Require(
                retryClockStore.TryRead(out var retryClockSession),
                "retry-clock trusted session was not persisted");
            var retryClockWall = ParseUtc(
                retryClockSession.LastOkLocalAt,
                "retry-clock local receipt");
            retryClockTicks =
                (firstUseExpiry - firstUseServerAt).Ticks;
            RequireDenied(
                (await new PosOfflineAuthorizationLeaseGuard(
                        retryClockStore,
                        () => retryClockWall,
                        () => retryClockTicks,
                        TimeSpan.TicksPerSecond)
                    .PreflightAsync().ConfigureAwait(true)).Decision,
                "offline_lease_expired",
                "lost-response retry reset the trusted receipt clock");

            long heartbeatClockTicks = 0;
            var heartbeatClockStore = new PosTrustedDeviceStore(
                () => heartbeatClockTicks,
                TimeSpan.TicksPerSecond,
                "qa-heartbeat-clock");
            heartbeatClockStore.Clear();
            heartbeatClockStore.SaveFirstLogin(
                firstUseResponse,
                "qa-auth-heartbeat-clock");
            Require(
                heartbeatClockStore.TryRead(
                    out var heartbeatClockSession),
                "heartbeat-clock trusted session was not persisted");
            heartbeatClockTicks =
                TimeSpan.FromSeconds(2).Ticks;
            var staleHeartbeat = new PosHeartbeatResponse
            {
                Ok = true,
                ServerTime = firstUseServerAt.AddSeconds(-1)
                    .ToString("O", CultureInfo.InvariantCulture),
                Session = new PosSessionResponse
                {
                    ExpiresAt = firstUseServerAt.AddHours(1)
                        .ToString("O", CultureInfo.InvariantCulture),
                    HeartbeatAfterSeconds = 300,
                    PosSessionId = firstUseResponse.Session.PosSessionId,
                    SessionToken = "qa-heartbeat-stale-token"
                }
            };
            Require(
                heartbeatClockStore.TrySaveHeartbeat(
                    heartbeatClockSession.GenerationId,
                    heartbeatClockSession,
                    staleHeartbeat,
                    out var staleHeartbeatRefreshed),
                "stale heartbeat did not commit fail-closed");
            var nonAdvancingHeartbeat = new PosHeartbeatResponse
            {
                Ok = true,
                ServerTime = firstUseResponse.ServerTime,
                Session = new PosSessionResponse
                {
                    ExpiresAt = firstUseServerAt.AddHours(1)
                        .ToString("O", CultureInfo.InvariantCulture),
                    HeartbeatAfterSeconds = 300,
                    PosSessionId = firstUseResponse.Session.PosSessionId,
                    SessionToken = "qa-heartbeat-clock-token"
                }
            };
            Require(
                heartbeatClockStore.TrySaveHeartbeat(
                    staleHeartbeatRefreshed.GenerationId,
                    staleHeartbeatRefreshed,
                    nonAdvancingHeartbeat,
                    out var heartbeatClockRefreshed),
                "non-advancing heartbeat did not commit");
            var heartbeatClockWall = ParseUtc(
                heartbeatClockRefreshed.LastOkLocalAt,
                "heartbeat-clock local receipt");
            heartbeatClockTicks =
                (firstUseExpiry - firstUseServerAt).Ticks;
            RequireDenied(
                (await new PosOfflineAuthorizationLeaseGuard(
                        heartbeatClockStore,
                        () => heartbeatClockWall,
                        () => heartbeatClockTicks,
                        TimeSpan.TicksPerSecond)
                    .PreflightAsync().ConfigureAwait(true)).Decision,
                "offline_lease_expired",
                "non-advancing heartbeat reset the trusted receipt clock");

            long betweenPreflightsTicks = 0;
            var betweenPreflightsStore = new PosTrustedDeviceStore(
                () => betweenPreflightsTicks,
                TimeSpan.TicksPerSecond,
                "qa-between-preflights-clock");
            betweenPreflightsStore.Clear();
            betweenPreflightsStore.SaveFirstLogin(
                firstUseResponse,
                "qa-auth-between-preflights");
            Require(
                betweenPreflightsStore.TryRead(
                    out var betweenPreflightsSession),
                "between-preflights trusted session was not persisted");
            var betweenPreflightsWall = ParseUtc(
                betweenPreflightsSession.LastOkLocalAt,
                "between-preflights local receipt");
            var betweenPreflightsGuard = new PosOfflineAuthorizationLeaseGuard(
                betweenPreflightsStore,
                () => betweenPreflightsWall,
                () => betweenPreflightsTicks,
                TimeSpan.TicksPerSecond);
            var beforePreflightWait = await betweenPreflightsGuard
                .PreflightAsync().ConfigureAwait(true);
            Require(
                beforePreflightWait.Decision.Allowed,
                "preflight-wait control was denied before expiry");
            betweenPreflightsTicks =
                (firstUseExpiry - firstUseServerAt).Ticks;
            RequireDenied(
                (await betweenPreflightsGuard.PreflightAsync()
                    .ConfigureAwait(true)).Decision,
                "offline_lease_expired",
                "time between preflights did not advance the trusted receipt clock");

            betweenPreflightsStore.Clear();
            await VerifyCrossGenerationReplayContinuityAsync(
                    factory,
                    users,
                    security,
                    username)
                .ConfigureAwait(true);
            store.Clear();
            store.SaveFirstLogin(response, "qa-auth-cross-preflight-regression");
            Require(
                store.TryRead(out var crossPreflightSession),
                "cross-preflight trusted session was not persisted");
            var crossPreflightWall = ParseUtc(
                    crossPreflightSession.LastOkLocalAt,
                    "cross-preflight local receipt")
                .AddMilliseconds(50);
            long crossPreflightTicks = 100;
            var crossPreflightGuard = new PosOfflineAuthorizationLeaseGuard(
                store,
                () => crossPreflightWall,
                () => crossPreflightTicks,
                TimeSpan.TicksPerSecond);
            var orderedFirst = await crossPreflightGuard
                .PreflightAsync().ConfigureAwait(true);
            Require(
                orderedFirst.Decision.Allowed,
                "cross-preflight control first evaluation was denied");
            crossPreflightTicks = 90;
            var regressedSecond = await crossPreflightGuard
                .PreflightAsync().ConfigureAwait(true);
            Require(
                regressedSecond.Decision.Allowed,
                "cross-preflight regression did not reach the commit boundary");
            crossPreflightTicks = 100;
            RequireDenied(
                (await crossPreflightGuard.CommitAuthenticationAsync(
                    orderedFirst,
                    regressedSecond).ConfigureAwait(true)).Decision,
                "trusted_time_continuity_lost",
                "cross-preflight monotonic regression was accepted at commit");

            await SeedCatalogSaleSafetyAsync(factory).ConfigureAwait(true);
            long saleRaceTicks = 0;
            var saleRaceStore = new PosTrustedDeviceStore(
                () => Interlocked.Read(ref saleRaceTicks),
                TimeSpan.TicksPerSecond,
                "qa-sale-race-clock");
            saleRaceStore.Clear();
            var saleRaceResponse =
                BuildResponse(includeOfflineAttestation: true);
            saleRaceStore.SaveFirstLogin(
                saleRaceResponse,
                "qa-auth-sale-race-expiry");
            Require(
                saleRaceStore.TryRead(out var saleRaceSession),
                "sale-race trusted session was not persisted");
            var saleRaceWall = ParseUtc(
                    saleRaceSession.LastOkLocalAt,
                    "sale-race local receipt")
                .AddMilliseconds(50);
            var saleRaceGuard = new PosOfflineAuthorizationLeaseGuard(
                saleRaceStore,
                () => saleRaceWall,
                () => Interlocked.Read(ref saleRaceTicks),
                TimeSpan.TicksPerSecond);
            var saleRaceOperator =
                new OperatorSession(users, security, saleRaceGuard);
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "sale-race initial control login did not authenticate");
            var loginRevocationInjected = false;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (!loginRevocationInjected &&
                        string.Equals(
                            checkpoint,
                            "before_operator_authority_bind",
                            StringComparison.Ordinal))
                    {
                        loginRevocationInjected = true;
                        PosOnlineSyncRevocationLatch
                            .InvalidateAuthorizationState();
                    }
                });
            var revokedLogin = await saleRaceOperator
                .LoginAsync(username, CorrectPin)
                .ConfigureAwait(true);
            saleRaceOperator
                .SetAuthorizationUseTestHookForTesting(null);
            Require(
                loginRevocationInjected &&
                revokedLogin == LoginResult.AuthorizationExpired &&
                !saleRaceOperator.IsLoggedIn,
                "revocation between authentication commit and operator binding retained stale operator authority");
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "sale-race control login did not authenticate");
            var operatorAuthorityVersionField = typeof(OperatorSession)
                .GetField(
                    "_operatorAuthorityVersion",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            Require(
                operatorAuthorityVersionField != null,
                "operator authority version was not found");
            var replacementOperator = new UserAccount
            {
                Id = 900004,
                Username = "qa-replacement-cashier",
                DisplayName = "QA Replacement Cashier",
                IsActive = true,
                RoleCode = "cashier",
                RoleName = "Cashier",
                PermissionCodes = new List<string>
                {
                    PermissionCodes.PosPay
                }
            };
            var replacementLoginInjected = false;
            long replacementLoginEpoch = long.MinValue;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (replacementLoginInjected ||
                        !string.Equals(
                            checkpoint,
                            "before_operator_authority_bind",
                            StringComparison.Ordinal))
                    {
                        return;
                    }
                    replacementLoginInjected = true;
                    saleRaceOperator.SetUserForTesting(
                        replacementOperator);
                    Require(
                        PosOnlineSyncRevocationLatch
                            .TryCaptureAuthorizationEpoch(
                                out replacementLoginEpoch),
                        "replacement login epoch was not captured");
                });
            LoginResult staleLoginResult;
            try
            {
                staleLoginResult = await saleRaceOperator
                    .LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true);
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                PosOnlineSyncRevocationLatch
                    .TryCaptureAuthorizationEpoch(
                        out var epochAfterStaleLoginCleanup),
                "post-cleanup authorization epoch was not captured");
            Require(
                replacementLoginInjected &&
                staleLoginResult == LoginResult.AuthorizationExpired &&
                saleRaceOperator.IsLoggedIn &&
                ReferenceEquals(
                    saleRaceOperator.CurrentUser,
                    replacementOperator) &&
                replacementLoginEpoch == epochAfterStaleLoginCleanup,
                "stale login cleanup cleared or invalidated replacement operator authority");
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "sale-race login after replacement race did not authenticate");
            var staleDenialOperatorAuthorityVersion =
                (long)operatorAuthorityVersionField.GetValue(
                    saleRaceOperator);
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "replacement authority login did not authenticate");
            Require(
                PosOnlineSyncRevocationLatch
                    .TryCaptureAuthorizationEpoch(
                        out var epochBeforeStaleDenial),
                "pre-denial authorization epoch was not captured");
            const string staleDenialAuditCode =
                "qa_stale_authority_denial";
            var staleDenialAuditBefore =
                CountAuthorizationAuditRows(
                    factory,
                    staleDenialAuditCode,
                    requireUnattributed: true);
            saleRaceOperator.HandleAuthorizationUseDenied(
                staleDenialAuditCode,
                staleDenialOperatorAuthorityVersion);
            Require(
                PosOnlineSyncRevocationLatch
                    .TryCaptureAuthorizationEpoch(
                        out var epochAfterStaleDenial),
                "post-denial authorization epoch was not captured");
            var saleRacePermissions = new PermissionService(
                saleRaceOperator);
            saleRacePermissions.Demand(
                PermissionCodes.PosPay,
                "qa replacement authority precheck");
            Require(
                saleRaceOperator.IsLoggedIn &&
                epochBeforeStaleDenial == epochAfterStaleDenial &&
                await WaitForAuthorizationAuditIncrementAsync(
                    factory,
                    staleDenialAuditCode,
                    staleDenialAuditBefore,
                    requireUnattributed: true).ConfigureAwait(true),
                "stale authorization denial mutated replacement authority or lost its unattributed audit");
            var concurrentReplacementAdmin = new UserAccount
            {
                Id = 900005,
                Username = "qa-concurrent-replacement-admin",
                DisplayName = "QA Concurrent Replacement Admin",
                IsActive = true,
                RoleCode = "admin",
                RoleName = "Administrator",
                PermissionCodes = new List<string>()
            };
            var permissionReplacementInjected = false;
            long permissionReplacementEpoch = long.MinValue;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (permissionReplacementInjected ||
                        !string.Equals(
                            checkpoint,
                            "after_authorization_valid_before_operator_capture",
                            StringComparison.Ordinal))
                    {
                        return;
                    }
                    permissionReplacementInjected = true;
                    saleRaceOperator.SetUserForTesting(
                        concurrentReplacementAdmin);
                    Require(
                        PosOnlineSyncRevocationLatch
                            .TryCaptureAuthorizationEpoch(
                                out permissionReplacementEpoch),
                        "permission replacement epoch was not captured");
                });
            bool replacementAdminReceivedUsersManage;
            try
            {
                replacementAdminReceivedUsersManage =
                    saleRacePermissions.Has(
                        PermissionCodes.UsersManage);
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                PosOnlineSyncRevocationLatch
                    .TryCaptureAuthorizationEpoch(
                        out var epochAfterPermissionRace),
                "post-permission-race epoch was not captured");
            Require(
                permissionReplacementInjected &&
                !replacementAdminReceivedUsersManage &&
                ReferenceEquals(
                    saleRaceOperator.CurrentUser,
                    concurrentReplacementAdmin) &&
                permissionReplacementEpoch ==
                    epochAfterPermissionRace,
                "permission evaluation crossed into a concurrently replaced admin authority");
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "sale-race login after permission replacement did not authenticate");
            saleRacePermissions.Demand(
                PermissionCodes.PosPay,
                "qa permission-race recovery precheck");
            var permissionRevocationInjected = false;
            long permissionRevocationEpoch = long.MinValue;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (permissionRevocationInjected ||
                        !string.Equals(
                            checkpoint,
                            "after_authorization_valid_before_operator_capture",
                            StringComparison.Ordinal))
                    {
                        return;
                    }
                    permissionRevocationInjected = true;
                    PosOnlineSyncRevocationLatch
                        .InvalidateAuthorizationState();
                    Require(
                        PosOnlineSyncRevocationLatch
                            .TryCaptureAuthorizationEpoch(
                                out permissionRevocationEpoch),
                        "permission revocation epoch was not captured");
                });
            bool permissionSurvivedConcurrentRevocation;
            try
            {
                permissionSurvivedConcurrentRevocation =
                    saleRacePermissions.Has(
                        PermissionCodes.PosPay);
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                PosOnlineSyncRevocationLatch
                    .TryCaptureAuthorizationEpoch(
                        out var epochAfterPermissionRevocation),
                "post-permission-revocation epoch was not captured");
            Require(
                permissionRevocationInjected &&
                !permissionSurvivedConcurrentRevocation &&
                permissionRevocationEpoch ==
                    epochAfterPermissionRevocation,
                "permission snapshot crossed a completed authorization revocation");
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "sale-race login after permission revocation did not authenticate");
            saleRacePermissions.Demand(
                PermissionCodes.PosPay,
                "qa permission-revocation recovery precheck");
            var workflow = new PosWorkflowService();
            await workflow.InitializeAsync().ConfigureAwait(true);
            await workflow.AddManualPriceAsync(100).ConfigureAwait(true);
            var localRecoverySalesBefore = CountSaleRows(factory);
            var localRecoveryOutboxBefore =
                CountSalesOutboxRows(factory);
            Require(
                await saleRaceOperator.LoginLocalRecoveryAsync(
                        RecoveryUsername,
                        RecoveryPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "local recovery control login did not authenticate");
            var localRecoverySaleDenied = false;
            try
            {
                await workflow.CompleteSaleAsync(
                        new PosPaymentInfo
                        {
                            CashAmountMinor = 100
                        },
                        saleRaceOperator,
                        "QA-LOCAL-RECOVERY-DENIED")
                    .ConfigureAwait(true);
            }
            catch (PosAuthorizationLeaseException ex)
            {
                localRecoverySaleDenied = string.Equals(
                    ex.Code,
                    "sync_generation_inactive",
                    StringComparison.Ordinal);
            }
            Require(
                localRecoverySaleDenied &&
                CountSaleRows(factory) == localRecoverySalesBefore &&
                CountSalesOutboxRows(factory) ==
                    localRecoveryOutboxBefore,
                "local recovery inherited cached POS sale authority");
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "sale-race login after local recovery did not authenticate");
            saleRacePermissions.Demand(
                PermissionCodes.PosPay,
                "qa sale-race precheck");

            var workflowGate = (SemaphoreSlim)typeof(PosWorkflowService)
                .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(workflow);
            Require(workflowGate != null, "sale-race workflow gate was not found");
            var authorizationUseGate = (SemaphoreSlim)typeof(
                    PosOnlineSyncRevocationLatch)
                .GetField(
                    "AuthorizationUseGate",
                    BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null);
            Require(
                authorizationUseGate != null,
                "authorization-use gate was not found");
            var beginDenialCallbackObservedReleasedGates = false;
            Action throwingBeginDenialSubscriber = () =>
            {
                var workflowGateReleased = workflowGate.Wait(0);
                if (workflowGateReleased)
                    workflowGate.Release();
                var authorizationUseGateReleased =
                    authorizationUseGate.Wait(0);
                if (authorizationUseGateReleased)
                    authorizationUseGate.Release();
                beginDenialCallbackObservedReleasedGates =
                    workflowGateReleased &&
                    authorizationUseGateReleased;
                throw new InvalidOperationException(
                    "qa_begin_denial_subscriber_failure");
            };
            saleRaceOperator.SessionChanged +=
                throwingBeginDenialSubscriber;
            var raceSalesBefore = CountSaleRows(factory);
            var raceOutboxBefore = CountSalesOutboxRows(factory);
            var expiryAuditBefore = CountAuthorizationAuditRows(
                factory,
                "offline_lease_expired",
                requireUnattributed: false);
            await workflowGate.WaitAsync().ConfigureAwait(true);
            Task<PosSaleResult> expiryRaceTask = null;
            try
            {
                expiryRaceTask = workflow.CompleteSaleAsync(
                    new PosPaymentInfo { CashAmountMinor = 100 },
                    saleRaceOperator,
                    "QA-AUTH-RACE-001");
                await Task.Delay(50).ConfigureAwait(true);
                Require(
                    !expiryRaceTask.IsCompleted,
                    "sale-race task did not pause after the UI precheck");
                var saleRaceServerAt = ParseUtc(
                    saleRaceResponse.ServerTime,
                    "sale-race server receipt");
                var saleRaceExpiry = ParseUtc(
                    saleRaceResponse.EffectiveOfflineAuthorizationExpiresAt,
                    "sale-race authoritative expiry");
                Interlocked.Exchange(
                    ref saleRaceTicks,
                    (saleRaceExpiry - saleRaceServerAt).Ticks);
            }
            finally
            {
                workflowGate.Release();
            }
            Require(expiryRaceTask != null, "expiry-race task was not created");
            var expiryRaceDenied = false;
            try
            {
                await expiryRaceTask.ConfigureAwait(true);
            }
            catch (PosAuthorizationLeaseException ex)
            {
                expiryRaceDenied = string.Equals(
                    ex.Code,
                    "offline_lease_expired",
                    StringComparison.Ordinal);
            }
            finally
            {
                saleRaceOperator.SessionChanged -=
                    throwingBeginDenialSubscriber;
            }
            Require(
                expiryRaceDenied &&
                beginDenialCallbackObservedReleasedGates &&
                CountSaleRows(factory) == raceSalesBefore &&
                CountSalesOutboxRows(factory) == raceOutboxBefore &&
                await WaitForAuthorizationAuditIncrementAsync(
                    factory,
                    "offline_lease_expired",
                    expiryAuditBefore,
                    requireUnattributed: false).ConfigureAwait(true),
                "expiry race reached the sale or outbox transaction");

            var revocationRaceResponse =
                BuildResponse(includeOfflineAttestation: true);
            saleRaceStore.SaveFirstLogin(
                revocationRaceResponse,
                "qa-auth-sale-race-revocation");
            Require(
                saleRaceStore.TryRead(out var revocationRaceSession),
                "revocation-race trusted session was not persisted");
            saleRaceWall = ParseUtc(
                    revocationRaceSession.LastOkLocalAt,
                    "revocation-race local receipt")
                .AddMilliseconds(50);
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "revocation-race control login did not authenticate");
            saleRacePermissions.Demand(
                PermissionCodes.PosPay,
                "qa revocation-race precheck");
            var revocationDemandCount = 0;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, demand) =>
                {
                    if (string.Equals(
                            checkpoint,
                            "before_demand",
                            StringComparison.Ordinal))
                    {
                        Volatile.Write(
                            ref revocationDemandCount,
                            demand);
                        if (demand == 4)
                        {
                            PosOnlineSyncRevocationLatch
                                .InvalidateAuthorizationState();
                        }
                    }
                });
            var denialCallbackObservedReleasedGates = false;
            Action throwingDenialSubscriber = () =>
            {
                var workflowGateReleased = workflowGate.Wait(0);
                if (workflowGateReleased)
                    workflowGate.Release();
                var authorizationUseGateReleased =
                    authorizationUseGate.Wait(0);
                if (authorizationUseGateReleased)
                    authorizationUseGate.Release();
                denialCallbackObservedReleasedGates =
                    workflowGateReleased &&
                    authorizationUseGateReleased;
                throw new InvalidOperationException(
                    "qa_denial_subscriber_failure");
            };
            saleRaceOperator.SessionChanged +=
                throwingDenialSubscriber;
            var revocationAuditBefore = CountAuthorizationAuditRows(
                factory,
                "sync_generation_inactive",
                requireUnattributed: false);
            var revocationRaceDenialCode = string.Empty;
            try
            {
                await workflow.CompleteSaleAsync(
                        new PosPaymentInfo { CashAmountMinor = 100 },
                        saleRaceOperator,
                        "QA-AUTH-RACE-001")
                    .ConfigureAwait(true);
            }
            catch (PosAuthorizationLeaseException ex)
            {
                revocationRaceDenialCode =
                    ex.Code ?? string.Empty;
            }
            finally
            {
                saleRaceOperator.SessionChanged -=
                    throwingDenialSubscriber;
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                string.Equals(
                    revocationRaceDenialCode,
                    "sync_generation_inactive",
                    StringComparison.Ordinal),
                "pre-COMMIT revocation denial code was " +
                (string.IsNullOrWhiteSpace(
                    revocationRaceDenialCode)
                    ? "<none>"
                    : revocationRaceDenialCode));
            Require(
                Volatile.Read(ref revocationDemandCount) == 4,
                "pre-COMMIT revocation demand count was " +
                Volatile.Read(ref revocationDemandCount).ToString(
                    CultureInfo.InvariantCulture));
            var revocationSaleRows = CountSaleRows(factory);
            var revocationOutboxRows =
                CountSalesOutboxRows(factory);
            Require(
                revocationSaleRows == raceSalesBefore &&
                revocationOutboxRows == raceOutboxBefore,
                "pre-COMMIT revocation persisted rows: sales=" +
                revocationSaleRows.ToString(
                    CultureInfo.InvariantCulture) +
                "; outbox=" +
                revocationOutboxRows.ToString(
                    CultureInfo.InvariantCulture));
            Require(
                denialCallbackObservedReleasedGates &&
                await WaitForAuthorizationAuditIncrementAsync(
                    factory,
                    "sync_generation_inactive",
                    revocationAuditBefore,
                    requireUnattributed: false).ConfigureAwait(true),
                "pre-COMMIT revocation callback or audit evidence was missing");

            var generationRaceResponse =
                BuildResponse(includeOfflineAttestation: true);
            saleRaceStore.SaveFirstLogin(
                generationRaceResponse,
                "qa-auth-sale-race-generation-source");
            Require(
                saleRaceStore.TryRead(out var generationRaceSession),
                "generation-race trusted session was not persisted");
            saleRaceWall = ParseUtc(
                    generationRaceSession.LastOkLocalAt,
                    "generation-race local receipt")
                .AddMilliseconds(50);
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "generation-race control login did not authenticate");
            var switchedGenerationResponse =
                BuildResponse(includeOfflineAttestation: true);
            var generationDemandCount = 0;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, demand) =>
                {
                    if (string.Equals(
                            checkpoint,
                            "before_demand",
                            StringComparison.Ordinal))
                    {
                        Volatile.Write(
                            ref generationDemandCount,
                            demand);
                        if (demand == 4)
                        {
                            saleRaceStore.SaveFirstLogin(
                                switchedGenerationResponse,
                                "qa-auth-sale-race-generation-target");
                            Require(
                                saleRaceStore.TryRead(
                                    out var switchedGenerationSession),
                                "generation-race target session was not persisted");
                            saleRaceWall = ParseUtc(
                                    switchedGenerationSession.LastOkLocalAt,
                                    "generation-race target local receipt")
                                .AddMilliseconds(50);
                        }
                    }
                });
            var generationRaceDenialCode = string.Empty;
            try
            {
                await workflow.CompleteSaleAsync(
                        new PosPaymentInfo { CashAmountMinor = 100 },
                        saleRaceOperator,
                        "QA-AUTH-RACE-001")
                    .ConfigureAwait(true);
            }
            catch (PosAuthorizationLeaseException ex)
            {
                generationRaceDenialCode = ex.Code ?? string.Empty;
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                string.Equals(
                    generationRaceDenialCode,
                    "sync_generation_inactive",
                    StringComparison.Ordinal),
                "in-transaction generation switch denial code was " +
                (string.IsNullOrWhiteSpace(generationRaceDenialCode)
                    ? "<none>"
                    : generationRaceDenialCode));
            Require(
                Volatile.Read(ref generationDemandCount) == 4,
                "in-transaction generation switch demand count was " +
                Volatile.Read(ref generationDemandCount).ToString(
                    CultureInfo.InvariantCulture));
            var generationSaleRows = CountSaleRows(factory);
            var generationOutboxRows =
                CountSalesOutboxRows(factory);
            Require(
                generationSaleRows == raceSalesBefore &&
                generationOutboxRows == raceOutboxBefore,
                "in-transaction generation switch persisted rows: sales=" +
                generationSaleRows.ToString(
                    CultureInfo.InvariantCulture) +
                "; outbox=" +
                generationOutboxRows.ToString(
                    CultureInfo.InvariantCulture));

            var commitExpiryResponse =
                BuildResponse(includeOfflineAttestation: true);
            saleRaceStore.SaveFirstLogin(
                commitExpiryResponse,
                "qa-auth-sale-commit-expiry");
            Require(
                saleRaceStore.TryRead(out var commitExpirySession),
                "commit-expiry trusted session was not persisted");
            saleRaceWall = ParseUtc(
                commitExpirySession.LastOkLocalAt,
                "commit-expiry local receipt");
            var commitExpiryAnchorTicks =
                Interlocked.Read(ref saleRaceTicks);
            var commitExpiryElapsedTicks =
                (ParseUtc(
                    commitExpirySession
                        .EffectiveOfflineAuthorizationExpiresAt,
                    "commit-expiry authoritative expiry") -
                 ParseUtc(
                    commitExpirySession.LastOkServerAt,
                    "commit-expiry server receipt")).Ticks;
            Require(
                commitExpiryElapsedTicks > 0,
                "commit-expiry window was not positive");
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "commit-expiry login did not authenticate");
            var commitExpiryDemandCount = 0;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, demand) =>
                {
                    if (string.Equals(
                            checkpoint,
                            "before_demand",
                            StringComparison.Ordinal))
                    {
                        Volatile.Write(
                            ref commitExpiryDemandCount,
                            demand);
                    }
                    if (string.Equals(
                            checkpoint,
                            "inside_commit_gate",
                            StringComparison.Ordinal))
                    {
                        Interlocked.Exchange(
                            ref saleRaceTicks,
                            commitExpiryAnchorTicks +
                            commitExpiryElapsedTicks);
                    }
                });
            var commitExpiryDenialCode = string.Empty;
            try
            {
                await workflow.CompleteSaleAsync(
                        new PosPaymentInfo { CashAmountMinor = 100 },
                        saleRaceOperator,
                        "QA-AUTH-RACE-001")
                    .ConfigureAwait(true);
            }
            catch (PosAuthorizationLeaseException ex)
            {
                commitExpiryDenialCode = ex.Code ?? string.Empty;
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                string.Equals(
                    commitExpiryDenialCode,
                    "offline_lease_expired",
                    StringComparison.Ordinal),
                "exact expiry inside the COMMIT gate denial code was " +
                (string.IsNullOrWhiteSpace(commitExpiryDenialCode)
                    ? "<none>"
                    : commitExpiryDenialCode));
            Require(
                Volatile.Read(ref commitExpiryDemandCount) == 5,
                "exact expiry inside the COMMIT gate demand count was " +
                Volatile.Read(ref commitExpiryDemandCount).ToString(
                    CultureInfo.InvariantCulture));
            var commitExpirySaleRows = CountSaleRows(factory);
            var commitExpiryOutboxRows =
                CountSalesOutboxRows(factory);
            Require(
                commitExpirySaleRows == raceSalesBefore &&
                commitExpiryOutboxRows == raceOutboxBefore,
                "exact expiry inside the COMMIT gate persisted rows: sales=" +
                commitExpirySaleRows.ToString(
                    CultureInfo.InvariantCulture) +
                "; outbox=" +
                commitExpiryOutboxRows.ToString(
                    CultureInfo.InvariantCulture));

            var blockedReaderResponse =
                BuildResponse(includeOfflineAttestation: true);
            var blockedReaderServerAt = ParseUtc(
                    commitExpirySession
                        .EffectiveOfflineAuthorizationExpiresAt,
                    "blocked-reader trusted-time floor")
                .AddTicks(commitExpiryElapsedTicks)
                .AddMinutes(1);
            ConfigureAuthoritativeWindow(
                blockedReaderResponse,
                blockedReaderServerAt,
                TimeSpan.FromMinutes(1));
            saleRaceStore.SaveFirstLogin(
                blockedReaderResponse,
                "qa-auth-sale-blocked-reader-expiry");
            Require(
                saleRaceStore.TryRead(
                    out var blockedReaderSession),
                "blocked-reader trusted session was not persisted");
            saleRaceWall = ParseUtc(
                blockedReaderSession.LastOkLocalAt,
                "blocked-reader local receipt");
            var blockedReaderAnchorTicks =
                Interlocked.Read(ref saleRaceTicks);
            var blockedReaderElapsedTicks =
                (ParseUtc(
                    blockedReaderSession
                        .EffectiveOfflineAuthorizationExpiresAt,
                    "blocked-reader authoritative expiry") -
                 ParseUtc(
                    blockedReaderSession.LastOkServerAt,
                    "blocked-reader server receipt")).Ticks;
            Require(
                blockedReaderElapsedTicks >
                    SqliteConnectionFactory
                        .DurableCommitSafetyBudget.Ticks,
                "blocked-reader authorization window lacked commit headroom");
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "blocked-reader login did not authenticate");

            var blockedWorkflow = new PosWorkflowService();
            await blockedWorkflow.InitializeAsync()
                .ConfigureAwait(true);
            await blockedWorkflow.AddManualPriceAsync(100)
                .ConfigureAwait(true);
            var blockedSalesBefore = CountSaleRows(factory);
            var blockedLinesBefore = CountSaleLineRows(factory);
            var blockedMovementsBefore =
                CountLocalStockMovementRows(factory);
            var blockedOutboxBefore =
                CountSalesOutboxRows(factory);
            var blockedStockBefore =
                CountTotalStockQuantity(factory);
            using var blockedReaderConnection = factory.Open();
            using var blockedReaderTransaction =
                blockedReaderConnection.BeginTransaction(
                    deferred: true);
            blockedReaderConnection.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM sales;",
                transaction: blockedReaderTransaction);
            using var pendingReaderProbe = new SqliteConnection(
                "Data Source=" + factory.DbPath +
                ";Default Timeout=1;Pooling=False");
            pendingReaderProbe.Open();
            var blockedReaderDemandCount = 0;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, demand) =>
                {
                    if (string.Equals(
                            checkpoint,
                            "before_demand",
                            StringComparison.Ordinal))
                    {
                        Volatile.Write(
                            ref blockedReaderDemandCount,
                            demand);
                    }
                });
            var blockedReaderDenialCode = string.Empty;
            var blockedSaleStarted =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
            Task<PosSaleResult> blockedSaleTask = null;
            try
            {
                blockedSaleTask = Task.Run(async () =>
                {
                    blockedSaleStarted.TrySetResult(true);
                    return await blockedWorkflow.CompleteSaleAsync(
                            new PosPaymentInfo
                            {
                                CashAmountMinor = 100
                            },
                            saleRaceOperator,
                            "QA-AUTH-BLOCKED-READER-001")
                        .ConfigureAwait(false);
                });
                await blockedSaleStarted.Task.ConfigureAwait(true);
                Require(
                    await WaitForPendingExclusiveLockAsync(
                            pendingReaderProbe)
                        .ConfigureAwait(true),
                    "blocked-reader sale never reached the EXCLUSIVE fence");
                Require(
                    blockedSaleTask != null &&
                    !blockedSaleTask.IsCompleted,
                    "blocked-reader sale did not remain pending during reader drain");
                Require(
                    Volatile.Read(
                        ref blockedReaderDemandCount) == 2,
                    "reader-drain demand count was " +
                    Volatile.Read(
                        ref blockedReaderDemandCount).ToString(
                            CultureInfo.InvariantCulture));

                Interlocked.Exchange(
                    ref saleRaceTicks,
                    blockedReaderAnchorTicks +
                    blockedReaderElapsedTicks);
                blockedReaderTransaction.Dispose();
                await blockedSaleTask.ConfigureAwait(true);
            }
            catch (PosAuthorizationLeaseException ex)
            {
                blockedReaderDenialCode = ex.Code ?? string.Empty;
            }
            finally
            {
                blockedReaderTransaction.Dispose();
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }

            using (var fenceReleaseProbe = factory.Open())
            {
                Require(
                    string.Equals(
                        fenceReleaseProbe.ExecuteScalar<string>(
                            "PRAGMA main.locking_mode;"),
                        "normal",
                        StringComparison.OrdinalIgnoreCase),
                    "sale commit fence did not restore NORMAL locking mode");
            }
            Require(
                string.Equals(
                    blockedReaderDenialCode,
                    "offline_lease_expired",
                    StringComparison.Ordinal),
                "reader-delayed expiry denial code was " +
                (string.IsNullOrWhiteSpace(blockedReaderDenialCode)
                    ? "<none>"
                    : blockedReaderDenialCode));
            var blockedSalesAfter = CountSaleRows(factory);
            var blockedLinesAfter = CountSaleLineRows(factory);
            var blockedMovementsAfter =
                CountLocalStockMovementRows(factory);
            var blockedOutboxAfter =
                CountSalesOutboxRows(factory);
            var blockedStockAfter =
                CountTotalStockQuantity(factory);
            Require(
                blockedSalesAfter == blockedSalesBefore &&
                blockedLinesAfter == blockedLinesBefore &&
                blockedMovementsAfter ==
                    blockedMovementsBefore &&
                blockedOutboxAfter ==
                    blockedOutboxBefore &&
                blockedStockAfter ==
                    blockedStockBefore,
                "reader-delayed expiry persisted rows: sales=" +
                blockedSalesAfter.ToString(
                    CultureInfo.InvariantCulture) +
                "; lines=" +
                blockedLinesAfter.ToString(
                    CultureInfo.InvariantCulture) +
                "; movements=" +
                blockedMovementsAfter.ToString(
                    CultureInfo.InvariantCulture) +
                "; outbox=" +
                blockedOutboxAfter.ToString(
                    CultureInfo.InvariantCulture) +
                "; stock=" +
                blockedStockAfter.ToString(
                    CultureInfo.InvariantCulture));
            pendingReaderProbe.Dispose();
            blockedReaderConnection.Dispose();

            var linearizationResponse =
                BuildResponse(includeOfflineAttestation: true);
            var linearizationServerAt =
                ParseUtc(
                    blockedReaderSession
                        .EffectiveOfflineAuthorizationExpiresAt,
                    "linearization trusted-time floor")
                .AddMinutes(1);
            ConfigureAuthoritativeWindow(
                linearizationResponse,
                linearizationServerAt,
                TimeSpan.FromHours(4));
            saleRaceStore.SaveFirstLogin(
                linearizationResponse,
                "qa-auth-sale-commit-linearization");
            Require(
                saleRaceStore.TryRead(out var linearizationSession),
                "commit-linearization trusted session was not persisted");
            saleRaceWall = ParseUtc(
                    linearizationSession.LastOkLocalAt,
                    "commit-linearization local receipt")
                .AddMilliseconds(50);
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "commit-linearization login did not authenticate");
            var linearizationWorkflow = new PosWorkflowService();
            await linearizationWorkflow.InitializeAsync()
                .ConfigureAwait(true);
            await linearizationWorkflow.AddManualPriceAsync(100)
                .ConfigureAwait(true);
            using var revocationStarted = new ManualResetEventSlim(false);
            using var revocationReturned = new ManualResetEventSlim(false);
            Task postValidationRevocation = null;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (!string.Equals(
                            checkpoint,
                            "inside_commit_gate",
                            StringComparison.Ordinal))
                    {
                        return;
                    }
                    postValidationRevocation = Task.Run(() =>
                    {
                        revocationStarted.Set();
                        PosOnlineSyncRevocationLatch
                            .InvalidateAuthorizationState();
                        revocationReturned.Set();
                    });
                    Require(
                        revocationStarted.Wait(TimeSpan.FromSeconds(5)),
                        "post-validation revocation did not start");
                    Require(
                        !revocationReturned.Wait(
                            TimeSpan.FromMilliseconds(100)),
                        "revocation returned before the guarded COMMIT");
                });
            PosSaleResult linearizedSale;
            try
            {
                linearizedSale =
                    await linearizationWorkflow.CompleteSaleAsync(
                            new PosPaymentInfo
                            {
                                CashAmountMinor = 100
                            },
                            saleRaceOperator,
                            "QA-AUTH-LINEARIZED-001")
                        .ConfigureAwait(true);
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
                if (postValidationRevocation != null)
                {
                    await postValidationRevocation.ConfigureAwait(true);
                }
            }
            Require(
                linearizedSale.SaleId > 0 &&
                revocationReturned.IsSet &&
                CountSaleRows(factory) == raceSalesBefore + 1 &&
                CountSalesOutboxRows(factory) == raceOutboxBefore + 1,
                "guarded COMMIT was not linearized before revocation return");
            raceSalesBefore = CountSaleRows(factory);
            raceOutboxBefore = CountSalesOutboxRows(factory);

            var retryRaceResponse =
                BuildResponse(includeOfflineAttestation: true);
            var retryRaceServerAt =
                linearizationServerAt.AddMinutes(1);
            ConfigureAuthoritativeWindow(
                retryRaceResponse,
                retryRaceServerAt,
                TimeSpan.FromHours(4));
            saleRaceStore.SaveFirstLogin(
                retryRaceResponse,
                "qa-auth-sale-race-retry");
            Require(
                saleRaceStore.TryRead(out var retryRaceSession),
                "sale-race retry trusted session was not persisted");
            saleRaceWall = ParseUtc(
                    retryRaceSession.LastOkLocalAt,
                    "sale-race retry local receipt")
                .AddMilliseconds(50);
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "sale-race retry login did not authenticate");
            var fixedSaleCreatedAt =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var frozenReceiptShop = new ReceiptShopInfo
            {
                Name = "QA Authorization Shop",
                ShopCode = ShopCode,
                ShopStatus = "active",
                Source = "qa_authorization_harness",
                SyncedAtUtc = DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture)
            };
            var retryResult = await workflow.CompleteSaleAsync(
                    new PosPaymentInfo { CashAmountMinor = 100 },
                    saleRaceOperator,
                    "QA-AUTH-RACE-001",
                    fixedSaleCreatedAt,
                    frozenReceiptShop)
                .ConfigureAwait(true);
            Require(
                retryResult.SaleId > 0 &&
                CountSaleRows(factory) == raceSalesBefore + 1 &&
                CountSalesOutboxRows(factory) == raceOutboxBefore + 1,
                "authorized retry did not persist exactly one sale/outbox");

            var retryWorkflow = new PosWorkflowService();
            await retryWorkflow.InitializeAsync().ConfigureAwait(true);
            await retryWorkflow.AddManualPriceAsync(100).ConfigureAwait(true);
            var replayResult = await retryWorkflow.CompleteSaleAsync(
                    new PosPaymentInfo { CashAmountMinor = 100 },
                    saleRaceOperator,
                    "QA-AUTH-RACE-001",
                    fixedSaleCreatedAt,
                    frozenReceiptShop)
                .ConfigureAwait(true);
            Require(
                replayResult.SaleId == retryResult.SaleId &&
                CountSaleRows(factory) == raceSalesBefore + 1 &&
                CountSalesOutboxRows(factory) == raceOutboxBefore + 1,
                "exact sale retry duplicated durable side effects");

            var ambiguousSalesBefore = CountSaleRows(factory);
            var ambiguousOutboxBefore =
                CountSalesOutboxRows(factory);
            var ambiguousWorkflow = new PosWorkflowService();
            await ambiguousWorkflow.InitializeAsync()
                .ConfigureAwait(true);
            await ambiguousWorkflow.AddManualPriceAsync(100)
                .ConfigureAwait(true);
            const string ambiguousOriginalCode =
                "QA-AUTH-AMBIGUOUS-ORIGINAL";
            var ambiguousCreatedAt = fixedSaleCreatedAt + 10;
            var injectedPostCommitFailure = false;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (!injectedPostCommitFailure &&
                        string.Equals(
                            checkpoint,
                            "after_commit_before_return",
                            StringComparison.Ordinal))
                    {
                        injectedPostCommitFailure = true;
                        throw new InvalidOperationException(
                            "qa_ambiguous_commit_result");
                    }
                });
            var ambiguousResultLost = false;
            try
            {
                await ambiguousWorkflow.CompleteSaleAsync(
                        new PosPaymentInfo
                        {
                            CashAmountMinor = 100
                        },
                        saleRaceOperator,
                        ambiguousOriginalCode,
                        ambiguousCreatedAt,
                        frozenReceiptShop)
                    .ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                ambiguousResultLost = string.Equals(
                    ex.Message,
                    "qa_ambiguous_commit_result",
                    StringComparison.Ordinal);
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                ambiguousResultLost &&
                injectedPostCommitFailure &&
                CountSaleRows(factory) == ambiguousSalesBefore + 1 &&
                CountSalesOutboxRows(factory) ==
                    ambiguousOutboxBefore + 1,
                "ambiguous COMMIT result did not preserve one durable sale");
            var ambiguousRetryResult =
                await ambiguousWorkflow.CompleteSaleAsync(
                        new PosPaymentInfo
                        {
                            CashAmountMinor = 100
                        },
                        saleRaceOperator,
                        "QA-AUTH-AMBIGUOUS-REPLACEMENT",
                        ambiguousCreatedAt + 1,
                        frozenReceiptShop)
                    .ConfigureAwait(true);
            Require(
                ambiguousRetryResult.SaleId > 0 &&
                string.Equals(
                    ambiguousRetryResult.SaleCode,
                    ambiguousOriginalCode,
                    StringComparison.Ordinal) &&
                ambiguousRetryResult.CreatedAtMs ==
                    ambiguousCreatedAt &&
                CountSaleRows(factory) == ambiguousSalesBefore + 1 &&
                CountSalesOutboxRows(factory) ==
                    ambiguousOutboxBefore + 1,
                "production retry replaced the pending sale identity");

            var mutationRetrySalesBefore =
                CountSaleRows(factory);
            var mutationRetryOutboxBefore =
                CountSalesOutboxRows(factory);
            await ambiguousWorkflow.AddManualPriceAsync(100)
                .ConfigureAwait(true);
            const string mutationOriginalCode =
                "QA-AUTH-AMBIGUOUS-MUTATION-ORIGINAL";
            var mutationOriginalCreatedAt =
                ambiguousCreatedAt + 10;
            var mutationFailureInjected = false;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (!mutationFailureInjected &&
                        string.Equals(
                            checkpoint,
                            "after_commit_before_return",
                            StringComparison.Ordinal))
                    {
                        mutationFailureInjected = true;
                        throw new InvalidOperationException(
                            "qa_ambiguous_mutation_result");
                    }
                });
            var mutationResultLost = false;
            try
            {
                await ambiguousWorkflow.CompleteSaleAsync(
                        new PosPaymentInfo
                        {
                            CashAmountMinor = 100
                        },
                        saleRaceOperator,
                        mutationOriginalCode,
                        mutationOriginalCreatedAt,
                        frozenReceiptShop)
                    .ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                mutationResultLost = string.Equals(
                    ex.Message,
                    "qa_ambiguous_mutation_result",
                    StringComparison.Ordinal);
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                mutationResultLost &&
                mutationFailureInjected &&
                CountSaleRows(factory) ==
                    mutationRetrySalesBefore + 1 &&
                CountSalesOutboxRows(factory) ==
                    mutationRetryOutboxBefore + 1,
                "ambiguous mutation setup did not preserve one durable sale");
            await ambiguousWorkflow.ClearCartAsync()
                .ConfigureAwait(true);
            await ambiguousWorkflow.AddManualPriceAsync(100)
                .ConfigureAwait(true);
            const string mutationReplacementCode =
                "QA-AUTH-AMBIGUOUS-MUTATION-NEW";
            var mutationReplacementResult =
                await ambiguousWorkflow.CompleteSaleAsync(
                        new PosPaymentInfo
                        {
                            CashAmountMinor = 100
                        },
                        saleRaceOperator,
                        mutationReplacementCode,
                        mutationOriginalCreatedAt + 1,
                        frozenReceiptShop)
                    .ConfigureAwait(true);
            Require(
                mutationReplacementResult.SaleId > 0 &&
                string.Equals(
                    mutationReplacementResult.SaleCode,
                    mutationReplacementCode,
                    StringComparison.Ordinal) &&
                CountSaleRows(factory) ==
                    mutationRetrySalesBefore + 2 &&
                CountSalesOutboxRows(factory) ==
                    mutationRetryOutboxBefore + 2,
                "mutated and recreated cart reused a stale pending sale identity");

            var authorityMismatchSalesBefore =
                CountSaleRows(factory);
            var authorityMismatchOutboxBefore =
                CountSalesOutboxRows(factory);
            var authorityMismatchWorkflow =
                new PosWorkflowService();
            await authorityMismatchWorkflow.InitializeAsync()
                .ConfigureAwait(true);
            await authorityMismatchWorkflow
                .AddManualPriceAsync(100)
                .ConfigureAwait(true);
            var authorityMismatchFailureInjected = false;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, _) =>
                {
                    if (!authorityMismatchFailureInjected &&
                        string.Equals(
                            checkpoint,
                            "after_commit_before_return",
                            StringComparison.Ordinal))
                    {
                        authorityMismatchFailureInjected = true;
                        throw new InvalidOperationException(
                            "qa_ambiguous_authority_result");
                    }
                });
            var authorityMismatchResultLost = false;
            try
            {
                await authorityMismatchWorkflow.CompleteSaleAsync(
                        new PosPaymentInfo
                        {
                            CashAmountMinor = 100
                        },
                        saleRaceOperator,
                        "QA-AUTH-AMBIGUOUS-AUTHORITY",
                        mutationOriginalCreatedAt + 20,
                        frozenReceiptShop)
                    .ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                authorityMismatchResultLost = string.Equals(
                    ex.Message,
                    "qa_ambiguous_authority_result",
                    StringComparison.Ordinal);
            }
            finally
            {
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                authorityMismatchResultLost &&
                authorityMismatchFailureInjected &&
                CountSaleRows(factory) ==
                    authorityMismatchSalesBefore + 1 &&
                CountSalesOutboxRows(factory) ==
                    authorityMismatchOutboxBefore + 1,
                "ambiguous authority setup did not preserve one durable sale");
            var authorityReplacementResponse =
                BuildResponse(includeOfflineAttestation: true);
            ConfigureAuthoritativeWindow(
                authorityReplacementResponse,
                retryRaceServerAt.AddMinutes(1),
                TimeSpan.FromHours(4));
            saleRaceStore.SaveFirstLogin(
                authorityReplacementResponse,
                "qa-auth-sale-pending-authority-replacement");
            Require(
                saleRaceStore.TryRead(
                    out var authorityReplacementSession),
                "pending authority replacement session was not persisted");
            saleRaceWall = ParseUtc(
                    authorityReplacementSession.LastOkLocalAt,
                    "pending authority replacement local receipt")
                .AddMilliseconds(50);
            Require(
                await saleRaceOperator.LoginAsync(username, CorrectPin)
                    .ConfigureAwait(true) == LoginResult.Success,
                "pending authority replacement login did not authenticate");
            var authorityMismatchDenied = false;
            try
            {
                await authorityMismatchWorkflow.CompleteSaleAsync(
                        new PosPaymentInfo
                        {
                            CashAmountMinor = 100
                        },
                        saleRaceOperator,
                        "QA-AUTH-AMBIGUOUS-AUTHORITY-REPLACEMENT",
                        mutationOriginalCreatedAt + 21,
                        frozenReceiptShop)
                    .ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                authorityMismatchDenied =
                    ex.Message.IndexOf(
                        "riconciliazione",
                        StringComparison.OrdinalIgnoreCase) >= 0;
            }
            Require(
                authorityMismatchDenied &&
                CountSaleRows(factory) ==
                    authorityMismatchSalesBefore + 1 &&
                CountSalesOutboxRows(factory) ==
                    authorityMismatchOutboxBefore + 1,
                "pending sale identity crossed an authorization generation");

            Task<IDisposable> queuedAuthorizationProbe = null;
            var firstAuthorizationProbe =
                await PosOnlineSyncRevocationLatch
                    .EnterAuthorizationUseAsync()
                    .ConfigureAwait(true);
            try
            {
                queuedAuthorizationProbe =
                    PosOnlineSyncRevocationLatch
                        .EnterAuthorizationUseAsync();
                Require(
                    !queuedAuthorizationProbe.IsCompleted,
                    "second direct authorization lease was not queued");
            }
            finally
            {
                firstAuthorizationProbe.Dispose();
            }
            Require(
                queuedAuthorizationProbe != null,
                "second direct authorization lease was not created");
            using (await queuedAuthorizationProbe.ConfigureAwait(true))
            {
            }

            var concurrentSalesBefore = CountSaleRows(factory);
            var concurrentOutboxBefore = CountSalesOutboxRows(factory);
            var concurrentWorkflowA = new PosWorkflowService();
            var concurrentWorkflowB = new PosWorkflowService();
            await Task.WhenAll(
                    concurrentWorkflowA.InitializeAsync(),
                    concurrentWorkflowB.InitializeAsync())
                .ConfigureAwait(true);
            await Task.WhenAll(
                    concurrentWorkflowA.AddManualPriceAsync(100),
                    concurrentWorkflowB.AddManualPriceAsync(100))
                .ConfigureAwait(true);
            using var firstSaleHasLease =
                new ManualResetEventSlim(false);
            using var releaseFirstSale =
                new ManualResetEventSlim(false);
            using var secondSaleHasLease =
                new ManualResetEventSlim(false);
            using var secondSaleReachedAuthorizationGate =
                new ManualResetEventSlim(false);
            using var secondSaleCallStarted =
                new ManualResetEventSlim(false);
            var authorizationLeaseEntries = 0;
            var authorizationGateArrivals = 0;
            saleRaceOperator.SetAuthorizationUseTestHookForTesting(
                (checkpoint, demand) =>
                {
                    if (string.Equals(
                            checkpoint,
                            "before_authorization_use_gate",
                            StringComparison.Ordinal))
                    {
                        if (Interlocked.Increment(
                                ref authorizationGateArrivals) == 2)
                        {
                            secondSaleReachedAuthorizationGate.Set();
                        }
                        return;
                    }
                    if (!string.Equals(
                            checkpoint,
                            "before_demand",
                            StringComparison.Ordinal) ||
                        demand != 1)
                    {
                        return;
                    }
                    var entry = Interlocked.Increment(
                        ref authorizationLeaseEntries);
                    if (entry == 1)
                    {
                        firstSaleHasLease.Set();
                        Require(
                            releaseFirstSale.Wait(
                                TimeSpan.FromSeconds(5)),
                            "first concurrent sale was not released");
                    }
                    else if (entry == 2)
                    {
                        secondSaleHasLease.Set();
                    }
                });
            Task<PosSaleResult> concurrentSaleA = null;
            Task<PosSaleResult> concurrentSaleB = null;
            PosSaleResult[] concurrentResults = null;
            try
            {
                concurrentSaleA = Task.Run(
                    async () => await concurrentWorkflowA
                        .CompleteSaleAsync(
                            new PosPaymentInfo
                            {
                                CashAmountMinor = 100
                            },
                            saleRaceOperator,
                            "QA-AUTH-CONCURRENT-A",
                            fixedSaleCreatedAt + 1,
                            frozenReceiptShop)
                        .ConfigureAwait(false));
                Require(
                    firstSaleHasLease.Wait(TimeSpan.FromSeconds(5)),
                    "first concurrent sale did not acquire the lease");
                concurrentSaleB = Task.Run(
                    async () =>
                    {
                        secondSaleCallStarted.Set();
                        return await concurrentWorkflowB
                            .CompleteSaleAsync(
                                new PosPaymentInfo
                                {
                                    CashAmountMinor = 100
                                },
                                saleRaceOperator,
                                "QA-AUTH-CONCURRENT-B",
                                fixedSaleCreatedAt + 2,
                                frozenReceiptShop)
                            .ConfigureAwait(false);
                    });
                Require(
                    secondSaleCallStarted.Wait(TimeSpan.FromSeconds(5)),
                    "second concurrent sale did not start");
                Require(
                    secondSaleReachedAuthorizationGate.Wait(
                        TimeSpan.FromSeconds(5)),
                    "second concurrent sale did not reach the authorization-use gate");
                releaseFirstSale.Set();
                concurrentResults = await Task.WhenAll(
                        concurrentSaleA,
                        concurrentSaleB)
                    .ConfigureAwait(true);
            }
            finally
            {
                releaseFirstSale.Set();
                saleRaceOperator
                    .SetAuthorizationUseTestHookForTesting(null);
            }
            Require(
                concurrentResults != null &&
                concurrentResults[0].SaleId != concurrentResults[1].SaleId &&
                Volatile.Read(ref authorizationGateArrivals) == 2 &&
                Volatile.Read(ref authorizationLeaseEntries) == 2 &&
                secondSaleReachedAuthorizationGate.IsSet &&
                secondSaleHasLease.IsSet &&
                CountSaleRows(factory) == concurrentSalesBefore + 2 &&
                CountSalesOutboxRows(factory) == concurrentOutboxBefore + 2,
                "two concurrent authorized sales were not serialized safely");
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
                "oversizedTrustedStateDenied=True" + Environment.NewLine +
                "firstLoginLostResponseRetry=True" + Environment.NewLine +
                "heartbeatDoesNotExtendBound=True" + Environment.NewLine +
                "staleHeartbeatClockNotReset=True" + Environment.NewLine +
                "concurrentHeartbeatCas=True" + Environment.NewLine +
                "frozenClockMonotonicExpiry=True" + Environment.NewLine +
                "frozenClockUnauthorizedSaleSinkRows=0" + Environment.NewLine +
                "frozenClockUnauthorizedPublicationOutboxRows=0" + Environment.NewLine +
                "monotonicCounterRegressionDenied=True" + Environment.NewLine +
                "monotonicProviderFailureDenied=True" + Environment.NewLine +
                "invalidMonotonicFrequencyDenied=True" + Environment.NewLine +
                "monotonicElapsedOverflowDenied=True" + Environment.NewLine +
                "preflightDelayExpiryDenied=True" + Environment.NewLine +
                "activationDelayCountedFromReceipt=True" + Environment.NewLine +
                "firstUseReceiptClockExpiryDenied=True" + Environment.NewLine +
                "firstUseUnauthorizedSaleSinkRows=0" + Environment.NewLine +
                "firstUseUnauthorizedPublicationOutboxRows=0" + Environment.NewLine +
                "firstLoginRetryClockNotReset=True" + Environment.NewLine +
                "heartbeatClockNotReset=True" + Environment.NewLine +
                "betweenPreflightsExpiryDenied=True" + Environment.NewLine +
                "crossGenerationReplayG2Denied=True" + Environment.NewLine +
                "crossGenerationReplayG3AfterClearDenied=True" + Environment.NewLine +
                "crossGenerationReplayG3AfterTryClearDenied=True" + Environment.NewLine +
                "crossGenerationReplaySinkRows=0" + Environment.NewLine +
                "crossGenerationReplayLineRows=0" + Environment.NewLine +
                "crossGenerationReplayStockMovementRows=0" + Environment.NewLine +
                "crossGenerationReplayOutboxRows=0" + Environment.NewLine +
                "crossGenerationFreshResponseRecovers=True" + Environment.NewLine +
                "trustedClockSaveFailureNotPublished=True" + Environment.NewLine +
                "saleCommitDurabilityHeadroomDenied=True" + Environment.NewLine +
                "crossPreflightRegressionDenied=True" + Environment.NewLine +
                "loginRevocationRaceDenied=True" + Environment.NewLine +
                "staleLoginCleanupDoesNotClearNewAuthority=True" + Environment.NewLine +
                "staleDenialDoesNotClearNewAuthority=True" + Environment.NewLine +
                "permissionSnapshotRejectsReplacementAdmin=True" + Environment.NewLine +
                "permissionSnapshotRejectsConcurrentRevocation=True" + Environment.NewLine +
                "localRecoveryCannotInheritPosAuthority=True" + Environment.NewLine +
                "saleExpiryRaceSinkRows=0" + Environment.NewLine +
                "saleExpiryRaceOutboxRows=0" + Environment.NewLine +
                "saleRevocationRaceSinkRows=0" + Environment.NewLine +
                "saleRevocationRaceOutboxRows=0" + Environment.NewLine +
                "saleRevocationDemandCount=4" + Environment.NewLine +
                "denialCallbacksAfterGateRelease=True" + Environment.NewLine +
                "saleGenerationRaceSinkRows=0" + Environment.NewLine +
                "saleGenerationRaceOutboxRows=0" + Environment.NewLine +
                "saleGenerationDemandCount=4" + Environment.NewLine +
                "saleCommitExpiryRaceSinkRows=0" + Environment.NewLine +
                "saleCommitExpiryRaceOutboxRows=0" + Environment.NewLine +
                "saleCommitExpiryDemandCount=5" + Environment.NewLine +
                "saleCommitBlockedReaderExpiryDenied=True" + Environment.NewLine +
                "saleCommitBlockedReaderSinkRows=0" + Environment.NewLine +
                "saleCommitBlockedReaderOutboxRows=0" + Environment.NewLine +
                "saleCommitBlockedReaderDemandCount=2" + Environment.NewLine +
                "saleCommitFenceReleased=True" + Environment.NewLine +
                "saleCommitRevocationLinearized=True" + Environment.NewLine +
                "saleRaceAuditPersisted=True" + Environment.NewLine +
                "saleRaceAuthorizedRetryRows=1" + Environment.NewLine +
                "saleExactRetryIdempotent=True" + Environment.NewLine +
                "saleAmbiguousCommitRetryIdempotent=True" + Environment.NewLine +
                "saleAmbiguousCartMutationStartsNewIdentity=True" + Environment.NewLine +
                "saleAmbiguousAuthorityMismatchDenied=True" + Environment.NewLine +
                "concurrentAuthorizedSalesRows=2" + Environment.NewLine +
                "concurrentAuthorizedSalesOutboxRows=2" + Environment.NewLine +
                "hardwareEffects=0" + Environment.NewLine;
        }

        private static async Task VerifyCrossGenerationReplayContinuityAsync(
            SqliteConnectionFactory factory,
            UserRepository users,
            SecurityRepository security,
            string username)
        {
            long clockTicks = 0;
            var clockStore = new PosTrustedDeviceStore(
                () => clockTicks,
                TimeSpan.TicksPerSecond,
                "qa-cross-generation-clock");
            clockStore.Clear();

            var originalResponse =
                BuildResponse(includeOfflineAttestation: true);
            var serverAt = ParseUtc(
                originalResponse.ServerTime,
                "cross-generation server receipt");
            var expiry = serverAt.AddSeconds(5);
            originalResponse.EffectiveOfflineAuthorizationExpiresAt =
                expiry.ToString("O", CultureInfo.InvariantCulture);
            originalResponse.Session.ExpiresAt =
                expiry.ToString("O", CultureInfo.InvariantCulture);
            clockStore.SaveFirstLogin(
                originalResponse,
                "qa-cross-generation-g1");
            Require(
                clockStore.TryRead(out var generationOne),
                "cross-generation G1 was not persisted");

            clockTicks = TimeSpan.FromSeconds(2).Ticks;
            var heartbeat = new PosHeartbeatResponse
            {
                Ok = true,
                ServerTime = serverAt.AddSeconds(2).ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                Session = new PosSessionResponse
                {
                    ExpiresAt = serverAt.AddMinutes(1).ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    HeartbeatAfterSeconds = 300,
                    PosSessionId =
                        originalResponse.Session.PosSessionId,
                    SessionToken =
                        "qa-cross-generation-heartbeat-token"
                }
            };
            Require(
                clockStore.TrySaveHeartbeat(
                    generationOne.GenerationId,
                    generationOne,
                    heartbeat,
                    out var heartbeatSession),
                "cross-generation heartbeat did not commit");
            Require(
                PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    heartbeatSession,
                    out var heartbeatGeneration) &&
                !clockStore.TryGetReusableGenerationId(
                    originalResponse,
                    heartbeatGeneration.Fingerprint,
                    out _),
                "heartbeat-mutated response remained an exact generation retry");

            clockTicks = TimeSpan.FromSeconds(10).Ticks;
            var expiredGuard = new PosOfflineAuthorizationLeaseGuard(
                clockStore,
                () => ParseUtc(
                    heartbeatSession.LastOkLocalAt,
                    "cross-generation heartbeat local receipt"),
                () => clockTicks,
                TimeSpan.TicksPerSecond);
            RequireDenied(
                expiredGuard.EvaluateAuthorizationUse(
                    out _,
                    out var expiredCommitGuard),
                "offline_lease_expired",
                "G1 did not observe the process trusted-time high-water");
            Require(
                expiredCommitGuard == null,
                "expired G1 issued a commit-expiry guard");

            var g2Denied = SaveFirstLoginDenied(
                clockStore,
                originalResponse,
                "qa-cross-generation-g2",
                "offline_lease_expired");
            Require(
                g2Denied &&
                clockStore.TryRead(out var afterG2) &&
                string.Equals(
                    afterG2.GenerationId,
                    generationOne.GenerationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    afterG2.SessionToken,
                    heartbeat.Session.SessionToken,
                    StringComparison.Ordinal),
                "G2 replay replaced the active heartbeat generation");

            var salesBefore = CountSaleRows(factory);
            var linesBefore = CountSaleLineRows(factory);
            var movementsBefore =
                CountLocalStockMovementRows(factory);
            var outboxBefore = CountSalesOutboxRows(factory);
            var replayOperator = new OperatorSession(
                users,
                security,
                expiredGuard);
            replayOperator.SetUserForTesting(new UserAccount
            {
                Id = 900004,
                Username = username,
                DisplayName = "QA Cross-generation Cashier",
                IsActive = true,
                RoleCode = "cashier",
                RoleName = "Cashier",
                PermissionCodes =
                    new List<string> { PermissionCodes.PosPay }
            });
            var replayDeniedBeforeSink = false;
            try
            {
                new PermissionService(replayOperator).Demand(
                    PermissionCodes.PosPay,
                    "qa cross-generation replay sale sink");
                InsertUnauthorizedSaleAndOutbox(
                    factory,
                    "cross-generation-replay");
            }
            catch (PosAuthorizationLeaseException ex)
            {
                replayDeniedBeforeSink = string.Equals(
                    ex.Code,
                    "offline_lease_expired",
                    StringComparison.Ordinal);
            }
            Require(
                replayDeniedBeforeSink,
                "cross-generation replay reached the payment boundary");

            clockStore.Clear();
            Require(
                SaveFirstLoginDenied(
                    clockStore,
                    originalResponse,
                    "qa-cross-generation-g3-clear",
                    "offline_lease_expired"),
                "Clear erased the process trusted-time high-water");

            var freshResponse =
                BuildResponse(includeOfflineAttestation: true);
            freshResponse.ServerTime = serverAt.AddSeconds(10)
                .ToString("O", CultureInfo.InvariantCulture);
            freshResponse.EffectiveOfflineAuthorizationExpiresAt =
                serverAt.AddMinutes(1)
                    .ToString("O", CultureInfo.InvariantCulture);
            freshResponse.Session.ExpiresAt =
                serverAt.AddMinutes(2)
                    .ToString("O", CultureInfo.InvariantCulture);
            freshResponse.TrustedDeviceToken =
                "qa-cross-generation-fresh-device-token";
            freshResponse.Session.SessionToken =
                "qa-cross-generation-fresh-session-token";
            clockStore.SaveFirstLogin(
                freshResponse,
                "qa-cross-generation-fresh-for-try-clear");
            Require(
                clockStore.TryRead(out var freshForTryClear) &&
                clockStore.TryClear(freshForTryClear.GenerationId),
                "TryClear control state was not cleared");
            Require(
                SaveFirstLoginDenied(
                    clockStore,
                    originalResponse,
                    "qa-cross-generation-g3-try-clear",
                    "offline_lease_expired"),
                "TryClear erased the process trusted-time high-water");

            clockStore.SaveFirstLogin(
                freshResponse,
                "qa-cross-generation-fresh-recovery");
            var freshGuard = new PosOfflineAuthorizationLeaseGuard(
                clockStore,
                () => DateTimeOffset.UtcNow,
                () => clockTicks,
                TimeSpan.TicksPerSecond);
            var freshPreflight = await freshGuard
                .PreflightAsync()
                .ConfigureAwait(true);
            Require(
                clockStore.TryRead(out var recoveredSession) &&
                string.Equals(
                    recoveredSession.GenerationId,
                    "qa-cross-generation-fresh-recovery",
                    StringComparison.Ordinal) &&
                freshPreflight.Decision.Allowed,
                "fresh authoritative response did not recover authorization");

            clockStore.Clear();
            var failedResponse =
                BuildResponse(includeOfflineAttestation: true);
            failedResponse.ServerTime = serverAt.AddSeconds(20)
                .ToString("O", CultureInfo.InvariantCulture);
            failedResponse.EffectiveOfflineAuthorizationExpiresAt =
                serverAt.AddMinutes(2)
                    .ToString("O", CultureInfo.InvariantCulture);
            failedResponse.Session.ExpiresAt =
                serverAt.AddMinutes(3)
                    .ToString("O", CultureInfo.InvariantCulture);
            var trustedStatePath = clockStore.TrustedDeviceFilePath;
            Directory.CreateDirectory(trustedStatePath);
            var saveFailed = false;
            try
            {
                clockStore.SaveFirstLogin(
                    failedResponse,
                    "qa-cross-generation-failed-save");
            }
            catch (IOException)
            {
                saveFailed = true;
            }
            catch (UnauthorizedAccessException)
            {
                saveFailed = true;
            }
            finally
            {
                if (Directory.Exists(trustedStatePath))
                    Directory.Delete(trustedStatePath, true);
                var trustedDirectory =
                    Path.GetDirectoryName(trustedStatePath);
                var trustedFileName =
                    Path.GetFileName(trustedStatePath);
                if (!string.IsNullOrWhiteSpace(trustedDirectory) &&
                    Directory.Exists(trustedDirectory))
                {
                    foreach (var tempPath in Directory.GetFiles(
                        trustedDirectory,
                        trustedFileName + ".*.tmp"))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
            Require(
                saveFailed && !clockStore.HasStoredState(),
                "trusted-state SaveState failure was not reproduced");

            var saveFailureControl =
                BuildResponse(includeOfflineAttestation: true);
            saveFailureControl.ServerTime = serverAt.AddSeconds(15)
                .ToString("O", CultureInfo.InvariantCulture);
            saveFailureControl.EffectiveOfflineAuthorizationExpiresAt =
                serverAt.AddSeconds(18)
                    .ToString("O", CultureInfo.InvariantCulture);
            saveFailureControl.Session.ExpiresAt =
                serverAt.AddSeconds(40)
                    .ToString("O", CultureInfo.InvariantCulture);
            clockStore.SaveFirstLogin(
                saveFailureControl,
                "qa-cross-generation-save-failure-control");
            Require(
                clockStore.TryRead(out var saveFailureSession) &&
                string.Equals(
                    saveFailureSession.GenerationId,
                    "qa-cross-generation-save-failure-control",
                    StringComparison.Ordinal),
                "failed SaveState published its candidate trusted-time high-water");

            clockStore.Clear();
            var headroomResponse =
                BuildResponse(includeOfflineAttestation: true);
            headroomResponse.ServerTime = serverAt.AddSeconds(30)
                .ToString("O", CultureInfo.InvariantCulture);
            headroomResponse.EffectiveOfflineAuthorizationExpiresAt =
                serverAt.AddSeconds(120)
                    .ToString("O", CultureInfo.InvariantCulture);
            headroomResponse.Session.ExpiresAt =
                serverAt.AddSeconds(180)
                    .ToString("O", CultureInfo.InvariantCulture);
            clockStore.SaveFirstLogin(
                headroomResponse,
                "qa-cross-generation-headroom");
            Require(
                clockStore.TryRead(out var headroomSession),
                "durability-headroom control state was not persisted");
            var headroomWall = ParseUtc(
                headroomSession.LastOkLocalAt,
                "durability-headroom local receipt");
            var headroomGuard = new PosOfflineAuthorizationLeaseGuard(
                clockStore,
                () => headroomWall,
                () => clockTicks,
                TimeSpan.TicksPerSecond);
            var headroomFirstPreflight = await headroomGuard
                .PreflightAsync()
                .ConfigureAwait(true);
            var headroomSecondPreflight = await headroomGuard
                .PreflightAsync()
                .ConfigureAwait(true);
            var headroomAuthentication = await headroomGuard
                .CommitAuthenticationAsync(
                    headroomFirstPreflight,
                    headroomSecondPreflight)
                .ConfigureAwait(true);
            Require(
                headroomAuthentication.Decision.Allowed,
                "durability-headroom control authentication was denied");
            var headroomDecision =
                headroomGuard.EvaluateAuthorizationUse(
                    out _,
                    out var commitHeadroomGuard);
            var budget =
                SqliteConnectionFactory.DurableCommitSafetyBudget;
            Require(
                headroomDecision.Allowed &&
                headroomDecision.EffectiveExpiresAt.HasValue &&
                headroomDecision.EstimatedServerNow.HasValue &&
                commitHeadroomGuard != null,
                "durability-headroom control guard was not issued");
            var remainingTicks =
                (headroomDecision.EffectiveExpiresAt.Value -
                 headroomDecision.EstimatedServerNow.Value).Ticks;
            Require(
                remainingTicks > budget.Ticks + 1,
                "durability-headroom control window was too small");
            var headroomAnchorTicks = clockTicks;
            clockTicks = headroomAnchorTicks +
                remainingTicks -
                budget.Ticks -
                1;
            Require(
                commitHeadroomGuard.Evaluate(budget).Allowed,
                "budget plus one tick was denied");
            clockTicks++;
            RequireDenied(
                commitHeadroomGuard.Evaluate(budget),
                "offline_lease_expired",
                "remaining time equal to the durability budget was allowed");
            clockTicks++;
            RequireDenied(
                commitHeadroomGuard.Evaluate(budget),
                "offline_lease_expired",
                "remaining time below the durability budget was allowed");

            Require(
                CountSaleRows(factory) == salesBefore &&
                CountSaleLineRows(factory) == linesBefore &&
                CountLocalStockMovementRows(factory) ==
                    movementsBefore &&
                CountSalesOutboxRows(factory) == outboxBefore,
                "cross-generation replay changed a durable sale sink");
            clockStore.Clear();
            await Task.CompletedTask;
        }

        private static bool SaveFirstLoginDenied(
            PosTrustedDeviceStore store,
            PosFirstLoginResponse response,
            string generationId,
            string expectedCode)
        {
            try
            {
                store.SaveFirstLogin(response, generationId);
                return false;
            }
            catch (InvalidDataException ex)
            {
                return ex.Message.IndexOf(
                    expectedCode,
                    StringComparison.Ordinal) >= 0;
            }
        }

        public static Task<string> RunClockCapacityAsync()
        {
            var serverAt = DateTimeOffset.UtcNow;
            var controlResponse = BuildClockCapacityResponse(
                serverAt,
                "control");

            var invalidFrequencyStore = new PosTrustedDeviceStore(
                () => 0L,
                0,
                "qa-clock-capacity-invalid-frequency");
            Require(
                SaveFirstLoginDenied(
                    invalidFrequencyStore,
                    controlResponse,
                    "qa-clock-capacity-invalid-frequency",
                    "trusted_time_continuity_lost"),
                "invalid process clock frequency did not fail closed");

            var invalidDomainStore = new PosTrustedDeviceStore(
                () => 0L,
                TimeSpan.TicksPerSecond,
                new string('x', 129));
            Require(
                SaveFirstLoginDenied(
                    invalidDomainStore,
                    controlResponse,
                    "qa-clock-capacity-invalid-domain",
                    "trusted_time_continuity_lost"),
                "invalid process clock domain did not fail closed");

            long firstClockTicks = 0;
            var firstStore = new PosTrustedDeviceStore(
                () => firstClockTicks,
                TimeSpan.TicksPerSecond,
                "qa-clock-capacity-00");
            firstStore.Clear();
            firstStore.SaveFirstLogin(
                BuildClockCapacityResponse(serverAt, "00"),
                "qa-clock-capacity-generation-00");

            var mismatchedStore = new PosTrustedDeviceStore(
                () => 0L,
                TimeSpan.TicksPerSecond,
                "qa-clock-capacity-mismatch");
            Require(
                SaveFirstLoginDenied(
                    mismatchedStore,
                    BuildClockCapacityResponse(
                        serverAt.AddSeconds(1),
                        "mismatch"),
                    "qa-clock-capacity-mismatch",
                    "trusted_time_continuity_lost"),
                "active process clock domain mismatch did not fail closed");
            firstStore.Clear();

            for (var index = 1; index < 16; index++)
            {
                long scopeTicks = 0;
                var scopeStore = new PosTrustedDeviceStore(
                    () => scopeTicks,
                    TimeSpan.TicksPerSecond,
                    "qa-clock-capacity-" +
                        index.ToString(
                            "D2",
                            CultureInfo.InvariantCulture));
                scopeStore.SaveFirstLogin(
                    BuildClockCapacityResponse(
                        serverAt.AddSeconds(index),
                        index.ToString(
                            "D2",
                            CultureInfo.InvariantCulture)),
                    "qa-clock-capacity-generation-" +
                        index.ToString(
                            "D2",
                            CultureInfo.InvariantCulture));
                scopeStore.Clear();
            }

            var saturatedStore = new PosTrustedDeviceStore(
                () => 0L,
                TimeSpan.TicksPerSecond,
                "qa-clock-capacity-16");
            Require(
                SaveFirstLoginDenied(
                    saturatedStore,
                    BuildClockCapacityResponse(
                        serverAt.AddSeconds(16),
                        "16"),
                    "qa-clock-capacity-generation-16",
                    "trusted_time_continuity_lost") &&
                !saturatedStore.HasStoredState(),
                "the seventeenth process clock scope did not fail closed");

            firstClockTicks = TimeSpan.FromMinutes(6).Ticks;
            Require(
                SaveFirstLoginDenied(
                    firstStore,
                    BuildClockCapacityResponse(
                        serverAt,
                        "00-stale-replay"),
                    "qa-clock-capacity-generation-00-stale-replay",
                    "offline_lease_expired") &&
                !firstStore.HasStoredState(),
                "process clock saturation evicted the first scope");

            firstStore.SaveFirstLogin(
                BuildClockCapacityResponse(
                    serverAt.AddMinutes(7),
                    "00-reuse"),
                "qa-clock-capacity-generation-00-reuse");
            Require(
                firstStore.TryRead(out var reusedFirstScope) &&
                string.Equals(
                    reusedFirstScope.GenerationId,
                    "qa-clock-capacity-generation-00-reuse",
                    StringComparison.Ordinal),
                "process clock saturation evicted the first scope");
            firstStore.Clear();

            return Task.FromResult(
                "PASS authorization lease clock capacity smoke" +
                Environment.NewLine +
                "trustedClockCapacityFailClosed=True" +
                Environment.NewLine +
                "trustedClockCapacityNoEviction=True" +
                Environment.NewLine +
                "trustedClockDomainMismatchDenied=True" +
                Environment.NewLine +
                "trustedClockInvalidKeyDenied=True" +
                Environment.NewLine);
        }

        private static PosFirstLoginResponse BuildClockCapacityResponse(
            DateTimeOffset serverAt,
            string suffix)
        {
            var response =
                BuildResponse(includeOfflineAttestation: true);
            response.ServerTime = serverAt.ToString(
                "O",
                CultureInfo.InvariantCulture);
            response.EffectiveOfflineAuthorizationExpiresAt =
                serverAt.AddMinutes(5).ToString(
                    "O",
                    CultureInfo.InvariantCulture);
            response.Session.ExpiresAt =
                serverAt.AddMinutes(10).ToString(
                    "O",
                    CultureInfo.InvariantCulture);
            response.TrustedDeviceToken =
                "qa-clock-capacity-device-" + suffix;
            response.Session.SessionToken =
                "qa-clock-capacity-session-" + suffix;
            return response;
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
            if (formatVersion == 1)
            {
                json = Regex.Replace(
                    json,
                    "\"generationId\":\"[^\"]*\",?",
                    string.Empty);
                json = Regex.Replace(
                    json,
                    "\"lastOkLocalAt\":\"[^\"]*\",?",
                    string.Empty);
            }
            if (formatVersion <= 2)
            {
                json = Regex.Replace(
                    json,
                    "\"effectiveOfflineAuthorizationExpiresAt\":\"[^\"]*\",?",
                    string.Empty);
            }
            json = Regex.Replace(
                json,
                "\"protectedOfflineAuthorizationBinding\":\"[^\"]*\",?",
                string.Empty);
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
                formatVersion == 1
                    ? "local_receipt_time_invalid"
                    : "offline_attestation_required",
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

        private static void ConfigureAuthoritativeWindow(
            PosFirstLoginResponse response,
            DateTimeOffset serverAt,
            TimeSpan duration)
        {
            if (response?.Session == null)
                throw new ArgumentNullException(nameof(response));
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));

            response.ServerTime = serverAt.ToString(
                "O",
                CultureInfo.InvariantCulture);
            var expiresAt = serverAt.Add(duration).ToString(
                "O",
                CultureInfo.InvariantCulture);
            response.EffectiveOfflineAuthorizationExpiresAt =
                expiresAt;
            response.Session.ExpiresAt = expiresAt;
        }

        private static async Task<bool>
            WaitForPendingExclusiveLockAsync(
                SqliteConnection connection)
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (var command =
                        connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT COUNT(1) FROM sales;";
                        command.CommandTimeout = 1;
                        command.ExecuteScalar();
                    }
                }
                catch (SqliteException ex)
                    when (ex.SqliteErrorCode == 5)
                {
                    return true;
                }

                await Task.Delay(20).ConfigureAwait(false);
            }

            return false;
        }

        private static async Task SeedCatalogSaleSafetyAsync(
            SqliteConnectionFactory factory)
        {
            await new ShopOfficialSnapshotRepository(factory).SaveAsync(
                new OfficialShopSnapshot
                {
                    ShopId = ShopId,
                    ShopCode = ShopCode,
                    ShopName = "QA Authorization Shop",
                    ShopStatus = "active",
                    Source = "qa_authorization_harness"
                }).ConfigureAwait(false);
            var state = new CatalogShopStateRepository(factory);
            var binding = await state
                .EnsureAndLoadCursorAsync(ShopId, ShopCode)
                .ConfigureAwait(false);
            using (var connection = factory.Open())
            using (var transaction = connection.BeginTransaction())
            {
                var now = DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture);
                await UpsertSettingAsync(
                    connection,
                    transaction,
                    CatalogShopStateRepository.CompletenessStatusKey,
                    "Verified").ConfigureAwait(false);
                await UpsertSettingAsync(
                    connection,
                    transaction,
                    CatalogShopStateRepository.CompletenessCodeKey,
                    "qa_verified").ConfigureAwait(false);
                await UpsertSettingAsync(
                    connection,
                    transaction,
                    CatalogShopStateRepository.RepairRequiredKey,
                    "0").ConfigureAwait(false);
                await UpsertSettingAsync(
                    connection,
                    transaction,
                    CatalogShopStateRepository.ExactnessShopIdKey,
                    ShopId).ConfigureAwait(false);
                await UpsertSettingAsync(
                    connection,
                    transaction,
                    CatalogShopStateRepository.ExactnessShopCodeKey,
                    ShopCode).ConfigureAwait(false);
                await UpsertSettingAsync(
                    connection,
                    transaction,
                    CatalogShopStateRepository.ExactnessEvaluatedAtKey,
                    now).ConfigureAwait(false);
                await UpsertSettingAsync(
                    connection,
                    transaction,
                    CatalogShopStateRepository.ExactnessVerifiedAtKey,
                    now).ConfigureAwait(false);
                transaction.Commit();
            }
            await state.StoreSaleSafeAsync(
                ShopId,
                ShopCode,
                DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                binding.Epoch).ConfigureAwait(false);
        }

        private static Task UpsertSettingAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            string key,
            string value)
        {
            return connection.ExecuteAsync(@"
INSERT INTO app_settings(key,value) VALUES(@key,@value)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                new { key, value },
                transaction);
        }

        private static long CountAuthorizationAuditRows(
            SqliteConnectionFactory factory,
            string code,
            bool requireUnattributed)
        {
            using (var connection = factory.Open())
            {
                return connection.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM security_events
WHERE event_type = @eventType
  AND details = @details
  AND (@requireUnattributed = 0 OR user_id IS NULL);",
                    new
                    {
                        eventType =
                            SecurityEventCodes.PosAuthorizationLeaseDenied,
                        details = "code=" + code,
                        requireUnattributed =
                            requireUnattributed ? 1 : 0
                    });
            }
        }

        private static async Task<bool>
            WaitForAuthorizationAuditIncrementAsync(
                SqliteConnectionFactory factory,
                string code,
                long baseline,
                bool requireUnattributed)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (CountAuthorizationAuditRows(
                        factory,
                        code,
                        requireUnattributed) > baseline)
                    return true;
                await Task.Delay(25).ConfigureAwait(false);
            }
            return false;
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

        private static long CountSaleLineRows(
            SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                return connection.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sale_lines;");
            }
        }

        private static long CountLocalStockMovementRows(
            SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                return connection.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM local_stock_movements;");
            }
        }

        private static long CountTotalStockQuantity(
            SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open())
            {
                return connection.ExecuteScalar<long>(
                    "SELECT COALESCE(SUM(stock_qty), 0) " +
                    "FROM product_meta;");
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
