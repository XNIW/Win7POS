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

function Has-Literal([string]$text, [string]$literal) {
    return $text.Contains($literal)
}

function Has-VisibleCopyOrLocKey([string]$text, [string]$visibleCopy, [string]$locKey) {
    return (Has-Literal $text $visibleCopy) -or (Has-Literal $text $locKey)
}

function Test-TranslationEntry(
    [string]$Text,
    [string]$Key,
    [string[]]$RequiredFragments = @()
) {
    $pattern = 'new\s+TranslationEntry\("' + [regex]::Escape($Key) + '"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        return $false
    }

    $values = @(
        $match.Groups[1].Value,
        $match.Groups[2].Value,
        $match.Groups[3].Value,
        $match.Groups[4].Value
    )

    foreach ($value in $values) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            return $false
        }
    }

    foreach ($fragment in $RequiredFragments) {
        $found = $false
        foreach ($value in $values) {
            if ($value.Contains($fragment)) {
                $found = $true
                break
            }
        }
        if (-not $found) {
            return $false
        }
    }

    return $true
}

$required = @(
    "src/Win7POS.Data/Online/PosAdminWebClient.cs",
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

$client = Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"
$wpfProject = Read-Text "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$userRepo = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$taskCombined = @(
    $client,
    $store,
    $options,
    $bootstrap,
    (Read-Text "src/Win7POS.Wpf/Pos/Online/PosDeviceIdentity.cs"),
    $dialogXaml,
    $dialog,
    $mainWindow
) -join "`n"
$baseUrlScope = @(
    $client,
    $options,
    $bootstrap,
    $dialog,
    $mainWindow
) -join "`n"
$forbiddenRuntimeUrlScope = @(
    $client,
    $bootstrap,
    $dialog,
    $dialogXaml,
    $mainWindow
) -join "`n"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String
$defaultUrlMatch = [regex]::Match($wpfProject, '<AdminWebDefaultBaseUrl[^>]*>([^<]+)</AdminWebDefaultBaseUrl>')

if ($client -notmatch "HttpClient") { Fail "HttpClient missing" } else { Pass "HttpClient present" }
if ($client -notmatch "SecurityProtocolType\.Tls12") { Fail "TLS 1.2 enforcement missing" } else { Pass "TLS 1.2 present" }
if ($client -notmatch "Timeout\s*=") { Fail "explicit timeout missing" } else { Pass "timeout present" }
if ($client -notmatch "/api/pos/auth/first-login") { Fail "first-login path missing" } else { Pass "first-login path present" }
if ($client -notmatch "/api/pos/session/heartbeat") { Fail "heartbeat path missing" } else { Pass "heartbeat path present" }
if ($store -notmatch "ProtectedData\.Protect" -or $store -notmatch "ProtectedData\.Unprotect") { Fail "DPAPI storage missing" } else { Pass "DPAPI storage present" }
if ($store -notmatch "WriteAllTextAtomic" -or $store -notmatch "File\.Replace" -or $store -notmatch "File\.Move") { Fail "trusted-device atomic write missing" } else { Pass "trusted-device atomic write present" }
if ($options -notmatch "WIN7POS_ADMIN_WEB_BASE_URL" -or $options -notmatch "pos-admin-web\.config") { Fail "base URL config sources missing" } else { Pass "base URL config present" }
if ($options -notmatch "PosAdminWebBaseUrlSource" -or $options -notmatch "EnvironmentVariable" -or $options -notmatch "ConfigFile" -or $options -notmatch "PackagedDefault") { Fail "base URL source model missing" } else { Pass "base URL source model present" }
if ($options -notmatch "TryLoadPackagedDefault" -or $options -notmatch "TryReadPackagedDefaultBaseUrl" -or $options -notmatch "AssemblyMetadataAttribute") { Fail "packaged default URL resolver missing" } else { Pass "packaged default URL resolver present" }
if ($wpfProject -notmatch "AdminWebEnvironment" -or $wpfProject -notmatch "AdminWebDefaultBaseUrl" -or $wpfProject -notmatch "AssemblyMetadataAttribute") { Fail "MSBuild Admin Web metadata missing" } else { Pass "MSBuild Admin Web metadata present" }
if (-not $defaultUrlMatch.Success) {
    Fail "packaged default base URL missing"
} else {
    try {
        $defaultUri = [Uri]$defaultUrlMatch.Groups[1].Value.Trim()
        if ($defaultUri.Scheme -ne "https" -or $defaultUri.UserInfo -or $defaultUri.AbsolutePath -ne "/" -or $defaultUri.Query -or $defaultUri.Fragment) {
            Fail "packaged default base URL must be HTTPS and base-only"
        } else {
            Pass "packaged default base URL is HTTPS and base-only"
        }
    } catch {
        Fail "packaged default base URL is not a valid absolute URI"
    }
}
if ($options -notmatch "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB" -or $options -notmatch "AllowInsecureLanAdminWeb") { Fail "insecure LAN override guard missing" } else { Pass "insecure LAN override guard present" }
if ($options -notmatch "parsed\.UserInfo" -or $options -notmatch "senza username o password") { Fail "base URL credentials guard missing" } else { Pass "base URL credentials rejected" }
if ($dialogXaml -match "Indirizzo pannello") { Fail "normal online link dialog still exposes URL field copy" } else { Pass "normal online link dialog hides URL field copy" }
if (-not ((Has-Literal $dialogXaml "AdvancedExpander") -and (Has-Literal $dialogXaml 'x:Name="BaseUrlBox"') -and (Has-VisibleCopyOrLocKey $dialogXaml "Impostazioni avanzate / Server" "access.login.advancedSettings") -and (Has-VisibleCopyOrLocKey $dialogXaml "URL Admin Web" "onlineFirstLogin.adminWebUrl") -and (Test-TranslationEntry $translations "access.login.advancedSettings" @("Advanced settings / Server", "Configuracion avanzada / Servidor", "Impostazioni avanzate / Server")) -and (Test-TranslationEntry $translations "onlineFirstLogin.adminWebUrl" @("Admin Web URL", "URL Admin Web")))) { Fail "advanced server URL settings missing" } else { Pass "advanced server URL settings present" }
if ($dialog -notmatch "PosDeviceIdentity\.GetStableDisplayName") { Fail "device display name is not generated automatically" } else { Pass "device display name generated automatically" }
if ($dialog -notmatch "PosOnlineBootstrapService") { Fail "first-login dialog does not use bootstrap service" } else { Pass "first-login dialog uses bootstrap service" }
if ($dialog -notmatch "finally[\s\S]*request\.Credential\s*=\s*string\.Empty[\s\S]*credential\s*=\s*string\.Empty[\s\S]*CredentialBox\.Clear\(\)") { Fail "first-login dialog does not clear PIN/password in finally" } else { Pass "first-login dialog clears PIN/password in finally" }
if ($bootstrap -notmatch "new\s+PosAdminWebClient" -or $bootstrap -notmatch "FirstLoginAsync") { Fail "bootstrap service does not call first-login through online client" } else { Pass "bootstrap service calls first-login through online client" }
if ($bootstrap -notmatch "SaveFirstLogin" -or $store -notmatch "ProtectedDeviceSecret" -or $store -notmatch "ProtectedSessionSecret") { Fail "bootstrap does not save trusted tokens through protected store" } else { Pass "trusted tokens saved through protected store" }
if ($store -match 'DataMember\(Name\s*=\s*"(trustedDeviceToken|deviceToken|sessionToken)"') { Fail "trusted store may persist raw token fields" } else { Pass "trusted store does not persist raw token field names" }
if ($userRepo -notmatch "PinHelper\.HashPin\(input\.Credential") { Fail "remote staff credential is not hashed for local mirror" } else { Pass "remote staff credential hashed for local mirror" }
if ($mainWindow -notmatch "new\s+PosOnlineFirstLoginDialog" -or $dialog -notmatch "PosOnlineBootstrapService") { Fail "unified POS access flow is not wired to the online client" } else { Pass "unified POS access flow uses online bootstrap client" }
if ($mainWindow -notmatch "RunCoordinatedOnlineRefreshAsync[\s\S]*HeartbeatAsync" -or
    $mainWindow -notmatch "IsSameTrustedSession" -or
    $mainWindow -notmatch "SaveHeartbeat") { Fail "coordinated startup heartbeat missing or unfenced" } else { Pass "coordinated startup heartbeat present and session-fenced" }

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
if ($forbiddenRuntimeUrlScope -match "https?://(?!(localhost|127\.0\.0\.1|::1|\.\.\.|schemas\.microsoft\.com))") { Fail "Admin Web URL hardcoded outside centralized config/sample/docs" } else { Pass "no Admin Web URL hardcoded in UI/bootstrap/client runtime" }
if ($baseUrlScope -match "https?://(?!(localhost|127\.0\.0\.1|::1|\.\.\.))") { Fail "production-like Admin Web URL hardcoded in resolver/runtime code" } else { Pass "no production Admin Web URL hardcoded in resolver/runtime code" }
if ($combined -match "sync_batch") { Fail "legacy sync batch marker detected" } else { Pass "TASK-081 sales sync scope allowed" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password)"
if ($taskCombined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
