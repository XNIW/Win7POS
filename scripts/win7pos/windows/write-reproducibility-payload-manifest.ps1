[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadRoot,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedCommitSha,

    [Parameter(Mandatory = $true)]
    [string]$BuildVersion
)

$ErrorActionPreference = "Stop"

$ManifestFormat = "win7pos-unsigned-payload-manifest-v1"
$NormalizedTimestamp = "<normalized-build-timestamp-utc>"
$RequiredSdkVersion = "10.0.301"

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-ExactlyOneFieldValue {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $matches = [regex]::Matches(
        $Text,
        ('^' + [regex]::Escape($Name) + '=(?<value>[^\r\n]*)(?=\r?$)'),
        [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($matches.Count -ne 1) {
        throw "VERSION.txt must contain exactly one $Name field; found $($matches.Count)."
    }
    return $matches[0].Groups["value"].Value
}

function Read-AndNormalizeVersionMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$CommitSha,
        [Parameter(Mandatory = $true)][string]$ExpectedBuildVersion
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasUtf8Bom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    $offset = if ($hasUtf8Bom) { 3 } else { 0 }
    $strictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        $text = $strictUtf8.GetString($bytes, $offset, $bytes.Length - $offset)
    }
    catch {
        throw "VERSION.txt must be valid UTF-8: $($_.Exception.Message)"
    }

    $productVersion = Get-ExactlyOneFieldValue -Text $text -Name "ProductVersion"
    $buildVersion = Get-ExactlyOneFieldValue -Text $text -Name "BuildVersion"
    $assemblyVersion = Get-ExactlyOneFieldValue -Text $text -Name "AssemblyVersion"
    $fileVersion = Get-ExactlyOneFieldValue -Text $text -Name "FileVersion"
    $informationalVersion = Get-ExactlyOneFieldValue -Text $text -Name "InformationalVersion"
    $commitShaValue = Get-ExactlyOneFieldValue -Text $text -Name "CommitSHA"
    $timestampValue = Get-ExactlyOneFieldValue -Text $text -Name "BuildTimestampUtc"
    $configuration = Get-ExactlyOneFieldValue -Text $text -Name "Configuration"
    $platform = Get-ExactlyOneFieldValue -Text $text -Name "Platform"
    $sdkVersion = Get-ExactlyOneFieldValue -Text $text -Name "SdkVersion"

    if (-not [string]::Equals($commitShaValue, $CommitSha, [StringComparison]::Ordinal)) {
        throw "VERSION.txt does not contain the exact expected CommitSHA '$CommitSha'."
    }
    if (-not [string]::Equals($buildVersion, $ExpectedBuildVersion, [StringComparison]::Ordinal)) {
        throw "VERSION.txt does not contain the authoritative BuildVersion '$ExpectedBuildVersion'."
    }

    $buildMatch = [regex]::Match($ExpectedBuildVersion, '^(?<product>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))(?:-dev\.[0-9a-f]{12})?$')
    $expectedProductVersion = $buildMatch.Groups["product"].Value
    $expectedFourPartVersion = "$expectedProductVersion.0"
    $expectedInformationalVersion = "$ExpectedBuildVersion+sha.$CommitSha"
    if (-not [string]::Equals($productVersion, $expectedProductVersion, [StringComparison]::Ordinal)) {
        throw "VERSION.txt ProductVersion '$productVersion' does not match BuildVersion '$ExpectedBuildVersion'."
    }
    if (-not [string]::Equals($assemblyVersion, $expectedFourPartVersion, [StringComparison]::Ordinal)) {
        throw "VERSION.txt AssemblyVersion '$assemblyVersion' does not match '$expectedFourPartVersion'."
    }
    if (-not [string]::Equals($fileVersion, $expectedFourPartVersion, [StringComparison]::Ordinal)) {
        throw "VERSION.txt FileVersion '$fileVersion' does not match '$expectedFourPartVersion'."
    }
    if (-not [string]::Equals($informationalVersion, $expectedInformationalVersion, [StringComparison]::Ordinal)) {
        throw "VERSION.txt InformationalVersion '$informationalVersion' does not match '$expectedInformationalVersion'."
    }
    if (-not [string]::Equals($configuration, "Release", [StringComparison]::Ordinal) -or
        -not [string]::Equals($platform, "x86", [StringComparison]::Ordinal) -or
        -not [string]::Equals($sdkVersion, $RequiredSdkVersion, [StringComparison]::Ordinal)) {
        throw "VERSION.txt must describe Release/net48/x86 built with SDK $RequiredSdkVersion."
    }

    if ($timestampValue -notmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$') {
        throw "VERSION.txt BuildTimestampUtc '$timestampValue' is not canonical UTC yyyy-MM-ddTHH:mm:ssZ."
    }
    $parsedTimestamp = [DateTime]::MinValue
    $timestampStyles = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [DateTime]::TryParseExact(
            $timestampValue,
            "yyyy-MM-ddTHH:mm:ssZ",
            [Globalization.CultureInfo]::InvariantCulture,
            $timestampStyles,
            [ref]$parsedTimestamp) -or
        -not [string]::Equals($parsedTimestamp.ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture), $timestampValue, [StringComparison]::Ordinal)) {
        throw "VERSION.txt BuildTimestampUtc '$timestampValue' is not a valid canonical UTC timestamp."
    }

    $normalizedText = [regex]::Replace(
        $text,
        '^BuildTimestampUtc=' + [regex]::Escape($timestampValue) + '(?=\r?$)',
        "BuildTimestampUtc=$NormalizedTimestamp",
        [System.Text.RegularExpressions.RegexOptions]::Multiline)
    $normalizedContent = (New-Object System.Text.UTF8Encoding($false)).GetBytes($normalizedText)
    $normalizedBytes = $normalizedContent
    if ($hasUtf8Bom) {
        $withBom = New-Object byte[] ($normalizedContent.Length + 3)
        $withBom[0] = 0xEF
        $withBom[1] = 0xBB
        $withBom[2] = 0xBF
        [System.Buffer]::BlockCopy($normalizedContent, 0, $withBom, 3, $normalizedContent.Length)
        $normalizedBytes = $withBom
    }

    return [pscustomobject][ordered]@{
        normalizedBytes = [byte[]]$normalizedBytes
        productVersion = $productVersion
        buildVersion = $buildVersion
        assemblyVersion = $assemblyVersion
        fileVersion = $fileVersion
        informationalVersion = $informationalVersion
        commitSha = $commitShaValue
        buildTimestampUtc = $timestampValue
        configuration = $configuration
        platform = $platform
        sdkVersion = $sdkVersion
    }
}

function Get-AndValidateBinaryVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][object]$VersionMetadata
    )

    try {
        $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($Path).Version.ToString()
    }
    catch {
        throw "Required payload binary '$RelativePath' is not a readable managed assembly: $($_.Exception.Message)"
    }
    $fileVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $fileVersion = [string]$fileVersionInfo.FileVersion
    $informationalVersion = [string]$fileVersionInfo.ProductVersion

    if (-not [string]::Equals($assemblyVersion, $VersionMetadata.assemblyVersion, [StringComparison]::Ordinal)) {
        throw "Binary AssemblyVersion mismatch for '$RelativePath': '$assemblyVersion' != '$($VersionMetadata.assemblyVersion)'."
    }
    if (-not [string]::Equals($fileVersion, $VersionMetadata.fileVersion, [StringComparison]::Ordinal)) {
        throw "Binary FileVersion mismatch for '$RelativePath': '$fileVersion' != '$($VersionMetadata.fileVersion)'."
    }
    if (-not [string]::Equals($informationalVersion, $VersionMetadata.informationalVersion, [StringComparison]::Ordinal)) {
        throw "Binary InformationalVersion mismatch for '$RelativePath': '$informationalVersion' != '$($VersionMetadata.informationalVersion)'."
    }

    return [pscustomobject][ordered]@{
        path = $RelativePath
        assemblyVersion = $assemblyVersion
        fileVersion = $fileVersion
        informationalVersion = $informationalVersion
    }
}

if ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "ExpectedCommitSha must be a full 40-character commit SHA."
}
$ExpectedCommitSha = $ExpectedCommitSha.ToLowerInvariant()

if ($BuildVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-dev\.[0-9a-f]{12})?$') {
    throw "BuildVersion '$BuildVersion' is not a supported authoritative release or exact-SHA development version."
}

if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) {
    throw "Payload root does not exist: $PayloadRoot"
}
$resolvedPayloadRoot = (Resolve-Path -LiteralPath $PayloadRoot).Path.TrimEnd('\', '/')

$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
$payloadPrefix = $resolvedPayloadRoot + [System.IO.Path]::DirectorySeparatorChar
if ($manifestFullPath.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The normalized manifest must be written outside the payload root."
}

$derivedExcludedPaths = @(
    "APP-FILES.txt",
    "SHA256SUMS.txt"
)
$outerZipFileName = "Win7POS-$BuildVersion-x86.zip"
$outerInstallerFileName = "Win7POS-$BuildVersion-Setup.exe"
$requiredPayloadPaths = @(
    "VERSION.txt",
    "Win7POS.Core.dll",
    "Win7POS.Data.dll",
    "Win7POS.Wpf.exe",
    "Win7POS.Wpf.exe.config"
)

$allFiles = @(Get-ChildItem -LiteralPath $resolvedPayloadRoot -Recurse -File)
if ($allFiles.Count -eq 0) {
    throw "The unsigned payload is empty: $resolvedPayloadRoot"
}

$relativePaths = New-Object System.Collections.Generic.List[string]
$fileByRelativePath = New-Object 'System.Collections.Generic.Dictionary[string,System.IO.FileInfo]' ([StringComparer]::Ordinal)
foreach ($file in $allFiles) {
    $relativePath = $file.FullName.Substring($resolvedPayloadRoot.Length).TrimStart('\', '/').Replace('\', '/')
    if ($relativePath.EndsWith(".pdb", [StringComparison]::OrdinalIgnoreCase)) {
        throw "PDB debug file was not removed before manifest generation: $relativePath"
    }
    if ($file.Name.EndsWith(".zip", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($file.Name, $outerInstallerFileName, [StringComparison]::OrdinalIgnoreCase) -or
        ($file.Name.StartsWith("Win7POS-", [StringComparison]::OrdinalIgnoreCase) -and
            $file.Name.EndsWith("-Setup.exe", [StringComparison]::OrdinalIgnoreCase))) {
        throw "Outer ZIP/installer container must not appear inside PayloadRoot: $relativePath"
    }
    if ($derivedExcludedPaths -contains $relativePath) {
        continue
    }
    if ($fileByRelativePath.ContainsKey($relativePath)) {
        throw "Duplicate payload path after normalization: $relativePath"
    }
    $relativePaths.Add($relativePath) | Out-Null
    $fileByRelativePath.Add($relativePath, $file)
}

$sortedRelativePaths = $relativePaths.ToArray()
[Array]::Sort($sortedRelativePaths, [StringComparer]::Ordinal)

foreach ($requiredPayloadPath in $requiredPayloadPaths) {
    if (-not $fileByRelativePath.ContainsKey($requiredPayloadPath)) {
        throw "Required unsigned payload file is missing: $requiredPayloadPath"
    }
}

$versionMetadata = Read-AndNormalizeVersionMetadata `
    -Path $fileByRelativePath["VERSION.txt"].FullName `
    -CommitSha $ExpectedCommitSha `
    -ExpectedBuildVersion $BuildVersion
$binaryVersions = New-Object System.Collections.Generic.List[object]
foreach ($binaryPath in @("Win7POS.Core.dll", "Win7POS.Data.dll", "Win7POS.Wpf.exe")) {
    $binaryVersions.Add((Get-AndValidateBinaryVersion `
        -Path $fileByRelativePath[$binaryPath].FullName `
        -RelativePath $binaryPath `
        -VersionMetadata $versionMetadata)) | Out-Null
}

$entries = New-Object System.Collections.Generic.List[object]
foreach ($relativePath in $sortedRelativePaths) {
    $file = $fileByRelativePath[$relativePath]
    if ([string]::Equals($relativePath, "VERSION.txt", [StringComparison]::Ordinal)) {
        $bytesToHash = [byte[]]$versionMetadata.normalizedBytes
        $normalization = "BuildTimestampUtc-only"
    }
    else {
        $bytesToHash = [System.IO.File]::ReadAllBytes($file.FullName)
        $normalization = "none"
    }

    $entries.Add([pscustomobject][ordered]@{
        path = $relativePath
        length = $bytesToHash.Length
        sha256 = Get-Sha256Hex -Bytes $bytesToHash
        normalization = $normalization
    }) | Out-Null
}

$manifest = [pscustomobject][ordered]@{
    format = $ManifestFormat
    hashAlgorithm = "SHA-256"
    commitSha = $ExpectedCommitSha
    buildVersion = $BuildVersion
    platform = "x86"
    targetFramework = "net48"
    versionMetadata = [pscustomobject][ordered]@{
        productVersion = $versionMetadata.productVersion
        buildVersion = $versionMetadata.buildVersion
        assemblyVersion = $versionMetadata.assemblyVersion
        fileVersion = $versionMetadata.fileVersion
        informationalVersion = $versionMetadata.informationalVersion
        commitSha = $versionMetadata.commitSha
        configuration = $versionMetadata.configuration
        platform = $versionMetadata.platform
        sdkVersion = $versionMetadata.sdkVersion
    }
    verifiedBinaryVersions = $binaryVersions.ToArray()
    normalization = @(
        [pscustomobject][ordered]@{
            path = "VERSION.txt"
            field = "BuildTimestampUtc"
            replacement = $NormalizedTimestamp
        }
    )
    exclusions = @(
        [pscustomobject][ordered]@{ path = "APP-FILES.txt"; reason = "derived payload inventory" },
        [pscustomobject][ordered]@{ path = "SHA256SUMS.txt"; reason = "derived checksum inventory" },
        [pscustomobject][ordered]@{ path = "Win7POS-$BuildVersion-x86.zip"; reason = "outer ZIP container" },
        [pscustomobject][ordered]@{ path = "Win7POS-$BuildVersion-Setup.exe"; reason = "outer Inno Setup container" }
    )
    fileCount = $entries.Count
    files = $entries.ToArray()
}

$manifestParent = Split-Path -Parent $manifestFullPath
if (-not [string]::IsNullOrWhiteSpace($manifestParent)) {
    New-Item -ItemType Directory -Force -Path $manifestParent | Out-Null
}
$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($manifestFullPath, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Normalized unsigned payload manifest: $manifestFullPath ($($entries.Count) files)"
