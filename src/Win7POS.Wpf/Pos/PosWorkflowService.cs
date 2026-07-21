using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Win7POS.Core;
using Win7POS.Core.Audit;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Core.Util;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Backup;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Printing;

namespace Win7POS.Wpf.Pos
{
    public sealed class DbRestoreResult
    {
        public string ForeignKeyCheck { get; set; }
        public string IntegrityCheck { get; set; }
        public bool IntegrityOk { get; set; }
        public string PreRestoreBackupPath { get; set; }
        public string RestoredFromPath { get; set; }
        public bool SyncReviewRequired { get; set; }
    }

    public sealed class PosWorkflowService
    {
        private const string KeyPrinterName = AppSettingKeys.PosPrinterReceiptName;
        private const string KeyPrinterCopies = AppSettingKeys.PosPrinterReceiptCopies;
        private const string KeyAutoPrint = AppSettingKeys.PosPrinterReceiptAutoPrintAfterSale;
        private const string KeyReceiptEnabled = AppSettingKeys.PosPrinterReceiptEnabled;
        private const string KeyAllowWindowsDefault = AppSettingKeys.PosPrinterReceiptAllowWindowsDefault;
        private const string KeyAllowVirtualPrinters = AppSettingKeys.PosPrinterReceiptAllowVirtualPrinters;
        private const string KeyCashDrawerCommand = AppSettingKeys.PosCashDrawerCommand;
        private const string KeyCashDrawerEnabled = AppSettingKeys.PosCashDrawerEnabled;
        private const string KeyCashDrawerMode = AppSettingKeys.PosCashDrawerMode;
        private const string KeyCashDrawerPrinterName = AppSettingKeys.PosCashDrawerPrinterName;
        private const string KeyCashDrawerOpenOnCashSale = AppSettingKeys.PosCashDrawerOpenOnCashSale;
        private const string LegacyKeyPrinterName = "printer.name";
        private const string LegacyKeyPrinterCopies = "printer.copies";
        private const string LegacyKeyAutoPrint = "pos.autoPrint";
        private const string LegacyKeyCashDrawerCommand = "printer.cashDrawerCommand";
        private const string DefaultCashDrawerCommand = "27,112,0,25,250";
        private const string CashDrawerModeDisabled = "disabled";
        private const string CashDrawerModePrinterKick = "printer_kick";
        private const string KeyShopName = "shop.name";
        private const string KeyShopAddress = "shop.address";
        private const string KeyShopCity = "shop.city";
        private const string KeyShopRut = "shop.rut";
        private const string KeyShopPhone = "shop.phone";
        private const string KeyShopFooter = "shop.footer";
        private const string KeyFiscalBoletaNumber = "fiscal.boletaNumber";
        private const string KeyRestoreNeedsSyncReview = "pos.restore.needs_sync_review";
        private const string KeyRestoreLastCompletedAt = "pos.restore.last_completed_at";
        private const string KeyRestoreLastPreBackupPath = "pos.restore.last_pre_backup_path";
        private const string KeyRestoreLastSourcePath = "pos.restore.last_source_path";
        private const string KeyRestoreLastIntegrityCheck = "pos.restore.last_integrity_check";

        private readonly FileLogger _logger = new FileLogger("PosWorkflowService");
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _printerDiscoverySync = new object();
        private Task<IReadOnlyList<InstalledPrinterInfo>> _printerDiscoveryTask;
        private IReadOnlyList<InstalledPrinterInfo> _lastPrinterDiscovery = new List<InstalledPrinterInfo>();

        private const int PrinterDiscoveryTimeoutMilliseconds = 5000;

        private readonly ProductRepository _products;
        private readonly SaleRepository _sales;
        private readonly SettingsRepository _settings;
        private readonly DbMaintenanceRepository _dbMaintenance;
        private readonly SqliteOnlineBackup _onlineBackup;
        private readonly CatalogImportOutboxRepository _catalogImportOutbox;
        private readonly SupplierRepository _suppliers;
        private readonly CategoryRepository _categories;
        private readonly HeldCartRepository _heldCarts;
        private readonly ShopOfficialSnapshotRepository _officialShopSnapshots;
        private readonly AuditLogRepository _audit = new AuditLogRepository();
        private readonly PosSession _session;
        private readonly PosDbOptions _options;
        private readonly SqliteConnectionFactory _factory;
        private readonly IReceiptPrinter _receiptPrinter = new WindowsSpoolerReceiptPrinter();

        private SaleCompleted _lastCompletedSale;

        public PosWorkflowService()
        {
            _options = PosDbOptions.Default();
            // EnsureCreated spostato in InitializeAsync() per non bloccare il thread UI al primo render

            _factory = new SqliteConnectionFactory(_options);
            _products = new ProductRepository(_factory);
            _sales = new SaleRepository(_factory);
            _settings = new SettingsRepository(_factory);
            _dbMaintenance = new DbMaintenanceRepository(_factory);
            _onlineBackup = new SqliteOnlineBackup(_factory);
            _catalogImportOutbox = new CatalogImportOutboxRepository(_factory);
            _suppliers = new SupplierRepository(_factory);
            _categories = new CategoryRepository(_factory);
            _heldCarts = new HeldCartRepository(_factory);
            _officialShopSnapshots = new ShopOfficialSnapshotRepository(_factory);
            _session = new PosSession(new DataProductLookup(_products), new DataSalesStore(_sales));
        }

        public string DbPath => _options.DbPath;

        public async Task<bool?> GetUseReceipt42Async()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _settings.GetBoolAsync("pos.useReceipt42").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetUseReceipt42Async(bool value)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settings.SetBoolAsync("pos.useReceipt42", value).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosPrinterSettings> GetPrinterSettingsAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ReadPrinterSettingsNoLockAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetPrinterSettingsAsync(PosPrinterSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (App.IsSafeStart &&
                (settings.ReceiptEnabled ||
                 settings.AutoPrint ||
                 settings.AllowWindowsDefault ||
                 settings.AllowVirtualPrinters ||
                 settings.CashDrawerEnabled ||
                 settings.CashDrawerOpenOnCashSale ||
                 !string.IsNullOrWhiteSpace(settings.PrinterName) ||
                 !string.IsNullOrWhiteSpace(settings.CashDrawerPrinterName) ||
                 !string.Equals(settings.CashDrawerMode, CashDrawerModeDisabled, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    PosLocalization.T("printer.hardwareDisabledSafeStart"));
            }
            if (!ReceiptPrintOptions.IsValidCopyCount(settings.Copies))
                throw new InvalidOperationException(
                    PosLocalization.T("printer.invalidCopies"));

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var copies = settings.Copies;
                var cashDrawerMode = string.Equals(settings.CashDrawerMode, CashDrawerModePrinterKick, StringComparison.OrdinalIgnoreCase)
                    ? CashDrawerModePrinterKick
                    : CashDrawerModeDisabled;
                var rawCashDrawerCommand = settings.CashDrawerCommand ?? string.Empty;
                if (rawCashDrawerCommand.Length > WindowsSpoolerReceiptPrinter.MaximumCashDrawerCommandLength)
                    throw new InvalidOperationException(PosLocalization.T("printer.testInvalidCommand"));
                var cashDrawerCommand = rawCashDrawerCommand.Trim();
                var cashDrawerActive = settings.CashDrawerEnabled &&
                                       string.Equals(cashDrawerMode, CashDrawerModePrinterKick, StringComparison.OrdinalIgnoreCase);
                if (cashDrawerActive && !WindowsSpoolerReceiptPrinter.IsCashDrawerCommandValid(rawCashDrawerCommand))
                    throw new InvalidOperationException(PosLocalization.T("printer.testInvalidCommand"));

                await _settings.SetStringAsync(KeyPrinterName, settings.PrinterName ?? string.Empty).ConfigureAwait(false);
                await _settings.SetIntAsync(KeyPrinterCopies, copies).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeyReceiptEnabled, settings.ReceiptEnabled).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeyAutoPrint, settings.AutoPrint).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeyAllowWindowsDefault, settings.AllowWindowsDefault).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeyAllowVirtualPrinters, settings.AllowVirtualPrinters).ConfigureAwait(false);
                await _settings.SetStringAsync(KeyCashDrawerCommand, cashDrawerCommand).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeyCashDrawerEnabled, settings.CashDrawerEnabled).ConfigureAwait(false);
                await _settings.SetStringAsync(KeyCashDrawerMode, cashDrawerMode).ConfigureAwait(false);
                await _settings.SetStringAsync(KeyCashDrawerPrinterName, settings.CashDrawerPrinterName ?? string.Empty).ConfigureAwait(false);
                await _settings.SetBoolAsync(KeyCashDrawerOpenOnCashSale, settings.CashDrawerOpenOnCashSale).ConfigureAwait(false);

                await _settings.SetStringAsync(LegacyKeyPrinterName, settings.PrinterName ?? string.Empty).ConfigureAwait(false);
                await _settings.SetIntAsync(LegacyKeyPrinterCopies, copies).ConfigureAwait(false);
                await _settings.SetBoolAsync(LegacyKeyAutoPrint, settings.AutoPrint).ConfigureAwait(false);
                await _settings.SetStringAsync(LegacyKeyCashDrawerCommand, cashDrawerCommand).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> GetAutoPrintAsync()
        {
            var settings = await GetPrinterSettingsAsync().ConfigureAwait(false);
            return settings.AutoPrint;
        }

        public async Task SetAutoPrintAsync(bool value)
        {
            var settings = await GetPrinterSettingsAsync().ConfigureAwait(false);
            settings.AutoPrint = value;
            await SetPrinterSettingsAsync(settings).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<InstalledPrinterInfo>> GetInstalledPrintersAsync()
        {
            if (App.IsSafeStart)
                return Array.Empty<InstalledPrinterInfo>();

            Task<IReadOnlyList<InstalledPrinterInfo>> discoveryTask;
            lock (_printerDiscoverySync)
            {
                if (_printerDiscoveryTask == null || _printerDiscoveryTask.IsCompleted)
                {
                    _printerDiscoveryTask = Task.Run<IReadOnlyList<InstalledPrinterInfo>>(() =>
                    {
                        var discovered = WindowsPrinterDiscovery.GetInstalledPrinters();
                        var freshSnapshot = ClonePrinterInventory(discovered, isFresh: true);
                        lock (_printerDiscoverySync)
                        {
                            _lastPrinterDiscovery = ClonePrinterInventory(freshSnapshot, isFresh: true);
                        }

                        return freshSnapshot;
                    });
                }

                discoveryTask = _printerDiscoveryTask;
            }

            var completed = await Task.WhenAny(
                discoveryTask,
                Task.Delay(PrinterDiscoveryTimeoutMilliseconds)).ConfigureAwait(false);
            if (completed == discoveryTask)
            {
                try
                {
                    return ClonePrinterInventory(
                        await discoveryTask.ConfigureAwait(false),
                        isFresh: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Printer discovery failed; stale cached inventory returned.", ex);
                    lock (_printerDiscoverySync)
                    {
                        return ClonePrinterInventory(_lastPrinterDiscovery, isFresh: false);
                    }
                }
            }

            _logger.LogWarning("Printer discovery timed out; stale cached inventory returned.");
            lock (_printerDiscoverySync)
            {
                return ClonePrinterInventory(_lastPrinterDiscovery, isFresh: false);
            }
        }

        private static IReadOnlyList<InstalledPrinterInfo> ClonePrinterInventory(
            IEnumerable<InstalledPrinterInfo> printers,
            bool isFresh)
        {
            return (printers ?? Enumerable.Empty<InstalledPrinterInfo>())
                .Where(printer => printer != null)
                .Select(printer => printer.CloneWithInventoryFreshness(isFresh))
                .ToList();
        }

        public async Task<string> BuildPrinterTestReceiptAsync(bool use42)
        {
            var shop = await GetShopInfoAsync().ConfigureAwait(false);
            return BuildPrinterTestReceipt(shop, use42, DateTimeOffset.Now);
        }

        public async Task TestReceiptPrinterAsync(
            PosPrinterSettings settings,
            string receiptText,
            bool use42)
        {
            PrinterHardwareSafety.DemandHardwareOutputAllowed();
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(receiptText))
                throw new InvalidOperationException(PosLocalization.T("printer.receiptTextEmpty"));
            var installedPrinters = await GetInstalledPrintersAsync().ConfigureAwait(false);
            var resolvedPrinter = ResolveReceiptPrinterOrThrow(
                settings,
                installedPrinters,
                automaticAfterSale: false,
                explicitUserAction: true);
            await _receiptPrinter.PrintAsync(receiptText, new ReceiptPrintOptions
            {
                PrinterName = resolvedPrinter.Name,
                Copies = 1,
                CharactersPerLine = use42 ? 42 : 32,
                SaleCodeForBarcode = "TEST-NO-SALE",
                UseReceiptHeaderStyle = true
            }).ConfigureAwait(false);
        }

        public async Task<DbRestoreResult> RestoreDbAsync(string backupDbPath)
        {
            if (string.IsNullOrWhiteSpace(backupDbPath))
                throw new ArgumentException("backup path is empty");
            if (!File.Exists(backupDbPath))
                throw new FileNotFoundException("Backup file not found.", backupDbPath);

            await _gate.WaitAsync().ConfigureAwait(false);
            IDisposable authorizationMaintenanceLease = null;
            IDisposable catalogTransitionLease = null;
            var syncSupervisorStopAttempted = false;
            var restoreInstalled = false;
            PosTrustedDeviceStore trustedDeviceStore = null;
            OnlineSyncGeneration invalidatedGeneration = null;
            try
            {
                authorizationMaintenanceLease =
                    PosOnlineSyncRevocationLatch.EnterAuthorizationMaintenance();
                syncSupervisorStopAttempted = true;
                await PosOnlineSyncSignalBus.StopAsync().ConfigureAwait(false);
                trustedDeviceStore = new PosTrustedDeviceStore();
                if (trustedDeviceStore.TryRead(out var trustedSession))
                {
                    PosOnlineSyncSupervisorHost.TryCreateGeneration(
                        trustedSession,
                        out invalidatedGeneration);
                }
                catalogTransitionLease = await new CatalogShopTransitionBarrier(_factory)
                    .EnterAsync()
                    .ConfigureAwait(false);
                var catalogState = new CatalogShopStateRepository(_factory);
                var liveCatalogEpoch = await catalogState.LoadTransitionEpochAsync().ConfigureAwait(false);
                var currentShop = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
                if (currentShop == null || string.IsNullOrWhiteSpace(currentShop.ShopCode))
                {
                    throw new InvalidOperationException(PosLocalization.T("dbMaintenance.restoreTrustedShopRequired"));
                }

                if (await _sales.HasUnresolvedSalesSyncOutboxAsync().ConfigureAwait(false))
                {
                    _logger.LogWarning("POS DB restore blocked: unresolved sales sync outbox exists.");
                    throw new InvalidOperationException(PosLocalization.T("dbMaintenance.restoreBlockedUnresolvedSales"));
                }

                if (await _catalogImportOutbox.HasUnresolvedAsync().ConfigureAwait(false))
                {
                    _logger.LogWarning("POS DB restore blocked: unresolved catalog import outbox exists.");
                    throw new InvalidOperationException(PosLocalization.T("dbMaintenance.restoreBlockedUnresolvedCatalogImports"));
                }

                var restoredAt = DateTimeOffset.UtcNow;

                var tempRestorePath = Path.Combine(
                    AppPaths.BackupsDirectory,
                    "pos_restore_validate_" + Guid.NewGuid().ToString("N") + ".db");
                try
                {
                    File.Copy(backupDbPath, tempRestorePath, true);
                    var validateOptions = new PosDbOptions(tempRestorePath);
                    DbInitializer.EnsureCreated(validateOptions);
                    var validateFactory = new SqliteConnectionFactory(validateOptions);
                    var validationMaintenance = new DbMaintenanceRepository(validateFactory);
                    var candidateValidation = await validationMaintenance.ValidateAsync().ConfigureAwait(false);
                    if (!candidateValidation.IsValid)
                    {
                        throw new InvalidOperationException(PosLocalization.F(
                            "dbMaintenance.integrityCheckFailed",
                            candidateValidation.IntegrityCheck + " / FK: " + candidateValidation.ForeignKeyCheck));
                    }

                    var restoreSafety = await new RestoreShopSafetyRepository(validateFactory)
                        .ValidateCandidateAsync(currentShop.ShopId, currentShop.ShopCode)
                        .ConfigureAwait(false);
                    if (!restoreSafety.IsValid)
                    {
                        throw new InvalidOperationException(restoreSafety.Code);
                    }

                    SqliteConnectionFactory.ClearAllPools();

                    var preBackupPath = string.Empty;
                    var integrity = string.Empty;
                    var foreignKeys = string.Empty;
                    var integrityOk = false;
                    await SqliteConnectionFactory.RunExclusiveMaintenanceAsync(async () =>
                    {
                        var livePreSwapSafety = await new RestoreShopSafetyRepository(_factory)
                            .ValidateLivePreSwapAsync(
                                currentShop.ShopId,
                                currentShop.ShopCode,
                                liveCatalogEpoch)
                            .ConfigureAwait(false);
                        if (!livePreSwapSafety.IsValid)
                        {
                            _logger.LogWarning(
                                "POS DB restore blocked by fenced pre-swap validation: " +
                                livePreSwapSafety.Code);
                            if (string.Equals(
                                livePreSwapSafety.Code,
                                "restore_live_sales_outbox_unresolved",
                                StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(PosLocalization.T("dbMaintenance.restoreBlockedUnresolvedSales"));
                            }

                            if (string.Equals(
                                livePreSwapSafety.Code,
                                "restore_live_catalog_outbox_unresolved",
                                StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(PosLocalization.T("dbMaintenance.restoreBlockedUnresolvedCatalogImports"));
                            }

                            throw new InvalidOperationException(livePreSwapSafety.Code);
                        }

                        var fencedCandidateSafety = await new RestoreShopSafetyRepository(validateFactory)
                            .ValidateCandidateAsync(currentShop.ShopId, currentShop.ShopCode)
                            .ConfigureAwait(false);
                        if (!fencedCandidateSafety.IsValid)
                        {
                            throw new InvalidOperationException(fencedCandidateSafety.Code);
                        }

                        preBackupPath = await CreateDbBackupNoLockAsync("pos_pre_restore_").ConfigureAwait(false);
                        await new AtomicRestoreInstaller().InstallAsync(
                            tempRestorePath,
                            _options.DbPath,
                            preBackupPath,
                            async () =>
                            {
                                DbInitializer.EnsureCreated(_options);
                                var liveValidation = await _dbMaintenance.ValidateAsync().ConfigureAwait(false);
                                integrity = liveValidation.IntegrityCheck;
                                foreignKeys = liveValidation.ForeignKeyCheck;
                                integrityOk = liveValidation.IsValid;
                                if (!integrityOk)
                                {
                                    throw new InvalidOperationException(PosLocalization.F(
                                        "dbMaintenance.integrityCheckFailed",
                                        integrity + " / FK: " + foreignKeys));
                                }

                                await new OnlineSyncGenerationRepository(_factory)
                                    .ResetForRestoreAsync(
                                        invalidatedGeneration,
                                        currentShop.ShopId,
                                        currentShop.ShopCode,
                                        restoredAt.ToUnixTimeMilliseconds())
                                    .ConfigureAwait(false);
                                await catalogState
                                    .ResetForRestoreReviewWhileBarrierHeldAsync(
                                        currentShop.ShopId,
                                        currentShop.ShopCode,
                                        liveCatalogEpoch)
                                    .ConfigureAwait(false);

                                await _settings.SetBoolAsync(KeyRestoreNeedsSyncReview, true).ConfigureAwait(false);
                                await _settings.SetStringAsync(KeyRestoreLastCompletedAt, restoredAt.ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                                await _settings.SetStringAsync(KeyRestoreLastPreBackupPath, preBackupPath).ConfigureAwait(false);
                                await _settings.SetStringAsync(KeyRestoreLastSourcePath, backupDbPath).ConfigureAwait(false);
                                await _settings.SetStringAsync(KeyRestoreLastIntegrityCheck, integrity).ConfigureAwait(false);
                                var details = AuditDetails.Kv(
                                    ("backupFile", Path.GetFileName(backupDbPath)),
                                    ("preBackupFile", Path.GetFileName(preBackupPath)),
                                    ("integrity", "ok"),
                                    ("foreignKeys", "ok"),
                                    ("syncReview", "required"));
                                await _audit.AppendAsync(
                                    _options,
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                    AuditActions.DbRestore,
                                    details).ConfigureAwait(false);

                                // This is deliberately the final non-throwing
                                // post-swap action. Every pre-restore synchronous
                                // authorization cache is invalid from this point.
                                PosOnlineSyncRevocationLatch
                                    .InvalidateAuthorizationState();
                            }).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    restoreInstalled = true;

                    PosOnlineSyncRevocationLatch.Revoke(invalidatedGeneration);
                    trustedDeviceStore.Clear();
                    if (trustedDeviceStore.HasStoredState())
                    {
                        _logger.LogWarning(
                            "POS DB restore left a stale trusted-session file; " +
                            "the authorization epoch and inactive generation " +
                            "tombstone keep it denied.");
                    }

                    _logger.LogWarning("POS DB restored; sync review required. backupFile=" + Path.GetFileName(backupDbPath) + " preBackupFile=" + Path.GetFileName(preBackupPath));
                    return new DbRestoreResult
                    {
                        ForeignKeyCheck = foreignKeys,
                        IntegrityCheck = integrity,
                        IntegrityOk = integrityOk,
                        PreRestoreBackupPath = preBackupPath,
                        RestoredFromPath = backupDbPath,
                        SyncReviewRequired = true
                    };
                }
                finally
                {
                    try { File.Delete(tempRestorePath); } catch { }
                    try { File.Delete(tempRestorePath + "-wal"); } catch { }
                    try { File.Delete(tempRestorePath + "-shm"); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS DB restore failed");
                throw;
            }
            finally
            {
                catalogTransitionLease?.Dispose();
                if (syncSupervisorStopAttempted)
                {
                    try
                    {
                        if (restoreInstalled)
                        {
                            await PosOnlineSyncSignalBus
                                .ExitMaintenanceWithoutResumeAsync()
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            authorizationMaintenanceLease?.Dispose();
                            authorizationMaintenanceLease = null;
                            await PosOnlineSyncSignalBus.ResumeAsync()
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception resumeEx)
                    {
                        _logger.LogWarning(
                            "POS sync supervisor maintenance exit was deferred.",
                            resumeEx);
                    }
                }
                authorizationMaintenanceLease?.Dispose();
                _gate.Release();
            }
        }

        public async Task<string> IntegrityCheckAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _dbMaintenance.IntegrityCheckAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CompleteRestoreSyncReviewAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var result = await new RestoreShopSafetyRepository(_factory)
                    .CompleteReviewAsync()
                    .ConfigureAwait(false);
                if (!result.IsValid)
                {
                    throw new InvalidOperationException(result.Code);
                }

                await _audit.AppendAsync(
                    _options,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    AuditActions.DbRestore,
                    AuditDetails.Kv(("syncReview", "completed"))).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task VacuumAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _dbMaintenance.VacuumAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task WalCheckpointAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _dbMaintenance.WalCheckpointAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DailySalesSummary> GetDailySummaryAsync(DateTime date, bool includeFiscalPrinted = true)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _sales.GetDailySummaryAsync(date, includeFiscalPrinted).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesAsync(DateTime fromDate, DateTime toDate, bool includeFiscalPrinted = true)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _sales.GetDailySummariesAsync(fromDate, toDate, includeFiscalPrinted).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Vendite per ora (0-23) del giorno, per grafico orario.</summary>
        public async Task<IReadOnlyList<long>> GetHourlySalesAsync(DateTime date, bool includeFiscalPrinted = true)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _sales.GetHourlySalesAsync(date, includeFiscalPrinted).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Marca la vendita come documento locale stampato senza rimuoverla da registro o chiusura.</summary>
        public Task MarkPdfPrintedAsync(long saleId)
        {
            return _sales.MarkPdfPrintedAsync(saleId);
        }

        public Task TrySyncPendingSalesAsync()
        {
            return PosOnlineSyncSignalBus.TriggerAsync(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.LocalCommit,
                CancellationToken.None);
        }

        public async Task<string> ExportDailyCsvAsync(DateTime date)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                AppPaths.EnsureCreated();
                var content = await GetDailyCsvContentAsync(date).ConfigureAwait(false);
                var fileName = "daily_" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";
                var path = Path.Combine(AppPaths.ExportsDirectory, fileName);
                await Task.Run(() => File.WriteAllText(path, content, Encoding.UTF8)).ConfigureAwait(false);
                return path;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Restituisce il contenuto CSV per un giorno (per Salva con nome).</summary>
        public async Task<string> GetDailyCsvContentAsync(DateTime date, bool includeFiscalPrinted = true)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.GetSalesForDateAsync(date, includeFiscalPrinted).ConfigureAwait(false);
                return BuildSalesCsvContent(rows);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Restituisce il contenuto CSV per un periodo (per Salva con nome).</summary>
        public async Task<string> GetPeriodCsvContentAsync(DateTime fromDate, DateTime toDate, bool includeFiscalPrinted = true)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var from = new DateTimeOffset(fromDate.Date).ToUnixTimeMilliseconds();
                var to = new DateTimeOffset(toDate.Date.AddDays(1)).ToUnixTimeMilliseconds();
                var rows = await _sales.GetSalesBetweenAsync(from, to, null, includeFiscalPrinted).ConfigureAwait(false);
                return BuildSalesCsvContent(rows);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Restituisce CSV per un elenco di date (es. giorni selezionati nello storico).</summary>
        public async Task<string> GetDaysCsvContentAsync(IReadOnlyList<DateTime> dates, bool includeFiscalPrinted = true)
        {
            if (dates == null || dates.Count == 0)
                return SalesCsvHeader() + Environment.NewLine;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sb = new StringBuilder();
                var headerDone = false;
                foreach (var date in dates)
                {
                    var rows = await _sales.GetSalesForDateAsync(date.Date, includeFiscalPrinted).ConfigureAwait(false);
                    if (rows.Count == 0) continue;
                    if (!headerDone)
                    {
                        sb.AppendLine(SalesCsvHeader());
                        headerDone = true;
                    }
                    foreach (var s in rows)
                    {
                        sb.Append(s.Id).Append(';')
                            .Append(EscapeCsv(s.Code)).Append(';')
                            .Append(s.Kind).Append(';')
                            .Append(s.RelatedSaleId.HasValue ? s.RelatedSaleId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
                            .Append(s.CreatedAt).Append(';')
                            .Append(s.Total).Append(';')
                            .Append(s.PaidCash).Append(';')
                            .Append(s.PaidCard).Append(';')
                            .Append(s.Change).Append(';')
                            .Append(s.PdfPrinted ? "1" : "0").Append(';')
                            .Append(DocumentStatusForSale(s)).AppendLine();
                    }
                }
                if (!headerDone)
                    sb.AppendLine(SalesCsvHeader());
                return sb.ToString();
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string BuildSalesCsvContent(IReadOnlyList<Sale> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(SalesCsvHeader());
            foreach (var s in rows)
            {
                sb.Append(s.Id).Append(';')
                    .Append(EscapeCsv(s.Code)).Append(';')
                    .Append(s.Kind).Append(';')
                    .Append(s.RelatedSaleId.HasValue ? s.RelatedSaleId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty).Append(';')
                    .Append(s.CreatedAt).Append(';')
                    .Append(s.Total).Append(';')
                    .Append(s.PaidCash).Append(';')
                    .Append(s.PaidCard).Append(';')
                    .Append(s.Change).Append(';')
                    .Append(s.PdfPrinted ? "1" : "0").Append(';')
                    .Append(DocumentStatusForSale(s)).AppendLine();
            }
            return sb.ToString();
        }

        private static string SalesCsvHeader()
        {
            return "saleId;code;kind;related_sale_id;createdAt;total;paidCash;paidCard;change;pdf_printed;document_status";
        }

        private static string DocumentStatusForSale(Sale sale)
        {
            if (sale == null)
            {
                return "non_disponibile";
            }

            if (sale.PdfPrinted)
            {
                return "boleta_termica_stampata";
            }

            return sale.PaidCash > 0 ? "non_stampata_contanti" : "non_stampata_policy_carta";
        }

        public async Task<string> BackupDbAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DbInitializer.EnsureCreated(_options);
                AppPaths.EnsureCreated();

                var fileName = "pos_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".db";
                var outputPath = Path.Combine(AppPaths.BackupsDirectory, fileName);
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outputDir))
                    Directory.CreateDirectory(outputDir);

                await _onlineBackup.CreateVerifiedAsync(outputPath).ConfigureAwait(false);
                _logger.LogInfo("POS DB backup created: " + Path.GetFileName(outputPath));
                return outputPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS DB backup failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<string> CreateDbBackupNoLockAsync(string prefix)
        {
            AppPaths.EnsureCreated();

            if (!File.Exists(_options.DbPath))
                throw new FileNotFoundException("Current POS database not found; restore pre-backup was not created.", _options.DbPath);

            var safePrefix = string.IsNullOrWhiteSpace(prefix) ? "pos_backup_" : prefix;
            var fileName = safePrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".db";
            var outputPath = Path.Combine(AppPaths.BackupsDirectory, fileName);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            await _onlineBackup.CreateVerifiedAsync(outputPath).ConfigureAwait(false);
            _logger.LogInfo("POS DB pre-restore backup created: " + Path.GetFileName(outputPath));
            return outputPath;
        }

        public async Task InitializeAsync()
        {
            await new AtomicRestoreInstaller()
                .RecoverInterruptedInstallAsync(_options.DbPath)
                .ConfigureAwait(false);
            await Task.Run(() => DbInitializer.EnsureCreated(_options)).ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInfo("POS initialize start");
                await EnsureDemoProductsAsync().ConfigureAwait(false);
                _logger.LogInfo("POS initialize done");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS initialize failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> AddByBarcodeAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var code = (barcode ?? string.Empty).Trim();
                _logger.LogInfo("POS add barcode: " + code);
                await _session.AddByBarcodeAsync(code).ConfigureAwait(false);
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.itemAdded"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS add barcode failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> AddManualPriceAsync(int unitPriceMinor)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInfo("POS add manual price: " + unitPriceMinor);
                await _session.AddManualPriceAsync(unitPriceMinor).ConfigureAwait(false);
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.manualItemAdded"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS add manual price failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CreateProductAsync(string barcode, string name, int unitPriceMinor)
        {
            await CreateProductFullAsync(barcode, name, unitPriceMinor, 0, null, null, 0).ConfigureAwait(false);
        }

        /// <summary>Overload con supplier/category by name (no IDs). Nome puo essere vuoto: usa una label localizzata.</summary>
        public async Task CreateProductFullAsync(
            string barcode,
            string name,
            int unitPriceMinor,
            int purchasePriceMinor,
            string supplierName,
            string categoryName,
            int stockQty)
        {
            var productName = (name ?? string.Empty).Trim();
            if (productName.Length == 0) productName = PosLocalization.T("products.productWithoutCode");
            await CreateProductFullAsync(barcode, productName, unitPriceMinor, purchasePriceMinor, null, supplierName ?? string.Empty, null, categoryName ?? string.Empty, stockQty).ConfigureAwait(false);
        }

        public async Task CreateProductFullAsync(
            string barcode,
            string name,
            int unitPriceMinor,
            int purchasePriceMinor,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName,
            int stockQty)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var code = (barcode ?? string.Empty).Trim();
                var productName = (name ?? string.Empty).Trim();
                if (code.Length == 0) throw new ArgumentException(PosLocalization.T("products.barcodeRequired"));
                if (productName.Length == 0) throw new ArgumentException(PosLocalization.T("products.nameRequired"));
                if (unitPriceMinor < 0) throw new ArgumentException(PosLocalization.T("products.priceInvalid"));
                if (purchasePriceMinor < 0) purchasePriceMinor = 0;
                if (stockQty < 0) stockQty = 0;

                await _products.UpsertAsync(new Product
                {
                    Barcode = code,
                    Name = productName,
                    UnitPrice = unitPriceMinor
                }).ConfigureAwait(false);

                await _products.UpsertMetaAsync(code, purchasePriceMinor, supplierId, supplierName ?? string.Empty, categoryId, categoryName ?? string.Empty, stockQty).ConfigureAwait(false);

                await _products.InsertPriceHistoryAsync(code, "retail", unitPriceMinor, "MANUAL").ConfigureAwait(false);
                if (purchasePriceMinor > 0)
                    await _products.InsertPriceHistoryAsync(code, "purchase", purchasePriceMinor, "MANUAL").ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Aggiorna nome e prezzo prodotto in DB e aggiorna la riga in carrello. Ritorna snapshot aggiornato.</summary>
        public async Task<PosWorkflowSnapshot> UpdateProductAsync(string barcode, string name, long unitPriceMinor)
        {
            var code = (barcode ?? string.Empty).Trim();
            if (code.Length == 0) return await GetSnapshotAsync().ConfigureAwait(true);

            var product = await _products.GetByBarcodeAsync(code).ConfigureAwait(false);
            if (product == null) return await GetSnapshotAsync().ConfigureAwait(true);

            await _products.UpdateAsync(product.Id, name ?? string.Empty, unitPriceMinor).ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.SetLineUnitPrice(code, unitPriceMinor);
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.priceUpdated"));
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Sincronizza la riga carrello con il catalogo (prezzo/nome). Se il prodotto non è più in DB, rimuove la riga. Usato dopo Modifica prodotto -> Salva.</summary>
        public async Task<PosWorkflowSnapshot> SyncCartLineFromCatalogAsync(string barcode)
        {
            var code = (barcode ?? string.Empty).Trim();
            if (code.Length == 0)
                return await GetSnapshotAsync().ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, code, StringComparison.Ordinal));
                if (line == null)
                    return await BuildSnapshotAsync(string.Empty).ConfigureAwait(false);

                var product = await _products.GetByBarcodeAsync(code).ConfigureAwait(false);

                if (product == null)
                {
                    _session.SetQuantity(code, 0);
                    return await BuildSnapshotAsync(PosLocalization.T("pos.status.productRemovedFromCartMissing")).ConfigureAwait(false);
                }

                _session.SetLineUnitPrice(code, product.UnitPrice);
                _session.SetLineName(code, product.Name ?? string.Empty);

                return await BuildSnapshotAsync(PosLocalization.T("pos.status.catalogSynced")).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<Data.Repositories.SupplierListItem>> GetSuppliersAsync()
        {
            return await _suppliers.ListAllAsync().ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<Data.Repositories.CategoryListItem>> GetCategoriesAsync()
        {
            return await _categories.ListAllAsync().ConfigureAwait(false);
        }

        public async Task<PosPayResult> PayAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInfo("POS pay start");
                var completed = await _session.PayCashAsync().ConfigureAwait(false);
                _lastCompletedSale = completed;
                var shop = await GetShopInfoNoLockAsync().ConfigureAwait(false);
                var preview = BuildReceiptPreview(completed, true, shop);
                var snapshot = await BuildSnapshotAsync(PosLocalization.T("pos.status.paymentCompleted"));
                _logger.LogInfo("POS pay done: " + completed.Sale.Code);
                return new PosPayResult
                {
                    SaleCode = completed.Sale.Code,
                    ReceiptPreview = preview,
                    Snapshot = snapshot
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS pay failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosSaleResult> CompleteSaleAsync(
            PosPaymentInfo payment,
            string saleCode = null,
            long? createdAtMs = null,
            int? operatorId = null,
            ReceiptShopInfo receiptShopSnapshot = null)
        {
            if (payment == null) throw new ArgumentNullException(nameof(payment));

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.Lines.Count == 0)
                    throw new PosException(PosErrorCode.EmptyCart);

                var total = _session.Total;
                if (!payment.IsValid(total))
                    throw new InvalidOperationException(PosLocalization.T("pos.status.invalidPayment"));

                var effectiveCreated = (createdAtMs.HasValue && createdAtMs.Value != 0) ? createdAtMs.Value : (long?)null;
                var sale = new Sale
                {
                    Code = !string.IsNullOrWhiteSpace(saleCode) ? saleCode : SaleCodeGenerator.NewCode("V"),
                    CreatedAt = effectiveCreated ?? UnixTime.NowMs(),
                    Total = total,
                    PaidCash = payment.CashAmountMinor,
                    PaidCard = payment.CardAmountMinor,
                    Change = payment.GetChangeMinor(total),
                    OperatorId = operatorId
                };

                var saleLines = _session.Lines.Select(x => new SaleLine
                {
                    ProductId = x.ProductId,
                    Barcode = x.Barcode,
                    Name = x.Name,
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    LineTotal = x.LineTotal
                }).ToList();

                var shop = FreezeReceiptShopInfo(
                    receiptShopSnapshot ?? await GetShopInfoNoLockAsync().ConfigureAwait(false));
                sale.ReceiptShopSnapshotJson = SerializeReceiptShopSnapshot(shop);
                var saleId = await _sales.InsertSaleAsync(sale, saleLines).ConfigureAwait(false);
                sale.Id = saleId;
                QueueSalesOutboxSyncNoThrow();

                var completed = new SaleCompleted(sale, saleLines);
                _lastCompletedSale = completed;
                _session.Clear();
                var snapshot = await BuildSnapshotAsync(PosLocalization.T("pos.status.saleCompleted"));

                return new PosSaleResult
                {
                    SaleId = sale.Id,
                    SaleCode = sale.Code,
                    TotalMinor = sale.Total,
                    PaidMinor = sale.PaidCash + sale.PaidCard,
                    ChangeMinor = sale.Change,
                    CreatedAtMs = sale.CreatedAt,
                    Receipt42 = BuildReceiptPreview(completed, true, shop),
                    Receipt32 = BuildReceiptPreview(completed, false, shop),
                    Snapshot = snapshot
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS complete sale failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RefundPreviewModel> BuildRefundPreviewAsync(long originalSaleId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DbInitializer.EnsureCreated(_options);
                var sale = await _sales.GetByIdAsync(originalSaleId).ConfigureAwait(false);
                if (sale == null)
                    throw new InvalidOperationException(PosLocalization.T("pos.status.saleNotFound"));
                if (sale.Kind != (int)SaleKind.Sale)
                    throw new InvalidOperationException(PosLocalization.T("refund.onlyNormalSalesRefundable"));

                var lines = await _sales.GetReturnableLinesAsync(originalSaleId).ConfigureAwait(false);
                var economics = await _sales
                    .GetReversalEconomicsSnapshotAsync(originalSaleId)
                    .ConfigureAwait(false);
                var rows = lines.Select(x => new RefundPreviewLine
                {
                    OriginalLineId = x.OriginalLineId,
                    ProductId = x.ProductId,
                    Barcode = x.Barcode ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    UnitPriceMinor = x.UnitPrice,
                    SoldQty = x.SoldQty,
                    RefundedQty = x.RefundedQty,
                    RemainingQty = x.RemainingQty,
                    QtyToRefund = 0
                }).ToList();
                var remainingGross = rows.Sum(x => (long)x.RemainingQty * x.UnitPriceMinor);
                var maxRefundable = remainingGross == 0
                    ? 0
                    : -ReversalEconomicsPolicy.Calculate(economics, remainingGross).NetClp;

                return new RefundPreviewModel
                {
                    OriginalSaleId = sale.Id,
                    OriginalSaleCode = sale.Code ?? string.Empty,
                    OriginalCreatedAtMs = sale.CreatedAt,
                    OriginalTotalMinor = sale.Total,
                    IsAlreadyVoided = sale.VoidedBySaleId.HasValue,
                    Economics = economics,
                    Lines = rows,
                    MaxRefundableMinor = maxRefundable
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<RefundCreateResult> CreateRefundAsync(RefundCreateRequest req, bool useReceipt42, bool autoPrint)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (req.OriginalSaleId <= 0) throw new ArgumentException("invalid original sale id");

            var effectiveAutoPrint = autoPrint && !App.IsSafeStart;
            var installedPrinters = effectiveAutoPrint
                ? await GetInstalledPrintersAsync().ConfigureAwait(false)
                : null;
            ReceiptPrintRequest automaticPrintRequest = null;
            RefundCreateResult result;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DbInitializer.EnsureCreated(_options);
                var original = await _sales.GetByIdAsync(req.OriginalSaleId).ConfigureAwait(false);
                if (original == null)
                    throw new InvalidOperationException(PosLocalization.T("refund.originalSaleNotFound"));
                if (original.Kind != (int)SaleKind.Sale)
                    throw new InvalidOperationException(PosLocalization.T("refund.saleNotRefundable"));

                var returnable = await _sales.GetReturnableLinesAsync(req.OriginalSaleId).ConfigureAwait(false);
                var economics = await _sales
                    .GetReversalEconomicsSnapshotAsync(req.OriginalSaleId)
                    .ConfigureAwait(false);
                var returnableMap = returnable.ToDictionary(x => x.OriginalLineId, x => x);
                if (returnableMap.Count == 0)
                    throw new InvalidOperationException(PosLocalization.T("refund.noRefundableLines"));

                var selected = new List<RefundLineRequest>();
                if (req.IsFullVoid)
                {
                    if (original.VoidedBySaleId.HasValue)
                        throw new InvalidOperationException(PosLocalization.T("refund.saleAlreadyVoided"));
                    foreach (var x in returnable)
                    {
                        if (x.RemainingQty <= 0) continue;
                        selected.Add(new RefundLineRequest
                        {
                            OriginalLineId = x.OriginalLineId,
                            ProductId = x.ProductId,
                            Barcode = x.Barcode ?? string.Empty,
                            Name = x.Name ?? string.Empty,
                            UnitPriceMinor = x.UnitPrice,
                            QtyToRefund = x.RemainingQty
                        });
                    }
                }
                else
                {
                    foreach (var line in req.Lines ?? new List<RefundLineRequest>())
                    {
                        if (line == null || line.QtyToRefund <= 0) continue;
                        if (!returnableMap.TryGetValue(line.OriginalLineId, out var source))
                            throw new InvalidOperationException(PosLocalization.T("refund.invalidRefundLine"));
                        if (line.QtyToRefund > source.RemainingQty)
                            throw new InvalidOperationException(PosLocalization.T("refund.quantityTooHigh"));

                        selected.Add(new RefundLineRequest
                        {
                            OriginalLineId = source.OriginalLineId,
                            ProductId = source.ProductId,
                            Barcode = source.Barcode ?? string.Empty,
                            Name = source.Name ?? string.Empty,
                            UnitPriceMinor = source.UnitPrice,
                            QtyToRefund = line.QtyToRefund
                        });
                    }
                }

                if (selected.Count == 0)
                    throw new InvalidOperationException(PosLocalization.T("refund.noSelectedLines"));

                var selectedGross = selected.Sum(x => (long)x.QtyToRefund * x.UnitPriceMinor);
                if (selectedGross <= 0)
                    throw new InvalidOperationException(PosLocalization.T("refund.invalidTotal"));
                var allocation = ReversalEconomicsPolicy.Calculate(economics, selectedGross);
                var refundPositiveTotal = -allocation.NetClp;
                if (refundPositiveTotal <= 0)
                    throw new InvalidOperationException(PosLocalization.T("refund.invalidTotal"));

                var cash = req.Payment == null ? 0 : req.Payment.CashMinor;
                var card = req.Payment == null ? 0 : req.Payment.CardMinor;
                if (cash < 0 || card < 0)
                    throw new InvalidOperationException(PosLocalization.T("refund.invalidPayment"));
                if (cash + card != refundPositiveTotal)
                    throw new InvalidOperationException(PosLocalization.T("refund.splitMismatch"));

                var refundSale = new Sale
                {
                    Code = SaleCodeGenerator.NewCode("R"),
                    CreatedAt = UnixTime.NowMs(),
                    Kind = req.IsFullVoid ? (int)SaleKind.Void : (int)SaleKind.Refund,
                    RelatedSaleId = original.Id,
                    Reason = (req.Reason ?? string.Empty).Trim(),
                    Total = allocation.NetClp,
                    PaidCash = -Math.Abs(cash),
                    PaidCard = -Math.Abs(card),
                    Change = 0
                };
                var shop = FreezeReceiptShopInfo(
                    await GetShopInfoNoLockAsync().ConfigureAwait(false));
                refundSale.ReceiptShopSnapshotJson = SerializeReceiptShopSnapshot(shop);

                var refundLines = selected.Select(x => new SaleLine
                {
                    ProductId = x.ProductId,
                    Barcode = x.Barcode ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    Quantity = x.QtyToRefund,
                    UnitPrice = x.UnitPriceMinor,
                    LineTotal = -Math.Abs(x.QtyToRefund * x.UnitPriceMinor),
                    RelatedOriginalLineId = x.OriginalLineId
                }).ToList();

                var refundSaleId = await _sales.InsertRefundOrVoidAsync(
                    refundSale,
                    refundLines,
                    req.IsFullVoid ? original.Id : (long?)null,
                    AuditActions.RefundCreate,
                    savedRefundSaleId =>
                    {
                        var voided = req.IsFullVoid ? "true" : "false";
                        return AuditDetails.Kv(new (string k, string v)[]
                        {
                            ("originalSaleId", original.Id.ToString()),
                            ("refundSaleId", savedRefundSaleId.ToString()),
                            ("isFullVoid", req.IsFullVoid.ToString()),
                            ("voided", voided),
                            ("totalMinor", refundSale.Total.ToString()),
                            ("lines", refundLines.Count.ToString())
                        });
                    }).ConfigureAwait(false);

                var completed = new SaleCompleted(refundSale, refundLines);
                QueueSalesOutboxSyncNoThrow();
                var receipt42 = BuildRefundReceiptPreview(completed, true, shop);
                var receipt32 = BuildRefundReceiptPreview(completed, false, shop);

                if (effectiveAutoPrint)
                {
                    try
                    {
                        var receiptText = useReceipt42 ? receipt42 : receipt32;
                        automaticPrintRequest = await CreateReceiptPrintRequestNoLockAsync(
                            receiptText,
                            useReceipt42,
                            "REFUND_" + refundSale.Code,
                            automaticAfterSale: true,
                            installedPrinters: installedPrinters).ConfigureAwait(false);
                    }
                    catch (Exception printEx)
                    {
                        _logger.LogError(printEx, "POS refund print failed");
                    }
                }

                result = new RefundCreateResult
                {
                    RefundSaleId = refundSale.Id,
                    RefundSaleCode = refundSale.Code,
                    Receipt42 = receipt42,
                    Receipt32 = receipt32,
                    TotalMinor = refundSale.Total
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS create refund failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }

            if (automaticPrintRequest != null)
            {
                try
                {
                    await _receiptPrinter.PrintAsync(
                        automaticPrintRequest.ReceiptText,
                        automaticPrintRequest.Options).ConfigureAwait(false);
                }
                catch (Exception printEx)
                {
                    _logger.LogError(printEx, "POS refund print failed");
                }
            }

            return result;
        }

        public async Task<PosWorkflowSnapshot> IncreaseQtyAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, barcode, StringComparison.Ordinal));
                if (line == null) return await BuildSnapshotAsync(string.Empty);
                _session.SetQuantity(line.Barcode, line.Quantity + 1);
                return await BuildSnapshotAsync(PosLocalization.F("pos.status.quantityPlus", 1));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> DecreaseQtyAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, barcode, StringComparison.Ordinal));
                if (line == null) return await BuildSnapshotAsync(string.Empty);
                var next = line.Quantity - 1;
                if (next < 1) next = 1;
                _session.SetQuantity(line.Barcode, next);
                return await BuildSnapshotAsync(PosLocalization.F("pos.status.quantityMinus", 1));
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Imposta la quantità della riga con il barcode dato (0 = rimuovi riga).</summary>
        public async Task<PosWorkflowSnapshot> SetQtyAsync(string barcode, int qty)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var code = (barcode ?? string.Empty).Trim();
                if (code.Length == 0) return await BuildSnapshotAsync(string.Empty);
                var line = _session.Lines.FirstOrDefault(x => string.Equals(x.Barcode, code, StringComparison.Ordinal));
                if (line == null) return await BuildSnapshotAsync(string.Empty);
                _session.SetQuantity(code, qty <= 0 ? 0 : qty);
                return await BuildSnapshotAsync(PosLocalization.F("pos.status.quantityUpdated", qty));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS SetQty failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Imposta la quantità della riga identificata da LineKey (indice nella lista). Funziona anche per righe manuali.
        /// Nota: LineKey = index è una patch veloce; la riga è ancora aggiornata tramite barcode interno (_session.SetQuantity(line.Barcode, qty)).
        /// In futuro preferire una chiave riga stabile (es. id univoco) per evitare fragilità con barcode duplicati o riordini.</summary>
        public async Task<PosWorkflowSnapshot> SetQtyByLineAsync(string lineKey, int qty)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(lineKey) || !int.TryParse(lineKey, out var index))
                    return await BuildSnapshotAsync(string.Empty);
                if (index < 0 || index >= _session.Lines.Count)
                    return await BuildSnapshotAsync(string.Empty);
                var line = _session.Lines[index];
                if (DiscountKeys.IsDiscount(line.Barcode ?? ""))
                    return await BuildSnapshotAsync(string.Empty);
                _session.SetQuantity(line.Barcode ?? "", qty <= 0 ? 0 : qty);
                return await BuildSnapshotAsync(PosLocalization.F("pos.status.quantityUpdated", qty));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS SetQtyByLine failed");
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> RemoveLineAsync(string barcode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.RemoveLine(barcode);
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.lineRemoved"));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ApplyCartDiscountPercentAsync(int percent)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyCartDiscountPercent(percent);
                return await BuildSnapshotAsync(percent <= 0 ? PosLocalization.T("pos.status.cartDiscountRemoved") : PosLocalization.T("pos.status.cartDiscountApplied"));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ApplyLineDiscountPercentAsync(string barcode, int percent)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyLineDiscountPercent(barcode, percent);
                return await BuildSnapshotAsync(percent <= 0 ? PosLocalization.T("pos.status.discountRemoved") : PosLocalization.T("pos.status.discountUpdated"));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ApplyLineDiscountAmountAsync(string barcode, int amountMinor)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyLineDiscountAmount(barcode, amountMinor);
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.amountDiscountApplied"));
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Applica sconto riga impostando il prezzo unitario finale desiderato (sempre unitario, non totale riga). 0 = rimuovi sconto.</summary>
        public async Task<PosWorkflowSnapshot> ApplyLineDiscountByFinalPriceAsync(string barcode, long finalUnitPriceMinor)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ApplyLineDiscountByFinalUnitPrice(barcode, finalUnitPriceMinor);
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.discountUpdated"));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ClearCartDiscountAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.ClearCartDiscount();
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.cartDiscountRemoved"));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<RecentSaleItem>> GetRecentSalesAsync(int limit = 20)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.LastSalesAsync(limit).ConfigureAwait(false);
                return rows.Select(x => new RecentSaleItem
                {
                    SaleId = x.Id,
                    SaleCode = x.Code ?? string.Empty,
                    CreatedAtMs = x.CreatedAt,
                    TotalMinor = x.Total,
                    Kind = x.Kind,
                    RelatedSaleId = x.RelatedSaleId,
                    VoidedBySaleId = x.VoidedBySaleId
                }).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<RecentSaleItem>> GetSalesBetweenAsync(long fromMs, long toMs, int? operatorId = null, bool includeFiscalPrinted = true)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.GetSalesBetweenAsync(fromMs, toMs, operatorId, includeFiscalPrinted).ConfigureAwait(false);
                return rows.Select(x => new RecentSaleItem
                {
                    SaleId = x.Id,
                    SaleCode = x.Code ?? string.Empty,
                    CreatedAtMs = x.CreatedAt,
                    TotalMinor = x.Total,
                    Kind = x.Kind,
                    RelatedSaleId = x.RelatedSaleId,
                    VoidedBySaleId = x.VoidedBySaleId
                }).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<RecentSaleItem>> GetSalesByCodeFilterAsync(string codeFilter, bool includeFiscalPrinted = true)
        {
            if (string.IsNullOrWhiteSpace(codeFilter))
                return Array.Empty<RecentSaleItem>();
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var rows = await _sales.GetByCodeLikeAsync(codeFilter, includeFiscalPrinted).ConfigureAwait(false);
                return rows.Select(x => new RecentSaleItem
                {
                    SaleId = x.Id,
                    SaleCode = x.Code ?? string.Empty,
                    CreatedAtMs = x.CreatedAt,
                    TotalMinor = x.Total,
                    Kind = x.Kind,
                    RelatedSaleId = x.RelatedSaleId,
                    VoidedBySaleId = x.VoidedBySaleId
                }).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<SaleDetailResult> GetSaleDetailsAsync(long saleId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sale = await _sales.GetByIdAsync(saleId).ConfigureAwait(false);
                if (sale == null)
                    return null;
                var lines = await _sales.GetLinesBySaleIdAsync(saleId).ConfigureAwait(false);
                return new SaleDetailResult { Sale = sale, Lines = lines };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosPrintResult> PrintReceiptBySaleIdAsync(long saleId, bool use42)
        {
            var preview = await GetReceiptPreviewBySaleIdAsync(saleId, use42).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(preview))
                return new PosPrintResult();
            var detail = await GetSaleDetailsAsync(saleId).ConfigureAwait(true);
            var fileTag = string.IsNullOrWhiteSpace(detail?.Sale?.Code)
                ? "SALE_ID_" + saleId.ToString(CultureInfo.InvariantCulture)
                : "SALE_" + detail.Sale.Code;
            return await PrintReceiptTextAsync(preview, use42, fileTag, explicitUserAction: true).ConfigureAwait(true);
        }

        public async Task<string> GetReceiptPreviewBySaleIdAsync(long saleId, bool use42)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sale = await _sales.GetByIdAsync(saleId).ConfigureAwait(false);
                if (sale == null) return string.Empty;
                var lines = await _sales.GetLinesBySaleIdAsync(saleId).ConfigureAwait(false);
                var completed = new SaleCompleted(sale, lines);
                var shop = await GetReceiptShopInfoNoLockAsync(sale).ConfigureAwait(false);
                if (sale.Kind == (int)SaleKind.Refund)
                    return BuildRefundReceiptPreview(completed, use42, shop);
                return BuildReceiptPreview(completed, use42, shop);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> GetLastReceiptPreviewAsync(bool use42)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_lastCompletedSale == null)
                    return string.Empty;
                var shop = await GetReceiptShopInfoNoLockAsync(_lastCompletedSale.Sale).ConfigureAwait(false);
                return BuildReceiptPreview(_lastCompletedSale, use42, shop);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> GetLastSaleCodeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return _lastCompletedSale?.Sale?.Code ?? string.Empty;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<ReceiptShopInfo> GetShopInfoAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await GetShopInfoNoLockAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<ReceiptShopInfo> GetShopInfoNoLockAsync()
        {
            var officialSnapshot = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if (officialSnapshot.HasOfficialData)
            {
                return officialSnapshot.ToReceiptShopInfo();
            }

            var name = await _settings.GetStringAsync(KeyShopName).ConfigureAwait(false);
            var address = await _settings.GetStringAsync(KeyShopAddress).ConfigureAwait(false);
            var city = await _settings.GetStringAsync(KeyShopCity).ConfigureAwait(false);
            var rut = await _settings.GetStringAsync(KeyShopRut).ConfigureAwait(false);
            var phone = await _settings.GetStringAsync(KeyShopPhone).ConfigureAwait(false);
            var footer = await _settings.GetStringAsync(KeyShopFooter).ConfigureAwait(false);
            return new ReceiptShopInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Win7 POS Store" : name.Trim(),
                Address = address?.Trim() ?? "",
                City = city?.Trim() ?? "",
                Rut = rut?.Trim() ?? "",
                Phone = phone?.Trim() ?? "",
                Footer = string.IsNullOrWhiteSpace(footer) ? PosLocalization.T("receipt.thanks") : footer.Trim()
            };
        }

        private async Task<ReceiptShopInfo> GetReceiptShopInfoNoLockAsync(Sale sale)
        {
            var snapshot = TryDeserializeReceiptShopSnapshot(sale?.ReceiptShopSnapshotJson);
            return snapshot ?? await GetShopInfoNoLockAsync().ConfigureAwait(false);
        }

        private static ReceiptShopInfo FreezeReceiptShopInfo(ReceiptShopInfo source)
        {
            source = source ?? new ReceiptShopInfo();
            ReceiptShopMetadataPolicy.EnsureValidReceiptShop(source);
            var frozen = new ReceiptShopInfo
            {
                Name = source.Name,
                Address = source.Address,
                BusinessGiro = source.BusinessGiro,
                City = source.City,
                LegalRepresentativeRut = source.LegalRepresentativeRut,
                Rut = source.Rut,
                Phone = source.Phone,
                Footer = source.Footer,
                ShopCode = source.ShopCode,
                ShopStatus = source.ShopStatus,
                Source = source.Source,
                SyncedAtUtc = source.SyncedAtUtc
            };
            ReceiptShopMetadataPolicy.EnsureValidReceiptShop(frozen);
            return frozen;
        }

        private static string SerializeReceiptShopSnapshot(ReceiptShopInfo shop)
        {
            ReceiptShopMetadataPolicy.EnsureValidReceiptShop(shop);
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ReceiptShopInfo)).WriteObject(stream, shop);
                var serialized = Encoding.UTF8.GetString(stream.ToArray());
                ReceiptDocumentPolicy.EnsureValidSnapshotJson(serialized);
                return serialized;
            }
        }

        private static ReceiptShopInfo TryDeserializeReceiptShopSnapshot(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
                return null;

            ReceiptDocumentPolicy.EnsureValidSnapshotJson(serialized);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(serialized)))
            {
                var shop = new DataContractJsonSerializer(typeof(ReceiptShopInfo))
                    .ReadObject(stream) as ReceiptShopInfo;
                if (shop == null)
                    throw new SerializationException("Receipt shop snapshot is invalid.");
                ReceiptShopMetadataPolicy.EnsureValidReceiptShop(shop);
                return shop;
            }
        }

        public async Task<OfficialShopSnapshot> GetOfficialShopSnapshotAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosSyncStatusSnapshot> GetPosSyncStatusAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await new PosSyncStatusReader(_factory).ReadAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosCatalogPullOutcome> RepairCatalogAsync(
            CancellationToken cancellationToken = default(CancellationToken),
            IProgress<PosCatalogPullProgress> progress = null)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(PosCatalogPullProgress.ForPhase("catalog"));
                var lane = await PosOnlineSyncSignalBus.TriggerAsync(
                    OnlineSyncLane.CatalogDelta,
                    OnlineSyncLaneTrigger.AdministratorRepair,
                    cancellationToken).ConfigureAwait(false);
                var saleSafe = await PosCatalogPullService
                    .IsCatalogSaleSafeAsync(_factory).ConfigureAwait(false);
                return lane.Success && !lane.CatalogHasMore && saleSafe
                    ? PosCatalogPullOutcome.CompletedOk(
                        lane.CatalogPagesProcessed,
                        productsApplied: lane.CatalogRowsApplied)
                    : PosCatalogPullOutcome.Failure(
                        lane.Code,
                        lane.AuthenticationDenied,
                        lane.CatalogHasMore,
                        lane.CatalogPagesProcessed,
                        productsApplied: lane.CatalogRowsApplied);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<int> GetFiscalBoletaNumberAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var v = await _settings.GetIntAsync(KeyFiscalBoletaNumber).ConfigureAwait(false);
                return v ?? 0;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<int> ReserveFiscalBoletaNumberAsync(int requestedNumber)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _settings
                    .ReserveMonotonicIntAsync(KeyFiscalBoletaNumber, requestedNumber)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosPrintResult> PrintReceiptTextAsync(
            string receiptText,
            bool use42,
            string fileTag,
            bool isFiscalPrint = false,
            bool automaticAfterSale = false,
            bool explicitUserAction = false)
        {
            PrinterHardwareSafety.DemandHardwareOutputAllowed();
            // Queue discovery and the physical spooler operation stay outside the
            // POS gate: a driver stall must not serialize the rest of the POS.
            var installedPrinters = await GetInstalledPrintersAsync().ConfigureAwait(false);
            ReceiptPrintRequest printRequest;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                printRequest = await CreateReceiptPrintRequestNoLockAsync(
                    receiptText,
                    use42,
                    fileTag,
                    isFiscalPrint,
                    automaticAfterSale,
                    explicitUserAction,
                    installedPrinters).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            await _receiptPrinter.PrintAsync(printRequest.ReceiptText, printRequest.Options).ConfigureAwait(false);
            return printRequest.Result;
        }

        public async Task<PosWorkflowSnapshot> GetSnapshotAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var toRemove = new List<string>();
                foreach (var x in _session.Lines)
                {
                    if (DiscountKeys.IsDiscount(x.Barcode ?? "")) continue;
                    if ((x.Barcode ?? "").StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase)) continue;
                    var product = await _products.GetByBarcodeAsync(x.Barcode ?? "").ConfigureAwait(false);
                    if (product == null) toRemove.Add(x.Barcode ?? "");
                }
                foreach (var b in toRemove)
                    _session.RemoveLine(b);
                var status = toRemove.Count > 0 ? "Prodotto rimosso dal carrello: non più presente nel database." : string.Empty;
                return await BuildSnapshotAsync(status).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<PosWorkflowSnapshot> ClearCartAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session.Clear();
                return await BuildSnapshotAsync(PosLocalization.T("pos.status.cartCleared"));
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<SuspendCartResult> SuspendCartAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.Lines.Count == 0)
                    return new SuspendCartResult { Success = false, Message = PosLocalization.T("pos.status.cartEmpty") };

                var lines = _session.Lines.Select(x => new Data.Repositories.HeldCartLineRow
                {
                    Barcode = x.Barcode ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    UnitPrice = x.UnitPrice,
                    Qty = x.Quantity
                }).ToList();

                var createdAtMs = UnixTime.NowMs();
                var holdId = "H-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                var total = _session.Total;

                await _heldCarts.CreateHoldAsync(holdId, createdAtMs, total, lines).ConfigureAwait(false);
                _session.Clear();

                return new SuspendCartResult { Success = true, HoldId = holdId, Message = PosLocalization.T("pos.status.cartSuspended") };
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<HeldCartItem>> GetHeldCartsAsync()
        {
            var rows = await _heldCarts.ListHoldsAsync().ConfigureAwait(false);
            return rows.Select(x => new HeldCartItem
            {
                HoldId = x.HoldId,
                CreatedAtMs = x.CreatedAtMs,
                TotalMinor = x.TotalMinor,
                TimeText = FormatHoldTime(x.CreatedAtMs)
            }).ToList();
        }

        /// <summary>Read-only preview of held cart lines (does not recover or delete).</summary>
        public async Task<IReadOnlyList<HoldLineDisplay>> PeekHeldCartLinesAsync(string holdId)
        {
            if (string.IsNullOrEmpty(holdId)) return Array.Empty<HoldLineDisplay>();
            var lines = await _heldCarts.LoadHoldLinesAsync(holdId).ConfigureAwait(false);
            return lines.Select(x => new HoldLineDisplay
            {
                Barcode = x.Barcode ?? string.Empty,
                Name = x.Name ?? string.Empty,
                UnitPrice = x.UnitPrice,
                Qty = x.Qty
            }).ToList();
        }

        public async Task DeleteHeldCartAsync(string holdId)
        {
            await _heldCarts.DeleteHoldAsync(holdId).ConfigureAwait(false);
        }

        public async Task<PosWorkflowSnapshot> RecoverHeldCartAsync(string holdId)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var lines = await _heldCarts.LoadHoldLinesAsync(holdId).ConfigureAwait(false);
                if (lines.Count == 0)
                    return await BuildSnapshotAsync(PosLocalization.T("pos.status.heldCartEmpty"));

                var restored = lines.Select(x => new RestoredLine
                {
                    ProductId = null,
                    Barcode = x.Barcode,
                    Name = x.Name,
                    UnitPrice = x.UnitPrice,
                    Quantity = x.Qty
                }).ToList();

                _session.ReplaceWithLines(restored);
                await _heldCarts.DeleteHoldAsync(holdId).ConfigureAwait(false);

                return await BuildSnapshotAsync(PosLocalization.T("pos.status.cartRecovered"));
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string FormatHoldTime(long ms)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return dt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }

        private async Task EnsureDemoProductsAsync()
        {
            if (!_options.IsDemo)
            {
                _logger.LogInfo("POS demo seed skipped: IsDemo=false");
                return;
            }

            var existing = await _products.ListAllAsync().ConfigureAwait(false);
            if (existing.Count > 0)
            {
                _logger.LogInfo("POS demo seed skipped: products already exist");
                return;
            }

            var demo = new[]
            {
                new Product { Barcode = "1234567890123", Name = "Coca Cola 500ml", UnitPrice = 1000 },
                new Product { Barcode = "9876543210000", Name = "Water 500ml", UnitPrice = 700 },
                new Product { Barcode = "1111111111111", Name = "Snack Bar", UnitPrice = 250 }
            };

            foreach (var item in demo)
                await _products.UpsertAsync(item).ConfigureAwait(false);
            _logger.LogInfo("POS demo seed inserted: " + demo.Length);
        }

        private async Task<PosWorkflowSnapshot> BuildSnapshotAsync(string status)
        {
            var lines = new List<PosCartLine>();
            var index = 0;
            // LineKey = index: patch veloce; in futuro usare chiave riga stabile (id univoco)
            foreach (var x in _session.Lines)
            {
                var stockQty = 0;
                long discountAmountMinor = 0;
                int discountPercent = 0;
                if (!DiscountKeys.IsDiscount(x.Barcode ?? "") && !(x.Barcode ?? "").StartsWith("MANUAL:", StringComparison.OrdinalIgnoreCase))
                {
                    var details = await _products.GetDetailsByBarcodeAsync(x.Barcode ?? "").ConfigureAwait(false);
                    if (details != null) stockQty = details.StockQty;
                }
                if (!DiscountKeys.IsDiscount(x.Barcode ?? ""))
                {
                    var discLine = _session.Lines.FirstOrDefault(d => DiscountKeys.IsLineDiscountFor(d.Barcode ?? "", x.Barcode ?? ""));
                    if (discLine != null)
                    {
                        discountAmountMinor = discLine.LineTotal < 0 ? -discLine.LineTotal : 0;
                        var (_, pct) = DiscountKeys.ParseLinePct(discLine.Barcode ?? "");
                        discountPercent = pct ?? (x.LineTotal > 0 ? (int)Math.Round(discountAmountMinor * 100.0 / x.LineTotal, MidpointRounding.AwayFromZero) : 0);
                    }
                }
                lines.Add(new PosCartLine
                {
                    LineKey = index.ToString(),
                    Barcode = x.Barcode ?? "",
                    Name = x.Name ?? "",
                    Quantity = x.Quantity,
                    UnitPrice = x.UnitPrice,
                    LineTotal = x.LineTotal,
                    StockQty = stockQty,
                    DiscountAmountMinor = discountAmountMinor,
                    DiscountPercent = discountPercent
                });
                index++;
            }

            long subtotalBeforeDiscounts = 0;
            foreach (var x in _session.Lines)
            {
                if (!DiscountKeys.IsDiscount(x.Barcode ?? ""))
                    subtotalBeforeDiscounts += x.LineTotal;
            }

            return new PosWorkflowSnapshot
            {
                Lines = lines,
                Subtotal = subtotalBeforeDiscounts,
                Total = _session.Total,
                Status = status ?? string.Empty
            };
        }

        private static string BuildReceiptPreview(SaleCompleted completed, bool use42, ReceiptShopInfo shop = null)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            return PosReceiptTextRenderer.BuildReceipt(
                completed.Sale,
                completed.Lines,
                use42,
                shop);
        }

        private static string BuildRefundReceiptPreview(SaleCompleted completed, bool use42, ReceiptShopInfo shop = null)
        {
            return BuildReceiptPreview(completed, use42, shop);
        }

        private void QueueSalesOutboxSyncNoThrow()
        {
            if (App.IsSafeStart) return;
            PosOnlineSyncSignalBus.Signal(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.LocalCommit);
        }

        private async Task<PosPrinterSettings> ReadPrinterSettingsNoLockAsync()
        {
            var printerName = await _settings.GetStringAsync(KeyPrinterName).ConfigureAwait(false);
            if (printerName == null)
                printerName = await _settings.GetStringAsync(LegacyKeyPrinterName).ConfigureAwait(false);
            printerName = printerName ?? string.Empty;

            var copies = await _settings.GetIntAsync(KeyPrinterCopies).ConfigureAwait(false);
            if (!copies.HasValue)
                copies = await _settings.GetIntAsync(LegacyKeyPrinterCopies).ConfigureAwait(false);
            var persistedCopies = copies ?? ReceiptPrintOptions.MinimumCopies;
            var copyCount = ReceiptPrintOptions.IsValidCopyCount(persistedCopies)
                ? persistedCopies
                : ReceiptPrintOptions.MinimumCopies;

            var receiptEnabled = await _settings.GetBoolAsync(KeyReceiptEnabled).ConfigureAwait(false);
            var autoPrint = await _settings.GetBoolAsync(KeyAutoPrint).ConfigureAwait(false);
            if (!autoPrint.HasValue)
                autoPrint = await _settings.GetBoolAsync(LegacyKeyAutoPrint).ConfigureAwait(false);
            var allowWindowsDefault = await _settings.GetBoolAsync(KeyAllowWindowsDefault).ConfigureAwait(false);
            var allowVirtualPrinters = await _settings.GetBoolAsync(KeyAllowVirtualPrinters).ConfigureAwait(false);
            var cashDrawerCmd = await _settings.GetStringAsync(KeyCashDrawerCommand).ConfigureAwait(false);
            if (cashDrawerCmd == null)
                cashDrawerCmd = await _settings.GetStringAsync(LegacyKeyCashDrawerCommand).ConfigureAwait(false);
            if (cashDrawerCmd == null)
                cashDrawerCmd = DefaultCashDrawerCommand;
            var cashDrawerEnabled = await _settings.GetBoolAsync(KeyCashDrawerEnabled).ConfigureAwait(false);
            var cashDrawerMode = await _settings.GetStringAsync(KeyCashDrawerMode).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cashDrawerMode))
                cashDrawerMode = cashDrawerEnabled == true ? CashDrawerModePrinterKick : CashDrawerModeDisabled;
            if (!string.Equals(cashDrawerMode, CashDrawerModePrinterKick, StringComparison.OrdinalIgnoreCase))
                cashDrawerMode = CashDrawerModeDisabled;
            var cashDrawerPrinterName = await _settings.GetStringAsync(KeyCashDrawerPrinterName).ConfigureAwait(false) ?? string.Empty;
            var cashDrawerOpenOnCashSale = await _settings.GetBoolAsync(KeyCashDrawerOpenOnCashSale).ConfigureAwait(false);

            return new PosPrinterSettings
            {
                PrinterName = printerName,
                Copies = copyCount,
                ReceiptEnabled = receiptEnabled ?? false,
                AutoPrint = autoPrint ?? false,
                AllowWindowsDefault = allowWindowsDefault ?? false,
                AllowVirtualPrinters = allowVirtualPrinters ?? false,
                CashDrawerCommand = cashDrawerCmd,
                CashDrawerEnabled = cashDrawerEnabled ?? false,
                CashDrawerMode = cashDrawerMode,
                CashDrawerPrinterName = cashDrawerPrinterName,
                CashDrawerOpenOnCashSale = cashDrawerOpenOnCashSale ?? true
            };
        }

        private async Task<ReceiptPrintRequest> CreateReceiptPrintRequestNoLockAsync(
            string receiptText,
            bool use42,
            string fileTag,
            bool isFiscalPrint = false,
            bool automaticAfterSale = false,
            bool explicitUserAction = false,
            IReadOnlyList<InstalledPrinterInfo> installedPrinters = null)
        {
            var text = receiptText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException(PosLocalization.T("printer.receiptTextEmpty"));

            var printer = await ReadPrinterSettingsNoLockAsync().ConfigureAwait(false);
            var resolvedPrinter = ResolveReceiptPrinterOrThrow(
                printer,
                installedPrinters,
                automaticAfterSale,
                explicitUserAction);
            return new ReceiptPrintRequest
            {
                ReceiptText = text,
                Options = new ReceiptPrintOptions
                {
                    PrinterName = resolvedPrinter.Name,
                    Copies = ReceiptPrintOptions.IsValidCopyCount(printer.Copies)
                        ? printer.Copies
                        : ReceiptPrintOptions.MinimumCopies,
                    CharactersPerLine = use42 ? 42 : 32,
                    SaleCodeForBarcode = ExtractSaleCodeForBarcode(fileTag),
                    UseReceiptHeaderStyle = !isFiscalPrint
                },
                Result = new PosPrintResult()
            };
        }

        public async Task OpenCashDrawerAsync()
        {
            PrinterHardwareSafety.DemandHardwareOutputAllowed();
            // Refresh/cache the inventory before taking _gate so a slow spooler
            // cannot hold the POS operation lock for the discovery timeout.
            var installedPrinters = await GetInstalledPrintersAsync().ConfigureAwait(false);
            ReceiptPrintOptions drawerOptions = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var printer = await ReadPrinterSettingsNoLockAsync().ConfigureAwait(false);
                if (!printer.CashDrawerEnabled) return;
                if (!string.Equals(printer.CashDrawerMode, CashDrawerModePrinterKick, StringComparison.OrdinalIgnoreCase)) return;
                if (!WindowsSpoolerReceiptPrinter.IsCashDrawerCommandValid(printer.CashDrawerCommand))
                    throw new InvalidOperationException(PosLocalization.T("printer.testInvalidCommand"));
                var drawerPrinter = ResolveCashDrawerPrinterOrThrow(printer, installedPrinters);
                drawerOptions = new ReceiptPrintOptions
                {
                    PrinterName = drawerPrinter.Name,
                    CashDrawerCommand = printer.CashDrawerCommand ?? string.Empty
                };
            }
            finally
            {
                _gate.Release();
            }

            if (drawerOptions != null)
                await _receiptPrinter.OpenCashDrawerAsync(drawerOptions).ConfigureAwait(false);
        }

        /// <summary>Testa il cassetto portamonete con i parametri forniti (senza salvare in impostazioni). Non usa fallback sulla stampante predefinita.</summary>
        public async Task TestCashDrawerAsync(string printerName, string cashDrawerCommand)
        {
            PrinterHardwareSafety.DemandHardwareOutputAllowed();
            var installedPrinters = await GetInstalledPrintersAsync().ConfigureAwait(false);
            var resolvedPrinter = ResolvePrinterNameOrThrow(
                printerName,
                installedPrinters,
                allowWindowsDefault: false,
                allowVirtualPrinters: false,
                requirePhysicalOutput: true);
            var rawCommand = cashDrawerCommand ?? string.Empty;
            if (!WindowsSpoolerReceiptPrinter.IsCashDrawerCommandValid(rawCommand))
                throw new InvalidOperationException(PosLocalization.T("printer.testInvalidCommand"));
            var cmd = rawCommand.Trim();

            await _receiptPrinter.OpenCashDrawerAsync(new ReceiptPrintOptions
            {
                PrinterName = resolvedPrinter.Name,
                CashDrawerCommand = cmd
            }).ConfigureAwait(false);
        }

        private static InstalledPrinterInfo ResolveReceiptPrinterOrThrow(
            PosPrinterSettings settings,
            IReadOnlyList<InstalledPrinterInfo> installedPrinters,
            bool automaticAfterSale,
            bool explicitUserAction)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (!settings.ReceiptEnabled)
                throw new InvalidOperationException(PosLocalization.T("printer.receiptDisabledSaleSaved"));

            return ResolvePrinterNameOrThrow(
                settings.PrinterName,
                installedPrinters,
                settings.AllowWindowsDefault,
                settings.AllowVirtualPrinters,
                requirePhysicalOutput: automaticAfterSale && !explicitUserAction);
        }

        private static InstalledPrinterInfo ResolveCashDrawerPrinterOrThrow(
            PosPrinterSettings settings,
            IReadOnlyList<InstalledPrinterInfo> installedPrinters)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var printerName = string.IsNullOrWhiteSpace(settings.CashDrawerPrinterName)
                ? settings.PrinterName
                : settings.CashDrawerPrinterName;

            return ResolvePrinterNameOrThrow(
                printerName,
                installedPrinters,
                allowWindowsDefault: false,
                allowVirtualPrinters: false,
                requirePhysicalOutput: true);
        }

        private static InstalledPrinterInfo ResolvePrinterNameOrThrow(
            string printerName,
            IReadOnlyList<InstalledPrinterInfo> installedPrinters,
            bool allowWindowsDefault,
            bool allowVirtualPrinters,
            bool requirePhysicalOutput)
        {
            var requestedName = (printerName ?? string.Empty).Trim();
            var usingWindowsDefault = false;

            if (requestedName.Length == 0)
            {
                if (!allowWindowsDefault)
                    throw new InvalidOperationException(PosLocalization.T("printer.saleSavedPrinterNotConfigured"));

                var defaultPrinter = (installedPrinters ?? Array.Empty<InstalledPrinterInfo>())
                    .FirstOrDefault(x => x != null && x.IsDefault);
                requestedName = defaultPrinter == null ? string.Empty : (defaultPrinter.Name ?? string.Empty).Trim();
                usingWindowsDefault = true;
            }

            if (requestedName.Length == 0)
                throw new InvalidOperationException(PosLocalization.T("printer.saleSavedPrinterNotConfigured"));

            var resolved = (installedPrinters ?? Array.Empty<InstalledPrinterInfo>())
                .FirstOrDefault(x => x != null && string.Equals(
                    x.Name,
                    requestedName,
                    StringComparison.OrdinalIgnoreCase));
            if (resolved == null)
            {
                throw new InvalidOperationException(PosLocalization.Current.Format(
                    "printer.saleSavedPrinterUnavailable",
                    requestedName));
            }

            if (!resolved.IsAvailable)
            {
                throw new InvalidOperationException(PosLocalization.Current.Format(
                    "printer.saleSavedPrinterUnavailable",
                    resolved.Name));
            }

            if (!resolved.IsInventoryFresh)
            {
                throw new InvalidOperationException(PosLocalization.Current.Format(
                    "printer.saleSavedPrinterUnavailable",
                    resolved.Name));
            }

            if (usingWindowsDefault && !allowWindowsDefault)
                throw new InvalidOperationException(PosLocalization.T("printer.saleSavedPrinterNotConfigured"));

            if (!resolved.IsPhysical && (requirePhysicalOutput || !allowVirtualPrinters))
            {
                throw new InvalidOperationException(PosLocalization.Current.Format(
                    "printer.saleSavedVirtualPrinterBlocked",
                    resolved.Name));
            }

            return resolved;
        }

        private static string BuildPrinterTestReceipt(
            ReceiptShopInfo shop,
            bool use42,
            DateTimeOffset createdAt)
        {
            var sale = new Sale
            {
                ClientSaleId = "TEST-NO-SALE",
                Code = "TEST-NO-SALE",
                CreatedAt = createdAt.ToUnixTimeMilliseconds(),
                Total = 14691,
                PaidCash = 7000,
                PaidCard = 7691,
                Change = 0,
                SyncStatus = "test_only"
            };
            var lines = new List<SaleLine>
            {
                new SaleLine
                {
                    Barcode = "TEST-CAFFE",
                    Name = "Caffè più qualità - información",
                    Quantity = 2,
                    UnitPrice = 6173,
                    LineTotal = 12346
                },
                new SaleLine
                {
                    Barcode = "TEST-PINGUINO",
                    Name = "Confezione città pingüino niño",
                    Quantity = 1,
                    UnitPrice = 2345,
                    LineTotal = 2345
                }
            };
            var preview = BuildReceiptPreview(new SaleCompleted(sale, lines), use42, shop);
            var marker = WrapPrinterTestLine(
                PosLocalization.T("printer.testReceiptMarker"),
                use42 ? 42 : 32);
            return preview + Environment.NewLine + marker;
        }

        private static string WrapPrinterTestLine(string text, int width)
        {
            return string.Join(
                Environment.NewLine,
                ReceiptTextLayout.WrapText(text, width));
        }

        private static string ExtractSaleCodeForBarcode(string fileTag)
        {
            var tag = (fileTag ?? string.Empty).Trim();
            foreach (var prefix in new[] { "SALE_", "REFUND_", "FISCAL_", "LAST_" })
            {
                if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = tag.Substring(prefix.Length).Trim();
                    return LooksLikeSaleCode(candidate) ? candidate : string.Empty;
                }
            }

            return LooksLikeSaleCode(tag) ? tag : string.Empty;
        }

        private static bool LooksLikeSaleCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length < 2)
            {
                return false;
            }

            var first = char.ToUpperInvariant(normalized[0]);
            return (first == 'V' || first == 'R') && normalized.Skip(1).Any(char.IsDigit);
        }

        private static string EscapeCsv(string value)
        {
            return (value ?? string.Empty).Replace(";", ",");
        }

        private sealed class ReceiptPrintRequest
        {
            public string ReceiptText { get; set; }
            public ReceiptPrintOptions Options { get; set; }
            public PosPrintResult Result { get; set; }
        }
    }

    public sealed class RefundPreviewModel
    {
        public long OriginalSaleId { get; set; }
        public string OriginalSaleCode { get; set; } = string.Empty;
        public long OriginalCreatedAtMs { get; set; }
        public long OriginalTotalMinor { get; set; }
        public bool IsAlreadyVoided { get; set; }
        public ReversalEconomicsSnapshot Economics { get; set; }
        public long MaxRefundableMinor { get; set; }
        public List<RefundPreviewLine> Lines { get; set; } = new List<RefundPreviewLine>();
    }

    public sealed class RefundPreviewLine
    {
        public long OriginalLineId { get; set; }
        public long? ProductId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPriceMinor { get; set; }
        public int SoldQty { get; set; }
        public int RefundedQty { get; set; }
        public int RemainingQty { get; set; }
        public int QtyToRefund { get; set; }
    }

    public sealed class PosWorkflowSnapshot
    {
        public List<PosCartLine> Lines { get; set; } = new List<PosCartLine>();
        public long Subtotal { get; set; }
        public long Total { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public sealed class PosPayResult
    {
        public string SaleCode { get; set; } = string.Empty;
        public string ReceiptPreview { get; set; } = string.Empty;
        public PosWorkflowSnapshot Snapshot { get; set; } = new PosWorkflowSnapshot();
    }

    public sealed class PosSaleResult
    {
        public long SaleId { get; set; }
        public string SaleCode { get; set; } = string.Empty;
        public long TotalMinor { get; set; }
        public long PaidMinor { get; set; }
        public long ChangeMinor { get; set; }
        public long CreatedAtMs { get; set; }
        public string Receipt42 { get; set; } = string.Empty;
        public string Receipt32 { get; set; } = string.Empty;
        public PosWorkflowSnapshot Snapshot { get; set; } = new PosWorkflowSnapshot();
    }

    public sealed class PosPrintResult
    {
    }

    public sealed class SaleDetailResult
    {
        public Sale Sale { get; set; }
        public IReadOnlyList<SaleLine> Lines { get; set; }
    }

    public sealed class PosPaymentInfo
    {
        public long CashAmountMinor { get; set; }
        public long CardAmountMinor { get; set; }

        public bool IsValid(long totalMinor)
        {
            if (CashAmountMinor < 0 || CardAmountMinor < 0) return false;
            if (CardAmountMinor > Math.Max(0, totalMinor - CashAmountMinor)) return false;
            return CashAmountMinor + CardAmountMinor >= totalMinor;
        }

        public long GetChangeMinor(long totalMinor)
        {
            if (!IsValid(totalMinor)) return 0;
            var balanceAfterCard = Math.Max(0, totalMinor - CardAmountMinor);
            return Math.Max(0, CashAmountMinor - balanceAfterCard);
        }
    }

    public sealed class SuspendCartResult
    {
        public bool Success { get; set; }
        public string HoldId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public sealed class HeldCartItem
    {
        public string HoldId { get; set; } = string.Empty;
        public long CreatedAtMs { get; set; }
        public long TotalMinor { get; set; }
        public string TimeText { get; set; } = string.Empty;
    }

    public sealed class HoldLineDisplay
    {
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public int Qty { get; set; }
    }

    public sealed class RecentSaleItem
    {
        public long SaleId { get; set; }
        public string SaleCode { get; set; } = string.Empty;
        public long CreatedAtMs { get; set; }
        public long TotalMinor { get; set; }
        public int Kind { get; set; }
        public long? RelatedSaleId { get; set; }
        public long? VoidedBySaleId { get; set; }
    }

    public sealed class PosCartLine
    {
        public string LineKey { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long LineTotal { get; set; }
        public int StockQty { get; set; }
        /// <summary>Importo sconto applicato a questa riga (solo righe prodotto con sconto riga).</summary>
        public long DiscountAmountMinor { get; set; }
        /// <summary>Percentuale sconto (es. 58 per -58%). Solo se sconto riga percentuale.</summary>
        public int DiscountPercent { get; set; }
    }

    public sealed class PosPrinterSettings
    {
        public string PrinterName { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
        public bool ReceiptEnabled { get; set; }
        public bool AutoPrint { get; set; }
        public bool AllowWindowsDefault { get; set; }
        public bool AllowVirtualPrinters { get; set; }
        /// <summary>Istruzione ESC/POS per aprire cassetto (es. "27,112,0,25,250"). Default: "27,112,0,25,250".</summary>
        public string CashDrawerCommand { get; set; } = "27,112,0,25,250";
        /// <summary>Se true, il pulsante "Apri cassa" è attivo.</summary>
        public bool CashDrawerEnabled { get; set; }
        public string CashDrawerMode { get; set; } = "disabled";
        public string CashDrawerPrinterName { get; set; } = string.Empty;
        public bool CashDrawerOpenOnCashSale { get; set; } = true;
    }
}
