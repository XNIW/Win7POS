[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Win7PosQaCredentialVault.psm1') -Force
Remove-Win7PosQaCredentialProfile -Profile $Profile
Write-Output ('QA_CREDENTIAL_PROFILE_REMOVED profile=' + $Profile)
