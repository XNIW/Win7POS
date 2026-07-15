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

function Require-File([string]$relativePath) {
    if (-not (Test-Path (Join-Path $repoRoot $relativePath))) {
        Fail "$relativePath missing"
    }
}

function Index-OrFail([string]$text, [string]$needle, [string]$message) {
    $index = $text.IndexOf($needle, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        Fail $message
    }
    return $index
}

$required = @(
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs",
    "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"
)

foreach ($path in $required) {
    Require-File $path
}

if ($fail) {
    exit 1
}

$main = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml.cs"
$service = Read-Text "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"

if ($service -notmatch "public sealed class PosStartOfDaySyncService" -or $service -notmatch "public sealed class StartOfDaySyncResult") {
    Fail "start-of-day service/result missing"
} else {
    Pass "start-of-day service/result present"
}

foreach ($field in @("CanOpenPos", "ShouldContinueInBackground", "RequiresOperatorAction", "BlockingReason", "StatusMessage", "PendingSales", "RetrySales", "BlockedSales", "CatalogStatus", "CatalogSaleSafe")) {
    if ($service -notmatch $field) {
        Fail "StartOfDaySyncResult missing $field"
    }
}
if (-not $fail) { Pass "StartOfDaySyncResult exposes required fields" }

if ($service -notmatch "StartOfDayTotalTimeout\s*=\s*TimeSpan\.FromSeconds\(28\)" -or
    $service -notmatch "HeartbeatTimeout\s*=\s*TimeSpan\.FromSeconds\(4\)" -or
    $service -notmatch "SalesSyncTimeout\s*=\s*TimeSpan\.FromSeconds\(8\)" -or
    $service -notmatch "CatalogDeltaTimeout\s*=\s*TimeSpan\.FromSeconds\(12\)") {
    Fail "start-of-day bounded timeouts missing or changed"
} else {
    Pass "start-of-day bounded timeouts present"
}

if ($service -notmatch "IsCatalogSaleSafeAsync" -or $service -notmatch "catalog_not_sale_safe" -or $service -notmatch "CanOpenPos\s*=\s*false") {
    Fail "start-of-day must block when local catalog is not sale-safe"
} else {
    Pass "start-of-day blocks without sale-safe catalog"
}

if ($service -notmatch "RestoreNeedsReviewSettingKey" -or $service -notmatch "restore_needs_review") {
    Fail "start-of-day must block restore-needs-review"
} else {
    Pass "start-of-day blocks restore-needs-review"
}

if ($service -notmatch "BlockedSales\s*>\s*0" -or $service -notmatch "sales_blocked") {
    Fail "start-of-day must block failed_blocked sales"
} else {
    Pass "start-of-day blocks failed_blocked sales"
}

if ($service -notmatch "response\.Denied" -or $service -notmatch "_store\.Clear\(\)" -or $service -notmatch "auth_denied" -or $service -notmatch "HasStoredAuthDeniedAsync") {
    Fail "start-of-day must block and clear trust on auth denied"
} else {
    Pass "start-of-day blocks auth denied and clears trust"
}

if ($service -notmatch "ContinueLocal" -or $service -notmatch "ShouldContinueInBackground\s*=\s*shouldContinueInBackground" -or $service -notmatch "catalog_timeout" -or $service -notmatch "sales_timeout") {
    Fail "start-of-day must allow sale-safe startup with retryable timeout/background sync"
} else {
    Pass "start-of-day continues in background on retryable timeouts when sale-safe"
}

if ($service -match "OperationCanceledException[\s\S]{0,260}_store\.Clear\(\)") {
    Fail "retryable timeout must not clear trusted device"
} else {
    Pass "retryable timeout does not clear trusted device"
}

$runIndex = Index-OrFail $main "RunStartOfDaySyncAsync(factory)" "MainWindow does not run start-of-day preflight"
$ensureIndex = Index-OrFail $main "EnsurePosViewCreated();" "MainWindow POS lazy creation missing"
if ($runIndex -gt $ensureIndex) {
    Fail "start-of-day preflight must run before PosView creation"
} else {
    Pass "start-of-day preflight runs before PosView creation"
}

if ($main -notmatch "startOfDayResult\.ShouldContinueInBackground[\s\S]{0,180}QueueBackgroundOnlineRefresh") {
    Fail "MainWindow must queue background sync only when start-of-day asks for it"
} else {
    Pass "MainWindow queues background sync from start-of-day result"
}

if ($dialogXaml -notmatch "StartOfDayProgressBar" -or $dialogXaml -notmatch "ContinueButton" -or $dialogXaml -notmatch "WaitButton" -or $dialogXaml -notmatch "RetryButton") {
    Fail "start-of-day dialog progress/buttons missing"
} else {
    Pass "start-of-day dialog progress/buttons present"
}

if ($dialog -notmatch "ContentRendered" -or $dialog -notmatch "RunPreflightAsync" -or $dialog -notmatch "Result\?\.CanOpenPos") {
    Fail "start-of-day dialog must run async preflight and gate Continue on CanOpenPos"
} else {
    Pass "start-of-day dialog runs async preflight and gates Continue"
}

$authIndex = Index-OrFail $statusReader 'failed_auth_denied' "status auth denied priority missing"
$blockedIndex = Index-OrFail $statusReader 'outbox.Blocked > 0 || catalogOutbox.Blocked > 0 || restoreNeedsReview' "status blocked priority missing"
$retryIndex = Index-OrFail $statusReader 'if (outbox.Retry > 0 || catalogOutbox.Retry > 0)' "status retry priority missing"
$pendingIndex = Index-OrFail $statusReader 'if (outbox.PendingOrRetry > 0 || catalogOutbox.PendingOrRetry > 0)' "status pending priority missing"
$catalogIndex = Index-OrFail $statusReader 'if (CatalogRequiresAttention' "status catalog attention priority missing"
$syncIndex = Index-OrFail $statusReader 'if (salesSyncInProgress)' "status sync-in-progress priority missing"
$readyIndex = Index-OrFail $statusReader 'if (catalogSaleSafety.IsSaleSafe && !string.IsNullOrWhiteSpace(catalogSaleSafeAt))' "status catalog-ready priority missing"
if ($authIndex -gt $blockedIndex -or $blockedIndex -gt $retryIndex -or $retryIndex -gt $pendingIndex -or $pendingIndex -gt $catalogIndex -or $catalogIndex -gt $syncIndex -or $syncIndex -gt $readyIndex) {
    Fail "status priority must be auth -> blocked -> retry -> pending -> catalog attention -> sync in progress -> catalog ready"
} else {
    Pass "status priority preserves critical sales/catalog states"
}

foreach ($key in @("startOfDay.title", "startOfDay.subtitle", "startOfDay.continue", "startOfDay.wait", "startOfDay.blockCatalogNotSafe", "startOfDay.continueBackground")) {
    if ($translations -notmatch [regex]::Escape($key)) {
        Fail "translation missing: $key"
    }
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
