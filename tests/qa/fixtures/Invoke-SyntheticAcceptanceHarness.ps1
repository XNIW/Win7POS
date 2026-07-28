[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AcceptanceOutput,
    [Parameter(Mandatory = $true)][int]$DelayMilliseconds,
    [Parameter(Mandatory = $true)][int]$SyntheticExitCode,
    [Parameter(Mandatory = $true)][string]$MarkerPath
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $AcceptanceOutput -Force | Out-Null
Add-Content -LiteralPath $MarkerPath -Value ([string]$PID)
Set-Content `
    -LiteralPath (Join-Path $AcceptanceOutput 'synthetic-process-id.txt') `
    -Value ([string]$PID) `
    -Encoding ASCII
Start-Sleep -Milliseconds $DelayMilliseconds

[ordered]@{
    logicalRuns = 1
    passed = $SyntheticExitCode -eq 0
} |
    ConvertTo-Json |
    Set-Content `
        -LiteralPath (
            Join-Path $AcceptanceOutput 'staging-acceptance-result.json'
        ) `
        -Encoding UTF8
exit $SyntheticExitCode
