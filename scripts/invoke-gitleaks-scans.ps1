[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ToolDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$ToolsConfigPath = (Join-Path $PSScriptRoot "..\eng\supply-chain\tools.json"),
    [string]$GitleaksConfigPath = (Join-Path $PSScriptRoot "..\eng\supply-chain\gitleaks.toml"),
    [string]$IgnorePath = (Join-Path $PSScriptRoot "..\.gitleaksignore")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

$isShallow = (& git -C $repoRoot rev-parse --is-shallow-repository).Trim()
if ($LASTEXITCODE -ne 0 -or $isShallow -notin @("true", "false")) {
    throw "Cannot verify Git repository history depth."
}
if ($isShallow -ceq "true") {
    throw "Full-history secret scanning requires a non-shallow checkout (fetch-depth: 0)."
}

function Read-JsonFile([string]$path) {
    try { return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $path).Path) | ConvertFrom-Json }
    catch { throw "Invalid or missing JSON '$path': $($_.Exception.Message)" }
}

function Sanitize-Report([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Gitleaks did not produce the required machine-readable report."
    }
    $raw = [System.IO.File]::ReadAllText($path).Trim()
    if (-not $raw.StartsWith('[', [StringComparison]::Ordinal) -or
        -not $raw.EndsWith(']', [StringComparison]::Ordinal)) {
        throw "Gitleaks report must be a JSON array."
    }
    $findings = @($raw | ConvertFrom-Json)
    foreach ($finding in $findings) {
        $finding.Secret = "[redacted]"
        $finding.Match = "[redacted]"
    }
    [System.IO.File]::WriteAllText($path, ($findings | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    return $findings.Count
}

function Invoke-Scan([string]$kind, [string]$reportPath) {
    if (Test-Path -LiteralPath $reportPath) {
        Remove-Item -LiteralPath $reportPath -Force
    }
    $arguments = @(
        $kind, $repoRoot,
        "--config", (Resolve-Path -LiteralPath $GitleaksConfigPath).Path,
        "--gitleaks-ignore-path", (Resolve-Path -LiteralPath $IgnorePath).Path,
        "--report-format", "json",
        "--report-path", $reportPath,
        "--redact=100", "--no-banner", "--no-color", "--exit-code", "1"
    )
    & $script:gitleaksExe @arguments
    $exitCode = $LASTEXITCODE
    $count = Sanitize-Report $reportPath
    if ($exitCode -ne 0) {
        if ($exitCode -eq 1 -and $count -gt 0) {
            throw "Gitleaks $kind scan found $count potential secret(s). Sanitized report: $reportPath"
        }
        throw "Gitleaks $kind scan failed with exit code $exitCode. Report: $reportPath"
    }
    if ($count -ne 0) { throw "Gitleaks $kind returned success with non-empty report." }
}

$configText = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $GitleaksConfigPath).Path)
if ($configText -notmatch '(?ms)^\[extend\]\s*useDefault\s*=\s*true\s*$' -or
    $configText -match '(?i)allowlist|paths\s*=|regexes\s*=') {
    throw "Gitleaks config must extend all default rules without broad allowlists or path exclusions."
}
$ignoreLines = @(Get-Content -LiteralPath (Resolve-Path -LiteralPath $IgnorePath).Path |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -notmatch '^\s*#' })
foreach ($line in $ignoreLines) {
    if ($line -cnotmatch '^[0-9a-f]{40}:[^:*?]+:[a-z0-9-]+:[0-9]+$') {
        throw "Gitleaks ignore entry is not one exact historical fingerprint: '$line'."
    }
}

$config = Read-JsonFile $ToolsConfigPath
$toolRoot = [System.IO.Path]::GetFullPath($ToolDirectory)
$marker = Read-JsonFile (Join-Path $toolRoot "supply-chain-toolchain.json")
$configHash = (Get-FileHash -LiteralPath (Resolve-Path -LiteralPath $ToolsConfigPath).Path -Algorithm SHA256).Hash.ToLowerInvariant()
if ([string]$marker.configSha256 -cne $configHash -or [string]$marker.gitleaks.version -cne "8.30.1" -or
    [string]$marker.gitleaks.archiveSha256 -cne [string]$config.gitleaks.sha256) {
    throw "Pinned Gitleaks marker verification failed."
}
$script:gitleaksExe = Join-Path $toolRoot ([string]$marker.gitleaks.executable)
if (-not (Test-Path -LiteralPath $script:gitleaksExe -PathType Leaf)) { throw "Pinned Gitleaks executable is missing." }
$actualVersion = (& $script:gitleaksExe version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualVersion -cne "8.30.1") { throw "Pinned Gitleaks executable version mismatch." }

$out = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null
Invoke-Scan "dir" (Join-Path $out "gitleaks-working-tree.json")
Invoke-Scan "git" (Join-Path $out "gitleaks-full-history.json")
Write-Host "PASS: Gitleaks 8.30.1 working-tree and full-history scans found 0 secrets"
Write-Host "Sanitized reports: $out"
