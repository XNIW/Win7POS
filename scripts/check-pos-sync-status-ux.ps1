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
$shopDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/ShopSettingsDialog.xaml"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"

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
if ($salesSync -notmatch "pos\.sales_sync\.last_success_at" -or $salesSync -notmatch "pos\.sales_sync\.last_error") { Fail "sales sync diagnostics settings missing" } else { Pass "sales sync diagnostics settings present" }
if ($shopDialog -notmatch "SyncStatusText" -or $shopDialog -notmatch "PendingSalesText") { Fail "shop dialog must surface sync status details" } else { Pass "shop dialog sync details present" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
