using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.CustomerDisplay;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Infrastructure.Displays;
using Win7POS.Wpf.Localization;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Wpf.Printing;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class Program
    {
        private const string QaShopId = "qa-shop-local";
        private const string QaShopCode = "QA-SHOP";
        private static string _receiptAlignmentFailure = string.Empty;

        [STAThread]
        private static void Main(string[] args)
        {
            var dataDir = ValueAfter(args, "--data-dir");
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                MessageBox.Show("--data-dir is required.", "Win7POS UI Smoke Harness");
                return;
            }

            Directory.CreateDirectory(dataDir);
            var automatedRun = HasArg(args, "--seed") ||
                               HasArg(args, "--shell-window-state") ||
                               HasArg(args, "--printer-selection-binding") ||
                               HasArg(args, "--printer-presentation") ||
                               HasArg(args, "--pos-footer-layout") ||
                               HasArg(args, "--receipt-rendering-alignment") ||
                               HasArg(args, "--capture-ux-artifacts") ||
                               HasArg(args, "--capture-settings-audit") ||
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

        private static void EnsureSyntheticTrustedSessionSeedPath(string dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir) || !Path.IsPathRooted(dataDir))
            {
                throw new InvalidOperationException(
                    "--seed-trusted-session requires an absolute QA data directory.");
            }

            var fullPath = Path.GetFullPath(dataDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var qaSegment = Path.DirectorySeparatorChar + "Win7POS-QA" + Path.DirectorySeparatorChar;
            var pathWithTerminator = fullPath + Path.DirectorySeparatorChar;
            if (pathWithTerminator.IndexOf(qaSegment, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    "--seed-trusted-session is restricted to a Win7POS-QA directory.");
            }

            if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                throw new InvalidOperationException(
                    "--seed-trusted-session requires a new or empty QA data directory.");
            }
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
                    var printerCommandPolicyPass = await VerifyPrinterCommandPolicyAsync().ConfigureAwait(true);
                    var printerSelectionBindingPass = await VerifyPrinterSelectionBindingAsync().ConfigureAwait(true);
                    var cashDrawerParsingPass = VerifyCashDrawerCommandParsing();
                    var receiptColumnFitPass = VerifyReceiptColumnFit();
                    var printerTestReceiptPass = VerifyPrinterTestReceiptBuilder();
                    var receiptShopSnapshotPass = await VerifyReceiptShopSnapshotReprintAsync().ConfigureAwait(true);
                    var activeDrawerSettingsValidationPass = await VerifyActiveDrawerSettingsValidationAsync().ConfigureAwait(true);
                    var samples = new List<LifecycleSample>();
                    var weakWindows = new List<LifecycleWindowReference>();
                    for (var cycle = 1; cycle <= 20; cycle++)
                    {
                        _status.Text = "Lifecycle cycle " + cycle.ToString(CultureInfo.InvariantCulture) + "/20";
                        await OpenThenCloseAsync(CreateDailyReportDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateUserManagementDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(new SettingsHubDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateCustomerDisplaySettingsDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateProductEditDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateSyncCenterDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(new PosStartOfDaySyncDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreatePrinterSettingsDialog(), weakWindows).ConfigureAwait(true);
                        samples.Add(LifecycleSample.Capture(cycle));
                    }

                    // Displace the last async/focus references held by WPF and the caller so
                    // the 20 measured instances are not mistaken for framework-local roots.
                    await OpenThenCloseAsync(CreateDailyReportDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateUserManagementDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(new SettingsHubDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateCustomerDisplaySettingsDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateProductEditDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateSyncCenterDialog(), null).ConfigureAwait(true);
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
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    for (var collection = 0; collection < 3; collection++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        await Task.Delay(100).ConfigureAwait(true);
                    }
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
                    var passed = openWindows == 0 && languageHandlersAfter == languageHandlersBefore &&
                                 displaySubscriptionsAfter == displaySubscriptionsBefore && displayWindowPass &&
                                 printerCommandPolicyPass && printerSelectionBindingPass &&
                                 cashDrawerParsingPass && receiptColumnFitPass &&
                                 printerTestReceiptPass && receiptShopSnapshotPass &&
                                 activeDrawerSettingsValidationPass &&
                                 !monotonicPrivateBytes && !monotonicHandles;
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: cycles=20; printerSettingsCycles=20; customerDisplayCycles=50; managerCycles=50; residualWindowsDiagnostic={1}; residualTypes={2}; residualViewModelsDiagnostic={3}; residualViewModelTypes={4}; openWindows={5}; languageHandlers={6}->{7}; privateBytes={8}->{9}; handles={10}->{11}; monotonicPrivateBytes={12}; monotonicHandles={13}; displayHandlers={14}->{15}; displayWindowPass={16}; printerCommandPolicyPass={17}; cashDrawerParsingPass={18}; receiptColumnFitPass={19}; printerTestReceiptPass={20}; printerSelectionBindingPass={21}; receiptShopSnapshotPass={22}; activeDrawerSettingsValidationPass={23}",
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
                        activeDrawerSettingsValidationPass);
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

                return "PASS: posFooter=True; paymentPreview=True; printerPreview=True";
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
                    new DbMaintenanceDialog(new DbMaintenanceViewModel(new PosWorkflowService())),
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
                var dataContext = dialog.DataContext;
                dialog.Owner = this;
                var rendered = new TaskCompletionSource<bool>();
                EventHandler renderedHandler = null;
                renderedHandler = (_, __) =>
                {
                    dialog.ContentRendered -= renderedHandler;
                    rendered.TrySetResult(true);
                };
                dialog.ContentRendered += renderedHandler;
                dialog.Show();
                await rendered.Task.ConfigureAwait(true);
                await Task.Delay(40).ConfigureAwait(true);
                dialog.Close();
                if (weakWindows != null)
                {
                    weakWindows.Add(new LifecycleWindowReference(
                        dialog.GetType().Name,
                        new WeakReference(dialog),
                        new WeakReference(dataContext)));
                }
            }

            private static DailyReportDialog CreateDailyReportDialog()
            {
                return new DailyReportDialog(new DailyReportViewModel(new PosWorkflowService()));
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
                        IsAvailable = true,
                        IsOffline = false,
                        IsPaused = false,
                        IsVirtual = false,
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
                    IsAvailable = true,
                    IsOffline = false,
                    IsPaused = false,
                    IsVirtual = false,
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
                    IsAvailable = true,
                    IsOffline = false,
                    IsPaused = false,
                    IsVirtual = false,
                    StatusText = "Ready"
                };
                var vm = new PrinterSettingsViewModel
                {
                    PrinterName = queue.Name,
                    ReceiptEnabled = true,
                    SaveCopyToFile = false,
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
                    var advanced = dialog.FindName("AdvancedOptionsExpander") as Expander;
                    var outputDirectory = dialog.FindName("OutputDirectoryPanel") as StackPanel;
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
                                           advanced != null && !advanced.IsExpanded &&
                                           outputDirectory != null &&
                                           outputDirectory.Visibility == Visibility.Collapsed &&
                                           drawer != null && !drawer.IsExpanded &&
                                           drawerDetails != null &&
                                           drawerDetails.Visibility == Visibility.Collapsed &&
                                           detected != null && !detected.IsExpanded &&
                                           detectedPrinters != null &&
                                           !(detectedPrinters is ListBox);

                    advanced.IsExpanded = true;
                    drawer.IsExpanded = true;
                    detected.IsExpanded = true;
                    vm.SaveCopyToFile = true;
                    vm.CashDrawerEnabled = true;
                    await Dispatcher.InvokeAsync(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                    var progressiveDisclosurePass = outputDirectory.Visibility == Visibility.Visible &&
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

                    queue.IsVirtual = false;
                    vm.PrinterName = string.Empty;
                    vm.CashDrawerPrinterName = string.Empty;
                    vm.AllowWindowsDefault = false;
                    if (vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    vm.AllowWindowsDefault = true;
                    if (!vm.TestPrintCommand.CanExecute(null) || vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

                    queue.IsVirtual = false;
                    vm.PrinterName = queue.Name;
                    vm.CashDrawerPrinterName = queue.Name;
                    vm.CashDrawerCommand = "27,112,0,25,250";
                    if (!vm.TestPrintCommand.CanExecute(null) || !vm.TestCashDrawerCommand.CanExecute(null))
                        return false;

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
                    "27,112,0,25,250,0"
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
                    SaveCopyToFile = before.SaveCopyToFile,
                    OutputDirectory = before.OutputDirectory,
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
                return lines.All(line => line.Length <= 42) &&
                       lines32.All(line => line.Length <= 32) &&
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
                    BindingFlags.Static | BindingFlags.NonPublic);
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
                            if (expected.Replace("\r\n", "\n").Split('\n').Any(line => line.Length > width))
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
                                expected.Replace("\r\n", "\n").Split('\n').Any(line => line.Length > width))
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
                                    : expected.Replace("\r\n", "\n").Split('\n').Max(line => line.Length);
                                var sampleMaxWidth = string.IsNullOrEmpty(sample)
                                    ? 0
                                    : sample.Replace("\r\n", "\n").Split('\n').Max(line => line.Length);
                                var cashPresent = !string.IsNullOrEmpty(expected) &&
                                                  expected.IndexOf(PosLocalization.T("common.cash"), StringComparison.Ordinal) >= 0;
                                var cardPresent = !string.IsNullOrEmpty(expected) &&
                                                  expected.IndexOf(PosLocalization.T("common.card"), StringComparison.Ordinal) >= 0;
                                var normalizedSample = (sample ?? string.Empty)
                                    .Replace("\r", string.Empty)
                                    .Replace("\n", string.Empty);
                                var normalizedMarker = PosLocalization.T("printer.testReceiptMarker")
                                    .Replace("\r", string.Empty)
                                    .Replace("\n", string.Empty);
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

            public static void SeedTrustedDeviceSession()
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
                        ShopName = "QA Synthetic Shop",
                        ShopStatus = "active",
                        Source = "qa_harness"
                    },
                    Staff = new PosStaffResponse
                    {
                        CredentialVersion = 1,
                        DisplayName = "QA Administrator",
                        RoleKey = "admin",
                        StaffCode = "qa-admin",
                        StaffId = "qa-admin-local"
                    }
                });
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

            private static async Task SeedShopAndSaleSafetyAsync(SqliteConnectionFactory factory)
            {
                await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
                {
                    ShopId = QaShopId,
                    ShopCode = QaShopCode,
                    ShopName = "QA Synthetic Shop",
                    Source = "qa_harness"
                }).ConfigureAwait(false);
                var state = new CatalogShopStateRepository(factory);
                var binding = await state.EnsureAndLoadCursorAsync(QaShopId, QaShopCode).ConfigureAwait(false);
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
}
