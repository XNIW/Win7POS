using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CustomerDisplaySettingsCloseoutTests
{
    [TestMethod]
    public async Task Load_MapsLegacyBarcodeAndInvalidCanonicalModeFailsClosed()
    {
        using var files = new DisplayFiles();
        await files.Generic.SetStringAsync("pos.customer_display.show_barcode", "1");
        Assert.AreEqual(CustomerDisplayBarcodeMode.Full, (await files.Repository.LoadAsync(2)).BarcodeMode);
        await files.Generic.SetStringAsync("pos.customer_display.privacy.barcode_mode", "last4");
        Assert.AreEqual(CustomerDisplayBarcodeMode.Last4, (await files.Repository.LoadAsync(2)).BarcodeMode);
        await files.Generic.SetStringAsync("pos.customer_display.privacy.barcode_mode", "INVALID");
        Assert.AreEqual(CustomerDisplayBarcodeMode.Hidden, (await files.Repository.LoadAsync(2)).BarcodeMode);
        await files.Generic.SetStringAsync("pos.customer_display.idle.message", "<script>private</script>");
        await files.Generic.SetStringAsync("pos.customer_display.branding.logo_file", @"\\fixture\secret.png");
        var safe = await files.Repository.LoadAsync(2);
        Assert.AreEqual("", safe.IdleMessage);
        Assert.AreEqual("", safe.LogoFile);
    }

    [TestMethod]
    public async Task Save_RoundTripsCanonicalSettingsWithRedactedAtomicAuditAndIdempotentOperation()
    {
        using var files = new DisplayFiles();
        var settings = CustomerDisplaySettings.CreateDefault(2);
        settings.Enabled = false;
        settings.BarcodeMode = CustomerDisplayBarcodeMode.Last4;
        settings.IdleMode = CustomerDisplayIdleMode.CustomMessage;
        settings.IdleMessage = "CANARY welcome 欢迎";
        settings.CustomerMonitorDeviceName = "CANARY_DEVICE";
        settings.LogoHash = new string('a', 64);
        settings.LogoFile = "logo_" + settings.LogoHash + ".png";
        settings.ShowPaidAmount = true;
        settings.ShowChangeAmount = false;
        settings.TestPatternDurationSeconds = 60;
        var operation = Guid.NewGuid().ToString("N");
        await files.Repository.SaveAsync(settings, () => { }, "fixture", operation);
        var loaded = await files.Repository.LoadAsync(2);
        Assert.AreEqual(settings.IdleMessage, loaded.IdleMessage);
        Assert.AreEqual(settings.LogoFile, loaded.LogoFile);
        Assert.AreEqual(settings.LogoHash, loaded.LogoHash);
        Assert.AreEqual(CustomerDisplayBarcodeMode.Last4, loaded.BarcodeMode);
        Assert.IsTrue(loaded.ShowPaidAmount); Assert.IsFalse(loaded.ShowChangeAmount);
        Assert.AreEqual(60, loaded.TestPatternDurationSeconds);
        settings.IdleMessage = "ignored duplicate";
        await files.Repository.SaveAsync(settings, () => { }, "fixture", operation);
        Assert.AreEqual(loaded.IdleMessage, (await files.Repository.LoadAsync(2)).IdleMessage);
        var audit = await files.Generic.GetSettingsAuditAsync(() => { });
        Assert.AreEqual(1, audit.Count);
        var serialized = System.Text.Json.JsonSerializer.Serialize(audit);
        Assert.IsFalse(serialized.Contains("CANARY", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(loaded.LogoFile, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Save_DeniesMissingOrRevokedPermissionAndRollsBackAuditFailure()
    {
        using var files = new DisplayFiles();
        var settings = CustomerDisplaySettings.CreateDefault(1);
        settings.IdleMessage = "old";
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => files.Repository.SaveAsync(settings));
        await files.Repository.SaveAsync(settings, () => { });
        settings.IdleMessage = "new";
        var checks = 0;
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => files.Repository.SaveAsync(settings,
            () => { if (++checks == 4) throw new UnauthorizedAccessException("fixture revoked before commit"); }));
        Assert.AreEqual("old", (await files.Repository.LoadAsync(1)).IdleMessage);
        using (var connection = files.Factory.Open())
            connection.Execute("CREATE TRIGGER display_audit_fail BEFORE INSERT ON settings_audit BEGIN SELECT RAISE(ABORT,'fixture audit failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => files.Repository.SaveAsync(settings, () => { }));
        Assert.AreEqual("old", (await files.Repository.LoadAsync(1)).IdleMessage);
        Assert.AreEqual(1, (await files.Generic.GetSettingsAuditAsync(() => { })).Count);
    }

    [TestMethod]
    public async Task ConcurrentReaders_ObserveCompleteDisplayGenerations()
    {
        using var files = new DisplayFiles();
        var settings = CustomerDisplaySettings.CreateDefault(1);
        settings.IdleMessage = "0";
        settings.ShowPaidAmount = false;
        await files.Repository.SaveAsync(settings, () => { });
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            started.SetResult(true);
            for (var i = 1; i <= 20; i++)
            {
                var next = settings.Clone(); next.IdleMessage = i.ToString(); next.ShowPaidAmount = i % 2 == 1;
                await files.Repository.SaveAsync(next, () => { });
            }
        });
        await started.Task;
        for (var read = 0; read < 40; read++)
        {
            var snapshot = await files.Repository.LoadAsync(1);
            Assert.AreEqual(int.Parse(snapshot.IdleMessage) % 2 == 1, snapshot.ShowPaidAmount);
        }
        await writer;
    }

    private sealed class DisplayFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Win7POS-display-closeout-" + Guid.NewGuid().ToString("N"));
        public SqliteConnectionFactory Factory { get; }
        public CustomerDisplaySettingsRepository Repository { get; }
        public SettingsRepository Generic { get; }
        public DisplayFiles()
        {
            var options = PosDbOptions.ForPath(Path.Combine(_root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            Factory = new SqliteConnectionFactory(options);
            Repository = new CustomerDisplaySettingsRepository(Factory);
            Generic = new SettingsRepository(Factory);
        }
        public void Dispose() { SqliteConnectionFactory.ClearAllPools(); Directory.Delete(_root, true); }
    }
}
