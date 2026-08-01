using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class ProductImageProfileWpfSmoke
    {
        internal static string Run()
        {
            VerifyMutationRequestSerialization();
            const string profileName =
                "asus-staging-image-phase-b-0123456789abcdef01234567";
            const string deviceToken = "qa-profile-device-secret";
            const string sessionToken = "qa-profile-session-secret";
            var shared = new PosTrustedDeviceStore();
            PosTrustedDeviceStore isolated = null;
            try
            {
                shared.Clear();
                shared.SaveFirstLogin(BuildResponse(deviceToken, sessionToken));
                var sharedBefore = File.ReadAllBytes(shared.TrustedDeviceFilePath);

                isolated = shared.CreateIsolatedProfileFromCurrentTrust(
                    profileName);
                if (!isolated.TryRead(out var isolatedSession) ||
                    !shared.TryRead(out var sharedSession))
                {
                    throw new InvalidOperationException(
                        "profile_read_failed");
                }
                var sharedAfter = File.ReadAllBytes(shared.TrustedDeviceFilePath);
                var isolatedBytes = File.ReadAllBytes(
                    isolated.TrustedDeviceFilePath);
                if (!FixedBytesEqual(sharedBefore, sharedAfter))
                    throw new InvalidOperationException("shared_profile_changed");
                if (FixedBytesEqual(sharedBefore, isolatedBytes))
                    throw new InvalidOperationException("encrypted_file_was_copied");
                if (ContainsPlaintext(isolatedBytes, deviceToken) ||
                    ContainsPlaintext(isolatedBytes, sessionToken))
                {
                    throw new InvalidOperationException(
                        "profile_contains_plaintext_secret");
                }
                if (string.Equals(
                    sharedSession.GenerationId,
                    isolatedSession.GenerationId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "profile_generation_not_isolated");
                }
                if (isolatedSession.OfflineAuthorizationAttested ||
                    isolatedSession.EffectiveOfflineAuthorizationExpiresAt != null)
                {
                    throw new InvalidOperationException(
                        "offline_authority_was_cloned");
                }
                if (!string.Equals(
                        isolated.ProfileName,
                        profileName,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        isolated.TrustedDeviceFilePath,
                        shared.TrustedDeviceFilePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "profile_path_not_isolated");
                }
                if (PosTrustedDeviceStore.IsValidProfileName("../escape") ||
                    PosTrustedDeviceStore.IsValidProfileName("UPPERCASE-PROFILE"))
                {
                    throw new InvalidOperationException(
                        "unsafe_profile_name_accepted");
                }

                Task.Run(() => VerifySupervisorUsesIsolatedProfileAsync(
                        shared,
                        isolated,
                        sharedSession,
                        isolatedSession))
                    .GetAwaiter()
                    .GetResult();

                isolated.Clear();
                if (File.Exists(isolated.TrustedDeviceFilePath) ||
                    !File.Exists(shared.TrustedDeviceFilePath))
                {
                    throw new InvalidOperationException(
                        "profile_cleanup_scope_invalid");
                }
                return "PASS product_image_profile_dpapi_isolated=true " +
                    "shared_unchanged=true plaintext_secrets=false " +
                    "offline_authority_cloned=false " +
                    "supervisor_profile_injected=true cleanup_exact=true " +
                    "net48_request_serialization=true";
            }
            finally
            {
                try { isolated?.Clear(); } catch { }
                try { shared.Clear(); } catch { }
            }
        }

        private static void VerifyMutationRequestSerialization()
        {
            const string shopId =
                "10000000-0000-4000-8000-000000000149";
            const string deviceId =
                "50000000-0000-4000-8000-000000000149";
            const string staffId =
                "60000000-0000-4000-8000-000000000149";
            const string sessionId =
                "70000000-0000-4000-8000-000000000149";
            const string productId =
                "20000000-0000-4000-8000-000000000149";
            const string currentVersionId =
                "30000000-0000-4000-8000-000000000149";
            const string newVersionId =
                "40000000-0000-4000-8000-000000000149";
            var envelope = new PosProductImageEnvelope(
                "1.0.0-test",
                shopId,
                deviceId,
                staffId,
                1,
                sessionId,
                "qa-contract-device-secret",
                "qa-contract-session-secret");
            var intent = new PosProductImageIntentRequest(
                "qa-net48-intent-001",
                "qa-net48-intent-idem-001",
                envelope,
                productId,
                currentVersionId,
                new PosProductImageUploadMetadata(
                    700000,
                    1200,
                    "image/jpeg",
                    new string('a', 64),
                    1600),
                new PosProductImageUploadMetadata(
                    80000,
                    288,
                    "image/jpeg",
                    new string('b', 64),
                    384));
            var finalize = new PosProductImageFinalizeRequest(
                "qa-net48-finalize-001",
                "qa-net48-finalize-idem-001",
                envelope,
                productId,
                currentVersionId,
                newVersionId);
            var remove = new PosProductImageRemoveRequest(
                "qa-net48-remove-001",
                "qa-net48-remove-idem-001",
                envelope,
                productId,
                newVersionId);
            var read = new PosProductImageReadUrlsRequest(
                envelope,
                new[]
                {
                    new PosProductImageReadRef(
                        productId,
                        "main",
                        newVersionId)
                });

            RequireSerializedDiscriminator(intent, "intent");
            RequireSerializedDiscriminator(finalize, "finalize");
            RequireSerializedDiscriminator(remove, "remove");
            var readJson = PosProductImageContractV1.SerializeRequest(read);
            if (readJson.IndexOf(
                    "\"schemaVersion\":\"pos-product-image-v1\"",
                    StringComparison.Ordinal) < 0 ||
                readJson.IndexOf(
                    "\"refs\":[",
                    StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "product_image_net48_read_serialization_invalid");
            }
        }

        private static void RequireSerializedDiscriminator<T>(
            T request,
            string operation)
        {
            var json = PosProductImageContractV1.SerializeRequest(request);
            if (json.IndexOf(
                    "\"schemaVersion\":\"pos-product-image-v1\"",
                    StringComparison.Ordinal) < 0 ||
                json.IndexOf(
                    "\"operation\":\"" + operation + "\"",
                    StringComparison.Ordinal) < 0 ||
                json.IndexOf(
                    "\"payloadHash\":\"sha256:",
                    StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "product_image_net48_serialization_invalid");
            }
        }

        private static async Task VerifySupervisorUsesIsolatedProfileAsync(
            PosTrustedDeviceStore shared,
            PosTrustedDeviceStore isolated,
            PosTrustedDeviceSession sharedSession,
            PosTrustedDeviceSession isolatedSession)
        {
            if (!PosOnlineSyncSupervisorHost.TryCreateGeneration(
                    isolatedSession,
                    out var generation))
            {
                throw new InvalidOperationException(
                    "isolated_generation_invalid");
            }

            var testRoot = Path.Combine(
                AppPaths.DataDirectory,
                "product-image-profile-supervisor-" +
                Guid.NewGuid().ToString("N"));
            var priorBaseUrl = Environment.GetEnvironmentVariable(
                PosAdminWebOptions.BaseUrlEnvironmentVariable);
            PosOnlineSyncSupervisorHost host = null;
            var regressionCompleted = false;
            try
            {
                Directory.CreateDirectory(testRoot);
                Environment.SetEnvironmentVariable(
                    PosAdminWebOptions.BaseUrlEnvironmentVariable,
                    "https://127.0.0.1:1");

                var options = PosDbOptions.ForPath(
                    Path.Combine(testRoot, "pos.db"));
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);
                var pending = await new SaleRepository(factory)
                    .GetPendingSalesSyncOutboxAsync(
                        1,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ConfigureAwait(false);
                if (pending.Count != 0)
                {
                    throw new InvalidOperationException(
                        "profile_regression_outbox_not_empty");
                }

                var generations = new OnlineSyncGenerationRepository(factory);
                await generations.ActivateAndRecoverAsync(
                        generation,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .ConfigureAwait(false);

                host = new PosOnlineSyncSupervisorHost(
                    factory,
                    isolated,
                    new FileLogger("ProductImageProfileWpfSmoke"));
                using (var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15)))
                {
                    var attached = await host.AttachCurrentTrustAsync(
                            timeout.Token)
                        .ConfigureAwait(false);
                    if (attached == null ||
                        !string.Equals(
                            attached.Fingerprint,
                            generation.Fingerprint,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "isolated_supervisor_attach_failed");
                    }

                    var outcome = await host.TriggerAsync(
                            OnlineSyncLane.SalesOutbox,
                            OnlineSyncLaneTrigger.StartOfDay,
                            timeout.Token)
                        .ConfigureAwait(false);
                    if (!outcome.Success || outcome.AuthenticationDenied)
                    {
                        throw new InvalidOperationException(
                            "isolated_supervisor_sales_denied_" + outcome.Code);
                    }
                }
                if (!isolated.TryReadGeneration(
                        generation,
                        out _,
                        out _) ||
                    !await generations.IsCurrentAndActiveAsync(generation)
                        .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "isolated_generation_was_revoked");
                }
                if (!shared.TryRead(out var sharedAfter) ||
                    !string.Equals(
                        sharedAfter.GenerationId,
                        sharedSession.GenerationId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "shared_profile_changed_by_supervisor");
                }
                regressionCompleted = true;
            }
            finally
            {
                Exception cleanupFailure = null;
                if (host != null)
                {
                    try
                    {
                        var stopTask = host.StopAsync();
                        var completed = await Task.WhenAny(
                                stopTask,
                                Task.Delay(TimeSpan.FromSeconds(10)))
                            .ConfigureAwait(false);
                        if (!ReferenceEquals(completed, stopTask))
                        {
                            throw new TimeoutException(
                                "profile_regression_supervisor_stop_timeout");
                        }
                        await stopTask.ConfigureAwait(false);
                    }
                    catch (Exception ex) { cleanupFailure = ex; }
                    try { host.Dispose(); }
                    catch (Exception ex)
                    {
                        if (cleanupFailure == null) cleanupFailure = ex;
                    }
                }
                try
                {
                    Environment.SetEnvironmentVariable(
                        PosAdminWebOptions.BaseUrlEnvironmentVariable,
                        priorBaseUrl);
                    SqliteConnectionFactory.ClearAllPools();
                    if (Directory.Exists(testRoot))
                        Directory.Delete(testRoot, recursive: true);
                    if (Directory.Exists(testRoot))
                    {
                        throw new IOException(
                            "profile_regression_data_cleanup_incomplete");
                    }
                }
                catch (Exception ex)
                {
                    if (cleanupFailure == null) cleanupFailure = ex;
                }
                if (regressionCompleted && cleanupFailure != null)
                {
                    throw new InvalidOperationException(
                        "profile_regression_cleanup_failed",
                        cleanupFailure);
                }
            }
        }

        private static PosFirstLoginResponse BuildResponse(
            string deviceToken,
            string sessionToken)
        {
            var now = DateTimeOffset.UtcNow;
            return new PosFirstLoginResponse
            {
                Ok = true,
                ServerTime = now.ToString("O", CultureInfo.InvariantCulture),
                TrustedDeviceToken = deviceToken,
                Device = new PosTrustedDeviceResponse
                {
                    ShopDeviceId =
                        "50000000-0000-4000-8000-000000000152",
                    Status = "active",
                    Trusted = true
                },
                Session = new PosSessionResponse
                {
                    ExpiresAt = now.AddHours(1).ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    HeartbeatAfterSeconds = 300,
                    PosSessionId =
                        "70000000-0000-4000-8000-000000000152",
                    SessionToken = sessionToken
                },
                Shop = new PosShopResponse
                {
                    ShopCode = "QA-IMAGE",
                    ShopId = "10000000-0000-4000-8000-000000000152",
                    ShopName = "QA Product Image Shop",
                    ShopStatus = "active",
                    Source = "qa_harness"
                },
                Staff = new PosStaffResponse
                {
                    CredentialVersion = 1,
                    DisplayName = "QA Image Operator",
                    RoleKey = "cashier",
                    StaffCode = "QA-IMAGE",
                    StaffId = "60000000-0000-4000-8000-000000000152"
                }
            };
        }

        private static bool ContainsPlaintext(byte[] bytes, string value)
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return text.IndexOf(value, StringComparison.Ordinal) >= 0;
        }

        private static bool FixedBytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }
}
