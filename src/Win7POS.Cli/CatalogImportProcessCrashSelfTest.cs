using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

internal sealed record CatalogImportProcessCrashRequest(string Path, bool Child);

// QA only: the child stops in the existing applier BeforeCommit callback. The
// parent terminates only the Process it started after validating its handshake.
// No application/runtime crash switch, UI process or remote peer is involved.
internal static class CatalogImportProcessCrashSelfTest
{
    private const int RowCount = 1001;
    private const string RootPrefix = "Win7POS.CatalogImportTxCrash.";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string EntryPoint = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);

    internal sealed record Control(string Root, string Scenario, string Nonce, int ParentPid,
        string Database, string EntryPoint, string EntryPointSha256, long OriginalId);
    internal sealed record SavedPart(string ClientImportId, string IdempotencyKey, string PayloadJson, string PayloadHash);
    internal sealed record FileProof(string Path, string Sha256);
    internal sealed record Marker(string Nonce, int ChildPid, int ParentPid, string Database,
        string EntryPoint, string EntryPointSha256, string ProcessPath, string Phase,
        SavedPart[] Parts, FileProof[] Backups);
    internal sealed record TableProof(long Count, string Sha256);
    internal sealed record Snapshot(Dictionary<string, TableProof> Tables);

    public static bool TryParse(string[] args, out CatalogImportProcessCrashRequest request)
    {
        request = null!;
        if (args.Length == 0 || args[0] is not ("--catalog-import-process-crash-selftest" or "--catalog-import-process-crash-child"))
            return false;
        var child = args[0] == "--catalog-import-process-crash-child";
        if (child && args.Length == 2) request = new(args[1], true);
        else if (!child && args.Length == 3 && args[1] == "--output-dir") request = new(args[2], false);
        else throw new ArgumentException("Use --catalog-import-process-crash-selftest --output-dir <new directory>.");
        return true;
    }

    public static async Task RunAsync(CatalogImportProcessCrashRequest request)
    {
        SQLitePCL.Batteries_V2.Init();
        if (request.Child) { await RunChildAsync(request.Path); return; }
        var output = Path.GetFullPath(request.Path);
        Require(!Directory.Exists(output) && !File.Exists(output), "Evidence output must be new.");
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), RootPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Write(Path.Combine(root, "owner.json"), new { parentPid = Environment.ProcessId, entryPoint = EntryPoint, entryPointSha256 = HashFile(EntryPoint) });
        try
        {
            var ordinary = await RunCaseAsync(root, output, "ordinary");
            var recovery = await RunCaseAsync(root, output, "recovery");
            Write(Path.Combine(output, "qualification.json"), new
            {
                schemaVersion = "win7pos-local-sqlite-transaction-process-crash-v1", status = "PASS",
                scope = "real local SQLite; owned net10 CLI processes; never-sent synthetic imports; no network or GUI",
                entryPoint = EntryPoint, entryPointSha256 = HashFile(EntryPoint), root,
                ordinary, recovery, physicalWin7Qualified = false, liveAdminQualified = false,
                budgetsChanged = false, productionHooksAdded = false
            });
            Console.WriteLine("CATALOG_IMPORT_TRANSACTION_CRASH_SELFTEST PASS ordinary=1001 recovery=1001 real_kills=2 partial_effects=0 retry_once=True");
            Console.WriteLine("CRASH_EVIDENCE=" + output);
        }
        catch (Exception ex)
        {
            Write(Path.Combine(output, "failure.json"), new { status = "FAIL", root, error = ex.ToString() });
            throw;
        }
        // Keep owned SQLite/journal/backup artifacts. They are part of the proof,
        // including failed attempts; this runner never deletes an operational DB.
    }

    private static async Task<object> RunCaseAsync(string root, string output, string scenario)
    {
        var directory = Path.Combine(root, scenario);
        Directory.CreateDirectory(directory);
        var caseOutput = Path.Combine(output, scenario);
        Directory.CreateDirectory(caseOutput);
        var database = Path.Combine(directory, "transaction.db");
        var options = PosDbOptions.ForPath(database);
        DbInitializer.EnsureCreated(options);
        var factory = new SqliteConnectionFactory(options);
        using (var connection = factory.Open())
            connection.Execute("INSERT INTO app_settings(key,value) VALUES(@id,'tx-crash-shop'),(@code,'TX-CRASH-SHOP')",
                new { id = OutboxShopBinding.OfficialShopIdKey, code = OutboxShopBinding.OfficialShopCodeKey });
        long originalId = 0;
        var applier = new SupplierExcelImportApplier(factory);
        if (scenario == "ordinary")
        {
            var seed = await applier.ApplyAsync(await applier.BuildPreviewAsync(Rows(1, false)), new SupplierExcelImportApplyOptions());
            Require(seed.Errors == 0 && seed.Inserted == 1, "Ordinary baseline failed.");
        }
        else
        {
            var preview = await applier.BuildPreviewAsync(Rows(RowCount, false));
            preview.OperationCreatedAtUtc = "2026-10-11T00:00:00Z";
            var original = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "qa-never-sent-original.xlsx", "tx-crash-qa");
            var seeded = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = original });
            Require(seeded.Errors == 0 && seeded.Inserted == RowCount, "Recovery baseline failed.");
            originalId = seeded.CatalogImportOutboxId;
            using (var connection = factory.Open())
                connection.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='qa_never_sent_invalid_price' WHERE id=@id", new { id = originalId });
            var service = new CatalogImportRecoveryService(factory, Path.Combine(directory, "backups"));
            var draft = await service.PrepareAsync(originalId, null!, Trusted(), null!, CancellationToken.None);
            Require(draft.Batch.NeverSent && draft.CanCommit, "Recovery must remain never-sent without any remote authorization.");
            ApplyEdits(draft.Rows);
            draft.OperationCreatedAtUtc = "2026-10-11T00:00:01Z";
            await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        }
        var baseline = Capture(factory);
        Write(Path.Combine(caseOutput, "before.json"), baseline);
        var beforeOutbox = SavedOutbox(factory);
        var control = new Control(root, scenario, Guid.NewGuid().ToString("N"), Environment.ProcessId,
            database, EntryPoint, HashFile(EntryPoint), originalId);
        var controlPath = Path.Combine(directory, "child-control.json");
        Write(controlPath, control);
        var start = ChildStartInfo(controlPath);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Child failed to start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        Marker? marker = null;
        int exitCode;
        try
        {
            var markerPath = Path.Combine(directory, "before-commit.json");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(markerPath))
            {
                Require(!child.HasExited, "Child exited before reaching an open transaction.");
                Require(wait.Elapsed < TimeSpan.FromSeconds(45), "Child did not reach BeforeCommit within its QA handshake bound.");
                await Task.Delay(25);
            }
            marker = Read<Marker>(markerPath);
            ValidateMarker(control, marker, child, start.FileName);
            // SQLite DELETE-journal policy requires a live rollback journal here.
            // A normal return or caught exception is not substituted for a kill.
            Require(File.Exists(database + "-journal") && new FileInfo(database + "-journal").Length > 0,
                "Open write transaction has no retained rollback journal.");
            File.Copy(database + "-journal", Path.Combine(caseOutput, "journal-before-kill.bin"));
            child.Kill(); // exact owned Process handle; never kill by process-name/PID discovery
            Require(child.WaitForExit(10000), "Owned child termination not confirmed.");
            exitCode = child.ExitCode;
            Require(exitCode != 0, "Child exited normally instead of being terminated during its transaction.");
        }
        finally
        {
            if (!child.HasExited)
            {
                ValidateOwnedProcess(control, child, start.FileName);
                child.Kill();
                Require(child.WaitForExit(10000), "Owned child cleanup not confirmed.");
            }
            File.WriteAllText(Path.Combine(caseOutput, "child.stdout.txt"), await stdout);
            File.WriteAllText(Path.Combine(caseOutput, "child.stderr.txt"), await stderr);
        }
        File.Copy(Path.Combine(directory, "before-commit.json"), Path.Combine(caseOutput, "before-commit.json"));
        marker = marker ?? throw new InvalidOperationException("Missing marker.");
        var afterKill = Capture(factory); // opens/recover SQLite before taking one consistent read transaction
        Require(Equal(baseline, afterKill), "Killed transaction left partial persisted state.");
        Write(Path.Combine(caseOutput, "after-kill.json"), afterKill);
        Require(SavedOutbox(factory).SequenceEqual(beforeOutbox), "Original outbox identity/payload changed during crash.");
        foreach (var backup in marker.Backups)
        {
            Require(HashFile(backup.Path) == backup.Sha256, "Verified pre-apply backup changed after kill.");
            Require(Equal(baseline, CaptureReadOnly(backup.Path)), "Pre-apply backup does not represent the complete baseline.");
        }
        Require(marker.Backups.Length == 1, "Expected one verified backup before the interrupted commit.");
        SupplierExcelImportApplyResult retried;
        if (scenario == "ordinary")
        {
            var preview = await applier.BuildPreviewAsync(Rows(RowCount, true));
            preview.OperationCreatedAtUtc = "2026-10-11T00:00:01Z";
            var plan = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview, "qa-ordinary.xlsx", "tx-crash-qa");
            Require(Parts(plan).SequenceEqual(marker.Parts), "Ordinary retry changed planned identities or payload bytes.");
            retried = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan = plan });
        }
        else
        {
            var service = new CatalogImportRecoveryService(factory, Path.Combine(directory, "backups"));
            var draft = await service.PrepareAsync(originalId, null!, Trusted(), null!, CancellationToken.None);
            Require(draft.HasSavedDraft && draft.Rows.All(row => row.RetailPrice == "250" && row.PurchasePrice == "125" && row.Quantity == "2.5"),
                "Restart did not preserve the exact unapplied draft.");
            var preview = await service.BuildPreviewAsync(draft, draft.Rows, CancellationToken.None);
            var plan = CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(preview, draft.OriginalRequest, draft.Original.PayloadHash,
                false, draft.Contributions, null!);
            Require(Parts(plan).SequenceEqual(marker.Parts), "Recovery restart changed planned identities or payload bytes.");
            retried = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        }
        Require(retried.Errors == 0 && retried.CatalogImportOutboxIds.Count == 2, "Retry did not apply exactly two saved parts.");
        var afterRetry = Capture(factory);
        Write(Path.Combine(caseOutput, "after-retry.json"), afterRetry);
        using (var connection = factory.Open())
        {
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM products") == RowCount, "Retry product count mismatch.");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta") == RowCount, "Retry metadata count mismatch.");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_plan") == 1 &&
                connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_plan_part") == 2 &&
                connection.ExecuteScalar<long>("SELECT SUM(row_count) FROM catalog_import_plan_part") == RowCount,
                "Retry group/membership coverage is not exactly once.");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox") == (scenario == "ordinary" ? 2 : 3), "Retry outbox count mismatch.");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE unitPrice=250") == RowCount, "Retry retail values mismatch.");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE purchase_price=125") == RowCount, "Retry purchase values mismatch.");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE stock_qty=2.5") == RowCount, "Retry stock was duplicated or missing.");
            Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history") == (scenario == "ordinary" ? 2 + 2 * RowCount : 4 * RowCount),
                "Retry price history duplicated or missing.");
            if (scenario == "recovery")
            {
                Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE stock_qty=2.5") == RowCount, "Recovery stock was applied more than once.");
                Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery_draft") == 0, "Committed recovery retained an unapplied draft.");
                Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery WHERE original_id=@id AND replacement_id IS NOT NULL AND resolved_at IS NULL", new { id = originalId }) == 1,
                    "Recovery root was incorrectly resolved without remote ACKs.");
            }
        }
        var finalParts = SavedOutbox(factory).Where(part => marker.Parts.Any(expected => expected.IdempotencyKey == part.IdempotencyKey)).ToArray();
        Require(finalParts.SequenceEqual(marker.Parts), "Committed parts differ from interrupted intent bytes/hash/identity.");
        foreach (var backup in marker.Backups) Require(HashFile(backup.Path) == backup.Sha256, "Initial backup changed during retry.");
        // A second same-intent apply / recovery commit must leave every table
        // unchanged. Do not manufacture ACKs to release a root.
        if (scenario == "ordinary")
        {
            var preview = await applier.BuildPreviewAsync(Rows(RowCount, true));
            var plan = new CatalogImportOutboxPlan { PlanId = "duplicate-qa-plan", Entries = marker.Parts.Select(ToEntry).ToArray(), TotalRows = RowCount };
            var second = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan = plan });
            Require(second.Errors == 0 && second.NoChange == RowCount && second.CatalogImportOutboxIds.Count == 0,
                "Identical ordinary intent produced another economic/outbox effect.");
        }
        else
        {
            try
            {
                var service = new CatalogImportRecoveryService(factory, Path.Combine(directory, "backups"));
                var second = await service.PrepareAsync(originalId, null!, Trusted(), null!, CancellationToken.None);
                await service.CommitAsync(second, second.Rows, () => true, null!, CancellationToken.None);
                throw new InvalidOperationException("Recovery duplicate unexpectedly committed.");
            }
            catch (CatalogImportRecoveryException) { }
        }
        Require(Equal(afterRetry, Capture(factory)), "Duplicate attempt changed persisted economic/group state.");
        var result = new { status = "PASS", scenario, rows = RowCount, childPid = child.Id, exitCode,
            killedInsideTransaction = true, transactionPhase = marker.Phase, partialPersistedEffects = 0,
            before = baseline, afterKill, afterRetry, originalOutboxUnchanged = true,
            backupPreserved = true, draftPreservedAfterKill = scenario == "recovery", retryAppliedOnce = true,
            partCount = marker.Parts.Length, parts = marker.Parts, remoteAcks = 0, root, database };
        Write(Path.Combine(caseOutput, "qualification.json"), result);
        return result;
    }

    private static async Task RunChildAsync(string controlPath)
    {
        var control = Read<Control>(controlPath);
        ValidateControl(control, controlPath);
        Require(HashFile(EntryPoint) == control.EntryPointSha256 && SamePath(EntryPoint, control.EntryPoint), "Child executable binding mismatch.");
        var directory = Path.Combine(control.Root, control.Scenario);
        var factory = new SqliteConnectionFactory(PosDbOptions.ForPath(control.Database));
        CatalogImportOutboxPlan plan;
        CatalogImportRecoveryDraft? draft = null;
        var service = new CatalogImportRecoveryService(factory, Path.Combine(directory, "backups"));
        var applier = new SupplierExcelImportApplier(factory);
        if (control.Scenario == "ordinary")
        {
            var preview = await applier.BuildPreviewAsync(Rows(RowCount, true));
            preview.OperationCreatedAtUtc = "2026-10-11T00:00:01Z";
            plan = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview, "qa-ordinary.xlsx", "tx-crash-qa");
        }
        else
        {
            draft = await service.PrepareAsync(control.OriginalId, null!, Trusted(), null!, CancellationToken.None);
            Require(draft.Batch.NeverSent && draft.HasSavedDraft, "Child recovery must use the persisted never-sent draft.");
            var preview = await service.BuildPreviewAsync(draft, draft.Rows, CancellationToken.None);
            plan = CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(preview, draft.OriginalRequest, draft.Original.PayloadHash, false, draft.Contributions, null!);
        }
        Require(plan.Entries.Count == 2 && plan.TotalRows == RowCount, "Child plan coverage mismatch.");
        var hooks = new SupplierExcelImportTestHooks { BeforeCommit = () =>
        {
            var backups = Directory.GetFiles(Path.Combine(directory, "backups"), "*.db").Select(path => new FileProof(path, HashFile(path))).ToArray();
            WriteAtomic(Path.Combine(directory, "before-commit.json"), new Marker(control.Nonce, Environment.ProcessId, control.ParentPid,
                control.Database, EntryPoint, HashFile(EntryPoint), Environment.ProcessPath!,
                "after_product_meta_stock_history_outbox_group_membership_writes_before_sqlite_commit", Parts(plan), backups));
            using var barrier = new ManualResetEvent(false);
            barrier.WaitOne(TimeSpan.FromSeconds(60));
            throw new InvalidOperationException("Parent did not terminate the owned QA child; rollback is not a crash PASS.");
        } };
        if (control.Scenario == "ordinary")
        {
            Directory.CreateDirectory(Path.Combine(directory, "backups"));
            await new SqliteOnlineBackup(factory).CreateVerifiedAsync(Path.Combine(directory, "backups", "before-ordinary.db"), CancellationToken.None);
            var preview = await applier.BuildPreviewAsync(Rows(RowCount, true));
            preview.OperationCreatedAtUtc = "2026-10-11T00:00:01Z";
            var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan = plan, TestHooks = hooks });
            throw new InvalidOperationException("Child returned from apply instead of being killed: errors=" + result.Errors);
        }
        else
        {
            service = new CatalogImportRecoveryService(factory, Path.Combine(directory, "backups"), null!, hooks);
            await service.CommitAsync(draft!, draft!.Rows, () => true, null!, CancellationToken.None);
            throw new InvalidOperationException("Recovery child returned instead of being killed.");
        }
    }

    private static void ValidateControl(Control value, string path)
    {
        Require(value.Scenario is "ordinary" or "recovery", "Unknown isolated scenario.");
        var root = Path.GetFullPath(value.Root);
        Require(SamePath(Path.GetDirectoryName(root)!, Path.GetTempPath()) &&
            Path.GetFileName(root).StartsWith(RootPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(Path.GetFileName(root).Substring(RootPrefix.Length), "N", out _), "Child root is not a unique owned temporary directory.");
        Require(SamePath(value.Database, Path.Combine(root, value.Scenario, "transaction.db")) &&
            SamePath(path, Path.Combine(root, value.Scenario, "child-control.json")), "Child paths escaped their exact isolated root.");
        Require(Guid.TryParseExact(value.Nonce, "N", out _) && value.ParentPid > 0 && File.Exists(Path.Combine(root, "owner.json")), "Missing owner handshake.");
        foreach (var directory in new[] { Path.GetFullPath(Path.GetTempPath()), root, Path.Combine(root, value.Scenario) })
            Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "QA root cannot use a redirected/reparse directory.");
        Require((File.GetAttributes(value.Database) & FileAttributes.ReparsePoint) == 0, "QA database cannot be a reparse file.");
    }

    private static void ValidateMarker(Control control, Marker marker, Process child, string launcher)
    {
        ValidateOwnedProcess(control, child, launcher);
        Require(marker.Nonce == control.Nonce && marker.ChildPid == child.Id && marker.ParentPid == Environment.ProcessId &&
            SamePath(marker.Database, control.Database) && SamePath(marker.EntryPoint, EntryPoint) &&
            marker.EntryPointSha256 == control.EntryPointSha256 && SamePath(marker.ProcessPath, launcher) &&
            marker.Phase == "after_product_meta_stock_history_outbox_group_membership_writes_before_sqlite_commit", "Child BeforeCommit identity/phase mismatch.");
        foreach (var backup in marker.Backups)
            Require(SamePath(Path.GetDirectoryName(backup.Path)!, Path.Combine(control.Root, control.Scenario, "backups")), "Backup escaped the owned scenario.");
    }

    private static void ValidateOwnedProcess(Control control, Process child, string launcher)
    {
        ValidateControl(control, Path.Combine(control.Root, control.Scenario, "child-control.json"));
        Require(!child.HasExited && SamePath(child.MainModule!.FileName, launcher) && HashFile(EntryPoint) == control.EntryPointSha256,
            "Refusing to terminate an unverified child process.");
    }

    private static ProcessStartInfo ChildStartInfo(string controlPath)
    {
        var launcher = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process launcher.");
        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(launcher).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(EntryPoint);
        start.ArgumentList.Add("--catalog-import-process-crash-child");
        start.ArgumentList.Add(controlPath);
        return start;
    }

    private static Snapshot Capture(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        return Capture(connection);
    }
    private static Snapshot CaptureReadOnly(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return Capture(connection);
    }
    private static Snapshot Capture(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        Require(connection.ExecuteScalar<string>("PRAGMA integrity_check", transaction: transaction) == "ok", "SQLite integrity failed.");
        Require(!connection.Query("PRAGMA foreign_key_check", transaction: transaction).Any(), "SQLite foreign keys failed.");
        var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", transaction: transaction).ToArray();
        var result = new Dictionary<string, TableProof>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null :
                    reader.GetValue(index) is byte[] bytes ? "blob:" + Convert.ToBase64String(bytes) :
                    Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)).ToArray();
                rows.Add(JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal);
            result[table] = new(rows.Count, CatalogImportOutboxPayloadBuilder.Sha256Hex(JsonSerializer.Serialize(rows)));
        }
        transaction.Commit();
        return new(result);
    }

    private static bool Equal(Snapshot first, Snapshot second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    private static SavedPart[] Parts(CatalogImportOutboxPlan plan) => plan.Entries.Select(entry => new SavedPart(entry.ClientImportId, entry.IdempotencyKey, entry.PayloadJson, entry.PayloadHash)).ToArray();
    private static SavedPart[] SavedOutbox(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        return connection.Query("SELECT client_import_id,idempotency_key,payload_json,payload_hash FROM catalog_import_outbox ORDER BY id")
            .Select(row => new SavedPart((string)row.client_import_id, (string)row.idempotency_key, (string)row.payload_json, (string)row.payload_hash)).ToArray();
    }
    private static CatalogImportOutboxEntry ToEntry(SavedPart part) => new() { ClientImportId = part.ClientImportId, IdempotencyKey = part.IdempotencyKey,
        PayloadJson = part.PayloadJson, PayloadHash = part.PayloadHash, SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = "supplier_excel" };
    private static SupplierImportEditableRow[] Rows(int count, bool edited) => Enumerable.Range(0, count).Select(index => new SupplierImportEditableRow
    {
        RowNumber = index + 2, Barcode = "TX-CRASH-" + index, ProductName = "QA 漢字 " + index,
        PurchasePrice = edited ? "125" : "100", RetailPrice = edited ? "250" : "200", Quantity = edited ? "2.5" : "1.25",
        HasProductNameSource = true, HasPurchasePriceSource = true, HasRetailPriceSource = true, HasQuantitySource = true
    }).ToArray();
    private static void ApplyEdits(IReadOnlyList<SupplierImportEditableRow> rows)
    {
        foreach (var row in rows) { row.PurchasePrice = "125"; row.RetailPrice = "250"; row.Quantity = "2.5"; }
    }
    private static PosTrustedDeviceSession Trusted() => new() { ShopId = "tx-crash-shop", ShopCode = "TX-CRASH-SHOP", ShopDeviceId = "tx-crash-device", PosSessionId = "tx-crash-session", DeviceToken = "synthetic-device", SessionToken = "synthetic-session" };
    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static T Read<T>(string path) where T : class => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Missing JSON object: " + path);
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    private static void WriteAtomic(string path, object value) { Write(path + ".partial", value); File.Move(path + ".partial", path); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
