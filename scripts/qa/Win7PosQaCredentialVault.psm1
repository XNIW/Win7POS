Set-StrictMode -Version Latest

$script:QaSecretsRoot = Join-Path $env:ProgramData 'Win7POS\QaSecrets'
$script:AllowedStagingHost = 'merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev'
$script:CurrentProfileVersion = 2

function Assert-Win7PosQaProfileName {
    param([Parameter(Mandatory = $true)][string]$Profile)

    if ($Profile -notmatch '^[a-z0-9][a-z0-9-]{0,63}$') {
        throw 'Profile must contain only lowercase letters, digits, and hyphens.'
    }
}

function Get-Win7PosQaCredentialPath {
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [string]$Root = $script:QaSecretsRoot
    )

    Assert-Win7PosQaProfileName -Profile $Profile
    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $path = [System.IO.Path]::GetFullPath((Join-Path $fullRoot ($Profile + '.dpapi')))
    if (-not $path.StartsWith($fullRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Credential profile path escaped the QA secrets root.'
    }

    return $path
}

function Set-Win7PosQaSecretsAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][bool]$Directory
    )

    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentSid) { throw 'Current Windows identity is unavailable.' }
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $rights = if ($Directory) { '(OI)(CI)F' } else { '(F)' }
    $arguments = @(
        $Path,
        '/inheritance:r',
        '/grant:r',
        ('*' + $currentSid.Value + ':' + $rights),
        ('*' + $systemSid.Value + ':' + $rights))
    & icacls.exe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'QA credential profile DACL update failed.'
    }
}

function Test-Win7PosQaSecretsAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $acl = Get-Acl -LiteralPath $Path
    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $systemSid = 'S-1-5-18'
    $fullControl = [long][System.Security.AccessControl.FileSystemRights]::FullControl
    $rules = @($acl.Access)
    $normalizedRules = @($rules | ForEach-Object {
        [pscustomobject]@{
            Sid = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            IsAllow = $_.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow
            HasFullControl = (([long]$_.FileSystemRights -band $fullControl) -eq $fullControl)
        }
    })
    $currentUserFullControl = @($normalizedRules | Where-Object {
        $_.Sid -eq $currentSid -and $_.IsAllow -and $_.HasFullControl
    }).Count -ge 1
    $systemFullControl = @($normalizedRules | Where-Object {
        $_.Sid -eq $systemSid -and $_.IsAllow -and $_.HasFullControl
    }).Count -ge 1
    $unexpected = @($normalizedRules | Where-Object {
        ($_.Sid -ne $currentSid -and $_.Sid -ne $systemSid) -or
        -not $_.IsAllow -or
        -not $_.HasFullControl
    })
    return [pscustomobject]@{
        InheritanceProtected = $acl.AreAccessRulesProtected
        CurrentUserFullControl = $currentUserFullControl
        SystemFullControl = $systemFullControl
        UnexpectedAllowCount = $unexpected.Count
        Passed = $acl.AreAccessRulesProtected -and
            $currentUserFullControl -and
            $systemFullControl -and
            $unexpected.Count -eq 0
    }
}

function Test-Win7PosQaStagingBaseUri {
    param([Parameter(Mandatory = $true)][Uri]$Uri)

    return $Uri.IsAbsoluteUri -and
        $Uri.Scheme -eq 'https' -and
        $Uri.IsDefaultPort -and
        [string]::Equals(
            $Uri.Host,
            $script:AllowedStagingHost,
            [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $Uri.UserInfo -and
        -not $Uri.Query -and
        -not $Uri.Fragment -and
        $Uri.AbsolutePath -eq '/'
}

function Test-Win7PosQaCodeFormat {
    param([string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value.Trim().Length -le 64 -and
        $Value.Trim() -match '^[A-Za-z0-9][A-Za-z0-9._-]*$'
}

function Test-Win7PosQaDeviceIdentifier {
    param([string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value -match '^win7pos:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
}

function Test-Win7PosQaDeviceDisplayName {
    param([string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value.Length -le 32 -and
        $Value -match '^CASSA-[A-Z0-9]+(?:-[A-Z0-9]+)*$'
}

function New-Win7PosQaDeviceDisplayName {
    $machine = [Environment]::MachineName
    if ($null -eq $machine) { $machine = '' }
    $machine = $machine.Trim().ToUpperInvariant()
    $builder = New-Object System.Text.StringBuilder
    $previousDash = $false
    foreach ($character in $machine.ToCharArray()) {
        $allowed = ($character -ge 'A' -and $character -le 'Z') -or
            ($character -ge '0' -and $character -le '9')
        if ($allowed) {
            [void]$builder.Append($character)
            $previousDash = $false
        } elseif (-not $previousDash -and $builder.Length -gt 0) {
            [void]$builder.Append('-')
            $previousDash = $true
        }
    }
    $sanitized = $builder.ToString().Trim('-')
    if ([string]::IsNullOrWhiteSpace($sanitized)) { $sanitized = 'WIN7POS' }
    $displayName = 'CASSA-' + $sanitized
    if ($displayName.Length -gt 32) {
        return $displayName.Substring(0, 32).TrimEnd('-')
    }
    return $displayName
}

function Get-Win7PosQaFingerprint {
    param([string]$Value)

    if ([string]::IsNullOrEmpty($Value)) { return '' }
    $bytes = $null
    $hash = $null
    $sha = $null
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        $sha = [Security.Cryptography.SHA256]::Create()
        $hash = $sha.ComputeHash($bytes)
        return ([BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()).Substring(0, 12)
    } finally {
        if ($null -ne $sha) { $sha.Dispose() }
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        if ($null -ne $hash) { [Array]::Clear($hash, 0, $hash.Length) }
    }
}

function ConvertFrom-Win7PosSecureString {
    param([Parameter(Mandatory = $true)][System.Security.SecureString]$Value)

    $bstr = [IntPtr]::Zero
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    } finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

function ConvertTo-Win7PosQaProfileBytes {
    param(
        [Parameter(Mandatory = $true)][System.Security.SecureString]$BaseUrl,
        [Parameter(Mandatory = $true)][System.Security.SecureString]$ShopCode,
        [Parameter(Mandatory = $true)][System.Security.SecureString]$StaffCode,
        [Parameter(Mandatory = $true)][System.Security.SecureString]$Credential,
        [Nullable[DateTimeOffset]]$ExpiresAt
    )

    $plainValues = @()
    try {
        $plainValues = @(
            (ConvertFrom-Win7PosSecureString -Value $BaseUrl),
            (ConvertFrom-Win7PosSecureString -Value $ShopCode),
            (ConvertFrom-Win7PosSecureString -Value $StaffCode),
            (ConvertFrom-Win7PosSecureString -Value $Credential))
        $uri = $null
        if (-not [Uri]::TryCreate($plainValues[0], [UriKind]::Absolute, [ref]$uri) -or
            -not (Test-Win7PosQaStagingBaseUri -Uri $uri)) {
            throw 'QA credential profile requires the allowlisted HTTPS staging base URL.'
        }
        if (-not (Test-Win7PosQaCodeFormat -Value $plainValues[1]) -or
            -not (Test-Win7PosQaCodeFormat -Value $plainValues[2]) -or
            [string]::IsNullOrWhiteSpace($plainValues[3])) {
            throw 'Shop code, staff code, and credential are required and must use the accepted QA format.'
        }

        $payload = [ordered]@{
            profileVersion = $script:CurrentProfileVersion
            createdAt = [DateTimeOffset]::UtcNow.ToString('O')
            expiresAt = if ($null -ne $ExpiresAt) { ([DateTimeOffset]$ExpiresAt).ToUniversalTime().ToString('O') } else { $null }
            baseUrl = $uri.GetLeftPart([System.UriPartial]::Authority)
            shopCode = $plainValues[1].Trim()
            staffCode = $plainValues[2].Trim()
            credential = $plainValues[3]
            deviceIdentifier = 'win7pos:' + [Guid]::NewGuid().ToString('D')
            deviceDisplayName = New-Win7PosQaDeviceDisplayName
        }
        return [System.Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress -Depth 3))
    } finally {
        foreach ($secure in @($BaseUrl, $ShopCode, $StaffCode, $Credential)) {
            if ($null -ne $secure) { $secure.Dispose() }
        }
        foreach ($value in $plainValues) { $value = $null }
    }
}

function Write-Win7PosQaProfileData {
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [Parameter(Mandatory = $true)]$ProfileData,
        [string]$Root = $script:QaSecretsRoot
    )

    $profilePath = Get-Win7PosQaCredentialPath -Profile $Profile -Root $Root
    $rootPath = Split-Path -Parent $profilePath
    New-Item -ItemType Directory -Force -Path $rootPath | Out-Null
    Set-Win7PosQaSecretsAcl -Path $rootPath -Directory $true
    $plainBytes = $null
    $encryptedBytes = $null
    try {
        $plainBytes = [Text.Encoding]::UTF8.GetBytes(($ProfileData | ConvertTo-Json -Compress -Depth 3))
        $encryptedBytes = [Security.Cryptography.ProtectedData]::Protect(
            $plainBytes,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        Write-Win7PosQaEncryptedProfileAtomically -ProfilePath $profilePath -EncryptedBytes $encryptedBytes
        if (-not (Test-Win7PosQaSecretsAcl -Path $profilePath).Passed) {
            throw 'QA credential profile ACL verification failed.'
        }
    } finally {
        if ($null -ne $plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
        if ($null -ne $encryptedBytes) { [Array]::Clear($encryptedBytes, 0, $encryptedBytes.Length) }
    }
}

function Write-Win7PosQaEncryptedProfileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$ProfilePath,
        [Parameter(Mandatory = $true)][byte[]]$EncryptedBytes
    )

    $directory = Split-Path -Parent $ProfilePath
    $temporaryPath = Join-Path $directory ('.' + [Guid]::NewGuid().ToString('N') + '.dpapi.tmp')
    $backupPath = $temporaryPath + '.bak'
    $committed = $false
    try {
        [IO.File]::WriteAllBytes($temporaryPath, $EncryptedBytes)
        Set-Win7PosQaSecretsAcl -Path $temporaryPath -Directory $false
        if (Test-Path -LiteralPath $ProfilePath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $ProfilePath, $backupPath)
        } else {
            [IO.File]::Move($temporaryPath, $ProfilePath)
        }
        $committed = $true
        Set-Win7PosQaSecretsAcl -Path $ProfilePath -Directory $false
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    } finally {
        if (-not $committed -and (Test-Path -LiteralPath $temporaryPath -PathType Leaf)) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if (-not $committed -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    }
}

function Read-Win7PosQaProfileData {
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [string]$Root = $script:QaSecretsRoot
    )

    $profilePath = Get-Win7PosQaCredentialPath -Profile $Profile -Root $Root
    $encryptedBytes = $null
    $plainBytes = $null
    try {
        $encryptedBytes = [IO.File]::ReadAllBytes($profilePath)
        $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
            $encryptedBytes,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        return ([Text.Encoding]::UTF8.GetString($plainBytes) | ConvertFrom-Json)
    } finally {
        if ($null -ne $plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
        if ($null -ne $encryptedBytes) { [Array]::Clear($encryptedBytes, 0, $encryptedBytes.Length) }
    }
}

function Get-Win7PosQaProfileValidation {
    param(
        [Parameter(Mandatory = $true)]$ProfileData,
        [Parameter(Mandatory = $true)]$Acl,
        [bool]$Migrated = $false
    )

    $uri = $null
    $baseUrlValid = [Uri]::TryCreate([string]$ProfileData.baseUrl, [UriKind]::Absolute, [ref]$uri) -and
        (Test-Win7PosQaStagingBaseUri -Uri $uri)
    $hasExpiry = -not [string]::IsNullOrWhiteSpace([string]$ProfileData.expiresAt)
    $expiry = [DateTimeOffset]::MinValue
    $expiryValid = -not $hasExpiry -or
        [DateTimeOffset]::TryParse([string]$ProfileData.expiresAt, [ref]$expiry)
    $expired = $hasExpiry -and $expiryValid -and $expiry.ToUniversalTime() -le [DateTimeOffset]::UtcNow
    $shopCode = [string]$ProfileData.shopCode
    $staffCode = [string]$ProfileData.staffCode
    $credential = [string]$ProfileData.credential
    $deviceIdentifier = [string]$ProfileData.deviceIdentifier
    $deviceDisplayName = [string]$ProfileData.deviceDisplayName
    $version = 0
    [void][int]::TryParse([string]$ProfileData.profileVersion, [ref]$version)
    $shopCodeFormatValid = Test-Win7PosQaCodeFormat -Value $shopCode
    $staffCodeFormatValid = Test-Win7PosQaCodeFormat -Value $staffCode
    $credentialPresent = -not [string]::IsNullOrWhiteSpace($credential)
    $deviceIdentifierFormatValid = Test-Win7PosQaDeviceIdentifier -Value $deviceIdentifier
    $deviceDisplayNameFormatValid = Test-Win7PosQaDeviceDisplayName -Value $deviceDisplayName
    return [pscustomobject]@{
        Exists = $true
        Valid = $Acl.Passed -and $version -eq $script:CurrentProfileVersion -and $baseUrlValid -and $expiryValid -and -not $expired -and
            $shopCodeFormatValid -and $staffCodeFormatValid -and $credentialPresent -and
            $deviceIdentifierFormatValid -and $deviceDisplayNameFormatValid
        Expired = $expired
        AclPassed = $Acl.Passed
        ProfileVersion = $version
        BaseUrlHost = if ($baseUrlValid) { $uri.Host } else { '' }
        ShopCodePresent = -not [string]::IsNullOrWhiteSpace($shopCode)
        ShopCodeLength = $shopCode.Length
        ShopCodeFormatValid = $shopCodeFormatValid
        StaffCodePresent = -not [string]::IsNullOrWhiteSpace($staffCode)
        StaffCodeLength = $staffCode.Length
        StaffCodeFormatValid = $staffCodeFormatValid
        CredentialPresent = $credentialPresent
        CredentialLength = $credential.Length
        DeviceIdentifierPresent = -not [string]::IsNullOrWhiteSpace($deviceIdentifier)
        DeviceIdentifierFormatValid = $deviceIdentifierFormatValid
        DeviceFingerprint = Get-Win7PosQaFingerprint -Value $deviceIdentifier
        DeviceDisplayNamePresent = -not [string]::IsNullOrWhiteSpace($deviceDisplayName)
        DeviceDisplayNameFormatValid = $deviceDisplayNameFormatValid
        Migrated = $Migrated
    }
}

function Migrate-Win7PosQaCredentialProfile {
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [string]$Root = $script:QaSecretsRoot
    )

    $profileData = Read-Win7PosQaProfileData -Profile $Profile -Root $Root
    $version = 0
    [void][int]::TryParse([string]$profileData.profileVersion, [ref]$version)
    if ($version -eq $script:CurrentProfileVersion) { return $false }
    if ($version -ne 1) { throw 'QA credential profile version is unsupported.' }

    $uri = $null
    if (-not [Uri]::TryCreate([string]$profileData.baseUrl, [UriKind]::Absolute, [ref]$uri) -or
        -not (Test-Win7PosQaStagingBaseUri -Uri $uri) -or
        -not (Test-Win7PosQaCodeFormat -Value ([string]$profileData.shopCode)) -or
        -not (Test-Win7PosQaCodeFormat -Value ([string]$profileData.staffCode)) -or
        [string]::IsNullOrWhiteSpace([string]$profileData.credential)) {
        throw 'QA credential profile cannot be migrated because its existing fields are invalid.'
    }

    $profileData.profileVersion = $script:CurrentProfileVersion
    $profileData | Add-Member -NotePropertyName deviceIdentifier -NotePropertyValue ('win7pos:' + [Guid]::NewGuid().ToString('D')) -Force
    $profileData | Add-Member -NotePropertyName deviceDisplayName -NotePropertyValue (New-Win7PosQaDeviceDisplayName) -Force
    Write-Win7PosQaProfileData -Profile $Profile -ProfileData $profileData -Root $Root
    return $true
}

function Set-Win7PosQaCredentialProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [Parameter(Mandatory = $true)][System.Security.SecureString]$BaseUrl,
        [Parameter(Mandatory = $true)][System.Security.SecureString]$ShopCode,
        [Parameter(Mandatory = $true)][System.Security.SecureString]$StaffCode,
        [Parameter(Mandatory = $true)][System.Security.SecureString]$Credential,
        [Nullable[DateTimeOffset]]$ExpiresAt,
        [string]$Root = $script:QaSecretsRoot
    )

    $profilePath = Get-Win7PosQaCredentialPath -Profile $Profile -Root $Root
    $rootPath = Split-Path -Parent $profilePath
    New-Item -ItemType Directory -Force -Path $rootPath | Out-Null
    Set-Win7PosQaSecretsAcl -Path $rootPath -Directory $true
    $plainBytes = $null
    $encryptedBytes = $null
    try {
        $plainBytes = ConvertTo-Win7PosQaProfileBytes -BaseUrl $BaseUrl -ShopCode $ShopCode -StaffCode $StaffCode -Credential $Credential -ExpiresAt $ExpiresAt
        $encryptedBytes = [Security.Cryptography.ProtectedData]::Protect(
            $plainBytes,
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        Write-Win7PosQaEncryptedProfileAtomically -ProfilePath $profilePath -EncryptedBytes $encryptedBytes
        $acl = Test-Win7PosQaSecretsAcl -Path $profilePath
        if (-not $acl.Passed) { throw 'QA credential profile ACL verification failed.' }
        return [pscustomobject]@{ Profile = $Profile; Path = $profilePath; AclPassed = $true; ProfileVersion = $script:CurrentProfileVersion }
    } finally {
        if ($null -ne $plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
        if ($null -ne $encryptedBytes) { [Array]::Clear($encryptedBytes, 0, $encryptedBytes.Length) }
    }
}

function Test-Win7PosQaCredentialProfile {
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [string]$Root = $script:QaSecretsRoot
    )

    $profilePath = Get-Win7PosQaCredentialPath -Profile $Profile -Root $Root
    if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
        return [pscustomobject]@{ Exists = $false; Valid = $false; Expired = $false; AclPassed = $false; ProfileVersion = 0; Migrated = $false }
    }
    try {
        $acl = Test-Win7PosQaSecretsAcl -Path $profilePath
        $profileData = Read-Win7PosQaProfileData -Profile $Profile -Root $Root
        $version = 0
        [void][int]::TryParse([string]$profileData.profileVersion, [ref]$version)
        $migrated = $false
        if ($version -eq 1) {
            $migrated = Migrate-Win7PosQaCredentialProfile -Profile $Profile -Root $Root
            $profileData = Read-Win7PosQaProfileData -Profile $Profile -Root $Root
            $acl = Test-Win7PosQaSecretsAcl -Path $profilePath
        }
        return Get-Win7PosQaProfileValidation -ProfileData $profileData -Acl $acl -Migrated $migrated
    } catch {
        return [pscustomobject]@{
            Exists = $true
            Valid = $false
            Expired = $false
            AclPassed = $false
            ProfileVersion = 0
            Migrated = $false
            ErrorType = $_.Exception.GetType().Name
        }
    }
}

function Remove-Win7PosQaCredentialProfile {
    param(
        [Parameter(Mandatory = $true)][string]$Profile,
        [string]$Root = $script:QaSecretsRoot
    )

    $profilePath = Get-Win7PosQaCredentialPath -Profile $Profile -Root $Root
    if (Test-Path -LiteralPath $profilePath -PathType Leaf) {
        Remove-Item -LiteralPath $profilePath -Force
    }
    if (Test-Path -LiteralPath $profilePath) { throw 'QA credential profile removal could not be verified.' }
}

Export-ModuleMember -Function @(
    'Get-Win7PosQaCredentialPath',
    'Set-Win7PosQaCredentialProfile',
    'Test-Win7PosQaCredentialProfile',
    'Remove-Win7PosQaCredentialProfile')
