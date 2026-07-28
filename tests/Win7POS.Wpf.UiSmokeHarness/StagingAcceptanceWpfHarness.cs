using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win7POS.Core;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    /// <summary>
    /// Test-only staging path. It deliberately composes production login,
    /// bootstrap and catalog services without adding an application auto-login
    /// entry point or accepting credentials in process arguments.
    /// </summary>
    internal static class StagingAcceptanceWpfHarness
    {
        private const string AllowedStagingHost =
            "merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev";
        private const string FixedDataDirectory =
            @"C:\POSData\Win7POSAutomatedStagingAcceptance";
        private const string QaSecretsDirectory = @"C:\ProgramData\Win7POS\QaSecrets";

        internal static async Task<bool> RunAsync(
            string profileName,
            string outputDirectory)
        {
            var report = new AcceptanceReport
            {
                StartedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Profile = SafeProfileName(profileName),
                DataDirectoryIsolated = IsExpectedDataDirectory(),
                LogicalRuns = 1
            };
            QaCredentialProfile profile = null;
            PosOnlineSyncSupervisorHost host = null;
            IOperatorSession operatorSession = null;
            try
            {
                if (!report.DataDirectoryIsolated)
                {
                    throw new AcceptanceFailure("data_directory_not_isolated");
                }

                profile = LoadProfile(profileName);
                report.ProfileValidated = true;

                DbInitializer.EnsureCreated(PosDbOptions.Default());
                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                PosAdminWebOptions.SaveBaseUrl(profile.BaseUri);

                host = new PosOnlineSyncSupervisorHost(factory);
                var request = new PosFirstLoginRequest
                {
                    Credential = profile.Credential,
                    Device = new PosFirstLoginDevice
                    {
                        AppVersion = GetProductionAppVersion(),
                        DeviceIdentifier = profile.DeviceIdentifier,
                        DisplayName = profile.DeviceDisplayName
                    },
                    ShopCode = profile.ShopCode,
                    StaffCode = profile.StaffCode
                };

                PosOnlineBootstrapResult bootstrap;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6)))
                {
                    bootstrap = await new PosOnlineBootstrapService(
                            factory,
                            new PosTrustedDeviceStore(),
                            host)
                        .BootstrapAsync(
                            new PosAdminWebOptions(profile.BaseUri),
                            request,
                            profile.Credential,
                            timeout.Token)
                        .ConfigureAwait(true);
                }
                request.Credential = string.Empty;

                report.HttpSuccess = bootstrap != null && bootstrap.HttpStatus.HasValue &&
                    bootstrap.HttpStatus.Value >= 200 && bootstrap.HttpStatus.Value <= 299;
                report.BootstrapCode = SafeCode(bootstrap?.Code);
                report.FailureStage = SafeCode(bootstrap?.FailureStage);
                report.RootCode = SafeCode(bootstrap?.RootCode);
                report.HttpStatus = bootstrap?.HttpStatus;
                report.AuthenticationDenied = bootstrap != null && bootstrap.AuthenticationDenied;
                report.Retryable = bootstrap != null && bootstrap.Retryable;
                report.DeviceApprovalState = SafeCode(bootstrap?.DeviceApprovalState);
                report.ClientRequestId = RedactTechnicalIdentifier(bootstrap?.ClientRequestId);
                report.ServerRequestId = RedactTechnicalIdentifier(bootstrap?.ServerRequestId);
                report.CfRay = RedactTechnicalIdentifier(bootstrap?.CfRay);
                report.ExceptionType = SafeCode(bootstrap?.ExceptionType);
                report.RequestReachedServer = bootstrap != null && bootstrap.RequestReachedServer;
                report.FirstLoginSucceeded = bootstrap != null && bootstrap.FirstLoginSucceeded;
                report.TrustedSessionPersisted = bootstrap != null && bootstrap.TrustedSessionPersisted;
                report.CatalogStarted = bootstrap != null && bootstrap.CatalogStarted;
                report.CatalogDrained = bootstrap != null &&
                    bootstrap.CatalogCompleted &&
                    !bootstrap.RequiresRetry;
                report.SaleSafe = bootstrap != null && bootstrap.CatalogSaleSafe;
                report.DeterministicRetrySuppressed = bootstrap == null ||
                    !CatalogRetryPolicy.IsDeterministicRevisionFailure(bootstrap.Code) ||
                    !bootstrap.RequiresRetry;

                if (bootstrap == null || !bootstrap.CanOpenPos)
                {
                    throw new AcceptanceFailure(
                        "bootstrap_" + SafeCode(bootstrap?.FailureStage) + "_" +
                        SafeCode(bootstrap?.RootCode));
                }

                var users = new UserRepository(factory);
                var username = await users.FindRemoteStaffUsernameAsync(
                    profile.ShopCode,
                    profile.StaffCode).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(username))
                {
                    RecordHarnessFailure(
                        report,
                        "operator_mirror",
                        "remote_staff_mirror_missing");
                    throw new AcceptanceFailure("remote_staff_mirror_missing");
                }

                operatorSession = CreateProductionOperatorSession(factory);
                OperatorSessionHolder.Current = operatorSession;
                var loginResult = await operatorSession.LoginAsync(
                    username,
                    profile.Credential).ConfigureAwait(true);
                report.PosUnlocked = loginResult == LoginResult.Success &&
                    operatorSession.IsLoggedIn;
                if (!report.PosUnlocked)
                {
                    RecordHarnessFailure(
                        report,
                        "local_operator_login",
                        "local_operator_login_" + SafeCode(loginResult.ToString()));
                    throw new AcceptanceFailure("local_operator_login_" +
                        SafeCode(loginResult.ToString()));
                }

                var exactness = await new CatalogShopStateRepository(factory)
                    .LoadExactnessAsync().ConfigureAwait(true);
                var audit = await new CatalogFullRefreshReconciler(factory)
                    .AuditCurrentAsync().ConfigureAwait(true);
                var settings = new SettingsRepository(factory);
                var pages = await settings.GetStringAsync(
                    CatalogShopStateRepository.ExactnessPagesKey).ConfigureAwait(true);
                var parsedPages = 0;
                int.TryParse(
                    pages,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsedPages);
                report.CatalogPages = parsedPages;
                report.ManifestActiveCategories = exactness.ActiveCategories;
                report.ManifestActiveProducts = exactness.ActiveProducts;
                report.ManifestActiveSuppliers = exactness.ActiveSuppliers;
                report.LocalActiveCategories = audit.ActiveRemoteCategories;
                report.LocalActiveProducts = audit.ActiveRemoteProducts;
                report.LocalActiveSuppliers = audit.ActiveRemoteSuppliers;
                report.PendingRemotePrices = audit.PendingRemotePrices;
                report.ExactnessVerified =
                    string.Equals(exactness.Status.ToString(), "Verified", StringComparison.Ordinal) &&
                    !exactness.RepairRequired &&
                    audit.PendingRemotePrices == 0 &&
                    exactness.ActiveCategories == audit.ActiveRemoteCategories &&
                    exactness.ActiveProducts == audit.ActiveRemoteProducts &&
                    exactness.ActiveSuppliers == audit.ActiveRemoteSuppliers;
                report.PreviousDiagnosticCleared = string.IsNullOrWhiteSpace(
                    await settings.GetStringAsync("pos.catalog.last_error")
                        .ConfigureAwait(true));

                var products = new ProductRepository(factory);
                var oneProduct = await products.SearchAsync(string.Empty, 1)
                    .ConfigureAwait(true);
                report.LocalProductsPositive = oneProduct != null && oneProduct.Count > 0;
                if (!report.CatalogDrained || !report.SaleSafe ||
                    !report.ExactnessVerified || !report.LocalProductsPositive ||
                    !report.PreviousDiagnosticCleared)
                {
                    RecordHarnessFailure(
                        report,
                        "catalog_pull",
                        "catalog_acceptance_verification_failed");
                    throw new AcceptanceFailure("catalog_acceptance_verification_failed");
                }

                report.ProductListPopulated = await CaptureReadOnlyProductsUiAsync(
                    outputDirectory).ConfigureAwait(true);
                if (!report.ProductListPopulated)
                {
                    RecordHarnessFailure(
                        report,
                        "catalog_pull",
                        "product_list_not_populated");
                    throw new AcceptanceFailure("product_list_not_populated");
                }

                report.LogRedactionPassed = LogsDoNotContainProfileValues(profile);
                if (!report.LogRedactionPassed)
                {
                    RecordHarnessFailure(
                        report,
                        "local_operator_login",
                        "secret_log_redaction_failed");
                    throw new AcceptanceFailure("secret_log_redaction_failed");
                }

                report.Passed = true;
                report.Code = "success";
            }
            catch (AcceptanceFailure failure)
            {
                if (string.IsNullOrWhiteSpace(report.FailureStage) ||
                    string.Equals(report.FailureStage, "completed", StringComparison.Ordinal))
                {
                    RecordHarnessFailure(
                        report,
                        report.ProfileValidated ? "request_build" : "profile_validation",
                        failure.Code);
                }
                report.Code = SafeCode(failure.Code);
            }
            catch (Exception)
            {
                if (string.IsNullOrWhiteSpace(report.FailureStage) ||
                    string.Equals(report.FailureStage, "completed", StringComparison.Ordinal))
                {
                    RecordHarnessFailure(
                        report,
                        report.ProfileValidated ? "request_build" : "profile_validation",
                        "unexpected_harness_failure");
                }
                report.Code = "unexpected_harness_failure";
            }
            finally
            {
                try { operatorSession?.Logout(); } catch { }
                OperatorSessionHolder.Current = null;
                try { host?.Dispose(); } catch { }
                profile?.Clear();
                report.CompletedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                WriteReport(outputDirectory, report);
            }

            return report.Passed;
        }

        private static IOperatorSession CreateProductionOperatorSession(
            SqliteConnectionFactory factory)
        {
            return new OperatorSession(
                new UserRepository(factory),
                new SecurityRepository(factory));
        }

        private static void RecordHarnessFailure(
            AcceptanceReport report,
            string failureStage,
            string rootCode)
        {
            if (report == null)
            {
                return;
            }

            report.FailureStage = SafeCode(failureStage);
            report.RootCode = SafeCode(rootCode);
        }

        internal static string GetProductionAppVersion()
        {
            return PosApplicationVersion.GetCurrent();
        }

        internal static string RunOfflineContractSmoke()
        {
            var appVersion = GetProductionAppVersion();
            Version parsed;
            var sameProductionAssembly = typeof(PosOnlineBootstrapService).Assembly ==
                typeof(StagingAcceptanceWpfHarness).Assembly;
            var versionValid = !string.IsNullOrWhiteSpace(appVersion) &&
                Version.TryParse(appVersion, out parsed);
            return versionValid && !sameProductionAssembly
                ? "PASS appVersion=production_wpf stableDevice=profile_v2"
                : "FAIL appVersion_contract";
        }

        internal static string RunOfflineDiagnosticsMatrixSmoke()
        {
            var cases = new[]
            {
                new { Code = "http_401", Status = (int?)401, Reached = true, Stage = "server_response" },
                new { Code = "device_pending", Status = (int?)403, Reached = true, Stage = "device_pending_approval" },
                new { Code = "device_revoked", Status = (int?)403, Reached = true, Stage = "device_denied" },
                new { Code = "unsupported_app_version", Status = (int?)409, Reached = true, Stage = "first_login_contract" },
                new { Code = "http_5xx", Status = (int?)500, Reached = true, Stage = "server_response" },
                new { Code = "timeout", Status = (int?)null, Reached = false, Stage = "timeout" },
                new { Code = "tls", Status = (int?)null, Reached = false, Stage = "tls" },
                new { Code = "network_error", Status = (int?)null, Reached = false, Stage = "network" },
                new { Code = "invalid_response", Status = (int?)200, Reached = true, Stage = "invalid_response" }
            };
            foreach (var item in cases)
            {
                if (!string.Equals(
                        PosBootstrapDiagnosticsPolicy.GetFailureStage(
                            item.Code,
                            item.Status,
                            item.Reached),
                        item.Stage,
                        StringComparison.Ordinal))
                {
                    return "FAIL diagnostics_matrix";
                }
            }

            var report = new AcceptanceReport
            {
                FailureStage = "completed",
                RootCode = "success"
            };
            RecordHarnessFailure(report, "local_operator_login", "operator_login_failed");
            var localOperatorTyped = report.FailureStage == "local_operator_login" &&
                report.RootCode == "operator_login_failed";
            return localOperatorTyped
                ? "PASS diagnostics=typed localOperator=typed redaction=required"
                : "FAIL local_operator_diagnostic";
        }

        private static async Task<bool> CaptureReadOnlyProductsUiAsync(
            string outputDirectory)
        {
            var viewModel = new ProductsViewModel();
            await viewModel.LoadAsync().ConfigureAwait(true);
            if (viewModel.Items.Count == 0)
            {
                return false;
            }

            Directory.CreateDirectory(outputDirectory);
            var window = new Window
            {
                Content = new ProductsView { DataContext = viewModel },
                Height = 768,
                ShowInTaskbar = false,
                Title = "Win7POS staging acceptance read-only products",
                Width = 1024,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                await Task.Delay(150).ConfigureAwait(true);
                CaptureWindow(
                    window,
                    Path.Combine(outputDirectory, "staging-acceptance-products-readonly.png"));
                return true;
            }
            finally
            {
                window.Close();
            }
        }

        private static void CaptureWindow(Window window, string path)
        {
            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            render.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(render));
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }
        }

        private static QaCredentialProfile LoadProfile(string profileName)
        {
            var profile = SafeProfileName(profileName);
            var root = Path.GetFullPath(QaSecretsDirectory);
            var path = Path.GetFullPath(Path.Combine(root, profile + ".dpapi"));
            if (!path.StartsWith(
                    root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new AcceptanceFailure("profile_path_invalid");
            }
            if (!File.Exists(path))
            {
                throw new AcceptanceFailure("profile_missing");
            }
            if (!HasRestrictedAcl(path))
            {
                throw new AcceptanceFailure("profile_acl_invalid");
            }

            byte[] encrypted = null;
            byte[] plaintext = null;
            try
            {
                encrypted = File.ReadAllBytes(path);
                plaintext = ProtectedData.Unprotect(
                    encrypted,
                    null,
                    DataProtectionScope.CurrentUser);
                var serializer = new DataContractJsonSerializer(typeof(QaCredentialProfile));
                using (var source = new MemoryStream(plaintext, writable: false))
                {
                    var result = serializer.ReadObject(source) as QaCredentialProfile;
                    if (result == null || !result.IsValid())
                    {
                        throw new AcceptanceFailure("profile_invalid");
                    }
                    return result;
                }
            }
            catch (AcceptanceFailure)
            {
                throw;
            }
            catch (Exception)
            {
                throw new AcceptanceFailure("profile_decrypt_failed");
            }
            finally
            {
                Clear(encrypted);
                Clear(plaintext);
            }
        }

        private static bool HasRestrictedAcl(string path)
        {
            try
            {
                var acl = File.GetAccessControl(path);
                if (!acl.AreAccessRulesProtected)
                {
                    return false;
                }

                var currentSid = WindowsIdentity.GetCurrent().User;
                var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                if (currentSid == null)
                {
                    return false;
                }

                var fullControl = (long)FileSystemRights.FullControl;
                var currentAllowed = false;
                var systemAllowed = false;
                var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    var sid = rule.IdentityReference as SecurityIdentifier;
                    var allow = rule.AccessControlType == AccessControlType.Allow;
                    var hasFullControl = ((long)rule.FileSystemRights & fullControl) == fullControl;
                    if (sid != null && sid.Equals(currentSid) && allow && hasFullControl)
                    {
                        currentAllowed = true;
                        continue;
                    }
                    if (sid != null && sid.Equals(systemSid) && allow && hasFullControl)
                    {
                        systemAllowed = true;
                        continue;
                    }
                    return false;
                }

                return currentAllowed && systemAllowed;
            }
            catch
            {
                return false;
            }
        }

        private static bool LogsDoNotContainProfileValues(QaCredentialProfile profile)
        {
            if (profile == null || !Directory.Exists(AppPaths.LogsDirectory))
            {
                return true;
            }

            foreach (var path in Directory.EnumerateFiles(
                AppPaths.LogsDirectory,
                "*.log",
                SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                if (info.Length > 2 * 1024 * 1024)
                {
                    return false;
                }

                var contents = File.ReadAllText(path);
                if (SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        profile.Credential) ||
                    SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        profile.ShopCode) ||
                    SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        profile.StaffCode) ||
                    SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        profile.BaseUrl) ||
                    SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        profile.DeviceIdentifier) ||
                    SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        profile.DeviceDisplayName))
                {
                    return false;
                }
            }
            return true;
        }

        private static void WriteReport(string outputDirectory, AcceptanceReport report)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                var serializer = new DataContractJsonSerializer(typeof(AcceptanceReport));
                using (var output = new FileStream(
                    Path.Combine(outputDirectory, "staging-acceptance-result.json"),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    serializer.WriteObject(output, report);
                }
            }
            catch
            {
                // The caller gets the non-zero acceptance result even if a local
                // evidence disk failure prevents the redacted report from writing.
            }
        }

        private static bool IsExpectedDataDirectory()
        {
            return string.Equals(
                Path.GetFullPath(AppPaths.DataDirectory),
                Path.GetFullPath(FixedDataDirectory),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeProfileName(string profile)
        {
            var value = (profile ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 64)
            {
                throw new AcceptanceFailure("profile_name_invalid");
            }
            for (var index = 0; index < value.Length; index++)
            {
                var ch = value[index];
                if (!((ch >= 'a' && ch <= 'z') ||
                      (ch >= '0' && ch <= '9') ||
                      (ch == '-' && index > 0)))
                {
                    throw new AcceptanceFailure("profile_name_invalid");
                }
            }
            return value;
        }

        private static string SafeCode(string value)
        {
            var code = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (code.Length == 0 || code.Length > 120)
            {
                return "unknown";
            }
            for (var index = 0; index < code.Length; index++)
            {
                var ch = code[index];
                if (!((ch >= 'a' && ch <= 'z') ||
                      (ch >= '0' && ch <= '9') || ch == '_' || ch == '-' ||
                      ch == '.' || ch == ':'))
                {
                    return "invalid_code";
                }
            }
            return code;
        }

        private static string RedactTechnicalIdentifier(string value)
        {
            return PosTechnicalIdentifier.Redact(value);
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                Array.Clear(value, 0, value.Length);
            }
        }

        private sealed class AcceptanceFailure : Exception
        {
            internal AcceptanceFailure(string code)
            {
                Code = SafeCode(code);
            }

            internal string Code { get; }
        }

        [DataContract]
        private sealed class QaCredentialProfile
        {
            [DataMember(Name = "baseUrl")]
            public string BaseUrl { get; set; }

            [DataMember(Name = "credential")]
            public string Credential { get; set; }

            [DataMember(Name = "deviceDisplayName")]
            public string DeviceDisplayName { get; set; }

            [DataMember(Name = "deviceIdentifier")]
            public string DeviceIdentifier { get; set; }

            [DataMember(Name = "expiresAt")]
            public string ExpiresAt { get; set; }

            [DataMember(Name = "profileVersion")]
            public int ProfileVersion { get; set; }

            [DataMember(Name = "shopCode")]
            public string ShopCode { get; set; }

            [DataMember(Name = "staffCode")]
            public string StaffCode { get; set; }

            internal Uri BaseUri => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
                ? uri
                : null;

            internal bool IsValid()
            {
                var uri = BaseUri;
                if (ProfileVersion != 2 || uri == null ||
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !uri.IsDefaultPort ||
                    !string.Equals(uri.Host, AllowedStagingHost, StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(uri.UserInfo) ||
                    !string.IsNullOrEmpty(uri.Query) ||
                    !string.IsNullOrEmpty(uri.Fragment) ||
                    uri.AbsolutePath != "/" ||
                    string.IsNullOrWhiteSpace(ShopCode) ||
                    string.IsNullOrWhiteSpace(StaffCode) ||
                    string.IsNullOrWhiteSpace(Credential) ||
                    !IsQaCode(ShopCode) ||
                    !IsQaCode(StaffCode) ||
                    !IsQaDeviceIdentifier(DeviceIdentifier) ||
                    !IsQaDeviceDisplayName(DeviceDisplayName))
                {
                    return false;
                }
                if (string.IsNullOrWhiteSpace(ExpiresAt))
                {
                    return true;
                }
                return DateTimeOffset.TryParse(
                    ExpiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var expiry) && expiry.ToUniversalTime() > DateTimeOffset.UtcNow;
            }

            internal void Clear()
            {
                BaseUrl = null;
                Credential = null;
                DeviceDisplayName = null;
                DeviceIdentifier = null;
                ExpiresAt = null;
                ShopCode = null;
                StaffCode = null;
            }

            private static bool IsQaCode(string value)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 64)
                {
                    return false;
                }

                foreach (var character in value.Trim())
                {
                    if (!char.IsLetterOrDigit(character) && character != '.' &&
                        character != '_' && character != '-')
                    {
                        return false;
                    }
                }

                return char.IsLetterOrDigit(value.Trim()[0]);
            }

            private static bool IsQaDeviceIdentifier(string value)
            {
                if (string.IsNullOrWhiteSpace(value) ||
                    !value.StartsWith("win7pos:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Guid parsed;
                return Guid.TryParse(value.Substring("win7pos:".Length), out parsed);
            }

            private static bool IsQaDeviceDisplayName(string value)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 32 ||
                    !value.StartsWith("CASSA-", StringComparison.Ordinal))
                {
                    return false;
                }

                foreach (var character in value)
                {
                    if (!((character >= 'A' && character <= 'Z') ||
                          (character >= '0' && character <= '9') || character == '-'))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        [DataContract]
        private sealed class AcceptanceReport
        {
            [DataMember(Name = "authenticationDenied")]
            public bool AuthenticationDenied { get; set; }

            [DataMember(Name = "bootstrapCode")]
            public string BootstrapCode { get; set; }

            [DataMember(Name = "catalogStarted")]
            public bool CatalogStarted { get; set; }

            [DataMember(Name = "catalogDrained")]
            public bool CatalogDrained { get; set; }

            [DataMember(Name = "catalogPages")]
            public int CatalogPages { get; set; }

            [DataMember(Name = "cfRay")]
            public string CfRay { get; set; }

            [DataMember(Name = "clientRequestId")]
            public string ClientRequestId { get; set; }

            [DataMember(Name = "code")]
            public string Code { get; set; }

            [DataMember(Name = "completedAtUtc")]
            public string CompletedAtUtc { get; set; }

            [DataMember(Name = "dataDirectoryIsolated")]
            public bool DataDirectoryIsolated { get; set; }

            [DataMember(Name = "deterministicRetrySuppressed")]
            public bool DeterministicRetrySuppressed { get; set; }

            [DataMember(Name = "exactnessVerified")]
            public bool ExactnessVerified { get; set; }

            [DataMember(Name = "exceptionType")]
            public string ExceptionType { get; set; }

            [DataMember(Name = "failureStage")]
            public string FailureStage { get; set; }

            [DataMember(Name = "firstLoginSucceeded")]
            public bool FirstLoginSucceeded { get; set; }

            [DataMember(Name = "httpSuccess")]
            public bool HttpSuccess { get; set; }

            [DataMember(Name = "httpStatus")]
            public int? HttpStatus { get; set; }

            [DataMember(Name = "localActiveCategories")]
            public long LocalActiveCategories { get; set; }

            [DataMember(Name = "localActiveProducts")]
            public long LocalActiveProducts { get; set; }

            [DataMember(Name = "localActiveSuppliers")]
            public long LocalActiveSuppliers { get; set; }

            [DataMember(Name = "localProductsPositive")]
            public bool LocalProductsPositive { get; set; }

            [DataMember(Name = "logRedactionPassed")]
            public bool LogRedactionPassed { get; set; }

            [DataMember(Name = "logicalRuns")]
            public int LogicalRuns { get; set; }

            [DataMember(Name = "manifestActiveCategories")]
            public long ManifestActiveCategories { get; set; }

            [DataMember(Name = "manifestActiveProducts")]
            public long ManifestActiveProducts { get; set; }

            [DataMember(Name = "manifestActiveSuppliers")]
            public long ManifestActiveSuppliers { get; set; }

            [DataMember(Name = "passed")]
            public bool Passed { get; set; }

            [DataMember(Name = "pendingRemotePrices")]
            public long PendingRemotePrices { get; set; }

            [DataMember(Name = "posUnlocked")]
            public bool PosUnlocked { get; set; }

            [DataMember(Name = "previousDiagnosticCleared")]
            public bool PreviousDiagnosticCleared { get; set; }

            [DataMember(Name = "productListPopulated")]
            public bool ProductListPopulated { get; set; }

            [DataMember(Name = "profile")]
            public string Profile { get; set; }

            [DataMember(Name = "profileValidated")]
            public bool ProfileValidated { get; set; }

            [DataMember(Name = "requestReachedServer")]
            public bool RequestReachedServer { get; set; }

            [DataMember(Name = "retryable")]
            public bool Retryable { get; set; }

            [DataMember(Name = "rootCode")]
            public string RootCode { get; set; }

            [DataMember(Name = "saleSafe")]
            public bool SaleSafe { get; set; }

            [DataMember(Name = "deviceApprovalState")]
            public string DeviceApprovalState { get; set; }

            [DataMember(Name = "serverRequestId")]
            public string ServerRequestId { get; set; }

            [DataMember(Name = "startedAtUtc")]
            public string StartedAtUtc { get; set; }

            [DataMember(Name = "trustedSessionPersisted")]
            public bool TrustedSessionPersisted { get; set; }
        }
    }
}
