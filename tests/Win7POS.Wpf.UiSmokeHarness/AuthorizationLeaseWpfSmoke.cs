using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
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
            var response = BuildResponse();
            store.SaveFirstLogin(response, "qa-auth-generation-1");
            Require(store.TryRead(out var trusted), "trusted session was not persisted");

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
                "hardwareEffects=0" + Environment.NewLine;
        }

        private static PosFirstLoginResponse BuildResponse()
        {
            var now = DateTimeOffset.UtcNow;
            return new PosFirstLoginResponse
            {
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
