using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Products.Images;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class CartPerformanceRegressionSmoke
    {
        private static void Require(bool condition, string reason) => FunctionalCompletionSmoke.Require(condition, reason);

        internal static async Task<IOperatorSession> CreateQaOperatorAsync()
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var response = AuthorizationLeaseWpfSmoke.BuildResponse(true);
            var users = new UserRepository(factory);
            await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
            {
                Credential = "2468", CredentialVersion = response.Staff.CredentialVersion,
                DisplayName = response.Staff.DisplayName, RemoteRoleKey = response.Staff.RoleKey,
                RemoteShopId = response.Shop.ShopId, RemoteStaffId = response.Staff.StaffId,
                ShopCode = response.Shop.ShopCode, StaffCode = response.Staff.StaffCode
            });
            var store = new PosTrustedDeviceStore();
            store.SaveFirstLogin(response, "qa-performance-" + Guid.NewGuid().ToString("N"));
            await AuthorizationLeaseWpfSmoke.SeedCatalogSaleSafetyAsync(factory);
            var username = await users.FindTrustedRemoteStaffUsernameAsync(response.Shop.ShopId, response.Shop.ShopCode,
                response.Staff.StaffId, response.Staff.StaffCode, response.Staff.CredentialVersion);
            var session = new OperatorSession(users, new SecurityRepository(factory), new PosOfflineAuthorizationLeaseGuard(store, () => DateTimeOffset.UtcNow));
            Require(await session.LoginAsync(username, "2468") == LoginResult.Success, "QA operator login failed");
            return session;
        }

        internal static async Task WaitAsync(Func<bool> condition, string reason, int milliseconds = 5000)
        {
            var time = Stopwatch.StartNew();
            while (!condition() && time.ElapsedMilliseconds < milliseconds) await Task.Delay(5);
            Require(condition(), reason);
        }

        internal static async Task DrainAsync()
        {
            var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.WhenAny(operation.Task, Task.Delay(3000));
            if (operation.Status != DispatcherOperationStatus.Completed) { operation.Abort(); throw new InvalidOperationException("Background did not progress"); }
        }

        internal static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item;
        }

        internal static async Task RunAsync()
        {
            var previous = OperatorSessionHolder.Current;
            Window host = null;
            PosViewModel vm = null;
            var phase = "initialization";
            try
            {
                OperatorSessionHolder.Current = await CreateQaOperatorAsync();
                using (var connection = new SqliteConnectionFactory(PosDbOptions.Default()).Open())
                    connection.Execute(@"WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<500)
INSERT INTO products(barcode,name,unitPrice,is_active) SELECT printf('PERF%04d',x),'中文 café long wrapped product '||x,1000,1 FROM n;
INSERT INTO product_meta(barcode,stock_qty) SELECT barcode,10000 FROM products WHERE barcode LIKE 'PERF%';");
                var view = new PosView(); // Preserve constructor composition and events.
                vm = (PosViewModel)view.DataContext;
                var loaded = false;
                view.Loaded += (_, __) => loaded = true;
                host = new Window { Content = view, Width = 1024, Height = 768, ShowInTaskbar = false };
                host.Show();
                await WaitAsync(() => loaded && !vm.IsBusy, "real view initialization");
                await DrainAsync();
                var barcode = (TextBox)view.FindName("BarcodeBox");
                var rows = (ListBox)view.FindName("CartListBox");
                var grid = (ListBox)view.FindName("CartGridListBox");
                // Execute through the public input event, command and real VM.
                for (var index = 1; index <= 50; index++)
                {
                    phase = "public scan " + index;
                    barcode.Text = "PERF" + index.ToString("D4");
                    barcode.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    barcode.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(barcode), Environment.TickCount, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                    var expected = index;
                    await WaitAsync(() => !vm.IsBusy && vm.CartItems.Count == expected, "public scanner command lost input");
                }
                var first = vm.CartItems[0];
                for (var repeat = 0; repeat < 20; repeat++)
                {
                    vm.BarcodeInput = "PERF0001";
                    Require(vm.AddBarcodeCommand.CanExecute(null), "scan command unexpectedly disabled");
                    vm.AddBarcodeCommand.Execute(null);
                    var quantity = repeat + 2;
                    await WaitAsync(() => !vm.IsBusy && first.Quantity == quantity, "repeated public scan lost quantity");
                }
                Require(ReferenceEquals(first, vm.CartItems[0]) && ReferenceEquals(vm.SelectedCartItem, first), "scan changed row identity or selection");
                // Fixture expansion is separate from the public-command checks.
                var service = (PosWorkflowService)typeof(PosViewModel).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vm);
                var session = (PosSession)typeof(PosWorkflowService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
                session.ReplaceWithLines(Enumerable.Range(1, 500).Select(index => new RestoredLine
                { Barcode = "PERF" + index.ToString("D4"), Name = "中文 café wrapped product " + index, UnitPrice = 1000, Quantity = 1 }).ToList());
                vm.ApplyDiscountSnapshot(await service.GetSnapshotAsync());
                foreach (var width in new[] { 1024d, 1440d, 800d, 1024d })
                {
                    host.Width = width;
                    foreach (var mode in new[] { CartViewMode.Grid, CartViewMode.Rows, CartViewMode.Grid })
                    {
                        await vm.SetCartViewModeAsync(mode);
                        view.UpdateLayout();
                        await DrainAsync();
                        var active = mode == CartViewMode.Grid ? grid : rows;
                        foreach (var index in new[] { 499, 0, 250, 499 })
                        {
                            phase = "scroll " + mode + " width=" + width + " index=" + index;
                            vm.SelectedCartItem = vm.CartItems[index];
                            active.ScrollIntoView(active.SelectedItem);
                            view.UpdateLayout();
                            await DrainAsync();
                            view.UpdateLayout();
                            var container = active.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
                            Require(container != null, "selected item was not realized");
                            Require(active.IsAncestorOf(container), "detached selected container; mode=" + vm.CartViewMode + ";visibility=" + active.Visibility + ";size=" + active.RenderSize + ";parent=" + VisualTreeHelper.GetParent(container)?.GetType().FullName +
                                "; visual indices=" + string.Join(",", Descendants(active).OfType<ListBoxItem>().Select(item => active.ItemContainerGenerator.IndexFromContainer(item))));
                            var bounds = container.TransformToAncestor(active).TransformBounds(new Rect(container.RenderSize));
                            Require(bounds.Bottom > 0 && bounds.Top < active.ActualHeight, "selected item outside viewport");
                            var realized = Enumerable.Range(0, 500).Select(i => active.ItemContainerGenerator.ContainerFromIndex(i)).OfType<FrameworkElement>().ToArray();
                            Require(realized.Length < 80, "cart realized all containers");
                            if (mode == CartViewMode.Grid)
                            {
                                Require(Descendants(active).OfType<VirtualizingCartWrapPanel>().Any(), "grid panel contract missing");
                                if (active.ActualWidth >= 368) Require(realized.Select(item => Math.Round(item.TransformToAncestor(active).Transform(new Point()).X)).Distinct().Count() > 1, "grid lost multiple columns");
                                Require(realized.All(item => item.ActualHeight > 0 && item.DesiredSize.Height <= item.ActualHeight + item.Margin.Top + item.Margin.Bottom + 1), "card height clipped");
                            }
                        }
                    }
                }
                phase = "grid structural changes and last public scan";
                await vm.SetCartViewModeAsync(CartViewMode.Grid);
                vm.SelectedCartItem = vm.CartItems[499];
                vm.RemoveLineCommand.Execute(null);
                await WaitAsync(() => !vm.IsBusy && vm.CartItems.Count == 499, "public removal failed");
                vm.BarcodeInput = "PERF0500";
                vm.AddBarcodeCommand.Execute(null);
                await WaitAsync(() => !vm.IsBusy && vm.CartItems.Count == 500, "last public product scan failed");
                await DrainAsync(); view.UpdateLayout();
                Require(vm.SelectedCartItem.Barcode == "PERF0500" && grid.IsAncestorOf((DependencyObject)grid.ItemContainerGenerator.ContainerFromItem(vm.SelectedCartItem)), "last added card detached");
                session.ReplaceWithLines(new RestoredLine[0]);
                vm.ApplyDiscountSnapshot(await service.GetSnapshotAsync());
                await DrainAsync(); view.UpdateLayout();
                Require(grid.Items.Count == 0 && !Descendants(grid).OfType<ListBoxItem>().Any(), "empty grid retained cards");
                vm.BarcodeInput = "PERF0500";
                vm.AddBarcodeCommand.Execute(null);
                await WaitAsync(() => !vm.IsBusy && vm.CartItems.Count == 1, "scan after clearing failed");
                await DrainAsync(); view.UpdateLayout();
                Require(grid.IsAncestorOf((DependencyObject)grid.ItemContainerGenerator.ContainerFromIndex(0)), "new card after empty grid detached");
                phase = "small focus rectangle in tall card";
                host.Height = 360; view.UpdateLayout(); await DrainAsync();
                var panel = Descendants(grid).OfType<VirtualizingCartWrapPanel>().Single();
                var card = (FrameworkElement)grid.ItemContainerGenerator.ContainerFromIndex(0);
                Require(panel.ViewportHeight > 0 && panel.ViewportHeight < card.ActualHeight, "short viewport fixture missing");
                panel.SetVerticalOffset(0); view.UpdateLayout();
                panel.MakeVisible(card, new Rect(0, 0, 50, 20));
                Require(panel.VerticalOffset == 0, "bringing card top into view scrolled it away");
                host.Height = 768; view.UpdateLayout(); await DrainAsync();
                // Actual template indicators must stop while hidden and restart
                // when visible/loading, including an ancestor becoming collapsed.
                var imageModel = new ProductImageDisplayViewModel();
                phase = "indicator lifetime";
                var image = new ProductImagePresenter { DataContext = imageModel };
                var imageHost = new Window { Content = image, Width = 200, Height = 160, Owner = host, ShowInTaskbar = false };
                try
                {
                    imageHost.Show(); imageModel.SetLoading(); imageHost.UpdateLayout(); await DrainAsync();
                    var progress = Descendants(image).OfType<ProgressBar>().Single();
                    Require(progress.IsVisible && progress.IsIndeterminate, "visible loading indicator stopped");
                    image.Visibility = Visibility.Collapsed; imageHost.UpdateLayout(); await DrainAsync();
                    Require(!progress.IsIndeterminate, "hidden loading indicator animates");
                    image.Visibility = Visibility.Visible; imageModel.SetNoImage(); imageHost.UpdateLayout(); await DrainAsync();
                    Require(!progress.IsIndeterminate, "completed image indicator animates");
                }
                finally { imageHost.Close(); }
                host.Activate(); await DrainAsync();
                phase = "bounded observer synchronous completion ordering";
                var dispatcher = Dispatcher.CurrentDispatcher;
                using (var observer = new BoundedDispatcherObservation(dispatcher))
                {
                    await Task.Run(() =>
                    {
                        for (var index = 0; index < 6000; index++) dispatcher.Invoke(() => { }, DispatcherPriority.Send);
                    });
                    var observation = observer.Snapshot();
                    Require(observer.Dropped == 0 && observation.Pending < 32, "completed synchronous operations saturated observer: " + observer.OverflowDetail);
                }
                // Equivalent visual requests must be bounded; scans above are
                // deliberately not coalesced or discarded.
                var focusPosted = 0;
                phase = "focus requests";
                DispatcherHookEventHandler posted = (_, args) =>
                {
                    var callback = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(args.Operation) as Delegate;
                    if (callback?.Method.Name.IndexOf("FocusBarcode", StringComparison.Ordinal) >= 0) focusPosted++;
                };
                Dispatcher.CurrentDispatcher.Hooks.OperationPosted += posted;
                try { for (var i = 0; i < 250; i++) view.RestoreScannerFocus(); }
                finally { Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= posted; }
                Require(focusPosted <= 1, "focus requests not coalesced: " + focusPosted);
                await DrainAsync();
                Require(barcode.IsKeyboardFocusWithin, "scanner focus missing; hostActive=" + host.IsActive + "; hostVisible=" + host.IsVisible + "; viewLoaded=" + view.IsLoaded);
                var editorText = new TextBox();
                var editor = new Window { Content = editorText, Width = 300, Height = 150, Owner = host, ShowInTaskbar = false };
                try
                {
                    editor.Show(); editorText.Focus();
                    view.RestoreScannerFocus(); await DrainAsync();
                    Require(editorText.IsKeyboardFocusWithin, "scanner stole focus from an active editor");
                    var composition = new TextComposition(InputManager.Current, editorText, "中文 café áéí", TextCompositionAutoComplete.Off);
                    TextCompositionManager.StartComposition(composition);
                    TextCompositionManager.CompleteComposition(composition);
                    Require(editorText.Text.Contains("中文 café áéí"), "Unicode composition lost text");
                }
                finally { editor.Close(); }
                view.RestoreScannerFocus(); host.Close(); await DrainAsync();
                Require(!barcode.IsKeyboardFocusWithin, "closed view retained keyboard focus");
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(PosDbOptions.Default().DbPath), "cart-regression-error.txt"), phase + Environment.NewLine + error);
                throw;
            }
            finally { host?.Close(); vm?.Dispose(); OperatorSessionHolder.Current = previous; }
        }
    }
}
