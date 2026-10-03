using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Hardware;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Printing;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HardwareSettingsSmoke
    {
        internal static async Task RunAsync()
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var settings = new SettingsRepository(factory);
            var repository = new HardwareSettingsRepository(settings);
            var prior = await repository.LoadAsync();
            var model = new HardwareSettings { ReceiptProfile = ReceiptProfile.Thermal58mm32col };
            model.Scanner.Terminator = ScannerTerminator.EnterOrTab;
            model.Scanner.Prefix = "]C1"; model.Scanner.Suffix = "#";
            await repository.SaveAsync(model, "qa_fixture", () => { });
            var barcode = "001 HW " + Guid.NewGuid().ToString("N");
            using (var conn = factory.Open()) conn.Execute("INSERT INTO products(barcode,name,unitPrice,is_active) VALUES(@barcode,'Hardware scanner fixture',1000,1)", new { barcode });
            var service = new PosWorkflowService();
            try
            {
                using (var dialogVm = new PrinterSettingsViewModel())
                {
                    dialogVm.LoadScannerSettings(model.Scanner);
                    long productsBefore, salesBefore;
                    using (var conn = factory.Open()) { productsBefore = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products"); salesBefore = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"); }
                    dialogVm.CompleteScannerTest("]C1" + barcode + "#", ScannerTerminator.Tab, 12);
                    Require(dialogVm.ScannerNormalized == "", "disarmed scanner test accepted input");
                    dialogVm.ArmScannerTestCommand.Execute(null);
                    dialogVm.CompleteScannerTest("]C1" + barcode + "#", ScannerTerminator.Tab, 12);
                    Require(dialogVm.ScannerNormalized == barcode && !dialogVm.ScannerTestArmed, "armed scanner test did not normalize one scan");
                    dialogVm.CompleteScannerTest("]C1changed#", ScannerTerminator.Enter, 4);
                    Require(dialogVm.ScannerNormalized == barcode, "scanner test accepted a second submit without arming");
                    Require((await service.GetSnapshotAsync()).Lines.Count == 0, "isolated scanner test changed cart");
                    using (var conn = factory.Open())
                    {
                        Require(productsBefore == conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products"), "isolated test changed catalog");
                        Require(salesBefore == conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"), "isolated test changed sales");
                        Require(conn.ExecuteScalar<long>("SELECT COUNT(*) FROM app_settings WHERE value LIKE @barcode", new { barcode = "%" + barcode + "%" }) == 0, "test barcode persisted in settings");
                    }
                    dialogVm.CashDrawerEnabled = true;
                    dialogVm.PrinterName = "QA_DRAWER_A";
                    dialogVm.CashDrawerPrinterName = "";
                    dialogVm.RecordDrawerCommandSent(true);
                    dialogVm.ConfirmDrawerTestCommand.Execute(null);
                    Require(dialogVm.DrawerHealth != PosLocalization.T("hardware.configuredNeedsTest"), "confirmed drawer observation was not recorded");
                    dialogVm.PrinterName = "QA_DRAWER_B";
                    Require(dialogVm.DrawerHealth == PosLocalization.T("hardware.configuredNeedsTest") && !dialogVm.ConfirmDrawerTestCommand.CanExecute(null), "receipt fallback queue inherited a different drawer queue's physical test");
                }
                using (var vm = new PosViewModel(service, permissionService: new FixturePermissionService()))
                {
                    var add = typeof(PosViewModel).GetMethod("AddBarcodeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    vm.BarcodeInput = "]C1" + barcode + "#";
                    var first = (Task)add.Invoke(vm, null);
                    var second = (Task)add.Invoke(vm, null);
                    await Task.WhenAll(first, second);
                    var snapshot = await service.GetSnapshotAsync();
                    Require(snapshot.Lines.Count == 1 && snapshot.Lines[0].Quantity == 1, "one scan submitted twice");
                    long productsBefore;
                    using (var conn = factory.Open()) productsBefore = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products");
                    vm.BarcodeInput = "INVALID MISSING PREFIX";
                    await (Task)add.Invoke(vm, null);
                    using (var conn = factory.Open()) Require(productsBefore == conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products"), "invalid scan reached quick-create");
                    Require((await service.GetSnapshotAsync()).Lines.Single().Quantity == 1, "invalid scan changed cart");
                    vm.BarcodeInput = "2*" + barcode;
                    await (Task)add.Invoke(vm, null);
                    Require((await service.GetSnapshotAsync()).Lines.Single().Quantity == 2, "scanner affixes blocked explicit quantity/barcode command");
                    vm.BarcodeInput = "=3";
                    await (Task)add.Invoke(vm, null);
                    Require((await service.GetSnapshotAsync()).Lines.Single().Quantity == 3, "scanner affixes blocked selected-line quantity command");
                    vm.BarcodeInput = "1500";
                    await (Task)add.Invoke(vm, null);
                    Require((await service.GetSnapshotAsync()).Lines.Any(line => line.Barcode == "MANUAL:1500" && line.UnitPrice == 1500), "scanner affixes blocked existing numeric CLP price command");
                    var updated = model.Copy(); updated.Scanner.Terminator = ScannerTerminator.Tab; updated.ReceiptProfile = ReceiptProfile.Thermal80mm42col;
                    await repository.SaveAsync(updated, "qa_fixture", () => { });
                    await CartPerformanceRegressionSmoke.WaitAsync(() => vm.AcceptsScannerTerminator(ScannerTerminator.Tab) && !vm.AcceptsScannerTerminator(ScannerTerminator.Enter) && vm.UseReceipt42, "hardware live apply did not use committed snapshot");
                    var denied = false;
                    try { await service.SetPrinterSettingsAsync(PosPrinterSettings.FromHardwareSettings(model)); }
                    catch (InvalidOperationException) { denied = true; }
                    Require(denied, "hardware service allowed configuration without authorization");
                }
                await CheckHardwareOutputAuthorizationAsync(service);
            }
            finally { await repository.SaveAsync(prior, "qa_fixture", () => { }); }
        }
        private static async Task CheckHardwareOutputAuthorizationAsync(PosWorkflowService service)
        {
            var safeStart = typeof(Win7POS.Wpf.App).GetProperty("IsSafeStart", BindingFlags.Static | BindingFlags.NonPublic);
            var setter = safeStart?.GetSetMethod(true);
            var receiptField = typeof(PosWorkflowService).GetField("_receiptPrinter", BindingFlags.Instance | BindingFlags.NonPublic);
            var discoveryField = typeof(PosWorkflowService).GetField("_printerDiscoveryTask", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(setter != null && receiptField != null && discoveryField != null, "hardware authorization fixture hooks missing");
            var originalSafeStart = (bool)safeStart.GetValue(null);
            var originalPrinter = receiptField.GetValue(service);
            var printer = new FixtureReceiptPrinter();
            var receiptSettings = new PosPrinterSettings { ReceiptEnabled = true, PrinterName = "QA_AUTH_PHYSICAL" };
            var inventory = new[] { new InstalledPrinterInfo { Name = "QA_AUTH_PHYSICAL", OutputKind = PrinterOutputKind.Physical, IsInventoryFresh = true, IsAvailable = true } };
            try
            {
                // Every output is intercepted; pending inventory prevents native discovery.
                receiptField.SetValue(service, printer);
                setter.Invoke(null, new object[] { false });
                await RequireDeniedAsync(() => service.TestReceiptPrinterAsync(receiptSettings, "fixture", false));
                await RequireDeniedAsync(() => service.TestCashDrawerAsync("QA_AUTH_PHYSICAL", "27,112,0,25,250"));
                for (var drawer = 0; drawer < 2; drawer++)
                {
                    var pendingInventory = new TaskCompletionSource<IReadOnlyList<InstalledPrinterInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    discoveryField.SetValue(service, pendingInventory.Task);
                    var actor = 1;
                    var originalActor = actor;
                    var demands = 0;
                    Action demand = () => { demands++; if (actor != originalActor) throw new InvalidOperationException("fixture operator changed"); };
                    var output = drawer == 0
                        ? service.TestReceiptPrinterAsync(receiptSettings, "fixture", false, demand)
                        : service.TestCashDrawerAsync("QA_AUTH_PHYSICAL", "27,112,0,25,250", demand);
                    Require(demands == 1 && !output.IsCompleted, "hardware test did not await controlled discovery after initial authorization");
                    actor = 2;
                    pendingInventory.SetResult(inventory);
                    await RequireDeniedAsync(() => output);
                    Require(demands == 2 && printer.PrintCalls == 0 && printer.DrawerCalls == 0, "operator change during discovery reached hardware output");
                }
                // A held permission still allows one intercepted effect after discovery.
                var allowedInventory = new TaskCompletionSource<IReadOnlyList<InstalledPrinterInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
                discoveryField.SetValue(service, allowedInventory.Task);
                var allowed = service.TestReceiptPrinterAsync(receiptSettings, "fixture", false, () => { });
                allowedInventory.SetResult(inventory);
                await allowed;
                Require(printer.PrintCalls == 1 && printer.DrawerCalls == 0, "authorized hardware test lost its single effect");
            }
            finally
            {
                receiptField.SetValue(service, originalPrinter);
                setter.Invoke(null, new object[] { originalSafeStart });
            }
        }

        private static async Task RequireDeniedAsync(Func<Task> operation)
        {
            var denied = false;
            try { await operation(); }
            catch (InvalidOperationException) { denied = true; }
            Require(denied, "hardware output service accepted missing or revoked authorization");
        }

        private sealed class FixtureReceiptPrinter : IReceiptPrinter
        {
            public int PrintCalls { get; private set; }
            public int DrawerCalls { get; private set; }
            public Task PrintAsync(string text, ReceiptPrintOptions options) { PrintCalls++; return Task.CompletedTask; }
            public Task OpenCashDrawerAsync(ReceiptPrintOptions options) { DrawerCalls++; return Task.CompletedTask; }
        }

        private static void Require(bool value, string reason) => FunctionalCompletionSmoke.Require(value, reason);
        private sealed class FixturePermissionService : IPermissionService
        {
            public bool Has(string code) => true;
            public void Demand(string code, string operation) { }
            public bool CanOverride(string code) => false;
        }
    }
}
