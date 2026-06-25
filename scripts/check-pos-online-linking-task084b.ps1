param(
    [string]$ReleasePackSource = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false
$stagingUrl = "https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev"

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

function Resolve-Source([string]$source) {
    if ([string]::IsNullOrWhiteSpace($source)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($source)) {
        return $source
    }

    return (Join-Path $repoRoot $source)
}

function Read-PackText([string]$root, [string]$fileName) {
    $found = Get-ChildItem -Path $root -Recurse -File -Filter $fileName -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $found) {
        return $null
    }

    return [System.IO.File]::ReadAllText($found.FullName)
}

function Test-ReleasePackSource([string]$sourceValue) {
    if ([string]::IsNullOrWhiteSpace($sourceValue)) {
        return
    }

    $source = Resolve-Source $sourceValue
    if (-not (Test-Path $source)) {
        Fail "ReleasePack source missing: $sourceValue"
        return
    }

    $tempDir = $null
    $root = $source
    if (-not (Test-Path $source -PathType Container)) {
        if ([System.IO.Path]::GetExtension($source) -notmatch "\.zip") {
            Fail "ReleasePack source must be a folder or zip: $sourceValue"
            return
        }

        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-task084b-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Expand-Archive -Path $source -DestinationPath $tempDir -Force
        $root = $tempDir
    }

    try {
        $helper = Read-PackText $root "set-admin-web-staging-url.bat"
        $readmeRun = Read-PackText $root "README_RUN.txt"
        $checklist = Read-PackText $root "RELEASE_CHECKLIST.txt"

        if ($null -eq $helper) {
            Fail "ReleasePack missing set-admin-web-staging-url.bat"
        } elseif ($helper -notmatch [regex]::Escape($stagingUrl) -or
            $helper -notmatch "%ProgramData%\\Win7POS" -or
            $helper -notmatch "AdminWebBaseUrl=" -or
            $helper -notmatch "pos-admin-web\.config") {
            Fail "staging helper does not write the expected Admin Web config"
        } elseif ($helper -match "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB\s*=") {
            Fail "staging helper must not set insecure LAN override"
        } else {
            Pass "ReleasePack staging helper writes expected Admin Web config"
        }

        if ($null -eq $readmeRun) {
            Fail "ReleasePack missing README_RUN.txt"
        } elseif ($readmeRun -notmatch "shop code, staff code and PIN/password" -or
            $readmeRun -notmatch "set-admin-web-staging-url\.bat" -or
            $readmeRun -notmatch [regex]::Escape($stagingUrl)) {
            Fail "README_RUN.txt must document simplified online linking and staging helper"
        } else {
            Pass "README_RUN.txt documents simplified online linking and staging helper"
        }

        if ($null -eq $checklist) {
            Fail "ReleasePack missing RELEASE_CHECKLIST.txt"
        } elseif ($checklist -notmatch "set-admin-web-staging-url\.bat" -or
            $checklist -notmatch "PIN/password") {
            Fail "RELEASE_CHECKLIST.txt must include staging helper and simplified linking checks"
        } else {
            Pass "RELEASE_CHECKLIST.txt includes staging helper and simplified linking checks"
        }
    }
    finally {
        if ($tempDir -and (Test-Path $tempDir)) {
            Remove-Item -Path $tempDir -Recurse -Force
        }
    }
}

$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"
$dialogCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$identity = Read-Text "src/Win7POS.Wpf/Pos/Online/PosDeviceIdentity.cs"
$workflow = Read-Text ".github/workflows/release-pack.yml"
$readme = Read-Text "README.md"
$helper = Read-Text "scripts/set-admin-web-staging-url.bat"

if ($dialogXaml -match "Indirizzo pannello") {
    Fail "normal online linking dialog must not show old panel URL label"
} else {
    Pass "old panel URL label removed"
}

if ($dialogXaml -match "x:Name=`"DeviceNameBox`"") {
    Fail "normal online linking dialog must not require editable device name"
} else {
    Pass "editable device name removed"
}

if ($dialogXaml -notmatch "Codice negozio" -or $dialogXaml -notmatch "Codice staff" -or $dialogXaml -notmatch "PIN/password") {
    Fail "normal online linking dialog must show shop code, staff code and PIN/password"
} else {
    Pass "normal operator credentials present"
}

if ($dialogXaml -notmatch "Impostazioni avanzate / Server" -or $dialogXaml -notmatch "URL Admin Web") {
    Fail "advanced server settings must expose Admin Web base URL for support"
} else {
    Pass "advanced server settings present"
}

if ($dialogXaml -notmatch "Server Admin Web configurato" -or $dialogCode -notmatch "URL Admin Web non configurato") {
    Fail "configured/missing server states are not explicit"
} else {
    Pass "configured/missing server states explicit"
}

if ($dialogCode -notmatch "PosDeviceIdentity\.GetStableDisplayName\(\)" -or $identity -notmatch "GetStableDisplayName") {
    Fail "device display name must be generated automatically"
} else {
    Pass "automatic device display name present"
}

$sensitiveIdentityPattern = "(?i)UserName|NetworkInterface|MacAddress|MAC\s*address|Serial|MachineGuid|ProcessorId|VolumeSerial|GetFolderPath"
if ($identity -match $sensitiveIdentityPattern) {
    Fail "device display name helper must not use username, MAC, serial, paths or hardware identifiers"
} else {
    Pass "device display name avoids sensitive identifiers"
}

if ($options -notmatch "WIN7POS_ADMIN_WEB_BASE_URL" -or $options -notmatch "pos-admin-web\.config") {
    Fail "Admin Web base URL config sources missing"
} else {
    Pass "Admin Web base URL config sources present"
}

if ($options -notmatch "Inserisci solo l'URL base HTTPS del pannello" -or $options -notmatch "/auth/login o /shop") {
    Fail "page URL rejection message missing"
} else {
    Pass "page URL rejection message present"
}

if ($options -notmatch "parsed\.Scheme == Uri\.UriSchemeHttp && !parsed\.IsLoopback && !AllowInsecureLanAdminWeb\(\)") {
    Fail "non-loopback HTTP must be rejected unless explicit override is set"
} else {
    Pass "non-loopback HTTP guard present"
}

if ($dialogCode -notmatch "workers\.dev/staging usa HTTPS") {
    Fail "visible insecure LAN warning for support/dev mode missing"
} else {
    Pass "insecure LAN warning present"
}

$releaseConfigScope = $workflow
if ($releaseConfigScope -match "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB\s*[:=]\s*1") {
    Fail "release docs/workflow must not enable insecure LAN Admin Web override"
} else {
    Pass "release docs/workflow do not enable insecure LAN override"
}

if ($workflow -notmatch "set-admin-web-staging-url\.bat" -or
    $helper -notmatch [regex]::Escape($stagingUrl) -or
    $helper -notmatch "%ProgramData%\\Win7POS" -or
    $helper -notmatch "AdminWebBaseUrl=" -or
    $helper -match "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB\s*=") {
    Fail "ReleasePack staging helper is missing or unsafe"
} else {
    Pass "ReleasePack staging helper present and safe"
}

if ($options -notmatch "URL base HTTPS" -or $readme -notmatch "WIN7POS_ADMIN_WEB_BASE_URL" -or $readme -notmatch "URL base HTTPS") {
    Fail "operator/runbook documentation for base URL is incomplete"
} else {
    Pass "operator/runbook documentation present"
}

Test-ReleasePackSource $ReleasePackSource

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
