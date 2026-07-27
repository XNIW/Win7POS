[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile,
    [Nullable[DateTimeOffset]]$ExpiresAt
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Win7PosQaCredentialVault.psm1') -Force

$baseUrl = Read-Host 'Staging base URL' -AsSecureString
$shopCode = Read-Host 'Shop code' -AsSecureString
$staffCode = Read-Host 'Staff code' -AsSecureString
$credential = Read-Host 'Staff PIN/password' -AsSecureString
try {
    $result = Set-Win7PosQaCredentialProfile -Profile $Profile -BaseUrl $baseUrl -ShopCode $shopCode -StaffCode $staffCode -Credential $credential -ExpiresAt $ExpiresAt
    if (-not $result.AclPassed) { throw 'QA credential profile ACL verification failed.' }
    Write-Output ('QA_CREDENTIAL_PROFILE_SET profile=' + $Profile + ' acl=PASS')
} finally {
    foreach ($secure in @($baseUrl, $shopCode, $staffCode, $credential)) {
        if ($null -ne $secure) { $secure.Dispose() }
    }
}
