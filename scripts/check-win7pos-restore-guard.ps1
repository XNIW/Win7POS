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
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
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
$maintenance = Read-Text "src/Win7POS.Data/Repositories/DbMaintenanceRepository.cs"
$combined = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

$restoreStart = Index-OrFail $workflow "public async Task<DbRestoreResult> RestoreDbAsync" "RestoreDbAsync missing"
$restoreEnd = Index-OrFail $workflow "public async Task<string> IntegrityCheckAsync" "RestoreDbAsync end marker missing"
$restoreBody = $workflow.Substring($restoreStart, $restoreEnd - $restoreStart)

$outboxCheck = Index-OrFail $restoreBody "HasUnresolvedSalesSyncOutboxAsync" "restore must check unresolved sales outbox"
$preBackup = Index-OrFail $restoreBody 'CreateDbBackupCopyNoLock("pos_pre_restore_")' "restore must create pre-restore backup"
$copyRestore = Index-OrFail $restoreBody "File.Copy(backupDbPath, _options.DbPath, true)" "restore must copy selected backup after guard"
$ensureCreated = Index-OrFail $restoreBody "DbInitializer.EnsureCreated(_options)" "restore must run migrations after copy"
$integrity = Index-OrFail $restoreBody "IntegrityCheckAsync()" "restore must run integrity check"
$reviewFlag = Index-OrFail $restoreBody "SetBoolAsync(KeyRestoreNeedsSyncReview, true)" "restore must mark sync review required"
$walCheckpoint = Index-OrFail $restoreBody "WalCheckpointAsync()" "restore must checkpoint WAL before copying live DB"

if ($outboxCheck -gt $preBackup -or $preBackup -gt $copyRestore -or $copyRestore -gt $ensureCreated -or $ensureCreated -gt $integrity -or $integrity -gt $reviewFlag) {
    Fail "restore guard order must be outbox -> pre-backup -> restore -> migrations -> integrity -> sync-review flag"
} else {
    Pass "restore guard order is safe"
}

if ($walCheckpoint -gt $preBackup) {
    Fail "restore pre-backup must checkpoint WAL before copying current DB"
} else {
    Pass "restore checkpoints WAL before pre-backup"
}

if ($saleRepo -notmatch "WHERE status IN \('pending', 'retry', 'failed_blocked'\)") {
    Fail "unresolved outbox guard must include pending, retry and failed_blocked"
} else {
    Pass "unresolved outbox guard includes pending/retry/failed_blocked"
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
    $startOfDay -notmatch "Block\(result, `"restore_needs_review`"") {
    Fail "start-of-day must block restore-needs-review"
} else {
    Pass "start-of-day blocks restore-needs-review"
}

if ($statusReader -notmatch "RestoreNeedsReviewSettingKey" -or
    $statusReader -notmatch "restoreNeedsReview" -or
    $statusReader -notmatch "RestoreReviewText") {
    Fail "sync status must surface restore-needs-review"
} else {
    Pass "sync status surfaces restore-needs-review"
}

if ($translations -notmatch "dbMaintenance.restoreBlockedUnresolvedSales" -or
    $translations -notmatch "dbMaintenance.restoreSyncReview") {
    Fail "restore user-facing safety messages missing"
} else {
    Pass "restore user-facing safety messages present"
}

if ($combined -match "(?i)\b(TRUNCATE\s+TABLE\s+sales_sync_outbox|DROP\s+TABLE\s+sales_sync_outbox|DELETE\s+FROM\s+sales_sync_outbox)\b") {
    Fail "destructive sales_sync_outbox cleanup detected"
} else {
    Pass "no destructive sales_sync_outbox cleanup detected"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
