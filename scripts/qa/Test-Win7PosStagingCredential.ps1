[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Win7PosQaCredentialVault.psm1') -Force

$state = Test-Win7PosQaCredentialProfile -Profile $Profile
if (-not $state.Exists) {
    Write-Output ('QA_CREDENTIAL_PROFILE_MISSING profile=' + $Profile)
    exit 2
}
if (-not $state.Valid) {
    throw 'QA credential profile is invalid, expired, cannot decrypt for this user, or has unsafe ACLs.'
}

Write-Output (
    'QA_CREDENTIAL_PROFILE_VALID profile=' + $Profile +
    ' version=' + $state.ProfileVersion +
    ' host=' + $state.BaseUrlHost +
    ' acl=PASS' +
    ' migrated=' + $state.Migrated.ToString().ToLowerInvariant() +
    ' shop=present:length-' + $state.ShopCodeLength +
    ' staff=present:length-' + $state.StaffCodeLength +
    ' credential=present:length-' + $state.CredentialLength +
    ' device=present:fingerprint-' + $state.DeviceFingerprint)
