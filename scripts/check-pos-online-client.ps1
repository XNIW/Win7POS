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
    "src/Win7POS.Core/Online/PosAdminWebClient.cs",
    "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs",
    "src/Win7POS.Core/Online/PosAdminWebOptions.cs",
    "src/Win7POS.Wpf/Pos/Online/PosDeviceIdentity.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Data/Repositories/UserRepository.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$client = Read-Text "src/Win7POS.Core/Online/PosAdminWebClient.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$operatorDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$userRepo = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$taskCombined = @(
    $client,
    $store,
    $options,
    $bootstrap,
    (Read-Text "src/Win7POS.Wpf/Pos/Online/PosDeviceIdentity.cs"),
    (Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"),
    $dialog,
    (Read-Text "src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml"),
    $operatorDialog,
    $mainWindow
) -join "`n"
$baseUrlScope = @(
    $client,
    $options,
    $bootstrap,
    $dialog,
    $operatorDialog,
    $mainWindow
) -join "`n"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($client -notmatch "HttpClient") { Fail "HttpClient missing" } else { Pass "HttpClient present" }
if ($client -notmatch "SecurityProtocolType\.Tls12") { Fail "TLS 1.2 enforcement missing" } else { Pass "TLS 1.2 present" }
if ($client -notmatch "Timeout\s*=") { Fail "explicit timeout missing" } else { Pass "timeout present" }
if ($client -notmatch "/api/pos/auth/first-login") { Fail "first-login path missing" } else { Pass "first-login path present" }
if ($client -notmatch "/api/pos/session/heartbeat") { Fail "heartbeat path missing" } else { Pass "heartbeat path present" }
if ($store -notmatch "ProtectedData\.Protect" -or $store -notmatch "ProtectedData\.Unprotect") { Fail "DPAPI storage missing" } else { Pass "DPAPI storage present" }
if ($options -notmatch "WIN7POS_ADMIN_WEB_BASE_URL" -or $options -notmatch "pos-admin-web\.config") { Fail "base URL config sources missing" } else { Pass "base URL config present" }
if ($dialog -notmatch "PosOnlineBootstrapService") { Fail "first-login dialog does not use bootstrap service" } else { Pass "first-login dialog uses bootstrap service" }
if ($dialog -notmatch "finally[\s\S]*request\.Credential\s*=\s*string\.Empty[\s\S]*credential\s*=\s*string\.Empty[\s\S]*CredentialBox\.Clear\(\)") { Fail "first-login dialog does not clear PIN/password in finally" } else { Pass "first-login dialog clears PIN/password in finally" }
if ($bootstrap -notmatch "new\s+PosAdminWebClient" -or $bootstrap -notmatch "FirstLoginAsync") { Fail "bootstrap service does not call first-login through online client" } else { Pass "bootstrap service calls first-login through online client" }
if ($bootstrap -notmatch "SaveFirstLogin" -or $store -notmatch "ProtectedDeviceSecret" -or $store -notmatch "ProtectedSessionSecret") { Fail "bootstrap does not save trusted tokens through protected store" } else { Pass "trusted tokens saved through protected store" }
if ($store -match 'DataMember\(Name\s*=\s*"(trustedDeviceToken|deviceToken|sessionToken)"') { Fail "trusted store may persist raw token fields" } else { Pass "trusted store does not persist raw token field names" }
if ($userRepo -notmatch "PinHelper\.HashPin\(input\.Credential") { Fail "remote staff credential is not hashed for local mirror" } else { Pass "remote staff credential hashed for local mirror" }
if ($operatorDialog -notmatch "PosOnlineFirstLoginDialog") { Fail "operator login does not expose online link" } else { Pass "operator login exposes online link" }
if ($mainWindow -notmatch "TryRefreshTrustedPosSessionAsync") { Fail "startup heartbeat missing" } else { Pass "startup heartbeat present" }

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
if ($baseUrlScope -match "https?://(?!(localhost|127\.0\.0\.1|::1))") { Fail "production-like Admin Web URL hardcoded" } else { Pass "no production Admin Web URL hardcoded" }
if ($combined -match "sync_batch") { Fail "legacy sync batch marker detected" } else { Pass "TASK-081 sales sync scope allowed" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password)"
if ($taskCombined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
