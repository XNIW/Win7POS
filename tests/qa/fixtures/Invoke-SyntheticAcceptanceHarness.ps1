[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AcceptanceOutput,
    [Parameter(Mandatory = $true)][int]$DelayMilliseconds,
    [Parameter(Mandatory = $true)][int]$SyntheticExitCode,
    [Parameter(Mandatory = $true)][string]$MarkerPath,
    [string]$RunConsumedMarkerPath,
    [string]$RunId
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $AcceptanceOutput -Force | Out-Null
Add-Content -LiteralPath $MarkerPath -Value ([string]$PID)
Set-Content `
    -LiteralPath (Join-Path $AcceptanceOutput 'synthetic-process-id.txt') `
    -Value ([string]$PID) `
    -Encoding ASCII
if (-not [string]::IsNullOrWhiteSpace($RunConsumedMarkerPath)) {
    $temporaryMarkerPath = $RunConsumedMarkerPath + '.' +
        [Guid]::NewGuid().ToString('N') + '.tmp'
    [ordered]@{
        logicalRuns = 1
        requestReachedServer = $true
        runId = $RunId
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $temporaryMarkerPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryMarkerPath `
        -Destination $RunConsumedMarkerPath
}
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
