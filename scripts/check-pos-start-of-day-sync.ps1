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
    "src/Win7POS.Core/Online/StartOfDaySalesDrainPolicy.cs",
    "tests/Win7POS.Core.Tests/Online/StartOfDaySalesDrainPolicyTests.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs",
    "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs",
    "src/Win7POS.Data/Online/CatalogSyncSchedulerPolicy.cs",
    "tests/Win7POS.Core.Tests/Online/SyncSchedulerPolicyTests.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs",
    "docs/ARCHITECTURE/CATALOG_SYNC_POLICY.md"
)

foreach ($path in $required) {
    Require-File $path
}

if ($fail) {
    exit 1
}

$main = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$drainPolicy = Read-Text "src/Win7POS.Core/Online/StartOfDaySalesDrainPolicy.cs"
$drainTests = Read-Text "tests/Win7POS.Core.Tests/Online/StartOfDaySalesDrainPolicyTests.cs"
$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml.cs"
$service = Read-Text "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"
$schedulerPolicy = Read-Text "src/Win7POS.Data/Online/CatalogSyncSchedulerPolicy.cs"
$schedulerTests = Read-Text "tests/Win7POS.Core.Tests/Online/SyncSchedulerPolicyTests.cs"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$catalogPolicyDoc = Read-Text "docs/ARCHITECTURE/CATALOG_SYNC_POLICY.md"

if ($drainPolicy -notmatch "public static class StartOfDaySalesDrainPolicy" -or
    $drainPolicy -notmatch "blockedSales\s*>\s*0[\s\S]{0,180}StartOfDaySalesDrainDecision\.Blocked" -or
    $drainPolicy -notmatch "pendingSales\s*>\s*0\s*\|\|\s*retrySales\s*>\s*0\s*\|\|\s*inProgressSales\s*>\s*0" -or
    $drainPolicy -notmatch "StartOfDaySalesDrainDecision\.ContinueBackground" -or
    $drainPolicy -notmatch "StartOfDaySalesDrainDecision\.Complete") {
    Fail "pure start-of-day sales drain policy missing or incomplete"
} else {
    Pass "pure start-of-day sales drain policy present"
}

foreach ($pendingCase in @("DataRow(0,", "DataRow(25,", "DataRow(26,", "DataRow(60,")) {
    if ($drainTests -notmatch [regex]::Escape($pendingCase)) {
        Fail "deterministic pending-sales case missing: $pendingCase"
    }
}
if ($drainTests -notmatch "RetrySales_ContinueInBackground" -or
    $drainTests -notmatch "ActiveInProgressSales_RemainUnresolved" -or
    $drainTests -notmatch "StaleInProgressSales_RemainUnresolved" -or
    $drainTests -notmatch "BlockedSales_TakePriority" -or
    $drainTests -notmatch "35, 10, 0") {
    Fail "deterministic retry/in-progress/blocked/60-sale drain coverage missing"
} else {
    Pass "deterministic sales drain cases present"
}

if ($service -notmatch "public sealed class PosStartOfDaySyncService" -or $service -notmatch "public sealed class StartOfDaySyncResult") {
    Fail "start-of-day service/result missing"
} else {
    Pass "start-of-day service/result present"
}

foreach ($field in @("CanOpenPos", "ShouldContinueInBackground", "RequiresOperatorAction", "BlockingReason", "StatusMessage", "PendingSales", "RetrySales", "InProgressSales", "BlockedSales", "CatalogStatus", "CatalogSaleSafe")) {
    if ($service -notmatch $field) {
        Fail "StartOfDaySyncResult missing $field"
    }
}
if (-not $fail) { Pass "StartOfDaySyncResult exposes required fields" }

if ($service -notmatch "InProgressSales\s*=\s*ToSafeInt\(outbox\.InProgress\)" -or
    $service -notmatch "result\.InProgressSales\s*>\s*0") {
    Fail "sales in_progress must be refreshed and treated as unresolved"
} else {
    Pass "sales in_progress is included in start-of-day state"
}

$trySalesIndex = $service.IndexOf(
    "await TrySalesSyncAsync(options, result, cancellationToken)",
    [System.StringComparison]::Ordinal)
$refreshAfterSalesIndex = if ($trySalesIndex -ge 0) {
    $service.IndexOf(
        "await RefreshOutboxAsync(result, sales, _factory)",
        $trySalesIndex,
        [System.StringComparison]::Ordinal)
} else { -1 }
$drainDecisionIndex = if ($refreshAfterSalesIndex -ge 0) {
    $service.IndexOf(
        "StartOfDaySalesDrainPolicy.Evaluate(",
        $refreshAfterSalesIndex,
        [System.StringComparison]::Ordinal)
} else { -1 }
if ($trySalesIndex -lt 0 -or
    $refreshAfterSalesIndex -lt 0 -or
    $drainDecisionIndex -lt 0 -or
    $trySalesIndex -gt $refreshAfterSalesIndex -or
    $refreshAfterSalesIndex -gt $drainDecisionIndex -or
    $service -notmatch "salesComplete\s*=\s*salesDrainDecision\s*==\s*StartOfDaySalesDrainDecision\.Complete") {
    Fail "sales completion must be recalculated from refreshed outbox counters"
} else {
    Pass "sales completion is recalculated from refreshed outbox counters"
}

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

if ($service -notmatch "var salesDrain = await TrySalesSyncAsync" -or
    $service -notmatch "salesDrain\.AuthenticationDenied" -or
    $service -notmatch "Task<OutboxDrainResult> TrySalesSyncAsync") {
    Fail "start-of-day must stop the next lane directly from the typed sales auth result"
} else {
    Pass "start-of-day uses typed sales auth-stop before the next lane"
}

if ($service -notmatch "var catalogImportDrain = await TryCatalogImportSyncAsync" -or
    $service -notmatch "catalogImportDrain\.AuthenticationDenied" -or
    $service -notmatch "Task<OutboxDrainResult> TryCatalogImportSyncAsync") {
    Fail "start-of-day must stop the next lane directly from the typed import auth result"
} else {
    Pass "start-of-day uses typed import auth-stop before the next lane"
}

if ($service -notmatch "RecordHeartbeatSkipBestEffortAsync" -or
    $service -notmatch "CatalogSyncDiagnosticsRepository" -or
    $service -notmatch "catalogPullAttempted:\s*false") {
    Fail "start-of-day heartbeat skips must reach shared diagnostics"
} else {
    Pass "start-of-day heartbeat skips reach shared diagnostics"
}

if ($service -notmatch "Task<PosCatalogPullOutcome> TryCatalogDeltaAsync" -or
    $service -notmatch "catalogOutcome\.AuthDenied" -or
    $service -notmatch "TryPullIncrementalCatalogAsync") {
    Fail "start-of-day must preserve typed catalog auth denial before opening POS"
} else {
    Pass "start-of-day preserves typed catalog auth denial"
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

$recoveryExclusionIndex = $main.IndexOf(
    'StartupTrace.Write("online refresh deferred: recovery mode")',
    [System.StringComparison]::Ordinal)
$safeStartExclusionIndex = if ($recoveryExclusionIndex -ge 0) {
    $main.IndexOf(
        'StartupTrace.Write("online refresh skipped: safe-start")',
        $recoveryExclusionIndex,
        [System.StringComparison]::Ordinal)
} else { -1 }
$normalPosSchedulerIndex = if ($safeStartExclusionIndex -ge 0) {
    $main.IndexOf(
        "QueueBackgroundOnlineRefresh(factory);",
        $safeStartExclusionIndex,
        [System.StringComparison]::Ordinal)
} else { -1 }
$startupCatchIndex = if ($normalPosSchedulerIndex -ge 0) {
    $main.IndexOf(
        "catch (Exception ex)",
        $normalPosSchedulerIndex,
        [System.StringComparison]::Ordinal)
} else { -1 }
if ($main -match "startOfDayResult\s*==\s*null\s*\|\|\s*startOfDayResult\.ShouldContinueInBackground" -or
    $recoveryExclusionIndex -lt 0 -or
    $safeStartExclusionIndex -lt 0 -or
    $normalPosSchedulerIndex -lt 0 -or
    $startupCatchIndex -lt 0 -or
    $normalPosSchedulerIndex -gt $startupCatchIndex) {
    Fail "MainWindow must always start the adaptive scheduler after normal POS opening"
} else {
    Pass "MainWindow always starts the scheduler after normal POS opening"
}

if ($main -notmatch "factory\s*==\s*null\s*\|\|\s*App\.IsSafeStart\s*\|\|\s*_recoveryMode" -or
    $main -notmatch "_onlineSchedulerTask\s*!=\s*null\s*&&\s*!_onlineSchedulerTask\.IsCompleted") {
    Fail "adaptive scheduler safe-start/recovery/single-flight guards missing"
} else {
    Pass "adaptive scheduler retains safe-start, recovery and single-flight guards"
}

if ($salesSync -notmatch "MaxOutboxItemsPerRun\s*=\s*25" -or
    $drainTests -notmatch "SixtyPendingSales_DrainAcrossThreeBoundedRuns" -or
    $drainTests -notmatch "35, 10, 0") {
    Fail "automatic drain coverage must preserve the 25-row batch and reach zero"
} else {
    Pass "automatic sales drain covers backlog above 25 through zero"
}

if ($schedulerPolicy -notmatch "idleSeconds\s*=\s*24d\s*\+\s*\(12d\s*\*\s*boundedJitter\)" -or
    $drainTests -notmatch "IdleSchedulerPolling_RemainsBetweenTwentyFourAndThirtySixSeconds" -or
    $catalogPolicyDoc -notmatch "24-36 second spacing" -or
    $catalogPolicyDoc -match "24-36 minute spacing") {
    Fail "idle polling code, deterministic test and documentation must agree on 24-36 seconds"
} else {
    Pass "idle polling code, test and documentation agree on 24-36 seconds"
}

if ($schedulerPolicy -notmatch "result\.Offline" -or
    $schedulerPolicy -notmatch "CatalogSyncScheduleKind\.OfflineQuiet" -or
    $schedulerTests -notmatch "ServerSideRecoveryWithoutNicTransition_ResumesAndResetsBackoff") {
    Fail "endpoint-offline polling must retry without requiring a NIC state transition"
} else {
    Pass "endpoint-offline polling recovers without a NIC state transition"
}

if ($drainTests -notmatch "HealthyPeriodicPolling_NeverSelectsFullCatalog" -or
    $drainTests -notmatch "CatalogSyncTrigger\.Periodic" -or
    $drainTests -notmatch "CatalogSyncMode\.Incremental") {
    Fail "periodic polling must have deterministic no-full coverage"
} else {
    Pass "periodic polling has deterministic no-full coverage"
}

if ($dialogXaml -notmatch "StartOfDayProgressBar" -or $dialogXaml -notmatch "ContinueButton" -or $dialogXaml -notmatch "WaitButton" -or $dialogXaml -notmatch "RetryButton") {
    Fail "start-of-day dialog progress/buttons missing"
} else {
    Pass "start-of-day dialog progress/buttons present"
}

if ($dialogXaml -notmatch "AccessVerifiedPanel" -or
    $dialogXaml -notmatch "SyncDetailsButton" -or
    $dialogXaml -notmatch "ActionHelpText" -or
    $dialog -notmatch "ApplyAuthenticatedAccessStatus" -or
    $dialog -notmatch "SyncDetailsRequested" -or
    $main -notmatch "UpdateOperatorDisplay\(session\);[\s\S]{0,1400}RunStartOfDaySyncAsync\(factory\)" -or
    $main -notmatch "SyncDetailsRequested \+= \(_, __\) => ShowSyncCenterDialog\(dialog\)") {
    Fail "authenticated identity and actionable sync diagnostics must remain visible at the start-of-day gate"
} else {
    Pass "start-of-day separates successful sign-in from the actionable sync gate"
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

foreach ($key in @("startOfDay.title", "startOfDay.subtitle", "startOfDay.accessVerified", "startOfDay.accessVerifiedHelp", "startOfDay.continue", "startOfDay.wait", "startOfDay.checkAgain", "startOfDay.syncDetails", "startOfDay.actionRequiredHelp", "startOfDay.blockCatalogNotSafe", "startOfDay.continueBackground")) {
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
