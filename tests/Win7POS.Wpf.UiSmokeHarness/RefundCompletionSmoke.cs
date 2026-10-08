using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Printing;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class RefundCompletionSmoke
    {
        internal static async Task RunAsync()
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var users = new UserRepository(factory);
            var response = AuthorizationLeaseWpfSmoke.BuildResponse(true);
            await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
            {
                Credential = "2468", CredentialVersion = response.Staff.CredentialVersion,
                DisplayName = response.Staff.DisplayName, RemoteRoleKey = response.Staff.RoleKey,
                RemoteShopId = response.Shop.ShopId, RemoteStaffId = response.Staff.StaffId,
                ShopCode = response.Shop.ShopCode, StaffCode = response.Staff.StaffCode
            });
            var store = new PosTrustedDeviceStore();
            store.SaveFirstLogin(response, "qa-refund-" + Guid.NewGuid().ToString("N"));
            await AuthorizationLeaseWpfSmoke.SeedCatalogSaleSafetyAsync(factory);
            var username = await users.FindTrustedRemoteStaffUsernameAsync(response.Shop.ShopId, response.Shop.ShopCode,
                response.Staff.StaffId, response.Staff.StaffCode, response.Staff.CredentialVersion);
            var op = new OperatorSession(users, new SecurityRepository(factory), new PosOfflineAuthorizationLeaseGuard(store, () => DateTimeOffset.UtcNow));
            Require(await op.LoginAsync(username, "2468") == LoginResult.Success, "refund fixture login failed");
            var safeStart = typeof(Win7POS.Wpf.App).GetProperty("IsSafeStart", BindingFlags.Static | BindingFlags.NonPublic);
            var setter = safeStart.GetSetMethod(true);
            var priorSafeStart = (bool)safeStart.GetValue(null);
            setter.Invoke(null, new object[] { false });
            try
            {
                for (var mode = 0; mode < 4; mode++)
                {
                    var service = new PosWorkflowService();
                    var printer = new FixturePrinter { Fail = mode == 1 };
                    typeof(PosWorkflowService).GetField("_receiptPrinter", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, printer);
                    var inventory = SetPendingInventory(service);
                    var configure = service.SetPrinterSettingsAsync(new PosPrinterSettings { PrinterName = "QA_REFUND_PHYSICAL", ReceiptEnabled = true, Copies = 1, CashDrawerMode = "disabled" }, () => { }, "qa_fixture");
                    CompleteInventory(inventory);
                    await configure;
                    if (mode == 0) await new SettingsRepository(factory).SetStringAsync(Win7POS.Wpf.Infrastructure.AppSettingKeys.PosPrinterReceiptName, "MISSING");
                    await service.AddManualPriceAsync(100);
                    var sale = await service.CompleteSaleAsync(new PosPaymentInfo { CashAmountMinor = 100 }, op);
                    var preview = await service.BuildRefundPreviewAsync(sale.SaleId);
                    var grant = op.CaptureReversalAuthorizationGrant();
                    grant = op.ApproveReversalAuthorization(grant, PermissionCodes.PosRefund, true);
                    if (mode == 2) grant = op.ApproveReversalAuthorization(grant, PermissionCodes.PosVoidSale, true);
                    inventory = SetPendingInventory(service);
                    var refund = service.CreateRefundAsync(new RefundCreateRequest
                    {
                        OriginalSaleId = sale.SaleId,
                        IsFullVoid = mode == 2,
                        Lines = new List<RefundLineRequest> { new RefundLineRequest { OriginalLineId = preview.Lines.Single().OriginalLineId, QtyToRefund = 1 } },
                        Payment = new RefundPaymentInfo { CashMinor = 100 }
                    }, true, mode != 3, op, grant);
                    CompleteInventory(inventory);
                    var result = await refund;
                    using (var conn = factory.Open())
                    {
                        Require(conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sales WHERE related_sale_id=@id", new { id = sale.SaleId }) == 1, "refund was not saved exactly once");
                        Require(conn.ExecuteScalar<long?>("SELECT operator_id FROM sales WHERE id=@id", new { id = result.RefundSaleId }) == op.CurrentUser.Id, "refund lost effective operator");
                    }
                    Require(printer.Calls == (mode == 0 || mode == 3 ? 0 : 1), "failure stage was not reached");
                    Require(result.RefundSaleId > 0 && result.TotalMinor == -100, "print failure affected accounting success");
                    Require(result.PrintStatus == (mode < 2 ? RefundPrintStatus.Failed : mode == 2 ? RefundPrintStatus.Accepted : RefundPrintStatus.NotRequested), "incorrect queue result");
                    Require((result.PrintError.Length > 0) == (mode < 2), "lost print error");
                    using (var vm = new PosViewModel(service, permissionService: new ReprintPermission()))
                    {
                        vm.ApplyRefundResult(result);
                        Require(vm.HasRefundReceipt && vm.ReprintRefundCommand.CanExecute(null), "saved refund retry unavailable");
                        Require(vm.StatusMessage.Contains(result.RefundSaleCode), "saved refund status lost identity");
                        if (mode < 2) Require(vm.StatusToastSeverity == Win7POS.Core.Pos.PosNoticeSeverity.Warning, "print failure shown as general success");
                        printer.Fail = false;
                        await new SettingsRepository(factory).SetStringAsync(Win7POS.Wpf.Infrastructure.AppSettingKeys.PosPrinterReceiptName, "QA_REFUND_PHYSICAL");
                        var callsBeforeRetry = printer.Calls;
                        var focusRequests = 0;
                        vm.FocusBarcodeRequested += () => focusRequests++;
                        inventory = SetPendingInventory(service);
                        vm.ReprintRefundCommand.Execute(null);
                        vm.ReprintRefundCommand.Execute(null);
                        CompleteInventory(inventory);
                        await CartPerformanceRegressionSmoke.WaitAsync(() => !vm.IsBusy, "refund retry did not finish");
                        Require(printer.Calls == callsBeforeRetry + 1, "duplicate retry command submitted twice");
                        Require(focusRequests > 0, "refund retry did not return scanner focus");
                        Require(vm.StatusToastSeverity == Win7POS.Core.Pos.PosNoticeSeverity.Success, "successful retry did not update status");
                    }
                    // Reopen and read the persisted document; printing never creates a movement.
                    var reopened = new PosWorkflowService();
                    var reprint = new FixturePrinter();
                    typeof(PosWorkflowService).GetField("_receiptPrinter", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reopened, reprint);
                    await new SettingsRepository(factory).SetStringAsync(Win7POS.Wpf.Infrastructure.AppSettingKeys.PosPrinterReceiptName, "QA_REFUND_PHYSICAL");
                    var originalReceipt = result.Receipt42;
                    Require(await reopened.GetReceiptPreviewBySaleIdAsync(result.RefundSaleId, true) == originalReceipt, "reopen changed persisted document");
                    inventory = SetPendingInventory(reopened);
                    var retry = reopened.PrintReceiptBySaleIdAsync(result.RefundSaleId, true);
                    CompleteInventory(inventory);
                    await retry;
                    Require(reprint.Calls == 1, "reopened refund was not submitted");
                    using (var conn = factory.Open()) Require(conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sales WHERE related_sale_id=@id", new { id = sale.SaleId }) == 1, "reprint duplicated refund");
                }
                await VerifyPartialReturnsAsync(factory, op);
            }
            finally { setter.Invoke(null, new object[] { priorSafeStart }); }
        }

        private static async Task VerifyPartialReturnsAsync(SqliteConnectionFactory factory, OperatorSession op)
        {
            const string barcode = "PARTIAL-HISTORICAL";
            var products = new ProductRepository(factory);
            var product = new Product { Barcode = barcode, Name = "Historical price", UnitPrice = 101 };
            await products.UpsertAsync(product, ProductWriteOrigin.TestFixture);
            using (var conn = factory.Open()) conn.Execute("INSERT INTO product_meta(barcode,stock_qty) VALUES(@barcode,10)", new { barcode });
            var service = new PosWorkflowService();
            await service.AddByBarcodeAsync(barcode);
            await service.SetQtyAsync(barcode, 3);
            var discounted = await service.ApplyCartDiscountPercentAsync(50);
            var sold = await service.CompleteSaleAsync(new PosPaymentInfo { CashAmountMinor = discounted.Total }, op);
            Require(sold.TotalMinor == discounted.Total && sold.TotalMinor == 151, "saved amount differs from established discount rounding");
            product.UnitPrice = 999;
            await products.UpsertAsync(product, ProductWriteOrigin.TestFixture);
            var sales = new SaleRepository(factory);
            var returned = 0L;
            for (var index = 0; index < 3; index++)
            {
                var preview = await service.BuildRefundPreviewAsync(sold.SaleId);
                var line = preview.Lines.Single();
                Require(line.UnitPriceMinor == 101 && line.RemainingQty == 3 - index, "return lost historical price or remaining quantity");
                var amount = -Win7POS.Core.Pos.ReversalEconomicsPolicy.Calculate(
                    await sales.GetReversalEconomicsSnapshotAsync(sold.SaleId), 101).NetClp;
                var grant = op.ApproveReversalAuthorization(op.CaptureReversalAuthorizationGrant(), PermissionCodes.PosRefund, true);
                var result = await service.CreateRefundAsync(new RefundCreateRequest
                {
                    OriginalSaleId = sold.SaleId,
                    Lines = new List<RefundLineRequest> { new RefundLineRequest { OriginalLineId = line.OriginalLineId, QtyToRefund = 1, UnitPriceMinor = 999 } },
                    Payment = new RefundPaymentInfo { CashMinor = amount }
                }, true, false, op, grant);
                returned -= result.TotalMinor;
            }
            Require(returned == sold.TotalMinor, "partial allocation did not return exactly the saved net amount");
            var lastPreview = await service.BuildRefundPreviewAsync(sold.SaleId);
            var excessDenied = false;
            try
            {
                await service.CreateRefundAsync(new RefundCreateRequest
                {
                    OriginalSaleId = sold.SaleId,
                    Lines = new List<RefundLineRequest> { new RefundLineRequest { OriginalLineId = lastPreview.Lines.Single().OriginalLineId, QtyToRefund = 1 } },
                    Payment = new RefundPaymentInfo { CashMinor = 1 }
                }, true, false, op, op.ApproveReversalAuthorization(op.CaptureReversalAuthorizationGrant(), PermissionCodes.PosRefund, true));
            }
            catch (InvalidOperationException) { excessDenied = true; }
            Require(excessDenied, "return exceeded original sold quantity");
            using (var conn = factory.Open())
            {
                Require(conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sales WHERE related_sale_id=@id", new { id = sold.SaleId }) == 3, "excess return persisted");
                Require(conn.ExecuteScalar<long>("SELECT stock_qty FROM product_meta WHERE barcode=@barcode", new { barcode }) == 10, "partial returns did not restore stock once");
            }
        }

        private static TaskCompletionSource<IReadOnlyList<InstalledPrinterInfo>> SetPendingInventory(PosWorkflowService service)
        {
            var pending = new TaskCompletionSource<IReadOnlyList<InstalledPrinterInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(PosWorkflowService).GetField("_printerDiscoveryTask", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, pending.Task);
            return pending;
        }
        private static void CompleteInventory(TaskCompletionSource<IReadOnlyList<InstalledPrinterInfo>> pending) => pending.SetResult(
            new[] { new InstalledPrinterInfo { Name = "QA_REFUND_PHYSICAL", OutputKind = PrinterOutputKind.Physical, IsInventoryFresh = true, IsAvailable = true } });

        private sealed class FixturePrinter : IReceiptPrinter
        {
            internal bool Fail;
            internal int Calls;
            public Task PrintAsync(string text, ReceiptPrintOptions options)
            {
                Calls++;
                if (Fail) throw new InvalidOperationException("injected queue rejection");
                return Task.CompletedTask;
            }
            public Task OpenCashDrawerAsync(ReceiptPrintOptions options) => Task.CompletedTask;
        }
        private sealed class ReprintPermission : IPermissionService
        {
            public bool Has(string code) => code == PermissionCodes.PosReprintReceipt;
            public bool CanOverride(string code) => false;
            public void Demand(string code, string operation)
            {
                if (!Has(code)) throw new InvalidOperationException("fixture permission denied");
            }
        }
        private static void Require(bool value, string reason) => FunctionalCompletionSmoke.Require(value, reason);
    }
}
