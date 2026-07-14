$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Index-OrFail([string]$text, [string]$needle, [string]$message) {
    $index = $text.IndexOf($needle, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        Fail $message
    }
    return $index
}

$required = @(
    "src/Win7POS.Wpf/Pos/PosWorkflowService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs",
    "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml.cs",
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs",
    "src/Win7POS.Data/Online/AtomicRestoreInstaller.cs",
    "src/Win7POS.Data/Repositories/DbMaintenanceRepository.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$startOfDay = Read-Text "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$saleRepo = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$catalogOutboxRepo = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$maintenance = Read-Text "src/Win7POS.Data/Repositories/DbMaintenanceRepository.cs"
$restoreSafety = Read-Text "src/Win7POS.Data/Online/RestoreShopSafetyRepository.cs"
$restoreSafetyTests = Read-Text "tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs"
$atomicInstaller = Read-Text "src/Win7POS.Data/Online/AtomicRestoreInstaller.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$maintenanceDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml.cs"
$combined = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

$restoreStart = Index-OrFail $workflow "public async Task<DbRestoreResult> RestoreDbAsync" "RestoreDbAsync missing"
$restoreEnd = Index-OrFail $workflow "public async Task<string> IntegrityCheckAsync" "RestoreDbAsync end marker missing"
$restoreBody = $workflow.Substring($restoreStart, $restoreEnd - $restoreStart)

$outboxCheck = Index-OrFail $restoreBody "HasUnresolvedSalesSyncOutboxAsync" "restore must check unresolved sales outbox"
$catalogOutboxCheck = Index-OrFail $restoreBody "_catalogImportOutbox.HasUnresolvedAsync" "restore must check unresolved catalog import outbox"
$preBackup = Index-OrFail $restoreBody 'CreateDbBackupCopyNoLock("pos_pre_restore_")' "restore must create pre-restore backup"
$copyRestore = Index-OrFail $restoreBody "new AtomicRestoreInstaller().InstallAsync" "restore must atomically install the validated copy"
$ensureCreated = Index-OrFail $restoreBody "DbInitializer.EnsureCreated(_options)" "restore must run migrations after copy"
$integrity = Index-OrFail $restoreBody "integrity = await _dbMaintenance.IntegrityCheckAsync()" "restore must run live DB integrity check"
$reviewFlag = Index-OrFail $restoreBody "SetBoolAsync(KeyRestoreNeedsSyncReview, true)" "restore must mark sync review required"
$walCheckpoint = Index-OrFail $restoreBody "WalCheckpointAsync()" "restore must checkpoint WAL before copying live DB"
$candidateValidation = Index-OrFail $restoreBody "ValidateCandidateAsync" "restore must validate candidate shop binding before copying live DB"

if ($outboxCheck -gt $preBackup -or $catalogOutboxCheck -gt $preBackup -or $preBackup -gt $copyRestore -or $copyRestore -gt $ensureCreated -or $ensureCreated -gt $integrity -or $integrity -gt $reviewFlag) {
    Fail "restore guard order must be outbox checks -> pre-backup -> restore -> migrations -> integrity -> sync-review flag"
} else {
    Pass "restore guard order is safe"
}

if ($walCheckpoint -gt $preBackup) {
    Fail "restore pre-backup must checkpoint WAL before copying current DB"
} else {
    Pass "restore checkpoints WAL before pre-backup"
}

if ($candidateValidation -gt $copyRestore -or
    $restoreSafety -notmatch "restore_shop_mismatch" -or
    $restoreSafety -notmatch "restore_catalog_shop_mismatch" -or
    $restoreSafety -notmatch "restore_candidate_outbox_unresolved" -or
    $restoreSafetyTests -notmatch "CandidateValidation_RejectsCrossShopSnapshotAndAnyUnresolvedOutbox" -or
    $restoreSafetyTests -notmatch "CandidateValidation_RejectsCatalogBindingMismatch") {
    Fail "restore must reject incoherent official/catalog binding and every unresolved candidate outbox before live DB copy"
} else {
    Pass "restore validates official/catalog binding and rejects every unresolved candidate outbox before copy"
}

if ($restoreBody -notmatch "File\.Copy\(backupDbPath, tempRestorePath, true\)" -or
    $restoreBody -notmatch "InstallAsync\([\s\S]*tempRestorePath" -or
    $restoreBody -match "File\.Copy\(backupDbPath, _options\.DbPath" -or
    $restoreSafetyTests -notmatch "AtomicInstaller_UsesValidatedCopyAfterSourceMutation") {
    Fail "restore must install the already-validated temporary copy and cover source TOCTOU"
} else {
    Pass "restore installs the validated temporary copy and covers source TOCTOU"
}

if ($atomicInstaller -notmatch "catch \(Exception installException\)" -or
    $atomicInstaller -notmatch "File\.Copy\(rollbackDatabasePath, liveDatabasePath, true\)" -or
    $atomicInstaller -notmatch "DeleteSqliteSidecars" -or
    $restoreSafetyTests -notmatch "AtomicInstaller_RollsBackOnPostSwapFailure") {
    Fail "restore post-swap work must roll back the live DB on every failure"
} else {
    Pass "restore post-swap work is fail-atomic with failure-injection coverage"
}

if ($saleRepo -notmatch "WHERE status IN \('pending', 'retry', 'in_progress', 'failed_blocked'\)") {
    Fail "unresolved outbox guard must include pending, retry, in_progress and failed_blocked"
} else {
    Pass "unresolved outbox guard includes pending/retry/in_progress/failed_blocked"
}

if ($catalogOutboxRepo -notmatch "WHERE status IN \('pending', 'retry', 'in_progress', 'failed_blocked'\)") {
    Fail "catalog import unresolved guard must include pending, retry, in_progress and failed_blocked"
} else {
    Pass "catalog import unresolved guard includes pending/retry/in_progress/failed_blocked"
}

if ($workflow -notmatch "KeyRestoreLastPreBackupPath" -or
    $workflow -notmatch "KeyRestoreLastSourcePath" -or
    $workflow -notmatch "KeyRestoreLastIntegrityCheck") {
    Fail "restore audit settings for source/pre-backup/integrity missing"
} else {
    Pass "restore records source, pre-backup and integrity"
}

if ($maintenance -notmatch "PRAGMA integrity_check" -or $maintenance -notmatch "PRAGMA wal_checkpoint\(FULL\)") {
    Fail "DB maintenance must expose integrity_check and WAL checkpoint"
} else {
    Pass "DB maintenance exposes integrity_check and WAL checkpoint"
}

if ($workflow -notmatch "public async Task<string> BackupDbAsync" -or
    $workflow -notmatch "BackupDbAsync[\s\S]*WalCheckpointAsync\(\)[\s\S]*File\.Copy\(_options\.DbPath, outputPath, true\)") {
    Fail "manual DB backup must checkpoint WAL before copying"
} else {
    Pass "manual DB backup checkpoints WAL before copy"
}

if ($startOfDay -notmatch "RestoreNeedsReviewSettingKey" -or
    $startOfDay -notmatch "restore_needs_review" -or
    $startOfDay -notmatch "RunRestoreReconciliationAsync" -or
    $startOfDay -notmatch "CompleteReviewAsync" -or
    $mainWindow -notmatch "ShowRestoreReviewRecoveryAsync" -or
    $maintenanceDialog -notmatch "restoreReviewOnly") {
    Fail "start-of-day must expose a restricted restore reconciliation/review path before MainWindow closes"
} else {
    Pass "start-of-day exposes reconciliation and restricted maintenance review before MainWindow closes"
}

if ($statusReader -notmatch "RestoreNeedsReviewSettingKey" -or
    $statusReader -notmatch "restoreNeedsReview" -or
    $statusReader -notmatch "RestoreReviewText") {
    Fail "sync status must surface restore-needs-review"
} else {
    Pass "sync status surfaces restore-needs-review"
}

if ($translations -notmatch "dbMaintenance.restoreBlockedUnresolvedSales" -or
    $translations -notmatch "dbMaintenance.restoreBlockedUnresolvedCatalogImports" -or
    $translations -notmatch "dbMaintenance.restoreSyncReview" -or
    $translations -notmatch "dbMaintenance.restoreTrustedShopRequired") {
    Fail "restore user-facing safety messages missing"
} else {
    Pass "restore user-facing safety messages present"
}

if ($workflow -notmatch "CompleteRestoreSyncReviewAsync" -or
    $restoreSafety -notmatch "CompleteReviewAsync" -or
    $restoreSafety -notmatch "restore_review_catalog_not_reconciled" -or
    $restoreSafetyTests -notmatch "CompleteReview_RequiresPostRestoreCatalogAndNoUnresolvedOutbox") {
    Fail "safe restore review completion path missing"
} else {
    Pass "restore review completion requires reconciled catalog and resolved outboxes"
}

if ($combined -match "(?i)\b(TRUNCATE\s+TABLE\s+sales_sync_outbox|DROP\s+TABLE\s+sales_sync_outbox|DELETE\s+FROM\s+sales_sync_outbox)\b") {
    Fail "destructive sales_sync_outbox cleanup detected"
} else {
    Pass "no destructive sales_sync_outbox cleanup detected"
}

if ($combined -match "(?i)\b(TRUNCATE\s+TABLE\s+catalog_import_outbox|DROP\s+TABLE\s+catalog_import_outbox|DELETE\s+FROM\s+catalog_import_outbox)\b") {
    Fail "destructive catalog_import_outbox cleanup detected"
} else {
    Pass "no destructive catalog_import_outbox cleanup detected"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
