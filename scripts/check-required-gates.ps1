param(
    [string]$ReleasePackSource = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) { $pwsh = (Get-Command powershell -ErrorAction Stop).Source }

$gates = @(
    "check-dialog-standards.ps1",
    "check-architecture-boundaries.ps1",
    "check-pos-startup-win7-safe.ps1",
    "check-pos-unified-login-ux.ps1",
    "check-pos-login-logging.ps1",
    "check-pos-first-login-sale-safe-ui.ps1",
    "check-pos-online-bootstrap.ps1",
    "check-pos-online-client.ps1",
    "check-pos-online-linking-task084b.ps1",
    "check-security-hardening.ps1",
    "check-supplier-excel-wizard.ps1",
    "check-win7pos-ui-ux-guard.ps1",
    "check-pos-shop-data-readonly.ps1",
    "check-release-pack-completeness.ps1",
    "check-win7-runtime-release-validation.ps1"
)

$results = New-Object System.Collections.Generic.List[object]
foreach ($gate in $gates) {
    $path = Join-Path $PSScriptRoot $gate
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $results.Add([pscustomobject]@{ Gate = $gate; Result = "FAIL"; Detail = "missing" }) | Out-Null
        continue
    }

    $arguments = @("-NoProfile", "-File", $path)
    if (-not [string]::IsNullOrWhiteSpace($ReleasePackSource) -and
        $gate -in @(
            "check-pos-online-linking-task084b.ps1",
            "check-release-pack-completeness.ps1",
            "check-win7-runtime-release-validation.ps1")) {
        $arguments += @("-ReleasePackSource", $ReleasePackSource)
    }

    $output = & $pwsh @arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        $results.Add([pscustomobject]@{ Gate = $gate; Result = "PASS"; Detail = "" }) | Out-Null
    }
    else {
        $essential = @($output | Where-Object { $_ -match '^(FAIL|Exception|.*: line |.*missing|.*must )' } | Select-Object -Last 4) -join " | "
        if ([string]::IsNullOrWhiteSpace($essential)) { $essential = "exit $exitCode" }
        $results.Add([pscustomobject]@{ Gate = $gate; Result = "FAIL"; Detail = $essential }) | Out-Null
    }
}

Write-Host ""
Write-Host "=== REQUIRED GATES ==="
$results | Format-Table -AutoSize Gate, Result, Detail

if ($results.Result -contains "FAIL") {
    exit 1
}

exit 0
