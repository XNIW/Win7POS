using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Products;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class ProductPriceHistoryCompletionSmoke
    {
        internal static async Task RunAsync(List<string> lines)
        {
            var failures = new List<string>();
            await CheckAsync(lines, failures, "history_ui_int64", CheckInt64Async);
            foreach (var invalid in new[] { "abc", "-1", "2147483648" })
            {
                await CheckAsync(lines, failures, "mixed_retail_" + invalid,
                    () => CheckInvalidMixedAsync(invalid, true));
                await CheckAsync(lines, failures, "mixed_purchase_" + invalid,
                    () => CheckInvalidMixedAsync(invalid, false));
            }
            await CheckAsync(lines, failures, "blank_zero_groups_manual_bounds", CheckValidInputsAsync);
            await CheckAsync(lines, failures, "malformed_groups_and_overflow", CheckMalformedInputsAsync);
            await CheckAsync(lines, failures, "blank_and_unchanged_no_success", CheckNoChangeAsync);
            await CheckAsync(lines, failures, "service_rollback_retry_focus", CheckServiceFailureAsync);
            await CheckAsync(lines, failures, "double_confirm_loading_refresh", CheckDoubleConfirmAsync);
            await CheckAsync(lines, failures, "field_localization_four_languages", CheckLocalizationAsync);
            Require(failures.Count == 0, string.Join(" | ", failures));
        }

        private static async Task CheckInt64Async()
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var products = new ProductRepository(factory);
            await products.UpsertProductAndMetaInTransactionAsync(
                new Product { Barcode = "HISTORY64-UI", Name = "History Int64", UnitPrice = long.MaxValue },
                "", "", 100, null, "", null, "", 1, ProductWriteOrigin.SupplierImportApply);
            var product = await products.GetByBarcodeAsync("HISTORY64-UI");
            using (var connection = factory.Open())
                connection.Execute("INSERT INTO product_price_history(barcode,timestamp,type,old_price,new_price,source) VALUES('HISTORY64-UI','2026-10-08 10:00:00','retail',@old,@price,'IMPORT')",
                    new { old = 4294967296L, price = long.MaxValue });

            var service = new ProductsWorkflowService();
            var vm = new ProductPriceHistoryViewModel(product.Id, product.Barcode, product.Name,
                product.UnitPrice, 100, service, true);
            var exact = long.MaxValue.ToString(CultureInfo.InvariantCulture);
            Require(vm.CurrentRetailPrice == exact, "initial history price was narrowed");
            await vm.LoadAsync();
            Require(vm.CurrentRetailPrice == exact && vm.RetailHistory.Count == 1 &&
                vm.RetailHistory[0].OldPrice == 4294967296L && vm.RetailHistory[0].NewPrice == long.MaxValue,
                "history UI reader lost Int64 values");

            // The existing manual/API ingress has a narrower range. A purchase-only
            // edit must retain the real retail value and fail explicitly, never wrap.
            vm.NewPurchaseText = "101";
            vm.ApplyNewPricesCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && (vm.IsBusy || vm.NewPurchaseText.Length == 0 ||
                vm.StatusMessage.IndexOf("Article price or stock is invalid.", StringComparison.Ordinal) < 0); attempt++)
                await Task.Delay(10);
            Require(vm.NewPurchaseText == "101" && vm.StatusMessage.IndexOf("Article price or stock is invalid.", StringComparison.Ordinal) >= 0,
                "manual history edit did not retain draft and report the existing API range");
            var unchanged = await products.GetDetailsByIdAsync(product.Id);
            Require(unchanged.UnitPrice == long.MaxValue && unchanged.PurchasePrice == 100,
                "rejected history edit changed product prices");
            using (var connection = factory.Open())
                Require(connection.ExecuteScalar<long>("SELECT count(*) FROM product_price_history WHERE barcode='HISTORY64-UI'") == 1,
                    "rejected history edit wrote another history row");
        }

        private static async Task CheckInvalidMixedAsync(string invalid, bool retailInvalid)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var products = new ProductRepository(factory);
            var barcode = "HISTORY-U1-" + (retailInvalid ? "R-" : "P-") + invalid;
            await products.UpsertProductAndMetaInTransactionAsync(
                new Product { Barcode = barcode, Name = "Price editor validation", UnitPrice = 200 },
                "", "", 100, null, "", null, "", 1, ProductWriteOrigin.SupplierImportApply);
            var product = await products.GetByBarcodeAsync(barcode);
            var vm = new ProductPriceHistoryViewModel(product.Id, barcode, product.Name, 200, 100,
                new ProductsWorkflowService(), true);
            var owner = new Window { Width = 1024, Height = 768, ShowInTaskbar = false };
            var dialog = new ProductPriceHistoryDialog(vm);
            try
            {
                owner.Show();
                dialog.Owner = owner;
                dialog.Show();
                await WaitForIdleAsync(vm);
                var retailEditor = FindEditor(dialog, nameof(vm.NewRetailText));
                var purchaseEditor = FindEditor(dialog, nameof(vm.NewPurchaseText));
                retailEditor.Text = retailInvalid ? invalid : "201";
                purchaseEditor.Text = retailInvalid ? "101" : invalid;
                var invalidEditor = retailInvalid ? retailEditor : purchaseEditor;
                invalidEditor.Focus();
                Keyboard.Focus(invalidEditor);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var before = ReadCounts(factory, barcode);
                Require(!vm.ApplyNewPricesCommand.CanExecute(null), "invalid mixed input left apply enabled");
                var boundApply = Descendants(dialog).OfType<Button>().Single(button => ReferenceEquals(button.Command, vm.ApplyNewPricesCommand));
                Require(!boundApply.IsEnabled, "actual apply button accepted invalid mixed input");
                var fieldError = Descendants(dialog).OfType<TextBlock>().Single(block =>
                    block.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path == (retailInvalid ? nameof(vm.NewRetailError) : nameof(vm.NewPurchaseError)));
                Require(fieldError.Text.Contains("2147483647"), "field error did not show the manual limit");
                // Exercise the real bound command even when the corrected UI disables it:
                // commit validation must also reject stale or direct command invocation.
                vm.ApplyNewPricesCommand.Execute(null);
                await WaitForIdleAsync(vm);
                var after = ReadCounts(factory, barcode);
                var actual = await products.GetDetailsByIdAsync(product.Id);
                var issues = new List<string>();
                if (actual.UnitPrice != 200 || actual.PurchasePrice != 100)
                    issues.Add("prices=" + actual.UnitPrice + "/" + actual.PurchasePrice);
                if (before != after) issues.Add("history/outbox=" + before + " -> " + after);
                if (vm.NewRetailText != (retailInvalid ? invalid : "201") || vm.NewPurchaseText != (retailInvalid ? "101" : invalid))
                    issues.Add("draft=" + vm.NewRetailText + "/" + vm.NewPurchaseText);
                if (vm.StatusMessage == Win7POS.Wpf.Localization.PosLocalization.T("priceHistory.pricesUpdated"))
                    issues.Add("false success");
                if (!ReferenceEquals(Keyboard.FocusedElement, invalidEditor)) issues.Add("invalid-field focus lost");
                Require(issues.Count == 0, string.Join(", ", issues));
                invalidEditor.Text = retailInvalid ? "201" : "101";
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(boundApply.IsEnabled && fieldError.Text.Length == 0, "correction did not enable apply and clear field error");
                vm.ApplyNewPricesCommand.Execute(null);
                await WaitForIdleAsync(vm);
                actual = await products.GetDetailsByIdAsync(product.Id);
                Require(actual.UnitPrice == 201 && actual.PurchasePrice == 101 && vm.NewRetailText == "" && vm.NewPurchaseText == "",
                    "correction retry did not apply both exact prices and clear drafts");
                Require(ReferenceEquals(Keyboard.FocusedElement, invalidEditor), "correction retry did not restore editor focus");
            }
            finally { dialog.Close(); owner.Close(); }
        }

        private static async Task CheckValidInputsAsync()
        {
            var fixture = await CreateAsync("HISTORY-U1-VALID");
            foreach (var input in new[]
            {
                new { Retail = "", Purchase = "101", R = 200L, P = 101 },
                new { Retail = "201", Purchase = " ", R = 201L, P = 101 },
                new { Retail = "0", Purchase = "0", R = 0L, P = 0 },
                new { Retail = "2147483647", Purchase = "2.147.483.647", R = (long)int.MaxValue, P = int.MaxValue },
                new { Retail = "1.200", Purchase = "1,200", R = 1200L, P = 1200 },
                new { Retail = "1 201", Purchase = "+1 201", R = 1201L, P = 1201 },
                new { Retail = " 1.202 ", Purchase = "000", R = 1202L, P = 0 }
            })
            {
                var prior = await fixture.Products.GetDetailsByIdAsync(fixture.Id);
                var historyBefore = HistoryCount(fixture.Factory, fixture.Barcode);
                var outboxBefore = OutboxCount(fixture.Factory);
                fixture.Vm.NewRetailText = input.Retail;
                fixture.Vm.NewPurchaseText = input.Purchase;
                Require(fixture.Vm.ApplyNewPricesCommand.CanExecute(null), "valid changed prices disabled apply: " + input.Retail + "/" + input.Purchase);
                fixture.Vm.ApplyNewPricesCommand.Execute(null);
                await WaitForIdleAsync(fixture.Vm);
                var actual = await fixture.Products.GetDetailsByIdAsync(fixture.Id);
                var changes = (prior.UnitPrice == input.R ? 0 : 1) + (prior.PurchasePrice == input.P ? 0 : 1);
                Require(actual.UnitPrice == input.R && actual.PurchasePrice == input.P, "valid input changed price precision");
                Require(HistoryCount(fixture.Factory, fixture.Barcode) == historyBefore + changes && OutboxCount(fixture.Factory) == outboxBefore + changes,
                    "valid input history/outbox was missing or duplicated");
                Require(fixture.Vm.NewRetailText == "" && fixture.Vm.NewPurchaseText == "" && fixture.Vm.StatusMessage == PosLocalization.T("priceHistory.pricesUpdated"),
                    "successful edit did not clear drafts and report completion");
            }
        }

        private static async Task CheckMalformedInputsAsync()
        {
            var fixture = await CreateAsync("HISTORY-U1-MALFORMED");
            var before = ReadCounts(fixture.Factory, fixture.Barcode);
            foreach (var invalid in new[] { "1.2", "1,23", "1..200", "1,200.000", "1 20", "+", "1  200", ".", "9223372036854775807", "9223372036854775808" })
            {
                foreach (var retail in new[] { true, false })
                {
                    fixture.Vm.NewRetailText = retail ? invalid : "201";
                    fixture.Vm.NewPurchaseText = retail ? "101" : invalid;
                    Require(!fixture.Vm.ApplyNewPricesCommand.CanExecute(null), "malformed/overflow input enabled apply: " + invalid);
                    Require((retail ? fixture.Vm.NewRetailError : fixture.Vm.NewPurchaseError).Contains("2147483647"), "invalid input lacked a field range error");
                    fixture.Vm.ApplyNewPricesCommand.Execute(null);
                    await WaitForIdleAsync(fixture.Vm);
                    Require(fixture.Vm.NewRetailText == (retail ? invalid : "201") && fixture.Vm.NewPurchaseText == (retail ? "101" : invalid), "malformed draft lost");
                }
            }
            var actual = await fixture.Products.GetDetailsByIdAsync(fixture.Id);
            Require(actual.UnitPrice == 200 && actual.PurchasePrice == 100 && before == ReadCounts(fixture.Factory, fixture.Barcode), "malformed input wrote data");
        }

        private static async Task CheckNoChangeAsync()
        {
            var fixture = await CreateAsync("HISTORY-U1-NOCHANGE");
            var before = ReadCounts(fixture.Factory, fixture.Barcode);
            foreach (var text in new[] { new[] { "", " " }, new[] { "200", "100" }, new[] { "0200", "" } })
            {
                fixture.Vm.NewRetailText = text[0];
                fixture.Vm.NewPurchaseText = text[1];
                Require(!fixture.Vm.ApplyNewPricesCommand.CanExecute(null), "empty/unchanged input enabled apply");
                fixture.Vm.ApplyNewPricesCommand.Execute(null);
                await WaitForIdleAsync(fixture.Vm);
                Require(fixture.Vm.StatusMessage == PosLocalization.T("priceHistory.noPriceChanges") && fixture.Vm.NewRetailText == text[0] && fixture.Vm.NewPurchaseText == text[1], "no-change simulated success or discarded draft");
            }
            Require(before == ReadCounts(fixture.Factory, fixture.Barcode), "no-change wrote history/outbox");
        }

        private static async Task CheckServiceFailureAsync()
        {
            var fixture = await CreateAsync("HISTORY-U1-SERVICE");
            var owner = new Window { Width = 1024, Height = 768, ShowInTaskbar = false };
            var dialog = new ProductPriceHistoryDialog(fixture.Vm);
            try
            {
                owner.Show(); dialog.Owner = owner; dialog.Show();
                await WaitForIdleAsync(fixture.Vm);
                var retail = FindEditor(dialog, nameof(fixture.Vm.NewRetailText));
                var purchase = FindEditor(dialog, nameof(fixture.Vm.NewPurchaseText));
                retail.Text = "201"; purchase.Text = "101";
                retail.Focus(); Keyboard.Focus(retail);
                var before = ReadCounts(fixture.Factory, fixture.Barcode);
                using (var connection = fixture.Factory.Open())
                    connection.Execute("CREATE TRIGGER fail_u1_price_history BEFORE INSERT ON product_price_history WHEN NEW.barcode='HISTORY-U1-SERVICE' BEGIN SELECT RAISE(ABORT,'u1_service_failure'); END;");
                fixture.Vm.ApplyNewPricesCommand.Execute(null);
                await WaitForIdleAsync(fixture.Vm);
                Require(fixture.Vm.NewRetailText == "201" && fixture.Vm.NewPurchaseText == "101" && fixture.Vm.StatusMessage.Contains("u1_service_failure"), "service failure lost draft or error");
                Require(before == ReadCounts(fixture.Factory, fixture.Barcode), "service failure left partial history/outbox");
                var actual = await fixture.Products.GetDetailsByIdAsync(fixture.Id);
                Require(actual.UnitPrice == 200 && actual.PurchasePrice == 100, "service failure left partial product prices");
                Require(ReferenceEquals(Keyboard.FocusedElement, retail) && fixture.Vm.ApplyNewPricesCommand.CanExecute(null), "service failure lost useful focus or retry");
                using (var connection = fixture.Factory.Open()) connection.Execute("DROP TRIGGER fail_u1_price_history");
                fixture.Vm.ApplyNewPricesCommand.Execute(null);
                await WaitForIdleAsync(fixture.Vm);
                actual = await fixture.Products.GetDetailsByIdAsync(fixture.Id);
                Require(actual.UnitPrice == 201 && actual.PurchasePrice == 101 && HistoryCount(fixture.Factory, fixture.Barcode) == 2,
                    "service retry did not commit exactly once");
            }
            finally
            {
                using (var connection = fixture.Factory.Open()) connection.Execute("DROP TRIGGER IF EXISTS fail_u1_price_history");
                dialog.Close(); owner.Close();
            }
        }

        private static async Task CheckDoubleConfirmAsync()
        {
            var fixture = await CreateAsync("HISTORY-U1-DOUBLE");
            fixture.Vm.NewRetailText = "201"; fixture.Vm.NewPurchaseText = "101";
            var before = OutboxCount(fixture.Factory);
            await CatalogMutationGate.Instance.WaitAsync();
            try
            {
                fixture.Vm.ApplyNewPricesCommand.Execute(null);
                Require(fixture.Vm.IsBusy && !fixture.Vm.CanEditPriceInputs && !fixture.Vm.ApplyNewPricesCommand.CanExecute(null) && !fixture.Vm.RefreshCommand.CanExecute(null),
                    "loading/single-flight did not own the pending mutation");
                Require(fixture.Vm.StatusMessage == PosLocalization.T("priceHistory.savingPrices"), "pending edit did not report loading");
                fixture.Vm.ApplyNewPricesCommand.Execute(null);
                fixture.Vm.RefreshCommand.Execute(null);
                Require(fixture.Vm.IsBusy && HistoryCount(fixture.Factory, fixture.Barcode) == 0 && OutboxCount(fixture.Factory) == before, "duplicate confirm/refresh bypassed the busy operation");
                var dispatched = false;
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => dispatched = true, DispatcherPriority.Input);
                Require(dispatched, "dispatcher could not process input while mutation waited");
            }
            finally { CatalogMutationGate.Instance.Release(); }
            await WaitForIdleAsync(fixture.Vm);
            Require(HistoryCount(fixture.Factory, fixture.Barcode) == 2 && OutboxCount(fixture.Factory) == before + 2 && fixture.Vm.NewRetailText == "" && fixture.Vm.NewPurchaseText == "",
                "double confirm duplicated effects or lost completion");
        }

        private static async Task CheckLocalizationAsync()
        {
            var fixture = await CreateAsync("HISTORY-U1-LANGUAGE");
            var original = PosLocalization.Current.CurrentLanguage;
            var seen = new HashSet<string>();
            var owner = new Window { Width = 1024, Height = 768, ShowInTaskbar = false };
            var dialog = new ProductPriceHistoryDialog(fixture.Vm);
            try
            {
                owner.Show(); dialog.Owner = owner; dialog.Show();
                await WaitForIdleAsync(fixture.Vm);
                foreach (var language in new[] { "en", "es", "it", "zh-CN" })
                {
                    PosLocalization.Current.SetLanguage(language);
                    fixture.Vm.NewRetailText = "abc"; fixture.Vm.NewPurchaseText = "2147483648";
                    var expected = PosLocalization.F("priceHistory.invalidPrice", "2147483647");
                    Require(fixture.Vm.NewRetailError == expected && fixture.Vm.NewPurchaseError == expected && !expected.Contains("priceHistory."), "field error not localized");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    foreach (var element in Descendants(dialog).OfType<FrameworkElement>().Where(element =>
                        element is TextBox || (element is Button button && (button.IsCancel || ReferenceEquals(button.Command, fixture.Vm.ApplyNewPricesCommand)))))
                    {
                        var bounds = element.TransformToAncestor(dialog).TransformBounds(new Rect(element.RenderSize));
                        Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= dialog.ActualWidth && bounds.Bottom <= dialog.ActualHeight,
                            "price inputs/footer CTA are clipped at 1024x768 in " + language);
                    }
                    var renderedErrors = Descendants(dialog).OfType<TextBlock>().Where(block => block.Text == expected).ToArray();
                    Require(renderedErrors.Length == 2 && renderedErrors.All(block => block.ActualHeight >= 16), "localized field error was not rendered by both fields");
                    seen.Add(expected);
                }
                Require(seen.Count == 4, "four language errors fell back to one translation");
            }
            finally { dialog.Close(); owner.Close(); PosLocalization.Current.SetLanguage(original); }
        }

        private static async Task<Fixture> CreateAsync(string barcode)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var products = new ProductRepository(factory);
            await products.UpsertProductAndMetaInTransactionAsync(new Product { Barcode = barcode, Name = "Price editor", UnitPrice = 200 },
                "", "", 100, null, "", null, "", 1, ProductWriteOrigin.SupplierImportApply);
            var product = await products.GetByBarcodeAsync(barcode);
            var vm = new ProductPriceHistoryViewModel(product.Id, barcode, product.Name, 200, 100, new ProductsWorkflowService(), true);
            await vm.LoadAsync();
            return new Fixture { Factory = factory, Products = products, Vm = vm, Id = product.Id, Barcode = barcode };
        }

        private sealed class Fixture
        {
            internal SqliteConnectionFactory Factory;
            internal ProductRepository Products;
            internal ProductPriceHistoryViewModel Vm;
            internal long Id;
            internal string Barcode;
        }

        private static long HistoryCount(SqliteConnectionFactory factory, string barcode)
        {
            using (var connection = factory.Open()) return connection.ExecuteScalar<long>("SELECT count(*) FROM product_price_history WHERE barcode=@barcode", new { barcode });
        }

        private static long OutboxCount(SqliteConnectionFactory factory)
        {
            using (var connection = factory.Open()) return connection.ExecuteScalar<long>("SELECT count(*) FROM article_mutation_outbox");
        }

        private static string ReadCounts(SqliteConnectionFactory factory, string barcode)
        {
            using (var connection = factory.Open())
                return connection.ExecuteScalar<long>("SELECT count(*) FROM product_price_history WHERE barcode=@barcode", new { barcode })
                    + "/" + connection.ExecuteScalar<long>("SELECT count(*) FROM article_mutation_outbox");
        }

        private static async Task WaitForIdleAsync(ProductPriceHistoryViewModel vm)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            for (var attempt = 0; attempt < 200 && vm.IsBusy; attempt++) await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!vm.IsBusy, "price editor remained busy");
        }

        private static TextBox FindEditor(DependencyObject root, string path)
            => Descendants(root).OfType<TextBox>().Single(box =>
                box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == path);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
        }

        private static async Task CheckAsync(List<string> lines, List<string> failures, string name, Func<Task> run)
        {
            string result;
            try { await run(); result = name + "=PASS"; }
            catch (Exception ex) { result = name + "=FAIL " + ex.Message; failures.Add(result); }
            lines.Add(result);
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "price-history-completion.txt"), result + Environment.NewLine);
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("price_history_completion: " + message);
        }
    }
}
