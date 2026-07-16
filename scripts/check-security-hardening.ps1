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

function Info([string]$message) {
    Write-Host "INFO: $message" -ForegroundColor Cyan
}

function Read-Text([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Read-Many([string[]]$roots) {
    $parts = New-Object System.Collections.Generic.List[string]
    $extensions = @(".cs", ".xaml", ".config", ".ps1", ".bat", ".cmd", ".iss", ".csproj", ".props", ".targets", ".md")
    foreach ($root in $roots) {
        $path = Join-Path $repoRoot $root
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $parts.Add([System.IO.File]::ReadAllText($path))
            continue
        }

        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        Get-ChildItem -LiteralPath $path -Recurse -File |
            Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() -and $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
            ForEach-Object { $parts.Add([System.IO.File]::ReadAllText($_.FullName)) }
    }

    return ($parts -join "`n")
}

function Require([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Pass $label } else { Fail $label }
}

function Forbid([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Fail $label } else { Pass $label }
}

function Run-Check([string]$relativePath) {
    $scriptPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        Fail "$relativePath missing"
        return
    }

    $ps = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $ps) {
        $ps = (Get-Command powershell -ErrorAction Stop).Source
    }

    & $ps -NoProfile -ExecutionPolicy Bypass -File $scriptPath
    if ($LASTEXITCODE -ne 0) { Fail "$relativePath failed" } else { Pass "$relativePath passed" }
}

$runtime = Read-Many @("src/Win7POS.Wpf", "src/Win7POS.Core", "src/Win7POS.Data")
$release = Read-Many @("installer", "samples", "scripts/set-admin-web-staging-url.bat", "src/Win7POS.Wpf/Win7POS.Wpf.csproj")
$logger = Read-Text "src/Win7POS.Wpf/Infrastructure/FileLogger.cs"
$startup = Read-Text "src/Win7POS.Wpf/Infrastructure/StartupTrace.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$salesBuilder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$catalogBuilder = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxPayloadBuilder.cs"
$recoveryPolicy = Read-Text "src/Win7POS.Core/Security/PosAccessRecoveryPolicy.cs"
$userRepository = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$accessRecovery = Read-Many @(
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml.cs",
    "src/Win7POS.Data/Repositories/UserRepository.cs"
)

foreach ($field in @("sessionToken", "deviceToken", "trustedDeviceToken", "pin", "password", "credential")) {
    Require "FileLogger redacts $field" $logger ([regex]::Escape($field))
    Require "StartupTrace redacts $field" $startup ([regex]::Escape($field))
}
Require "FileLogger applies regex redaction" $logger "Regex\.Replace[\s\S]*\[redacted\]"
Require "StartupTrace applies regex redaction" $startup "Regex\.Replace[\s\S]*\[redacted\]"
Forbid "direct sensitive runtime logging absent" $runtime "(?i)(Log(?:Info|Warning|Error)|StartupTrace\.Write|Console\.WriteLine)\s*\([^\r\n;]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|password|credential|payloadJson|payload_json|Authorization\s*:|Bearer)"
Forbid "literal POS token absent" $runtime "mcpos_(device|session)_[A-Za-z0-9_-]{8,}"

Require "trusted-device DPAPI CurrentUser present" $store "ProtectedData\.Protect[\s\S]*DataProtectionScope\.CurrentUser"
Require "trusted-device protected secret fields present" $store "ProtectedDeviceSecret[\s\S]*ProtectedSessionSecret"
Forbid "trusted store raw token data members absent" $store "DataMember\(Name\s*=\s*`"(deviceToken|sessionToken|trustedDeviceToken)`""

Require "sales payload redaction present" $salesBuilder "SerializeRedacted[\s\S]*DeviceToken\s*=\s*null[\s\S]*SessionToken\s*=\s*null"
Require "catalog source path redaction present" $catalogBuilder "Path\.GetFileName"
Forbid "catalog original file bytes not stored" $catalogBuilder "File\.(ReadAllBytes|ReadAllText|OpenRead)"

Require "Admin Web rejects URL credentials" $options "parsed\.UserInfo"
Require "Admin Web rejects path/query/fragment" $options "parsed\.Query[\s\S]*parsed\.Fragment[\s\S]*path != `"/`""
Require "Admin Web HTTP LAN guard present" $options "parsed\.Scheme == Uri\.UriSchemeHttp && !parsed\.IsLoopback && !AllowInsecureLanAdminWeb\(\)"
Forbid "release does not enable insecure LAN override" $release "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB\s*[:=]\s*1"
Forbid "direct Supabase/service-role absent" (Read-Many @("src", "samples", "installer")) "(?i)(SUPABASE_SERVICE_ROLE_KEY|NEXT_PUBLIC_SUPABASE|createClient\s*\(|supabase\.co|supabaseUrl|supabaseKey|\bservice_role\b)"

Require "online denial is classified before recovery" $recoveryPolicy "IsDenied\(failureKind\)[\s\S]*PosAccessNextStep\.Denied"
Require "first-run admin rechecks zero users in immediate transaction" $userRepository "BeginTransaction\(deferred:\s*false\)[\s\S]*SELECT COUNT\(\*\)[\s\S]*FirstRunAdminCreated"
Require "first-run credential is cleared in finally" $accessRecovery "finally[\s\S]*CredentialBox\.Clear\(\)"
Forbid "access/recovery logs contain no raw operator identifiers or credentials" $accessRecovery "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n;]{0,260}\+\s*(username|shopCode|staffCode|credential|pin)\b"

if ($runtime -match "(?i)FileSystemAccessRule|SetAccessControl|DirectorySecurity|FileSecurity") {
    Info "custom file ACL code present; review manually"
} else {
    Info "no custom file ACL code; trusted-device confidentiality relies on DPAPI CurrentUser"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
