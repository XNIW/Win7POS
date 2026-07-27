[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'Win7PosQaCredentialVault.psm1'
$module = Import-Module $modulePath -Force -PassThru
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('win7pos-qa-vault-' + [Guid]::NewGuid().ToString('N'))

try {
    $v1Profile = 'synthetic-v1'
    & $module {
        param($Root, $Profile)
        $path = Get-Win7PosQaCredentialPath -Profile $Profile -Root $Root
        $directory = Split-Path -Parent $path
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
        Set-Win7PosQaSecretsAcl -Path $directory -Directory $true
        $payload = [ordered]@{
            profileVersion = 1
            createdAt = [DateTimeOffset]::UtcNow.ToString('O')
            expiresAt = $null
            baseUrl = 'https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev'
            shopCode = 'QA-SHOP'
            staffCode = 'QA-STAFF'
            credential = '9999'
        }
        $bytes = $null
        $encrypted = $null
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
            $encrypted = [Security.Cryptography.ProtectedData]::Protect(
                $bytes,
                $null,
                [Security.Cryptography.DataProtectionScope]::CurrentUser)
            [IO.File]::WriteAllBytes($path, $encrypted)
            Set-Win7PosQaSecretsAcl -Path $path -Directory $false
        } finally {
            if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
            if ($null -ne $encrypted) { [Array]::Clear($encrypted, 0, $encrypted.Length) }
        }
    } $testRoot $v1Profile

    $migrated = Test-Win7PosQaCredentialProfile -Profile $v1Profile -Root $testRoot
    if (-not $migrated.Valid -or $migrated.ProfileVersion -ne 2 -or -not $migrated.Migrated -or
        -not $migrated.DeviceIdentifierFormatValid -or -not $migrated.DeviceDisplayNameFormatValid) {
        throw ('v1 to v2 migration validation failed: valid=' + $migrated.Valid +
            ' version=' + $migrated.ProfileVersion + ' migrated=' + $migrated.Migrated +
            ' errorType=' + $migrated.ErrorType)
    }
    $secondRead = Test-Win7PosQaCredentialProfile -Profile $v1Profile -Root $testRoot
    if (-not $secondRead.Valid -or $secondRead.Migrated -or
        $secondRead.DeviceFingerprint -ne $migrated.DeviceFingerprint) {
        throw 'Stable QA device identity validation failed.'
    }
    $firstDataDirectory = Join-Path $testRoot 'isolated-data-a'
    $secondDataDirectory = Join-Path $testRoot 'isolated-data-b'
    New-Item -ItemType Directory -Force -Path $firstDataDirectory, $secondDataDirectory | Out-Null
    Remove-Item -LiteralPath $firstDataDirectory -Recurse -Force
    Remove-Item -LiteralPath $secondDataDirectory -Recurse -Force
    $afterCleanDirectories = Test-Win7PosQaCredentialProfile -Profile $v1Profile -Root $testRoot
    if ($afterCleanDirectories.DeviceFingerprint -ne $migrated.DeviceFingerprint) {
        throw 'QA identity changed across clean isolated data directories.'
    }

    $expiredProfile = 'synthetic-expired'
    $baseUrl = ConvertTo-SecureString 'https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev' -AsPlainText -Force
    $shop = ConvertTo-SecureString 'QA-SHOP' -AsPlainText -Force
    $staff = ConvertTo-SecureString 'QA-STAFF' -AsPlainText -Force
    $credential = ConvertTo-SecureString '9999' -AsPlainText -Force
    [void](Set-Win7PosQaCredentialProfile -Profile $expiredProfile -Root $testRoot -BaseUrl $baseUrl -ShopCode $shop -StaffCode $staff -Credential $credential -ExpiresAt ([DateTimeOffset]::UtcNow.AddMinutes(-1)))
    $expired = Test-Win7PosQaCredentialProfile -Profile $expiredProfile -Root $testRoot
    if ($expired.Valid -or -not $expired.Expired) {
        throw 'Expired profile validation failed.'
    }

    $corruptProfile = 'synthetic-corrupt'
    $corruptPath = Get-Win7PosQaCredentialPath -Profile $corruptProfile -Root $testRoot
    [IO.File]::WriteAllBytes($corruptPath, [byte[]](1, 2, 3, 4))
    & $module {
        param($Path)
        Set-Win7PosQaSecretsAcl -Path $Path -Directory $false
    } $corruptPath
    $corrupt = Test-Win7PosQaCredentialProfile -Profile $corruptProfile -Root $testRoot
    if ($corrupt.Valid) {
        throw 'Corrupt profile validation failed.'
    }

    Write-Output 'QA_CREDENTIAL_VAULT_SELF_TEST=PASS migration=v1_to_v2 stable_device=PASS clean_data_dirs=PASS expired=PASS corrupt=PASS'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
    Remove-Module $module -Force -ErrorAction SilentlyContinue
}
