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

$samplePath = "samples/pos-admin-web.config.example"
$readme = Read-Text "README.md"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$wpfProject = Read-Text "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
$helper = Read-Text "scripts/set-admin-web-staging-url.bat"
$defaultUrlMatch = [regex]::Match($wpfProject, '<AdminWebDefaultBaseUrl[^>]*>([^<]+)</AdminWebDefaultBaseUrl>')

if (-not (Test-Path (Join-Path $repoRoot $samplePath))) {
    Fail "$samplePath missing"
} else {
    $sample = Read-Text $samplePath

    if ($sample -notmatch "^AdminWebBaseUrl=https://merchandise-control-admin-web-staging\.merchandise-control-admin-web\.workers\.dev\s*$") {
        Fail "sample config must contain only the verified public workers.dev HTTPS base URL"
    } else {
        Pass "sample config uses verified workers.dev HTTPS base URL"
    }

    if ($sample -match "localhost|127\.0\.0\.1|192\.168\.|http://|@|SUPABASE_SERVICE_ROLE_KEY|mcpos_|token|password|pin") {
        Fail "sample config contains local URL, credentials or secret-like text"
    } else {
        Pass "sample config has no local URL, credentials or secret-like text"
    }
}

if ($readme -notmatch [regex]::Escape($samplePath)) {
    Fail "README must reference the safe sample config"
} else {
    Pass "README references safe sample config"
}

if (-not $defaultUrlMatch.Success) {
    Fail "packaged staging default URL missing from WPF MSBuild metadata"
} else {
    $defaultUrl = $defaultUrlMatch.Groups[1].Value.Trim()
    if ($defaultUrl -ne "https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev") {
        Fail "packaged staging default URL must be the verified public workers.dev HTTPS base URL"
    } else {
        Pass "packaged staging default URL uses verified workers.dev HTTPS base URL"
    }

    try {
        $defaultUri = [Uri]$defaultUrl
        if ($defaultUri.Scheme -ne "https" -or $defaultUri.UserInfo -or $defaultUri.AbsolutePath -ne "/" -or $defaultUri.Query -or $defaultUri.Fragment) {
            Fail "packaged staging default URL must be HTTPS and base-only"
        } else {
            Pass "packaged staging default URL is HTTPS and base-only"
        }
    } catch {
        Fail "packaged staging default URL is not a valid absolute URI"
    }
}

if ($wpfProject -notmatch "AdminWebEnvironment" -or $wpfProject -notmatch "AssemblyMetadataAttribute") {
    Fail "WPF project must expose Admin Web packaged metadata"
} else {
    Pass "WPF project exposes Admin Web packaged metadata"
}

if ($readme -notmatch "HTTPS staging richiesto" -and $readme -notmatch "workers.dev/staging usare sempre HTTPS") {
    Fail "README must require HTTPS for staging"
} else {
    Pass "README requires HTTPS for staging"
}

if ($options -notmatch "TryLoadPackagedDefault" -or $options -notmatch "TryReadPackagedDefaultBaseUrl") {
    Fail "runtime packaged default resolver missing"
} else {
    Pass "runtime packaged default resolver present"
}

if ($options -notmatch "parsed\.UserInfo" -or $options -notmatch "HTTP e consentito solo per localhost/127\.0\.0\.1") {
    Fail "runtime URL guards for credentials and non-loopback HTTP are missing"
} else {
    Pass "runtime URL guards for credentials and non-loopback HTTP present"
}

if ($helper -match "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB\s*=" -or $helper -match "localhost|127\.0\.0\.1|http://") {
    Fail "staging helper must not enable insecure LAN or localhost target"
} else {
    Pass "staging helper keeps HTTPS public target and no insecure LAN override"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
