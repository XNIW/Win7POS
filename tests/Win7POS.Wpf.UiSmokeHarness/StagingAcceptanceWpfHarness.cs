using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
            @"C:\POSData\Win7POSArticleMutationAcceptance";
        private const string QaSecretsDirectory = @"C:\ProgramData\Win7POS\QaSecrets";
        private const string EvidenceStatusFail = "fail";
        private const string EvidenceStatusNotRun = "not_run";
        private const string EvidenceStatusPass = "pass";
        private static readonly string[] RequiredAcceptanceEvidenceFileNames =
        {
            "preflight.txt",
            "exact-main-build.txt",
            "contract-digests.txt",
            "first-login-result.json",
            "catalog-exactness.json",
            "article-mutation-results.json",
            "local-outbox-state.json",
            "price-history-counts.txt",
            "stock-movement-counts.txt",
            "no-echo-result.txt",
            "redaction-scan.txt",
            "article-mutation-product-editor-1024x768.png",
            "article-mutation-sync-center-conflict-1024x768.png",
            "staging-acceptance-products-readonly.png",
            "CLEANUP-MANIFEST.json",
            "NEXT-CODEX-MAC-FINAL-CLEANUP.md"
        };

        internal static async Task<bool> RunAsync(
            string profileName,
            string runId,
            string outputDirectory)
        {
            var report = new AcceptanceReport
            {
                StartedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Profile = SafeProfileName(profileName),
                RunId = SafeRunId(runId),
                DataDirectoryIsolated = IsExpectedDataDirectory(),
                LogicalRuns = 1,
                EvidenceCompletenessStatus = EvidenceStatusNotRun,
                EvidenceRedactionStatus = EvidenceStatusNotRun,
                LogRedactionStatus = EvidenceStatusNotRun
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
                var trustedStore = new PosTrustedDeviceStore();
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
                            trustedStore,
                            host)
                        .BootstrapAsync(
                            new PosAdminWebOptions(profile.BaseUri),
                            request,
                            profile.Credential,
                            timeout.Token)
                        .ConfigureAwait(true);
                }
                request.Credential = string.Empty;

                ApplyBootstrapResult(report, bootstrap);

                if (bootstrap == null || !bootstrap.CanOpenPos)
                {
                    throw new AcceptanceFailure(
                        "bootstrap_" + SafeCode(bootstrap?.FailureStage) + "_" +
                        SafeCode(bootstrap?.RootCode));
                }

                PosTrustedDeviceSession trustedSession;
                report.OfflineAuthorizationValid =
                    trustedStore.TryRead(out trustedSession) &&
                    trustedSession.OfflineAuthorizationAttested &&
                    PosOfflineAuthorizationLeasePolicy.Evaluate(
                        trustedSession,
                        DateTimeOffset.UtcNow).Allowed;
                if (!report.OfflineAuthorizationValid)
                {
                    RecordHarnessFailure(
                        report,
                        "offline_authorization",
                        "offline_authorization_invalid");
                    throw new AcceptanceFailure(
                        "offline_authorization_invalid");
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
                var parsedPages = 0L;
                long.TryParse(
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

                StagingArticleMutationAcceptance
                    .StagingArticleMutationAcceptanceResult articleResult;
                using (var mutationTimeout = new CancellationTokenSource(
                    TimeSpan.FromMinutes(7)))
                {
                    articleResult = await StagingArticleMutationAcceptance
                        .RunAsync(
                            factory,
                            host,
                            trustedSession,
                            profile.BaseUri,
                            report.RunId,
                            outputDirectory,
                            mutationTimeout.Token)
                        .ConfigureAwait(true);
                }
                host = articleResult.ActiveHost ?? host;
                ApplyArticleResult(report, articleResult);
                if (!articleResult.Passed)
                {
                    RecordHarnessFailure(
                        report,
                        "article_mutations",
                        articleResult.Code);
                    throw new AcceptanceFailure(articleResult.Code);
                }

                report.LogRedactionPassed =
                    LogsDoNotContainProfileValues(profile) &&
                    LogsDoNotContainValue(report.RunId);
                report.LogRedactionStatus = EvidenceStatus(
                    report.LogRedactionPassed == true);
                WriteFirstLoginEvidence(outputDirectory, report);
                WriteCatalogEvidence(outputDirectory, report);
                report.EvidenceComplete =
                    RequiredAcceptanceEvidenceFilesExist(
                        outputDirectory,
                        includeRedactionScan: false);
                report.EvidenceCompletenessStatus = EvidenceStatus(
                    report.EvidenceComplete == true);
                report.EvidenceRedactionPassed =
                    EvidenceFilesDoNotContainSecrets(
                        outputDirectory,
                        profile,
                        trustedSession);
                report.EvidenceRedactionStatus = EvidenceStatus(
                    report.EvidenceRedactionPassed == true);
                WriteRedactionEvidence(outputDirectory, report);
                report.EvidenceComplete =
                    report.EvidenceComplete == true &&
                    RequiredAcceptanceEvidenceFilesExist(
                        outputDirectory,
                        includeRedactionScan: true);
                report.EvidenceCompletenessStatus = EvidenceStatus(
                    report.EvidenceComplete == true);
                WriteRedactionEvidence(outputDirectory, report);
                if (report.EvidenceComplete != true)
                {
                    RecordHarnessFailure(
                        report,
                        "security_evidence",
                        "acceptance_evidence_incomplete");
                    throw new AcceptanceFailure(
                        "acceptance_evidence_incomplete");
                }
                if (report.LogRedactionPassed != true ||
                    report.EvidenceRedactionPassed != true)
                {
                    RecordHarnessFailure(
                        report,
                        "security_evidence",
                        "secret_or_payload_redaction_failed");
                    throw new AcceptanceFailure(
                        "secret_or_payload_redaction_failed");
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
                report.CompletedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                WriteFirstLoginEvidence(outputDirectory, report);
                WriteCatalogEvidence(outputDirectory, report);
                if (!File.Exists(Path.Combine(
                    outputDirectory,
                    "redaction-scan.txt")))
                {
                    WriteRedactionEvidence(outputDirectory, report);
                }
                WriteReport(outputDirectory, report);
                profile?.Clear();
            }

            return report.Passed;
        }

        private static void ApplyBootstrapResult(
            AcceptanceReport report,
            PosOnlineBootstrapResult bootstrap)
        {
            if (report == null)
            {
                return;
            }

            report.HttpSuccess = bootstrap != null &&
                bootstrap.HttpStatus.HasValue &&
                bootstrap.HttpStatus.Value >= 200 &&
                bootstrap.HttpStatus.Value <= 299;
            report.BootstrapCode = SafeCode(bootstrap?.Code);
            report.FailureStage = SafeCode(bootstrap?.FailureStage);
            report.RootCode = SafeCode(bootstrap?.RootCode);
            report.HttpStatus = bootstrap?.HttpStatus;
            report.FirstLoginHttpStatus = bootstrap?.FirstLoginHttpStatus;
            report.AuthenticationDenied =
                bootstrap != null && bootstrap.AuthenticationDenied;
            report.Retryable = bootstrap != null && bootstrap.Retryable;
            report.DeviceApprovalState =
                SafeCode(bootstrap?.DeviceApprovalState);
            report.ClientRequestId =
                RedactTechnicalIdentifier(bootstrap?.ClientRequestId);
            report.ServerRequestId =
                RedactTechnicalIdentifier(bootstrap?.ServerRequestId);
            report.CfRay =
                RedactTechnicalIdentifier(bootstrap?.CfRay);
            report.ExceptionType = SafeCode(bootstrap?.ExceptionType);
            report.RequestReachedServer =
                bootstrap != null && bootstrap.RequestReachedServer;
            report.FirstLoginSucceeded =
                bootstrap != null && bootstrap.FirstLoginSucceeded;
            report.TrustedSessionPersisted =
                bootstrap != null && bootstrap.TrustedSessionPersisted;
            report.CatalogStarted =
                bootstrap != null && bootstrap.CatalogStarted;
            report.CatalogDrained = bootstrap != null &&
                bootstrap.CatalogCompleted &&
                !bootstrap.RequiresRetry;
            report.SaleSafe =
                bootstrap != null && bootstrap.CatalogSaleSafe;
            report.DeterministicRetrySuppressed = bootstrap == null ||
                !CatalogRetryPolicy.IsDeterministicRevisionFailure(
                    bootstrap.Code) ||
                !bootstrap.RequiresRetry;

            var diagnostic = bootstrap?.Diagnostic;
            if (diagnostic == null)
            {
                return;
            }

            report.CatalogAttempt = diagnostic.AttemptNumber;
            report.CatalogCode = SafeCode(diagnostic.Code);
            report.CatalogFailureStage = SafeCode(diagnostic.Stage);
            report.CatalogHttpStatus = diagnostic.HttpStatus;
            report.CatalogPage = diagnostic.PageNumber;
            report.CatalogPagesProcessed = diagnostic.PagesProcessed;
            report.CatalogRetryable = diagnostic.Retryable;
            report.CatalogRowsReceived = diagnostic.RowsReceived;
            report.CatalogRowsApplied = diagnostic.RowsApplied;
            if (!report.CatalogDrained)
            {
                report.CatalogPages = diagnostic.PagesProcessed;
            }
        }

        private static void ApplyArticleResult(
            AcceptanceReport report,
            StagingArticleMutationAcceptance
                .StagingArticleMutationAcceptanceResult article)
        {
            report.ArticleMutationsPassed = article.Passed;
            report.ArticleCode = SafeCode(article.Code);
            report.HarnessRestartSurvived =
                article.HarnessRestartSurvived;
            report.OfflineCreateAtomic = article.OfflineCreateAtomic;
            report.DependentEditPersisted =
                article.DependentEditPersisted;
            report.RemoteIdentityAssigned =
                article.RemoteIdentityAssigned;
            report.DependentSequenceApplied =
                article.DependentSequenceApplied;
            report.SameMutationReplay = article.SameMutationReplay;
            report.DifferentPayloadMismatch =
                article.DifferentPayloadMismatch;
            report.StaleConflict = article.StaleConflict;
            report.UnrelatedProductContinued =
                article.UnrelatedProductContinued;
            report.CanonicalPull = article.CanonicalPull;
            report.ZeroEcho = article.ZeroEcho;
            report.ArticleWaitingDependency =
                article.WaitingDependency;
            report.ArticlePending = article.Pending;
            report.ArticleInProgress = article.InProgress;
            report.ArticleRetryWait = article.RetryWait;
            report.ArticleBlockedConflicts =
                article.BlockedConflicts;
            report.PriceHistoryDuplicateGroups =
                article.PriceHistoryDuplicateGroups;
            report.StockDuplicateGroups =
                article.StockDuplicateGroups;
            report.SalesRows = article.SalesRows;
            report.HardwareActions = article.HardwareActions;
            report.CleanupManifestCreated =
                article.CleanupManifestCreated;
        }

        private static string EvidenceStatus(bool passed)
        {
            return passed ? EvidenceStatusPass : EvidenceStatusFail;
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
            if (!localOperatorTyped)
            {
                return "FAIL local_operator_diagnostic";
            }

            var diagnostic = new PosRuntimeDiagnostic(
                "catalog.pull",
                "server_response",
                "http_5xx",
                503,
                false,
                false,
                1,
                135,
                134,
                7920,
                0,
                true,
                false,
                "fixture-client-request",
                string.Empty,
                "fixture-cf-ray",
                "inc-fixture",
                DateTimeOffset.UtcNow,
                63000,
                "HttpRequestException",
                "bounded fixture");
            var catalogOutcome = PosCatalogPullOutcome.Failure(
                "http_5xx",
                false,
                true,
                134,
                diagnostic: diagnostic);
            var bootstrap = PosOnlineBootstrapResult.CatalogIncomplete(
                "http_5xx",
                "bounded fixture",
                false,
                false,
                catalogOutcome,
                httpStatus: 200);
            var evidenceReport = new AcceptanceReport
            {
                EvidenceCompletenessStatus = EvidenceStatusNotRun,
                EvidenceRedactionStatus = EvidenceStatusNotRun,
                LogRedactionStatus = EvidenceStatusNotRun
            };
            ApplyBootstrapResult(evidenceReport, bootstrap);
            var catalogFailureTyped =
                evidenceReport.FirstLoginHttpStatus == 200 &&
                evidenceReport.HttpStatus == 503 &&
                evidenceReport.CatalogAttempt == 1 &&
                evidenceReport.CatalogCode == "http_5xx" &&
                evidenceReport.CatalogFailureStage == "server_response" &&
                evidenceReport.CatalogHttpStatus == 503 &&
                evidenceReport.CatalogPage == 135 &&
                evidenceReport.CatalogPages == 134 &&
                evidenceReport.CatalogPagesProcessed == 134 &&
                evidenceReport.CatalogRetryable == false &&
                evidenceReport.CatalogRowsReceived == 7920 &&
                evidenceReport.CatalogRowsApplied == 0;
            if (!catalogFailureTyped)
            {
                return "FAIL catalog_failure_evidence";
            }

            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "win7pos-redaction-separation-" +
                Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDirectory);
                if (!EvidenceFilesDoNotContainSecrets(
                        tempDirectory,
                        null,
                        null) ||
                    RequiredAcceptanceEvidenceFilesExist(
                        tempDirectory,
                        includeRedactionScan: false))
                {
                    return "FAIL redaction_completeness_separation";
                }

                foreach (var fileName in
                    RequiredAcceptanceEvidenceFileNames.Where(
                        name => !string.Equals(
                            name,
                            "redaction-scan.txt",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    File.WriteAllBytes(
                        Path.Combine(tempDirectory, fileName),
                        new byte[] { 1 });
                }
                if (!RequiredAcceptanceEvidenceFilesExist(
                        tempDirectory,
                        includeRedactionScan: false))
                {
                    return "FAIL complete_evidence_fixture";
                }

                File.Delete(Path.Combine(
                    tempDirectory,
                    "local-outbox-state.json"));
                if (RequiredAcceptanceEvidenceFilesExist(
                        tempDirectory,
                        includeRedactionScan: false))
                {
                    return "FAIL missing_non_screenshot_evidence";
                }

                WriteRedactionEvidence(tempDirectory, evidenceReport);
                var redactionState = File.ReadAllText(Path.Combine(
                    tempDirectory,
                    "redaction-scan.txt"));
                if (redactionState.IndexOf(
                        "logRedactionStatus=NOT_RUN",
                        StringComparison.Ordinal) < 0 ||
                    redactionState.IndexOf(
                        "evidenceRedactionStatus=NOT_RUN",
                        StringComparison.Ordinal) < 0 ||
                    redactionState.IndexOf(
                        "evidenceCompletenessStatus=NOT_RUN",
                        StringComparison.Ordinal) < 0)
                {
                    return "FAIL redaction_not_run_serialization";
                }
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }

            return "PASS diagnostics=typed localOperator=typed " +
                "catalogFailureEvidence=typed redactionState=separated";
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
                RedactVisibleProductRows(window);
                window.UpdateLayout();
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

        private static void RedactVisibleProductRows(DependencyObject root)
        {
            if (root == null) return;
            var textBlock = root as TextBlock;
            if (textBlock != null && HasVisualAncestor<DataGridRow>(textBlock))
            {
                textBlock.SetCurrentValue(
                    TextBlock.TextProperty,
                    "[REDACTED]");
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index += 1)
            {
                RedactVisibleProductRows(
                    VisualTreeHelper.GetChild(root, index));
            }
        }

        private static bool HasVisualAncestor<T>(DependencyObject child)
            where T : DependencyObject
        {
            var current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
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

        private static bool LogsDoNotContainValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !Directory.Exists(AppPaths.LogsDirectory))
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
                    return false;
                if (File.ReadAllText(path).IndexOf(
                        value,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool EvidenceFilesDoNotContainSecrets(
            string outputDirectory,
            QaCredentialProfile profile,
            PosTrustedDeviceSession trustedSession)
        {
            if (!Directory.Exists(outputDirectory))
                return false;
            var forbiddenMarkers = new[]
            {
                "\"deviceToken\"",
                "\"sessionToken\"",
                "canonical_payload_json",
                "intent_json",
                "payload_json"
            };
            foreach (var path in Directory.EnumerateFiles(
                outputDirectory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(path);
                if (string.Equals(
                        extension,
                        ".png",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var pngInfo = new FileInfo(path);
                    if (pngInfo.Length <= 0 || pngInfo.Length > 5 * 1024 * 1024)
                        return false;
                    var bytes = File.ReadAllBytes(path);
                    if (ContainsUtf8Value(bytes, profile?.Credential) ||
                        ContainsUtf8Value(bytes, trustedSession?.DeviceToken) ||
                        ContainsUtf8Value(bytes, trustedSession?.SessionToken))
                    {
                        return false;
                    }
                    continue;
                }
                if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var info = new FileInfo(path);
                if (info.Length > 2 * 1024 * 1024)
                    return false;
                var contents = File.ReadAllText(path);
                if (SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        profile?.Credential) ||
                    SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        trustedSession?.DeviceToken) ||
                    SensitiveValueLogScanPolicy.ContainsSensitiveValue(
                        contents,
                        trustedSession?.SessionToken) ||
                    forbiddenMarkers.Any(marker =>
                        contents.IndexOf(
                            marker,
                            StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool RequiredAcceptanceEvidenceFilesExist(
            string outputDirectory,
            bool includeRedactionScan)
        {
            if (!Directory.Exists(outputDirectory))
            {
                return false;
            }

            return RequiredAcceptanceEvidenceFileNames
                .Where(fileName =>
                    includeRedactionScan ||
                    !string.Equals(
                        fileName,
                        "redaction-scan.txt",
                        StringComparison.OrdinalIgnoreCase))
                .All(fileName =>
            {
                var path = Path.Combine(outputDirectory, fileName);
                var maximumLength = string.Equals(
                    Path.GetExtension(path),
                    ".png",
                    StringComparison.OrdinalIgnoreCase)
                    ? 5 * 1024 * 1024
                    : 2 * 1024 * 1024;
                return File.Exists(path) &&
                    new FileInfo(path).Length > 0 &&
                    new FileInfo(path).Length <= maximumLength;
            });
        }

        private static bool ContainsUtf8Value(byte[] haystack, string value)
        {
            if (haystack == null || haystack.Length == 0 ||
                string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            var needle = Encoding.UTF8.GetBytes(value);
            if (needle.Length < 4 || needle.Length > haystack.Length)
                return false;
            for (var start = 0;
                start <= haystack.Length - needle.Length;
                start += 1)
            {
                var match = true;
                for (var index = 0; index < needle.Length; index += 1)
                {
                    if (haystack[start + index] == needle[index]) continue;
                    match = false;
                    break;
                }
                if (match) return true;
            }
            return false;
        }

        private static void WriteFirstLoginEvidence(
            string outputDirectory,
            AcceptanceReport report)
        {
            WriteEvidenceJson(
                Path.Combine(outputDirectory, "first-login-result.json"),
                new FirstLoginEvidence
                {
                    FirstLoginSucceeded = report.FirstLoginSucceeded,
                    HttpStatus = report.FirstLoginHttpStatus,
                    OfflineAuthorizationValid =
                        report.OfflineAuthorizationValid,
                    PosUnlocked = report.PosUnlocked,
                    TrustedSessionPersisted =
                        report.TrustedSessionPersisted
                });
        }

        private static void WriteCatalogEvidence(
            string outputDirectory,
            AcceptanceReport report)
        {
            WriteEvidenceJson(
                Path.Combine(outputDirectory, "catalog-exactness.json"),
                new CatalogEvidence
                {
                    Attempt = report.CatalogAttempt,
                    Code = string.IsNullOrWhiteSpace(report.CatalogCode)
                        ? report.RootCode
                        : report.CatalogCode,
                    CatalogDrained = report.CatalogDrained,
                    ExactnessVerified = report.ExactnessVerified,
                    FailureStage = string.IsNullOrWhiteSpace(
                        report.CatalogFailureStage)
                        ? report.FailureStage
                        : report.CatalogFailureStage,
                    HttpStatus =
                        report.CatalogHttpStatus ?? report.HttpStatus,
                    LocalActiveCategories =
                        report.LocalActiveCategories,
                    LocalActiveProducts = report.LocalActiveProducts,
                    LocalActiveSuppliers = report.LocalActiveSuppliers,
                    ManifestActiveCategories =
                        report.ManifestActiveCategories,
                    ManifestActiveProducts =
                        report.ManifestActiveProducts,
                    ManifestActiveSuppliers =
                        report.ManifestActiveSuppliers,
                    Page = report.CatalogPage,
                    Pages = report.CatalogPages,
                    PagesProcessed = report.CatalogPagesProcessed,
                    PendingRemotePrices = report.PendingRemotePrices,
                    Retryable =
                        report.CatalogRetryable ?? report.Retryable,
                    RowsApplied = report.CatalogRowsApplied,
                    RowsReceived = report.CatalogRowsReceived,
                    SaleSafe = report.SaleSafe
                });
        }

        private static void WriteRedactionEvidence(
            string outputDirectory,
            AcceptanceReport report)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(
                    Path.Combine(outputDirectory, "redaction-scan.txt"),
                    "logRedactionStatus=" +
                    (report.LogRedactionStatus ??
                        EvidenceStatusNotRun).ToUpperInvariant() +
                    Environment.NewLine +
                    "evidenceRedactionStatus=" +
                    (report.EvidenceRedactionStatus ??
                        EvidenceStatusNotRun).ToUpperInvariant() +
                    Environment.NewLine +
                    "evidenceCompletenessStatus=" +
                    (report.EvidenceCompletenessStatus ??
                        EvidenceStatusNotRun).ToUpperInvariant() +
                    Environment.NewLine +
                    "requestBodiesCaptured=False" +
                    Environment.NewLine +
                    "hardwareActions=" +
                    report.HardwareActions.ToString(
                        CultureInfo.InvariantCulture) +
                    Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                report.Passed = false;
                report.Code = "redaction_evidence_write_failed";
            }
        }

        private static void WriteEvidenceJson(string path, object value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var serializer = new DataContractJsonSerializer(value.GetType());
                using (var output = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    serializer.WriteObject(output, value);
                }
            }
            catch
            {
                // The main result remains authoritative for evidence I/O failure.
            }
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

        private static string SafeRunId(string runId)
        {
            var value = (runId ?? string.Empty).Trim();
            if (value.Length == 0 ||
                value.Length > 64 ||
                !value.StartsWith("ASUSART_", StringComparison.Ordinal))
            {
                throw new AcceptanceFailure("run_id_invalid");
            }
            foreach (var character in value)
            {
                if (!((character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9') ||
                      character == '_'))
                {
                    throw new AcceptanceFailure("run_id_invalid");
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
            [DataMember(Name = "articleBlockedConflicts")]
            public long ArticleBlockedConflicts { get; set; }

            [DataMember(Name = "articleCode")]
            public string ArticleCode { get; set; }

            [DataMember(Name = "articleInProgress")]
            public long ArticleInProgress { get; set; }

            [DataMember(Name = "articleMutationsPassed")]
            public bool ArticleMutationsPassed { get; set; }

            [DataMember(Name = "articlePending")]
            public long ArticlePending { get; set; }

            [DataMember(Name = "articleRetryWait")]
            public long ArticleRetryWait { get; set; }

            [DataMember(Name = "articleWaitingDependency")]
            public long ArticleWaitingDependency { get; set; }

            [DataMember(Name = "authenticationDenied")]
            public bool AuthenticationDenied { get; set; }

            [DataMember(Name = "bootstrapCode")]
            public string BootstrapCode { get; set; }

            [DataMember(Name = "catalogStarted")]
            public bool CatalogStarted { get; set; }

            [DataMember(Name = "catalogDrained")]
            public bool CatalogDrained { get; set; }

            [DataMember(Name = "catalogAttempt")]
            public int CatalogAttempt { get; set; }

            [DataMember(Name = "catalogCode")]
            public string CatalogCode { get; set; }

            [DataMember(Name = "catalogPage")]
            public long? CatalogPage { get; set; }

            [DataMember(Name = "catalogFailureStage")]
            public string CatalogFailureStage { get; set; }

            [DataMember(Name = "catalogHttpStatus")]
            public int? CatalogHttpStatus { get; set; }

            [DataMember(Name = "catalogPages")]
            public long CatalogPages { get; set; }

            [DataMember(Name = "catalogPagesProcessed")]
            public long CatalogPagesProcessed { get; set; }

            [DataMember(Name = "catalogRowsApplied")]
            public long CatalogRowsApplied { get; set; }

            [DataMember(Name = "catalogRowsReceived")]
            public long CatalogRowsReceived { get; set; }

            [DataMember(Name = "catalogRetryable")]
            public bool? CatalogRetryable { get; set; }

            [DataMember(Name = "cfRay")]
            public string CfRay { get; set; }

            [DataMember(Name = "clientRequestId")]
            public string ClientRequestId { get; set; }

            [DataMember(Name = "code")]
            public string Code { get; set; }

            [DataMember(Name = "completedAtUtc")]
            public string CompletedAtUtc { get; set; }

            [DataMember(Name = "canonicalPull")]
            public bool CanonicalPull { get; set; }

            [DataMember(Name = "cleanupManifestCreated")]
            public bool CleanupManifestCreated { get; set; }

            [DataMember(Name = "dataDirectoryIsolated")]
            public bool DataDirectoryIsolated { get; set; }

            [DataMember(Name = "dependentEditPersisted")]
            public bool DependentEditPersisted { get; set; }

            [DataMember(Name = "dependentSequenceApplied")]
            public bool DependentSequenceApplied { get; set; }

            [DataMember(Name = "deterministicRetrySuppressed")]
            public bool DeterministicRetrySuppressed { get; set; }

            [DataMember(Name = "differentPayloadMismatch")]
            public bool DifferentPayloadMismatch { get; set; }

            [DataMember(Name = "evidenceComplete")]
            public bool? EvidenceComplete { get; set; }

            [DataMember(Name = "evidenceCompletenessStatus")]
            public string EvidenceCompletenessStatus { get; set; }

            [DataMember(Name = "evidenceRedactionPassed")]
            public bool? EvidenceRedactionPassed { get; set; }

            [DataMember(Name = "evidenceRedactionStatus")]
            public string EvidenceRedactionStatus { get; set; }

            [DataMember(Name = "exactnessVerified")]
            public bool ExactnessVerified { get; set; }

            [DataMember(Name = "exceptionType")]
            public string ExceptionType { get; set; }

            [DataMember(Name = "failureStage")]
            public string FailureStage { get; set; }

            [DataMember(Name = "firstLoginSucceeded")]
            public bool FirstLoginSucceeded { get; set; }

            [DataMember(Name = "firstLoginHttpStatus")]
            public int? FirstLoginHttpStatus { get; set; }

            [DataMember(Name = "httpSuccess")]
            public bool HttpSuccess { get; set; }

            [DataMember(Name = "httpStatus")]
            public int? HttpStatus { get; set; }

            [DataMember(Name = "hardwareActions")]
            public int HardwareActions { get; set; }

            [DataMember(Name = "harnessRestartSurvived")]
            public bool HarnessRestartSurvived { get; set; }

            [DataMember(Name = "localActiveCategories")]
            public long LocalActiveCategories { get; set; }

            [DataMember(Name = "localActiveProducts")]
            public long LocalActiveProducts { get; set; }

            [DataMember(Name = "localActiveSuppliers")]
            public long LocalActiveSuppliers { get; set; }

            [DataMember(Name = "localProductsPositive")]
            public bool LocalProductsPositive { get; set; }

            [DataMember(Name = "logRedactionPassed")]
            public bool? LogRedactionPassed { get; set; }

            [DataMember(Name = "logRedactionStatus")]
            public string LogRedactionStatus { get; set; }

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

            [DataMember(Name = "offlineAuthorizationValid")]
            public bool OfflineAuthorizationValid { get; set; }

            [DataMember(Name = "offlineCreateAtomic")]
            public bool OfflineCreateAtomic { get; set; }

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

            [DataMember(Name = "priceHistoryDuplicateGroups")]
            public long PriceHistoryDuplicateGroups { get; set; }

            [DataMember(Name = "remoteIdentityAssigned")]
            public bool RemoteIdentityAssigned { get; set; }

            [DataMember(Name = "requestReachedServer")]
            public bool RequestReachedServer { get; set; }

            [DataMember(Name = "retryable")]
            public bool Retryable { get; set; }

            [DataMember(Name = "rootCode")]
            public string RootCode { get; set; }

            [DataMember(Name = "runId")]
            public string RunId { get; set; }

            [DataMember(Name = "saleSafe")]
            public bool SaleSafe { get; set; }

            [DataMember(Name = "salesRows")]
            public long SalesRows { get; set; }

            [DataMember(Name = "sameMutationReplay")]
            public bool SameMutationReplay { get; set; }

            [DataMember(Name = "deviceApprovalState")]
            public string DeviceApprovalState { get; set; }

            [DataMember(Name = "serverRequestId")]
            public string ServerRequestId { get; set; }

            [DataMember(Name = "startedAtUtc")]
            public string StartedAtUtc { get; set; }

            [DataMember(Name = "staleConflict")]
            public bool StaleConflict { get; set; }

            [DataMember(Name = "stockDuplicateGroups")]
            public long StockDuplicateGroups { get; set; }

            [DataMember(Name = "trustedSessionPersisted")]
            public bool TrustedSessionPersisted { get; set; }

            [DataMember(Name = "unrelatedProductContinued")]
            public bool UnrelatedProductContinued { get; set; }

            [DataMember(Name = "zeroEcho")]
            public bool ZeroEcho { get; set; }
        }

        [DataContract]
        private sealed class FirstLoginEvidence
        {
            [DataMember(Name = "firstLoginSucceeded")]
            public bool FirstLoginSucceeded { get; set; }

            [DataMember(Name = "httpStatus")]
            public int? HttpStatus { get; set; }

            [DataMember(Name = "offlineAuthorizationValid")]
            public bool OfflineAuthorizationValid { get; set; }

            [DataMember(Name = "posUnlocked")]
            public bool PosUnlocked { get; set; }

            [DataMember(Name = "trustedSessionPersisted")]
            public bool TrustedSessionPersisted { get; set; }
        }

        [DataContract]
        private sealed class CatalogEvidence
        {
            [DataMember(Name = "attempt")]
            public int Attempt { get; set; }

            [DataMember(Name = "catalogDrained")]
            public bool CatalogDrained { get; set; }

            [DataMember(Name = "code")]
            public string Code { get; set; }

            [DataMember(Name = "exactnessVerified")]
            public bool ExactnessVerified { get; set; }

            [DataMember(Name = "failureStage")]
            public string FailureStage { get; set; }

            [DataMember(Name = "httpStatus")]
            public int? HttpStatus { get; set; }

            [DataMember(Name = "localActiveCategories")]
            public long LocalActiveCategories { get; set; }

            [DataMember(Name = "localActiveProducts")]
            public long LocalActiveProducts { get; set; }

            [DataMember(Name = "localActiveSuppliers")]
            public long LocalActiveSuppliers { get; set; }

            [DataMember(Name = "manifestActiveCategories")]
            public long ManifestActiveCategories { get; set; }

            [DataMember(Name = "manifestActiveProducts")]
            public long ManifestActiveProducts { get; set; }

            [DataMember(Name = "manifestActiveSuppliers")]
            public long ManifestActiveSuppliers { get; set; }

            [DataMember(Name = "page")]
            public long? Page { get; set; }

            [DataMember(Name = "pages")]
            public long Pages { get; set; }

            [DataMember(Name = "pagesProcessed")]
            public long PagesProcessed { get; set; }

            [DataMember(Name = "pendingRemotePrices")]
            public long PendingRemotePrices { get; set; }

            [DataMember(Name = "retryable")]
            public bool Retryable { get; set; }

            [DataMember(Name = "rowsApplied")]
            public long RowsApplied { get; set; }

            [DataMember(Name = "rowsReceived")]
            public long RowsReceived { get; set; }

            [DataMember(Name = "saleSafe")]
            public bool SaleSafe { get; set; }
        }
    }
}
