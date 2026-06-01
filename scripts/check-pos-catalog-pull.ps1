$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$srcRoot = Join-Path $repoRoot "src"
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
    "src/Win7POS.Wpf/Pos/Online/PosAdminWebClient.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$client = Read-Text "src/Win7POS.Wpf/Pos/Online/PosAdminWebClient.cs"
$service = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($client -notmatch "/api/pos/catalog/pull") { Fail "catalog pull path missing" } else { Pass "catalog pull path present" }
if ($client -notmatch "CatalogPullAsync") { Fail "CatalogPullAsync missing" } else { Pass "CatalogPullAsync present" }
if ($service -notmatch "PosTrustedDeviceStore") { Fail "trusted device store not used for catalog pull" } else { Pass "trusted device store used" }
if ($service -notmatch "ProductRepository") { Fail "local product repository not used" } else { Pass "local product repository used" }
if ($service -notmatch "UpsertProductAndMetaInTransactionAsync") { Fail "catalog rows are not persisted locally" } else { Pass "catalog rows persisted locally" }
if ($service -notmatch "pos.catalog.last_sync_at") { Fail "last catalog sync setting missing" } else { Pass "last catalog sync setting present" }
if ($mainWindow -notmatch "TryPullCatalogAsync") { Fail "startup catalog pull foundation missing" } else { Pass "startup catalog pull foundation present" }

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
if ($combined -match "pos_sales|sales_sync|sync_batch|payment_sync|cash_close") { Fail "sales/payment sync scope detected" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password)"
if ($combined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
