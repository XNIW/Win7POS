using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Images;
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
            VerifyStagingDiagnosticArtifact();
            Task.Run(VerifyJsonStringifyStyleResponseParsingAsync)
                .GetAwaiter()
                .GetResult();
            Task.Run(VerifyStorageProviderErrorMappingAsync)
                .GetAwaiter()
                .GetResult();
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
                    "staging_diagnostic_redacted=true " +
                    "net48_request_serialization=true " +
                    "net48_json_stringify_response=true " +
                    "net48_storage_error_mapping=true";
            }
            finally
            {
                try { isolated?.Clear(); } catch { }
                try { shared.Clear(); } catch { }
            }
        }

        private static void VerifyStagingDiagnosticArtifact()
        {
            var acceptanceType = typeof(ProductImageStagingAcceptance);
            var reportType = acceptanceType.GetNestedType(
                "SafeReport",
                BindingFlags.NonPublic);
            var writer = reportType == null
                ? null
                : acceptanceType.GetMethod(
                    "WriteDiagnosticArtifact",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[]
                    {
                        typeof(string),
                        typeof(string),
                        typeof(Exception),
                        reportType
                    },
                    null);
            var failureWriter = acceptanceType.GetMethod(
                "TryWriteDiagnosticArtifact",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(Exception) },
                null);
            var cleanupRefresh = acceptanceType.GetMethod(
                "TryRefreshDiagnosticAfterCleanup",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (reportType == null || writer == null ||
                failureWriter == null || cleanupRefresh == null)
            {
                throw new InvalidOperationException(
                    "product_image_staging_diagnostic_writer_missing");
            }

            var root = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "Win7POS.ProductImageDiagnostic." +
                Guid.NewGuid().ToString("N")));
            var previousSha = Environment.GetEnvironmentVariable(
                "WIN7POS_ACCEPTANCE_EXACT_MAIN_SHA");
            try
            {
                Directory.CreateDirectory(root);
                Environment.SetEnvironmentVariable(
                    "WIN7POS_ACCEPTANCE_EXACT_MAIN_SHA",
                    new string('a', 40));
                File.WriteAllBytes(
                    Path.Combine(
                        root,
                        "product-image-first-list-thumb-1024x768.png"),
                    new byte[] { 1 });
                File.WriteAllBytes(
                    Path.Combine(root, "unexpected-secret-name.png"),
                    new byte[] { 2 });

                var report = Activator.CreateInstance(reportType, true);
                reportType.GetProperty("AuthBootstrap").SetValue(
                    report,
                    true,
                    null);
                reportType.GetProperty("CachePromoted").SetValue(
                    report,
                    true,
                    null);
                reportType.GetProperty("CleanupPending").SetValue(
                    report,
                    true,
                    null);
                reportType.GetProperty("Phase").SetValue(
                    report,
                    "first_cache_ready",
                    null);

                writer.Invoke(
                    null,
                    new[]
                    {
                        root,
                        "cache-restart",
                        new InvalidOperationException(
                            "staging_editor_runtime_image_not_loaded"),
                        report
                    });
                var path = Path.Combine(
                    root,
                    "product-image-staging-diagnostic.json");
                var known = ReadStagingDiagnostic(path);
                RequireDiagnostic(
                    known.SchemaVersion ==
                        "win7pos-product-image-staging-diagnostic-v1" &&
                    known.ExactMainSha == new string('a', 40) &&
                    known.Phase == "cache-restart" &&
                    known.LastCompletedCheckpoint == "first_cache_ready" &&
                    known.FailureCode ==
                        "staging_editor_runtime_image_not_loaded" &&
                    known.CompletedControlCount == 2 &&
                    known.CompletedControls.Length == 2 &&
                    Array.IndexOf(
                        known.CompletedControls,
                        "authBootstrap") >= 0 &&
                    Array.IndexOf(
                        known.CompletedControls,
                        "cachePromoted") >= 0 &&
                    known.MissingControls.Length == 41 &&
                    known.ExpectedScreenshots.Length == 8 &&
                    known.PresentScreenshots.Length == 1 &&
                    known.PresentScreenshots[0] ==
                        "product-image-first-list-thumb-1024x768.png" &&
                    known.CleanupPending,
                    "product_image_staging_diagnostic_known_invalid");
                foreach (var screenshot in known.ExpectedScreenshots)
                {
                    RequireDiagnostic(
                        Array.IndexOf(ExpectedStagingScreenshots, screenshot) >= 0,
                        "product_image_staging_diagnostic_screenshot_allowlist_invalid");
                }

                var safeReportPath = Path.Combine(
                    root,
                    "product-image-staging-result.json");
                File.WriteAllText(
                    safeReportPath,
                    "{\"schemaVersion\":\"win7pos-product-image-staging-v1\"," +
                    "\"exactMainSha\":\"" + new string('a', 40) + "\"," +
                    "\"phase\":\"offline_queued\"," +
                    "\"authBootstrap\":true,\"cleanupPending\":true}");
                failureWriter.Invoke(
                    null,
                    new object[]
                    {
                        root,
                        "resume",
                        new InvalidOperationException("first_cache_failed")
                    });
                var oldReport = ReadStagingDiagnostic(path);
                RequireDiagnostic(
                    oldReport.FailureCode == "first_cache_failed" &&
                    oldReport.LastCompletedCheckpoint == "offline_queued" &&
                    oldReport.CompletedControlCount == 1 &&
                    oldReport.CleanupPending,
                    "product_image_staging_diagnostic_report_compatibility_failed");

                File.WriteAllText(
                    safeReportPath,
                    "{\"schemaVersion\":\"win7pos-product-image-staging-v1\"," +
                    "\"exactMainSha\":\"" + new string('a', 40) + "\"," +
                    "\"phase\":\"terminal_clean\"," +
                    "\"authBootstrap\":true,\"cleanupComplete\":true," +
                    "\"cleanupPending\":false," +
                    "\"immutableAuditPreserved\":true," +
                    "\"runProfileRemoved\":true," +
                    "\"sharedSnapshotUnchanged\":true}");
                cleanupRefresh.Invoke(null, new object[] { root });
                var cleaned = ReadStagingDiagnostic(path);
                RequireDiagnostic(
                    cleaned.FailureCode == "first_cache_failed" &&
                    cleaned.LastCompletedCheckpoint == "offline_queued" &&
                    cleaned.CompletedControlCount == 5 &&
                    !cleaned.CleanupPending,
                    "product_image_staging_diagnostic_cleanup_refresh_failed");
                File.Delete(safeReportPath);

                var successRoot = Path.Combine(root, "successful-cleanup");
                Directory.CreateDirectory(successRoot);
                cleanupRefresh.Invoke(null, new object[] { successRoot });
                RequireDiagnostic(
                    !File.Exists(Path.Combine(
                        successRoot,
                        "product-image-staging-diagnostic.json")),
                    "product_image_staging_diagnostic_success_changed");

                var controls = new List<string>();
                controls.AddRange(known.CompletedControls);
                controls.AddRange(known.MissingControls);
                foreach (var control in controls)
                {
                    var propertyName = char.ToUpperInvariant(control[0]) +
                        control.Substring(1);
                    reportType.GetProperty(propertyName).SetValue(
                        report,
                        true,
                        null);
                }
                reportType.GetProperty("CleanupPending").SetValue(
                    report,
                    false,
                    null);
                var matrix = acceptanceType.GetMethod(
                    "IsFullMatrixComplete",
                    BindingFlags.NonPublic | BindingFlags.Static);
                RequireDiagnostic(
                    matrix != null && (bool)matrix.Invoke(
                        null,
                        new[] { report }),
                    "product_image_staging_diagnostic_matrix_changed");
                reportType.GetProperty("CleanupPending").SetValue(
                    report,
                    true,
                    null);
                RequireDiagnostic(
                    !(bool)matrix.Invoke(null, new[] { report }),
                    "product_image_staging_diagnostic_cleanup_semantics_changed");

                var unsafeDetail =
                    "https://storage.example.invalid/object?token=secret " +
                    "ASUSPIB_SECRET bearer raw-response " +
                    new string('x', 512);
                writer.Invoke(
                    null,
                    new[]
                    {
                        root,
                        "cache-restart",
                        new InvalidOperationException(unsafeDetail),
                        report
                    });
                var unsafeText = File.ReadAllText(path);
                var redacted = ReadStagingDiagnostic(path);
                RequireDiagnostic(
                    redacted.FailureCode ==
                        "product_image_acceptance_failure" &&
                    redacted.FailureCode.Length <= 80 &&
                    unsafeText.IndexOf(
                        "storage.example.invalid",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    unsafeText.IndexOf(
                        "token=",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    unsafeText.IndexOf(
                        "ASUSPIB_SECRET",
                        StringComparison.Ordinal) < 0 &&
                    unsafeText.IndexOf(
                        "bearer",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    unsafeText.IndexOf(
                        "raw-response",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    unsafeText.IndexOf(
                        "unexpected-secret-name.png",
                        StringComparison.Ordinal) < 0,
                    "product_image_staging_diagnostic_redaction_failed");
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "WIN7POS_ACCEPTANCE_EXACT_MAIN_SHA",
                    previousSha);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static StagingDiagnosticProbe ReadStagingDiagnostic(
            string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                return (StagingDiagnosticProbe)new DataContractJsonSerializer(
                    typeof(StagingDiagnosticProbe)).ReadObject(stream);
            }
        }

        private static void RequireDiagnostic(bool condition, string code)
        {
            if (!condition) throw new InvalidOperationException(code);
        }

        private static readonly string[] ExpectedStagingScreenshots =
        {
            "product-image-first-list-thumb-1024x768.png",
            "product-image-first-editor-main-1024x768.png",
            "product-image-offline-restart-list-thumb-1024x768.png",
            "product-image-offline-restart-editor-main-1024x768.png",
            "product-image-replacement-list-thumb-1024x768.png",
            "product-image-replacement-editor-main-1024x768.png",
            "product-image-removed-list-no-image-1024x768.png",
            "product-image-removed-editor-no-image-1024x768.png"
        };

        [DataContract]
        private sealed class StagingDiagnosticProbe
        {
            [DataMember(Name = "schemaVersion")]
            public string SchemaVersion { get; set; }
            [DataMember(Name = "exactMainSha")]
            public string ExactMainSha { get; set; }
            [DataMember(Name = "phase")]
            public string Phase { get; set; }
            [DataMember(Name = "lastCompletedCheckpoint")]
            public string LastCompletedCheckpoint { get; set; }
            [DataMember(Name = "failureCode")]
            public string FailureCode { get; set; }
            [DataMember(Name = "completedControlCount")]
            public int CompletedControlCount { get; set; }
            [DataMember(Name = "completedControls")]
            public string[] CompletedControls { get; set; }
            [DataMember(Name = "missingControls")]
            public string[] MissingControls { get; set; }
            [DataMember(Name = "expectedScreenshots")]
            public string[] ExpectedScreenshots { get; set; }
            [DataMember(Name = "presentScreenshots")]
            public string[] PresentScreenshots { get; set; }
            [DataMember(Name = "cleanupPending")]
            public bool CleanupPending { get; set; }
            [DataMember(Name = "recordedAt")]
            public string RecordedAt { get; set; }
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

        private static async Task VerifyJsonStringifyStyleResponseParsingAsync()
        {
            const string shopId =
                "10000000-0000-4000-8000-000000000149";
            const string productId =
                "20000000-0000-4000-8000-000000000149";
            const string currentVersionId =
                "30000000-0000-4000-8000-000000000149";
            const string newVersionId =
                "40000000-0000-4000-8000-000000000149";
            var storageOrigin = new Uri("https://storage.example.invalid/");
            var envelope = new PosProductImageEnvelope(
                "1.0.0-test",
                shopId,
                "50000000-0000-4000-8000-000000000149",
                "60000000-0000-4000-8000-000000000149",
                1,
                "70000000-0000-4000-8000-000000000149",
                "qa-contract-device-secret",
                "qa-contract-session-secret");
            var intent = new PosProductImageIntentRequest(
                "qa-net48-intent-response-001",
                "qa-net48-intent-response-idem-001",
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
            var read = new PosProductImageReadUrlsRequest(
                envelope,
                new[]
                {
                    new PosProductImageReadRef(
                        productId,
                        "thumb",
                        newVersionId)
                });
            var mainUploadUrl = storageOrigin +
                "storage/v1/object/upload/sign/product-images/shops/" +
                shopId + "/products/" + productId + "/primary/" +
                newVersionId + "/main.jpg?token=ephemeral";
            var thumbUploadUrl = storageOrigin +
                "storage/v1/object/upload/sign/product-images/shops/" +
                shopId + "/products/" + productId + "/primary/" +
                newVersionId + "/thumb.jpg?token=ephemeral";
            var thumbReadUrl = storageOrigin +
                "storage/v1/object/sign/product-images/shops/" +
                shopId + "/products/" + productId + "/primary/" +
                newVersionId + "/thumb.jpg?token=ephemeral";
            var intentBody = "{" +
                "\"schemaVersion\":\"pos-product-image-v1\"," +
                "\"operation\":\"intent\"," +
                "\"operationId\":\"" + intent.OperationId + "\"," +
                "\"idempotencyKey\":\"" + intent.IdempotencyKey + "\"," +
                "\"payloadHash\":\"" + intent.PayloadHash + "\"," +
                "\"ok\":true," +
                "\"code\":\"success\"," +
                "\"replayed\":false," +
                "\"serverTime\":\"2026-07-30T16:55:57.123456Z\"," +
                "\"cacheScope\":\"fixture-pos-image-scope-149\"," +
                "\"status\":\"upload_required\"," +
                "\"versionId\":\"" + newVersionId + "\"," +
                "\"expiresAt\":\"2026-07-30T18:55:57.123456Z\"," +
                "\"mainUploadUrl\":\"" + mainUploadUrl + "\"," +
                "\"thumbUploadUrl\":\"" + thumbUploadUrl + "\"}";
            var readBody = "{" +
                "\"schemaVersion\":\"pos-product-image-v1\"," +
                "\"operation\":\"read-urls\"," +
                "\"ok\":true," +
                "\"code\":\"success\"," +
                "\"serverTime\":\"2026-07-30T16:56:03.123456Z\"," +
                "\"cacheScope\":\"fixture-pos-image-scope-149\"," +
                "\"items\":[{" +
                "\"expiresAt\":\"2026-07-30T17:01:03.123456Z\"," +
                "\"metadata\":{\"bytes\":80000,\"height\":288," +
                "\"mimeType\":\"image/jpeg\",\"sha256\":\"" +
                new string('b', 64) + "\",\"width\":384}," +
                "\"productId\":\"" + productId + "\"," +
                "\"signedUrl\":\"" + thumbReadUrl + "\"," +
                "\"status\":\"ready\"," +
                "\"variant\":\"thumb\"," +
                "\"versionId\":\"" + newVersionId + "\"}]}";
            var reorderedIntentBody = intentBody.Replace(
                "\"status\":\"upload_required\",\"versionId\":\"" +
                    newVersionId + "\"",
                "\"versionId\":\"" + newVersionId +
                    "\",\"status\":\"upload_required\"");
            if (intentBody.IndexOf(@"https:\/\/", StringComparison.Ordinal) >= 0 ||
                readBody.IndexOf(@"https:\/\/", StringComparison.Ordinal) >= 0 ||
                reorderedIntentBody == intentBody)
            {
                throw new InvalidOperationException(
                    "product_image_net48_response_fixture_not_json_stringify_style");
            }
            var responses = new Queue<HttpResponseMessage>(new[]
            {
                JsonResponse(intentBody),
                JsonResponse(readBody),
                JsonResponse(reorderedIntentBody)
            });
            using (var client = new PosProductImageClient(
                new PosAdminWebOptions(new Uri("https://admin.example.invalid/")),
                storageOrigin,
                new QueueHttpMessageHandler(responses),
                () => DateTimeOffset.Parse("2026-07-30T16:56:00Z")))
            {
                var intentResult = await client.IntentAsync(
                        intent,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var readResult = await client.ReadUrlsAsync(
                        read,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var reorderedResult = await client.IntentAsync(
                        intent,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!intentResult.IsSuccess ||
                    intentResult.Value == null ||
                    intentResult.Value.Status != "upload_required" ||
                    intentResult.Value.MainUploadUrl != mainUploadUrl ||
                    intentResult.Value.ThumbUploadUrl != thumbUploadUrl ||
                    !readResult.IsSuccess ||
                    readResult.Value == null ||
                    readResult.Value.Items == null ||
                    readResult.Value.Items.Length != 1 ||
                    readResult.Value.Items[0].SignedUrl != thumbReadUrl ||
                    reorderedResult.IsSuccess ||
                    reorderedResult.FailureKind !=
                        PosProductImageFailureKind.CorruptResponse ||
                    responses.Count != 0)
                {
                    throw new InvalidOperationException(
                        "product_image_net48_json_stringify_response_invalid");
                }
            }
        }

        private static async Task VerifyStorageProviderErrorMappingAsync()
        {
            const string shopId =
                "10000000-0000-4000-8000-000000000149";
            const string productId =
                "20000000-0000-4000-8000-000000000149";
            const string versionId =
                "40000000-0000-4000-8000-000000000149";
            var storageOrigin = new Uri("https://storage.example.invalid/");
            var responses = new Queue<HttpResponseMessage>(new[]
            {
                JsonStorageError("InvalidJWT", "InvalidJWT", "400"),
                JsonStorageError(null, "ExpiredToken", "400"),
                JsonStorageError("ResourceAlreadyExists", "Duplicate", "409"),
                JsonStorageError(null, "ResourceAlreadyExists", "409")
            });
            using (var client = new PosProductImageClient(
                new PosAdminWebOptions(new Uri("https://admin.example.invalid/")),
                storageOrigin,
                new QueueHttpMessageHandler(responses)))
            {
                var uploadUrl = storageOrigin +
                    "storage/v1/object/upload/sign/product-images/shops/" +
                    shopId + "/products/" + productId + "/primary/" +
                    versionId + "/main.jpg?token=ephemeral";
                var readUrl = storageOrigin +
                    "storage/v1/object/sign/product-images/shops/" +
                    shopId + "/products/" + productId + "/primary/" +
                    versionId + "/main.jpg?token=ephemeral";

                PosProductImageUploadResult expiredUpload;
                using (var bytes = new MemoryStream(new byte[128]))
                {
                    expiredUpload = await client.UploadJpegAsync(
                            uploadUrl,
                            shopId,
                            productId,
                            versionId,
                            "main",
                            bytes,
                            128,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                var expected = new PosProductImageUploadMetadata(
                    128,
                    8,
                    ProductImageContractV1.WireMimeType,
                    new string('a', 64),
                    8);
                var expiredDownload = await client.DownloadJpegAsync(
                        readUrl,
                        shopId,
                        productId,
                        versionId,
                        "main",
                        expected,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                PosProductImageUploadResult alreadyUploaded;
                using (var bytes = new MemoryStream(new byte[128]))
                {
                    alreadyUploaded = await client.UploadJpegAsync(
                            uploadUrl,
                            shopId,
                            productId,
                            versionId,
                            "main",
                            bytes,
                            128,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                PosProductImageUploadResult errorOnly;
                using (var bytes = new MemoryStream(new byte[128]))
                {
                    errorOnly = await client.UploadJpegAsync(
                            uploadUrl,
                            shopId,
                            productId,
                            versionId,
                            "main",
                            bytes,
                            128,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (expiredUpload.Code != "expired_capability" ||
                    !expiredUpload.Retryable ||
                    expiredDownload.Code != "expired_capability" ||
                    !expiredDownload.Retryable ||
                    !alreadyUploaded.IsSuccess ||
                    alreadyUploaded.Code != "already_uploaded" ||
                    errorOnly.IsSuccess ||
                    errorOnly.Code != "upload_rejected" ||
                    responses.Count != 0)
                {
                    throw new InvalidOperationException(
                        "product_image_net48_storage_error_mapping_invalid");
                }
            }
        }

        private static HttpResponseMessage JsonStorageError(
            string code,
            string error,
            string statusCode)
        {
            var body = "{\"statusCode\":\"" + statusCode + "\"," +
                (code == null ? string.Empty : "\"code\":\"" + code + "\",") +
                "\"error\":\"" + error + "\"}";
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage JsonResponse(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private sealed class QueueHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses;

            internal QueueHttpMessageHandler(Queue<HttpResponseMessage> responses)
            {
                _responses = responses;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (_responses.Count == 0)
                    throw new InvalidOperationException(
                        "product_image_net48_storage_response_missing");
                return Task.FromResult(_responses.Dequeue());
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
