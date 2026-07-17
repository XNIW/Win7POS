using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class Program
    {
        private const string QaShopId = "qa-shop-local";
        private const string QaShopCode = "QA-SHOP";

        [STAThread]
        private static void Main(string[] args)
        {
            var dataDir = ValueAfter(args, "--data-dir");
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                MessageBox.Show("--data-dir is required.", "Win7POS UI Smoke Harness");
                return;
            }

            Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", dataDir);
            Environment.SetEnvironmentVariable("WIN7POS_SAFE_START", "1");

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AddApplicationResources(app);
            app.DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                MessageBox.Show(e.Exception.GetType().Name + ": " + e.Exception.Message,
                    "Win7POS UI Smoke Harness - unhandled");
                app.Shutdown(2);
            };
            app.Startup += async (_, __) =>
            {
                try
                {
                    if (HasArg(args, "--seed"))
                    {
                        await QaFixture.SeedAsync().ConfigureAwait(true);
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
                    if (HasArg(args, "--lifecycle"))
                    {
                        var result = await launcher.RunLifecycleMatrixAsync().ConfigureAwait(true);
                        File.WriteAllText(Path.Combine(dataDir, "lifecycle-result.txt"), result);
                        app.Shutdown(result.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.GetType().Name + ": " + ex.Message,
                        "Win7POS UI Smoke Harness - startup");
                    app.Shutdown(2);
                }
            };
            app.Run();
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
            shell.Close();

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
                Height = 430;
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
                    var languageHandlersBefore = GetLanguageChangedHandlerCount();
                    var samples = new List<LifecycleSample>();
                    var weakWindows = new List<LifecycleWindowReference>();
                    for (var cycle = 1; cycle <= 20; cycle++)
                    {
                        _status.Text = "Lifecycle cycle " + cycle.ToString(CultureInfo.InvariantCulture) + "/20";
                        await OpenThenCloseAsync(CreateDailyReportDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateUserManagementDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(new SettingsHubDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateProductEditDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(CreateSyncCenterDialog(), weakWindows).ConfigureAwait(true);
                        await OpenThenCloseAsync(new PosStartOfDaySyncDialog(), weakWindows).ConfigureAwait(true);
                        samples.Add(LifecycleSample.Capture(cycle));
                    }

                    // Displace the last async/focus references held by WPF and the caller so
                    // the 20 measured instances are not mistaken for framework-local roots.
                    await OpenThenCloseAsync(CreateDailyReportDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateUserManagementDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(new SettingsHubDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateProductEditDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(CreateSyncCenterDialog(), null).ConfigureAwait(true);
                    await OpenThenCloseAsync(new PosStartOfDaySyncDialog(), null).ConfigureAwait(true);

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
                    var monotonicPrivateBytes = IsStrictlyIncreasing(samples.Select(x => x.PrivateBytes).ToList());
                    var monotonicHandles = IsStrictlyIncreasing(samples.Select(x => (long)x.HandleCount).ToList());
                    var passed = openWindows == 0 && languageHandlersAfter == languageHandlersBefore &&
                                 !monotonicPrivateBytes && !monotonicHandles;
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: cycles=20; residualWindowsDiagnostic={1}; residualTypes={2}; residualViewModelsDiagnostic={3}; residualViewModelTypes={4}; openWindows={5}; languageHandlers={6}->{7}; privateBytes={8}->{9}; handles={10}->{11}; monotonicPrivateBytes={12}; monotonicHandles={13}",
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
                        monotonicHandles);
                }
                finally
                {
                    IsEnabled = true;
                    _running = false;
                }
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
