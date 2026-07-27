Set-StrictMode -Version Latest

$script:QaSecretsRoot = Join-Path $env:ProgramData 'Win7POS\QaSecrets'
$script:AllowedStagingHost = 'merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev'

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
    # icacls changes only the DACL, unlike Set-Acl which can try to preserve a
    # parent SACL and require SeSecurityPrivilege for an ordinary QA user.
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
    $jsonBytes = $null
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
        if ([string]::IsNullOrWhiteSpace($plainValues[1]) -or
            [string]::IsNullOrWhiteSpace($plainValues[2]) -or
            [string]::IsNullOrWhiteSpace($plainValues[3])) {
            throw 'Shop code, staff code, and credential are required.'
        }

        $payload = [ordered]@{
            profileVersion = 1
            createdAt = [DateTimeOffset]::UtcNow.ToString('O')
            expiresAt = if ($null -ne $ExpiresAt) { ([DateTimeOffset]$ExpiresAt).ToUniversalTime().ToString('O') } else { $null }
            baseUrl = $uri.GetLeftPart([System.UriPartial]::Authority)
            shopCode = $plainValues[1].Trim()
            staffCode = $plainValues[2].Trim()
            credential = $plainValues[3]
        }
        $json = $payload | ConvertTo-Json -Compress -Depth 3
        $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        return $jsonBytes
    } finally {
        foreach ($secure in @($BaseUrl, $ShopCode, $StaffCode, $Credential)) {
            if ($null -ne $secure) { $secure.Dispose() }
        }
        foreach ($value in $plainValues) {
            if ($null -ne $value) { $value = $null }
        }
    }
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
        $encryptedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $plainBytes,
            $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        [System.IO.File]::WriteAllBytes($profilePath, $encryptedBytes)
        Set-Win7PosQaSecretsAcl -Path $profilePath -Directory $false
        $acl = Test-Win7PosQaSecretsAcl -Path $profilePath
        if (-not $acl.Passed) { throw 'QA credential profile ACL verification failed.' }
        return [pscustomobject]@{ Profile = $Profile; Path = $profilePath; AclPassed = $true }
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
        return [pscustomobject]@{ Exists = $false; Valid = $false; Expired = $false; AclPassed = $false }
    }
    $encryptedBytes = $null
    $plainBytes = $null
    try {
        $acl = Test-Win7PosQaSecretsAcl -Path $profilePath
        $encryptedBytes = [System.IO.File]::ReadAllBytes($profilePath)
        $plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $encryptedBytes,
            $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $profileData = ([System.Text.Encoding]::UTF8.GetString($plainBytes) | ConvertFrom-Json)
        $uri = [Uri]$profileData.baseUrl
        $expiry = if ([string]::IsNullOrWhiteSpace($profileData.expiresAt)) { $null } else { [DateTimeOffset]::Parse($profileData.expiresAt) }
        $expired = $null -ne $expiry -and $expiry.ToUniversalTime() -le [DateTimeOffset]::UtcNow
        $valid = $acl.Passed -and $profileData.profileVersion -eq 1 -and
            (Test-Win7PosQaStagingBaseUri -Uri $uri) -and
            -not $expired -and
            -not [string]::IsNullOrWhiteSpace($profileData.shopCode) -and
            -not [string]::IsNullOrWhiteSpace($profileData.staffCode) -and
            -not [string]::IsNullOrWhiteSpace($profileData.credential)
        return [pscustomobject]@{ Exists = $true; Valid = $valid; Expired = $expired; AclPassed = $acl.Passed }
    } catch {
        return [pscustomobject]@{ Exists = $true; Valid = $false; Expired = $false; AclPassed = $false }
    } finally {
        if ($null -ne $plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
        if ($null -ne $encryptedBytes) { [Array]::Clear($encryptedBytes, 0, $encryptedBytes.Length) }
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
