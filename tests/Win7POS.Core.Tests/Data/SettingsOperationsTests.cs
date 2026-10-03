using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Operations;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Operations;
using Win7POS.Data.Repositories;
using Win7POS.Data.Backup;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SettingsOperationsTests
{
    [TestMethod]
    public async Task ExportPreservesManualVirtualConsentWithPhysicalAutomaticPrinting()
    {
        using var fixture = new Fixture();
        await fixture.Repository.SetStringsAsync(new Dictionary<string, string>
        {
            ["pos.printer.receipt.enabled"] = "1",
            ["pos.printer.receipt.auto_print_after_sale"] = "1",
            ["pos.printer.receipt.allow_virtual_printers"] = "1",
            ["pos.printer.receipt.name"] = "QA_PHYSICAL_QUEUE"
        });
        var profile = PortableSettingsProfile.Parse(await fixture.Service.ExportAsync("1.2.3.4"));
        Assert.AreEqual("true", profile.Settings["pos.printer.receipt.allow_virtual_printers"]);
        Assert.AreEqual("true", profile.Settings["pos.printer.receipt.auto_print_after_sale"]);
        Assert.IsFalse(profile.Settings.ContainsKey("pos.printer.receipt.name"));
    }

    [TestMethod]
    public async Task ExportNeverContainsSecretPathDeviceOrUnknownCanaries()
    {
        using var fixture = new Fixture();
        var canaries = new Dictionary<string, string>
        {
            ["pos.online.token"] = "SECRET_CANARY_9137",
            ["pos.printer.receipt.name"] = "QUEUE_CANARY_8276",
            ["pos.cashdrawer.printer_name"] = "DEVICE_CANARY_7289",
            ["pos.operations.backup.destination.path"] = "C:\\PATH_CANARY_5298",
            ["pos.customer_display.branding.logo_hash"] = "LOGO_CANARY_3462",
            ["unknown_CANARY_key"] = "UNKNOWN_CANARY_1234"
        };
        await fixture.Repository.SetStringsAsync(canaries);
        var bytes = await fixture.Service.ExportAsync("1.2.3.4");
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var canary in canaries.Values) Assert.IsFalse(text.Contains(canary, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("unknown_CANARY_key", StringComparison.Ordinal));
        var parsed = PortableSettingsProfile.Parse(bytes);
        Assert.AreEqual(PortableSettingsPolicy.Keys.Count, parsed.Settings.Count);
        Assert.AreEqual("1.2.3.4", parsed.ApplicationVersion);
        var audits = await fixture.Service.GetAuditAsync();
        Assert.AreEqual("SettingsProfileExport", audits[0].Event);
        Assert.AreEqual(PortableSettingsPolicy.Keys.Count, audits[0].KeyCount);
        var hash = PortableSettingsPolicy.SafeHash(canaries);
        var changed = canaries.ToDictionary(x => x.Key, x => x.Value + "changed");
        Assert.AreEqual(hash, PortableSettingsPolicy.SafeHash(changed), "Redacted values must not influence published digests.");
    }

    [TestMethod]
    public void ParserRejectsAlteredDuplicateUnknownOversizeDepthAndInvalidUtf8()
    {
        var json = PortableSettingsProfile.Create("1.2.3.4", DateTime.UtcNow, PortableSettingsPolicy.Defaults(SettingsDefaultsScope.AllPortable)).ToJson();
        var invalid = new[]
        {
            json.Replace("\"applicationVersion\":\"1.2.3.4\"", "\"applicationVersion\":\"2.2.3.4\"", StringComparison.Ordinal),
            json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            json.Replace("\"settings\":{", "\"settings\":{\"unknown\":\"value\",", StringComparison.Ordinal),
            json.Replace("\"settings\":{", "\"settings\":{\"ui.language\":\"en\",", StringComparison.Ordinal),
            "{\"x\":{\"x\":{\"x\":{\"x\":{\"x\":1}}}}}",
            json + "garbage"
        };
        foreach (var text in invalid) Assert.ThrowsExactly<ArgumentException>(() => PortableSettingsProfile.Parse(Encoding.UTF8.GetBytes(text)));
        Assert.ThrowsExactly<ArgumentException>(() => PortableSettingsProfile.Parse(new byte[PortableSettingsPolicy.MaximumFileBytes + 1]));
        Assert.ThrowsExactly<ArgumentException>(() => PortableSettingsProfile.Parse(new byte[] { 0xff, 0xfe }));
    }

    [TestMethod]
    public void PolicyRejectsRangesDependenciesAndKeepsCanonicalLegacyMappings()
    {
        var values = PortableSettingsPolicy.Defaults(SettingsDefaultsScope.AllPortable).ToDictionary(x => x.Key, x => x.Value);
        values["pos.scanner.keyboard_wedge.min_length"] = "128";
        values["pos.scanner.keyboard_wedge.max_length"] = "1";
        Assert.ThrowsExactly<ArgumentException>(() => PortableSettingsPolicy.Validate(values));
        values["pos.scanner.keyboard_wedge.max_length"] = "128";
        values["pos.printer.receipt.copies"] = "4";
        Assert.ThrowsExactly<ArgumentException>(() => PortableSettingsPolicy.Validate(values));
        var legacy = PortableSettingsPolicy.Snapshot(new Dictionary<string, string> { ["pos.useReceipt42"] = "0", ["printer.copies"] = "3", ["pos.customer_display.show_barcode"] = "1" });
        Assert.AreEqual("thermal_58mm_32col", legacy["pos.printer.receipt.profile"]);
        Assert.AreEqual("3", legacy["pos.printer.receipt.copies"]);
        Assert.AreEqual("full", legacy["pos.customer_display.privacy.barcode_mode"]);
        Assert.IsFalse(legacy.ContainsKey("pos.customer_display.show_barcode"));
    }

    [TestMethod]
    public async Task ImportRequiresPreviewConfirmationAndPermissionAndDoesNotArmHardware()
    {
        using var fixture = new Fixture();
        var settings = PortableSettingsPolicy.Defaults(SettingsDefaultsScope.AllPortable).ToDictionary(x => x.Key, x => x.Value);
        settings["pos.printer.receipt.enabled"] = "true";
        settings["pos.printer.receipt.auto_print_after_sale"] = "true";
        settings["pos.cashdrawer.mode"] = "printer_kick";
        settings["pos.printer.receipt.copies"] = "3";
        settings["pos.operations.backup.schedule"] = "daily";
        var bytes = Bytes(settings);
        var preview = await fixture.Service.PreviewImportAsync(bytes);
        Assert.IsTrue(preview.SafetyAdjustments.Contains("hardware_activation_local"));
        Assert.IsTrue(preview.SafetyAdjustments.Contains("backup_target_invalid"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.ImportAsync(preview, false));
        fixture.Permitted = false;
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => fixture.Service.ImportAsync(preview, true));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => fixture.Service.ExportAsync("1.2.3.4"));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => fixture.Service.RestoreDefaultsAsync(SettingsDefaultsScope.Hardware, true));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => fixture.Service.GetAuditAsync());
        fixture.Permitted = true;
        Assert.IsTrue(await fixture.Service.ImportAsync(preview, true));
        Assert.AreEqual("false", await fixture.Repository.GetStringAsync("pos.printer.receipt.enabled"));
        Assert.AreEqual("false", await fixture.Repository.GetStringAsync("pos.printer.receipt.auto_print_after_sale"));
        Assert.AreEqual("disabled", await fixture.Repository.GetStringAsync("pos.cashdrawer.mode"));
        Assert.AreEqual("disabled", await fixture.Repository.GetStringAsync("pos.operations.backup.schedule"));
        Assert.AreEqual("3", await fixture.Repository.GetStringAsync("printer.copies"));
    }

    [TestMethod]
    public async Task DoubleImportCommitsOneAuditAndOneLiveGeneration()
    {
        using var fixture = new Fixture();
        var settings = PortableSettingsPolicy.Defaults(SettingsDefaultsScope.AllPortable).ToDictionary(x => x.Key, x => x.Value);
        settings["pos.scanner.keyboard_wedge.terminator"] = "tab";
        settings["pos.printer.receipt.copies"] = "3";
        var preview = await fixture.Service.PreviewImportAsync(Bytes(settings));
        var notifications = 0;
        var snapshots = new List<IReadOnlyDictionary<string, string>>();
        EventHandler<SettingsCommittedEventArgs> handler = (_, change) =>
        {
            if (change.DatabasePath != fixture.Factory.DbPath) return;
            Interlocked.Increment(ref notifications);
            snapshots.Add(fixture.Repository.GetStringsAsync(new[] { "pos.printer.receipt.copies", "printer.copies", "pos.scanner.keyboard_wedge.terminator" }).GetAwaiter().GetResult());
        };
        SettingsRepository.SettingsChanged += handler;
        try
        {
            var results = await Task.WhenAll(fixture.Service.ImportAsync(preview, true), fixture.Service.ImportAsync(preview, true));
            Assert.AreEqual(1, results.Count(x => x));
            Assert.AreEqual(1, notifications);
            Assert.AreEqual("tab", snapshots.Single()["pos.scanner.keyboard_wedge.terminator"]);
            Assert.AreEqual("3", snapshots.Single()["pos.printer.receipt.copies"]);
            Assert.AreEqual("3", snapshots.Single()["printer.copies"]);
            Assert.AreEqual(1, (await fixture.Service.GetAuditAsync()).Count(x => x.Event == "SettingsProfileImport"));
        }
        finally { SettingsRepository.SettingsChanged -= handler; }
    }

    [TestMethod]
    public async Task MidImportAndAuditFailureRollbackSettingsAuditAndBackupActivation()
    {
        using var fixture = new Fixture();
        await fixture.Repository.SetStringAsync("pos.printer.receipt.copies", "1");
        var settings = PortableSettingsPolicy.Defaults(SettingsDefaultsScope.AllPortable).ToDictionary(x => x.Key, x => x.Value);
        settings["pos.printer.receipt.copies"] = "3";
        var preview = await fixture.Service.PreviewImportAsync(Bytes(settings));
        await fixture.Service.ExportAsync("1.2.3.4");
        using (var connection = fixture.Factory.Open()) connection.Execute("CREATE TRIGGER audit_fail BEFORE INSERT ON settings_audit WHEN NEW.event='SettingsProfileImport' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.Service.ImportAsync(preview, true));
        Assert.AreEqual("1", await fixture.Repository.GetStringAsync("pos.printer.receipt.copies"));
        Assert.IsNull(await fixture.Repository.GetStringAsync("printer.copies"));
        Assert.AreEqual(0, (await fixture.Service.GetAuditAsync()).Count(x => x.Event == "SettingsProfileImport"));
        using (var connection = fixture.Factory.Open())
        {
            Assert.AreEqual(0L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM backup_automation_audit WHERE event='settings_imported'"));
            connection.Execute("DROP TRIGGER audit_fail; CREATE TRIGGER setting_fail BEFORE INSERT ON app_settings WHEN NEW.key='pos.printer.receipt.profile' BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        }
        await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.Service.ImportAsync(preview, true));
        Assert.AreEqual("1", await fixture.Repository.GetStringAsync("pos.printer.receipt.copies"));
        Assert.IsNull(await fixture.Repository.GetStringAsync("pos.scanner.keyboard_wedge.terminator"));
    }

    [TestMethod]
    public async Task StalePreviewAndPermissionRevocationInsideTransactionFailClosed()
    {
        using var fixture = new Fixture();
        var preview = await fixture.Service.PreviewImportAsync(Bytes(PortableSettingsPolicy.Defaults(SettingsDefaultsScope.AllPortable)));
        await fixture.Repository.SetStringAsync("pos.printer.receipt.name", "renamed_local_queue");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Service.ImportAsync(preview, true));
        var checks = 0;
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => fixture.Repository.SetStringsAuditedAsync(
            new Dictionary<string, string> { ["ui.language"] = "it" }, "HardwareSettingsUpdate", "1", "fixture",
            () => { if (++checks == 3) throw new UnauthorizedAccessException(); }));
        Assert.IsNull(await fixture.Repository.GetStringAsync("ui.language"));
        Assert.AreEqual(0, (await fixture.Service.GetAuditAsync()).Count);
    }

    [TestMethod]
    public async Task DefaultsAreScopedFailClosedAndPreserveIdentityAndBusinessData()
    {
        using var fixture = new Fixture();
        await fixture.Repository.SetStringsAsync(new Dictionary<string, string>
        {
            ["pos.printer.receipt.enabled"] = "true", ["pos.cashdrawer.mode"] = "printer_kick",
            ["pos.operations.backup.schedule"] = "daily", ["pos.online.token"] = "preserve-session",
            ["pos.official_shop.id"] = "preserve-shop", ["pos.catalog.proof"] = "preserve-catalog",
            ["pos.operations.backup.destination.path"] = "preserve-target", ["ui.language"] = "it"
        });
        await fixture.Service.RestoreDefaultsAsync(SettingsDefaultsScope.Hardware, true);
        Assert.AreEqual("false", await fixture.Repository.GetStringAsync("pos.printer.receipt.enabled"));
        Assert.AreEqual("disabled", await fixture.Repository.GetStringAsync("pos.cashdrawer.mode"));
        Assert.AreEqual("daily", await fixture.Repository.GetStringAsync("pos.operations.backup.schedule"));
        Assert.AreEqual("it", await fixture.Repository.GetStringAsync("ui.language"));
        await fixture.Service.RestoreDefaultsAsync(SettingsDefaultsScope.AllPortable, true);
        Assert.AreEqual("disabled", await fixture.Repository.GetStringAsync("pos.operations.backup.schedule"));
        Assert.AreEqual("false", await fixture.Repository.GetStringAsync("pos.customer_display.enabled"));
        Assert.AreEqual("preserve-session", await fixture.Repository.GetStringAsync("pos.online.token"));
        Assert.AreEqual("preserve-shop", await fixture.Repository.GetStringAsync("pos.official_shop.id"));
        Assert.AreEqual("preserve-catalog", await fixture.Repository.GetStringAsync("pos.catalog.proof"));
        Assert.AreEqual("preserve-target", await fixture.Repository.GetStringAsync("pos.operations.backup.destination.path"));
    }

    [TestMethod]
    public async Task ConcurrentReadersCannotSeeMixedReceiptAliases()
    {
        using var fixture = new Fixture();
        await fixture.Repository.SetStringsAsync(new Dictionary<string, string> { ["pos.printer.receipt.copies"] = "1", ["printer.copies"] = "1" });
        using var start = new ManualResetEventSlim(false);
        var writer = Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < 25; i++)
            {
                var value = (i % 3 + 1).ToString();
                await fixture.Repository.SetStringsAuditedAsync(new Dictionary<string, string> { ["pos.printer.receipt.copies"] = value, ["printer.copies"] = value }, "HardwareSettingsUpdate", "1", "fixture", () => { });
            }
        });
        var reader = Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < 60; i++)
            {
                var snapshot = await fixture.Repository.GetStringsAsync(new[] { "pos.printer.receipt.copies", "printer.copies" });
                Assert.AreEqual(snapshot["pos.printer.receipt.copies"], snapshot["printer.copies"]);
            }
        });
        start.Set();
        await Task.WhenAll(writer, reader);
        Assert.AreEqual(25, (await fixture.Service.GetAuditAsync()).Count);
    }

    [TestMethod]
    public async Task VersionObservationAuditsFirstUpgradeDowngradeAndIgnoresNoop()
    {
        using var fixture = new Fixture();
        Assert.IsTrue(await SettingsOperationsService.ObserveApplicationVersionAsync(fixture.Factory, "1.0.0.0"));
        Assert.IsFalse(await SettingsOperationsService.ObserveApplicationVersionAsync(fixture.Factory, "1.0.0.0"));
        Assert.IsTrue(await SettingsOperationsService.ObserveApplicationVersionAsync(fixture.Factory, "2.0.0.0"));
        Assert.IsTrue(await SettingsOperationsService.ObserveApplicationVersionAsync(fixture.Factory, "1.5.0.0"));
        var audits = await fixture.Service.GetAuditAsync();
        CollectionAssert.AreEqual(new[] { "downgrade", "upgrade", "first_observation" }, audits.Select(x => x.Source).ToArray());
        Assert.AreEqual("1.5.0.0", await fixture.Repository.GetStringAsync(PortableSettingsPolicy.LastSeenVersionKey));
    }

    [TestMethod]
    public async Task BackupPolicyPublicServiceChecksPermissionAndAuditsWithoutDestinationDigest()
    {
        using var fixture = new Fixture();
        var destination = Path.Combine(Path.GetDirectoryName(fixture.Factory.DbPath)!, "backups");
        var backup = BackupAutomationService.GetOrCreate(fixture.Factory, destination);
        var options = new BackupAutomationOptions();
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => backup.SaveOptionsAsync(options, "42", () => throw new UnauthorizedAccessException()));
        var checks = 0;
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => backup.SaveOptionsAsync(options, "42", () => { if (++checks == 4) throw new UnauthorizedAccessException(); }));
        Assert.IsNull(await fixture.Repository.GetStringAsync("pos.operations.backup.schedule"));
        Assert.AreEqual(0, (await fixture.Service.GetAuditAsync()).Count);
        await backup.SaveOptionsAsync(options, "42", () => { });
        Assert.AreEqual("BackupPolicyUpdate", (await fixture.Service.GetAuditAsync()).Single().Event);
        var firstHash = (await fixture.Service.GetAuditAsync()).Single().AfterHash;
        options.DestinationKind = "custom_local";
        options.DestinationPath = Path.Combine(Path.GetDirectoryName(fixture.Factory.DbPath)!, "OTHER_DESTINATION_CANARY");
        await backup.SaveOptionsAsync(options, "42", () => { });
        var audit = (await fixture.Service.GetAuditAsync()).First();
        Assert.AreEqual(firstHash, audit.AfterHash);
        Assert.IsFalse((audit.KeyNames + audit.AfterHash + audit.BeforeHash).Contains("OTHER_DESTINATION_CANARY", StringComparison.Ordinal));
    }

    private static byte[] Bytes(IReadOnlyDictionary<string, string> settings) => Encoding.UTF8.GetBytes(PortableSettingsProfile.Create("1.2.3.4", DateTime.UtcNow, settings).ToJson());
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Win7POS-Operations-" + Guid.NewGuid().ToString("N"));
        internal SqliteConnectionFactory Factory { get; }
        internal SettingsRepository Repository { get; }
        internal SettingsOperationsService Service { get; }
        internal bool Permitted { get; set; } = true;
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var options = PosDbOptions.ForPath(Path.Combine(_root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            Factory = new SqliteConnectionFactory(options);
            Repository = new SettingsRepository(Factory);
            Service = new SettingsOperationsService(Factory, Path.Combine(_root, "backups"), code => Permitted && (code == PermissionCodes.DbBackup || code == PermissionCodes.DbMaintenance), "42");
        }
        public void Dispose() { SqliteConnectionFactory.ClearAllPools(); Directory.Delete(_root, true); }
    }
}
