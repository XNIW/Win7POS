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
    "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsViewModel.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterViewModel.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml"
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
$syncCenter = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterDialog.xaml"
$syncCenterCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterDialog.xaml.cs"
$syncCenterViewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterViewModel.cs"
$settingsHub = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml"
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
if ($workflow -notmatch 'InsertSaleAsync\([\s\S]{0,220}QueueSalesOutboxSyncNoThrow\(\)' -or
    $workflow -notmatch 'QueueSalesOutboxSyncNoThrow[\s\S]{0,260}PosOnlineSyncSignalBus\.Signal\([\s\S]{0,120}OnlineSyncLane\.SalesOutbox,[\s\S]{0,100}OnlineSyncLaneTrigger\.LocalCommit' -or
    $posViewModel -match "await _service\.TrySyncPendingSalesAsync\(\)\.ConfigureAwait\(true\)") { Fail "payment path must signal sales sync without awaiting remote sync" } else { Pass "payment path signals sales sync without awaiting remote sync" }
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
if ($workflow -notmatch 'RepairCatalogAsync[\s\S]{0,700}PosOnlineSyncSignalBus\.TriggerAsync\([\s\S]{0,140}OnlineSyncLane\.CatalogDelta,[\s\S]{0,120}OnlineSyncLaneTrigger\.AdministratorRepair') { Fail "workflow must expose supervised authoritative full catalog repair" } else { Pass "workflow exposes supervised authoritative full catalog repair" }
if ($syncCenter -notmatch 'x:Class="Win7POS\.Wpf\.Pos\.Dialogs\.SyncCenterDialog"' -or
    $syncCenter -notmatch 'WindowStartupLocation="CenterOwner"' -or
    $syncCenter -notmatch 'DialogFooterMargin' -or
    $syncCenter -notmatch 'sync\.center\.fullRepair') { Fail "Sync Center must use the shared dialog shell, centered owner and standard footer" } else { Pass "Sync Center follows shared dialog structure" }
if ($syncCenterCode -notmatch "ApplyConfirmDialog\.ShowConfirm" -or
    $mainCode -notmatch "AuthorizeFullCatalogRepairAsync" -or
    $mainCode -notmatch "PermissionCodes\.DbMaintenance" -or
    $mainCode -notmatch "requestedTrigger\s*==\s*CatalogSyncTrigger\.AdministratorRepair" -or
    $mainCode -notmatch '"CatalogFullRepair"') { Fail "Full Repair must be owner-safe, confirmed and protected by database maintenance permission" } else { Pass "Full Repair is confirmed and permission protected" }
if ($syncCenter -notmatch "DialogCancelButtonStyle" -or $syncCenter -notmatch "DialogActionButtonStyle" -or $syncCenter -notmatch "AutomationProperties\.Name") { Fail "Sync Center actions must use shared dialog resources and automation names" } else { Pass "Sync Center actions use shared resources and automation names" }
if ($mainCode -notmatch "allowFullDecision:\s*administratorRepairAuthorized" -or
    $mainCode -notmatch "!allowFullDecision\s*&&\s*previewDecision\.Mode\s*==\s*CatalogSyncMode\.Full" -or
    $mainCode -notmatch "catalog_sync_full_repair_required") { Fail "Sync Now must not be able to start a Full run" } else { Pass "Sync Now is constrained to incremental/resume" }
if ($syncCenterCode -notmatch "_fullRepairRunning" -or
    $syncCenterCode -notmatch "OnClosing\(CancelEventArgs e\)" -or
    $syncCenterCode -notmatch "e\.Cancel\s*=\s*true" -or
    $syncCenterCode -notmatch "_operationCts\?\.Cancel\(\)") { Fail "Sync Center close semantics must block Full Repair and cancel incremental operations" } else { Pass "Sync Center close semantics match operation type" }
if ($syncCenterViewModel -notmatch "BuildSafeDiagnostics" -or
    $syncCenterViewModel -match "DeviceToken|SessionToken|ShopCode|StaffDisplayName" -or
    $syncCenterViewModel -notmatch "cursor_fingerprint") { Fail "Sync Center diagnostics must expose only redacted safe codes and counts" } else { Pass "Sync Center diagnostics are redacted" }
if ($reader -notmatch "ObservedRevisionKey" -or
    $reader -notmatch "CommittedRevisionKey" -or
    $reader -notmatch "CatalogRevisionMatchCode" -or
    $syncCenterViewModel -notmatch "CatalogObservedRevisionText" -or
    $syncCenterViewModel -notmatch "CatalogCommittedRevisionText" -or
    $syncCenter -notmatch "CatalogObservedRevisionText" -or
    $syncCenter -notmatch "CatalogCommittedRevisionText") {
    Fail "Sync Center must surface redacted observed/committed catalog revision state"
} else {
    Pass "Sync Center surfaces redacted observed/committed catalog revision state"
}
if ($reader -notmatch "CatalogHasError" -or
    $syncCenterViewModel -notmatch "CatalogHasError" -or
    $syncCenterViewModel -notmatch "CatalogErrorText" -or
    $syncCenterViewModel -notmatch 'catalog\.error=' -or
    $syncCenterViewModel -notmatch 'catalog\.observed_revision_fingerprint=' -or
    $syncCenterViewModel -notmatch 'catalog\.committed_revision_fingerprint=' -or
    $syncCenterViewModel -notmatch 'catalog\.revision_status=' -or
    $syncCenter -notmatch "CatalogErrorText") {
    Fail "catalog errors and revision fingerprints must be visible and safely copyable"
} else {
    Pass "catalog errors and revision fingerprints are visible and safely copyable"
}
if ($settingsHub -notmatch "OnSyncCenterClick" -or $mainXaml -notmatch "OnSyncStatusPillClick" -or $mainCode -notmatch "RestoreScannerFocus") { Fail "Sync Center must be reachable from shell/settings and restore scanner focus" } else { Pass "Sync Center entry points and scanner focus restoration present" }
if ($shopDialog -match "RepairCatalogCommand" -or $shopViewModel -match "RepairCatalogAsync|TryRepairCatalogAsync") { Fail "Shop settings must remain diagnostic-only and not duplicate Full Repair logic" } else { Pass "Shop settings is diagnostic-only" }
if ($shopDialog -notmatch 'IsEnabled="\{Binding CanClose\}"' -or $shopViewModel -notmatch "CanClose\s*=>\s*!IsBusy" -or $shopViewModel -notmatch "OnPropertyChanged\(nameof\(CanClose\)\)") { Fail "shop settings close action must follow its active load state" } else { Pass "shop settings close action follows active load state" }
if ($shopDialogCode -notmatch "OnClosing\(CancelEventArgs e\)" -or $shopDialogCode -notmatch "CanClose\s*==\s*false" -or $shopDialogCode -notmatch "e\.Cancel\s*=\s*true") { Fail "shop settings must block Escape/Alt+F4 while loading" } else { Pass "shop settings guards Escape/Alt+F4 while loading" }
if ($shopViewModel -notmatch "INotifyPropertyChanged,\s*IDisposable" -or
    $shopViewModel -notmatch "LanguageChanged\s*-=\s*OnLanguageChanged" -or
    $shopDialogCode -notmatch "OnClosed\(EventArgs e\)" -or
    $shopDialogCode -notmatch "viewModel\.Dispose\(\)" -or
    $shopDialogCode -notmatch "DataContext\s*=\s*null") {
    Fail "shop settings must detach localization and release its data context when closed"
} else {
    Pass "shop settings releases localization and data context on close"
}
foreach ($key in @("sync.catalogCompleteness", "sync.catalogLocalCounts", "sync.catalogSyncMode", "sync.catalogRepairRequired", "sync.catalogNotSaleSafe", "sync.center.observedRevision", "sync.center.committedRevision", "sync.center.revisionStatus", "sync.center.revision.match", "sync.center.revision.mismatch", "sync.center.revision.unknown", "sync.center.attention", "sync.center.title", "sync.center.syncNow", "sync.center.retryCheckpoint", "sync.center.fullRepair", "sync.center.copyDiagnostics", "sync.center.repairConfirm")) {
    if ($localization -notmatch [regex]::Escape($key)) { Fail "catalog exactness localization missing key: $key" }
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
