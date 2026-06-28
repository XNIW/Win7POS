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
$helper = Read-Text "scripts/set-admin-web-staging-url.bat"

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

if ($readme -notmatch "HTTPS staging richiesto" -and $readme -notmatch "workers.dev/staging usare sempre HTTPS") {
    Fail "README must require HTTPS for staging"
} else {
    Pass "README requires HTTPS for staging"
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
