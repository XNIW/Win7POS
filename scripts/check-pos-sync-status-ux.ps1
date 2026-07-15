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

$required = @(
    "src/Win7POS.Wpf/MainWindow.xaml",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs",
    "src/Win7POS.Data/Online/CatalogShopStateRepository.cs",
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs",
    "src/Win7POS.Wpf/Pos/PosViewModel.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsViewModel.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) { exit 1 }

$mainXaml = Read-Text "src/Win7POS.Wpf/MainWindow.xaml"
$mainCode = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$reader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$catalogState = Read-Text "src/Win7POS.Data/Online/CatalogShopStateRepository.cs"
$sales = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$catalogOutbox = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$posViewModel = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$shopDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml"
$shopDialogCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml.cs"
$shopViewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsViewModel.cs"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$localization = Read-Text "src/Win7POS.Wpf/Localization/PosLocalization.cs"
$secondaryLocalization = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$uxCopy = $reader + $mainXaml + $shopDialog + $localization + $secondaryLocalization

foreach ($label in @("Online", "Offline", "Non collegato", "Sessione da ricollegare", "Ultimo catalogo", "Ultima vendita inviata", "Vendite in coda", "Da ritentare", "Bloccate", "Ultimo errore", "Negozio", "Dispositivo", "Staff online", "Sessione verificata")) {
    if ($uxCopy -notmatch [regex]::Escape($label)) {
        Fail "sync status UX missing label: $label"
    }
}

if ($mainXaml -notmatch "SyncStatusPill" -or $mainXaml -notmatch "SyncStatusText") { Fail "main header sync status strip missing" } else { Pass "main header sync status strip present" }
if ($mainCode -notmatch "DispatcherTimer" -or $mainCode -notmatch "TimeSpan\.FromSeconds\(30\)") { Fail "sync status refresh timer missing" } else { Pass "sync status refresh timer present" }
if ($mainCode -notmatch "PosSyncStatusReader") { Fail "main shell does not use sync status reader" } else { Pass "main shell uses sync status reader" }
if ($reader -match "DeviceToken|SessionToken|ProtectedDeviceSecret|ProtectedSessionSecret") { Fail "sync status reader must not expose POS secrets" } else { Pass "sync status reader avoids POS secrets" }
if ($reader -notmatch "pos\.catalog\.last_sync_at" -or $reader -notmatch "pos\.sales_sync\.last_success_at") { Fail "sync status reader must show catalog and sales sync timestamps" } else { Pass "sync timestamps present" }
if ($uxCopy -notmatch "Bloccate" -or $sales -notmatch "failed_blocked" -or $sales -notmatch "GetSalesSyncOutboxSummaryAsync") { Fail "outbox summary must expose pending/retry/blocked" } else { Pass "outbox summary exposes pending/retry/blocked" }
if ($reader -notmatch "CatalogImportOutboxRepository" -or $reader -notmatch "GetSummaryAsync" -or $reader -notmatch "pendingCatalogImports" -or $reader -notmatch "catalogOutbox\.Blocked") { Fail "sync status must surface catalog import outbox pending/retry/blocked" } else { Pass "sync status surfaces catalog import outbox" }
if ($reader -notmatch "DetailedPendingOutboxText" -or $reader -notmatch "salesOutbox\.Blocked" -or $reader -notmatch "catalogOutbox\.Blocked") { Fail "sync status must separate sales and catalog import retry/blocked counts" } else { Pass "sync status separates sales/catalog import retry and blocked counts" }
if ($reader -notmatch "BlockedOutboxText" -or $reader -notmatch "RetryOutboxText" -or $reader -notmatch "salesOutbox\.Retry" -or $reader -notmatch "catalogOutbox\.Retry") { Fail "sync status headline must separate sales and catalog retry/blocked counts" } else { Pass "sync status headline separates sales/catalog retry and blocked counts" }
if ($catalogOutbox -notmatch "InProgress" -or $catalogOutbox -notmatch "PendingOrRetry => Pending \+ Retry \+ InProgress") { Fail "catalog import summary must include in_progress as pending work" } else { Pass "catalog import summary includes in_progress" }
if ($uxCopy -notmatch "Sync in corso" -or $reader -notmatch "IsSyncing" -or $salesSync -notmatch "pos\.sales_sync\.in_progress") { Fail "syncing status must be visible while background sync runs" } else { Pass "syncing status visible" }
if ($salesSync -notmatch "Interlocked\.CompareExchange" -or $salesSync -notmatch "Sales sync skipped: already running") { Fail "sales sync service must guard concurrent runs" } else { Pass "sales sync concurrency guard present" }
if ($salesSync -notmatch "pos\.sales_sync\.last_success_at" -or $salesSync -notmatch "pos\.sales_sync\.last_error") { Fail "sales sync diagnostics settings missing" } else { Pass "sales sync diagnostics settings present" }
if ($posViewModel -notmatch "QueueSalesSyncAfterPayment" -or $posViewModel -match "await _service\.TrySyncPendingSalesAsync\(\)\.ConfigureAwait\(true\)") { Fail "payment path must queue sales sync without awaiting remote sync" } else { Pass "payment path queues sales sync without awaiting remote sync" }
if ($sales -notmatch "HasUnresolvedSalesSyncOutboxAsync" -or $workflow -notmatch "HasUnresolvedSalesSyncOutboxAsync" -or $workflow -notmatch "restore blocked") { Fail "restore must block when unresolved outbox rows exist" } else { Pass "restore blocks unresolved outbox rows" }
if ($shopDialog -notmatch "SyncStatusText" -or $shopDialog -notmatch "PendingSalesText") { Fail "shop dialog must surface sync status details" } else { Pass "shop dialog sync details present" }
if ($reader -notmatch "LoadExactnessAsync" -or $reader -notmatch "CatalogCompletenessText" -or $reader -notmatch "CatalogCountsText" -or $reader -notmatch "CatalogSyncModeText") { Fail "sync status must expose catalog exactness, local counts and sync mode" } else { Pass "catalog exactness diagnostics exposed" }
if ($reader -notmatch "CatalogCompletenessStatus\.Verified" -or $reader -notmatch "catalogExactness\.RepairRequired" -or $reader -notmatch "RequiresAttention") { Fail "catalog mismatch/unverified state must require operator attention" } else { Pass "catalog exactness contributes to attention state" }
if ($reader -notmatch "EvaluateSaleSafetyForOfficialShopAsync" -or
    $reader -notmatch "!catalogSaleSafety\.IsSaleSafe" -or
    $reader -notmatch "CatalogSaleSafetyCode" -or
    $catalogState -notmatch "CatalogSaleSafetyEvaluation" -or
    $catalogState -notmatch "EvaluateSaleSafetyAsync" -or
    $catalogState -notmatch "allowLegacyUnbound:\s*false" -or
    $catalogState -notmatch "allowLegacyUnbound:\s*true" -or
    $catalogState -notmatch "TryParseOptionalBinaryFlag" -or
    $catalogState -notmatch "catalog_sale_blocked_repair_state_invalid" -or
    $catalogState -notmatch "throw new InvalidOperationException\(evaluation\.ReasonCode\)") {
    Fail "sync readiness and ordinary-sale persistence must share one shop-bound sale-safety evaluation with a redacted reason code"
} else {
    Pass "sync readiness uses the same reasoned sale-safety evaluation as ordinary-sale persistence"
}
if ($shopDialog -notmatch "CatalogCompletenessText" -or $shopDialog -notmatch "CatalogCountsText" -or $shopDialog -notmatch "CatalogRepairText" -or $shopDialog -notmatch "CatalogSyncModeText") { Fail "shop dialog must display catalog exactness diagnostics" } else { Pass "shop dialog displays catalog exactness diagnostics" }
if ($workflow -notmatch "RepairCatalogAsync" -or $workflow -notmatch "TryRepairCatalogAsync") { Fail "workflow must expose authoritative full catalog repair" } else { Pass "workflow exposes authoritative full catalog repair" }
if ($shopViewModel -notmatch "RepairCatalogCommand" -or $shopViewModel -notmatch "ApplyConfirmDialog\.ShowConfirm" -or $shopViewModel -notmatch "OwnerWindow \?\? DialogOwnerHelper\.GetSafeOwner\(\)") { Fail "shop settings repair must be confirmed and owner-safe" } else { Pass "shop settings repair is confirmed and owner-safe" }
if ($posViewModel -notmatch "PermissionCodes\.DbMaintenance" -or $posViewModel -notmatch "Has\(PermissionCodes\.DbMaintenance\)") { Fail "catalog repair action must be protected by database maintenance permission" } else { Pass "catalog repair action is permission protected" }
if ($shopDialog -notmatch "RepairCatalogCommand" -or $shopDialog -notmatch "DialogActionButtonStyle" -or $shopDialog -notmatch "DialogFooterMargin") { Fail "catalog repair footer action must use shared dialog resources" } else { Pass "catalog repair footer uses shared dialog resources" }
if ($shopDialog -notmatch 'IsEnabled="\{Binding CanClose\}"' -or $shopViewModel -notmatch "CanClose\s*=>\s*!IsBusy" -or $shopViewModel -notmatch "OnPropertyChanged\(nameof\(CanClose\)\)") { Fail "shop settings close action must be disabled while catalog repair is busy" } else { Pass "shop settings close action follows repair busy state" }
if ($shopDialogCode -notmatch "OnClosing\(CancelEventArgs e\)" -or $shopDialogCode -notmatch "CanClose\s*==\s*false" -or $shopDialogCode -notmatch "e\.Cancel\s*=\s*true") { Fail "shop settings must block Escape/Alt+F4 while catalog repair is busy" } else { Pass "shop settings guards Escape/Alt+F4 during repair" }
if ($shopViewModel -notmatch "INotifyPropertyChanged,\s*IDisposable" -or
    $shopViewModel -notmatch "LanguageChanged\s*-=\s*OnLanguageChanged" -or
    $shopViewModel -notmatch "OwnerWindow\s*=\s*null" -or
    $shopDialogCode -notmatch "OnClosed\(EventArgs e\)" -or
    $shopDialogCode -notmatch "viewModel\.Dispose\(\)" -or
    $shopDialogCode -notmatch "DataContext\s*=\s*null") {
    Fail "shop settings must detach localization and release its owner when closed"
} else {
    Pass "shop settings releases localization and owner references on close"
}
if ($shopViewModel -notmatch "IsRepairInProgress" -or
    $shopViewModel -notmatch "SetRepairInProgress\(true\)" -or
    $shopViewModel -notmatch "SetRepairInProgress\(false\)" -or
    $shopViewModel -notmatch "IsBusy\s*=>\s*_activeLoads\s*>\s*0\s*\|\|\s*IsRepairInProgress" -or
    $shopViewModel -match "IsBusy\s*=\s*(true|false)") {
    Fail "catalog repair must own a distinct busy state through refresh and outcome presentation"
} else {
    Pass "catalog repair busy state remains active through refresh and outcome presentation"
}
foreach ($key in @("sync.catalogCompleteness", "sync.catalogLocalCounts", "sync.catalogSyncMode", "sync.catalogRepairRequired", "sync.catalogNotSaleSafe", "settings.catalogRepairAction", "settings.catalogRepairConfirm", "settings.catalogRepairCompleted", "settings.catalogRepairFailed")) {
    if ($localization -notmatch [regex]::Escape($key)) { Fail "catalog exactness localization missing key: $key" }
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
