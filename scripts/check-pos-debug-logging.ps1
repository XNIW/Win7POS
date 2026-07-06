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

function Require-Marker([string]$label, [string]$source, [string]$pattern) {
    if ($source -notmatch $pattern) {
        Fail "$label missing"
    } else {
        Pass "$label present"
    }
}

$required = @(
    "src/Win7POS.Data/Online/PosAdminWebClient.cs",
    "src/Win7POS.Wpf/Infrastructure/FileLogger.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$client = Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs"
$clientContracts = Read-Text "src/Win7POS.Core/Online/PosOnlineTransportContracts.cs"
$client = $client + "`n" + $clientContracts
$logger = Read-Text "src/Win7POS.Wpf/Infrastructure/FileLogger.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$catalog = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$combined = @($client, $logger, $mainWindow, $catalog, $bootstrap, $salesSync) -join "`n"

Require-Marker "client request id header" $client "X-Client-Request-Id"
Require-Marker "server request id header" $client "X-Request-Id"
Require-Marker "Cloudflare ray capture" $client "CF-Ray"
Require-Marker "client request id result" $client "ClientRequestId"
Require-Marker "server request id result" $client "ServerRequestId"
Require-Marker "POS error response request id" $client "DataMember\(Name\s*=\s*""requestId"""

Require-Marker "JSON token redaction" $logger "sessionToken\|deviceToken\|trustedDeviceToken"
Require-Marker "DB password alias redaction" $logger "db_password\|database password"
Require-Marker "authorization bearer redaction" $logger "Authorization\\s\*:\\s\*Bearer"
Require-Marker "POS token prefix redaction" $logger "mcpos_\(device\|session\)"
Require-Marker "log rotation" $logger "RotateIfNeeded"

Require-Marker "bootstrap category" $bootstrap "category=online\.bootstrap"
Require-Marker "heartbeat category" $mainWindow "category=online\.heartbeat"
Require-Marker "catalog category" $catalog "category=catalog\.pull"
Require-Marker "sales category" $salesSync "category=sales\.sync"
Require-Marker "sales sync attempt id" $salesSync "syncAttemptId"
Require-Marker "sales ACK status guard" $salesSync "IsAcceptedAckStatus"
Require-Marker "blocked ACK status guard" $salesSync "IsBlockedAckStatus"
Require-Marker "sales server request id log" $salesSync "serverRequestId"

$sensitiveLoggingPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n;]*(sessionToken|deviceToken|trustedDeviceToken|CredentialBox|PinBox|password|credential|pin)"
if ($combined -match $sensitiveLoggingPattern) {
    Fail "sensitive value may be logged directly"
} else {
    Pass "no direct sensitive-value logging markers found"
}

$rawTokenPattern = "mcpos_(device|session)_[A-Za-z0-9_-]{8,}"
if ($combined -match $rawTokenPattern) {
    Fail "literal POS token-like value found"
} else {
    Pass "no literal POS token-like value found"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
