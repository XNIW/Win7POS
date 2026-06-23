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
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Core/Online/PosAdminWebOptions.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs",
    "src/Win7POS.Data/DbInitializer.cs",
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

$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$firstRun = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml"
$firstRunCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml.cs"
$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$userRepo = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($mainWindow -notmatch "TryOnlineBootstrapFirstRunAsync") { Fail "fresh install does not try online bootstrap before recovery wizard" } else { Pass "fresh install online bootstrap present" }
if ($mainWindow -notmatch "FirstRunSetupDialog") { Fail "recovery/dev first-run wizard removed" } else { Pass "recovery/dev first-run wizard retained" }
if ($firstRun -notmatch "Recovery/dev") { Fail "FirstRunSetupDialog is not labelled as recovery/dev" } else { Pass "FirstRunSetupDialog labelled recovery/dev" }
if ($dialogXaml -notmatch "Indirizzo pannello") { Fail "online bootstrap should label the Admin Web URL as an operator-facing panel address" } else { Pass "panel address field present" }
if ($dialogXaml -match "Shop code|Staff code|Nome device") { Fail "online bootstrap still exposes technical/English labels" } else { Pass "online bootstrap labels are operator-facing" }
if ($dialog -notmatch "PosOnlineBootstrapService") { Fail "online dialog does not use bootstrap service" } else { Pass "online dialog uses bootstrap service" }
if ($dialog -notmatch "finally[\s\S]*CredentialBox\.Clear\(\)") { Fail "online dialog must clear PIN/password in a finally block" } else { Pass "online dialog clears PIN/password in finally" }
if ($options -notmatch "SaveBaseUrl") { Fail "Admin Web base URL cannot be saved from bootstrap" } else { Pass "Admin Web base URL save present" }
if ($bootstrap -notmatch "FirstLoginAsync") { Fail "bootstrap does not call first-login" } else { Pass "bootstrap calls first-login" }
if ($bootstrap -match "shop code|staff code") { Fail "bootstrap service error copy must be operator-facing Italian" } else { Pass "bootstrap service error copy is operator-facing" }
if ($bootstrap -notmatch "SaveFirstLogin") { Fail "bootstrap does not save trusted device with DPAPI store" } else { Pass "trusted device save present" }
if ($bootstrap -notmatch "UpsertRemoteStaffMirrorAsync") { Fail "bootstrap does not create/sync local staff mirror" } else { Pass "local staff mirror present" }
$validationIndex = $bootstrap.IndexOf("ValidateFirstLoginResponse(")
$mirrorIndex = $bootstrap.IndexOf("UpsertRemoteStaffMirrorAsync")
if ($validationIndex -lt 0 -or $mirrorIndex -lt 0 -or $validationIndex -gt $mirrorIndex) {
    Fail "bootstrap must validate first-login trusted/session payload before local staff mirror"
} else {
    Pass "bootstrap validates trusted/session payload before local staff mirror"
}
$validationPattern = "ValidateFirstLoginResponse[\s\S]*TrustedDeviceToken[\s\S]*Session[\s\S]*SessionToken[\s\S]*PosSessionId[\s\S]*ShopDeviceId[\s\S]*StaffId[\s\S]*StaffCode[\s\S]*ShopCode"
if ($bootstrap -notmatch $validationPattern) { Fail "bootstrap first-login validation must cover tokens, session, device, staff and shop fields" } else { Pass "bootstrap first-login validation covers required fields" }
if ($bootstrap -notmatch "PosCatalogPullService") { Fail "bootstrap does not attempt initial catalog pull" } else { Pass "initial catalog pull present" }
if ($userRepo -notmatch "UpsertRemoteStaffMirrorAsync") { Fail "UserRepository remote staff upsert missing" } else { Pass "UserRepository remote staff upsert present" }
if ($initializer -notmatch "remote_staff_id") { Fail "remote staff id column missing" } else { Pass "remote staff id column present" }
if ($initializer -notmatch "remote_credential_version") { Fail "remote credential version column missing" } else { Pass "remote credential version column present" }
if ($store -notmatch "ProtectedData\.Protect") { Fail "trusted device store does not use DPAPI" } else { Pass "DPAPI trusted device store present" }
if ($mainWindow -match "ModernMessageDialog\.Show[\s\S]{0,180}ex\.Message") { Fail "startup/user error dialogs must not expose raw exception messages" } else { Pass "startup/user error dialogs are user-facing" }
if ($firstRunCode -match "ShowError\(msg\)") { Fail "recovery/dev setup must not show raw exception messages" } else { Pass "recovery/dev setup errors are user-facing" }
if ($firstRunCode -notmatch "finally[\s\S]*PinBox\.Clear\(\)[\s\S]*ConfirmPinBox\.Clear\(\)") { Fail "recovery/dev setup must clear PIN fields in a finally block" } else { Pass "recovery/dev setup clears PIN fields in finally" }
if ((Read-Text "src/Win7POS.Core/Online/PosAdminWebClient.cs") -notmatch "MaxResponseBodyBytes|ReadResponseBodyAsync") { Fail "Admin Web client must bound response body reads" } else { Pass "Admin Web client bounds response body reads" }

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
if ($combined -match "NEXT_PUBLIC_SUPABASE|supabase\.co") { Fail "Supabase direct client/config reference found" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password)"
if ($combined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
