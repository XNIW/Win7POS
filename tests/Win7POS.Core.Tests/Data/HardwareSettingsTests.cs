using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Hardware;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class HardwareSettingsTests
{
    [TestMethod]
    public async Task LegacyWidthDrawerAndAliasesRoundTripInOneAuditedGroup()
    {
        await WithDatabase(async factory =>
        {
            var settings = new SettingsRepository(factory);
            await settings.SetStringsAsync(new Dictionary<string, string> { ["pos.useReceipt42"] = "0", ["pos.cashdrawer.enabled"] = "1", ["printer.name"] = "LOCAL QUEUE CANARY", ["printer.cashDrawerCommand"] = CashDrawerPresetPolicy.Pin5Command });
            var repository = new HardwareSettingsRepository(settings);
            var model = await repository.LoadAsync();
            Assert.AreEqual(ReceiptProfile.Thermal58mm32col, model.ReceiptProfile);
            Assert.AreEqual("printer_kick", model.CashDrawerMode);
            Assert.AreEqual(CashDrawerPreset.EscposPin5, model.CashDrawerPreset);
            var id = Guid.NewGuid().ToString("N");
            Assert.IsTrue(await repository.SaveAsync(model, "fixture", () => { }, id));
            Assert.IsFalse(await repository.SaveAsync(model, "fixture", () => { }, id));
            var values = await settings.GetStringsAsync(HardwareSettingsRepository.Keys);
            Assert.AreEqual("thermal_58mm_32col", values["pos.printer.receipt.profile"]);
            Assert.AreEqual("0", values["pos.useReceipt42"]);
            Assert.AreEqual(CashDrawerPresetPolicy.Pin5Command, values["pos.cashdrawer.command"]);
            using var conn = factory.Open();
            Assert.AreEqual(1L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM settings_audit"));
            var audit = string.Join(" ", conn.Query<string>("SELECT actor || source || key_names || before_hash || after_hash FROM settings_audit"));
            Assert.IsFalse(audit.Contains("LOCAL QUEUE CANARY"));
            Assert.IsFalse(audit.Contains(CashDrawerPresetPolicy.Pin5Command));
        });
    }

    [TestMethod]
    public async Task InvalidHardwareFailsClosedAndCanonicalModeOverridesAlias()
    {
        await WithDatabase(async factory =>
        {
            var settings = new SettingsRepository(factory);
            await settings.SetStringsAsync(new Dictionary<string, string>
            {
                ["pos.cashdrawer.mode"] = "disabled", ["pos.cashdrawer.enabled"] = "1", ["pos.printer.receipt.enabled"] = "1",
                ["pos.printer.receipt.auto_print_after_sale"] = "1", ["pos.printer.receipt.copies"] = "999", ["pos.scanner.keyboard_wedge.max_length"] = "0"
            });
            var model = await new HardwareSettingsRepository(settings).LoadAsync();
            Assert.AreEqual("disabled", model.CashDrawerMode);
            Assert.IsFalse(model.ReceiptEnabled);
            Assert.IsFalse(model.AutoPrint);
            Assert.AreEqual(1, model.Copies);
            Assert.AreEqual(128, model.Scanner.MaximumLength);
        });
    }

    [TestMethod]
    public async Task AuditFailureAndPermissionRevocationRollBackEveryHardwareKey()
    {
        await WithDatabase(async factory =>
        {
            var repository = new HardwareSettingsRepository(factory);
            var model = new HardwareSettings { PrinterName = "first" };
            await repository.SaveAsync(model, "fixture", () => { });
            using (var conn = factory.Open()) conn.Execute("CREATE TRIGGER audit_failure BEFORE INSERT ON settings_audit BEGIN SELECT RAISE(ABORT,'fixture'); END;");
            model.PrinterName = "second"; model.Copies = 3; model.Scanner.Terminator = ScannerTerminator.Tab;
            await Assert.ThrowsExactlyAsync<SqliteException>(() => repository.SaveAsync(model, "fixture", () => { }));
            using (var conn = factory.Open()) conn.Execute("DROP TRIGGER audit_failure");
            var calls = 0;
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => repository.SaveAsync(model, "fixture", () => { if (++calls >= 3) throw new UnauthorizedAccessException("fixture revoked"); }));
            var loaded = await repository.LoadAsync();
            Assert.AreEqual("first", loaded.PrinterName);
            Assert.AreEqual(1, loaded.Copies);
            Assert.AreEqual(ScannerTerminator.Enter, loaded.Scanner.Terminator);
            using var check = factory.Open();
            Assert.AreEqual(1L, check.ExecuteScalar<long>("SELECT COUNT(*) FROM settings_audit"));
        });
    }

    [TestMethod]
    public async Task ConcurrentReadersNeverMixScannerReceiptAndDrawerGenerations()
    {
        await WithDatabase(async factory =>
        {
            var repository = new HardwareSettingsRepository(factory);
            var model = new HardwareSettings();
            await repository.SaveAsync(model, "fixture", () => { });
            var writer = Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    var alternate = i % 2 == 1;
                    var update = new HardwareSettings { ReceiptProfile = alternate ? ReceiptProfile.Thermal58mm32col : ReceiptProfile.Thermal80mm42col, CashDrawerPreset = alternate ? CashDrawerPreset.EscposPin5 : CashDrawerPreset.EscposPin2 };
                    update.Scanner.Terminator = alternate ? ScannerTerminator.Tab : ScannerTerminator.Enter;
                    await repository.SaveAsync(update, "fixture", () => { });
                }
            });
            for (var i = 0; i < 30; i++)
            {
                var read = await repository.LoadAsync();
                var alternate = read.Scanner.Terminator == ScannerTerminator.Tab;
                Assert.AreEqual(alternate ? ReceiptProfile.Thermal58mm32col : ReceiptProfile.Thermal80mm42col, read.ReceiptProfile);
                Assert.AreEqual(alternate ? CashDrawerPreset.EscposPin5 : CashDrawerPreset.EscposPin2, read.CashDrawerPreset);
            }
            await writer;
        });
    }

    private static async Task WithDatabase(Func<SqliteConnectionFactory, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "Win7POS-Hardware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            await test(new SqliteConnectionFactory(options));
        }
        finally { SqliteConnectionFactory.ClearAllPools(); Directory.Delete(root, true); }
    }
}
