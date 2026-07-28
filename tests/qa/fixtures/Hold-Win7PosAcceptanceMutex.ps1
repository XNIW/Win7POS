[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModulePath,
    [Parameter(Mandatory = $true)][string]$MutexName,
    [Parameter(Mandatory = $true)][string]$MarkerPath,
    [Parameter(Mandatory = $true)][int]$HoldMilliseconds
)

$ErrorActionPreference = 'Stop'
Import-Module $ModulePath -Force
$lease = Enter-Win7PosAcceptanceLock -Name $MutexName
if (-not $lease.Acquired) {
    exit 2
}
try {
    Set-Content -LiteralPath $MarkerPath -Value 'acquired' -Encoding ASCII
    Start-Sleep -Milliseconds $HoldMilliseconds
}
finally {
    Exit-Win7PosAcceptanceLock -Lease $lease
}
