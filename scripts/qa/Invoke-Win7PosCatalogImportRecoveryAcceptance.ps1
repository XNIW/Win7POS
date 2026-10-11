[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile,
    [Parameter(Mandatory)][ValidatePattern('^ASUSART_POST_PR68_[0-9]{8}T[0-9]{9}Z_[A-F0-9]{8}$')][string]$ReadinessRunId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+$')][string]$ReleasePackRunId,
    [ValidateRange(15, 60)][int]$TimeoutMinutes = 15
)
$ErrorActionPreference = 'Stop'
# Explicit new U2 entry point; the canonical runner owns every preflight and
# the lease for both suites. No alternate directory, scope or skip-gate option.
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-Win7PosStagingAcceptance.ps1') `
    -Profile $Profile -ReadinessRunId $ReadinessRunId -ReleasePackRunId $ReleasePackRunId `
    -TimeoutMinutes $TimeoutMinutes -CatalogImportRecovery
exit $LASTEXITCODE
