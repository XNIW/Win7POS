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
    "src/Win7POS.Data/Backup/SqliteOnlineBackup.cs",
    "src/Win7POS.Data/Repositories/DbMaintenanceRepository.cs",
    "src/Win7POS.Data/SqliteConnectionFactory.cs",
    "tests/Win7POS.Core.Tests/Data/PersistenceFoundationTests.cs",
    "tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs"
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
$onlineBackup = Read-Text "src/Win7POS.Data/Backup/SqliteOnlineBackup.cs"
$connectionFactory = Read-Text "src/Win7POS.Data/SqliteConnectionFactory.cs"
$persistenceTests = Read-Text "tests/Win7POS.Core.Tests/Data/PersistenceFoundationTests.cs"
$connectionPolicyTests = Read-Text "tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs"
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
$candidateValidation = Index-OrFail $restoreBody "ValidateCandidateAsync" "restore must validate candidate shop binding before installation"
$exclusiveMaintenance = Index-OrFail $restoreBody "RunExclusiveMaintenanceAsync" "restore must acquire the process-wide connection fence"
$fencedLiveValidation = Index-OrFail $restoreBody "ValidateLivePreSwapAsync" "restore must revalidate live state after the connection drain"
$fencedCandidateValidation = $restoreBody.IndexOf(
    "ValidateCandidateAsync",
    $candidateValidation + "ValidateCandidateAsync".Length,
    [System.StringComparison]::Ordinal)
if ($fencedCandidateValidation -lt 0) {
    Fail "restore must revalidate candidate binding inside the connection fence"
}
$preBackup = Index-OrFail $restoreBody 'CreateDbBackupNoLockAsync("pos_pre_restore_")' "restore must create a verified online pre-restore backup"
$copyRestore = Index-OrFail $restoreBody "new AtomicRestoreInstaller().InstallAsync" "restore must atomically install the validated copy"
$ensureCreated = Index-OrFail $restoreBody "DbInitializer.EnsureCreated(_options)" "restore must run migrations after copy"
$integrity = Index-OrFail $restoreBody "liveValidation = await _dbMaintenance.ValidateAsync()" "restore must run live integrity and foreign-key checks"
$reviewFlag = Index-OrFail $restoreBody "SetBoolAsync(KeyRestoreNeedsSyncReview, true)" "restore must mark sync review required"

if ($outboxCheck -gt $candidateValidation -or
    $catalogOutboxCheck -gt $candidateValidation -or
    $candidateValidation -gt $exclusiveMaintenance -or
    $exclusiveMaintenance -gt $fencedLiveValidation -or
    $fencedLiveValidation -gt $fencedCandidateValidation -or
    $fencedCandidateValidation -gt $preBackup -or
    $preBackup -gt $copyRestore -or
    $copyRestore -gt $ensureCreated -or
    $ensureCreated -gt $integrity -or
    $integrity -gt $reviewFlag) {
    Fail "restore guard order must be preliminary validation -> connection fence -> authoritative live/candidate validation -> verified pre-backup -> atomic install -> migrations -> validation -> sync-review flag"
} else {
    Pass "restore guard order is safe"
}

if ($connectionFactory -notmatch "RunExclusiveMaintenanceAsync" -or
    $connectionFactory -notmatch "_maintenancePending" -or
    $connectionFactory -notmatch "_maintenanceEntered" -or
    $connectionFactory -notmatch "_activeConnections" -or
    $connectionFactory -notmatch "RegisterConnection" -or
    $connectionFactory -notmatch "DefaultMaintenanceDrainTimeout" -or
    $connectionFactory -notmatch "Timed out waiting for active SQLite connections to drain" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_DrainsActiveConnectionsBlocksNewOpenAndAllowsOwnerReentry" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_AllowsConnectionNeededToCompleteDrainWithoutDeadlock" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_DrainTimeoutAbortsBeforeActionAndReleasesFence" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_LeakedOwnerConnectionFailsButReleasesGlobalFence") {
    Fail "restore must complete or safely time out the connection drain, avoid nested-open deadlock, block opens at the zero boundary, and allow maintenance-owner reentry"
} else {
    Pass "process-wide connection fence is covered by concurrency tests"
}

if ($restoreSafety -notmatch "ValidateLivePreSwapAsync" -or
    $restoreSafety -notmatch "restore_live_sales_outbox_unresolved" -or
    $restoreSafety -notmatch "restore_live_catalog_outbox_unresolved" -or
    $restoreSafety -notmatch "restore_live_catalog_epoch_changed" -or
    $restoreSafetyTests -notmatch "LivePreSwapValidation_RejectsOutboxCommittedAfterPreliminaryCheck") {
    Fail "restore must close the pre-fence outbox/shop/epoch TOCTOU before backup or live swap"
} else {
    Pass "fenced pre-swap validation closes the outbox/shop/epoch TOCTOU"
}

if ($candidateValidation -gt $exclusiveMaintenance -or
    $restoreSafety -notmatch "restore_shop_mismatch" -or
    $restoreSafety -notmatch "restore_catalog_shop_mismatch" -or
    $restoreSafety -notmatch "restore_candidate_outbox_unresolved" -or
    $restoreSafetyTests -notmatch "CandidateValidation_RejectsCrossShopSnapshotAndAnyUnresolvedOutbox" -or
    $restoreSafetyTests -notmatch "CandidateValidation_RejectsCatalogBindingMismatch") {
    Fail "restore must reject incoherent official/catalog binding and every unresolved candidate outbox before live DB installation"
} else {
    Pass "restore validates official/catalog binding and rejects every unresolved candidate outbox before installation"
}

if ($restoreBody -notmatch "File\.Copy\(backupDbPath, tempRestorePath, true\)" -or
    $restoreBody -notmatch "InstallAsync\([\s\S]*tempRestorePath" -or
    $restoreBody -match "File\.Copy\(backupDbPath, _options\.DbPath" -or
    $restoreSafetyTests -notmatch "AtomicInstaller_UsesValidatedCopyAfterSourceMutation") {
    Fail "restore must install the already-validated temporary copy and cover source TOCTOU"
} else {
    Pass "restore installs the validated temporary copy and covers source TOCTOU"
}

if ($atomicInstaller -notmatch "File\.Replace\(candidatePath, liveDatabasePath, atomicRollbackPath\)" -or
    $atomicInstaller -notmatch "File\.Replace\(rollbackPath, liveDatabasePath, null\)" -or
    $atomicInstaller -notmatch "PhasePrepared" -or
    $atomicInstaller -notmatch "PhaseCommitted" -or
    $atomicInstaller -notmatch "RecoverInterruptedInstallCore" -or
    $atomicInstaller -notmatch "Flush\(true\)" -or
    $atomicInstaller -notmatch 'databasePath \+ "-journal"' -or
    $restoreSafetyTests -notmatch "AtomicInstaller_RollsBackOnPostSwapFailure" -or
    $persistenceTests -notmatch "Recovery_PreparedBeforeSwapKeepsOldLiveAndRemovesPartialCandidate" -or
    $persistenceTests -notmatch "Recovery_PreparedAfterAtomicSwapRestoresOldLive" -or
    $persistenceTests -notmatch "Recovery_CommittedAfterSwapKeepsNewLiveAndCleansRollback") {
    Fail "restore must use same-directory atomic replacement with durable prepared/committed recovery"
} else {
    Pass "restore swap is fail-atomic and crash recovery is covered"
}

if ($persistenceTests -notmatch "Recovery_CommittedCorruptLiveRestoresValidatedRollback" -or
    $atomicInstaller -notmatch "IsDatabaseValidAsync" -or
    $atomicInstaller -notmatch "TryCleanupCommittedRestore") {
    Fail "committed recovery must validate the live DB, preserve rollback until validation, and defer cleanup safely"
} else {
    Pass "committed recovery validates live state before idempotent cleanup"
}

if ($onlineBackup -notmatch "BackupDatabase" -or
    $onlineBackup -notmatch "ValidateAsync" -or
    $onlineBackup -notmatch "File\.Move\(temporaryPath, finalPath\)" -or
    $workflow -notmatch "_onlineBackup\.CreateVerifiedAsync\(outputPath\)" -or
    $persistenceTests -notmatch "OnlineBackup_ProducesValidatedSnapshotWhileWriterContinues") {
    Fail "manual and pre-restore backups must use the verified SQLite online-backup path"
} else {
    Pass "verified online backup is used and covered under concurrent writes"
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

if ($maintenance -notmatch "PRAGMA integrity_check" -or
    $maintenance -notmatch "PRAGMA foreign_key_check" -or
    $maintenance -notmatch "ValidateAsync" -or
    $maintenance -notmatch "PRAGMA wal_checkpoint\(FULL\)" -or
    $persistenceTests -notmatch "CandidateForeignKeyViolation_IsRejectedBeforeLiveSwap") {
    Fail "DB maintenance must expose combined integrity and foreign-key validation while preserving WAL checkpoint support"
} else {
    Pass "DB maintenance validates integrity and foreign keys and preserves WAL checkpoint support"
}

$backupStart = Index-OrFail $workflow "public async Task<string> BackupDbAsync" "BackupDbAsync missing"
$backupEnd = Index-OrFail $workflow "private async Task<string> CreateDbBackupNoLockAsync" "BackupDbAsync end marker missing"
$backupBody = $workflow.Substring($backupStart, $backupEnd - $backupStart)
if ($backupBody -notmatch "_onlineBackup\.CreateVerifiedAsync\(outputPath\)" -or
    $backupBody -match "File\.Copy\(_options\.DbPath") {
    Fail "manual DB backup must use SQLite online backup and validate the snapshot"
} else {
    Pass "manual DB backup uses a verified SQLite online snapshot"
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
