using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Models;
using Win7POS.Core.Logging;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.CustomerDisplay;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Displays;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Core.Reports;
using Win7POS.Wpf.Printing;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class Program
    {
        private const string QaShopId = "qa-shop-local";
        private const string QaShopCode = "QA-SHOP";
        private const string QaOfflineShopName = "QA Offline Sandbox";
        private const string QaOfflineStaffCode = "qa-admin";
        private const string QaOfflineStaffId = "qa-admin-local";
        private const string QaOfflineCredential = "2468";
        private const int OfflineSalesSafetyVerifiedExitCode = 73;
        private static string _receiptAlignmentFailure = string.Empty;

        private sealed class HarnessPermissionService : IPermissionService
        {
            public bool Has(string permissionCode) => false;
            public void Demand(string permissionCode, string operationText)
                => throw new InvalidOperationException("Harness permission denied.");
            public bool CanOverride(string permissionCode) => false;
        }

        [STAThread]
        private static void Main(string[] args)
        {
            var dataDir = ValueAfter(args, "--data-dir");
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                MessageBox.Show("--data-dir is required.", "Win7POS UI Smoke Harness");
                return;
            }

            var physicalPrinterQa = HasArg(args, "--physical-printer-qa");
            var restrictedSeed = physicalPrinterQa ||
                                 HasArg(args, "--authorization-lease-smoke") ||
                                 HasArg(args, "--offline-sales-sandbox") ||
                                 (HasArg(args, "--seed") && HasArg(args, "--seed-trusted-session"));
            var verifyOfflineSalesSandboxSafety =
                HasArg(args, "--verify-offline-sales-sandbox-safety");
            if (restrictedSeed)
            {
                dataDir = EnsureSyntheticTrustedSessionSeedPath(dataDir);
            }

            Directory.CreateDirectory(dataDir);
            if (restrictedSeed)
            {
                dataDir = EnsureSyntheticTrustedSessionSeedPath(dataDir);
            }
            var automatedRun = HasArg(args, "--seed") ||
                               HasArg(args, "--authorization-lease-smoke") ||
                               HasArg(args, "--product-paging-dispatcher-smoke") ||
                               HasArg(args, "--bounded-logging-smoke") ||
                               HasArg(args, "--supplier-excel-wpf-viewmodel-smoke") ||
                               HasArg(args, "--offline-sales-sandbox") ||
                               HasArg(args, "--shell-window-state") ||
                               HasArg(args, "--printer-selection-binding") ||
                               HasArg(args, "--printer-presentation") ||
                               HasArg(args, "--pos-footer-layout") ||
                               HasArg(args, "--receipt-rendering-alignment") ||
                               HasArg(args, "--fiscal-direct-print") ||
                               physicalPrinterQa ||
                               HasArg(args, "--capture-ux-artifacts") ||
                               HasArg(args, "--capture-settings-audit") ||
                               verifyOfflineSalesSandboxSafety ||
                               HasArg(args, "--lifecycle");

            Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", dataDir);
            Environment.SetEnvironmentVariable("WIN7POS_SAFE_START", "1");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AddApplicationResources(app);
            app.DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                var detail = e.Exception.GetType().Name + ": " + e.Exception.Message;
                try { File.WriteAllText(Path.Combine(dataDir, "harness-error.txt"), detail); }
                catch { }
                if (!automatedRun)
                    MessageBox.Show(detail, "Win7POS UI Smoke Harness - unhandled");
                app.Shutdown(2);
            };
            app.Startup += async (_, __) =>
            {
                try
                {
                    if (HasArg(args, "--authorization-lease-smoke"))
                    {
                        var result = await AuthorizationLeaseWpfSmoke.RunAsync()
                            .ConfigureAwait(true);
                        File.WriteAllText(
                            Path.Combine(dataDir, "authorization-lease-smoke.txt"),
                            result,
                            Encoding.UTF8);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--product-paging-dispatcher-smoke"))
                    {
                        var result = await ProductPagingWpfSmoke.RunAsync().ConfigureAwait(true);
                        File.WriteAllText(
                            Path.Combine(dataDir, "product-paging-dispatcher-smoke.txt"),
                            result,
                            Encoding.UTF8);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--bounded-logging-smoke"))
                    {
                        var result = BoundedLoggingWpfSmoke.Run(dataDir);
                        File.WriteAllText(
                            Path.Combine(dataDir, "bounded-logging-smoke.txt"),
                            result,
                            Encoding.UTF8);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (SupplierExcelWpfViewModelSmoke.TryRun(args, out var supplierSmokeExitCode))
                    {
                        app.Shutdown(supplierSmokeExitCode);
                        return;
                    }

                    if (verifyOfflineSalesSandboxSafety)
                    {
                        await QaFixture.VerifyOfflineSalesSandboxSafetyAsync().ConfigureAwait(true);
                        QaFixture.VerifyTrustedDeviceSession(QaOfflineShopName);
                        app.Shutdown(OfflineSalesSafetyVerifiedExitCode);
                        return;
                    }

                    if (HasArg(args, "--offline-sales-sandbox"))
                    {
                        EnsureSyntheticTrustedSessionSeedPath(dataDir);
                        await QaFixture.SeedOfflineSalesSandboxAsync().ConfigureAwait(true);
                        QaFixture.SeedTrustedDeviceSession(QaOfflineShopName);
                        QaFixture.VerifyTrustedDeviceSession(QaOfflineShopName);
                        File.WriteAllText(
                            Path.Combine(dataDir, "QA-OFFLINE-SANDBOX.txt"),
                            "QA OFFLINE SANDBOX - TEST / NON FISCAL" + Environment.NewLine +
                            "Online sync, automatic receipt printing and cash drawer are disabled." + Environment.NewLine);
                        app.Shutdown(0);
                        return;
                    }

                    if (HasArg(args, "--seed"))
                    {
                        var seedTrustedSession = HasArg(args, "--seed-trusted-session");
                        if (seedTrustedSession)
                        {
                            EnsureSyntheticTrustedSessionSeedPath(dataDir);
                        }

                        await QaFixture.SeedAsync().ConfigureAwait(true);
                        if (seedTrustedSession)
                        {
                            QaFixture.SeedTrustedDeviceSession();
                        }

                        app.Shutdown(0);
                        return;
                    }

                    if (HasArg(args, "--shell-window-state"))
                    {
                        var result = RunShellWindowStateCheck();
                        File.WriteAllText(Path.Combine(dataDir, "shell-window-state.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    var launcher = new HarnessWindow();
                    app.MainWindow = launcher;
                    launcher.Show();
                    if (physicalPrinterQa)
                    {
                        if (!HasArg(args, "--no-drawer"))
                            throw new InvalidOperationException("--physical-printer-qa requires explicit --no-drawer.");
                        var printerName = ValueAfter(args, "--printer-name");
                        if (string.IsNullOrWhiteSpace(printerName))
                            throw new InvalidOperationException("--physical-printer-qa requires --printer-name.");
                        var result = await launcher.RunPhysicalPrinterQaAsync(
                            printerName,
                            dataDir).ConfigureAwait(true);
                        app.Shutdown(result.StartsWith(
                            "SUBMITTED_AWAITING_VISUAL_CONFIRMATION",
                            StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--fiscal-direct-print"))
                    {
                        var result = await launcher.RunFiscalDirectPrintCheckAsync().ConfigureAwait(true);
                        File.WriteAllText(Path.Combine(dataDir, "fiscal-direct-print.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--printer-selection-binding"))
                    {
                        var result = await launcher.RunPrinterSelectionBindingCheckAsync().ConfigureAwait(true);
                        File.WriteAllText(Path.Combine(dataDir, "printer-selection-binding.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--printer-presentation"))
                    {
                        var result = await launcher.RunPrinterPresentationCheckAsync().ConfigureAwait(true);
                        File.WriteAllText(Path.Combine(dataDir, "printer-presentation.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--pos-footer-layout"))
                    {
                        var result = launcher.RunPosFooterLayoutCheck();
                        File.WriteAllText(Path.Combine(dataDir, "pos-footer-layout.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--receipt-rendering-alignment"))
                    {
                        var result = launcher.RunReceiptRenderingAlignmentCheck();
                        File.WriteAllText(Path.Combine(dataDir, "receipt-rendering-alignment.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--capture-ux-artifacts"))
                    {
                        var result = await launcher.CaptureUxArtifactsAsync(dataDir).ConfigureAwait(true);
                        File.WriteAllText(Path.Combine(dataDir, "capture-ux-artifacts.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--capture-settings-audit"))
                    {
                        var result = await launcher.CaptureSettingsAuditAsync(dataDir).ConfigureAwait(true);
                        File.WriteAllText(Path.Combine(dataDir, "capture-settings-audit.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                        return;
                    }

                    if (HasArg(args, "--lifecycle"))
                    {
                        var result = await launcher.RunLifecycleMatrixAsync().ConfigureAwait(true);
                        File.WriteAllText(Path.Combine(dataDir, "lifecycle-result.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                    }
                }
                catch (Exception ex)
                {
                    var detail = ex.GetType().Name + ": " + ex.Message;
                    try { File.WriteAllText(Path.Combine(dataDir, "harness-error.txt"), detail); }
                    catch { }
                    if (!automatedRun)
                        MessageBox.Show(detail, "Win7POS UI Smoke Harness - startup");
                    app.Shutdown(2);
                }
            };
            app.Run();
        }

        private static string EnsureSyntheticTrustedSessionSeedPath(string dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir) || !Path.IsPathRooted(dataDir))
            {
                throw new InvalidOperationException(
                    "--seed-trusted-session requires an absolute QA data directory.");
            }

            var fullPath = Path.GetFullPath(dataDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var driveRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(driveRoot) ||
                driveRoot.Length != 3 ||
                !char.IsLetter(driveRoot[0]) ||
                driveRoot[1] != ':' ||
                driveRoot[2] != Path.DirectorySeparatorChar)
            {
                throw new InvalidOperationException(
                    "Synthetic trusted-session seed requires a local drive-letter path.");
            }

            var drive = new DriveInfo(driveRoot);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                throw new InvalidOperationException(
                    "Synthetic trusted-session seed requires a ready local fixed drive.");
            }

            var qaSegment = Path.DirectorySeparatorChar + "Win7POS-QA" + Path.DirectorySeparatorChar;
            var pathWithTerminator = fullPath + Path.DirectorySeparatorChar;
            if (pathWithTerminator.IndexOf(qaSegment, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    "--seed-trusted-session is restricted to a Win7POS-QA directory.");
            }

            var existingAncestor = fullPath;
            while (!Directory.Exists(existingAncestor))
            {
                if (File.Exists(existingAncestor))
                {
                    throw new InvalidOperationException(
                        "Synthetic trusted-session seed path has a non-directory ancestor.");
                }

                existingAncestor = Path.GetDirectoryName(existingAncestor);
                if (string.IsNullOrWhiteSpace(existingAncestor))
                {
                    throw new InvalidOperationException(
                        "Synthetic trusted-session seed path has no local directory ancestor.");
                }
            }

            for (var ancestor = new DirectoryInfo(existingAncestor);
                 ancestor != null;
                 ancestor = ancestor.Parent)
            {
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Synthetic trusted-session seed does not allow a reparse-point ancestor.");
                }
            }

            if (Directory.Exists(fullPath) &&
                (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Synthetic trusted-session seed does not allow a reparse-point data directory.");
            }

            if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                throw new InvalidOperationException(
                    "--seed-trusted-session requires a new or empty QA data directory.");
            }

            return fullPath;
        }

        private static string RunShellWindowStateCheck()
        {
            var shell = new Win7POS.Wpf.MainWindow();
            var stateChanged = typeof(Win7POS.Wpf.MainWindow).GetMethod(
                "OnStateChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (stateChanged == null) throw new MissingMethodException("MainWindow.OnStateChanged");
            var initial = shell.WindowState;
            shell.WindowState = WindowState.Normal;
            stateChanged.Invoke(shell, new object[] { EventArgs.Empty });
            var afterNormal = shell.WindowState;
            shell.WindowState = WindowState.Minimized;
            stateChanged.Invoke(shell, new object[] { EventArgs.Empty });
            var afterMinimize = shell.WindowState;
            shell.WindowState = WindowState.Normal;
            stateChanged.Invoke(shell, new object[] { EventArgs.Empty });
            var afterRestore = shell.WindowState;
                shell.CloseWithoutUserPrompt();

            var passed = initial == WindowState.Maximized &&
                         afterNormal == WindowState.Maximized &&
                         afterMinimize == WindowState.Minimized &&
                         afterRestore == WindowState.Maximized;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}: initial={1}; normalRequest={2}; minimizeRequest={3}; restoreRequest={4}",
                passed ? "PASS" : "FAIL",
                initial,
                afterNormal,
                afterMinimize,
                afterRestore);
        }

        private static void AddApplicationResources(Application app)
        {
            var baseUri = "pack://application:,,,/Win7POS.Wpf;component/";
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(baseUri + "Icons/MaterialSymbols.xaml", UriKind.Absolute)
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(baseUri + "ModernStyles.xaml", UriKind.Absolute)
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(baseUri + "Themes/DialogChrome.xaml", UriKind.Absolute)
            });
        }

        private static bool HasArg(IEnumerable<string> args, string expected)
        {
            return args.Any(x => string.Equals(x, expected, StringComparison.OrdinalIgnoreCase));
        }

        private static string ValueAfter(IReadOnlyList<string> args, string key)
        {
            for (var i = 0; i + 1 < args.Count; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return string.Empty;
        }

        private sealed class HarnessWindow : Window
        {
            private readonly TextBlock _status;
            private bool _running;

            public HarnessWindow()
            {
                Title = "Win7POS UI Smoke Harness";
                Width = 680;
                Height = 560;
                MinWidth = 560;
                MinHeight = 360;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Background = (Brush)new BrushConverter().ConvertFrom("#F1EDF7");

                var panel = new StackPanel { Margin = new Thickness(28) };
                panel.Children.Add(new TextBlock
                {
                    Text = "Win7POS QA-only runtime surfaces",
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)new BrushConverter().ConvertFrom("#4F2F68"),
                    Margin = new Thickness(0, 0, 0, 8)
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "Real application XAML with isolated synthetic data. This executable is not shipped.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20)
                });

                panel.Children.Add(MakeButton("Open DailyReportDialog", OpenDailyReport));
                panel.Children.Add(MakeButton("Open SalesRegisterDialog", OpenSalesRegister));
                panel.Children.Add(MakeButton("Open UserManagementDialog", OpenUserManagement));
                panel.Children.Add(MakeButton("Open SettingsHubDialog", OpenSettingsHub));
                panel.Children.Add(MakeButton("Open CustomerDisplaySettingsDialog", OpenCustomerDisplaySettings));
                panel.Children.Add(MakeButton("Run 20x lifecycle matrix", RunLifecycleMatrix));

                _status = new TextBlock
                {
                    Text = "Ready",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 18, 0, 0),
                    Foreground = (Brush)new BrushConverter().ConvertFrom("#4F2F68")
                };
                panel.Children.Add(_status);
                Content = panel;
                Closed += (_, __) => Application.Current.Shutdown(0);
            }

            private static Button MakeButton(string text, RoutedEventHandler click)
            {
                var button = new Button
                {
                    Content = text,
                    Height = 44,
                    MinWidth = 260,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 10),
                    Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
                };
                button.Click += click;
                return button;
            }

            private void OpenDailyReport(object sender, RoutedEventArgs e)
            {
                var dialog = CreateDailyReportDialog();
                dialog.Owner = this;
                dialog.ShowDialog();
            }

            private void OpenUserManagement(object sender, RoutedEventArgs e)
            {
                var dialog = CreateUserManagementDialog();
                dialog.Owner = this;
                dialog.ShowDialog();
            }

            private void OpenSalesRegister(object sender, RoutedEventArgs e)
            {
                var dialog = CreateSalesRegisterDialog();
                dialog.Owner = this;
                dialog.ShowDialog();
            }

            private void OpenSettingsHub(object sender, RoutedEventArgs e)
            {
                var dialog = new SettingsHubDialog { Owner = this };
                dialog.ShowDialog();
            }

            private void OpenCustomerDisplaySettings(object sender, RoutedEventArgs e)
            {
                var dialog = CreateCustomerDisplaySettingsDialog();
                dialog.Owner = this;
                dialog.ShowDialog();
            }

            private async void RunLifecycleMatrix(object sender, RoutedEventArgs e)
            {
                if (_running) return;
                try
                {
                    _status.Text = await RunLifecycleMatrixAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _status.Text = "FAIL: Lifecycle " + ex.GetType().Name + ": " + ex.Message;
                }
            }

            public async Task<string> RunLifecycleMatrixAsync()
            {
                if (_running) throw new InvalidOperationException("Lifecycle matrix is already running.");
                _running = true;
                IsEnabled = false;
                try
                {
                    DbInitializer.EnsureCreated(PosDbOptions.Default());
                    var languageHandlersBefore = GetLanguageChangedHandlerCount();
                    var displaySubscriptionsBefore = CustomerDisplayManager.ActiveDisplaySettingsSubscriptions;
                    var safeStartHardwareBoundaryPass = await VerifySafeStartHardwareBoundaryAsync().ConfigureAwait(true);
                    var printerCopyCountPolicyPass = await VerifyPrinterCopyCountPolicyAsync().ConfigureAwait(true);
                    var printerCommandPolicyPass = await VerifyPrinterCommandPolicyAsync().ConfigureAwait(true);
                    var printerSelectionBindingPass = await VerifyPrinterSelectionBindingAsync().ConfigureAwait(true);
                    var cashDrawerParsingPass = VerifyCashDrawerCommandParsing();
                    var receiptColumnFitPass = VerifyReceiptColumnFit();
                    var printerTestReceiptPass = VerifyPrinterTestReceiptBuilder();
                    var receiptShopSnapshotPass = await VerifyReceiptShopSnapshotReprintAsync().ConfigureAwait(true);
                    var activeDrawerSettingsValidationPass = await VerifyActiveDrawerSettingsValidationAsync().ConfigureAwait(true);
                    var dailyCloseReceiptPass = VerifyDailyCloseReceiptWidthAndParity();
                    var receiptArchiveRemovalPass = await VerifyReceiptArchiveRemovalAsync().ConfigureAwait(true);
                    var salesRegisterRapidSelectionPass = await VerifySalesRegisterRapidSelectionAsync().ConfigureAwait(true);
                    var dailyHistoryPreviewFencingPass = await VerifyDailyHistoryPreviewFencingAsync().ConfigureAwait(true);
                    var dailyDatePreviewFencingPass = VerifyDailyDatePreviewFencing();
                    var physicalPrinterQaPayloadPass = VerifyPhysicalPrinterQaPayloads();
                    var logRedactionVectorsPass = VerifyLogRedactionTestVectors();
                    var samples = new List<LifecycleSample>();
                    var weakWindows = new List<LifecycleWindowReference>();
                    for (var cycle = 1; cycle <= 20; cycle++)
                    {
                        _status.Text = "Lifecycle cycle " + cycle.ToString(CultureInfo.InvariantCulture) + "/20";
                        await OpenThenCloseAsync(CreateDailyReportDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateSalesRegisterDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateUserManagementDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(new SettingsHubDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateCustomerDisplaySettingsDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateProductEditDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateSyncCenterDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(new PosOnlineFirstLoginDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(new PosStartOfDaySyncDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreatePrinterSettingsDialog(), weakWindows).ConfigureAwait(true);
                        samples.Add(LifecycleSample.Capture(cycle));
                    }

                    // Displace the last async/focus references held by WPF and the caller so
                    // the 20 measured instances are not mistaken for framework-local roots.
                    await OpenThenCloseAsync(CreateDailyReportDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateSalesRegisterDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateUserManagementDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(new SettingsHubDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateCustomerDisplaySettingsDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateProductEditDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateSyncCenterDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(new PosOnlineFirstLoginDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(new PosStartOfDaySyncDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreatePrinterSettingsDialog(), null).ConfigureAwait(true);

                    _status.Text = "Lifecycle customer display windows 0/50";
                    var displayWindowPass = true;
                    for (var displayCycle = 0; displayCycle < 50; displayCycle++)
                    {
                        _status.Text = "Lifecycle customer display windows " +
                                       (displayCycle + 1).ToString(CultureInfo.InvariantCulture) + "/50";
                        displayWindowPass &= await OpenThenCloseCustomerDisplayAsync().ConfigureAwait(true);
                    }

                    _status.Text = "Lifecycle customer display manager setup";
                    var displaySettings = CustomerDisplaySettings.CreateDefault(2);
                    displaySettings.Enabled = false;
                    var displayRepository = new CustomerDisplaySettingsRepository(
                        new SqliteConnectionFactory(PosDbOptions.Default()));
                    await displayRepository.SaveAsync(displaySettings).ConfigureAwait(true);
                    for (var managerCycle = 0; managerCycle < 50; managerCycle++)
                    {
                        _status.Text = "Lifecycle customer display managers " +
                                       (managerCycle + 1).ToString(CultureInfo.InvariantCulture) + "/50";
                        using (var manager = new CustomerDisplayManager(
                            new FakeDisplayTopologyProvider(),
                            displayRepository,
                            Dispatcher))
                        {
                            await manager.InitializeAsync().ConfigureAwait(true);
                        }
                    }

                    _status.Text = "Lifecycle final collection";
                    var collectionAttempts = await WaitForLifecycleCollectionAsync(weakWindows)
                        .ConfigureAwait(true);
                    var residualWindows = weakWindows.Where(x => x.Window.IsAlive).ToList();
                    var residual = residualWindows.Count;
                    var residualTypes = string.Join(",", residualWindows
                        .GroupBy(x => x.WindowType)
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => x.Key + "=" + x.Count().ToString(CultureInfo.InvariantCulture)));
                    var residualViewModels = weakWindows.Where(x => x.DataContext.IsAlive).ToList();
                    var residualViewModelCount = residualViewModels.Count;
                    var residualViewModelTypes = string.Join(",", residualViewModels
                        .GroupBy(x => x.WindowType)
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => x.Key + "=" + x.Count().ToString(CultureInfo.InvariantCulture)));
                    var openWindows = Application.Current.Windows
                        .OfType<Window>()
                        .Count(x => !ReferenceEquals(x, this));
                    var languageHandlersAfter = GetLanguageChangedHandlerCount();
                    var displaySubscriptionsAfter = CustomerDisplayManager.ActiveDisplaySettingsSubscriptions;
                    var monotonicPrivateBytes = IsStrictlyIncreasing(samples.Select(x => x.PrivateBytes).ToList());
                    var monotonicHandles = IsStrictlyIncreasing(samples.Select(x => (long)x.HandleCount).ToList());
                    // Closed WPF Window shells can remain temporarily rooted by
                    // framework input/dispatcher caches even after their content and
                    // ViewModel have been released. Keep that weak count diagnostic;
                    // the leak gate is the ViewModel, open-window, subscription and
                    // monotonic resource checks below.
                    var passed = residualViewModelCount == 0 &&
                                 openWindows == 0 && languageHandlersAfter == languageHandlersBefore &&
                                 displaySubscriptionsAfter == displaySubscriptionsBefore && displayWindowPass &&
                                 printerCommandPolicyPass && printerSelectionBindingPass &&
                                 cashDrawerParsingPass && receiptColumnFitPass &&
                                 printerTestReceiptPass && receiptShopSnapshotPass &&
                                 activeDrawerSettingsValidationPass && dailyCloseReceiptPass &&
                                  receiptArchiveRemovalPass && salesRegisterRapidSelectionPass &&
                                  dailyHistoryPreviewFencingPass && dailyDatePreviewFencingPass &&
                                   physicalPrinterQaPayloadPass && logRedactionVectorsPass &&
                                   safeStartHardwareBoundaryPass && printerCopyCountPolicyPass &&
                                   !monotonicPrivateBytes && !monotonicHandles;
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: cycles=20; dailyReportCycles=20; salesRegisterCycles=20; printerSettingsCycles=20; recoveryAccessCycles=20; startOfDayCycles=20; customerDisplayCycles=50; managerCycles=50; residualWindowsDiagnostic={1}; residualTypes={2}; residualViewModelsDiagnostic={3}; residualViewModelTypes={4}; openWindows={5}; languageHandlers={6}->{7}; privateBytes={8}->{9}; handles={10}->{11}; monotonicPrivateBytes={12}; monotonicHandles={13}; displayHandlers={14}->{15}; displayWindowPass={16}; printerCommandPolicyPass={17}; cashDrawerParsingPass={18}; receiptColumnFitPass={19}; printerTestReceiptPass={20}; printerSelectionBindingPass={21}; receiptShopSnapshotPass={22}; activeDrawerSettingsValidationPass={23}; dailyCloseReceiptPass={24}; receiptArchiveRemovalPass={25}; salesRegisterRapidSelectionPass={26}; dailyHistoryPreviewFencingPass={27}; dailyDatePreviewFencingPass={28}; physicalPrinterQaPayloadPass={29}; logRedactionVectorsPass={30}; safeStartHardwareBoundaryPass={31}; printerCopyCountPolicyPass={32}; collectionAttempts={33}",
                        passed ? "PASS" : "FAIL",
                        residual,
                        residualTypes,
                        residualViewModelCount,
                        residualViewModelTypes,
                        openWindows,
                        languageHandlersBefore,
                        languageHandlersAfter,
                        samples.First().PrivateBytes,
                        samples.Last().PrivateBytes,
                        samples.First().HandleCount,
                        samples.Last().HandleCount,
                        monotonicPrivateBytes,
                        monotonicHandles,
                        displaySubscriptionsBefore,
                        displaySubscriptionsAfter,
                        displayWindowPass,
                        printerCommandPolicyPass,
                        cashDrawerParsingPass,
                        receiptColumnFitPass,
                        printerTestReceiptPass,
                        printerSelectionBindingPass,
                        receiptShopSnapshotPass,
                        activeDrawerSettingsValidationPass,
                        dailyCloseReceiptPass,
                        receiptArchiveRemovalPass,
                        salesRegisterRapidSelectionPass,
                        dailyHistoryPreviewFencingPass,
                        dailyDatePreviewFencingPass,
                        physicalPrinterQaPayloadPass,
                        logRedactionVectorsPass,
                        safeStartHardwareBoundaryPass,
                        printerCopyCountPolicyPass,
                        collectionAttempts);
                }
                finally
                {
                    IsEnabled = true;
                    _running = false;
                }
            }

            public async Task<string> RunPrinterSelectionBindingCheckAsync()
            {
                var passed = await VerifyPrinterSelectionBindingAsync().ConfigureAwait(true);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: printerSelectionBindingPass={1}",
                    passed ? "PASS" : "FAIL",
                    passed);
            }

            public async Task<string> RunFiscalDirectPrintCheckAsync()
            {
                var passed = await VerifyReceiptArchiveRemovalAsync().ConfigureAwait(true);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: fiscalDirectPrintNoArchivePass={1}",
                    passed ? "PASS" : "FAIL",
                    passed);
            }

            public async Task<string> RunPhysicalPrinterQaAsync(
                string requestedPrinterName,
                string dataDirectory)
            {
                if (string.IsNullOrWhiteSpace(requestedPrinterName))
                    throw new ArgumentException("Printer name is required.", nameof(requestedPrinterName));
                if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathRooted(dataDirectory))
                    throw new InvalidOperationException("Physical printer QA requires an absolute data directory.");
                if (Directory.EnumerateFileSystemEntries(dataDirectory).Any())
                    throw new InvalidOperationException("Physical printer QA requires a fresh empty data directory.");

                EnsureNoPhysicalQaDatabaseArtifacts(dataDirectory);
                var matches = WindowsPrinterDiscovery.GetInstalledPrinters()
                    .Where(printer => printer != null && string.Equals(
                        printer.Name,
                        requestedPrinterName.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException("The requested printer must resolve to exactly one installed queue.");

                var queue = matches[0];
                if (!queue.IsInventoryFresh ||
                    !queue.IsAvailable ||
                    !queue.IsPhysical ||
                    queue.IsOffline ||
                    queue.IsPaused)
                {
                    throw new InvalidOperationException(
                        "The requested queue is not a fresh, available, proven physical printer.");
                }

                var jobs = BuildPhysicalPrinterQaJobs(queue.Name);
                ValidatePhysicalPrinterQaJobs(jobs);
                var manifestPath = Path.Combine(dataDirectory, "physical-printer-qa.txt");
                var runUtc = DateTimeOffset.UtcNow;
                var submitted = 0;
                var status = "PREPARED_NOT_SUBMITTED";
                var detail = "none";
                var printer = new WindowsSpoolerReceiptPrinter();
                WritePhysicalPrinterQaManifest(
                    manifestPath,
                    status,
                    detail,
                    runUtc,
                    queue,
                    jobs,
                    submitted,
                    dataDirectory);

                for (var index = 0; index < jobs.Count; index++)
                {
                    try
                    {
                        await printer.PrintAsync(jobs[index].Text, jobs[index].Options)
                            .ConfigureAwait(true);
                        jobs[index].Submitted = true;
                        submitted++;
                        EnsureNoPhysicalQaDatabaseArtifacts(dataDirectory);
                        status = submitted == jobs.Count
                            ? "SUBMITTED_AWAITING_VISUAL_CONFIRMATION"
                            : "SUBMITTED_PARTIAL_AWAITING_REMAINING_JOBS";
                        WritePhysicalPrinterQaManifest(
                            manifestPath,
                            status,
                            detail,
                            runUtc,
                            queue,
                            jobs,
                            submitted,
                            dataDirectory);
                    }
                    catch (TimeoutException ex)
                    {
                        status = "INDETERMINATE_DO_NOT_RETRY";
                        detail = ex.GetType().Name + ": " + ManifestValue(ex.Message);
                        WritePhysicalPrinterQaManifest(
                            manifestPath,
                            status,
                            detail,
                            runUtc,
                            queue,
                            jobs,
                            submitted,
                            dataDirectory);
                        return File.ReadAllText(manifestPath);
                    }
                    catch (Exception ex)
                    {
                        status = "FAILED_NO_RETRY";
                        detail = ex.GetType().Name + ": " + ManifestValue(ex.Message);
                        WritePhysicalPrinterQaManifest(
                            manifestPath,
                            status,
                            detail,
                            runUtc,
                            queue,
                            jobs,
                            submitted,
                            dataDirectory);
                        return File.ReadAllText(manifestPath);
                    }
                }

                EnsureNoPhysicalQaDatabaseArtifacts(dataDirectory);
                return File.ReadAllText(manifestPath);
            }

            public async Task<string> RunPrinterPresentationCheckAsync()
            {
                var passed = await VerifyPrinterSettingsPresentationAsync().ConfigureAwait(true);
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: printerPresentationPass={1}",
                    passed ? "PASS" : "FAIL",
                    passed);
            }

            public string RunPosFooterLayoutCheck()
            {
                var desktopPass = VerifyPosFooterLayout(new Size(1280, 720));
                var compactPass = VerifyPosFooterLayout(new Size(1024, 600));
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: desktopPass={1}; compactPass={2}",
                    desktopPass && compactPass ? "PASS" : "FAIL",
                    desktopPass,
                    compactPass);
            }

            public string RunReceiptRenderingAlignmentCheck()
            {
                _receiptAlignmentFailure = string.Empty;
                var passed = VerifyReceiptRenderingAlignment();
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: receiptRenderingAlignmentPass={1}; variants=cash,card,split; discounts=line,cart; languages=en,es,it,zh-CN; widths=32,42; detail={2}",
                    passed ? "PASS" : "FAIL",
                    passed,
                    string.IsNullOrWhiteSpace(_receiptAlignmentFailure) ? "none" : _receiptAlignmentFailure);
            }

            public async Task<string> CaptureUxArtifactsAsync(string outputDirectory)
            {
                Directory.CreateDirectory(outputDirectory);

                var posView = new PosView
                {
                    DataContext = new PosLayoutPreviewDataContext(),
                    Width = 1280,
                    Height = 720
                };
                await CaptureHostedElementAsync(
                    posView,
                    new Size(1280, 720),
                    Path.Combine(outputDirectory, "pos-footer.png")).ConfigureAwait(true);

                var shop = new ReceiptShopInfo
                {
                    Name = "QA Café",
                    Address = "Calle Información 123",
                    City = "Santiago",
                    Rut = "76.123.456-7",
                    Phone = "+56 2 1234 5678",
                    Footer = "Grazie - Gracias"
                };
                var createdAtMs = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeMilliseconds();
                var paymentDraft = new PaymentReceiptDraft
                {
                    SaleCode = "QA-RECEIPT-ALIGNMENT",
                    CreatedAtMs = createdAtMs,
                    UseReceipt42 = true,
                    DefaultPrint = true,
                    ShopInfo = shop,
                    CartLines = new List<PaymentReceiptDraftLine>
                    {
                        new PaymentReceiptDraftLine
                        {
                            Barcode = "TEST-CAFFE",
                            Name = "Caffè più qualità - información",
                            Quantity = 2,
                            UnitPrice = 6173,
                            LineTotal = 12346
                        },
                        new PaymentReceiptDraftLine
                        {
                            Barcode = "TEST-PINGUINO",
                            Name = "Confezione città pingüino niño",
                            Quantity = 1,
                            UnitPrice = 2345,
                            LineTotal = 2345
                        }
                    }
                };
                using (var paymentViewModel = new PaymentViewModel(14691, paymentDraft, openDrawerDefault: true)
                {
                    CashReceived = "7000",
                    CardAmount = "7691"
                })
                {
                    var paymentView = new PaymentView
                    {
                        DataContext = paymentViewModel,
                        Width = 1280,
                        Height = 720
                    };
                    await CaptureHostedElementAsync(
                        paymentView,
                        new Size(1280, 720),
                        Path.Combine(outputDirectory, "payment-receipt-preview.png")).ConfigureAwait(true);
                }

                var printerViewModel = new PrinterSettingsViewModel
                {
                    PrinterName = "QA Receipt Printer",
                    ReceiptEnabled = true,
                    AutoPrint = true,
                    CashDrawerEnabled = true,
                    CashDrawerPrinterName = "QA Receipt Printer",
                    TestReceiptPreview = BuildQaPrinterTestReceipt()
                };
                printerViewModel.ReplaceInstalledPrinters(new[]
                {
                    new InstalledPrinterInfo
                    {
                        Name = "QA Receipt Printer",
                        DriverName = "EPSON TM-T60 Receipt5",
                        PortName = "ESDPRT001",
                        IsDefault = true,
                        OutputKind = PrinterOutputKind.Physical,
                        IsInventoryFresh = true,
                        IsAvailable = true,
                        StatusText = "Ready"
                    }
                });
                var printerDialog = new PrinterSettingsDialog(printerViewModel) { Owner = this };
                var rendered = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = null;
                renderedHandler = (_, __) =>
                {
                    printerDialog.ContentRendered -= renderedHandler;
                    rendered.TrySetResult(true);
                };
                printerDialog.ContentRendered += renderedHandler;
                try
                {
                    printerDialog.Show();
                    await rendered.Task.ConfigureAwait(true);
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    SaveVisual(
                        printerDialog.Content as FrameworkElement,
                        Path.Combine(outputDirectory, "printer-settings-preview.png"));
                }
                finally
                {
                    printerDialog.ContentRendered -= renderedHandler;
                    if (printerDialog.IsVisible)
                        printerDialog.Close();
                    else
                        printerViewModel.Dispose();
                }

                await CaptureReceiptSurfaceDialogsAsync(outputDirectory).ConfigureAwait(true);

                return "PASS: posFooter=True; paymentPreview=True; printerPreview=True; salesRegisterPreview=True; dailyClosePreview=True; compact1024x600=True";
            }

            private async Task CaptureReceiptSurfaceDialogsAsync(string outputDirectory)
            {
                foreach (var compact in new[] { false, true })
                {
                    var salesVm = new SalesRegisterViewModel(
                        new PosWorkflowService(),
                        useReceipt42: true,
                        permissionService: new HarnessPermissionService());
                    var salesDialog = new SalesRegisterDialog(salesVm)
                    {
                        Owner = this,
                        Width = compact ? 1000 : 1180,
                        Height = compact ? 560 : 700
                    };
                    try
                    {
                        salesDialog.Show();
                        for (var attempt = 0; attempt < 100 && salesVm.IsBusy; attempt++)
                            await Task.Delay(25).ConfigureAwait(true);
                        if (salesVm.SalesList.Count > 0)
                            salesVm.SelectedSale = salesVm.SalesList[0];
                        for (var attempt = 0; attempt < 120 && salesVm.IsPreviewLoading; attempt++)
                            await Task.Delay(25).ConfigureAwait(true);
                        await Dispatcher.InvokeAsync(
                            () => { },
                            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        SaveVisual(
                            salesDialog.Content as FrameworkElement,
                            Path.Combine(
                                outputDirectory,
                                compact
                                    ? "sales-register-receipt-preview-1024x600.png"
                                    : "sales-register-receipt-preview.png"));
                    }
                    finally
                    {
                        if (salesDialog.IsVisible) salesDialog.Close();
                    }

                    var dailyVm = new DailyReportViewModel(new PosWorkflowService());
                    var dailyDialog = new DailyReportDialog(dailyVm)
                    {
                        Owner = this,
                        Width = compact ? 1000 : 1180,
                        Height = compact ? 560 : 700
                    };
                    try
                    {
                        dailyDialog.Show();
                        for (var attempt = 0; attempt < 120 &&
                             (dailyVm.IsBusy || string.IsNullOrWhiteSpace(dailyVm.SummaryReceiptPreview)); attempt++)
                        {
                            await Task.Delay(25).ConfigureAwait(true);
                        }
                        await Dispatcher.InvokeAsync(
                            () => { },
                            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        SaveVisual(
                            dailyDialog.Content as FrameworkElement,
                            Path.Combine(
                                outputDirectory,
                                compact
                                    ? "daily-close-receipt-preview-1024x600.png"
                                    : "daily-close-receipt-preview.png"));
                    }
                    finally
                    {
                        if (dailyDialog.IsVisible) dailyDialog.Close();
                    }
                }
            }

            public async Task<string> CaptureSettingsAuditAsync(string outputDirectory)
            {
                Directory.CreateDirectory(outputDirectory);
                if (!File.Exists(Path.Combine(outputDirectory, "pos.db")))
                    await QaFixture.SeedAsync().ConfigureAwait(true);
                else
                    DbInitializer.EnsureCreated(PosDbOptions.Default());

                await CaptureDialogAsync(
                    new SettingsHubDialog(),
                    Path.Combine(outputDirectory, "settings-hub.png")).ConfigureAwait(true);
                var compactSettingsHub = new SettingsHubDialog { Width = 640, Height = 520 };
                await CaptureDialogAsync(
                    compactSettingsHub,
                    Path.Combine(outputDirectory, "settings-hub-compact.png")).ConfigureAwait(true);
                await CaptureDialogAsync(
                    new SettingsHubDialog(recoveryMode: true),
                    Path.Combine(outputDirectory, "settings-hub-recovery.png")).ConfigureAwait(true);
                await CaptureDialogAsync(
                    CreateCustomerDisplaySettingsDialog(),
                    Path.Combine(outputDirectory, "customer-display-settings.png")).ConfigureAwait(true);
                var compactCustomerDisplay = CreateCustomerDisplaySettingsDialog();
                compactCustomerDisplay.Width = 700;
                compactCustomerDisplay.Height = 540;
                await CaptureDialogAsync(
                    compactCustomerDisplay,
                    Path.Combine(outputDirectory, "customer-display-settings-compact.png")).ConfigureAwait(true);
                await CaptureDialogAsync(
                    new LanguageSettingsDialog(),
                    Path.Combine(outputDirectory, "language-settings.png")).ConfigureAwait(true);
                await CaptureDialogAsync(
                    new ShopSettingsDialog(new ShopSettingsViewModel(new PosWorkflowService())),
                    Path.Combine(outputDirectory, "shop-settings.png"),
                    idleDelayMs: 250).ConfigureAwait(true);
                await CaptureDialogAsync(
                    new ShopSettingsDialog(new ShopSettingsViewModel(new PosWorkflowService())) { Width = 560, Height = 560 },
                    Path.Combine(outputDirectory, "shop-settings-compact.png"),
                    idleDelayMs: 250).ConfigureAwait(true);
                await CaptureDialogAsync(
                    new UserManagementDialog(new UserManagementViewModel()),
                    Path.Combine(outputDirectory, "user-management.png")).ConfigureAwait(true);
                await CaptureDialogAsync(
                    CreateSyncCenterDialog(),
                    Path.Combine(outputDirectory, "sync-center.png")).ConfigureAwait(true);
                await CaptureDialogAsync(
                    new DbMaintenanceDialog(new DbMaintenanceViewModel(
                        new PosWorkflowService(),
                         () => Task.FromResult(false),
                         () => false,
                         () => false,
                         () => false)),
                    Path.Combine(outputDirectory, "database-maintenance.png")).ConfigureAwait(true);
                await CaptureDialogAsync(
                    new AboutSupportDialog(new AboutSupportViewModel(new PosWorkflowService())),
                    Path.Combine(outputDirectory, "about-support.png"),
                    idleDelayMs: 150).ConfigureAwait(true);

                return "PASS: settingsHub=True; settingsHubCompact=True; settingsHubRecovery=True; customerDisplay=True; customerDisplayCompact=True; language=True; shop=True; shopCompact=True; users=True; sync=True; database=True; about=True";
            }

            private async Task CaptureDialogAsync(
                Window dialog,
                string outputPath,
                int idleDelayMs = 0)
            {
                dialog.Owner = this;
                var rendered = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = null;
                renderedHandler = (_, __) =>
                {
                    dialog.ContentRendered -= renderedHandler;
                    rendered.TrySetResult(true);
                };
                dialog.ContentRendered += renderedHandler;
                try
                {
                    dialog.Show();
                    await rendered.Task.ConfigureAwait(true);
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    if (idleDelayMs > 0)
                        await Task.Delay(idleDelayMs).ConfigureAwait(true);
                    SaveVisual(dialog.Content as FrameworkElement, outputPath);
                }
                finally
                {
                    dialog.ContentRendered -= renderedHandler;
                    if (dialog.IsVisible)
                        dialog.Close();
                }
            }

            private async Task CaptureHostedElementAsync(
                FrameworkElement element,
                Size viewport,
                string outputPath)
            {
                if (element is Control control)
                    control.Background = Brushes.White;

                var host = new Window
                {
                    Owner = this,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Width = viewport.Width,
                    Height = viewport.Height,
                    Content = element
                };
                var rendered = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = null;
                renderedHandler = (_, __) =>
                {
                    host.ContentRendered -= renderedHandler;
                    rendered.TrySetResult(true);
                };
                host.ContentRendered += renderedHandler;
                try
                {
                    host.Show();
                    await rendered.Task.ConfigureAwait(true);
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    SaveVisual(element, outputPath);
                }
                finally
                {
                    host.ContentRendered -= renderedHandler;
                    if (host.IsVisible) host.Close();
                }
            }

            private static void SaveVisual(FrameworkElement visual, string outputPath)
            {
                if (visual == null || visual.ActualWidth <= 0 || visual.ActualHeight <= 0)
                    throw new InvalidOperationException("Visual capture target has no rendered size.");

                visual.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(visual);
                var bitmap = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY)),
                    96 * dpi.DpiScaleX,
                    96 * dpi.DpiScaleY,
                    PixelFormats.Pbgra32);
                bitmap.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(outputPath))
                    encoder.Save(stream);
            }

            private async Task OpenThenCloseAsync(Window dialog, ICollection<LifecycleWindowReference> weakWindows)
            {
                object dataContext = dialog.DataContext;
                LifecycleWindowReference lifecycleReference = null;
                TaskCompletionSource<bool> rendered = new TaskCompletionSource<bool>();
                TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = null;
                EventHandler closedHandler = null;
                renderedHandler = (_, __) => rendered.TrySetResult(true);
                closedHandler = (_, __) => closed.TrySetResult(true);

                try
                {
                    dialog.Owner = this;
                    dialog.ContentRendered += renderedHandler;
                    dialog.Closed += closedHandler;
                    dialog.Show();
                    await rendered.Task.ConfigureAwait(true);
                    await Task.Delay(40).ConfigureAwait(true);
                    await WaitForDialogWorkToSettleAsync(dataContext).ConfigureAwait(true);

                    // Sales Register intentionally focuses its code box on load. Move
                    // keyboard focus back to the harness before closing so WPF input
                    // restoration cannot retain each retired dialog until a later input.
                    System.Windows.Input.Keyboard.ClearFocus();
                    Focus();
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.Input);

                    if (weakWindows != null)
                    {
                        lifecycleReference = new LifecycleWindowReference(
                            dialog.GetType().Name,
                            new WeakReference(dialog),
                            new WeakReference(dataContext));
                    }

                    dialog.Close();
                    await closed.Task.ConfigureAwait(true);

                    if (lifecycleReference != null)
                    {
                        weakWindows.Add(lifecycleReference);
                    }
                }
                finally
                {
                    if (dialog != null)
                    {
                        dialog.ContentRendered -= renderedHandler;
                        dialog.Closed -= closedHandler;
                        if (dialog.IsVisible)
                            dialog.Close();
                    }

                    // The async state machine and its event closures must not become the
                    // last strong roots of a successfully closed dialog during GC checks.
                    dialog = null;
                    dataContext = null;
                    lifecycleReference = null;
                    renderedHandler = null;
                    closedHandler = null;
                    rendered = null;
                    closed = null;
                }
            }

            private static async Task WaitForDialogWorkToSettleAsync(object dataContext)
            {
                const int maxAttempts = 80;
                for (var attempt = 0; attempt < maxAttempts; attempt++)
                {
                    var salesRegister = dataContext as SalesRegisterViewModel;
                    var userManagement = dataContext as UserManagementViewModel;
                    var settled = salesRegister != null
                        ? !salesRegister.IsBusy && !salesRegister.IsPreviewLoading
                        : userManagement == null || !userManagement.IsBusy;
                    if (settled)
                        return;

                    await Task.Delay(25).ConfigureAwait(true);
                }
            }

            private async Task<int> WaitForLifecycleCollectionAsync(
                ICollection<LifecycleWindowReference> weakWindows)
            {
                const int maxAttempts = 10;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                    if (!HasLiveLifecycleReferences(weakWindows))
                        return attempt;

                    await Task.Delay(100).ConfigureAwait(true);
                }

                return maxAttempts;
            }

            private static bool HasLiveLifecycleReferences(
                IEnumerable<LifecycleWindowReference> weakWindows)
            {
                foreach (var reference in weakWindows)
                {
                    if (reference.Window.IsAlive || reference.DataContext.IsAlive)
                        return true;
                }

                return false;
            }

            private static DailyReportDialog CreateDailyReportDialog()
            {
                return new DailyReportDialog(new DailyReportViewModel(new PosWorkflowService()));
            }

            private static SalesRegisterDialog CreateSalesRegisterDialog()
            {
                return new SalesRegisterDialog(new SalesRegisterViewModel(
                    new PosWorkflowService(),
                    useReceipt42: true,
                    permissionService: new HarnessPermissionService()));
            }

            private static UserManagementDialog CreateUserManagementDialog()
            {
                return new UserManagementDialog(new UserManagementViewModel());
            }

            private static PrinterSettingsDialog CreatePrinterSettingsDialog()
            {
                var vm = new PrinterSettingsViewModel
                {
                    PrinterName = "QA Receipt Printer",
                    ReceiptEnabled = true,
                    CashDrawerEnabled = true,
                    CashDrawerPrinterName = "QA Receipt Printer",
                    TestReceiptPreview = BuildQaPrinterTestReceipt()
                };
                vm.ReplaceInstalledPrinters(new[]
                {
                    new InstalledPrinterInfo
                    {
                        Name = "QA Receipt Printer",
                        DriverName = "QA Driver",
                        PortName = "QA001",
                        IsDefault = true,
                        OutputKind = PrinterOutputKind.Physical,
                        IsInventoryFresh = true,
                        IsAvailable = true,
                        IsOffline = false,
                        IsPaused = false,
                        StatusText = "Ready",
                        Notes = "Lifecycle-only synthetic queue"
                    }
                });
                return new PrinterSettingsDialog(vm);
            }

            private async Task<bool> VerifyPrinterSelectionBindingAsync()
            {
                var queue = new InstalledPrinterInfo
                {
                    Name = "QA Default Receipt Printer",
                    DriverName = "QA Driver",
                    PortName = "QA001",
                    IsDefault = true,
                    OutputKind = PrinterOutputKind.Physical,
                    IsInventoryFresh = true,
                    IsAvailable = true,
                    IsOffline = false,
                    IsPaused = false,
                    StatusText = "Ready"
                };
                var vm = new PrinterSettingsViewModel
                {
                    PrinterName = queue.Name,
                    ReceiptEnabled = true
                };
                vm.ReplaceInstalledPrinters(new[] { queue });

                var dialog = new PrinterSettingsDialog(vm) { Owner = this };
                var rendered = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = null;
                renderedHandler = (_, __) =>
                {
                    dialog.ContentRendered -= renderedHandler;
                    rendered.TrySetResult(true);
                };
                dialog.ContentRendered += renderedHandler;

                try
                {
                    dialog.Show();
                    await rendered.Task.ConfigureAwait(true);
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                    var receiptPrinterCombo = FindVisualDescendants<ComboBox>(dialog)
                        .FirstOrDefault(combo => ReferenceEquals(combo.SelectedItem, queue));
                    var selectedItemText = receiptPrinterCombo == null
                        ? string.Empty
                        : Convert.ToString(receiptPrinterCombo.SelectedItem, CultureInfo.InvariantCulture);
                    var selectionBoxText = receiptPrinterCombo == null
                        ? string.Empty
                        : Convert.ToString(receiptPrinterCombo.SelectionBoxItem, CultureInfo.InvariantCulture);
                    var visibleSelectionText = receiptPrinterCombo == null ||
                                               string.IsNullOrWhiteSpace(receiptPrinterCombo.Text)
                        ? selectionBoxText
                        : receiptPrinterCombo.Text;
                    var clrTypeName = typeof(InstalledPrinterInfo).FullName ??
                                      nameof(InstalledPrinterInfo);

                    return !string.Equals(queue.Summary, queue.Name, StringComparison.Ordinal) &&
                           string.Equals(vm.PrinterName, queue.Name, StringComparison.Ordinal) &&
                           receiptPrinterCombo != null &&
                           string.Equals(selectedItemText, queue.Summary, StringComparison.Ordinal) &&
                           string.Equals(visibleSelectionText, queue.Summary, StringComparison.Ordinal) &&
                           !string.Equals(selectedItemText, clrTypeName, StringComparison.Ordinal) &&
                           !string.Equals(visibleSelectionText, clrTypeName, StringComparison.Ordinal) &&
                           vm.TestPrintCommand.CanExecute(null);
                }
                finally
                {
                    dialog.ContentRendered -= renderedHandler;
                    if (dialog.IsVisible)
                        dialog.Close();
                    else
                        vm.Dispose();
                }
            }

            private async Task<bool> VerifyPrinterSettingsPresentationAsync()
            {
                var queue = new InstalledPrinterInfo
                {
                    Name = "QA Receipt Printer",
                    DriverName = "QA Driver",
                    PortName = "QA001",
                    IsDefault = true,
                    OutputKind = PrinterOutputKind.Physical,
                    IsInventoryFresh = true,
                    IsAvailable = true,
                    IsOffline = false,
                    IsPaused = false,
                    StatusText = "Ready"
                };
                var vm = new PrinterSettingsViewModel
                {
                    PrinterName = queue.Name,
                    ReceiptEnabled = true,
                    CashDrawerEnabled = false,
                    TestReceiptPreview = BuildQaPrinterTestReceipt()
                };
                vm.ReplaceInstalledPrinters(new[] { queue });

                var dialog = new PrinterSettingsDialog(vm) { Owner = this };
                var rendered = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = null;
                renderedHandler = (_, __) =>
                {
                    dialog.ContentRendered -= renderedHandler;
                    rendered.TrySetResult(true);
                };
                dialog.ContentRendered += renderedHandler;

                try
                {
                    dialog.Show();
                    await rendered.Task.ConfigureAwait(true);
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                    var receiptCard = dialog.FindName("ReceiptSettingsCard") as Border;
                    var previewCard = dialog.FindName("TestReceiptPreviewCard") as Border;
                    var previewScroll = dialog.FindName("TestReceiptPreviewScrollViewer") as ScrollViewer;
                    var testPrintButton = dialog.FindName("TestPrintButton") as Button;
                    var copiesTextBox = dialog.FindName("CopiesTextBox") as TextBox;
                    var advanced = dialog.FindName("AdvancedOptionsExpander") as Expander;
                    var receiptHistoryInfo = dialog.FindName("ReceiptHistoryStorageInfo") as Border;
                    var drawer = dialog.FindName("CashDrawerExpander") as Expander;
                    var drawerDetails = dialog.FindName("CashDrawerDetailsPanel") as StackPanel;
                    var detected = dialog.FindName("DetectedPrintersExpander") as Expander;
                    var detectedPrinters = dialog.FindName("DetectedPrintersList") as ItemsControl;

                    var primaryStatePass = receiptCard != null && receiptCard.IsVisible &&
                                           previewCard != null && previewCard.IsVisible &&
                                           previewScroll != null &&
                                           previewScroll.HorizontalScrollBarVisibility ==
                                           ScrollBarVisibility.Disabled &&
                                            testPrintButton != null && testPrintButton.IsEnabled &&
                                            testPrintButton.HorizontalAlignment == HorizontalAlignment.Stretch &&
                                            copiesTextBox != null && copiesTextBox.MaxLength == 1 &&
                                           advanced != null && !advanced.IsExpanded &&
                                           receiptHistoryInfo != null &&
                                           !receiptHistoryInfo.IsVisible &&
                                           drawer != null && !drawer.IsExpanded &&
                                           drawerDetails != null &&
                                           drawerDetails.Visibility == Visibility.Collapsed &&
                                           detected != null && !detected.IsExpanded &&
                                           detectedPrinters != null &&
                                           !(detectedPrinters is ListBox);

                    advanced.IsExpanded = true;
                    drawer.IsExpanded = true;
                    detected.IsExpanded = true;
                    vm.CashDrawerEnabled = true;
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                    var progressiveDisclosurePass = receiptHistoryInfo.IsVisible &&
                                                     drawerDetails.Visibility == Visibility.Visible &&
                                                     detectedPrinters.Items.Count == 1;
                    return primaryStatePass && progressiveDisclosurePass;
                }
                finally
                {
                    dialog.ContentRendered -= renderedHandler;
                    if (dialog.IsVisible)
                        dialog.Close();
                    else
                        vm.Dispose();
                }
            }

            private static bool VerifyPosFooterLayout(Size viewport)
            {
                var view = new PosView
                {
                    DataContext = new PosLayoutPreviewDataContext(),
                    Width = viewport.Width,
                    Height = viewport.Height
                };
                view.Measure(viewport);
                view.Arrange(new Rect(new Point(0, 0), viewport));
                view.UpdateLayout();

                var footer = view.FindName("PosCheckoutFooter") as Border;
                var tools = view.FindName("PosToolActionsPanel") as FrameworkElement;
                var elements = new FrameworkElement[]
                {
                    view.FindName("FooterSuspendButton") as FrameworkElement,
                    view.FindName("FooterRecoverButton") as FrameworkElement,
                    view.FindName("FooterClearButton") as FrameworkElement,
                    view.FindName("FooterItemsSummary") as FrameworkElement,
                    view.FindName("FooterTotalPanel") as FrameworkElement,
                    view.FindName("FooterPayButton") as FrameworkElement
                };
                if (footer == null || tools == null || elements.Any(element => element == null) ||
                    footer.ActualHeight <= 0 || footer.ActualHeight > 80)
                    return false;

                var pay = view.FindName("FooterPayButton") as FrameworkElement;
                if (pay == null || pay.ActualWidth < 240 || pay.ActualHeight < 48 ||
                    Math.Abs(pay.ActualWidth - tools.ActualWidth) > 1)
                    return false;

                var toolsLeft = tools.TranslatePoint(new Point(0, 0), view).X;
                var payLeft = pay.TranslatePoint(new Point(0, 0), view).X;
                var toolsRight = toolsLeft + tools.ActualWidth;
                var payRight = payLeft + pay.ActualWidth;
                if (Math.Abs(payLeft - toolsLeft) > 1 || Math.Abs(payRight - toolsRight) > 1)
                    return false;

                var centers = new List<double>();
                foreach (var element in elements)
                {
                    if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                        return false;

                    var topLeft = element.TranslatePoint(new Point(0, 0), footer);
                    var center = element.TranslatePoint(
                        new Point(element.ActualWidth / 2, element.ActualHeight / 2),
                        footer);
                    if (topLeft.X < -1 || topLeft.Y < -1 ||
                        topLeft.X + element.ActualWidth > footer.ActualWidth + 1 ||
                        topLeft.Y + element.ActualHeight > footer.ActualHeight + 1)
                        return false;

                    centers.Add(center.Y);
                }

                return centers.Max() - centers.Min() <= 2;
            }

            private sealed class PosLayoutPreviewDataContext
            {
                public bool IsBusy => false;
                public bool IsStatusToastVisible => false;
                public bool HasDiscount => false;
                public int ItemsCount => 0;
                public string FinalTotalDisplay => "0";
                public string BarcodeInput { get; set; } = string.Empty;
                public object SelectedCartItem { get; set; }
                public IEnumerable<object> CartItems => Array.Empty<object>();
            }

            private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
                where T : DependencyObject
            {
                if (root == null) yield break;
                var childCount = VisualTreeHelper.GetChildrenCount(root);
                for (var index = 0; index < childCount; index++)
                {
                    var child = VisualTreeHelper.GetChild(root, index);
                    if (child is T match)
                        yield return match;

                    foreach (var descendant in FindVisualDescendants<T>(child))
                        yield return descendant;
                }
            }

            private static async Task<bool> VerifyPrinterCommandPolicyAsync()
            {
                var queue = new InstalledPrinterInfo
                {
                    Name = "QA Receipt Printer",
                    DriverName = "QA Driver",
                    PortName = "QA001",
                    IsDefault = true,
                    OutputKind = PrinterOutputKind.Physical,
                    IsInventoryFresh = true,
                    IsAvailable = true,
                    StatusText = "Ready"
                };
                var vm = new PrinterSettingsViewModel
                {
                    PrinterName = queue.Name,
                    ReceiptEnabled = true,
                    CashDrawerEnabled = true,
                    CashDrawerPrinterName = queue.Name
                };

                try
                {
                    vm.ReplaceInstalledPrinters(new[] { queue });
                    foreach (var validCopies in new[] { "1", "3" })
                    {
                        vm.Copies = validCopies;
                        if (!vm.IsValid || !vm.ConfirmCommand.CanExecute(null)) return false;
                    }
                    foreach (var invalidCopies in new[] { "0", "4", "-1", " 1", "2147483648", "not-a-number" })
                    {
                        vm.Copies = invalidCopies;
                        if (vm.IsValid || vm.ConfirmCommand.CanExecute(null)) return false;
                    }
                    vm.Copies = "1";
                    if (!vm.TestPrintCommand.CanExecute(null) || !vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.IsOffline = true;
                    if (vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.IsOffline = false;
                    queue.IsPaused = true;
                    if (vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.IsPaused = false;
                    queue.IsAvailable = false;
                    if (vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.IsAvailable = true;
                    queue.IsVirtual = true;
                    vm.AllowVirtualPrinters = false;
                    if (vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    vm.AllowVirtualPrinters = true;
                    if (!vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.OutputKind = PrinterOutputKind.Unknown;
                    if (!vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.OutputKind = PrinterOutputKind.Physical;
                    vm.PrinterName = string.Empty;
                    vm.CashDrawerPrinterName = string.Empty;
                    vm.AllowWindowsDefault = false;
                    if (vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    vm.AllowWindowsDefault = true;
                    if (!vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.OutputKind = PrinterOutputKind.Physical;
                    vm.PrinterName = queue.Name;
                    vm.CashDrawerPrinterName = queue.Name;
                    vm.CashDrawerCommand = "27,112,0,25,250";
                    if (!vm.TestPrintCommand.CanExecute(null) || !vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.IsInventoryFresh = false;
                    if (vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;
                    queue.IsInventoryFresh = true;

                    if (!VerifyPrinterOutputClassificationAndStaleClone(queue) ||
                        !VerifyReceiptPageProgressGuard() ||
                        !await VerifySharedPrinterEffectCoordinatorAsync().ConfigureAwait(true))
                    {
                        return false;
                    }

                    var printRelease = new TaskCompletionSource<bool>();
                    var printCalls = 0;
                    vm.TestPrintRequested += async () =>
                    {
                        printCalls++;
                        await printRelease.Task.ConfigureAwait(true);
                    };
                    vm.TestPrintCommand.Execute(null);
                    var printCommandsDisabled =
                        !vm.TestPrintCommand.CanExecute(null) &&
                        !vm.TestCashDrawerCommand.CanExecute(null);
                    vm.TestPrintCommand.Execute(null);
                    var printSingleFlight = printCalls == 1;
                    printRelease.TrySetResult(true);
                    await vm.ActiveTestOperation.ConfigureAwait(true);
                    if (!printCommandsDisabled || !printSingleFlight ||
                        !vm.TestPrintCommand.CanExecute(null) || !vm.TestCashDrawerCommand.CanExecute(null))
                    {
                        return false;
                    }

                    var drawerRelease = new TaskCompletionSource<bool>();
                    var drawerCalls = 0;
                    var drawerArgumentsValid = false;
                    vm.TestCashDrawerRequested += async (printerName, command) =>
                    {
                        drawerCalls++;
                        drawerArgumentsValid =
                            string.Equals(printerName, queue.Name, StringComparison.Ordinal) &&
                            string.Equals(command, "27,112,0,25,250", StringComparison.Ordinal);
                        await drawerRelease.Task.ConfigureAwait(true);
                    };
                    vm.TestCashDrawerCommand.Execute(null);
                    var drawerCommandsDisabled =
                        !vm.TestPrintCommand.CanExecute(null) &&
                        !vm.TestCashDrawerCommand.CanExecute(null);
                    vm.TestCashDrawerCommand.Execute(null);
                    var drawerSingleFlight = drawerCalls == 1;
                    drawerRelease.TrySetResult(true);
                    await vm.ActiveTestOperation.ConfigureAwait(true);
                    if (!drawerCommandsDisabled || !drawerSingleFlight || !drawerArgumentsValid ||
                        !vm.TestPrintCommand.CanExecute(null) || !vm.TestCashDrawerCommand.CanExecute(null))
                    {
                        return false;
                    }

                    vm.CashDrawerCommand = "27,112,0,25,not-a-byte";
                    if (vm.TestCashDrawerCommand.CanExecute(null) ||
                        vm.IsCashDrawerCommandValid ||
                        vm.IsValid ||
                        !string.Equals(
                            vm.TestCashDrawerStatusMessage,
                            Win7POS.Wpf.Localization.PosLocalization.T("printer.testInvalidCommand"),
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    vm.CashDrawerCommand = "  ";
                    return !vm.TestCashDrawerCommand.CanExecute(null) &&
                           !vm.IsCashDrawerCommandValid &&
                           !vm.IsValid &&
                           string.Equals(
                               vm.TestCashDrawerStatusMessage,
                               Win7POS.Wpf.Localization.PosLocalization.T("printer.testMissing"),
                               StringComparison.Ordinal);
                }
                finally
                {
                    vm.Dispose();
                    vm.Dispose();
                }
            }

            private static bool VerifyPrinterOutputClassificationAndStaleClone(
                InstalledPrinterInfo physicalQueue)
            {
                if (WindowsPrinterDiscovery.ClassifyOutputKind(
                        "EPSON TM-T60 Receipt",
                        "EPSON TM-T60 Receipt5",
                        "ESDPRT001") != PrinterOutputKind.Physical ||
                    WindowsPrinterDiscovery.ClassifyOutputKind(
                        "QA Queue",
                        "QA Driver",
                        "QA001") != PrinterOutputKind.Unknown ||
                    WindowsPrinterDiscovery.ClassifyOutputKind(
                        "Microsoft Print to PDF",
                        "PDF Driver",
                        "USB001") != PrinterOutputKind.Virtual ||
                    WindowsPrinterDiscovery.ClassifyOutputKind(
                        "Virtual Queue",
                        "QA Driver",
                        "FILE:") != PrinterOutputKind.Virtual)
                {
                    return false;
                }

                var stale = physicalQueue.CloneWithInventoryFreshness(false);
                if (ReferenceEquals(stale, physicalQueue) ||
                    stale.IsInventoryFresh ||
                    !physicalQueue.IsInventoryFresh ||
                    stale.OutputKind != PrinterOutputKind.Physical)
                {
                    return false;
                }

                var resolver = typeof(PosWorkflowService).GetMethod(
                    "ResolvePrinterNameOrThrow",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (resolver == null) return false;

                var unknown = new InstalledPrinterInfo
                {
                    Name = "QA Unknown Queue",
                    OutputKind = PrinterOutputKind.Unknown,
                    IsInventoryFresh = true,
                    IsAvailable = true,
                    StatusText = "Ready"
                };
                try
                {
                    var manual = resolver.Invoke(
                        null,
                        new object[]
                        {
                            unknown.Name,
                            new[] { unknown },
                            false,
                            true,
                            false
                        }) as InstalledPrinterInfo;
                    if (!ReferenceEquals(manual, unknown)) return false;
                }
                catch
                {
                    return false;
                }

                return ResolverRejects(resolver, unknown, requirePhysicalOutput: true) &&
                       ResolverRejects(resolver, stale, requirePhysicalOutput: false);
            }

            private static bool ResolverRejects(
                MethodInfo resolver,
                InstalledPrinterInfo printer,
                bool requirePhysicalOutput)
            {
                try
                {
                    resolver.Invoke(
                        null,
                        new object[]
                        {
                            printer.Name,
                            new[] { printer },
                            false,
                            true,
                            requirePhysicalOutput
                        });
                    return false;
                }
                catch (TargetInvocationException ex)
                {
                    return ex.InnerException is InvalidOperationException;
                }
            }

            private static bool VerifyReceiptPageProgressGuard()
            {
                var guard = typeof(WindowsSpoolerReceiptPrinter).GetMethod(
                    "EnsurePageProgress",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (guard == null) return false;

                try
                {
                    guard.Invoke(null, new object[] { 1, true, 0, 1, false, false });
                    guard.Invoke(null, new object[] { 1, true, 1, 1, false, true });
                    guard.Invoke(null, new object[] { 1, false, 1, 1, true, true });
                }
                catch
                {
                    return false;
                }

                return PageProgressGuardRejects(
                           guard,
                           new object[] { 1, true, 0, 0, false, false }) &&
                       PageProgressGuardRejects(
                           guard,
                           new object[] { 129, false, 0, 0, true, true });
            }

            private static bool PageProgressGuardRejects(MethodInfo guard, object[] arguments)
            {
                try
                {
                    guard.Invoke(null, arguments);
                    return false;
                }
                catch (TargetInvocationException ex)
                {
                    return ex.InnerException is InvalidOperationException;
                }
            }

            private static async Task<bool> VerifySafeStartHardwareBoundaryAsync()
            {
                var safeStartProperty = typeof(Win7POS.Wpf.App).GetProperty(
                    "IsSafeStart",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var safeStartSetter = safeStartProperty?.GetSetMethod(true);
                var tailsField = typeof(WindowsSpoolerReceiptPrinter).GetField(
                    "PrinterEffectTails",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var discoveryTaskField = typeof(PosWorkflowService).GetField(
                    "_printerDiscoveryTask",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var adminWebGuard = typeof(PosOnlineFirstLoginDialog).GetMethod(
                    "IsAdminWebOptionsAllowedForCurrentLaunch",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (safeStartProperty == null || safeStartSetter == null ||
                    tailsField == null || discoveryTaskField == null || adminWebGuard == null)
                {
                    return false;
                }

                var originalSafeStart = (bool)safeStartProperty.GetValue(null);
                var tailsBefore = tailsField.GetValue(null) as System.Collections.IDictionary;
                var tailCountBefore = tailsBefore?.Count ?? 0;
                var service = new PosWorkflowService();
                var settingsBefore = await service.GetPrinterSettingsAsync().ConfigureAwait(true);
                try
                {
                    safeStartSetter.Invoke(null, new object[] { true });
                    var loopbackAllowed = (bool)adminWebGuard.Invoke(
                        null,
                        new object[] { new PosAdminWebOptions(new Uri("http://127.0.0.1:9/")) });
                    var stagingBlocked = !(bool)adminWebGuard.Invoke(
                        null,
                        new object[] { new PosAdminWebOptions(new Uri("https://staging.example.invalid/")) });
                    var blockedSettings = ClonePrinterSettings(settingsBefore);
                    blockedSettings.ReceiptEnabled = true;
                    blockedSettings.PrinterName = "QA MUST NOT REACH HARDWARE";

                    var workflowBlocked =
                        await ExpectInvalidOperationAsync(() => service.TestReceiptPrinterAsync(
                            blockedSettings,
                            "QA MUST NOT PRINT",
                            false)).ConfigureAwait(true) &&
                        await ExpectInvalidOperationAsync(() => service.PrintReceiptTextAsync(
                            "QA MUST NOT PRINT",
                            false,
                            "QA_SAFE_START",
                            explicitUserAction: true)).ConfigureAwait(true) &&
                        await ExpectInvalidOperationAsync(() => service.OpenCashDrawerAsync()).ConfigureAwait(true) &&
                        await ExpectInvalidOperationAsync(() => service.TestCashDrawerAsync(
                            "QA MUST NOT REACH HARDWARE",
                            "27,112,0,25,250")).ConfigureAwait(true) &&
                        await ExpectInvalidOperationAsync(() => service.SetPrinterSettingsAsync(
                            blockedSettings)).ConfigureAwait(true);

                    var inventory = await service.GetInstalledPrintersAsync().ConfigureAwait(true);
                    var spooler = new WindowsSpoolerReceiptPrinter();
                    var lastMileBlocked =
                        await ExpectInvalidOperationAsync(() => spooler.PrintAsync(
                            "QA MUST NOT PRINT",
                            new ReceiptPrintOptions
                            {
                                PrinterName = "QA MUST NOT REACH HARDWARE",
                                Copies = 1
                            })).ConfigureAwait(true) &&
                        await ExpectInvalidOperationAsync(() => spooler.OpenCashDrawerAsync(
                            new ReceiptPrintOptions
                            {
                                PrinterName = "QA MUST NOT REACH HARDWARE",
                                CashDrawerCommand = "27,112,0,25,250"
                            })).ConfigureAwait(true);

                    var settingsAfter = await service.GetPrinterSettingsAsync().ConfigureAwait(true);
                    var tailsAfter = tailsField.GetValue(null) as System.Collections.IDictionary;
                    return loopbackAllowed && stagingBlocked &&
                           workflowBlocked &&
                           lastMileBlocked &&
                           inventory != null && inventory.Count == 0 &&
                           discoveryTaskField.GetValue(service) == null &&
                           (tailsAfter?.Count ?? 0) == tailCountBefore &&
                           settingsAfter.ReceiptEnabled == settingsBefore.ReceiptEnabled &&
                           string.Equals(
                               settingsAfter.PrinterName,
                               settingsBefore.PrinterName,
                               StringComparison.Ordinal);
                }
                finally
                {
                    safeStartSetter.Invoke(null, new object[] { originalSafeStart });
                }
            }

            private static async Task<bool> VerifyPrinterCopyCountPolicyAsync()
            {
                if (!ReceiptPrintOptions.IsValidCopyCount(1) ||
                    !ReceiptPrintOptions.IsValidCopyCount(3) ||
                    ReceiptPrintOptions.IsValidCopyCount(0) ||
                    ReceiptPrintOptions.IsValidCopyCount(4) ||
                    ReceiptPrintOptions.IsValidCopyCount(int.MaxValue))
                {
                    return false;
                }

                var service = new PosWorkflowService();
                var before = await service.GetPrinterSettingsAsync().ConfigureAwait(true);
                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                var settings = new SettingsRepository(factory);
                var primaryKey = AppSettingKeys.PosPrinterReceiptCopies;
                const string legacyKey = "printer.copies";
                var primaryBefore = await settings.GetStringAsync(primaryKey).ConfigureAwait(true);
                var legacyBefore = await settings.GetStringAsync(legacyKey).ConfigureAwait(true);
                var tailsField = typeof(WindowsSpoolerReceiptPrinter).GetField(
                    "PrinterEffectTails",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (tailsField == null) return false;
                var tailsBefore = tailsField.GetValue(null) as System.Collections.IDictionary;
                var tailCountBefore = tailsBefore?.Count ?? 0;

                try
                {
                    foreach (var invalidCopies in new[] { 0, 4, int.MaxValue })
                    {
                        var invalid = ClonePrinterSettings(before);
                        invalid.Copies = invalidCopies;
                        if (!await ExpectInvalidOperationAsync(() => service.SetPrinterSettingsAsync(invalid))
                                .ConfigureAwait(true))
                        {
                            return false;
                        }

                        var unchanged = await service.GetPrinterSettingsAsync().ConfigureAwait(true);
                        if (unchanged.Copies != before.Copies) return false;
                    }

                    var maximum = ClonePrinterSettings(before);
                    maximum.Copies = ReceiptPrintOptions.MaximumCopies;
                    await service.SetPrinterSettingsAsync(maximum).ConfigureAwait(true);
                    var maximumRoundTrip = await service.GetPrinterSettingsAsync().ConfigureAwait(true);
                    if (maximumRoundTrip.Copies != ReceiptPrintOptions.MaximumCopies) return false;

                    await settings.SetIntAsync(primaryKey, 999).ConfigureAwait(true);
                    await settings.SetIntAsync(legacyKey, 999).ConfigureAwait(true);
                    var corruptRead = await new PosWorkflowService()
                        .GetPrinterSettingsAsync()
                        .ConfigureAwait(true);
                    if (corruptRead.Copies != ReceiptPrintOptions.MinimumCopies) return false;

                    var spooler = new WindowsSpoolerReceiptPrinter();
                    var invalidDocumentRejectedBeforeEffect = false;
                    try
                    {
                        await spooler.PrintAsync(
                            new string('x', ReceiptDocumentPolicy.MaxDocumentCharacters + 1),
                            new ReceiptPrintOptions
                            {
                                PrinterName = "QA INVALID DOCUMENT MUST NOT PRINT",
                                Copies = 1
                            }).ConfigureAwait(true);
                    }
                    catch (ReceiptContentValidationException)
                    {
                        invalidDocumentRejectedBeforeEffect = true;
                    }

                    foreach (var invalidCopies in new[] { 0, 4, int.MaxValue })
                    {
                        if (!await ExpectInvalidOperationAsync(() => spooler.PrintAsync(
                                "QA INVALID COPY COUNT MUST NOT PRINT",
                                new ReceiptPrintOptions
                                {
                                    PrinterName = "QA MUST NOT REACH HARDWARE",
                                    Copies = invalidCopies
                                })).ConfigureAwait(true))
                        {
                            return false;
                        }
                    }

                    var tailsAfter = tailsField.GetValue(null) as System.Collections.IDictionary;
                    return invalidDocumentRejectedBeforeEffect &&
                           (tailsAfter?.Count ?? 0) == tailCountBefore;
                }
                finally
                {
                    await RestoreAppSettingAsync(factory, primaryKey, primaryBefore).ConfigureAwait(true);
                    await RestoreAppSettingAsync(factory, legacyKey, legacyBefore).ConfigureAwait(true);
                }
            }

            private static PosPrinterSettings ClonePrinterSettings(PosPrinterSettings source)
            {
                return new PosPrinterSettings
                {
                    PrinterName = source?.PrinterName ?? string.Empty,
                    Copies = source?.Copies ?? ReceiptPrintOptions.MinimumCopies,
                    ReceiptEnabled = source?.ReceiptEnabled ?? false,
                    AutoPrint = source?.AutoPrint ?? false,
                    AllowWindowsDefault = source?.AllowWindowsDefault ?? false,
                    AllowVirtualPrinters = source?.AllowVirtualPrinters ?? false,
                    CashDrawerCommand = source?.CashDrawerCommand ?? string.Empty,
                    CashDrawerEnabled = source?.CashDrawerEnabled ?? false,
                    CashDrawerMode = source?.CashDrawerMode ?? "disabled",
                    CashDrawerPrinterName = source?.CashDrawerPrinterName ?? string.Empty,
                    CashDrawerOpenOnCashSale = source?.CashDrawerOpenOnCashSale ?? false
                };
            }

            private static async Task RestoreAppSettingAsync(
                SqliteConnectionFactory factory,
                string key,
                string value)
            {
                using (var connection = factory.Open())
                {
                    if (value == null)
                    {
                        await connection.ExecuteAsync(
                            "DELETE FROM app_settings WHERE key = @key",
                            new { key }).ConfigureAwait(true);
                        return;
                    }

                    await connection.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                        new { key, value }).ConfigureAwait(true);
                }
            }

            private static async Task<bool> ExpectInvalidOperationAsync(Func<Task> action)
            {
                try
                {
                    await action().ConfigureAwait(true);
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }

            private static async Task<bool> VerifySharedPrinterEffectCoordinatorAsync()
            {
                var startEffect = typeof(WindowsSpoolerReceiptPrinter).GetMethod(
                    "StartExclusivePrinterEffect",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var awaitEffect = typeof(WindowsSpoolerReceiptPrinter).GetMethod(
                    "AwaitEffectWithinTimeoutAsync",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var tailsField = typeof(WindowsSpoolerReceiptPrinter).GetField(
                    "PrinterEffectTails",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (startEffect == null || awaitEffect == null || tailsField == null) return false;

                var printerName = "QA Effect " + Guid.NewGuid().ToString("N");
                var otherPrinterName = "QA Effect Other " + Guid.NewGuid().ToString("N");
                var firstStarted = new TaskCompletionSource<bool>();
                var releaseFirst = new TaskCompletionSource<bool>();
                var secondStarted = new TaskCompletionSource<bool>();
                Task first = null;
                try
                {
                    first = startEffect.Invoke(
                        null,
                        new object[]
                        {
                            printerName,
                            new Action(() =>
                            {
                                firstStarted.TrySetResult(true);
                                releaseFirst.Task.GetAwaiter().GetResult();
                            })
                        }) as Task;
                    if (first == null ||
                        await Task.WhenAny(firstStarted.Task, Task.Delay(2000)).ConfigureAwait(true) != firstStarted.Task)
                    {
                        return false;
                    }

                    try
                    {
                        startEffect.Invoke(
                            null,
                            new object[]
                            {
                                printerName.ToLowerInvariant(),
                                new Action(() => secondStarted.TrySetResult(true))
                            });
                        return false;
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                    {
                    }

                    var otherStarted = false;
                    var other = startEffect.Invoke(
                        null,
                        new object[]
                        {
                            otherPrinterName,
                            new Action(() => otherStarted = true)
                        }) as Task;
                    if (other == null) return false;
                    await other.ConfigureAwait(true);
                    if (!otherStarted) return false;

                    var boundedWait = awaitEffect.Invoke(
                        null,
                        new object[] { first, "QA expected timeout", 50 }) as Task;
                    if (boundedWait == null) return false;
                    try
                    {
                        await boundedWait.ConfigureAwait(true);
                        return false;
                    }
                    catch (TimeoutException)
                    {
                    }

                    try
                    {
                        startEffect.Invoke(
                            null,
                            new object[]
                            {
                                printerName,
                                new Action(() => secondStarted.TrySetResult(true))
                            });
                        return false;
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                    {
                    }

                    releaseFirst.TrySetResult(true);
                    await first.ConfigureAwait(true);
                    await Task.Delay(50).ConfigureAwait(true);
                    if (secondStarted.Task.IsCompleted) return false;

                    var afterTimeoutStarted = false;
                    var afterTimeout = startEffect.Invoke(
                        null,
                        new object[]
                        {
                            printerName,
                            new Action(() => afterTimeoutStarted = true)
                        }) as Task;
                    if (afterTimeout == null) return false;
                    await afterTimeout.ConfigureAwait(true);
                    if (!afterTimeoutStarted || secondStarted.Task.IsCompleted) return false;

                    var faulted = startEffect.Invoke(
                        null,
                        new object[]
                        {
                            printerName,
                            new Action(() => { throw new InvalidOperationException("QA expected effect failure"); })
                        }) as Task;
                    if (faulted == null) return false;
                    try
                    {
                        await faulted.ConfigureAwait(true);
                        return false;
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    var continued = false;
                    var afterFault = startEffect.Invoke(
                        null,
                        new object[]
                        {
                            printerName,
                            new Action(() => continued = true)
                        }) as Task;
                    if (afterFault == null) return false;
                    await afterFault.ConfigureAwait(true);
                    await Task.Delay(50).ConfigureAwait(true);
                    var tails = tailsField.GetValue(null) as System.Collections.IDictionary;
                    return continued &&
                           !secondStarted.Task.IsCompleted &&
                           (tails == null ||
                            (!tails.Contains(printerName) && !tails.Contains(otherPrinterName)));
                }
                finally
                {
                    releaseFirst.TrySetResult(true);
                    if (first != null)
                    {
                        try { await first.ConfigureAwait(true); }
                        catch { }
                    }
                }
            }

            private static bool VerifyCashDrawerCommandParsing()
            {
                var parse = typeof(WindowsSpoolerReceiptPrinter).GetMethod(
                    "ParseCashDrawerCommand",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (parse == null) return false;

                var valid = new[]
                {
                    "27,112,0,25,250",
                    "27 112 1 50 250",
                    "27;112;48;0;1",
                    "27,112,49,0,255"
                };
                foreach (var command in valid)
                {
                    if (!TryParseCashDrawerCommandForSmoke(parse, command, out var bytes) ||
                        bytes == null || bytes.Length != 5)
                    {
                        return false;
                    }
                }

                var invalid = new[]
                {
                    null,
                    string.Empty,
                    "   ",
                    "27,,112,0,25,250",
                    "27,112,0,25,256",
                    "27,112,0,25,not-a-byte",
                    "26,112,0,25,250",
                    "27,111,0,25,250",
                    "27,112,2,25,250",
                    "27,112,0,25,25",
                    "27,112,0,50,25",
                    "27,112,0,25",
                    "27,112,0,25,250,0",
                    new string(' ', WindowsSpoolerReceiptPrinter.MaximumCashDrawerCommandLength + 1),
                    new string('1', WindowsSpoolerReceiptPrinter.MaximumCashDrawerCommandLength + 1),
                    new string('9', 10000)
                };
                return invalid.All(command =>
                    !TryParseCashDrawerCommandForSmoke(parse, command, out _));
            }

            private static bool TryParseCashDrawerCommandForSmoke(
                MethodInfo parse,
                string command,
                out byte[] bytes)
            {
                try
                {
                    bytes = parse.Invoke(null, new object[] { command }) as byte[];
                    return true;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is FormatException)
                {
                    bytes = null;
                    return false;
                }
            }

            private static async Task<bool> VerifyReceiptShopSnapshotReprintAsync()
            {
                var serialize = typeof(PosWorkflowService).GetMethod(
                    "SerializeReceiptShopSnapshot",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (serialize == null) return false;

                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                var shopAtSale = new ReceiptShopInfo
                {
                    Name = "QA Snapshot Shop A",
                    Address = "Snapshot Avenue A",
                    City = "Santiago",
                    Rut = "76.000.000-1",
                    Footer = "Snapshot footer A"
                };
                var serialized = serialize.Invoke(null, new object[] { shopAtSale }) as string;
                if (string.IsNullOrWhiteSpace(serialized)) return false;

                long saleId;
                var code = "QA-SNAPSHOT-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    .ToString(CultureInfo.InvariantCulture);
                using (var conn = factory.Open())
                {
                    saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, total, paidCash, paidCard, change, receipt_shop_snapshot)
VALUES(@code, @createdAt, 0, 1250, 1250, 0, 0, @serialized);
SELECT last_insert_rowid();",
                        new
                        {
                            code,
                            createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            serialized
                        }).ConfigureAwait(true);
                    await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, NULL, 'QA-SNAPSHOT-LINE', 'Snapshot product', 1, 1250, 1250);",
                        new { saleId }).ConfigureAwait(true);
                }

                var persisted = await new SaleRepository(factory).GetByIdAsync(saleId).ConfigureAwait(true);
                if (persisted == null || !string.Equals(
                    persisted.ReceiptShopSnapshotJson,
                    serialized,
                    StringComparison.Ordinal))
                {
                    return false;
                }

                await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
                {
                    ShopId = QaShopId,
                    ShopCode = QaShopCode,
                    ShopName = "QA Snapshot Shop B",
                    BusinessAddress = "Snapshot Avenue B",
                    CompanyRut = "76.000.000-2",
                    Footer = "Snapshot footer B",
                    Source = "qa_harness"
                }).ConfigureAwait(true);

                var preview = await new PosWorkflowService()
                    .GetReceiptPreviewBySaleIdAsync(saleId, true)
                    .ConfigureAwait(true);
                return preview.IndexOf("QA SNAPSHOT SHOP A", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       preview.IndexOf("SNAPSHOT AVENUE A", StringComparison.OrdinalIgnoreCase) >= 0 &&
                       preview.IndexOf("QA SNAPSHOT SHOP B", StringComparison.OrdinalIgnoreCase) < 0;
            }

            private static async Task<bool> VerifyActiveDrawerSettingsValidationAsync()
            {
                var service = new PosWorkflowService();
                var before = await service.GetPrinterSettingsAsync().ConfigureAwait(true);
                var invalid = new PosPrinterSettings
                {
                    PrinterName = before.PrinterName,
                    Copies = before.Copies,
                    ReceiptEnabled = before.ReceiptEnabled,
                    AutoPrint = before.AutoPrint,
                    AllowWindowsDefault = before.AllowWindowsDefault,
                    AllowVirtualPrinters = before.AllowVirtualPrinters,
                    CashDrawerCommand = " ",
                    CashDrawerEnabled = true,
                    CashDrawerMode = "printer_kick",
                    CashDrawerPrinterName = before.CashDrawerPrinterName,
                    CashDrawerOpenOnCashSale = before.CashDrawerOpenOnCashSale
                };

                try
                {
                    await service.SetPrinterSettingsAsync(invalid).ConfigureAwait(true);
                    return false;
                }
                catch (InvalidOperationException)
                {
                    var after = await service.GetPrinterSettingsAsync().ConfigureAwait(true);
                    return string.Equals(after.CashDrawerCommand, before.CashDrawerCommand, StringComparison.Ordinal) &&
                           after.CashDrawerEnabled == before.CashDrawerEnabled &&
                           string.Equals(after.CashDrawerMode, before.CashDrawerMode, StringComparison.Ordinal);
                }
            }

            private static bool VerifyDailyCloseReceiptWidthAndParity()
            {
                var model = new DailyTakingsReceiptModel
                {
                    Date = new DateTime(2026, 7, 17),
                    PeriodStart = new DateTime(2026, 7, 17),
                    PeriodEnd = new DateTime(2026, 7, 17),
                    OperatorName = "Operatore QA con nome volutamente molto lungo",
                    GeneratedAt = new DateTimeOffset(2026, 7, 17, 22, 30, 0, TimeSpan.Zero),
                    SalesCount = 987654,
                    GrossSalesAmount = 9876543210,
                    DiscountsAmount = 123456789,
                    TaxAmount = 187654321,
                    RefundsAmount = 87654321,
                    VoidsAmount = 7654321,
                    NetAmount = 9543209876,
                    CashAmount = 4321098765,
                    CardAmount = 5222111111,
                    MixedSalesCount = 12345,
                    ChangeAmount = 1234567,
                    OpeningAmount = 100000,
                    ClosingAmount = 4320000000,
                    ExpectedCashAmount = 4322222222,
                    DifferenceAmount = -2222222,
                    PendingSyncCount = 123,
                    RetrySyncCount = 45,
                    BlockedSyncCount = 6
                };
                var shop = new ReceiptShopInfo
                {
                    Name = "QA 日常营业结算商店名称非常长",
                    Address = "Avenida de validación internacional extremadamente larga 12345",
                    City = "Santiago",
                    Rut = "76.123.456-7",
                    Footer = "Grazie · Gracias · 谢谢"
                };
                var cultures = new[] { "en-US", "es-CL", "it-IT", "zh-CN" };
                foreach (var width in new[] { 32, 42 })
                {
                    foreach (var culture in cultures)
                    {
                        var options = new ReceiptOptions
                        {
                            Width = width,
                            Currency = "CLP",
                            CultureName = culture,
                            Labels = ReceiptLabels.English
                        };
                        var labels = new DailyCloseReceiptLabels
                        {
                            BusinessDate = "Data commerciale / Fecha comercial / 营业日期",
                            Period = "Periodo di rendicontazione estremamente lungo",
                            Operator = "Operatore responsabile / 操作员",
                            Discounts = "Sconti complessivi applicati",
                            Tax = "Imposte",
                            Mixed = "Pagamenti misti",
                            Voids = "Annullamenti",
                            ExpectedCash = "Contanti attesi nel cassetto",
                            OpeningAmount = "Importo iniziale",
                            ClosingAmount = "Importo di chiusura",
                            Difference = "Differenza negativa",
                            PendingSync = "Sincronizzazioni in attesa",
                            RetrySync = "Sincronizzazioni da riprovare",
                            BlockedSync = "Sincronizzazioni bloccate",
                            Generated = "Generato il"
                        };
                        var preview = Win7POS.Core.Reports.DailyCloseReceiptTextRenderer.Render(
                            model, shop, options, labels);
                        var printed = Win7POS.Core.Reports.DailyCloseReceiptTextRenderer.Render(
                            model, shop, options, labels);
                        if (!string.Equals(preview, printed, StringComparison.Ordinal)) return false;
                        var lines = preview.Replace("\r\n", "\n").Split('\n');
                        if (lines.Any(line => ReceiptTextLayout.VisibleWidth(line) > width)) return false;
                    }
                }

                return true;
            }

            private static async Task<bool> VerifyReceiptArchiveRemovalAsync()
            {
                var forbiddenNames = new[]
                {
                    "SaveCopyToFile",
                    "OutputDirectory",
                    "OutputPath",
                    "GenerateFiscalPdf"
                };
                var types = new[]
                {
                    typeof(ReceiptPrintOptions),
                    typeof(PosPrinterSettings),
                    typeof(PrinterSettingsViewModel),
                    typeof(PosPrintResult),
                    typeof(PaymentViewModel)
                };
                var archiveApiRemoved = types.All(type => forbiddenNames.All(name =>
                    type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null &&
                    type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null));
                if (!archiveApiRemoved) return false;

                var safeStartProperty = typeof(Win7POS.Wpf.App).GetProperty(
                    "IsSafeStart",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var isSafeStart = safeStartProperty != null &&
                                  safeStartProperty.GetValue(null) is bool safeStartEnabled &&
                                  safeStartEnabled;
                var exportDirectory = Win7POS.Core.AppPaths.ExportsDirectory;
                var before = Directory.Exists(exportDirectory)
                    ? new HashSet<string>(Directory.GetFiles(exportDirectory, "*.pdf", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var createdByCheck = new List<string>();
                try
                {
                    PaymentReceiptDraft lastDraft = null;
                    foreach (var use42 in new[] { false, true })
                    {
                        var draft = new PaymentReceiptDraft
                        {
                            SaleCode = "QA-DIRECT-BOLETA-" + (use42 ? "42" : "32"),
                            CreatedAtMs = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)
                                .ToUnixTimeMilliseconds(),
                            NextBoletaNumber = 42,
                            UseReceipt42 = use42,
                            ShopInfo = new ReceiptShopInfo
                            {
                                Name = "QA Direct Print con nombre muy largo 中文",
                                Rut = "76.000.000-0",
                                BusinessGiro = "Validación de impresión térmica únicamente",
                                LegalRepresentativeRut = "12.345.678-9",
                                Address = "QA only - dirección de validación muy larga 12345",
                                City = "Santiago"
                            }
                        };
                        lastDraft = draft;
                        var printCalls = 0;
                        string printedText = null;
                        string printedCode = null;
                        using (var vm = new PaymentViewModel(
                            1000,
                            draft,
                            (text, code) =>
                            {
                                printCalls++;
                                printedText = text;
                                printedCode = code;
                                return Task.CompletedTask;
                            },
                            openDrawerDefault: false))
                        {
                            vm.NextBoletaNumber = 1;
                            if (vm.NextBoletaNumber != 42) return false;
                            vm.CashReceived = "1000";
                            vm.CardAmount = "0";
                            vm.AutoPrintFiscalBoleta = true;

                            var cashPrinted = await vm.TriggerAutoPrintFiscalBoletaIfEnabledAsync().ConfigureAwait(true);
                            if (isSafeStart)
                            {
                                if (cashPrinted || printCalls != 0 || vm.AutoPrintFiscalBoleta) return false;
                            }
                            else
                            {
                                var width = use42 ? 42 : 32;
                                var markerCount = (printedText ?? string.Empty)
                                    .Replace("\r\n", "\n")
                                    .Split('\n')
                                    .Count(line => string.Equals(
                                        line,
                                        FiscalBoletaTextRenderer.SiiStampMarker,
                                        StringComparison.Ordinal));
                                if (!cashPrinted || printCalls != 1 ||
                                    !string.Equals(printedText, vm.FiscalPreviewText, StringComparison.Ordinal) ||
                                    !string.Equals(printedCode, draft.SaleCode, StringComparison.Ordinal) ||
                                    markerCount != 1 ||
                                    (printedText ?? string.Empty).Replace("\r\n", "\n").Split('\n')
                                        .Any(line => ReceiptTextLayout.VisibleWidth(line) > width) ||
                                    !string.Equals(vm.FiscalStatus, PosLocalization.T("payment.printed"), StringComparison.Ordinal))
                                    return false;

                                vm.CashReceived = "0";
                                vm.CardAmount = "1000";
                                var cardPrinted = await vm.TriggerAutoPrintFiscalBoletaIfEnabledAsync().ConfigureAwait(true);
                                if (cardPrinted || printCalls != 1 ||
                                    !string.Equals(vm.FiscalStatus, PosLocalization.T("payment.notPrintedCardOnly"), StringComparison.Ordinal))
                                    return false;
                            }
                        }
                    }

                    if (!isSafeStart)
                    {
                        var expectedFailure = new InvalidOperationException("QA spooler failure");
                        var failedPrintCalls = 0;
                        using (var failingVm = new PaymentViewModel(
                            1000,
                            lastDraft,
                            (text, code) =>
                            {
                                failedPrintCalls++;
                                return Task.FromException(expectedFailure);
                            },
                            openDrawerDefault: false))
                        {
                            failingVm.CashReceived = "1000";
                            failingVm.AutoPrintFiscalBoleta = true;
                            try
                            {
                                await failingVm.TriggerAutoPrintFiscalBoletaIfEnabledAsync().ConfigureAwait(true);
                                return false;
                            }
                            catch (InvalidOperationException ex)
                            {
                                if (!ReferenceEquals(ex, expectedFailure) ||
                                    failedPrintCalls != 1 ||
                                    string.Equals(
                                        failingVm.FiscalStatus,
                                        PosLocalization.T("payment.printed"),
                                        StringComparison.Ordinal))
                                {
                                    return false;
                                }
                            }
                        }
                    }

                    if (Directory.Exists(exportDirectory))
                    {
                        createdByCheck.AddRange(Directory.GetFiles(exportDirectory, "*.pdf", SearchOption.AllDirectories)
                            .Where(path => !before.Contains(path)));
                    }

                    return createdByCheck.Count == 0;
                }
                finally
                {
                    if (Directory.Exists(exportDirectory))
                    {
                        createdByCheck.AddRange(Directory.GetFiles(exportDirectory, "*.pdf", SearchOption.AllDirectories)
                            .Where(path => !before.Contains(path) && !createdByCheck.Contains(path, StringComparer.OrdinalIgnoreCase)));
                    }

                    foreach (var path in createdByCheck.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try { File.Delete(path); }
                        catch { }
                    }
                }
            }

            private static async Task<bool> VerifySalesRegisterRapidSelectionAsync()
            {
                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                var seedPrefix = "QA-RAPID-SELECTION-" + Guid.NewGuid().ToString("N");
                using (var conn = factory.Open())
                {
                    for (var index = 0; index < 2; index++)
                    {
                        var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, total, paidCash, paidCard, change)
VALUES(@code, @createdAt, 0, @total, @total, 0, 0);
SELECT last_insert_rowid();",
                            new
                            {
                                code = seedPrefix + "-" + index.ToString(CultureInfo.InvariantCulture),
                                createdAt = DateTimeOffset.Now.AddMinutes(-index).ToUnixTimeMilliseconds(),
                                total = 1000L + index
                            }).ConfigureAwait(true);
                        await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal)
VALUES(@saleId, NULL, @barcode, @name, 1, @total, @total);",
                            new
                            {
                                saleId,
                                barcode = seedPrefix + "-LINE-" + index.ToString(CultureInfo.InvariantCulture),
                                name = "QA rapid selection item " + index.ToString(CultureInfo.InvariantCulture),
                                total = 1000L + index
                            }).ConfigureAwait(true);
                    }
                }

                var service = new PosWorkflowService();
                var vm = new SalesRegisterViewModel(
                    service,
                    useReceipt42: true,
                    permissionService: new HarnessPermissionService());
                try
                {
                    vm.LoadCommand.Execute(null);
                    for (var attempt = 0; attempt < 80 && vm.IsBusy; attempt++)
                        await Task.Delay(25).ConfigureAwait(true);
                    if (vm.SalesList.Count < 2) return false;

                    for (var index = 0; index < 20; index++)
                    {
                        vm.SelectedSale = vm.SalesList[index % vm.SalesList.Count];
                        await Task.Delay(2).ConfigureAwait(true);
                    }

                    var expectedSelection = vm.SalesList[vm.SalesList.Count - 1];
                    vm.SelectedSale = expectedSelection;
                    for (var attempt = 0; attempt < 120 && vm.IsPreviewLoading; attempt++)
                        await Task.Delay(25).ConfigureAwait(true);

                    var expected = await service
                        .GetReceiptPreviewBySaleIdAsync(expectedSelection.SaleId, true)
                        .ConfigureAwait(true);
                    return !vm.IsPreviewLoading &&
                           vm.HasReceiptPreview &&
                           vm.PrintCommand.CanExecute(null) &&
                           string.Equals(vm.DetailReceiptPreview, expected, StringComparison.Ordinal);
                }
                finally
                {
                    vm.Dispose();
                }
            }

            private static async Task<bool> VerifyDailyHistoryPreviewFencingAsync()
            {
                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                var service = new PosWorkflowService();
                var vm = new DailyReportViewModel(service);
                var prefix = "QA-DAILY-FENCE-" + Guid.NewGuid().ToString("N");
                var dayA = DateTime.Today.AddYears(-3).Date;
                var dayB = dayA.AddDays(1);
                try
                {
                    using (var conn = factory.Open())
                    {
                        await conn.ExecuteAsync(@"
INSERT INTO sales(code, createdAt, kind, total, paidCash, paidCard, change, pdf_printed)
VALUES(@code, @createdAt, 0, @total, @paidCash, @paidCard, 0, @pdfPrinted);",
                            new[]
                            {
                                new
                                {
                                    code = prefix + "-A-CASH",
                                    createdAt = new DateTimeOffset(dayA.AddHours(10)).ToUnixTimeMilliseconds(),
                                    total = 1111L,
                                    paidCash = 1111L,
                                    paidCard = 0L,
                                    pdfPrinted = 0
                                },
                                new
                                {
                                    code = prefix + "-A-CARD-PRINTED",
                                    createdAt = new DateTimeOffset(dayA.AddHours(11)).ToUnixTimeMilliseconds(),
                                    total = 2222L,
                                    paidCash = 0L,
                                    paidCard = 2222L,
                                    pdfPrinted = 1
                                },
                                new
                                {
                                    code = prefix + "-B-CASH",
                                    createdAt = new DateTimeOffset(dayB.AddHours(12)).ToUnixTimeMilliseconds(),
                                    total = 4444L,
                                    paidCash = 4444L,
                                    paidCard = 0L,
                                    pdfPrinted = 0
                                }
                            }).ConfigureAwait(true);
                    }

                    vm.SelectedTabIndex = 1;
                    vm.HistoryFromText = dayA.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    vm.HistoryToText = dayB.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    vm.LoadHistoryCommand.Execute(null);
                    for (var attempt = 0; attempt < 160 && vm.IsBusy; attempt++)
                        await Task.Delay(25).ConfigureAwait(true);
                    if (vm.IsBusy || vm.HistoryRows.Count != 2) return false;

                    vm.ClearMarkedHistoryRowsCommand.Execute(null);
                    if (vm.MarkedCount != 0) return false;

                    var rowA = vm.HistoryRows.SingleOrDefault(row => row.Date == dayA);
                    var rowB = vm.HistoryRows.SingleOrDefault(row => row.Date == dayB);
                    if (rowA == null || rowB == null) return false;

                    vm.SelectedHistoryRow = rowB;
                    for (var attempt = 0; attempt < 160 && vm.IsHistoryPreviewLoading; attempt++)
                        await Task.Delay(25).ConfigureAwait(true);
                    var previousPreview = vm.SummaryReceiptPreview;
                    if (vm.IsHistoryPreviewLoading || string.IsNullOrWhiteSpace(previousPreview)) return false;

                    vm.SelectedHistoryRow = rowA;
                    var immediateFencePass = vm.IsHistoryPreviewLoading
                        ? string.IsNullOrEmpty(vm.SummaryReceiptPreview) &&
                          !vm.PrintSelectedHistoryCommand.CanExecute(null)
                        : !string.IsNullOrWhiteSpace(vm.SummaryReceiptPreview) &&
                          !string.Equals(vm.SummaryReceiptPreview, previousPreview, StringComparison.Ordinal);
                    if (!immediateFencePass) return false;

                    for (var attempt = 0; attempt < 160 && vm.IsHistoryPreviewLoading; attempt++)
                        await Task.Delay(25).ConfigureAwait(true);

                    var selectedPreviewPass = !vm.IsHistoryPreviewLoading &&
                                              rowA.SalesCount == 2 &&
                                              rowA.NetAmount == 3333L &&
                                              rowA.CashAmount == 1111L &&
                                              rowA.CardAmount == 2222L &&
                                              !string.IsNullOrWhiteSpace(vm.SummaryReceiptPreview) &&
                                              !string.Equals(vm.SummaryReceiptPreview, previousPreview, StringComparison.Ordinal) &&
                                              vm.PrintSelectedHistoryCommand.CanExecute(null);
                    if (!selectedPreviewPass) return false;

                    rowA.IsMarked = true;
                    var markedFencePass = vm.IsMarkedPreviewLoading
                        ? string.IsNullOrEmpty(vm.SingleMarkedReceiptPreview) &&
                          !vm.PrintSelectedHistoryCommand.CanExecute(null)
                        : !string.IsNullOrWhiteSpace(vm.SingleMarkedReceiptPreview);
                    if (!markedFencePass) return false;
                    for (var attempt = 0; attempt < 160 && vm.IsMarkedPreviewLoading; attempt++)
                        await Task.Delay(25).ConfigureAwait(true);
                    var markedAPreview = vm.SingleMarkedReceiptPreview;
                    if (vm.IsMarkedPreviewLoading || string.IsNullOrWhiteSpace(markedAPreview)) return false;

                    rowA.IsMarked = false;
                    rowB.IsMarked = true;
                    var switchedMarkedFencePass = vm.IsMarkedPreviewLoading
                        ? string.IsNullOrEmpty(vm.SingleMarkedReceiptPreview) &&
                          !vm.PrintSelectedHistoryCommand.CanExecute(null)
                        : !string.IsNullOrWhiteSpace(vm.SingleMarkedReceiptPreview) &&
                          !string.Equals(vm.SingleMarkedReceiptPreview, markedAPreview, StringComparison.Ordinal);
                    if (!switchedMarkedFencePass) return false;
                    for (var attempt = 0; attempt < 160 && vm.IsMarkedPreviewLoading; attempt++)
                        await Task.Delay(25).ConfigureAwait(true);

                    return !vm.IsMarkedPreviewLoading &&
                           vm.MarkedCount == 1 &&
                           !string.IsNullOrWhiteSpace(vm.SingleMarkedReceiptPreview) &&
                           !string.Equals(vm.SingleMarkedReceiptPreview, markedAPreview, StringComparison.Ordinal) &&
                           vm.PrintSelectedHistoryCommand.CanExecute(null);
                }
                finally
                {
                    vm.Dispose();
                    using (var conn = factory.Open())
                    {
                        await conn.ExecuteAsync(
                            "DELETE FROM sales WHERE code LIKE @prefix;",
                            new { prefix = prefix + "%" }).ConfigureAwait(true);
                    }
                }
            }

            private static bool VerifyDailyDatePreviewFencing()
            {
                var vm = new DailyReportViewModel(new PosWorkflowService());
                try
                {
                    vm.SummaryReceiptPreview = "STALE DAILY RECEIPT";
                    if (!vm.PrintSummaryCommand.CanExecute(null)) return false;

                    vm.DateText = DateTime.Today.AddDays(-1)
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                    return string.IsNullOrEmpty(vm.SummaryReceiptPreview) &&
                           !vm.PrintSummaryCommand.CanExecute(null) &&
                           !vm.PrintSelectedHistoryCommand.CanExecute(null);
                }
                finally
                {
                    vm.Dispose();
                }
            }

            private static bool VerifyReceiptColumnFit()
            {
                var fit = typeof(WindowsSpoolerReceiptPrinter).GetMethod(
                    "CreateColumnFittedFont",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (fit == null) return false;

                using (var bitmap = new System.Drawing.Bitmap(400, 100))
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                using (var sourceFont = new System.Drawing.Font("Courier New", 9f))
                using (var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic)
                {
                    FormatFlags = System.Drawing.StringFormatFlags.MeasureTrailingSpaces |
                                  System.Drawing.StringFormatFlags.NoWrap
                })
                {
                    var sourceWidth42 = graphics.MeasureString(
                        new string('W', 42),
                        sourceFont,
                        System.Drawing.PointF.Empty,
                        format).Width;
                    var narrowPrintableWidth = sourceWidth42 * 0.85f;
                    using (var fittedFont = fit.Invoke(
                        null,
                        new object[] { graphics, sourceFont, 42, narrowPrintableWidth, format }) as System.Drawing.Font)
                    {
                        if (fittedFont == null) return false;
                        var width = graphics.MeasureString(
                            new string('W', 42),
                            fittedFont,
                            System.Drawing.PointF.Empty,
                            format).Width;
                        if (fittedFont.Size < 5f ||
                            fittedFont.Size >= sourceFont.Size ||
                            width > narrowPrintableWidth)
                            return false;
                    }

                    var sourceWidth32 = graphics.MeasureString(
                        new string('W', 32),
                        sourceFont,
                        System.Drawing.PointF.Empty,
                        format).Width;
                    var widePrintableWidth = sourceWidth32 * 1.10f;
                    using (var fitted32 = fit.Invoke(
                        null,
                        new object[] { graphics, sourceFont, 32, widePrintableWidth, format }) as System.Drawing.Font)
                    {
                        return fitted32 != null &&
                               Math.Abs(fitted32.Size - sourceFont.Size) < 0.01f;
                    }
                }
            }

            private static bool VerifyPrinterTestReceiptBuilder()
            {
                var build = typeof(PosWorkflowService).GetMethod(
                    "BuildPrinterTestReceipt",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (build == null) return false;

                var shop = new ReceiptShopInfo
                {
                    Name = "QA Café",
                    Address = "Calle Información 123",
                    City = "Santiago",
                    Rut = "76.123.456-7",
                    Phone = "+56 2 1234 5678",
                    Footer = "Grazie - Gracias"
                };
                var createdAt = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
                var first = build.Invoke(null, new object[] { shop, true, createdAt }) as string;
                var second = build.Invoke(null, new object[] { shop, true, createdAt }) as string;
                if (string.IsNullOrWhiteSpace(first) || !string.Equals(first, second, StringComparison.Ordinal))
                    return false;

                var lines = first.Replace("\r\n", "\n").Split('\n');
                var preview32 = build.Invoke(null, new object[] { shop, false, createdAt }) as string;
                var lines32 = (preview32 ?? string.Empty).Replace("\r\n", "\n").Split('\n');
                return lines.All(line => ReceiptTextLayout.VisibleWidth(line) <= 42) &&
                       lines32.All(line => ReceiptTextLayout.VisibleWidth(line) <= 32) &&
                       first.IndexOf("QA CAFÉ", StringComparison.Ordinal) >= 0 &&
                       first.IndexOf("76.123.456-7", StringComparison.Ordinal) >= 0 &&
                       first.IndexOf("Caffè", StringComparison.Ordinal) >= 0 &&
                       first.IndexOf("información", StringComparison.Ordinal) >= 0 &&
                       first.IndexOf("pingüino", StringComparison.Ordinal) >= 0 &&
                       first.IndexOf("TEST-NO-SALE", StringComparison.Ordinal) >= 0 &&
                       first.IndexOf("14", StringComparison.Ordinal) >= 0 &&
                       first.IndexOf(PosLocalization.T("common.cash"), StringComparison.Ordinal) >= 0 &&
                       first.IndexOf(PosLocalization.T("common.card"), StringComparison.Ordinal) >= 0 &&
                       first.IndexOf(
                           Win7POS.Wpf.Localization.PosLocalization.T("printer.testReceiptMarker"),
                           StringComparison.Ordinal) >= 0 &&
                       VerifyReceiptRenderingAlignment();
            }

            private static bool VerifyReceiptRenderingAlignment()
            {
                var rendererType = typeof(PosWorkflowService).Assembly.GetType(
                    "Win7POS.Wpf.Pos.PosReceiptTextRenderer",
                    throwOnError: false);
                var build = rendererType?.GetMethod(
                    "BuildReceipt",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    binder: null,
                    types: new[]
                    {
                        typeof(Sale),
                        typeof(IReadOnlyList<SaleLine>),
                        typeof(bool),
                        typeof(ReceiptShopInfo)
                    },
                    modifiers: null);
                if (build == null) return false;

                const long total = 14691;
                var createdAtMs = new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)
                    .ToUnixTimeMilliseconds();
                var shop = new ReceiptShopInfo
                {
                    Name = "QA Café",
                    Address = "Calle Información 123",
                    City = "Santiago",
                    Rut = "76.123.456-7",
                    Phone = "+56 2 1234 5678",
                    Footer = "Grazie - Gracias"
                };
                var draftLines = new List<PaymentReceiptDraftLine>
                {
                    new PaymentReceiptDraftLine
                    {
                        Barcode = "TEST-CAFFE",
                        Name = "Caffè più qualità - información",
                        Quantity = 2,
                        UnitPrice = 6173,
                        LineTotal = 12346
                    },
                    new PaymentReceiptDraftLine
                    {
                        Barcode = "TEST-PINGUINO",
                        Name = "Confezione città pingüino niño",
                        Quantity = 1,
                        UnitPrice = 2345,
                        LineTotal = 2345
                    }
                };
                var saleLines = draftLines.Select(x => new SaleLine
                {
                    Barcode = x.Barcode,
                    Name = x.Name,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    LineTotal = x.LineTotal
                }).ToList();
                var mixes = new[]
                {
                    new ReceiptPaymentMix(15000, 0, 309),
                    new ReceiptPaymentMix(0, 14691, 0),
                    new ReceiptPaymentMix(7000, 7691, 0)
                };

                foreach (var use42 in new[] { false, true })
                {
                    foreach (var mix in mixes)
                    {
                        var draft = new PaymentReceiptDraft
                        {
                            SaleCode = "QA-RECEIPT-ALIGNMENT",
                            CreatedAtMs = createdAtMs,
                            CartLines = draftLines,
                            UseReceipt42 = use42,
                            ShopInfo = shop
                        };
                        using (var viewModel = new PaymentViewModel(total, draft, openDrawerDefault: false)
                        {
                            CashReceived = mix.Cash.ToString(CultureInfo.InvariantCulture),
                            CardAmount = mix.Card.ToString(CultureInfo.InvariantCulture)
                        })
                        {
                            var sale = new Sale
                            {
                                Code = draft.SaleCode,
                                CreatedAt = createdAtMs,
                                Total = total,
                                PaidCash = mix.Cash,
                                PaidCard = mix.Card,
                                Change = mix.Change
                            };
                            var expected = build.Invoke(
                                null,
                                new object[] { sale, saleLines, use42, shop }) as string;
                            if (string.IsNullOrWhiteSpace(expected) ||
                                !string.Equals(viewModel.ReceiptPreviewText, expected, StringComparison.Ordinal))
                            {
                                return false;
                            }

                            var recomposed = viewModel.ReceiptPreviewFirstLine + Environment.NewLine +
                                             viewModel.ReceiptPreviewRest;
                            if (!string.Equals(recomposed, expected, StringComparison.Ordinal))
                                return false;

                            var width = use42 ? 42 : 32;
                            if (expected.Replace("\r\n", "\n").Split('\n')
                                .Any(line => ReceiptTextLayout.VisibleWidth(line) > width))
                                return false;
                        }
                    }
                }

                if (!VerifyDiscountReceiptAlignment(createdAtMs, shop))
                    return false;
                if (!VerifyReceiptLocalizationMatrix(createdAtMs, shop))
                    return false;

                var sampleBuilder = typeof(PosWorkflowService).GetMethod(
                    "BuildPrinterTestReceipt",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var sample = sampleBuilder?.Invoke(
                    null,
                    new object[]
                    {
                        shop,
                        true,
                        DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs)
                    }) as string;
                var sampleSale = new Sale
                {
                    ClientSaleId = "TEST-NO-SALE",
                    Code = "TEST-NO-SALE",
                    CreatedAt = createdAtMs,
                    Total = total,
                    PaidCash = 7000,
                    PaidCard = 7691,
                    Change = 0,
                    SyncStatus = "test_only"
                };
                var productionSample = build.Invoke(
                    null,
                    new object[] { sampleSale, saleLines, true, shop }) as string;
                if (string.IsNullOrWhiteSpace(sample) || string.IsNullOrWhiteSpace(productionSample) ||
                    !sample.StartsWith(productionSample + Environment.NewLine, StringComparison.Ordinal))
                {
                    return false;
                }

                var settingsViewModel = new PrinterSettingsViewModel { TestReceiptPreview = sample };
                try
                {
                    var settingsRecomposed = settingsViewModel.TestReceiptPreviewFirstLine + Environment.NewLine +
                                             settingsViewModel.TestReceiptPreviewRest;
                    return string.Equals(settingsRecomposed, sample, StringComparison.Ordinal);
                }
                finally
                {
                    settingsViewModel.Dispose();
                }
            }

            private static bool VerifyDiscountReceiptAlignment(
                long createdAtMs,
                ReceiptShopInfo shop)
            {
                var mapper = typeof(PosViewModel).GetMethod(
                    "CreatePaymentReceiptLines",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var productionBuilder = typeof(PosWorkflowService).GetMethod(
                    "BuildReceiptPreview",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (mapper == null || productionBuilder == null)
                    return false;

                var scenarios = new[]
                {
                    new
                    {
                        Total = 13456L,
                        Cash = 6000,
                        Card = 7456,
                        Lines = new List<PosCartLine>
                        {
                            new PosCartLine { Barcode = "TEST-CAFFE", Name = "Caffè più qualità - información", Quantity = 2, UnitPrice = 6173, LineTotal = 12346 },
                            new PosCartLine { Barcode = "TEST-PINGUINO", Name = "Confezione città pingüino niño", Quantity = 1, UnitPrice = 2345, LineTotal = 2345 },
                            new PosCartLine { Barcode = DiscountKeys.BuildLinePct("TEST-CAFFE", 10), Name = "Sconto 10%", Quantity = 1, UnitPrice = -1235, LineTotal = -1235 }
                        }
                    },
                    new
                    {
                        Total = 13222L,
                        Cash = 6000,
                        Card = 7222,
                        Lines = new List<PosCartLine>
                        {
                            new PosCartLine { Barcode = "TEST-CAFFE", Name = "Caffè più qualità - información", Quantity = 2, UnitPrice = 6173, LineTotal = 12346 },
                            new PosCartLine { Barcode = "TEST-PINGUINO", Name = "Confezione città pingüino niño", Quantity = 1, UnitPrice = 2345, LineTotal = 2345 },
                            new PosCartLine { Barcode = DiscountKeys.BuildCartPct(10), Name = "Sconto carrello 10%", Quantity = 1, UnitPrice = -1469, LineTotal = -1469 }
                        }
                    }
                };

                foreach (var scenario in scenarios)
                {
                    var mapped = mapper.Invoke(null, new object[] { scenario.Lines }) as
                        IReadOnlyList<PaymentReceiptDraftLine>;
                    if (mapped == null || mapped.Count != scenario.Lines.Count)
                        return false;

                    for (var index = 0; index < mapped.Count; index++)
                    {
                        var source = scenario.Lines[index];
                        var target = mapped[index];
                        if (!string.Equals(source.Barcode, target.Barcode, StringComparison.Ordinal) ||
                            !string.Equals(source.Name, target.Name, StringComparison.Ordinal) ||
                            source.Quantity != target.Quantity ||
                            source.UnitPrice != target.UnitPrice ||
                            source.LineTotal != target.LineTotal)
                        {
                            return false;
                        }
                    }

                    var saleLines = mapped.Select(line => new SaleLine
                    {
                        Barcode = line.Barcode,
                        Name = line.Name,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        LineTotal = line.LineTotal
                    }).ToList();

                    foreach (var use42 in new[] { false, true })
                    {
                        var draft = new PaymentReceiptDraft
                        {
                            SaleCode = "QA-DISCOUNT",
                            CreatedAtMs = createdAtMs,
                            CartLines = mapped,
                            UseReceipt42 = use42,
                            ShopInfo = shop
                        };
                        using (var viewModel = new PaymentViewModel(scenario.Total, draft, openDrawerDefault: false)
                        {
                            CashReceived = scenario.Cash.ToString(CultureInfo.InvariantCulture),
                            CardAmount = scenario.Card.ToString(CultureInfo.InvariantCulture)
                        })
                        {
                            var sale = new Sale
                            {
                                Code = draft.SaleCode,
                                CreatedAt = createdAtMs,
                                Total = scenario.Total,
                                PaidCash = scenario.Cash,
                                PaidCard = scenario.Card,
                                Change = 0
                            };
                            var expected = productionBuilder.Invoke(
                                null,
                                new object[] { new SaleCompleted(sale, saleLines), use42, shop }) as string;
                            var width = use42 ? 42 : 32;
                            if (string.IsNullOrWhiteSpace(expected) ||
                                !string.Equals(viewModel.ReceiptPreviewText, expected, StringComparison.Ordinal) ||
                                expected.IndexOf(PosLocalization.T("receipt.discount"), StringComparison.Ordinal) < 0 ||
                                expected.IndexOf(PosLocalization.T("receipt.totalDiscounts"), StringComparison.Ordinal) < 0 ||
                                expected.IndexOf(PosLocalization.T("common.cash"), StringComparison.Ordinal) < 0 ||
                                expected.IndexOf(PosLocalization.T("common.card"), StringComparison.Ordinal) < 0 ||
                                expected.Replace("\r\n", "\n").Split('\n')
                                    .Any(line => ReceiptTextLayout.VisibleWidth(line) > width))
                            {
                                return false;
                            }
                        }
                    }
                }

                return true;
            }

            private static bool VerifyReceiptLocalizationMatrix(
                long createdAtMs,
                ReceiptShopInfo shop)
            {
                var productionBuilder = typeof(PosWorkflowService).GetMethod(
                    "BuildReceiptPreview",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var sampleBuilder = typeof(PosWorkflowService).GetMethod(
                    "BuildPrinterTestReceipt",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (productionBuilder == null || sampleBuilder == null)
                    return false;

                var originalLanguage = PosLocalization.Current.CurrentLanguage;
                var draftLines = new List<PaymentReceiptDraftLine>
                {
                    new PaymentReceiptDraftLine { Barcode = "TEST-CAFFE", Name = "Caffè più qualità - información", Quantity = 2, UnitPrice = 6173, LineTotal = 12346 },
                    new PaymentReceiptDraftLine { Barcode = "TEST-PINGUINO", Name = "Confezione città pingüino niño", Quantity = 1, UnitPrice = 2345, LineTotal = 2345 }
                };
                var saleLines = draftLines.Select(line => new SaleLine
                {
                    Barcode = line.Barcode,
                    Name = line.Name,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    LineTotal = line.LineTotal
                }).ToList();
                var sale = new Sale
                {
                    Code = "QA-LANGUAGE",
                    CreatedAt = createdAtMs,
                    Total = 14691,
                    PaidCash = 7000,
                    PaidCard = 7691,
                    Change = 0
                };

                try
                {
                    foreach (var use42 in new[] { false, true })
                    {
                        var draft = new PaymentReceiptDraft
                        {
                            SaleCode = sale.Code,
                            CreatedAtMs = createdAtMs,
                            CartLines = draftLines,
                            UseReceipt42 = use42,
                            ShopInfo = shop
                        };
                        using (var viewModel = new PaymentViewModel(14691, draft, openDrawerDefault: false)
                        {
                            CashReceived = "7000",
                            CardAmount = "7691"
                        })
                        {
                            foreach (var language in PosLocalization.SupportedLanguages.Select(option => option.Code))
                            {
                                PosLocalization.Current.SetLanguage(language);
                                var expected = productionBuilder.Invoke(
                                    null,
                                    new object[] { new SaleCompleted(sale, saleLines), use42, shop }) as string;
                                var sample = sampleBuilder.Invoke(
                                    null,
                                    new object[]
                                    {
                                        shop,
                                        use42,
                                        DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs)
                                    }) as string;
                                var width = use42 ? 42 : 32;
                                var previewMatches = string.Equals(
                                    viewModel.ReceiptPreviewText,
                                    expected,
                                    StringComparison.Ordinal);
                                var expectedMaxWidth = string.IsNullOrEmpty(expected)
                                    ? 0
                                    : expected.Replace("\r\n", "\n").Split('\n')
                                        .Max(ReceiptTextLayout.VisibleWidth);
                                var sampleMaxWidth = string.IsNullOrEmpty(sample)
                                    ? 0
                                    : sample.Replace("\r\n", "\n").Split('\n')
                                        .Max(ReceiptTextLayout.VisibleWidth);
                                var cashPresent = !string.IsNullOrEmpty(expected) &&
                                                  expected.IndexOf(PosLocalization.T("common.cash"), StringComparison.Ordinal) >= 0;
                                var cardPresent = !string.IsNullOrEmpty(expected) &&
                                                  expected.IndexOf(PosLocalization.T("common.card"), StringComparison.Ordinal) >= 0;
                                var normalizedSample = (sample ?? string.Empty)
                                    .Replace("\r", string.Empty)
                                    .Replace("\n", string.Empty)
                                    .Replace(" ", string.Empty);
                                var normalizedMarker = PosLocalization.T("printer.testReceiptMarker")
                                    .Replace("\r", string.Empty)
                                    .Replace("\n", string.Empty)
                                    .Replace(" ", string.Empty);
                                var markerPresent = normalizedSample.IndexOf(
                                    normalizedMarker,
                                    StringComparison.Ordinal) >= 0;
                                var missingKey = (expected ?? string.Empty).IndexOf("[missing:", StringComparison.Ordinal) >= 0 ||
                                                 (sample ?? string.Empty).IndexOf("[missing:", StringComparison.Ordinal) >= 0;
                                if (string.IsNullOrWhiteSpace(expected) ||
                                    string.IsNullOrWhiteSpace(sample) ||
                                    !previewMatches ||
                                    !cashPresent ||
                                    !cardPresent ||
                                    !markerPresent ||
                                    missingKey ||
                                    expectedMaxWidth > width ||
                                    sampleMaxWidth > width)
                                {
                                    _receiptAlignmentFailure = string.Format(
                                        CultureInfo.InvariantCulture,
                                        "language={0},width={1},preview={2},cash={3},card={4},marker={5},missing={6},expectedMax={7},sampleMax={8}",
                                        language,
                                        width,
                                        previewMatches,
                                        cashPresent,
                                        cardPresent,
                                        markerPresent,
                                        missingKey,
                                        expectedMaxWidth,
                                        sampleMaxWidth);
                                    return false;
                                }
                            }
                        }
                    }

                    return true;
                }
                finally
                {
                    PosLocalization.Current.SetLanguage(originalLanguage);
                }
            }

            private sealed class ReceiptPaymentMix
            {
                public ReceiptPaymentMix(int cash, int card, int change)
                {
                    Cash = cash;
                    Card = card;
                    Change = change;
                }

                public int Cash { get; }
                public int Card { get; }
                public int Change { get; }
            }

            private static IReadOnlyList<PhysicalPrinterQaJob> BuildPhysicalPrinterQaJobs(
                string printerName)
            {
                const long totalMinor = 14691;
                const long vatMinor = 2345;
                const int syntheticFiscalNumber = 990001;
                var createdAt = new DateTimeOffset(2026, 7, 19, 15, 0, 0, TimeSpan.Zero);
                var createdAtMs = createdAt.ToUnixTimeMilliseconds();
                var shop = new ReceiptShopInfo
                {
                    Name = "QA PRINTER TEST SHOP",
                    Address = "Avenida QA 123",
                    City = "Santiago",
                    Rut = "76.123.456-7",
                    Phone = "+56 2 0000 0000",
                    Footer = "QA ONLY - THANK YOU",
                    BusinessGiro = "QA TEST SALES",
                    LegalRepresentativeRut = "11.111.111-1"
                };

                var fiscal32 = PrefixPhysicalQaWarnings(
                    "QA CASE: FISCAL 32 SYNTHETIC",
                    FiscalBoletaTextRenderer.Render(
                        shop,
                        createdAtMs,
                        syntheticFiscalNumber,
                        totalMinor,
                        vatMinor,
                        32));
                var fiscal42 = PrefixPhysicalQaWarnings(
                    "QA CASE: FISCAL 42 SYNTHETIC",
                    FiscalBoletaTextRenderer.Render(
                        shop,
                        createdAtMs,
                        syntheticFiscalNumber,
                        totalMinor,
                        vatMinor,
                        42));

                var draft = new PaymentReceiptDraft
                {
                    SaleCode = "TEST-NO-SALE",
                    CreatedAtMs = createdAtMs,
                    UseReceipt42 = true,
                    DefaultPrint = false,
                    ShopInfo = shop,
                    NextBoletaNumber = syntheticFiscalNumber,
                    CartLines = new List<PaymentReceiptDraftLine>
                    {
                        new PaymentReceiptDraftLine
                        {
                            Barcode = "QA-COFFEE",
                            Name = "QA café producto",
                            Quantity = 2,
                            UnitPrice = 6173,
                            LineTotal = 12346
                        },
                        new PaymentReceiptDraftLine
                        {
                            Barcode = "QA-PACK",
                            Name = "QA paquete pingüino",
                            Quantity = 1,
                            UnitPrice = 2345,
                            LineTotal = 2345
                        }
                    }
                };
                string receiptText;
                using (var payment = new PaymentViewModel(
                    totalMinor,
                    draft,
                    printFiscalToThermal: null,
                    openDrawerDefault: false))
                {
                    payment.CashReceived = "7000";
                    payment.CardAmount = "7691";
                    receiptText = PrefixPhysicalQaWarnings(
                        "QA CASE: RECEIPT ORIGINAL/REPRINT",
                        payment.ReceiptPreviewText);
                }

                var dailyModel = new DailyTakingsReceiptModel
                {
                    Date = new DateTime(2026, 7, 19),
                    PeriodStart = new DateTime(2026, 7, 19),
                    PeriodEnd = new DateTime(2026, 7, 19),
                    OperatorName = "QA OPERATOR",
                    GeneratedAt = createdAt,
                    SalesCount = 3,
                    GrossSalesAmount = 24691,
                    DiscountsAmount = 1000,
                    TaxAmount = 3942,
                    RefundsAmount = 2000,
                    VoidsAmount = 0,
                    NetAmount = 21691,
                    CashAmount = 14000,
                    CardAmount = 7691,
                    MixedSalesCount = 1,
                    ChangeAmount = 309,
                    OpeningAmount = 50000,
                    ClosingAmount = 63691,
                    ExpectedCashAmount = 63691,
                    DifferenceAmount = 0,
                    PendingSyncCount = 0,
                    RetrySyncCount = 0,
                    BlockedSyncCount = 0
                };
                var daily32 = PrefixPhysicalQaWarnings(
                    "QA CASE: DAILY CLOSE 32",
                    Win7POS.Core.Reports.DailyCloseReceiptTextRenderer.Render(
                        dailyModel,
                        shop,
                        new ReceiptOptions
                        {
                            Width = 32,
                            Currency = "CLP",
                            CultureName = "en-US",
                            Labels = ReceiptLabels.English
                        },
                        DailyCloseReceiptLabels.English));
                var daily42 = PrefixPhysicalQaWarnings(
                    "QA CASE: DAILY CLOSE 42",
                    Win7POS.Core.Reports.DailyCloseReceiptTextRenderer.Render(
                        dailyModel,
                        shop,
                        new ReceiptOptions
                        {
                            Width = 42,
                            Currency = "CLP",
                            CultureName = "en-US",
                            Labels = ReceiptLabels.English
                        },
                        DailyCloseReceiptLabels.English));

                var receiptOptions = CreatePhysicalPrinterQaOptions(
                    printerName,
                    42,
                    useReceiptHeaderStyle: true,
                    saleCodeForBarcode: "TEST-NO-SALE");
                return new List<PhysicalPrinterQaJob>
                {
                    new PhysicalPrinterQaJob(
                        "fiscal-32",
                        fiscal32,
                        CreatePhysicalPrinterQaOptions(printerName, 32, false, string.Empty)),
                    new PhysicalPrinterQaJob(
                        "fiscal-42",
                        fiscal42,
                        CreatePhysicalPrinterQaOptions(printerName, 42, false, string.Empty)),
                    new PhysicalPrinterQaJob("receipt-original", receiptText, receiptOptions),
                    new PhysicalPrinterQaJob("receipt-reprint-identical", receiptText, receiptOptions),
                    new PhysicalPrinterQaJob(
                        "daily-close-32",
                        daily32,
                        CreatePhysicalPrinterQaOptions(printerName, 32, true, string.Empty)),
                    new PhysicalPrinterQaJob(
                        "daily-close-42",
                        daily42,
                        CreatePhysicalPrinterQaOptions(printerName, 42, true, string.Empty))
                };
            }

            private static ReceiptPrintOptions CreatePhysicalPrinterQaOptions(
                string printerName,
                int columns,
                bool useReceiptHeaderStyle,
                string saleCodeForBarcode)
            {
                return new ReceiptPrintOptions
                {
                    PrinterName = printerName,
                    Copies = 1,
                    CharactersPerLine = columns,
                    UseReceiptHeaderStyle = useReceiptHeaderStyle,
                    SaleCodeForBarcode = saleCodeForBarcode ?? string.Empty,
                    CashDrawerCommand = string.Empty
                };
            }

            private static string PrefixPhysicalQaWarnings(string qaCase, string body)
            {
                return string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        "QA - PRINTER TEST",
                        "NON FISCAL",
                        "NO SALE SAVED",
                        "NO DRAWER",
                        qaCase ?? string.Empty,
                        string.Empty,
                        body ?? string.Empty
                    });
            }

            private static bool VerifyPhysicalPrinterQaPayloads()
            {
                var manifestPath = Path.Combine(
                    Path.GetTempPath(),
                    "win7pos-physical-qa-manifest-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    var jobs = BuildPhysicalPrinterQaJobs("QA PHYSICAL PRINTER");
                    ValidatePhysicalPrinterQaJobs(jobs);
                    WritePhysicalPrinterQaManifestAtomically(
                        manifestPath,
                        new[] { "STATUS=FIRST" });
                    WritePhysicalPrinterQaManifestAtomically(
                        manifestPath,
                        new[] { "STATUS=SECOND" });
                    return jobs.Count == 6 &&
                           jobs.All(job => !job.Submitted) &&
                           jobs.All(job => job.Options.Copies == 1) &&
                           jobs.All(job => string.IsNullOrEmpty(job.Options.CashDrawerCommand)) &&
                           string.Equals(
                               File.ReadAllText(manifestPath).Trim(),
                               "STATUS=SECOND",
                               StringComparison.Ordinal) &&
                           !File.Exists(manifestPath + ".pending");
                }
                catch
                {
                    return false;
                }
                finally
                {
                    try { File.Delete(manifestPath); } catch { }
                    try { File.Delete(manifestPath + ".pending"); } catch { }
                }
            }

            private static bool VerifyLogRedactionTestVectors()
            {
                const string sessionSecret = "SessionSecret123456";
                const string refreshSecret = "RefreshSecret123456";
                const string clientSecret = "ClientSecret123456";
                const string apiSecret = "ApiSecret123456"; // gitleaks:allow -- synthetic redaction-test value
                const string passwordSecret = "CorrectHorseBatteryStaple";
                const string credentialSecret = "CredentialSecret123456";
                const string pinSecret = "12345678";
                const string dbSecret = "DatabaseSecret123456";
                const string bearerSecret = "BearerSecret123456";
                const string prefixSecret = "sk-abcdefghijklmnopqrstuvwxyz";
                const string jwtSecret = "eyJheader12345.payload12345.signature12345";
                const string privateKeyBody = "PRIVATEKEYBODY123456789"; // gitleaks:allow -- synthetic redaction-test value
                const string truncatedPrivateKeyBody = "TRUNCATEDPRIVATEKEYBODY987654321"; // gitleaks:allow -- synthetic redaction-test value
                var input = string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        "session_token=" + sessionSecret,
                        "refresh-token=" + refreshSecret,
                        "{\"client_secret\":\"" + clientSecret + "\",\"api_key\":\"" + apiSecret + "\"}",
                        "password=" + passwordSecret,
                        "credential:" + credentialSecret,
                        "pin=" + pinSecret,
                        "db_password=" + dbSecret,
                        "Authorization: Bearer " + bearerSecret,
                        prefixSecret,
                        jwtSecret,
                        "-----BEGIN PRIVATE KEY-----", // gitleaks:allow -- synthetic redaction-test envelope
                        privateKeyBody,
                        "-----END PRIVATE KEY-----",
                        "-----BEGIN RSA PRIVATE KEY-----", // gitleaks:allow -- synthetic truncated redaction-test envelope
                        truncatedPrivateKeyBody
                    });
                var outputs = new[]
                {
                    InvokePrivateRedactor(
                        typeof(Win7POS.Wpf.Infrastructure.FileLogger),
                        "Sanitize",
                        input),
                    InvokePrivateRedactor(
                        typeof(Win7POS.Wpf.Infrastructure.StartupTrace),
                        "Sanitize",
                        input),
                    InvokePrivateRedactor(
                        typeof(DbInitializer),
                        "SanitizeLogMessage",
                        input)
                };
                var secrets = new[]
                {
                    sessionSecret,
                    refreshSecret,
                    clientSecret,
                    apiSecret,
                    passwordSecret,
                    credentialSecret,
                    pinSecret,
                    dbSecret,
                    bearerSecret,
                    prefixSecret,
                    jwtSecret,
                    privateKeyBody,
                    "-----END PRIVATE KEY-----",
                    truncatedPrivateKeyBody,
                    "-----BEGIN RSA PRIVATE KEY-----"
                };
                return outputs.All(output =>
                    !string.IsNullOrWhiteSpace(output) &&
                    output.IndexOf("[redacted]", StringComparison.Ordinal) >= 0 &&
                    output.IndexOf("[private-key-redacted]", StringComparison.Ordinal) >= 0 &&
                    secrets.All(secret => output.IndexOf(secret, StringComparison.Ordinal) < 0));
            }

            private static string InvokePrivateRedactor(Type type, string methodName, string input)
            {
                var method = type.GetMethod(
                    methodName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (method == null)
                    throw new MissingMethodException(type.FullName, methodName);
                return method.Invoke(null, new object[] { input }) as string ?? string.Empty;
            }

            private static void ValidatePhysicalPrinterQaJobs(
                IReadOnlyList<PhysicalPrinterQaJob> jobs)
            {
                if (jobs == null || jobs.Count != 6)
                    throw new InvalidOperationException("Physical printer QA must contain exactly six jobs.");

                foreach (var job in jobs)
                {
                    if (job == null ||
                        job.Options == null ||
                        job.Options.Copies != 1 ||
                        !string.IsNullOrEmpty(job.Options.CashDrawerCommand) ||
                        (job.Options.CharactersPerLine != 32 && job.Options.CharactersPerLine != 42))
                    {
                        throw new InvalidOperationException(
                            "Every physical printer QA job must be one-copy, drawer-free and 32/42 columns.");
                    }

                    var lines = SplitReceiptLines(job.Text);
                    if (lines.Length < 5 ||
                        !string.Equals(lines[0], "QA - PRINTER TEST", StringComparison.Ordinal) ||
                        !string.Equals(lines[1], "NON FISCAL", StringComparison.Ordinal) ||
                        !string.Equals(lines[2], "NO SALE SAVED", StringComparison.Ordinal) ||
                        !string.Equals(lines[3], "NO DRAWER", StringComparison.Ordinal) ||
                        lines.Any(line => ReceiptTextLayout.VisibleWidth(line) > job.Options.CharactersPerLine))
                    {
                        throw new InvalidOperationException(
                            "Physical printer QA warning prefix or width bound is invalid.");
                    }
                }

                if (SplitReceiptLines(jobs[0].Text).Count(line =>
                        string.Equals(line, FiscalBoletaTextRenderer.SiiStampMarker, StringComparison.Ordinal)) != 1 ||
                    SplitReceiptLines(jobs[1].Text).Count(line =>
                        string.Equals(line, FiscalBoletaTextRenderer.SiiStampMarker, StringComparison.Ordinal)) != 1)
                {
                    throw new InvalidOperationException("Each fiscal QA job must contain one standalone SII marker.");
                }

                if (!string.Equals(jobs[2].Text, jobs[3].Text, StringComparison.Ordinal) ||
                    !string.Equals(jobs[2].TextSha256, jobs[3].TextSha256, StringComparison.Ordinal) ||
                    !string.Equals(jobs[2].RequestSha256, jobs[3].RequestSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Receipt original and reprint QA requests must be byte-for-byte identical.");
                }
            }

            private static string[] SplitReceiptLines(string text)
            {
                return (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            }

            private static void WritePhysicalPrinterQaManifest(
                string manifestPath,
                string status,
                string detail,
                DateTimeOffset runUtc,
                InstalledPrinterInfo queue,
                IReadOnlyList<PhysicalPrinterQaJob> jobs,
                int submitted,
                string dataDirectory)
            {
                var lines = new List<string>
                {
                    "STATUS=" + ManifestValue(status),
                    "DETAIL=" + ManifestValue(detail),
                    "RUN_UTC=" + runUtc.ToString("O", CultureInfo.InvariantCulture),
                    "PRINTER_NAME=" + ManifestValue(queue?.Name),
                    "PRINTER_DRIVER=" + ManifestValue(queue?.DriverName),
                    "PRINTER_PORT=" + ManifestValue(queue?.PortName),
                    "PRINTER_OUTPUT_KIND=" + (queue?.OutputKind.ToString() ?? "missing"),
                    "PRINTER_INVENTORY_FRESH=" + BoolManifest(queue?.IsInventoryFresh == true),
                    "PRINTER_AVAILABLE=" + BoolManifest(queue?.IsAvailable == true),
                    "PRINTER_PHYSICAL=" + BoolManifest(queue?.IsPhysical == true),
                    "EXPECTED_JOBS=6",
                    "SUBMITTED_JOBS=" + submitted.ToString(CultureInfo.InvariantCulture),
                    "COPIES_PER_JOB=1",
                    "DRAWER_CALLS=0",
                    "FISCAL_NUMBER_SOURCE=SYNTHETIC_NOT_RESERVED",
                    "FISCAL_NUMBER=990001",
                    "HASH_ENCODING=SHA256_UTF8_NO_BOM",
                    "REQUEST_HASH_SCHEMA=printer,copies,columns,header,barcode,drawer,text_sha256 joined as key=value with LF",
                    "DATABASE_ARTIFACTS=" + (PhysicalQaDatabaseArtifactsPresent(dataDirectory) ? "PRESENT" : "ABSENT"),
                    "RECEIPT_3_4_TEXT_IDENTICAL=" + BoolManifest(string.Equals(
                        jobs[2].TextSha256,
                        jobs[3].TextSha256,
                        StringComparison.Ordinal)),
                    "RECEIPT_3_4_REQUEST_IDENTICAL=" + BoolManifest(string.Equals(
                        jobs[2].RequestSha256,
                        jobs[3].RequestSha256,
                        StringComparison.Ordinal)),
                    "VISUAL_CONFIRMATION=REQUIRED"
                };

                for (var index = 0; index < jobs.Count; index++)
                {
                    var job = jobs[index];
                    var prefix = "JOB_" + (index + 1).ToString(CultureInfo.InvariantCulture) + "_";
                    lines.Add(prefix + "ROLE=" + ManifestValue(job.Role));
                    lines.Add(prefix + "COLUMNS=" + job.Options.CharactersPerLine.ToString(CultureInfo.InvariantCulture));
                    lines.Add(prefix + "COPIES=" + job.Options.Copies.ToString(CultureInfo.InvariantCulture));
                    lines.Add(prefix + "USE_RECEIPT_HEADER_STYLE=" + BoolManifest(job.Options.UseReceiptHeaderStyle));
                    lines.Add(prefix + "SALE_CODE_FOR_BARCODE=" + ManifestValue(job.Options.SaleCodeForBarcode));
                    lines.Add(prefix + "DRAWER_COMMAND_EMPTY=" + BoolManifest(string.IsNullOrEmpty(job.Options.CashDrawerCommand)));
                    lines.Add(prefix + "TEXT_SHA256=" + job.TextSha256);
                    lines.Add(prefix + "REQUEST_SHA256=" + job.RequestSha256);
                    lines.Add(prefix + "SUBMITTED=" + BoolManifest(job.Submitted));
                }

                WritePhysicalPrinterQaManifestAtomically(manifestPath, lines);
            }

            private static void WritePhysicalPrinterQaManifestAtomically(
                string manifestPath,
                IReadOnlyList<string> lines)
            {
                var pendingPath = manifestPath + ".pending";
                using (var stream = new FileStream(
                    pendingPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    foreach (var line in lines ?? new string[0])
                        writer.WriteLine(line ?? string.Empty);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(manifestPath))
                    File.Replace(pendingPath, manifestPath, null);
                else
                    File.Move(pendingPath, manifestPath);
            }

            private static void EnsureNoPhysicalQaDatabaseArtifacts(string dataDirectory)
            {
                if (PhysicalQaDatabaseArtifactsPresent(dataDirectory))
                    throw new InvalidOperationException("Physical printer QA created an unexpected POS database artifact.");
            }

            private static bool PhysicalQaDatabaseArtifactsPresent(string dataDirectory)
            {
                return Directory.EnumerateFiles(
                        dataDirectory,
                        "pos.db*",
                        SearchOption.AllDirectories)
                    .Any();
            }

            private static string BoolManifest(bool value) => value ? "true" : "false";

            private static string ManifestValue(string value)
            {
                return (value ?? string.Empty)
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Trim();
            }

            private static string Sha256Hex(string value)
            {
                using (var sha256 = SHA256.Create())
                {
                    var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                    return BitConverter.ToString(hash).Replace("-", string.Empty);
                }
            }

            private sealed class PhysicalPrinterQaJob
            {
                public PhysicalPrinterQaJob(
                    string role,
                    string text,
                    ReceiptPrintOptions options)
                {
                    Role = role ?? string.Empty;
                    Text = text ?? string.Empty;
                    Options = options ?? throw new ArgumentNullException(nameof(options));
                    TextSha256 = Sha256Hex(Text);
                    var canonicalRequest = string.Join(
                        "\n",
                        new[]
                        {
                            "printer=" + (Options.PrinterName ?? string.Empty),
                            "copies=" + Options.Copies.ToString(CultureInfo.InvariantCulture),
                            "columns=" + Options.CharactersPerLine.ToString(CultureInfo.InvariantCulture),
                            "header=" + BoolManifest(Options.UseReceiptHeaderStyle),
                            "barcode=" + (Options.SaleCodeForBarcode ?? string.Empty),
                            "drawer=" + (Options.CashDrawerCommand ?? string.Empty),
                            "text_sha256=" + TextSha256
                        });
                    RequestSha256 = Sha256Hex(canonicalRequest);
                }

                public string Role { get; }
                public string Text { get; }
                public ReceiptPrintOptions Options { get; }
                public string TextSha256 { get; }
                public string RequestSha256 { get; }
                public bool Submitted { get; set; }
            }

            private static string BuildQaPrinterTestReceipt()
            {
                var build = typeof(PosWorkflowService).GetMethod(
                    "BuildPrinterTestReceipt",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (build == null)
                    return "QA fictitious receipt preview";

                var shop = new ReceiptShopInfo
                {
                    Name = "QA Café",
                    Address = "Calle Información 123",
                    City = "Santiago",
                    Rut = "76.123.456-7",
                    Phone = "+56 2 1234 5678",
                    Footer = "Grazie - Gracias"
                };
                return build.Invoke(
                           null,
                           new object[]
                           {
                               shop,
                               true,
                               new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero)
                           }) as string ??
                       "QA fictitious receipt preview";
            }

            private static ProductEditDialog CreateProductEditDialog()
            {
                var vm = new ProductEditViewModel(ProductEditMode.New, null, ProductsWorkflowService.CreateDefault());
                return new ProductEditDialog(vm);
            }

            private static SyncCenterDialog CreateSyncCenterDialog()
            {
                var factory = new SqliteConnectionFactory(PosDbOptions.Default());
                return new SyncCenterDialog(
                    factory,
                    (_, __, ___) => Task.FromResult<CatalogSyncRunResult>(null),
                    _ => Task.FromResult(false));
            }

            private static CustomerDisplaySettingsDialog CreateCustomerDisplaySettingsDialog()
            {
                var monitors = new FakeDisplayTopologyProvider().GetMonitors();
                return new CustomerDisplaySettingsDialog(
                    new CustomerDisplaySettingsViewModel(CustomerDisplaySettings.CreateDefault(2), monitors),
                    null, null, null, null);
            }

            private static Task<bool> OpenThenCloseCustomerDisplayAsync()
            {
                var monitor = new FakeDisplayTopologyProvider().GetMonitors()[1];
                var settings = CustomerDisplaySettings.CreateDefault(2);
                var snapshot = CustomerDisplayProjection.Cart(
                    new[]
                    {
                        new CustomerDisplayProjectionLine
                        {
                            StableKey = "qa-line",
                            Name = "QA item",
                            Barcode = "QA0001",
                            Quantity = 2,
                            UnitPrice = 1000,
                            LineTotal = 2000,
                            LineKind = CustomerDisplayLineKind.Item
                        }
                    },
                    2000, 2000, "QA Shop", "qa-line", false, DateTimeOffset.UtcNow);
                var window = new CustomerDisplayWindow();
                window.PrepareDisplay(snapshot, settings, monitor);
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                var placementPass = handle != IntPtr.Zero &&
                                    GetWindowRect(handle, out var bounds) &&
                                    bounds.Left == monitor.BoundsLeft &&
                                    bounds.Top == monitor.BoundsTop &&
                                    bounds.Right - bounds.Left == monitor.Width &&
                                    bounds.Bottom - bounds.Top == monitor.Height;
                var passed = !window.ShowActivated && !window.ShowInTaskbar && !window.Focusable &&
                             placementPass;
                window.Close();
                return Task.FromResult(passed);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeRect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

            private sealed class FakeDisplayTopologyProvider : IDisplayTopologyProvider
            {
                public IReadOnlyList<DisplayMonitorInfo> GetMonitors()
                {
                    return new List<DisplayMonitorInfo>
                    {
                        new DisplayMonitorInfo
                        {
                            DeviceName = "QA-CASHIER", IsPrimary = true,
                            BoundsLeft = 0, BoundsTop = 0, Width = 1024, Height = 768,
                            WorkAreaLeft = 0, WorkAreaTop = 0, WorkingWidth = 1024, WorkingHeight = 728,
                            BitsPerPixel = 32
                        },
                        new DisplayMonitorInfo
                        {
                            DeviceName = "QA-CUSTOMER", IsPrimary = false,
                            BoundsLeft = -800, BoundsTop = 0, Width = 800, Height = 600,
                            WorkAreaLeft = -800, WorkAreaTop = 0, WorkingWidth = 800, WorkingHeight = 600,
                            BitsPerPixel = 32
                        }
                    }.AsReadOnly();
                }
            }

            private static bool IsStrictlyIncreasing(IReadOnlyList<long> values)
            {
                if (values.Count < 4) return false;
                for (var i = 1; i < values.Count; i++)
                {
                    if (values[i] <= values[i - 1]) return false;
                }
                return true;
            }

            private static int GetLanguageChangedHandlerCount()
            {
                var eventField = typeof(Win7POS.Wpf.Localization.PosLocalization).GetField(
                    "LanguageChanged",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handlers = eventField?.GetValue(Win7POS.Wpf.Localization.PosLocalization.Current) as Delegate;
                return handlers?.GetInvocationList().Length ?? 0;
            }

            private sealed class LifecycleSample
            {
                public int Cycle { get; private set; }
                public int HandleCount { get; private set; }
                public long PrivateBytes { get; private set; }

                public static LifecycleSample Capture(int cycle)
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        process.Refresh();
                        return new LifecycleSample
                        {
                            Cycle = cycle,
                            HandleCount = process.HandleCount,
                            PrivateBytes = process.PrivateMemorySize64
                        };
                    }
                }
            }

            private sealed class LifecycleWindowReference
            {
                public LifecycleWindowReference(
                    string windowType,
                    WeakReference window,
                    WeakReference dataContext)
                {
                    WindowType = windowType;
                    Window = window;
                    DataContext = dataContext;
                }

                public string WindowType { get; }
                public WeakReference Window { get; }
                public WeakReference DataContext { get; }
            }
        }

        private static class QaFixture
        {
            public static async Task VerifyOfflineSalesSandboxSafetyAsync()
            {
                var options = PosDbOptions.Default();
                if (!File.Exists(options.DbPath))
                {
                    throw new InvalidOperationException(
                        "Offline sales sandbox database is missing.");
                }

                var factory = new SqliteConnectionFactory(options);
                var printer = await new PosWorkflowService()
                    .GetPrinterSettingsAsync()
                    .ConfigureAwait(false);
                if (printer.ReceiptEnabled || printer.AutoPrint ||
                    printer.AllowWindowsDefault || printer.AllowVirtualPrinters ||
                    printer.CashDrawerEnabled || printer.CashDrawerOpenOnCashSale ||
                    !string.IsNullOrWhiteSpace(printer.PrinterName) ||
                    !string.IsNullOrWhiteSpace(printer.CashDrawerPrinterName) ||
                    !string.Equals(printer.CashDrawerMode, "disabled", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Offline sales sandbox resume refused because hardware output is enabled.");
                }

                var officialShop = await new ShopOfficialSnapshotRepository(factory)
                    .GetAsync()
                    .ConfigureAwait(false);
                string rawFiscalLock;
                using (var conn = factory.Open())
                {
                    rawFiscalLock = await conn.ExecuteScalarAsync<string>(
                        "SELECT value FROM app_settings WHERE key = 'pos.official_shop.fiscal_locked';")
                        .ConfigureAwait(false);
                }
                if (!string.Equals(rawFiscalLock?.Trim(), "1", StringComparison.Ordinal) ||
                    !officialShop.FiscalIdentityLockedByPlatform ||
                    !string.Equals(officialShop.ShopId, QaShopId, StringComparison.Ordinal) ||
                    !string.Equals(officialShop.ShopCode, QaShopCode, StringComparison.Ordinal) ||
                    !string.Equals(officialShop.ShopName, QaOfflineShopName, StringComparison.Ordinal) ||
                    !string.Equals(officialShop.Source, "qa_harness", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Offline sales sandbox resume refused because its synthetic shop identity or fiscal lock is invalid.");
                }
            }

            public static async Task SeedOfflineSalesSandboxAsync()
            {
                var options = PosDbOptions.Default();
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);
                await SeedOfflineSalesOperatorAsync(factory).ConfigureAwait(false);
                await SeedCatalogAsync(factory).ConfigureAwait(false);
                await SeedShopAndSaleSafetyAsync(
                    factory,
                    QaOfflineShopName,
                    "TEST / NON FISCAL - OFFLINE DEVELOPMENT").ConfigureAwait(false);
                await new PosWorkflowService().SetPrinterSettingsAsync(new PosPrinterSettings
                {
                    ReceiptEnabled = false,
                    AutoPrint = false,
                    AllowWindowsDefault = false,
                    AllowVirtualPrinters = false,
                    CashDrawerEnabled = false,
                    CashDrawerMode = "disabled",
                    CashDrawerOpenOnCashSale = false
                }).ConfigureAwait(false);

                using (var conn = factory.Open())
                {
                    var products = await conn.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM products WHERE COALESCE(is_active, 1) = 1;").ConfigureAwait(false);
                    var sales = await conn.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales;").ConfigureAwait(false);
                    var salesOutbox = await conn.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM sales_sync_outbox;").ConfigureAwait(false);
                    var catalogOutbox = await conn.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM catalog_import_outbox;").ConfigureAwait(false);
                    var remoteUsers = await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(1) FROM users
                          WHERE is_active = 1
                            AND remote_staff_id = @staffId
                            AND UPPER(TRIM(remote_shop_code)) = UPPER(@shopCode);",
                        new { staffId = QaOfflineStaffId, shopCode = QaShopCode }).ConfigureAwait(false);
                    var localRecoveryUsers = await conn.ExecuteScalarAsync<long>(@"SELECT COUNT(1) FROM users
                          WHERE is_active = 1
                            AND remote_staff_id IS NULL
                            AND remote_staff_code IS NULL
                            AND remote_shop_id IS NULL
                            AND remote_shop_code IS NULL;").ConfigureAwait(false);
                    if (products != 48 || sales != 0 || salesOutbox != 0 || catalogOutbox != 0 ||
                        remoteUsers != 1 || localRecoveryUsers != 0)
                    {
                        throw new InvalidOperationException(
                            "Offline sales sandbox seed postconditions failed.");
                    }
                }

                await VerifyOfflineSalesSandboxSafetyAsync().ConfigureAwait(false);

                if (!await PosCatalogPullService.IsCatalogSaleSafeAsync(factory).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Offline sales sandbox catalog is not sale-safe.");
                }
            }

            public static async Task SeedAsync()
            {
                var options = PosDbOptions.Default();
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);
                await SeedUsersAsync(factory).ConfigureAwait(false);
                await SeedCatalogAsync(factory).ConfigureAwait(false);
                await SeedShopAndSaleSafetyAsync(factory).ConfigureAwait(false);
                await SeedSalesAsync(factory).ConfigureAwait(false);
                await SeedHeldCartsAsync(factory).ConfigureAwait(false);
                await SeedSyncStatesAsync(factory).ConfigureAwait(false);
            }

            public static void SeedTrustedDeviceSession(string shopName = "QA Synthetic Shop")
            {
                var now = DateTimeOffset.UtcNow;
                new PosTrustedDeviceStore().SaveFirstLogin(new PosFirstLoginResponse
                {
                    Ok = true,
                    ServerTime = now.ToString("O", CultureInfo.InvariantCulture),
                    TrustedDeviceToken = Guid.NewGuid().ToString("N"),
                    Device = new PosTrustedDeviceResponse
                    {
                        ShopDeviceId = "qa-device-local",
                        Status = "active",
                        Trusted = true
                    },
                    Session = new PosSessionResponse
                    {
                        ExpiresAt = now.AddSeconds(PosOnlineContract.OfflineAuthorizationMaxAgeSeconds)
                            .ToString("O", CultureInfo.InvariantCulture),
                        HeartbeatAfterSeconds = 300,
                        PosSessionId = "qa-session-" + Guid.NewGuid().ToString("N"),
                        SessionToken = Guid.NewGuid().ToString("N")
                    },
                    Shop = new PosShopResponse
                    {
                        ShopCode = QaShopCode,
                        ShopId = QaShopId,
                        ShopName = shopName,
                        ShopStatus = "active",
                        Source = "qa_harness"
                    },
                    Staff = new PosStaffResponse
                    {
                        CredentialVersion = 1,
                        DisplayName = "QA Administrator",
                        RoleKey = "admin",
                        StaffCode = QaOfflineStaffCode,
                        StaffId = QaOfflineStaffId
                    }
                });
            }

            public static void VerifyTrustedDeviceSession(string expectedShopName)
            {
                PosTrustedDeviceSession session;
                if (!new PosTrustedDeviceStore().TryRead(out session) ||
                    session == null ||
                    !string.Equals(session.ShopId, QaShopId, StringComparison.Ordinal) ||
                    !string.Equals(session.ShopCode, QaShopCode, StringComparison.Ordinal) ||
                    !string.Equals(session.ShopName, expectedShopName, StringComparison.Ordinal) ||
                    !PosOfflineAuthorizationLeasePolicy.Evaluate(session, DateTimeOffset.UtcNow).Allowed)
                {
                    throw new InvalidOperationException(
                        "Offline sales sandbox trusted session verification failed.");
                }
            }

            private static async Task SeedUsersAsync(SqliteConnectionFactory factory)
            {
                var roles = new RoleRepository(factory);
                var users = new UserRepository(factory);
                await EnsureUserAsync(users, await roles.GetByCodeAsync("admin").ConfigureAwait(false),
                    "qa-admin", "QA Administrator", "2468", 100).ConfigureAwait(false);
                await EnsureUserAsync(users, await roles.GetByCodeAsync("manager").ConfigureAwait(false),
                    "qa-manager", "QA Manager", "1357", 35).ConfigureAwait(false);
                await EnsureUserAsync(users, await roles.GetByCodeAsync("cashier").ConfigureAwait(false),
                    "qa-cashier", "QA Cashier", "8642", 0).ConfigureAwait(false);
            }

            private static Task<int> SeedOfflineSalesOperatorAsync(SqliteConnectionFactory factory)
            {
                return new UserRepository(factory).UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
                {
                    Credential = QaOfflineCredential,
                    CredentialVersion = 1,
                    DisplayName = "QA Administrator",
                    RemoteRoleKey = "admin",
                    RemoteShopId = QaShopId,
                    RemoteStaffId = QaOfflineStaffId,
                    ShopCode = QaShopCode,
                    StaffCode = QaOfflineStaffCode
                });
            }

            private static async Task EnsureUserAsync(
                UserRepository users,
                UserRole role,
                string username,
                string displayName,
                string pin,
                int maxDiscount)
            {
                if (role == null) throw new InvalidOperationException("QA role is missing: " + username);
                if (await users.GetByUsernameAsync(username).ConfigureAwait(false) != null) return;
                var salt = PinHelper.GenerateSalt();
                await users.CreateAsync(
                    username,
                    displayName,
                    PinHelper.HashPin(pin, salt),
                    salt,
                    role.Id,
                    maxDiscount,
                    requirePinChange: false).ConfigureAwait(false);
            }

            private static async Task SeedCatalogAsync(SqliteConnectionFactory factory)
            {
                var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var categories = new CategoryRepository(factory);
                var suppliers = new SupplierRepository(factory);
                for (var i = 1; i <= 6; i++)
                {
                    await categories.UpsertRemoteAsync("qa-category-" + i, "QA Category " + i, now).ConfigureAwait(false);
                    await suppliers.UpsertRemoteAsync("qa-supplier-" + i, "QA Supplier " + i, now).ConfigureAwait(false);
                }

                var categoryRows = await categories.ListAllAsync().ConfigureAwait(false);
                var supplierRows = await suppliers.ListAllAsync().ConfigureAwait(false);
                var products = new ProductRepository(factory);
                for (var i = 1; i <= 48; i++)
                {
                    var barcode = "QA" + i.ToString("D6", CultureInfo.InvariantCulture);
                    var price = 500 + i * 25;
                    await products.UpsertProductAndMetaInTransactionAsync(
                        new Product { Barcode = barcode, Name = "QA Product " + i, UnitPrice = price },
                        "QA-ART-" + i.ToString("D4", CultureInfo.InvariantCulture),
                        i % 5 == 0 ? "QA long localized secondary product name " + i : string.Empty,
                        Math.Max(1, price - 120),
                        supplierRows[(i - 1) % supplierRows.Count].Id,
                        supplierRows[(i - 1) % supplierRows.Count].Name,
                        categoryRows[(i - 1) % categoryRows.Count].Id,
                        categoryRows[(i - 1) % categoryRows.Count].Name,
                        20 + i).ConfigureAwait(false);
                    if (i <= 12)
                    {
                        await products.InsertPriceHistoryAsync(barcode, "retail", price - 25, "QA_FIXTURE").ConfigureAwait(false);
                        await products.InsertPriceHistoryAsync(barcode, "retail", price, "QA_FIXTURE").ConfigureAwait(false);
                    }
                }
            }

            private static async Task SeedShopAndSaleSafetyAsync(
                SqliteConnectionFactory factory,
                string shopName = "QA Synthetic Shop",
                string footer = "")
            {
                await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
                {
                    ShopId = QaShopId,
                    ShopCode = QaShopCode,
                    ShopName = shopName,
                    Footer = footer,
                    Source = "qa_harness"
                }).ConfigureAwait(false);
                var state = new CatalogShopStateRepository(factory);
                var binding = await state.EnsureAndLoadCursorAsync(QaShopId, QaShopCode).ConfigureAwait(false);
                using (var conn = factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.CompletenessStatusKey, "Verified").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.CompletenessCodeKey, "qa_verified").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.RepairRequiredKey, "0").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessShopIdKey, QaShopId).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessShopCodeKey, QaShopCode).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessEvaluatedAtKey, now).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessVerifiedAtKey, now).ConfigureAwait(false);
                    tx.Commit();
                }
                await state.StoreSaleSafeAsync(
                    QaShopId,
                    QaShopCode,
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    binding.Epoch).ConfigureAwait(false);
            }

            private static async Task SeedSalesAsync(SqliteConnectionFactory factory)
            {
                using (var conn = factory.Open())
                {
                    if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;").ConfigureAwait(false) > 0) return;
                }

                var productRepo = new ProductRepository(factory);
                var products = await productRepo.ListAllAsync().ConfigureAwait(false);
                var sales = new SaleRepository(factory);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var saleIds = new List<long>();
                for (var i = 0; i < 4; i++)
                {
                    var product = products[i];
                    var total = product.UnitPrice * (i + 1);
                    saleIds.Add(await sales.InsertSaleAsync(
                        new Sale
                        {
                            Code = "QA-SALE-" + (i + 1).ToString("D3", CultureInfo.InvariantCulture),
                            CreatedAt = now - (i * 60000),
                            Kind = (int)SaleKind.Sale,
                            Total = total,
                            PaidCash = i % 2 == 0 ? total : 0,
                            PaidCard = i % 2 == 0 ? 0 : total,
                            Change = 0
                        },
                        new[]
                        {
                            new SaleLine
                            {
                                Barcode = product.Barcode,
                                Name = product.Name,
                                ProductId = product.Id,
                                Quantity = i + 1,
                                UnitPrice = product.UnitPrice
                            }
                        }).ConfigureAwait(false));
                }

                var originalLines = await sales.GetLinesBySaleIdAsync(saleIds[0]).ConfigureAwait(false);
                var source = originalLines.Single();
                await sales.InsertSaleAsync(
                    new Sale
                    {
                        Code = "QA-REFUND-001",
                        CreatedAt = now + 1000,
                        Kind = (int)SaleKind.Refund,
                        RelatedSaleId = saleIds[0],
                        Reason = "QA partial refund",
                        Total = -source.UnitPrice,
                        PaidCash = -source.UnitPrice,
                        PaidCard = 0,
                        Change = 0
                    },
                    new[]
                    {
                        new SaleLine
                        {
                            Barcode = source.Barcode,
                            Name = source.Name,
                            ProductId = source.ProductId,
                            Quantity = 1,
                            UnitPrice = source.UnitPrice,
                            RelatedOriginalLineId = source.Id
                        }
                    }).ConfigureAwait(false);

                var voidSourceProduct = products[4];
                var voidSourceId = await sales.InsertSaleAsync(
                    new Sale
                    {
                        Code = "QA-SALE-VOID-SOURCE",
                        CreatedAt = now + 2000,
                        Kind = (int)SaleKind.Sale,
                        Total = voidSourceProduct.UnitPrice,
                        PaidCash = 0,
                        PaidCard = voidSourceProduct.UnitPrice,
                        Change = 0
                    },
                    new[]
                    {
                        new SaleLine
                        {
                            Barcode = voidSourceProduct.Barcode,
                            Name = voidSourceProduct.Name,
                            ProductId = voidSourceProduct.Id,
                            Quantity = 1,
                            UnitPrice = voidSourceProduct.UnitPrice
                        }
                    }).ConfigureAwait(false);
                var voidSourceLine = (await sales.GetLinesBySaleIdAsync(voidSourceId).ConfigureAwait(false)).Single();
                await sales.InsertSaleAsync(
                    new Sale
                    {
                        Code = "QA-VOID-001",
                        CreatedAt = now + 3000,
                        Kind = (int)SaleKind.Void,
                        RelatedSaleId = voidSourceId,
                        Reason = "QA full void",
                        Total = -voidSourceLine.UnitPrice,
                        PaidCash = 0,
                        PaidCard = -voidSourceLine.UnitPrice,
                        Change = 0
                    },
                    new[]
                    {
                        new SaleLine
                        {
                            Barcode = voidSourceLine.Barcode,
                            Name = voidSourceLine.Name,
                            ProductId = voidSourceLine.ProductId,
                            Quantity = 1,
                            UnitPrice = voidSourceLine.UnitPrice,
                            RelatedOriginalLineId = voidSourceLine.Id
                        }
                    }).ConfigureAwait(false);
            }

            private static async Task SeedHeldCartsAsync(SqliteConnectionFactory factory)
            {
                var held = new HeldCartRepository(factory);
                if ((await held.ListHoldsAsync().ConfigureAwait(false)).Count > 0) return;
                await held.CreateHoldAsync(
                    "QA-HOLD-001",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    2600,
                    new[]
                    {
                        new HeldCartLineRow { Barcode = "QA000001", Name = "QA Product 1", UnitPrice = 525, Qty = 2 },
                        new HeldCartLineRow { Barcode = "QA000002", Name = "QA Product 2", UnitPrice = 550, Qty = 1 }
                    }).ConfigureAwait(false);
            }

            private static async Task SeedSyncStatesAsync(SqliteConnectionFactory factory)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using (var conn = factory.Open())
                using (var tx = conn.BeginTransaction())
                {
                    var outboxIds = (await conn.QueryAsync<long>(
                        "SELECT id FROM sales_sync_outbox ORDER BY id LIMIT 4;", transaction: tx)
                        .ConfigureAwait(false)).ToList();
                    var states = new[] { "pending", "retry", "failed_blocked", "in_progress" };
                    for (var i = 0; i < outboxIds.Count && i < states.Length; i++)
                    {
                        await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status=@status,
    attempt_count=@attempt,
    next_retry_at=@nextRetry,
    last_attempt_at=CASE WHEN @status='in_progress' THEN @now ELSE NULL END,
    last_error_code=CASE WHEN @status IN ('retry','failed_blocked') THEN 'qa_synthetic_error' ELSE NULL END,
    updated_at=@now
WHERE id=@id;",
                            new
                            {
                                id = outboxIds[i],
                                status = states[i],
                                attempt = i,
                                nextRetry = states[i] == "retry" ? now + 60000 : 0,
                                now
                            }, tx).ConfigureAwait(false);
                    }

                    for (var i = 0; i < states.Length; i++)
                    {
                        await conn.ExecuteAsync(@"
INSERT INTO catalog_import_outbox(
  client_import_id,idempotency_key,schema_version,operation_type,
  origin_shop_id,origin_shop_code,source,payload_json,payload_hash,
  status,attempt_count,next_retry_at,last_attempt_at,last_error_code,
  created_at,updated_at)
VALUES(
  @clientId,@key,'pos-catalog-import-v1','catalog_import',
  @shopId,@shopCode,'qa_harness','{}',@hash,
  @status,@attempt,@nextRetry,@lastAttempt,@errorCode,@now,@now);",
                            new
                            {
                                clientId = "qa-import-" + i,
                                key = "qa-import-key-" + i,
                                shopId = QaShopId,
                                shopCode = QaShopCode,
                                hash = new string('0', 64),
                                status = states[i],
                                attempt = i,
                                nextRetry = states[i] == "retry" ? now + 60000 : 0,
                                lastAttempt = states[i] == "in_progress" ? (long?)now : null,
                                errorCode = states[i] == "retry" || states[i] == "failed_blocked" ? "qa_synthetic_error" : null,
                                now
                            }, tx).ConfigureAwait(false);
                    }

                    var isoNow = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.CompletenessStatusKey, "Verified").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.CompletenessCodeKey, "qa_verified").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.RepairRequiredKey, "0").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessShopIdKey, QaShopId).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessShopCodeKey, QaShopCode).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessEvaluatedAtKey, isoNow).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessVerifiedAtKey, isoNow).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessActiveProductsKey, "48").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessActiveCategoriesKey, "6").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.ExactnessActiveSuppliersKey, "6").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.LastSyncModeKey, "incremental").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, CatalogShopStateRepository.LastSyncAtKey, isoNow).ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, "pos.catalog.bootstrap_status", "complete").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, "pos.catalog.last_has_more", "0").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, "pos.sales_sync.in_progress", "1").ConfigureAwait(false);
                    await UpsertSettingAsync(conn, tx, "pos.restore.needs_sync_review", "0").ConfigureAwait(false);
                    tx.Commit();
                }
            }

            private static Task UpsertSettingAsync(
                Microsoft.Data.Sqlite.SqliteConnection conn,
                Microsoft.Data.Sqlite.SqliteTransaction tx,
                string key,
                string value)
            {
                return conn.ExecuteAsync(@"
INSERT INTO app_settings(key,value) VALUES(@key,@value)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new { key, value }, tx);
            }
        }
    }

    internal static class BoundedLoggingWpfSmoke
    {
        private const int FloodCount = 100000;
        private const int FloodCapacity = 256;
        private const int FloodInfoLimit = 128;
        private const int FloodWarningLimit = 224;
        private const int MaxLengthFloodSampleCount = 2048;
        private const long MaxFloodPrivateBytesGrowth = 32L * 1024L * 1024L;
        private const double MaxProducerP95Microseconds = 5000d;
        private const double MaxProducerCallMicroseconds = 250000d;
        private static readonly TimeSpan FacadeFlushTimeout = TimeSpan.FromSeconds(3);

        internal static string Run(string dataDir)
        {
            try
            {
                if (IntPtr.Size != 4)
                    throw new InvalidOperationException("Bounded logging smoke must run as x86.");

                var facadeEvidence = VerifySharedFacadeAndRedaction();
                var rotationFiles = VerifyRotation(dataDir);
                var flood = VerifyBoundedFlood();

                return "PASS bounded async logging x86; " +
                       "facade_written=" + facadeEvidence.Written.ToString(CultureInfo.InvariantCulture) +
                       " rotation_files=" + rotationFiles.ToString(CultureInfo.InvariantCulture) +
                       " accepted=" + flood.Accepted.ToString(CultureInfo.InvariantCulture) +
                       " dropped=" + flood.Dropped.ToString(CultureInfo.InvariantCulture) +
                       " highWater=" + flood.HighWater.ToString(CultureInfo.InvariantCulture) +
                       " max_length_samples=" + MaxLengthFloodSampleCount.ToString(CultureInfo.InvariantCulture) +
                       " producer_ms=" + flood.ProducerMilliseconds.ToString(CultureInfo.InvariantCulture) +
                       " producer_p95_us=" + flood.ProducerP95Microseconds.ToString("F2", CultureInfo.InvariantCulture) +
                       " producer_max_us=" + flood.ProducerMaxMicroseconds.ToString("F2", CultureInfo.InvariantCulture) +
                       " private_delta_bytes=" + flood.PrivateBytesGrowth.ToString(CultureInfo.InvariantCulture) +
                       " private_bytes_after=" + flood.PrivateBytesAfter.ToString(CultureInfo.InvariantCulture) +
                       " private_high_water_observed_bytes=" + flood.PrivateHighWaterObservedBytes.ToString(CultureInfo.InvariantCulture) +
                       " private_high_water_delta_bytes=" + flood.PrivateHighWaterGrowth.ToString(CultureInfo.InvariantCulture) +
                       " peak_paged_bytes=" + flood.PeakPagedBytes.ToString(CultureInfo.InvariantCulture) +
                       " peak_working_set_bytes=" + flood.PeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture) +
                       " worker_alive_after_shutdown=false.";
            }
            catch (Exception ex)
            {
                return "FAIL bounded async logging x86: " +
                       ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static FacadeEvidence VerifySharedFacadeAndRedaction()
        {
            const string passwordSecret = "FacadePasswordSecret123456"; // gitleaks:allow -- synthetic redaction-test value
            const string tokenSecret = "FacadeTokenSecret123456"; // gitleaks:allow -- synthetic redaction-test value
            var first = new FileLogger("BoundedSmoke-A");
            var second = new FileLogger("BoundedSmoke-B");

            first.LogInfo(
                "facade-one password=" + passwordSecret +
                "\r\nforged-line");
            second.LogWarning("facade-two access_token=" + tokenSecret);

            var hostileException = new HostileLogException();
            var hostileFormatted = FileLogger.FormatExceptionFull(hostileException);
            if (hostileFormatted.IndexOf(
                    "[exception-message-unavailable]",
                    StringComparison.Ordinal) < 0 ||
                hostileFormatted.IndexOf(
                    "[exception-stack-unavailable]",
                    StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "Hostile exception getters were not formatted with safe fallbacks.");
            }
            second.LogError(hostileException, "hostile-exception-getter-smoke");

            var aggregateChildren = Enumerable.Range(0, 64)
                .Select(index => (Exception)new InvalidOperationException(
                    "aggregate-child-" + index.ToString(CultureInfo.InvariantCulture)))
                .ToArray();
            var formatted = FileLogger.FormatExceptionFull(
                new AggregateException("bounded aggregate", aggregateChildren));
            if (formatted.Length > 32 * 1024 ||
                formatted.IndexOf("aggregate-child-40", StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("Aggregate exception expansion was not bounded.");
            }

            if (!FileLogger.Shutdown(FacadeFlushTimeout))
                throw new InvalidOperationException("Process logger did not flush within its bounded timeout.");

            var metrics = FileLogger.GetMetrics();
            if (metrics.AcceptedTotal != 3 ||
                metrics.WrittenTotal != metrics.AcceptedTotal ||
                metrics.CurrentPending != 0 ||
                metrics.IsAccepting ||
                metrics.IsWriterAlive)
            {
                throw new InvalidOperationException("Process logger lifecycle metrics were inconsistent.");
            }

            var logText = File.ReadAllText(AppPaths.LogPath);
            if (logText.IndexOf(passwordSecret, StringComparison.Ordinal) >= 0 ||
                logText.IndexOf(tokenSecret, StringComparison.Ordinal) >= 0 ||
                logText.IndexOf("[redacted]", StringComparison.Ordinal) < 0 ||
                logText.IndexOf("[BoundedSmoke-A]", StringComparison.Ordinal) < 0 ||
                logText.IndexOf("[BoundedSmoke-B]", StringComparison.Ordinal) < 0 ||
                logText.IndexOf("[exception-message-unavailable]", StringComparison.Ordinal) < 0 ||
                logText.IndexOf("[exception-stack-unavailable]", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Facade output failed redaction or source checks.");
            }

            var nonEmptyLines = logText.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            if (nonEmptyLines.Length != 3 ||
                nonEmptyLines.Any(line => line.StartsWith("forged-line", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("A producer message injected an extra log line.");
            }

            return new FacadeEvidence(metrics.WrittenTotal);
        }

        private static int VerifyRotation(string dataDir)
        {
            var rotationRoot = Path.Combine(dataDir, "rotation-smoke");
            var logPath = Path.Combine(rotationRoot, "rotation.log");
            var sink = new RotatingFileLogSink(
                logPath,
                maxLogBytes: 512,
                retainedLogFiles: 2);

            sink.WriteBatch(RotationBatch('A'));
            sink.WriteBatch(RotationBatch('B'));
            sink.WriteBatch(RotationBatch('C'));
            sink.WriteBatch(RotationBatch('D'));

            var current = File.ReadAllText(logPath);
            var firstArchive = File.ReadAllText(logPath + ".1");
            var secondArchive = File.ReadAllText(logPath + ".2");
            if (current.IndexOf("ROTATE-D", StringComparison.Ordinal) < 0 ||
                firstArchive.IndexOf("ROTATE-C", StringComparison.Ordinal) < 0 ||
                secondArchive.IndexOf("ROTATE-B", StringComparison.Ordinal) < 0 ||
                File.Exists(logPath + ".3"))
            {
                throw new InvalidOperationException("Rotating sink retention order was incorrect.");
            }

            return Directory.GetFiles(rotationRoot, "rotation.log*").Length;
        }

        private static IReadOnlyList<string> RotationBatch(char marker)
        {
            return new[]
            {
                "ROTATE-" + marker + "-1-" + new string(marker, 130) + Environment.NewLine,
                "ROTATE-" + marker + "-2-" + new string(marker, 130) + Environment.NewLine
            };
        }

        private static FloodEvidence VerifyBoundedFlood()
        {
            var maxLengthMessage = new string('M', LogSanitizer.MaxStoredChars);
            LogSanitizer.Sanitize(maxLengthMessage);
            var sink = new BlockingBatchSink();
            var writer = new BoundedAsyncLogWriter(
                sink,
                capacity: FloodCapacity,
                infoAdmissionLimit: FloodInfoLimit,
                warningAdmissionLimit: FloodWarningLimit,
                batchSize: 32,
                maxMessageLength: LogSanitizer.MaxStoredChars,
                maxSourceLength: 64);

            try
            {
                var primeStarted = Stopwatch.GetTimestamp();
                var primeAccepted = writer.TryWrite(
                    LogLevel.Info,
                    "Flood",
                    "prime slow sink");
                var primeLatencyTicks = Stopwatch.GetTimestamp() - primeStarted;
                if (!primeAccepted || !sink.WaitUntilEntered(TimeSpan.FromSeconds(3)))
                {
                    throw new InvalidOperationException("Slow sink did not enter its blocked state.");
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    var privateBytesBefore = process.PrivateMemorySize64;
                    var privateHighWaterObserved = privateBytesBefore;
                    var producerLatencyTicks = new List<long>(
                        MaxLengthFloodSampleCount + (FloodCount / 25) + 1024);
                    producerLatencyTicks.Add(primeLatencyTicks);
                    var producerMaxTicks = primeLatencyTicks;
                    var stopwatch = Stopwatch.StartNew();
                    for (var index = 0; index < MaxLengthFloodSampleCount; index++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        writer.TryWrite(LogLevel.Info, "FloodMax", maxLengthMessage);
                        var elapsedTicks = Stopwatch.GetTimestamp() - started;
                        producerLatencyTicks.Add(elapsedTicks);
                        producerMaxTicks = Math.Max(producerMaxTicks, elapsedTicks);
                    }

                    process.Refresh();
                    privateHighWaterObserved = Math.Max(
                        privateHighWaterObserved,
                        process.PrivateMemorySize64);

                    for (var index = 0; index < FloodCount; index++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        writer.TryWrite(LogLevel.Info, "Flood", "bounded-info-message");
                        var elapsedTicks = Stopwatch.GetTimestamp() - started;
                        producerMaxTicks = Math.Max(producerMaxTicks, elapsedTicks);
                        if (index % 25 == 0)
                        {
                            producerLatencyTicks.Add(elapsedTicks);
                        }

                        if (index % 4096 == 0)
                        {
                            process.Refresh();
                            privateHighWaterObserved = Math.Max(
                                privateHighWaterObserved,
                                process.PrivateMemorySize64);
                        }
                    }

                    for (var index = 0; index < 512; index++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        writer.TryWrite(LogLevel.Warning, "Flood", "bounded-warning-message");
                        var elapsedTicks = Stopwatch.GetTimestamp() - started;
                        producerLatencyTicks.Add(elapsedTicks);
                        producerMaxTicks = Math.Max(producerMaxTicks, elapsedTicks);
                    }

                    for (var index = 0; index < 512; index++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        writer.TryWrite(LogLevel.Error, "Flood", "bounded-error-message");
                        var elapsedTicks = Stopwatch.GetTimestamp() - started;
                        producerLatencyTicks.Add(elapsedTicks);
                        producerMaxTicks = Math.Max(producerMaxTicks, elapsedTicks);
                    }
                    stopwatch.Stop();

                    process.Refresh();
                    privateHighWaterObserved = Math.Max(
                        privateHighWaterObserved,
                        process.PrivateMemorySize64);
                    var privateHighWaterGrowth = Math.Max(
                        0L,
                        privateHighWaterObserved - privateBytesBefore);
                    if (privateHighWaterGrowth > MaxFloodPrivateBytesGrowth)
                    {
                        throw new InvalidOperationException(
                            "100k producer flood exceeded the x86 private-memory high-water budget.");
                    }

                    var saturated = writer.GetMetrics();
                    if (saturated.DroppedInfo <= 0 ||
                        saturated.AcceptedWarning <= 0 ||
                        saturated.AcceptedError <= 0 ||
                        saturated.HighWaterMark != FloodCapacity ||
                        saturated.CurrentPending > FloodCapacity)
                    {
                        throw new InvalidOperationException(
                            "Saturation did not preserve WARN/ERROR reserve or queue bounds.");
                    }

                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        throw new InvalidOperationException(
                            "100k producer flood exceeded the bounded latency budget.");
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    process.Refresh();
                    var privateBytesAfter = process.PrivateMemorySize64;
                    privateHighWaterObserved = Math.Max(
                        privateHighWaterObserved,
                        privateBytesAfter);
                    var privateBytesGrowth = Math.Max(
                        0L,
                        privateBytesAfter - privateBytesBefore);
                    if (privateBytesGrowth > MaxFloodPrivateBytesGrowth)
                    {
                        throw new InvalidOperationException(
                            "100k producer flood exceeded the x86 private-memory budget.");
                    }

                    sink.Release();
                    if (!writer.Shutdown(TimeSpan.FromSeconds(5)))
                    {
                        throw new InvalidOperationException("Flood writer did not stop after sink release.");
                    }

                    var final = writer.GetMetrics();
                    if (final.IsWriterAlive || final.IsAccepting || final.CurrentPending != 0 ||
                        final.WrittenTotal != final.AcceptedTotal)
                    {
                        throw new InvalidOperationException("Flood writer leaked work or its worker thread.");
                    }

                    process.Refresh();
                    producerLatencyTicks.Sort();
                    var p95Index = Math.Max(
                        0,
                        (int)Math.Ceiling(producerLatencyTicks.Count * 0.95d) - 1);
                    var producerP95Microseconds = TicksToMicroseconds(
                        producerLatencyTicks[p95Index]);
                    var producerMaxMicroseconds = TicksToMicroseconds(
                        producerMaxTicks);
                    if (producerP95Microseconds >= MaxProducerP95Microseconds ||
                        producerMaxMicroseconds >= MaxProducerCallMicroseconds)
                    {
                        throw new InvalidOperationException(
                            "Producer latency exceeded p95/max nonblocking budgets.");
                    }
                    return new FloodEvidence(
                        final.AcceptedTotal,
                        final.DroppedTotal,
                        final.HighWaterMark,
                        stopwatch.ElapsedMilliseconds,
                        producerP95Microseconds,
                        producerMaxMicroseconds,
                        privateBytesGrowth,
                        privateBytesAfter,
                        privateHighWaterObserved,
                        privateHighWaterGrowth,
                        process.PeakPagedMemorySize64,
                        process.PeakWorkingSet64);
                }
            }
            finally
            {
                sink.Release();
                writer.Shutdown(TimeSpan.FromSeconds(1));
                writer.Dispose();
            }
        }

        private static double TicksToMicroseconds(long ticks)
        {
            return ticks * 1000000d / Stopwatch.Frequency;
        }

        private sealed class BlockingBatchSink : ILogBatchSink
        {
            private readonly ManualResetEvent _entered = new ManualResetEvent(false);
            private readonly ManualResetEvent _release = new ManualResetEvent(false);

            public void WriteBatch(IReadOnlyList<string> lines)
            {
                _entered.Set();
                _release.WaitOne();
            }

            internal bool WaitUntilEntered(TimeSpan timeout)
            {
                return _entered.WaitOne(timeout);
            }

            internal void Release()
            {
                _release.Set();
            }
        }

        private sealed class HostileLogException : Exception
        {
            public override string Message =>
                throw new InvalidOperationException("synthetic hostile Message getter");

            public override string StackTrace =>
                throw new InvalidOperationException("synthetic hostile StackTrace getter");
        }

        private sealed class FacadeEvidence
        {
            internal FacadeEvidence(long written)
            {
                Written = written;
            }

            internal long Written { get; }
        }

        private sealed class FloodEvidence
        {
            internal FloodEvidence(
                long accepted,
                long dropped,
                int highWater,
                long producerMilliseconds,
                double producerP95Microseconds,
                double producerMaxMicroseconds,
                long privateBytesGrowth,
                long privateBytesAfter,
                long privateHighWaterObservedBytes,
                long privateHighWaterGrowth,
                long peakPagedBytes,
                long peakWorkingSetBytes)
            {
                Accepted = accepted;
                Dropped = dropped;
                HighWater = highWater;
                ProducerMilliseconds = producerMilliseconds;
                ProducerP95Microseconds = producerP95Microseconds;
                ProducerMaxMicroseconds = producerMaxMicroseconds;
                PrivateBytesGrowth = privateBytesGrowth;
                PrivateBytesAfter = privateBytesAfter;
                PrivateHighWaterObservedBytes = privateHighWaterObservedBytes;
                PrivateHighWaterGrowth = privateHighWaterGrowth;
                PeakPagedBytes = peakPagedBytes;
                PeakWorkingSetBytes = peakWorkingSetBytes;
            }

            internal long Accepted { get; }
            internal long Dropped { get; }
            internal int HighWater { get; }
            internal long ProducerMilliseconds { get; }
            internal double ProducerP95Microseconds { get; }
            internal double ProducerMaxMicroseconds { get; }
            internal long PrivateBytesGrowth { get; }
            internal long PrivateBytesAfter { get; }
            internal long PrivateHighWaterObservedBytes { get; }
            internal long PrivateHighWaterGrowth { get; }
            internal long PeakPagedBytes { get; }
            internal long PeakWorkingSetBytes { get; }
        }
    }
}
