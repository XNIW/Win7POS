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
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Wpf/Pos/PosViewModel.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml"
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
$sales = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$posViewModel = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$shopDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"

foreach ($label in @("Online", "Offline", "Non collegato", "Sessione da ricollegare", "Ultimo catalogo", "Ultima vendita inviata", "Vendite in coda", "Da ritentare", "Bloccate", "Ultimo errore", "Negozio", "Dispositivo", "Staff online", "Sessione verificata")) {
    if (($reader + $mainXaml + $shopDialog) -notmatch [regex]::Escape($label)) {
        Fail "sync status UX missing label: $label"
    }
}

if ($mainXaml -notmatch "SyncStatusPill" -or $mainXaml -notmatch "SyncStatusText") { Fail "main header sync status strip missing" } else { Pass "main header sync status strip present" }
if ($mainCode -notmatch "DispatcherTimer" -or $mainCode -notmatch "TimeSpan\.FromSeconds\(30\)") { Fail "sync status refresh timer missing" } else { Pass "sync status refresh timer present" }
if ($mainCode -notmatch "PosSyncStatusReader") { Fail "main shell does not use sync status reader" } else { Pass "main shell uses sync status reader" }
if ($reader -match "DeviceToken|SessionToken|ProtectedDeviceSecret|ProtectedSessionSecret") { Fail "sync status reader must not expose POS secrets" } else { Pass "sync status reader avoids POS secrets" }
if ($reader -notmatch "pos\.catalog\.last_sync_at" -or $reader -notmatch "pos\.sales_sync\.last_success_at") { Fail "sync status reader must show catalog and sales sync timestamps" } else { Pass "sync timestamps present" }
if ($reader -notmatch "Bloccate" -or $sales -notmatch "failed_blocked" -or $sales -notmatch "GetSalesSyncOutboxSummaryAsync") { Fail "outbox summary must expose pending/retry/blocked" } else { Pass "outbox summary exposes pending/retry/blocked" }
if ($reader -notmatch "Sync in corso" -or $reader -notmatch "IsSyncing" -or $salesSync -notmatch "pos\.sales_sync\.in_progress") { Fail "syncing status must be visible while background sync runs" } else { Pass "syncing status visible" }
if ($salesSync -notmatch "Interlocked\.CompareExchange" -or $salesSync -notmatch "Sales sync skipped: already running") { Fail "sales sync service must guard concurrent runs" } else { Pass "sales sync concurrency guard present" }
if ($salesSync -notmatch "pos\.sales_sync\.last_success_at" -or $salesSync -notmatch "pos\.sales_sync\.last_error") { Fail "sales sync diagnostics settings missing" } else { Pass "sales sync diagnostics settings present" }
if ($posViewModel -notmatch "QueueSalesSyncAfterPayment" -or $posViewModel -match "await _service\.TrySyncPendingSalesAsync\(\)\.ConfigureAwait\(true\)") { Fail "payment path must queue sales sync without awaiting remote sync" } else { Pass "payment path queues sales sync without awaiting remote sync" }
if ($sales -notmatch "HasUnresolvedSalesSyncOutboxAsync" -or $workflow -notmatch "HasUnresolvedSalesSyncOutboxAsync" -or $workflow -notmatch "restore blocked") { Fail "restore must block when unresolved outbox rows exist" } else { Pass "restore blocks unresolved outbox rows" }
if ($shopDialog -notmatch "SyncStatusText" -or $shopDialog -notmatch "PendingSalesText") { Fail "shop dialog must surface sync status details" } else { Pass "shop dialog sync details present" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
