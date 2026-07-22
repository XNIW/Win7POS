param(
    [string]$ReleasePackSource = "",
    [string]$ExpectedCommitSha = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) { $pwsh = (Get-Command powershell -ErrorAction Stop).Source }

$sourceGates = @(
    "check-dialog-standards.ps1",
    "check-architecture-boundaries.ps1",
    "check-release-foundation.ps1",
    "check-release-supply-chain.ps1",
    "check-pos-startup-win7-safe.ps1",
    "check-win7pos-startup-no-eager-db.ps1",
    "check-win7pos-legacy-db-migrations.ps1",
    "check-pos-unified-login-ux.ps1",
    "check-pos-login-logging.ps1",
    "check-pos-debug-logging.ps1",
    "check-bounded-async-logging.ps1",
    "check-pos-first-login-sale-safe-ui.ps1",
    "check-pos-online-bootstrap.ps1",
    "check-pos-online-client.ps1",
    "check-pos-online-linking-task084b.ps1",
    "check-security-hardening.ps1",
    "check-public-staging-config.ps1",
    "check-supplier-excel-wizard.ps1",
    "check-product-dialog-free-text.ps1",
    "check-product-keyset-paging.ps1",
    "check-win7pos-ui-ux-guard.ps1",
    "check-pos-customer-display-safety.ps1",
    "check-pos-shop-data-readonly.ps1",
    "check-pos-sync-status-ux.ps1",
    "check-pos-sales-sync.ps1",
    "check-pos-outbox-shop-binding.ps1",
    "check-pos-catalog-pull.ps1",
    "check-pos-catalog-import-outbox.ps1",
    "check-pos-catalog-import-sync.ps1",
    "check-pos-offline-authorization-lease.ps1",
    "check-pos-reversal-economics.ps1",
    "check-pos-revenue-copy.ps1",
    "check-win7pos-restore-guard.ps1",
    "check-pos-start-of-day-sync.ps1",
    "check-pos-printer-cashdrawer-safety.ps1",
    "check-pos-printer-driver-discovery.ps1",
    "check-pos-receipt-surface-consistency.ps1"
)

$releaseOnlyGates = @(
    "check-release-pack-completeness.ps1",
    "check-win7-runtime-release-validation.ps1",
    "check-pos-online-linking-task084b.ps1"
)

$results = New-Object System.Collections.Generic.List[object]
function Invoke-RequiredGate {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Gate,
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [switch]$UseReleasePack
    )

    $gate = $Gate
    $path = Join-Path $PSScriptRoot $gate
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Host "FAIL missing required gate: $path"
        $results.Add([pscustomobject]@{ Gate = $Label; Result = "FAIL"; Detail = "missing" }) | Out-Null
        return
    }

    $arguments = @("-NoProfile", "-File", $path)
    if ($UseReleasePack) {
        $arguments += @("-ReleasePackSource", $ReleasePackSource)
        if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha) -and
            $gate -in @("check-release-pack-completeness.ps1", "check-win7-runtime-release-validation.ps1")) {
            $arguments += @("-ExpectedCommitSha", $ExpectedCommitSha)
        }
    }

    $output = @(& $pwsh @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $outputLines = @($output | ForEach-Object { $_.ToString() })
    $reportedErrorRecords = @($output | Where-Object {
        $_ -is [System.Management.Automation.ErrorRecord]
    })
    $reportedFailure = @($outputLines | Where-Object {
        $_ -match '^\s*FAIL(?:\s*:|\s|$)' -or
        $_ -match '^\s*(?:===\s*)?RESULT:\s*FAIL(?:\s*===)?\s*$' -or
        $_ -match '^\s*ERROR(?:\s*:|\s|$)'
    })
    if ($exitCode -eq 0 -and
        $reportedFailure.Count -eq 0 -and
        $reportedErrorRecords.Count -eq 0) {
        $results.Add([pscustomobject]@{ Gate = $Label; Result = "PASS"; Detail = "" }) | Out-Null
    }
    else {
        $essential = @($outputLines | Where-Object {
            $_ -match '^\s*(FAIL|ERROR)(?:\s*:|\s|$)' -or
            $_ -match '^\s*(?:===\s*)?RESULT:\s*FAIL(?:\s*===)?\s*$' -or
            $_ -match '^(Exception|.*: line |.*missing|.*must )'
        } | Select-Object -Last 4) -join " | "
        if ([string]::IsNullOrWhiteSpace($essential) -and $reportedErrorRecords.Count -gt 0) {
            $essential = "stderr/ErrorRecord: " + (($reportedErrorRecords | Select-Object -Last 2 | ForEach-Object { $_.ToString() }) -join " | ")
        }
        if ([string]::IsNullOrWhiteSpace($essential)) { $essential = "exit $exitCode" }
        $results.Add([pscustomobject]@{ Gate = $Label; Result = "FAIL"; Detail = $essential }) | Out-Null
    }
}

foreach ($gate in $sourceGates) {
    Invoke-RequiredGate -Gate $gate -Label $gate
}

if (-not [string]::IsNullOrWhiteSpace($ReleasePackSource)) {
    foreach ($gate in $releaseOnlyGates) {
        Invoke-RequiredGate -Gate $gate -Label "$gate [release]" -UseReleasePack
    }
}

Write-Host ""
Write-Host "=== REQUIRED GATES ==="
$results | Format-Table -AutoSize Gate, Result, Detail
$passed = @($results | Where-Object { $_.Result -eq "PASS" }).Count
$total = $results.Count
Write-Host "GATES_SUMMARY=$passed/$total"

if ($results.Result -contains "FAIL") {
    exit 1
}

exit 0
