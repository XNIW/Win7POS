[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot '..\..\scripts\qa\Win7PosQaCredentialVault.psm1') -Force

$root = Join-Path $env:TEMP ('win7pos-qa-vault-self-test-' + [Guid]::NewGuid().ToString('N'))
$profile = 'qa-test'
$stagingUrl = 'https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev/'

function New-TestSecret([string]$value) {
    return ConvertTo-SecureString $value -AsPlainText -Force
}

function Set-TestProfile([Nullable[DateTimeOffset]]$ExpiresAt) {
    return Set-Win7PosQaCredentialProfile `
        -Profile $profile `
        -BaseUrl (New-TestSecret $stagingUrl) `
        -ShopCode (New-TestSecret ('q' * 8)) `
        -StaffCode (New-TestSecret ('s' * 8)) `
        -Credential (New-TestSecret ('x' * 12)) `
        -ExpiresAt $ExpiresAt `
        -Root $root
}

try {
    $created = Set-TestProfile -ExpiresAt $null
    $baseline = Test-Win7PosQaCredentialProfile -Profile $profile -Root $root
    if (-not $created.AclPassed -or -not $baseline.Valid -or -not $baseline.AclPassed) {
        throw 'Baseline DPAPI or ACL validation failed.'
    }

    Remove-Win7PosQaCredentialProfile -Profile $profile -Root $root
    $expiredCreated = Set-TestProfile -ExpiresAt ([DateTimeOffset]::UtcNow.AddMinutes(-1))
    $expired = Test-Win7PosQaCredentialProfile -Profile $profile -Root $root
    if (-not $expiredCreated.AclPassed -or -not $expired.Expired -or $expired.Valid) {
        throw 'Expired profile was not rejected.'
    }

    Remove-Win7PosQaCredentialProfile -Profile $profile -Root $root
    [void](Set-TestProfile -ExpiresAt $null)
    $profilePath = Get-Win7PosQaCredentialPath -Profile $profile -Root $root
    [System.IO.File]::WriteAllBytes($profilePath, [byte[]](1, 2, 3, 4))
    $corrupt = Test-Win7PosQaCredentialProfile -Profile $profile -Root $root
    if ($corrupt.Valid) {
        throw 'Corrupt DPAPI profile was accepted.'
    }

    $invalidHostRejected = $false
    try {
        Set-Win7PosQaCredentialProfile `
            -Profile 'qa-invalid-host' `
            -BaseUrl (New-TestSecret 'https://example.invalid/') `
            -ShopCode (New-TestSecret ('q' * 8)) `
            -StaffCode (New-TestSecret ('s' * 8)) `
            -Credential (New-TestSecret ('x' * 12)) `
            -Root $root | Out-Null
    }
    catch {
        $invalidHostRejected = $true
    }
    if (-not $invalidHostRejected) {
        throw 'Non-staging hostname was accepted.'
    }

    Remove-Win7PosQaCredentialProfile -Profile $profile -Root $root
    $removed = Test-Win7PosQaCredentialProfile -Profile $profile -Root $root
    if ($removed.Exists) {
        throw 'Profile removal was not verified.'
    }

    Write-Output 'WIN7POS_QA_CREDENTIAL_VAULT_SELF_TEST=PASS'
}
finally {
    try { Remove-Win7PosQaCredentialProfile -Profile $profile -Root $root } catch { }
    try { Remove-Win7PosQaCredentialProfile -Profile 'qa-invalid-host' -Root $root } catch { }
}
